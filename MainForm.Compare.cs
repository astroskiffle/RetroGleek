// RetroGleek - the main window, continued: starting and ending side-by-side compare, before / after and the retro
// preview (the view itself is CompareView.cs)
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

// ====================================================================== main window (continued)

partial class MainForm
{
    // ================================================================ the compare view

    CompareView cmp;

    // what the rest of the main window asks about it
    bool comparing { get { return cmp.Active; } }              // compare, before / after or the retro preview is shown
    bool beforeAfter { get { return cmp.BeforeAfter; } }       // before / after or the retro preview
    bool baSim { get { return cmp.RetroPreview; } }            // the retro preview
    RetroSim simOpts { get { return cmp.Options; } }           // the retro preview's choices (remembered in settings.ini)
    bool simSaving { get { return cmp.Saving; } }

    void BuildCompare()
    {
        cmp = new CompareView(canvas, KeysOf);
        cmp.CloseRequested = EndCompare;
        cmp.FullscreenRequested = ToggleFull;
        cmp.IsFullscreen = () => full;
        cmp.TitleChanged = t => Text = t;
        cmp.Warn = Warn;
        cmp.FolderWritten = FilesChanged;                  // one of the retro preview's files was saved there
        foreach (Control c in new Control[] { cmp.LeftView, cmp.RightView })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnterFiles;
            c.DragDrop += delegate { };                    // dropping files while comparing does nothing
        }
    }

    // the compare view instead of the browser and the viewer
    void ShowCompareView()
    {
        if (viewing) SetViewing(false);
        SuspendLayout();
        browse.Visible = false; status.Visible = false; canvas.Visible = false;
        cmp.Visible = true;
        ResumeLayout(true);
    }

    void SimSet(int machine, int dither, bool fill) { cmp.SetSim(machine, dither, fill); }
    void SimSave() { cmp.SaveSim(); }
    void SimSaveAnim() { cmp.SaveSimAnim(); }

    // ================================================================ side-by-side compare

    bool CanCompare() { return !viewing && !comparing && grid.SelectedIndices().Count == 2; }

    void StartCompare()
    {
        if (!CanCompare()) return;
        List<int> sel = grid.SelectedIndices();
        ComparePaths(grid.Items[sel[0]].Path, grid.Items[sel[1]].Path);
    }

    // two pictures side by side (from the browser's selection, or from Find duplicates)
    void ComparePaths(string a, string b)
    {
        if (comparing) EndCompare();
        ShowCompareView();
        cmp.ShowPair(a, b);
        Text = "Compare: " + Path.GetFileName(a) + "  |  " + Path.GetFileName(b) + " - RetroGleek";
    }

    // ends compare, before / after or the retro preview
    void EndCompare()
    {
        if (beforeAfter) { EndBeforeAfter(); return; }
        if (!comparing) return;
        cmp.Close();
        SetViewing(false);
    }

    // keys while the compare view is shown
    bool CompareKey(Keys keyData) { return cmp.HandleKey(keyData); }

    // ================================================================ before / after (Shift+O) and the retro preview

    static bool RetroFile(string p) { return p != null && !Util.IsNative(p) && Retro.Claims(p); }

    // before / after: a retro picture
    bool CanBeforeAfter()
    {
        if (comparing) return false;
        if (viewing) return canvas.Platform != null;
        List<int> sel = grid.SelectedIndices();
        return sel.Count == 1 && !grid.Items[sel[0]].Offline && RetroFile(grid.Items[sel[0]].Path);
    }

    // retro preview: a modern picture (photo, PNG, WebP ...)
    bool CanRetroPreview()
    {
        if (comparing) return false;
        if (viewing) return canvas.HasPicture && canvas.Platform == null;
        List<int> sel = grid.SelectedIndices();
        return sel.Count == 1 && !grid.Items[sel[0]].Offline && !RetroFile(grid.Items[sel[0]].Path);
    }

    // Shift+O: before / after for a retro picture, the retro preview for any other
    void StartBeforeAfter()
    {
        if (comparing) return;
        string path;
        bool simulate;
        if (viewing)
        {
            if (!canvas.HasPicture) return;
            path = canvas.CurrentPath;
            simulate = canvas.Platform == null;
        }
        else
        {
            List<int> sel = grid.SelectedIndices();
            if (sel.Count != 1 || grid.Items[sel[0]].Offline)
            {
                status.Text = "  " + KeysOf("before-after") + ": select one picture (before / after for a retro picture, the 8-bit retro preview for a photo), or press it in the viewer";
                return;
            }
            path = grid.Items[sel[0]].Path;
            simulate = !RetroFile(path);
        }
        if (path == null) return;
        bool fromViewer = viewing;
        var list = new List<string>(grid.Paths);           // a copy: a refresh of the folder must not change it under us
        int at = list.FindIndex(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        if (at < 0) { list = new List<string> { path }; at = 0; }
        SuspendLayout();
        browse.Visible = false; status.Visible = false; canvas.Visible = false;
        cmp.Visible = true;
        ResumeLayout(true);
        cmp.ShowBeforeAfter(list, at, simulate, fromViewer);
    }

    // back to where before / after started: the viewer (on the picture shown last), or the browser with it selected
    void EndBeforeAfter()
    {
        if (!beforeAfter) return;
        string shown = cmp.ShownPath;
        bool fromViewer = cmp.FromViewer;
        cmp.Close();                                       // what you set on the right side becomes the viewer's own
        int i = shown == null ? -1 : grid.IndexOfPath(shown, -1);
        if (fromViewer && shown != null && File.Exists(shown))
        {
            if (i >= 0) canvas.Open(grid.Paths, i); else canvas.ShowSingle(shown);
            SetViewing(true);
            Text = Path.GetFileName(shown) + " - RetroGleek";
        }
        else
        {
            SetViewing(false);
            if (i >= 0) grid.SelectOnly(i);
        }
    }
}
