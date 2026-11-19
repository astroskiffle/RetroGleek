// RetroGleek - the compare view: side-by-side compare, before / after and the retro preview
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ====================================================================== the compare view

// Two viewers side by side, filling the main window while it is shown. Three ways to use it:
//   - side-by-side compare (Ctrl+Shift+C, Find duplicates): two pictures, zoom and panning linked;
//   - before / after (Shift+O on a retro picture): one picture twice, as decoded on the left and with the screen
//     effects on the right;
//   - the retro preview (Shift+O on a photo or other modern picture): the picture on the left, and on the right as it
//     might look on an 8-bit computer, with a bar of choices above it and Save.
// The main window starts it (ShowPair, ShowBeforeAfter), hands it the keys (HandleKey), the effect presets
// (UsePreset) and the retro preview's choices (SetSim), and ends it (Close). The view asks the main window, through
// the callbacks below, to close it, to go fullscreen, to show a title or a warning, and to rescan a folder it saved
// into. It never changes the browser or its files otherwise: saving writes new files only, where you choose.
sealed class CompareView : TableLayoutPanel
{
    // ---- set by the main window
    public Action CloseRequested;                         // Esc, Ctrl+Shift+C, Shift+O: end the view
    public Action FullscreenRequested;                    // Enter, or Esc in fullscreen
    public Func<bool> IsFullscreen;
    public Action<string> TitleChanged;
    public Action<string> Warn;
    public Action<string> FolderWritten;                  // a file was saved into this folder
    readonly Func<string, string> KeysOf;                 // a command's keys, as the menus show them (MainForm.Commands.cs)

    public readonly Canvas LeftView, RightView;

    readonly Canvas main;                                  // the main viewer: its effects are used here, and kept from before / after
    readonly ToolTip tip = new ToolTip();

    bool active;                                           // shown: comparing, before / after or the retro preview

