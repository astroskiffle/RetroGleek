// RetroGleek - the main window, continued: context menus, the menu bar, keyboard shortcuts, help and about
// (the commands themselves, with their keys: MainForm.Commands.cs)
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using VBFS = Microsoft.VisualBasic.FileIO.FileSystem;
using VBUI = Microsoft.VisualBasic.FileIO.UIOption;
using VBRecycle = Microsoft.VisualBasic.FileIO.RecycleOption;
using WinTimer = System.Windows.Forms.Timer;
using VBCancel = Microsoft.VisualBasic.FileIO.UICancelOption;

// ====================================================================== main window (continued)

partial class MainForm
{
    // ================================================================ context menus

    static ToolStripMenuItem AddEntry(ContextMenuStrip m, string text, string keys, EventHandler h)
    {
        var mi = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
        mi.Click += h;
        m.Items.Add(mi);
        return mi;
    }

    void BuildMenus()
    {
        gridMenu = new ContextMenuStrip();
        gOpen = AddEntry(gridMenu, "Open", "Enter", delegate { OpenIndex(grid.FocusIndex); });
        gridMenu.Items.Add(new ToolStripSeparator());
        gInvert = AddEntry(gridMenu, "Invert selection", KeysOf("invert"), delegate { grid.InvertSelection(); });
        gridMenu.Items.Add(new ToolStripSeparator());
        gRename = AddEntry(gridMenu, "Rename", KeysOf("rename"), delegate { DoRename(false); });
        gBatch = AddEntry(gridMenu, "Batch rename...", KeysOf("batch-rename"), delegate { DoBatchRename(); });
        gCompare = AddEntry(gridMenu, "Compare side by side", KeysOf("compare"), delegate { StartCompare(); });
        gCut = AddEntry(gridMenu, "Cut", KeysOf("cut"), delegate { DoCut(false); });
        gCopy = AddEntry(gridMenu, "Copy", KeysOf("copy"), delegate { DoCopy(false); });
        gPaste = AddEntry(gridMenu, "Paste", KeysOf("paste"), delegate { DoPaste(false); });
        gridMenu.Items.Add(new ToolStripSeparator());
        gToOther = AddEntry(gridMenu, "Copy to the other pane", KeysOf("copy-other"), delegate { SendToOther(false); });
        gMoveOther = AddEntry(gridMenu, "Move to the other pane", KeysOf("move-other"), delegate { SendToOther(true); });
        gProps = AddEntry(gridMenu, "Properties...", KeysOf("properties"), delegate { ShowProperties(); });
        gRate = new ToolStripMenuItem("Rating");               // the selected pictures (or the focused one), as Alt+0..5
        for (int ri = 0; ri <= 5; ri++)
        {
            int stars = ri;
            var it = new ToolStripMenuItem(ri == 0 ? "No rating" : Marks.StarText(ri)) { ShortcutKeyDisplayString = KeysOf("rate-" + ri), Tag = ri };
            it.Click += delegate { DoMark(stars, -1, false); };
            gRate.DropDownItems.Add(it);
        }
        gridMenu.Items.Add(gRate);
        gridMenu.Items.Add(new ToolStripSeparator());
        gSave = AddEntry(gridMenu, "Save as PNG (lossless)...", KeysOf("save-png"), delegate { DoSavePng(); });
        gFrames = AddEntry(gridMenu, "Export all frames as PNG...", KeysOf("export-frames"), delegate { DoExportFrames(); });
        gridMenu.Items.Add(new ToolStripSeparator());
        gDelete = AddEntry(gridMenu, "Delete (to Recycle Bin)", KeysOf("delete"), delegate { DoDelete(false); });
        gridMenu.Items.Add(new ToolStripSeparator());
        AddEntry(gridMenu, "Refresh", KeysOf("refresh"), delegate { ReloadAll(); });
        gridMenu.Items.Add(new ToolStripSeparator());
        var sortMenu = new ToolStripMenuItem("Sort by");
        string[] sortNames = { "Name", "Date modified", "Size", "Type" };
        for (int i = 0; i < sortNames.Length; i++)
        {
            int key = i;
            gSort[i] = new ToolStripMenuItem(sortNames[i]);
            gSort[i].Click += delegate { ChooseSort(key); };
            sortMenu.DropDownItems.Add(gSort[i]);
        }
        sortMenu.DropDownItems.Add(new ToolStripSeparator());
        gSortDesc = new ToolStripMenuItem("Reverse order");
        gSortDesc.Click += delegate { grid.SetSort(grid.SortKey, !grid.SortDesc); };
        sortMenu.DropDownItems.Add(gSortDesc);
        gridMenu.Items.Add(sortMenu);
        gDetails = AddEntry(gridMenu, "Show details on tiles", "", delegate { grid.ShowDetails = !grid.ShowDetails; });
        gridMenu.Items.Add(new ToolStripSeparator());
        gRetro = AddEntry(gridMenu, "Show retro-computer formats (RECOIL)", KeysOf("retro"), delegate { ToggleRetro(); });

        treeMenu = new ContextMenuStrip();
        tNew = AddEntry(treeMenu, "New Folder", "", delegate { DoNewFolder(); });
        treeMenu.Items.Add(new ToolStripSeparator());
        tRename = AddEntry(treeMenu, "Rename", KeysOf("rename"), delegate { DoRename(true); });
        tCut = AddEntry(treeMenu, "Cut", KeysOf("cut"), delegate { DoCut(true); });
        tCopy = AddEntry(treeMenu, "Copy", KeysOf("copy"), delegate { DoCopy(true); });
        tPaste = AddEntry(treeMenu, "Paste", KeysOf("paste"), delegate { DoPaste(true); });
        treeMenu.Items.Add(new ToolStripSeparator());
        tDelete = AddEntry(treeMenu, "Delete (to Recycle Bin)", KeysOf("delete"), delegate { DoDelete(true); });
        treeMenu.Items.Add(new ToolStripSeparator());
        AddEntry(treeMenu, "Refresh", KeysOf("refresh"), delegate { ReloadAll(); });
        treeMenu.Items.Add(new ToolStripSeparator());
        tRetro = AddEntry(treeMenu, "Show retro-computer formats (RECOIL)", KeysOf("retro"), delegate { ToggleRetro(); });
        treeMenu.Items.Insert(1, tFav = new ToolStripMenuItem("Add to favourites"));
        tFav.Click += delegate { ToggleFavourite(NodePath(tree.SelectedNode)); };
    }

    void ShowGridMenu(Point p, int hit)
    {
        lastPane = grid;
        bool on = hit >= 0;
        gOpen.Enabled = on;
        gRename.Enabled = on && grid.SelectedIndices().Count == 1;
        gBatch.Enabled = on;
        gCompare.Enabled = CanCompare();
        gCopy.Enabled = on;
        gCut.Enabled = on;
        gDelete.Enabled = on;
        gPaste.Enabled = grid.Dir != null && ClipHasFiles();
        gSave.Enabled = on;
        gFrames.Enabled = on;
        List<string> targets = MarkTargets();                  // tick the rating they all share (none if they differ)
        int shared = -1;
        foreach (string tp in targets)
        {
            Mark tm = Marks.ForFile(tp);
            int st = tm == null ? 0 : tm.Stars;
            if (shared < 0) shared = st; else if (shared != st) { shared = -2; break; }
        }
        gRate.Enabled = on && targets.Count > 0;
        gProps.Enabled = on;
        gToOther.Visible = gMoveOther.Visible = dual;
        gToOther.Enabled = gMoveOther.Enabled = on && CanSendToOther();
        foreach (ToolStripItem ti in gRate.DropDownItems)
        {
            var tmi = ti as ToolStripMenuItem;
            if (tmi != null) tmi.Checked = shared >= 0 && (int)tmi.Tag == shared;
        }
        gInvert.Enabled = grid.Items.Count > 0;
        for (int i = 0; i < gSort.Length; i++) gSort[i].Checked = grid.SortKey == i;
        gSortDesc.Checked = grid.SortDesc;
        gDetails.Checked = grid.ShowDetails;
        gRetro.Checked = Retro.Enabled;
        gridMenu.Show(grid, p);
    }

    void OnTreeClick(object s, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        tree.SelectedNode = e.Node;
        lastPane = tree;
        string p = NodePath(e.Node);
        bool real = p != null, plain = real && !IsRootPath(p) && e.Node.Name != "fav";   // a favourite is a shortcut
        tNew.Enabled = real;
        tFav.Enabled = real;
        tFav.Text = real && IsFavourite(p) ? "Remove from favourites" : "Add to favourites";
        tRename.Enabled = plain;
        tCopy.Enabled = plain;
        tCut.Enabled = plain;
        tDelete.Enabled = plain;
        tPaste.Enabled = real && ClipHasFiles();
        tRetro.Checked = Retro.Enabled;
        treeMenu.Show(tree, e.Location);
    }

    // ================================================================ menu bar

    // Each entry remembers when it is available and whether it is ticked; both are worked out each time a menu
    // opens. The commands themselves (text, keys, what they do) are in the command table (MainForm.Commands.cs);
    // the menu bar only places them. Lists that change (recent folders, presets, sort keys ...) are built here.
    sealed class MenuState
    {
        public ToolStripMenuItem Item;
        public Func<bool> On, Checked;
        public Func<string> Text;
        public Func<bool> Show;                            // shown only while this is true (e.g. a preset slot in use)
        public bool InCompare;                             // stays available while two pictures are being compared
    }

    MenuStrip menuBar;

    readonly List<MenuState> menuStates = new List<MenuState>();

    static readonly Color MenuText = Color.Gainsboro;

    ToolStripMenuItem MenuTop(string text)
    {
        var m = new ToolStripMenuItem(text) { ForeColor = MenuText };
        m.DropDownOpening += delegate { UpdateMenus(); };
        m.DropDown.BackColor = Color.FromArgb(38, 38, 38);
        menuBar.Items.Add(m);
        return m;
    }

    ToolStripMenuItem MenuSub(ToolStripMenuItem parent, string text, Func<bool> on)
    {
        var m = new ToolStripMenuItem(text) { ForeColor = MenuText };
        m.DropDown.BackColor = Color.FromArgb(38, 38, 38);
        parent.DropDownItems.Add(m);
        menuStates.Add(new MenuState { Item = m, On = on });
        return m;
    }

    ToolStripMenuItem MenuEntry(ToolStripMenuItem parent, string text, string keys, Action run, Func<bool> on, Func<bool> chk)
    {
        var m = new ToolStripMenuItem(text) { ForeColor = MenuText, ShortcutKeyDisplayString = keys };
        m.Click += delegate { try { run(); UpdatePreview(); } catch (Exception ex) { Warn(ex.Message); } };   // the preview pane's badges follow
        parent.DropDownItems.Add(m);
        menuStates.Add(new MenuState { Item = m, On = on, Checked = chk });
        return m;
    }

    // a command from the command table, with its keys (text: another wording for this menu, or null)
    ToolStripMenuItem MenuCmd(ToolStripMenuItem parent, string id, string text = null)
    {
        Command c = Cmd(id);
        ToolStripMenuItem m = MenuEntry(parent, text ?? c.Text, c.KeyText, c.Run, c.On, c.Checked);
        MenuState s = LastEntry;
        if (text == null) s.Text = c.TextNow;
        s.Show = c.Show;
        s.InCompare = c.InCompare;
        return m;
    }

    MenuState LastEntry { get { return menuStates[menuStates.Count - 1]; } }

    static void MenuSep(ToolStripMenuItem parent) { parent.DropDownItems.Add(new ToolStripSeparator()); }

    // an entry that only says something (greyed out)
    void MenuNote(ToolStripMenuItem parent, string text) { MenuEntry(parent, text, "", delegate { }, delegate { return false; }, null); }

    void UpdateMenus()
    {
        foreach (MenuState s in menuStates)
        {
            try
            {
                if (s.Show != null) s.Item.Visible = s.Show();
                if (s.Text != null) s.Item.Text = s.Text();
                s.Item.Enabled = (!comparing || s.InCompare) && (s.On == null || s.On());
                if (s.Checked != null) s.Item.Checked = s.Checked();
            }
            catch (Exception) { }                             // a menu must never raise an error dialog
        }
    }