    public CompareView(Canvas mainViewer, Func<string, string> keysOf)
    {
        main = mainViewer;
        KeysOf = keysOf;
        Dock = DockStyle.Fill; ColumnCount = 2; RowCount = 1; Visible = false; Padding = new Padding(0); Margin = new Padding(0);
        BackColor = Color.FromArgb(52, 52, 52);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        LeftView = new Canvas { Margin = new Padding(0, 0, Util.S(1), 0) };
        RightView = new Canvas { Margin = new Padding(0) };
        // the right half: the retro preview's bar (hidden otherwise) above the right picture
        rightHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(Util.S(1), 0, 0, 0), Padding = new Padding(0), BackColor = Color.FromArgb(24, 24, 24) };
        rightHost.Controls.Add(RightView);                 // fill first, edge-docked after
        BuildSimBar();
        rightHost.Controls.Add(simBar);
        Controls.Add(LeftView, 0, 0);
        Controls.Add(rightHost, 1, 0);
        // each follows the other's zoom and position
        // (before / after: the right side leads, and the left one always takes its exact scale, see BaMatch)
        LeftView.ViewChanged += delegate
        {
            if (beforeAfter && baSim && simOpts.Fill) return;     // the crop frame: the left side stays fitted, on its own
            double z, x, y; int fm; LeftView.GetView(out z, out x, out y, out fm);
            double sc = beforeAfter && baSim ? RightView.SimScale : 1;      // retro preview: the two pictures differ in size
            if (fm != 0 || sc > 0) RightView.SetView(fm == 0 ? z / sc : z, x, y, fm);
            BaMatch();
        };
        RightView.ViewChanged += delegate
        {
            if (beforeAfter) { BaMatch(); return; }
            double z, x, y; int fm; RightView.GetView(out z, out x, out y, out fm); LeftView.SetView(z, x, y, fm);
        };
        // before / after: whichever side moves to another picture (keys, wheel), the other follows
        LeftView.ImageChanged += delegate (string p, int i) { BaFollow(RightView, p); BaMatch(); };
        RightView.ImageChanged += delegate (string p, int i) { BaFollow(LeftView, p); BaMatch(); };
        RightView.Resize += delegate { if (beforeAfter) BeginInvoke(new MethodInvoker(BaMatch)); };
        // a click on a viewer's signal / scanline badge: compare keeps both sides alike, before / after matches sizes again
        LeftView.EffectsClicked += delegate (int which) { if (active && !beforeAfter) SameBadge(LeftView, RightView, which); };
        RightView.EffectsClicked += delegate (int which)
        {
            if (beforeAfter) { simFxTouched = true; BaMatch(); }
            else if (active) SameBadge(RightView, LeftView, which);
        };
        // retro preview of an animated GIF or WebP: the left side shows the frame the right side shows
        RightView.FrameShown += delegate (int f) { if (beforeAfter && baSim) LeftView.ShowFrameOf(f); };   // after the right side has fitted itself again
        // retro preview, Fill the screen: dragging the frame on the left moves the crop; the right side is converted
        // again when you let go
        LeftView.CropDragged += delegate (RectangleF r) { SimCropFrom(r); };
        LeftView.CropDropped += delegate (RectangleF r)
        {
            SimCropFrom(r);
            if (!beforeAfter || !baSim || !simOpts.Fill) return;
            RightView.Simulation = simOpts;
            BaTitle();
        };
    }

    // ---------------------------------------------------------------- what the main window asks

    public bool Active { get { return active; } }
    public bool BeforeAfter { get { return beforeAfter; } }
    public bool RetroPreview { get { return beforeAfter && baSim; } }
    public RetroSim Options { get { return simOpts; } }     // the retro preview's choices (remembered in settings.ini)
    public bool Saving { get { return simSaving; } }
    public int SimFrameCount { get { return RightView.SimFrameCount; } }
    public string ShownPath { get { return baPath; } }      // before / after: the picture shown now
    public bool FromViewer { get { return baFromViewer; } } // before / after: started in the viewer (it goes back there)

    // the viewer(s) that show the screen effects: both halves while comparing, the right one in before / after
    public IEnumerable<Canvas> EffectTargets { get { return beforeAfter ? new[] { RightView } : new[] { LeftView, RightView }; } }

    // where a new preset takes its effects from
    public Canvas PresetSource { get { return beforeAfter ? RightView : LeftView; } }

    // a screen effects preset: both sides while comparing, the right side in before / after
    public void UsePreset(EffectSet p)
    {
        if (beforeAfter) { simFxTouched = true; RightView.UseEffects(p); BaMatch(); }
        else { LeftView.UseEffects(p); RightView.UseEffects(p); }
    }

    // side-by-side compare: two pictures, with the main viewer's effects (the view is already shown)
    public void ShowPair(string a, string b)
    {
        active = true;
        LeftView.Visible = true; RightView.Visible = true;
        foreach (Canvas c in new[] { LeftView, RightView })
        {
            c.UseEffects(main.ManualEffects);
            c.AutoEffects = main.AutoEffects;              // automatic: each picture gets its own computer's effects
            c.EffectsOff = main.EffectsOff;
        }
        LeftView.ShowSingle(a);
        RightView.ShowSingle(b);
        LeftView.Focus();
    }

    // Before / after (simulate = false) or the retro preview (true) of list[at]; moving to another picture moves
    // through the list. fromViewer: started in the viewer, which it goes back to. The view is already shown.
    public void ShowBeforeAfter(List<string> list, int at, bool simulate, bool fromViewer)
    {
        baFromViewer = fromViewer;
        baList = list;
        baPath = list[at];
        active = true;
        beforeAfter = true;
        baSim = simulate;
        LeftView.Visible = true; RightView.Visible = true;
        simBar.Visible = simulate;
        PerformLayout();
        LeftView.UseEffects(main.ManualEffects);
        LeftView.AutoEffects = false;
        LeftView.EffectsOff = true;                        // before: the picture as decoded
        RightView.UseEffects(main.ManualEffects);
        EffectSet own = main.ManualEffects;
        // the retro preview is about the screen too: with no effects of your own, the automatic ones are used (for the
        // preview only, unless you change them there)
        simAutoForced = simulate && !main.AutoEffects && !own.Tv && own.Scan == 0 && own.Mask == 0 && own.Signal == 0 && own.Glow == 0 && own.Curve == 0;
        simFxTouched = false;
        RightView.AutoEffects = main.AutoEffects || simAutoForced;   // after: your effects, or the automatic ones
        RightView.EffectsOff = false;
        foreach (Canvas c in new[] { LeftView, RightView }) { c.FitLock = true; c.PixMode = main.PixMode; c.Checker = main.Checker; c.InfoOn = main.InfoOn; }
        if (simulate) { SyncSimBar(); RightView.Simulation = simOpts; }
        LeftView.FollowFrames = simulate;                  // an animation: the left side shows the right side's frame
        LeftView.ShowBadges = false;                       // the plain side: its effects stay off
        LeftView.Open(baList, at);
        RightView.Open(baList, at);
        if (simulate)
        {
            LeftView.ShowMessage(simOpts.Fill ? "Before: the picture as it is. Drag the frame to choose the part the screen shows" : "Before: the picture as it is");
            RightView.ShowMessage("After: " + SimText() + "   (" + KeysOf("sim-computer") + " computer, " + KeysOf("sim-dither") + " dither, " + KeysOf("sim-fill") + " framing; " + KeysOf("retro-preview") + " or Esc closes)");
        }
        else
        {
            LeftView.ShowMessage("Before: as decoded, no screen effects");
            RightView.ShowMessage("After: " + (RightView.AutoEffects ? "automatic screen effects" : "with your screen effects") + "   (" + KeysOf("before-after") + " or Esc closes)");
        }
        BaTitle();
        RightView.Focus();
    }

    // Ends whatever is shown and hides the view. Before / after: what you set on the right side becomes the main
    // viewer's own settings (KeepsEffects). The main window shows the browser or the viewer again.
    public void Close()
    {
        if (!active) return;
        bool wasBa = beforeAfter;
        if (wasBa && BaKeepsEffects) BaKeepEffects();
        active = false;
        beforeAfter = false;
        Visible = false;
        LeftView.Release(); RightView.Release();
        if (!wasBa) return;
        simBar.Visible = false;
        RightView.Simulation = null;
        LeftView.FollowFrames = false;
        LeftView.ShowBadges = true;
        LeftView.CropFrame = RectangleF.Empty;
        baSim = false;
        LeftView.FitLock = false; RightView.FitLock = false;   // side-by-side compare keeps its own behaviour
        LeftView.EffectsOff = false;
        baList = null; baPath = null;
    }

    // RetroGleek closes while before / after is shown: keep what you set there, as closing the view would
    public void KeepEffectsNow() { if (beforeAfter && BaKeepsEffects) BaKeepEffects(); }

    // every key while the view is shown (after the keys that work everywhere: F1, F11, presets)
    public bool HandleKey(Keys keyData) { return CompareKey(keyData); }

    // the retro preview's choices (bar, keys or the Effects menu)
    public void SetSim(int machine, int dither, bool fill) { SimSet(machine, dither, fill); }
    public void SaveSim() { SimSave(); }
    public void SaveSimAnim() { SimSaveAnim(); }

    // ---------------------------------------------------------------- the rest, as it was in the main window

    // One retro picture twice, side by side: on the left as decoded (no screen effects at all), on the right with
    // the screen effects you have chosen (or the automatic ones). Built on the compare view: zoom and panning are
    // linked, both sides fit the window, and moving to another picture moves both. Effect keys and presets change
    // the right side only, and what you set there is kept when you close it. Display only, like every effect.
    //
    // Retro preview: the same view for a photo or any other modern picture. The left side shows the picture as it is,
    // the right side the picture converted in memory for an 8-bit computer (ZX Spectrum, C64, Amstrad CPC, MSX), with
    // the dithering and framing you choose, shown with the screen effects. Nothing is written: Convert (Ctrl+Shift+E)
    // makes the file. The left side follows the right side's zoom, scaled so the picture is the same size on both.
    bool beforeAfter, baFromViewer;
    List<string> baList;
    string baPath;
    bool baSim;                                            // the retro preview (a modern picture), not before / after
    readonly RetroSim simOpts = new RetroSim { Dither = 1 };
    bool simAutoForced, simFxTouched;                      // automatic effects switched on for the preview only

    // the retro preview's bar, above the right picture, in three columns, each control with a short grey note under it:
    //   Computer [box]           [Framing box]          [Save...]
    //            screen, colours  Shift+F changes this   Ctrl+S saves
    //   Dither   [box]           [Centre the crop]  (only with Fill the screen)
    //            Q, D change these
    // (the choices are also on the Effects menu)
    readonly Panel rightHost;
    Panel simBar;
    DarkCombo simMachine, simDither, simFrame;
    Label simDetail;                                       // the chosen computer's screen and colours
    Button simCentre;
    bool simSyncing;

    // ---- the retro preview's Save (button, Ctrl+S, Effects menu): two new files from the picture shown.
    //   1. The computer's own file (.scr, .koa, .dd, .sc2), converted exactly as previewed: computer, dithering,
    //      framing and crop. It holds the picture only, as the machine stores it: no screen effects. Checked by RECOIL.
    //   2. Beside it, a PNG of the screen as the right side shows it, with its screen effects (TV aspect, signal,
    //      scanlines, mask, glow, curvature), each original line 4 pixels high: a picture to share. Verified lossless.
    // The Save dialog names the first file (and asks before replacing one); the PNG gets a free name, never replacing
    // anything. The original picture is never changed.
    bool simSaving;

    // side-by-side compare: what a badge click changed on one side, on the other side too
    static void SameBadge(Canvas from, Canvas to, int which)
    {
        switch (which)
        {
            case Canvas.BadgeSignal: to.SignalMode = from.SignalMode; break;
            case Canvas.BadgeScan: to.ScanMode = from.ScanMode; break;
            case Canvas.BadgeMask: to.MaskMode = from.MaskMode; break;
            case Canvas.BadgeGlow: to.GlowMode = from.GlowMode; break;
            case Canvas.BadgeCurve: to.CurveMode = from.CurveMode; break;
            case Canvas.BadgeTv: to.TvAspect = from.TvAspect; break;
            case Canvas.BadgePixels: to.PixMode = from.PixMode; break;
            case Canvas.BadgeAuto: to.AutoEffects = from.AutoEffects; break;
            case Canvas.BadgeOff: to.EffectsOff = from.EffectsOff; break;
        }
    }

    // the crop position (0..1 each way) from where the frame is on the left picture
    void SimCropFrom(RectangleF r)
    {
        double w, h;
        if (!LeftView.UprightSize(out w, out h)) return;
        simOpts.CropX = w - r.Width > 0.5 ? r.X / (w - r.Width) : 0.5;
        simOpts.CropY = h - r.Height > 0.5 ? r.Y / (h - r.Height) : 0.5;
        simOpts.CropX = Math.Max(0, Math.Min(1, simOpts.CropX));
        simOpts.CropY = Math.Max(0, Math.Min(1, simOpts.CropY));
    }

    void BuildSimBar()
    {
        Color panelBg = Color.FromArgb(38, 38, 38), boxBg = Color.FromArgb(52, 52, 52), note = Color.FromArgb(150, 150, 150);
        Font barFont = new Font("Segoe UI", 9.5f), noteFont = new Font("Segoe UI", 8.5f);
        int boxH = Util.S(26), noteH = Util.S(18);
        int row1 = Util.S(6), row2 = Util.S(58);                       // each row: a box, then its note under it
        int col1 = Util.S(10), box1 = col1 + Util.S(76);               // labels, then the Computer and Dither boxes
        int col2 = box1 + Util.S(290), col3 = col2 + Util.S(230);      // Framing, then Save
        simBar = new Panel { Dock = DockStyle.Top, Height = row2 + boxH + noteH + Util.S(8), BackColor = panelBg, Visible = false };
        Func<string, int, int, Label> label = delegate (string text, int x, int y)
        {
            var l = new Label { Text = text, Left = x, Top = y, Width = Util.S(76), Height = boxH, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver, Font = barFont };
            simBar.Controls.Add(l);
            return l;
        };
        Func<int, int, int, DarkCombo> box = delegate (int x, int y, int w)
        {
            var c = new DarkCombo { Left = x, Top = y, Width = w, Height = boxH, BackColor = boxBg, ForeColor = Color.White, Font = barFont };
            c.DropDownClosed += delegate { RightView.Focus(); };
            simBar.Controls.Add(c);
            return c;
        };
        Func<string, int, int, int, ContentAlignment, Label> noteUnder = delegate (string text, int x, int y, int w, ContentAlignment align)
        {
            var l = new Label { Text = text, Left = x, Top = y + boxH + Util.S(1), Width = w, Height = noteH, TextAlign = align, ForeColor = note, Font = noteFont };
            simBar.Controls.Add(l);
            return l;
        };
        Func<string, Color, Color, Button> button = delegate (string text, Color back, Color hover)
        {
            var b = new Button { Text = text, Height = boxH, TabStop = false, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White, Font = barFont };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hover;
            simBar.Controls.Add(b);
            return b;
        };

        // column 1: the computer, and under it its screen and colours; the dithering, and under it the keys
        label("Computer:", col1, row1);
        simMachine = box(box1, row1, Util.S(200));
        simMachine.SetItems(RetroConvert.PreviewShort);
        simDetail = noteUnder("", box1, row1, Util.S(280), ContentAlignment.TopLeft);
        label("Dither:", col1, row2);
        simDither = box(box1, row2, Util.S(270));
        simDither.SetItems(RetroConvert.DitherNames);
        noteUnder("Q, D change these", box1, row2, Util.S(270), ContentAlignment.TopLeft);

        // column 2: the framing, its key, and Centre the crop under it (only with Fill the screen)
        int frameW = Util.S(190);
        simFrame = box(col2, row1, frameW);
        simFrame.SetItems("Whole picture (borders)", "Fill the screen (cropped)");
        noteUnder(KeysOf("sim-fill") + " changes this", col2, row1, frameW, ContentAlignment.TopCenter);
        simCentre = button("Centre the crop", boxBg, Color.FromArgb(72, 72, 72));
        simCentre.ForeColor = Color.Gainsboro;
        simCentre.Width = Util.S(120);
        simCentre.Left = col2 + (frameW - simCentre.Width) / 2;
        simCentre.Top = row2;
        simCentre.Visible = false;
        simCentre.Click += delegate { SimCentre(); RightView.Focus(); };

        // column 3: Save, and its key
        // Save: the computer's own file (as previewed, no screen effects) plus a PNG of the screen with its effects
        Button save = button("Save...", Color.FromArgb(60, 130, 230), Color.FromArgb(80, 150, 245));
        save.Width = Util.S(90);
        save.Left = col3; save.Top = row1;
        save.Click += delegate { SimSave(); RightView.Focus(); };
        noteUnder(KeysOf("sim-save") + " saves", col3 - Util.S(30), row1, save.Width + Util.S(60), ContentAlignment.TopCenter);

        simMachine.SelectedIndexChanged += delegate { if (!simSyncing) SimSet(simMachine.SelectedIndex, simOpts.Dither, simOpts.Fill); };
        simDither.SelectedIndexChanged += delegate { if (!simSyncing) SimSet(simOpts.Machine, simDither.SelectedIndex, simOpts.Fill); };
        simFrame.SelectedIndexChanged += delegate { if (!simSyncing) SimSet(simOpts.Machine, simOpts.Dither, simFrame.SelectedIndex == 1); };
        tip.SetToolTip(simMachine, "Which 8-bit computer to show the picture on (" + KeysOf("sim-computer") + " next, Shift+" + KeysOf("sim-computer") + " previous)");
        tip.SetToolTip(simDither, "How in-between colours are made from the computer's own (" + KeysOf("sim-dither") + " next, Shift+" + KeysOf("sim-dither") + " previous)");
        tip.SetToolTip(simFrame, "Whole picture with black borders, or cropped to fill the computer's screen (" + KeysOf("sim-fill") + "); when cropped, drag the frame on the left picture to choose the part shown");
        tip.SetToolTip(simCentre, "Put the crop back in the middle of the picture");
        tip.SetToolTip(save, "Save the picture as this computer's own file (as previewed, without screen effects), and a PNG of the screen with its effects to share (" + KeysOf("sim-save") + ")");
    }

    void SimSave()
    {
        if (!beforeAfter || !baSim || simSaving) return;
        string src = baPath;
        if (src == null || !RightView.HasPicture) { RightView.ShowMessage("Wait until the preview is shown, then save it"); return; }
        RetroSim s = simOpts.Copy();
        RightView.PauseAnimation();                             // an animation: the frame shown is the one saved
        int frame = RightView.SimFrame, frames = RightView.SimFrameCount;   // (with the colours kept for every frame)
        int[] lockPal = RightView.SimLock;
        string ext = RetroConvert.PreviewExt(s.Machine), name = RetroConvert.PreviewShort[s.Machine];
        string dest;
        using (var d = new SaveFileDialog())
        {
            d.Title = "Save as a " + name + " picture (a PNG of the screen with its effects is saved beside it)";
            d.Filter = RetroConvert.PreviewFilter(s.Machine);
            d.DefaultExt = ext.TrimStart('.'); d.AddExtension = true; d.OverwritePrompt = true;
            d.InitialDirectory = Path.GetDirectoryName(src);
            d.FileName = Path.GetFileNameWithoutExtension(src) + ext;
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            dest = d.FileName;
        }
        if (!string.Equals(Path.GetExtension(dest), ext, StringComparison.OrdinalIgnoreCase)) dest += ext;
        string dir = Path.GetDirectoryName(dest);
        string why = MainForm.RefuseSource(dir);                    // local drives only, as for every other file operation
        if (why != null) { Warn("RetroGleek can't save there: " + why); return; }
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
        { Warn("That is the picture itself. Choose another name: the original is never changed."); return; }
        Bitmap shot;
        try { shot = RightView.RenderAsShown(4); }               // the screen, effects included (UI thread: it uses the viewer)
        catch (Exception ex) { Warn("The picture of the screen could not be made:\n\n" + Util.Why(ex)); return; }
        string png = Util.UniquePath(dir, Path.GetFileNameWithoutExtension(dest) + " - " + name + " screen" + (frames > 1 ? " frame " + (frame + 1) : ""), ".png");
        simSaving = true;
        RightView.ShowMessage("Saving " + Path.GetFileName(dest) + " and the screen PNG...");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err1 = null, err2 = null;
            try { RetroConvert.SavePreview(src, s, dest, frame, lockPal); }
            catch (Exception ex) { err1 = Util.Why(ex); }
            try { if (shot != null) Exporter.SavePng(shot, png, false); else err2 = "there was no picture to save."; }
            catch (Exception ex) { err2 = Util.Why(ex); }
            finally { if (shot != null) shot.Dispose(); }
            Util.Post(this, delegate { SimSaveDone(dest, png, err1, err2); });
        });
    }

    void SimSaveDone(string dest, string png, string err1, string err2)
    {
        simSaving = false;
        string dir = Path.GetDirectoryName(dest);
        FolderWritten(dir);
        var saved = new List<string>();
        if (err1 == null) saved.Add(Path.GetFileName(dest));
        if (err2 == null) saved.Add(Path.GetFileName(png));
        if (beforeAfter && baSim && saved.Count > 0) RightView.ShowMessage("Saved " + string.Join(" and ", saved));
        var errs = new List<string>();
        if (err1 != null) errs.Add(Path.GetFileName(dest) + ": " + err1);
        if (err2 != null) errs.Add(Path.GetFileName(png) + ": " + err2);
        if (errs.Count > 0) Warn((saved.Count > 0 ? "Saved " + string.Join(" and ", saved) + ", but not:" : "Nothing was saved:") + "\n\n" + string.Join("\n", errs));
    }

    // Ctrl+Shift+S in the retro preview of an animated GIF or WebP: the whole animation as an animated GIF of the
    // converted frames (no screen effects: a GIF holds 256 colours, the effects make thousands), and if you like every
    // frame as a PNG with its screen effects, as Ctrl+S makes one. The GIF is checked before it is kept; PNGs get free
    // names ("name frame 001.png" ...), so nothing is overwritten. Changing the preview or closing it stops the PNGs.
    void SimSaveAnim()
    {
        if (!beforeAfter || !baSim || simSaving) return;
        string src = baPath;
        int n = RightView.SimFrameCount;
        if (src == null || !RightView.HasPicture) { RightView.ShowMessage("Wait until the preview is shown, then save it"); return; }
        if (n < 2) { RightView.ShowMessage("This picture isn't animated: " + KeysOf("sim-save") + " saves it"); return; }
        int ready = RightView.SimFramesReady;
        if (ready < n) { RightView.ShowMessage("Wait until all " + n + " frames are converted (" + ready + " so far), then save"); return; }
        RightView.PauseAnimation();
        string name = RetroConvert.PreviewShort[simOpts.Machine];
        string dest;
        using (var d = new SaveFileDialog())
        {
            d.Title = "Save the " + name + " animation as a GIF (the converted frames, without screen effects)";
            d.Filter = "Animated GIF|*.gif";
            d.DefaultExt = "gif"; d.AddExtension = true; d.OverwritePrompt = true;
            d.InitialDirectory = Path.GetDirectoryName(src);
            d.FileName = Path.GetFileNameWithoutExtension(src) + " - " + name + ".gif";
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            dest = d.FileName;
        }
        if (!string.Equals(Path.GetExtension(dest), ".gif", StringComparison.OrdinalIgnoreCase)) dest += ".gif";
        string dir = Path.GetDirectoryName(dest);
        string why = MainForm.RefuseSource(dir);                    // local drives only, as for every other file operation
        if (why != null) { Warn("RetroGleek can't save there: " + why); return; }
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
        { Warn("That is the picture itself. Choose another name: the original is never changed."); return; }
        string stem = Path.GetFileNameWithoutExtension(dest);
        DialogResult pngs = MessageBox.Show(FindForm(),
            "Also save every frame with its screen effects as a PNG, to share or to make a video from?\n\n" +
            n + " PNG files, \"" + stem + " frame " + 1.ToString(FrameDigits(n)) + ".png\" and so on, next to the GIF. Each is drawn as " + KeysOf("sim-save") + " " +
            "draws one, which takes a moment per frame. Nothing is overwritten.",
            "Save the animation", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (pngs == DialogResult.Cancel) return;
        int w, h;
        int[] palette, delays;
        List<byte[]> frames;
        try { RightView.SimPlainFrames(out w, out h, out palette, out frames, out delays); }
        catch (Exception ex) { Warn("The animation can't be saved: " + Util.Why(ex)); return; }
        object token = RightView.SimPicture;
        bool alsoPngs = pngs == DialogResult.Yes;
        simSaving = true;
        RightView.ShowMessage("Saving " + Path.GetFileName(dest) + "...");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null;
            int written = 0;
            try { written = GifWriter.Save(dest, w, h, palette, frames, delays); }
            catch (Exception ex) { err = Util.Why(ex); }
            Util.Post(this, delegate
            {
                FolderWritten(dir);
                string gifText = err == null ? Path.GetFileName(dest) + " (" + n + " frames)" : null;
                if (alsoPngs) SimPngStep(token, dir, stem, 0, n, 0, gifText, err);
                else SimAnimDone(dir, gifText, err, 0, n, null);
            });
        });
    }

    static string FrameDigits(int n) { return n >= 1000 ? "D4" : "D3"; }

    // one frame's PNG with its screen effects: drawn here (the viewer draws it), saved and checked on a worker
    void SimPngStep(object token, string dir, string stem, int i, int n, int saved, string gifText, string gifErr)
    {
        if (!beforeAfter || !baSim || RightView.SimPicture != token)
        {
            SimAnimDone(dir, gifText, gifErr, saved, n, "the preview changed or was closed after " + saved + " of them");
            return;
        }
        if (i >= n) { SimAnimDone(dir, gifText, gifErr, saved, n, null); return; }
        Bitmap shot;
        try { shot = RightView.RenderFrameAsShown(i, 4); }
        catch (Exception ex) { SimAnimDone(dir, gifText, gifErr, saved, n, Util.Why(ex)); return; }
        if (shot == null) { SimAnimDone(dir, gifText, gifErr, saved, n, "frame " + (i + 1) + " could not be drawn"); return; }
        string png = Util.UniquePath(dir, stem + " frame " + (i + 1).ToString(FrameDigits(n)), ".png");
        RightView.ShowMessage("Saving the frames as PNG: " + (i + 1) + " of " + n);
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null;
            try { Exporter.SavePng(shot, png, false); }
            catch (Exception ex) { err = Path.GetFileName(png) + ": " + Util.Why(ex); }
            finally { shot.Dispose(); }
            Util.Post(this, delegate
            {
                if (err != null) SimAnimDone(dir, gifText, gifErr, saved, n, err);
                else SimPngStep(token, dir, stem, i + 1, n, saved + 1, gifText, gifErr);
            });
        });
    }

    // pngError: why the PNGs stopped (null: all saved, or none were asked for)
    void SimAnimDone(string dir, string gifText, string gifErr, int pngs, int n, string pngError)
    {
        simSaving = false;
        if (pngs > 0) FolderWritten(dir);
        var saved = new List<string>();
        if (gifText != null) saved.Add(gifText);
        if (pngs > 0) saved.Add(pngs + (pngs == 1 ? " frame PNG" : " frame PNGs"));
        if (beforeAfter && baSim && saved.Count > 0) RightView.ShowMessage("Saved " + string.Join(" and ", saved));
        var errs = new List<string>();
        if (gifErr != null) errs.Add("The GIF: " + gifErr);
        if (pngError != null) errs.Add("The frame PNGs stopped: " + pngError);
        if (errs.Count > 0) Warn((saved.Count > 0 ? "Saved " + string.Join(" and ", saved) + ", but:" : "Nothing was saved:") + "\n\n" + string.Join("\n", errs));
    }

    // Fill the screen: the crop back in the middle
    void SimCentre()
    {
        if (simOpts.CropX == 0.5 && simOpts.CropY == 0.5) return;
        simOpts.CropX = simOpts.CropY = 0.5;
        if (!beforeAfter || !baSim) return;
        RightView.Simulation = simOpts;
        BaMatch();
    }

    // keys while comparing: zoom keys go to the left picture (the right one follows), the rest to both
    bool CompareKey(Keys keyData)
    {
        if (beforeAfter) return BeforeAfterKey(keyData);
        Keys k = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
        if (k == Keys.Escape) { if (IsFullscreen()) FullscreenRequested(); else CloseRequested(); return true; }
        if (ctrl && shift && k == Keys.C) { CloseRequested(); return true; }
        if (k == Keys.Enter && !ctrl) { FullscreenRequested(); return true; }
        bool zoomKey = k == Keys.Add || k == Keys.Subtract || k == Keys.Oemplus || k == Keys.OemMinus ||
                       (k >= Keys.D0 && k <= Keys.D9) || (k >= Keys.NumPad0 && k <= Keys.NumPad9);
        bool navKey = k == Keys.Left || k == Keys.Right || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Space ||
                      k == Keys.Back || k == Keys.Home || k == Keys.End || k == Keys.S || k == Keys.Up || k == Keys.Down;
        if (navKey || ctrl) return true;                   // nothing to step through; Ctrl+C / Ctrl+S etc. do nothing here
        if (k == Keys.Z) return true;                      // no loupe while comparing (only one side would see Z let go)
        if (zoomKey) return LeftView.HandleKey(keyData);
        bool used = LeftView.HandleKey(keyData);
        used |= RightView.HandleKey(keyData);
        return used;
    }

    void BaTitle()
    {
        TitleChanged((baSim ? "Retro preview (" + SimText() + "): " : "Before / after: ") + Path.GetFileName(baPath) + " - RetroGleek");
    }

    // e.g. "ZX Spectrum, ordered dithering, whole picture"
    string SimText()
    {
        return RetroConvert.PreviewShort[simOpts.Machine] + ", " + RetroConvert.DitherShort[simOpts.Dither] + ", " + (simOpts.Fill ? "filling the screen" : "whole picture");
    }

    void SyncSimBar()
    {
        simSyncing = true;
        try
        {
            simMachine.SelectedIndex = simOpts.Machine;
            simDither.SelectedIndex = simOpts.Dither;
            simFrame.SelectedIndex = simOpts.Fill ? 1 : 0;
        }
        finally { simSyncing = false; }
        simCentre.Visible = simOpts.Fill;
        simDetail.Text = RetroConvert.PreviewDetails[simOpts.Machine];
    }

    // a new retro preview choice (bar, keys or the Effects menu); converts the picture again when the preview is open
    void SimSet(int machine, int dither, bool fill)
    {
        int n = RetroConvert.PreviewFormats.Length;
        machine = ((machine % n) + n) % n;
        dither = ((dither % 3) + 3) % 3;
        if (machine == simOpts.Machine && dither == simOpts.Dither && fill == simOpts.Fill) return;
        simOpts.Machine = machine; simOpts.Dither = dither; simOpts.Fill = fill;
        SyncSimBar();
        if (!beforeAfter || !baSim) return;
        RightView.Simulation = simOpts;
        BaMatch();
        BaTitle();
        RightView.ShowMessage(SimText());
        if (fill) LeftView.ShowMessage("Drag the frame to choose the part of the picture the screen shows");
        RightView.Focus();
    }

    // one side moved to another picture: the other follows
    void BaFollow(Canvas other, string p)
    {
        if (!beforeAfter || p == null || string.Equals(p, baPath, StringComparison.OrdinalIgnoreCase)) return;
        baPath = p;
        int i = baList.FindIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) other.Open(baList, i); else other.ShowSingle(p);
        BaTitle();
    }

    // Before / after: the left side always shows the picture at the right side's scale and position, so both are the
    // same size on screen. Fitting on its own, the left side would be larger whenever scanlines make the right side
    // fit at a whole-number size (300% beside 200%, say). True TV aspect can still make them differ slightly in width.
    // Retro preview: the two sides show pictures of different sizes (a photo, and a 320 x 200 screen say), so the left
    // side takes the right side's zoom times the conversion's scale (SimScale): the picture's height is the same on
    // both, and as both are centred on the picture, the same offset keeps them in step. Until the converted picture is
    // there, the left side fits its half. With Fill the screen, the left side shows the whole picture, fitted, with a
    // frame around the part the screen shows (drag it to move the crop), and zooms on its own.
    void BaMatch()
    {
        if (!beforeAfter) return;
        double z, x, y; int fm;
        RightView.GetView(out z, out x, out y, out fm);
        if (!baSim) { LeftView.SetView(z, x, y, 0); return; }
        if (simOpts.Fill)                                  // the whole picture fitted, with the crop frame on it
        {
            LeftView.SetView(z, x, y, 2);
            double w, h;
            LeftView.CropFrame = LeftView.UprightSize(out w, out h) ? RetroConvert.CropRect(simOpts.Machine, w, h, simOpts.CropX, simOpts.CropY) : RectangleF.Empty;
            return;
        }
        LeftView.CropFrame = RectangleF.Empty;
        double sc = RightView.SimScale;
        if (sc > 0) LeftView.SetView(z * sc, x, y, 0); else LeftView.SetView(z, x, y, 2);
    }

    // the preview's own automatic effects (switched on because you had none) are not kept unless you changed them there
    bool BaKeepsEffects { get { return !baSim || !simAutoForced || simFxTouched; } }

    // what you set on the right side becomes the viewer's own settings (also when RetroGleek closes meanwhile)
    void BaKeepEffects()
    {
        main.UseEffects(RightView.ManualEffects);
        main.AutoEffects = RightView.AutoEffects;
        main.EffectsOff = RightView.EffectsOff;
    }

    // keys in before / after: effects go to the right side only; moving and zooming to one side (the other follows);
    // turning, flipping and display options to both. Retro preview: Q / D / Shift+F choose the computer, the dithering
    // and the framing; turning and flipping are not used
    bool BeforeAfterKey(Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0, alt = (keyData & Keys.Alt) != 0;
        if (k == Keys.Escape) { if (IsFullscreen()) FullscreenRequested(); else CloseRequested(); return true; }
        if (k == Keys.O && shift && !ctrl && !alt) { CloseRequested(); return true; }
        if (ctrl && shift && k == Keys.C) { CloseRequested(); return true; }
        if (k == Keys.Enter && !ctrl) { FullscreenRequested(); return true; }
        if (baSim && ctrl && !shift && !alt && k == Keys.S) { SimSave(); return true; }   // retro preview: save the files
        if (baSim && ctrl && shift && !alt && k == Keys.S) { SimSaveAnim(); return true; }   // retro preview: save the animation
        if (ctrl || alt) return true;                      // Ctrl+C / Ctrl+S etc. do nothing here (presets are handled before)
        if (k == Keys.S || k == Keys.J || k == Keys.Z || k == Keys.Up || k == Keys.Down) return true;   // no slideshow, filmstrip or loupe
        if (baSim)
        {
            if (k == Keys.Q) { SimSet(simOpts.Machine + (shift ? -1 : 1), simOpts.Dither, simOpts.Fill); return true; }   // computer
            if (k == Keys.D) { SimSet(simOpts.Machine, simOpts.Dither + (shift ? -1 : 1), simOpts.Fill); return true; }   // dithering
            if (k == Keys.F && shift) { SimSet(simOpts.Machine, simOpts.Dither, !simOpts.Fill); return true; }          // framing
            if (k == Keys.R || k == Keys.L || k == Keys.H || k == Keys.V) return true;   // the conversion is of the upright picture
            if (k == Keys.P || k == Keys.Oemcomma || k == Keys.OemPeriod) return RightView.HandleKey(keyData);   // animation: the left side follows
        }
        bool effectKey = k == Keys.T || k == Keys.K || k == Keys.M || k == Keys.C || k == Keys.W || k == Keys.U || k == Keys.O || k == Keys.A;
        if (effectKey) { simFxTouched = true; bool fx = RightView.HandleKey(keyData); BaMatch(); return fx; }   // scanlines on / off can change the fit
        bool oneSide = k == Keys.Left || k == Keys.Right || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Space ||
                       k == Keys.Back || k == Keys.Home || k == Keys.End ||
                       k == Keys.Add || k == Keys.Subtract || k == Keys.Oemplus || k == Keys.OemMinus ||
                       (k >= Keys.D0 && k <= Keys.D9) || (k >= Keys.NumPad0 && k <= Keys.NumPad9);
        if (oneSide) { bool one = RightView.HandleKey(keyData); BaMatch(); return one; }   // the left side follows (picture: BaFollow)
        bool used = LeftView.HandleKey(keyData);
        used |= RightView.HandleKey(keyData);
        BaMatch();                                         // F, R, L ... fit each side again: the left one takes the right one's scale
        return used;
    }
}