    // a submenu of choices for one setting, the first showing the key that cycles through them
    void MenuChoices(ToolStripMenuItem sub, string cycleKeys, string[] names, Action<int> choose, Func<int, bool> chosen)
    {
        for (int i = 0; i < names.Length; i++)
        {
            int mode = i;
            MenuEntry(sub, names[i], i == 0 ? cycleKeys + " cycles" : "", delegate { choose(mode); }, null, delegate { return chosen(mode); });
        }
    }

    void BuildMenuBar()
    {
        menuBar = new MenuStrip
        {
            Dock = DockStyle.Top, Renderer = new DarkMenuRenderer(), BackColor = Color.FromArgb(38, 38, 38),
            ForeColor = MenuText, Font = new Font("Segoe UI", 9f), GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(Util.S(4), Util.S(2), 0, Util.S(2))
        };
        Func<bool> browsing = delegate { return !viewing; };
        Func<bool> inViewer = delegate { return viewing && canvas.HasPicture; };
        Func<bool> hasSel = delegate { return !viewing && (lastPane == tree ? IsPlainFolder(tree.SelectedNode) : grid.SelectedIndices().Count > 0); };

        // ---- File
        ToolStripMenuItem file = MenuTop("&File");
        MenuCmd(file, "open");
        MenuCmd(file, "new-folder");
        MenuSep(file);
        MenuCmd(file, "save-png");
        MenuCmd(file, "properties");
        MenuCmd(file, "export-frames");
        MenuCmd(file, "save-turn");
        MenuSep(file);
        MenuCmd(file, "exit");

        // ---- Edit
        ToolStripMenuItem edit = MenuTop("&Edit");
        MenuCmd(edit, "undo");
        MenuSep(edit);
        MenuCmd(edit, "copy");
        MenuCmd(edit, "cut");
        MenuCmd(edit, "paste");
        Func<bool> canSend = delegate { return viewing ? canvas.CurrentPath != null : hasSel(); };
        for (int tm = 0; tm < 2; tm++)
        {
            bool mv = tm == 1;
            ToolStripMenuItem sendMenu = MenuSub(edit, mv ? "&Move to" : "C&opy to", canSend);
            MenuCmd(sendMenu, mv ? "move-to" : "copy-to");
            for (int ri = 0; ri < MaxRecentDest; ri++)
            {
                int slot = ri;
                MenuEntry(sendMenu, "Recent " + (slot + 1), "", delegate { if (slot < recentDest.Count) TransferTo(mv, recentDest[slot]); }, null, null);
                LastEntry.Show = delegate { return slot < recentDest.Count; };
                LastEntry.Text = delegate { return slot < recentDest.Count ? "&" + (slot + 1) + "  " + recentDest[slot].Replace("&", "&&") : ""; };
            }
        }
        MenuCmd(edit, "copy-other");
        MenuCmd(edit, "move-other");
        MenuCmd(edit, "rename");
        MenuCmd(edit, "delete");
        ToolStripMenuItem turnMenu = MenuSub(edit, "Turn the file (lossless &JPEG)", delegate { return !rotateBusy && (viewing ? canvas.CurrentPath != null : grid.SelectedIndices().Count > 0); });
        MenuCmd(turnMenu, "turn-cw");
        MenuCmd(turnMenu, "turn-ccw");
        MenuCmd(turnMenu, "turn-180");
        MenuSep(edit);
        // marks: the picture in the viewer, or the selected pictures (kept in marks.tsv, never in the pictures)
        Func<bool> canMark = delegate { return viewing ? canvas.CurrentPath != null : MarkTargets().Count > 0; };
        ToolStripMenuItem rate = MenuSub(edit, "Rati&ng", canMark);
        for (int ri = 0; ri <= 5; ri++) MenuCmd(rate, "rate-" + ri);
        MenuSep(rate);
        MenuCmd(rate, "write-ratings");
        ToolStripMenuItem lab = MenuSub(edit, "Colour &label", canMark);
        for (int li = 0; li <= 5; li++) MenuCmd(lab, "label-" + li);
        MenuCmd(edit, "tag");
        MenuCmd(edit, "select-tagged");
        MenuCmd(edit, "clear-tags");
        MenuSep(edit);
        MenuCmd(edit, "select-all");
        MenuCmd(edit, "invert");
        MenuSep(edit);
        MenuCmd(edit, "filter");
        MenuCmd(edit, "subfolders");

        // ---- View
        ToolStripMenuItem view = MenuTop("&View");
        MenuCmd(view, "viewer");
        MenuCmd(view, "fullscreen");
        MenuCmd(view, "compare");
        MenuCmd(view, "refresh");
        MenuSep(view);
        ToolStripMenuItem sortBy = MenuSub(view, "&Sort by", browsing);
        string[] sortNames = { "&Name", "&Date modified", "&Size", "&Type", "&Platform", "D&imensions", "&Frames", "&Colours", "S&tatus", "R&ating", "&Label" };
        for (int i = 0; i < sortNames.Length; i++)
        {
            int key = i;
            MenuEntry(sortBy, sortNames[i], "", delegate { ChooseSort(key); }, null, delegate { return grid.SortKey == key; });
        }
        MenuSep(sortBy);
        MenuEntry(sortBy, "&Reverse order", "", delegate { grid.SetSort(grid.SortKey, !grid.SortDesc); }, null, delegate { return grid.SortDesc; });
        ToolStripMenuItem showMenu = MenuSub(view, "S&how (marks)", browsing);
        for (int shi = 0; shi < ThumbGrid.ShowNames.Length; shi++)
        {
            int mode = shi;
            if (shi == 3 || shi == 9) MenuSep(showMenu);
            MenuEntry(showMenu, ThumbGrid.ShowNames[shi], "", delegate { SetShow(mode); }, null, delegate { return grid.ShowMode == mode; });
        }
        MenuCmd(view, "dual");
        MenuCmd(view, "switch-pane");
        MenuCmd(view, "swap-panes");
        MenuCmd(view, "details");
        MenuCmd(view, "tile-details");
        MenuSep(view);
        ToolStripMenuItem zoom = MenuSub(view, "&Zoom", inViewer);
        MenuCmd(zoom, "zoom-fit");
        MenuCmd(zoom, "zoom-100");
        MenuCmd(zoom, "zoom-in");
        MenuCmd(zoom, "zoom-out");
        MenuCmd(view, "rotate-cw");
        MenuCmd(view, "rotate-ccw");
        MenuCmd(view, "flip-h");
        MenuCmd(view, "flip-v");
        MenuSep(view);
        MenuCmd(view, "info");
        MenuCmd(view, "exif");
        MenuCmd(view, "pixel-grid");
        MenuCmd(view, "loupe");
        MenuCmd(view, "filmstrip");
        MenuCmd(view, "preview");

        // ---- Effects: screen effects for retro pictures (display only; remembered in settings.ini)
        ToolStripMenuItem effects = MenuTop("Effe&cts");
        MenuCmd(effects, "effects");
        MenuCmd(effects, "before-after");
        // retro preview: a photo or other modern picture as it might look on an 8-bit computer (display only)
        MenuCmd(effects, "retro-preview");
        ToolStripMenuItem simComp = MenuSub(effects, Cmd("sim-computer").Text, null);
        LastEntry.InCompare = true;
        for (int ci = 0; ci < RetroConvert.PreviewNames.Length; ci++)
        {
            int m = ci;
            MenuEntry(simComp, "&" + (m + 1) + "  " + RetroConvert.PreviewNames[m], m == 0 ? KeysOf("sim-computer") + " cycles" : "",
                      delegate { SimSet(m, simOpts.Dither, simOpts.Fill); }, null, delegate { return simOpts.Machine == m; });
            LastEntry.InCompare = true;
        }
        ToolStripMenuItem simDith = MenuSub(effects, Cmd("sim-dither").Text, null);
        LastEntry.InCompare = true;
        for (int di = 0; di < RetroConvert.DitherNames.Length; di++)
        {
            int d = di;
            MenuEntry(simDith, "&" + (d + 1) + "  " + RetroConvert.DitherNames[d], d == 0 ? KeysOf("sim-dither") + " cycles" : "",
                      delegate { SimSet(simOpts.Machine, d, simOpts.Fill); }, null, delegate { return simOpts.Dither == d; });
            LastEntry.InCompare = true;
        }
        MenuCmd(effects, "sim-fill");
        MenuCmd(effects, "sim-save");
        MenuCmd(effects, "sim-save-anim");
        MenuCmd(effects, "auto");
        MenuSep(effects);
        MenuCmd(effects, "tv");
        MenuChoices(MenuSub(effects, Cmd("scanlines").Text, null), KeysOf("scanlines"), new[] { "&Off", "&Light", "&Medium", "&Strong" },
                    delegate (int v) { canvas.ScanMode = v; }, delegate (int v) { return canvas.ScanMode == v; });
        MenuChoices(MenuSub(effects, Cmd("mask").Text, null), KeysOf("mask"),
                    new[] { "&Off", "&Aperture grille (Trinitron)", "&Slot mask (most TVs, Commodore 1084S)", "&Dot mask (fine)" },
                    delegate (int v) { canvas.MaskMode = v; }, delegate (int v) { return canvas.MaskMode == v; });
        MenuChoices(MenuSub(effects, Cmd("signal").Text, null), KeysOf("signal"),
                    new[] { "&RGB (sharp, as decoded)", "&S-Video (colours soften a little)", "&Composite (dithering blends, as on a TV)" },
                    delegate (int v) { canvas.SignalMode = v; }, delegate (int v) { return canvas.SignalMode == v; });
        MenuChoices(MenuSub(effects, Cmd("glow").Text, null), KeysOf("glow"), new[] { "&Off", "&Soft", "St&rong" },
                    delegate (int v) { canvas.GlowMode = v; }, delegate (int v) { return canvas.GlowMode == v; });
        MenuChoices(MenuSub(effects, Cmd("curvature").Text, null), KeysOf("curvature"), new[] { "&Off (flat)", "&Slight", "St&rong" },
                    delegate (int v) { canvas.CurveMode = v; }, delegate (int v) { return canvas.CurveMode == v; });
        MenuSep(effects);
        for (int pi = 1; pi <= MaxPresets; pi++) MenuCmd(effects, "preset-" + pi);
        MenuNote(effects, "(no presets saved yet)");
        LastEntry.Show = delegate { return PresetCount() == 0; };
        MenuCmd(effects, "save-preset");
        ToolStripMenuItem del = MenuSub(effects, "&Delete a preset", delegate { return PresetCount() > 0; });
        for (int di = 0; di < MaxPresets; di++)
        {
            int slot = di;
            MenuEntry(del, "Preset " + (slot + 1), "", delegate { DeletePreset(slot); }, null, null);
            LastEntry.Show = delegate { return presets[slot] != null; };
            LastEntry.Text = delegate { return presets[slot] == null ? "" : "&" + (slot + 1) + "  " + presets[slot].Name.Replace("&", "&&"); };
        }
        MenuSep(effects);
        MenuNote(effects, "Retro pictures only. Display only: saved and copied pictures never change");

        // ---- Go
        ToolStripMenuItem go = MenuTop("&Go");
        MenuCmd(go, "back");
        MenuCmd(go, "forward");
        MenuCmd(go, "up");
        MenuCmd(go, "address");
        MenuSep(go);
        ToolStripMenuItem favMenu = MenuSub(go, "Fa&vourites", null);
        MenuCmd(favMenu, "favourite");
        MenuSep(favMenu);
        for (int fi = 0; fi < MaxFavourites; fi++)
        {
            int slot = fi;
            MenuEntry(favMenu, "Favourite " + (slot + 1), "", delegate { if (slot < favourites.Count) OpenFolderShortcut(favourites[slot]); }, null, null);
            LastEntry.Show = delegate { return slot < favourites.Count; };
            LastEntry.Text = delegate { return slot < favourites.Count ? (slot < 9 ? "&" + (slot + 1) + "  " : "    ") + FavText(favourites[slot]).Replace("&", "&&") + "    " + favourites[slot].Replace("&", "&&") : ""; };
        }
        MenuNote(favMenu, "(no favourites yet: " + KeysOf("favourite") + " adds the open folder)");
        LastEntry.Show = delegate { return favourites.Count == 0; };
        ToolStripMenuItem recMenu = MenuSub(go, "Recen&t folders", delegate { return recentFolders.Count > 0; });
        for (int ri = 0; ri < MaxRecentFolders; ri++)
        {
            int slot = ri;
            MenuEntry(recMenu, "Recent " + (slot + 1), "", delegate { if (slot < recentFolders.Count) OpenFolderShortcut(recentFolders[slot]); }, null, null);
            LastEntry.Show = delegate { return slot < recentFolders.Count; };
            LastEntry.Text = delegate { return slot < recentFolders.Count ? "&" + ((slot + 1) % 10) + "  " + recentFolders[slot].Replace("&", "&&") : ""; };
        }
        MenuSep(go);
        MenuCmd(go, "prev-pic");
        MenuCmd(go, "next-pic");
        MenuCmd(go, "first-pic");
        MenuCmd(go, "last-pic");

        // ---- Tools
        ToolStripMenuItem tools = MenuTop("&Tools");
        MenuCmd(tools, "analyse");
        MenuSep(tools);
        MenuCmd(tools, "audit");
        MenuCmd(tools, "audit-small");
        MenuCmd(tools, "duplicates");
        MenuCmd(tools, "contact-sheet");
        MenuCmd(tools, "convert");
        MenuCmd(tools, "batch-rename");
        MenuSep(tools);
        MenuCmd(tools, "slideshow");
        ToolStripMenuItem anim = MenuSub(tools, "&Animated GIF / WebP", delegate { return viewing && canvas.Animated; });
        MenuCmd(anim, "anim-pause");
        MenuCmd(anim, "prev-frame");
        MenuCmd(anim, "next-frame");
        MenuSep(anim);
        MenuCmd(anim, "export-frames", "&Export all frames as PNG...");

        // ---- Settings (remembered in settings.ini next to RetroGleek.exe, written when RetroGleek closes)
        ToolStripMenuItem settings = MenuTop("&Settings");
        MenuCmd(settings, "retro");
        MenuSep(settings);
        MenuCmd(settings, "fit-lock");
        MenuCmd(settings, "auto-advance");
        MenuCmd(settings, "checker");
        ToolStripMenuItem pix = MenuSub(settings, Cmd("pixels").Text, null);
        MenuEntry(pix, "&Automatic (retro sharp, photos smooth)", KeysOf("pixels") + " cycles", delegate { canvas.PixMode = 0; }, null, delegate { return canvas.PixMode == 0; });
        MenuEntry(pix, "&Crisp", "", delegate { canvas.PixMode = 1; }, null, delegate { return canvas.PixMode == 1; });
        MenuEntry(pix, "S&harp (even pixels at any size)", "", delegate { canvas.PixMode = 3; }, null, delegate { return canvas.PixMode == 3; });
        MenuEntry(pix, "&Smooth", "", delegate { canvas.PixMode = 2; }, null, delegate { return canvas.PixMode == 2; });
        MenuSep(settings);
        MenuCmd(settings, "clear-history");
        MenuCmd(settings, "reset");
        MenuNote(settings, "Settings are remembered in settings.ini next to RetroGleek.exe");

        // ---- Help
        ToolStripMenuItem help = MenuTop("&Help");
        MenuCmd(help, "help");
        MenuCmd(help, "self-test");
        MenuSep(help);
        MenuCmd(help, "about");
    }

    // ================================================================ keyboard shortcuts

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (RunShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Every key goes through here. The commands' keys come from the command table (MainForm.Commands.cs), in
    // stages: everywhere; then the viewer, or the browser (first the keys that also work while typing in the filter or
    // address box, then the others). What isn't a command is handled here (Esc, Enter, Shift+O) or by the viewer.
    bool RunShortcut(Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0, alt = (keyData & Keys.Alt) != 0, shift = (keyData & Keys.Shift) != 0;
        bool typing = filterBox.Focused || addrBox.Focused;   // a text box has the keyboard: don't steal editing keys
        if (RunCommandKey(keyData, KeyWhere.Always)) return true;
        if (k == Keys.O && shift && !ctrl && !alt && !typing && !comparing && (!viewing || CanBeforeAfter() || CanRetroPreview())) { StartBeforeAfter(); return true; }   // before / after, or the retro preview
        if (comparing) return IsMarkKey(keyData) || CompareKey(keyData);  // marks are given in the browser or the viewer

        if (viewing)
        {
            if (RunCommandKey(keyData, KeyWhere.Viewer)) return true;
            if (k == Keys.Enter && !ctrl) { ToggleFull(); return true; }
            if (k == Keys.Escape)
            {
                if (canvas.Slideshow) canvas.StopSlideshow();      // Esc stops a running slideshow first
                else if (full) ToggleFull();
                else SetViewing(false);
                return true;
            }
            return canvas.HandleKey(keyData);
        }

        if (RunCommandKey(keyData, KeyWhere.Browser)) return true;
        bool onBar = sortCombo.Focused || sizeSlider.Focused;   // the sort box / size slider have the keyboard
        if (typing || onBar)
        {
            if (k != Keys.Escape) return false;
            if (filterBox.Focused) CloseFilter();
            else if (addrBox.Focused) { RestoreAddress(); grid.Focus(); }
            else grid.Focus();
            return true;
        }
        if (RunCommandKey(keyData, KeyWhere.BrowserKeys)) return true;
        if (k == Keys.Escape && full) { ToggleFull(); return true; }
        return false;
    }

    // ================================================================ help and about

    // a line of the F1 page: the keys, then what they do (more lines after "\n", indented under the first)
    static string HelpLine(string keys, string text)
    {
        string first = keys.Length < 24 ? "  " + keys.PadRight(24) : "  " + keys + "   ";
        return first + text.Replace("\n", "\r\n" + new string(' ', 26));
    }

    // the keys of several commands, as F1 shows them: "Ctrl+A / Ctrl+I"
    string KeysJoined(string joiner, params string[] ids)
    {
        var parts = new List<string>();
        foreach (string id in ids) parts.Add(KeysOf(id));
        return string.Join(joiner, parts.ToArray());
    }

    string Keys2(params string[] ids) { return KeysJoined(" / ", ids); }

    // the keys of a row of numbered commands: "Alt+0 .. Alt+5"
    string KeysRange(string first, string last) { return KeysOf(first) + " .. " + KeysOf(last); }

    // F1: every shortcut on one page, followed by the About / credits text. The keys of every command come from the
    // command table (MainForm.Commands.cs), so the page always says what the keys really do.
    void ShowHelp()
    {
        string nl = "\r\n";
        var L = new List<string>();
        L.Add("RETROGLEEK - KEYS AND MOUSE");
        L.Add("");
        L.Add("EVERYWHERE");
        L.Add(HelpLine(KeysOf("help"), "this help"));
        L.Add(HelpLine("Alt", "the menu bar (File, Edit, View, Effects, Go, Tools, Settings, Help)"));
        L.Add(HelpLine(KeysOf("fullscreen"), "fullscreen"));
        L.Add(HelpLine(KeysOf("open"), "open a picture or folder"));
        L.Add(HelpLine("Drag & drop", "open a dropped file or folder"));
        L.Add("");
        L.Add("BROWSER (folder tree and thumbnails)");
        L.Add(HelpLine("Double-click / Enter", "open the picture in the viewer"));
        L.Add(HelpLine("Arrows, Home / End,", "move; Shift or Ctrl for multi-select"));
        L.Add("    PgUp / PgDn");
        L.Add(HelpLine("Drag with the mouse", "rubber-band select (Ctrl adds to the selection)"));
        L.Add(HelpLine(Keys2("select-all", "invert"), "select all / invert the selection"));
        L.Add(HelpLine("Mouse wheel", "scroll;  Ctrl + wheel or the Size slider: thumbnail size"));
        L.Add(HelpLine(KeysOf("filter"), "filter box (* and ? wildcards, spaces mean AND); Esc clears"));
        L.Add(HelpLine(KeysOf("preview"), "preview pane: a large preview and facts about the focused picture, right of the list"));
        L.Add(HelpLine(KeysOf("details"), "details list (name, type, platform, dimensions, frames, colours, size, date, status);\n" +
                                          "click a column title to sort by it, again to reverse"));
        L.Add(HelpLine("Tools > Analyse folder", "fills in frames, colours and duplicates in the details list"));
        L.Add(HelpLine(KeysOf("subfolders"), "Subfolders switch: also list pictures in all folders below this one\n" +
                                             "(turns off again when you open another folder or drive)"));
        L.Add(HelpLine(KeysOf("address"), "address bar: type or paste a local path, Enter"));
        L.Add(HelpLine(Keys2("back", "forward"), "back / forward"));
        L.Add(HelpLine(KeysOf("favourite"), "add the open folder to the favourites (or remove it); Go > Favourites, Recent folders"));
        L.Add(HelpLine(KeysJoined(" or ", "up", "up-back"), "up one folder"));
        L.Add(HelpLine(KeysOf("dual"), "dual pane: two folders side by side, one folder tree that follows the active pane\n" +
                                       "(outlined in blue; click a pane or press " + KeysOf("switch-pane") + " to switch). Drag pictures from one pane\n" +
                                       "to the other (same drive moves, another drive copies; Ctrl copies, Shift moves);\n" +
                                       "files dragged in from Explorer are copied (Shift moves them)"));
        L.Add(HelpLine(Keys2("copy-other", "move-other"), "dual pane: copy / move the selection to the other pane's folder\n" +
                                                          "(also the buttons right of the address bar: dual pane, swap the panes' folders,\n" +
                                                          "copy and move to the other pane, preview pane; greyed out until they can be used),\n" +
                                                          "then a button for each local drive: a click opens it in the active pane"));
        L.Add(HelpLine(KeysOf("undo"), "undo the last rename, batch rename or move (up to 20 steps)"));
        L.Add(HelpLine(KeysRange("rate-0", "rate-5"), "rating: 1 to 5 stars (" + KeysOf("rate-0") + " removes it); also in the viewer and slideshow"));
        L.Add(HelpLine(KeysRange("label-0", "label-5"), "colour label: red, yellow, green, blue, purple (" + KeysOf("label-0") + " removes it)"));
        L.Add(HelpLine(KeysOf("tag"), "tag / untag;  the Show pull-down lists only tagged, rated or labelled pictures\n" +
                                      "(marks are kept in marks.tsv next to settings.ini, never in the pictures)\n" +
                                      "ratings stored in photos by Windows / Lightroom show in silver (read only)"));
        L.Add(HelpLine("Edit > Rating > Write my ratings into the files", "copies your stars into JPEG / PNG files (asks first)"));
        L.Add(HelpLine(KeysOf("properties"), "properties: every EXIF, GPS, IPTC, XMP and PNG text detail of the picture (also in\n" +
                                             "the viewer); edit the title, description, keywords, author and copyright of JPEG and\n" +
                                             "PNG files (written into the file's XMP, checked before it replaces the old file)"));
        L.Add(HelpLine(KeysOf("rename"), "rename"));
        L.Add(HelpLine(KeysOf("batch-rename"), "batch rename the selection (pattern such as holiday_###, with a preview)"));
        L.Add(HelpLine(KeysOf("compare"), "compare the 2 selected pictures side by side (zoom and panning linked;\n" +
                                          "rotate / flip / grid / info apply to both; Esc goes back)"));
        L.Add(HelpLine("Tools > Contact sheet", "lossless PNG index sheets of the selection (100 per page, at most 2,000)"));
        L.Add(HelpLine(KeysOf("convert"), "convert the selection (or the picture in the viewer) to a retro format:\n" +
                                          "ZX Spectrum .scr, C64 Koala .koa / Doodle .dd, Amstrad CPC .scr (mode 0 / 1),\n" +
                                          "Atari ST Degas .pi1, Amiga IFF .iff, MSX .sc2 / .sc8;\n" +
                                          "new files only, each checked with RECOIL (also in the viewer)"));
        L.Add(HelpLine("Tools > Find duplicates", "identical files, the same picture in other files (also retro screenshots\n" +
                                                  "enlarged or with a border) and similar pictures, across folders; tag the extra\n" +
                                                  "copies or move them to the Recycle Bin (asks first; one of each group is kept)"));
        L.Add(HelpLine(KeysOf("audit"), "folder audit: an offline HTML report of every picture file in this folder\n" +
                                        "(platform, type, size, colours, duplicates, files that could not be opened)"));
        L.Add(HelpLine(Keys2("copy", "cut", "paste"), "copy / cut / paste files (cut + paste moves them; works with Explorer too)"));
        L.Add(HelpLine(Keys2("copy-to", "move-to"), "copy / move the selection to a folder (Edit > Copy to / Move to: the last 8)"));
        L.Add(HelpLine("Drag selected pictures", "onto a folder in the tree (or into Explorer): same drive moves, another\n" +
                                                 "drive copies; hold Ctrl to copy, Shift to move"));
        L.Add(HelpLine("Drag a folder in the tree", "onto another folder: moves / copies it the same way"));
        L.Add(HelpLine(KeysOf("delete"), "delete (to the Recycle Bin)"));
        L.Add(HelpLine(KeysOf("refresh"), "refresh (the folder tree and the open folder also refresh by themselves)"));
        L.Add(HelpLine(KeysOf("save-png"), "save the selection as lossless PNG"));
        L.Add(HelpLine(KeysOf("export-frames"), "save every frame of animated GIFs and WebPs / pages of TIFFs as separate PNGs"));
        L.Add(HelpLine(Keys2("turn-cw", "turn-ccw"), "turn the selected JPEG photos on disk, without loss (asks first; also in the viewer)"));
        L.Add(HelpLine(KeysOf("retro"), "show / hide retro-computer formats"));
        L.Add(HelpLine("Right-click", "menu: rating, sort, details on tiles, invert selection ..."));
        L.Add("");
        L.Add("VIEWER");
        L.Add(HelpLine(Keys2("prev-pic", "next-pic"), "previous / next picture (also PgUp / PgDn, Space / Backspace, the mouse wheel)"));
        L.Add(HelpLine(Keys2("first-pic", "last-pic"), "first / last picture"));
        L.Add(HelpLine(Keys2("zoom-in", "zoom-out"), "zoom (whole-number steps above 100%; also Ctrl + wheel)"));
        L.Add(HelpLine("2 .. 9", "200% .. 900%"));
        L.Add(HelpLine(Keys2("zoom-100", "zoom-fit"), "actual size / fit this picture;  double-click toggles"));
        L.Add(HelpLine(KeysOf("fit-lock"), "Fit-to-Screen on / off for every picture (remembered)"));
        L.Add(HelpLine("Click and drag", "pan"));
        L.Add(HelpLine(Keys2("rotate-cw", "rotate-ccw"), "rotate clockwise / counter-clockwise"));
        L.Add(HelpLine(Keys2("flip-h", "flip-v"), "flip left-right / top-bottom"));
        L.Add(HelpLine(KeysOf("checker"), "checkerboard behind transparent pictures"));
        L.Add(HelpLine(KeysOf("effects"), "screen effects off / on (compare with the picture as decoded)"));
        L.Add(HelpLine(KeysOf("before-after"), "before / after: the picture as decoded beside it with its screen effects\n" +
                                               "(retro; also from the browser with one picture selected; effect keys and\n" +
                                               "presets change the right side; " + KeysOf("before-after") + " or Esc closes)"));
        L.Add(HelpLine(KeysOf("retro-preview") + " on a photo", "retro preview: the picture beside it as it might look on an 8-bit computer\n" +
                                               "(ZX Spectrum, C64, Amstrad CPC, MSX), with the screen effects; display only.\n" +
                                               KeysOf("sim-computer") + " / Shift+" + KeysOf("sim-computer") + " computer, " +
                                               KeysOf("sim-dither") + " / Shift+" + KeysOf("sim-dither") + " dithering, " +
                                               KeysOf("sim-fill") + " whole picture or fill the screen\n" +
                                               "(also the bar above the right picture and the Effects menu); with Fill the screen,\n" +
                                               "drag the frame on the left picture to choose the part shown;\n" +
                                               "an animated GIF or WebP plays on both sides (" + KeysOf("anim-pause") + " pauses, " +
                                               KeysOf("prev-frame") + " and " + KeysOf("next-frame") + " step)"));
        L.Add(HelpLine(KeysOf("sim-save") + " (retro preview)", "save the computer's own file (.scr, .koa, .dd, .sc2), as previewed and without\n" +
                                               "screen effects, plus a PNG of the screen with its effects beside it, to share\n" +
                                               "(an animation: the frame shown; it pauses there)"));
        L.Add(HelpLine(KeysOf("sim-save-anim") + " (retro preview, animation)", "save the whole animation as a GIF (the converted\n" +
                                               "frames, no screen effects) and, if you like, every frame as a PNG with them"));
        L.Add(HelpLine(KeysOf("auto"), "automatic effects: the usual screen of each picture's computer (" + KeysOf("auto") + " again: your own)"));
        L.Add(HelpLine(KeysRange("preset-1", "preset-9"), "use a saved screen effects preset (Effects > Save these effects as a preset)"));
        L.Add(HelpLine(KeysOf("tv"), "True TV aspect: pixels shaped as on the original PAL / NTSC screen (retro)"));
        L.Add(HelpLine(KeysOf("scanlines"), "scanlines: off / light / medium / strong (retro, shown from about 300%)"));
        L.Add(HelpLine(KeysOf("signal"), "video signal: RGB / S-Video / composite (retro; composite blends dithering)"));
        L.Add(HelpLine("Click the badges", "above the info bar (retro pictures): cable, scanlines, phosphor mask, glow and\n" +
                                           "curvature; above them TV aspect, enlarged pixels and automatic effects (with\n" +
                                           "effects off: one badge to show them again); a click does what " +
                                           KeysJoined(", ", "signal", "scanlines", "mask", "glow", "curvature") + ",\n" +
                                           KeysJoined(", ", "tv", "pixels", "auto") + " or " + KeysOf("effects") + " does. " +
                                           "To their left, the computer badge (not clickable): the\n" +
                                           "maker, where it was designed (flags), the model, the year and the palette size\n" +
                                           "(the preview pane, " + KeysOf("preview") + ", shows the same badges for a retro picture, not clickable).\n" +
                                           "Above them: the colours the picture uses (\"11 of 16 colours used\"), with swatches"));
        L.Add(HelpLine(KeysOf("mask"), "phosphor mask: off / aperture grille / slot mask / dot mask (retro, from 200%)"));
        L.Add(HelpLine(KeysOf("glow"), "glow: off / soft / strong (bright areas light up their surroundings; retro)"));
        L.Add(HelpLine(KeysOf("curvature"), "screen curvature: off / slight / strong, darker rounded corners (retro, below 800%)"));
        L.Add(HelpLine(KeysOf("pixel-grid"), "pixel grid (at 800% and above)"));
        L.Add(HelpLine(KeysOf("filmstrip"), "filmstrip: thumbnails of the folder along the bottom; click one to show it"));
        L.Add(HelpLine(KeysOf("loupe") + " (hold)", "loupe: a magnifier around the pointer, 4 times the current size"));
        L.Add(HelpLine(KeysOf("pixels"), "crisp / sharp / smooth enlargement"));
        L.Add(HelpLine(KeysOf("info"), "info bar (x, y and colour under the pointer)"));
        L.Add(HelpLine(KeysOf("exif"), "EXIF details (camera, exposure, date ...)"));
        L.Add(HelpLine(KeysOf("anim-pause"), "pause / play an animated GIF or WebP"));
        L.Add(HelpLine(Keys2("prev-frame", "next-frame"), "previous / next frame of an animated GIF or WebP"));
        L.Add(HelpLine(KeysOf("slideshow"), "slideshow;  Up / Down change the interval"));
        L.Add(HelpLine(KeysRange("rate-0", "rate-5") + " / " + KeysRange("label-0", "label-5") + " / " + KeysOf("tag"),
                       "rate / colour-label / tag the picture (Settings: move on to the next one)"));
        L.Add(HelpLine(KeysOf("copy"), "copy the picture to the clipboard"));
        L.Add(HelpLine(KeysOf("save-png"), "save the picture as lossless PNG (first frame of an animation)"));
        L.Add(HelpLine(KeysOf("export-frames"), "save every frame / page of the picture as separate PNGs"));
        L.Add(HelpLine(KeysOf("delete"), "delete the picture (to the Recycle Bin)"));
        L.Add(HelpLine("Enter", "fullscreen"));
        L.Add(HelpLine("Esc", "stop the slideshow / leave fullscreen / back to the browser"));
        L.Add("");
        L.Add("");
        L.Add("ABOUT");
        L.Add("RetroGleek " + Program.Version + " - offline image browser and viewer");
        L.Add("Copyright (c) 2026 Mark Billson. Released under the MIT License.");
        L.Add("No network access and no telemetry. Files are only written when you save, copy, move, rename or delete,");
        L.Add("plus settings.ini next to RetroGleek.exe when it closes (delete it, or Settings > Reset, for the defaults).");
        L.Add("");
        L.Add("Retro-computer pictures are decoded by RECOIL " + Recoil.RECOIL.Version);
        L.Add(Recoil.RECOIL.Credits.Trim().Replace("\r\n", "\n").Replace("\n", nl));
        L.Add(Recoil.RECOIL.License.Replace("\r\n", "\n").Replace("\n", nl));
        string text = string.Join(nl, L.ToArray());
        using (var f = new Form())
        {
            f.AutoScaleMode = AutoScaleMode.None;
            f.Text = "RetroGleek - help";
            f.StartPosition = FormStartPosition.CenterParent;
            f.ShowInTaskbar = false; f.MinimizeBox = false;
            f.BackColor = Color.FromArgb(38, 38, 38);
            f.ClientSize = new Size(Math.Min(Util.S(820), Screen.FromControl(this).WorkingArea.Width * 9 / 10),
                                    Math.Min(Util.S(640), Screen.FromControl(this).WorkingArea.Height * 9 / 10));
            f.KeyPreview = true;
            f.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F1) f.Close(); };
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(32, 32, 32), ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 10f), Text = text
            };
            var close = new Button
            {
                Text = "Close", Dock = DockStyle.Bottom, Height = Util.S(32), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(52, 52, 52), ForeColor = Color.Gainsboro
            };
            close.Click += delegate { f.Close(); };
            f.Controls.Add(box);                               // fill first, edge-docked after
            f.Controls.Add(close);
            f.CancelButton = close;
            Util.Dark(box);
            f.Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; close.Focus(); };
            f.HandleCreated += delegate { try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch (Exception) { } };
            f.ShowDialog(this);
        }
    }

    // Help > Run the self-test (SelfTest.cs): in the background, in a temporary folder of its own; the status bar
    // shows each test, then a message box the result
    bool selfTesting;

    void RunSelfTest()
    {
        if (selfTesting) return;
        selfTesting = true;
        status.Text = "  Self-test: starting...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            var lines = new List<string>();
            int failed;
            try { failed = SelfTest.Run(delegate (string s) { Util.Post(this, delegate { if (!viewing) status.Text = "  Self-test: " + s; }); }, lines); }
            catch (Exception ex) { failed = 1; lines.Add("FAIL  " + ex.Message); }
            Util.Post(this, delegate
            {
                selfTesting = false;
                UpdateStatus();
                MessageBox.Show(this, SelfTest.Report(lines, failed), failed == 0 ? "RetroGleek self-test: passed" : "RetroGleek self-test: FAILED",
                                MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            });
        });
    }

    void ShowAbout()
    {
        MessageBox.Show(this,
            "RetroGleek " + Program.Version + " - offline image browser and viewer\n" +
            "Copyright (c) 2026 Mark Billson. Released under the MIT License.\n" +
            "No network access and no telemetry. Files are only written when you save, copy, move, rename or delete,\n" +
            "plus settings.ini next to RetroGleek.exe when it closes.\n\n" +
            "Retro-computer pictures are decoded by RECOIL " + Recoil.RECOIL.Version + "\n" +
            Recoil.RECOIL.Credits.Trim() + "\n" + Recoil.RECOIL.License + "\n\n" +
            "Press F1 for every key and mouse action.",
            "About RetroGleek", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
