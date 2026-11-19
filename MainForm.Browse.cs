// RetroGleek - the main window, continued: browsing <-> viewing, address bar, history, filter, sort, marks, effect presets
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
    // ================================================================ browsing <-> viewing

    void ShowFolder(string dir, string select, int hint, bool force)
    {
        if (dir == null) return;
        if (!force && select != null && !grid.Scanning && Util.SameDir(dir, grid.Dir))
        {
            int i = grid.IndexOfPath(select, -1);
            if (i >= 0) { grid.SelectOnly(i); SyncCanvas(); return; }
        }
        if (grid.Recursive && !Util.SameDir(dir, grid.Dir)) SetSubfolders(false, false);   // another folder or drive: switch off
        grid.Load(dir, select, hint);
        UpdateStatus();
    }

    void OnFolderLoaded(Pane p, string dir, string err)
    {
        RecordHistory(p, dir);
        Watch(p, err == null ? dir : null);
        UpdatePaneButtons();
        if (p != act) return;                               // the other pane: nothing else to update
        UpdateStatus();
        if (!viewing) Text = dir + " - RetroGleek";
        if (historyClearedAt != null && !Util.SameDir(dir, historyClearedAt)) historyClearedAt = null;   // a new folder: history starts again
        if (err == null && historyClearedAt == null) RememberRecentFolder(dir);
        if (!addrBox.Focused) addrBox.Text = dir;
        UpdateNavButtons();
        SyncCanvas();
    }

    // the Subfolders switch: applies to the folder that is open, and turns itself off when another folder or drive
    // is opened (ShowFolder). A refresh of the same folder keeps it on.
    void ToggleSubfolders() { SetSubfolders(!grid.Recursive, true); }

    void SetSubfolders(bool on, bool rescan)
    {
        grid.Recursive = on;
        subBtn.BackColor = on ? Color.FromArgb(60, 130, 230) : Color.FromArgb(52, 52, 52);
        subBtn.ForeColor = on ? Color.White : Color.Gainsboro;
        if (rescan && grid.Dir != null) { grid.Rescan(); UpdateStatus(); }
    }

    // Alt+P: the preview pane to the right of the thumbnails (remembered in settings.ini)
    void SetPreview(bool on)
    {
        SuspendLayout();
        preview.Visible = on;
        previewSplit.Visible = on;
        ResumeLayout(true);
        UpdatePreview();
        UpdatePaneButtons();
    }

    void UpdatePreview()
    {
        if (preview == null || preview.IsDisposed || !preview.Visible) return;
        int f = grid.FocusIndex;
        List<int> sel = grid.SelectedIndices();
        Item it = f >= 0 && f < grid.Items.Count ? grid.Items[f] : sel.Count > 0 ? grid.Items[sel[0]] : null;
        string folder = null;
        if (it != null && grid.Recursive && grid.Dir != null)
        {
            string d = Path.GetDirectoryName(it.Path);
            folder = Util.SameDir(d, grid.Dir) ? "(this folder)" : d.Substring(Math.Min(d.Length, grid.Dir.TrimEnd('\\').Length + 1));
        }
        int others = it != null && sel.Count > 1 ? sel.Count - (it.Sel ? 1 : 0) : 0;
        preview.SetEffects(canvas.ManualEffects, canvas.AutoEffects, canvas.EffectsOff, canvas.PixMode);   // for its badges
        preview.ShowItem(it, grid.MarkOf(it), folder, others);
    }

    // thumbnails <-> details list (remembered in settings.ini)
    void SetDetailsView(bool on)
    {
        grid.DetailsMode = on;
        detailHeader.Visible = on;
        viewBtn.BackColor = on ? Color.FromArgb(60, 130, 230) : Color.FromArgb(52, 52, 52);
        viewBtn.ForeColor = on ? Color.White : Color.Gainsboro;
        viewBtn.Text = on ? "Thumbnails" : "Details";
        viewBtn.Width = Util.S(on ? 92 : 72);
    }

    // hand the viewer the (sorted) list of the folder once the grid has scanned it
    void SyncCanvas()
    {
        string w = canvas.Want;
        if (w == null || grid.Dir == null) return;
        if (grid.Covers(Path.GetDirectoryName(w)) && grid.IndexOfPath(w, -1) >= 0) canvas.SetList(grid.Paths, w);
    }

    void OpenImage(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (grid.FilterActive) { filterBox.Text = ""; SetShow(0); }   // opening something explicitly shouldn't be hidden by a filter
            if (Directory.Exists(path))
            {
                SetViewing(false);
                ShowFolder(path, null, -1, false);
                tree.Reveal(path);
                return;
            }
            if (!File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            if (!Util.IsNative(path) && Retro.Claims(path)) Retro.Enabled = true;   // you opened a retro picture: show its neighbours too
            canvas.ShowSingle(path);                       // show it immediately, before the folder scan finishes
            SetViewing(true);
            ShowFolder(dir, path, -1, false);              // grid + tree follow in the background
            tree.Reveal(dir);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "RetroGleek"); }
    }

    void OpenIndex(int i)
    {
        if (i < 0 || i >= grid.Paths.Count) return;
        canvas.Open(grid.Paths, i);
        SetViewing(true);
    }

    void OnImageChanged(string path, int index)
    {
        if (viewing) Text = Path.GetFileName(path) + " - RetroGleek";
        if (grid.Dir != null && grid.Covers(Path.GetDirectoryName(path)))
        {
            int i = grid.IndexOfPath(path, index);
            if (i >= 0) grid.SelectOnly(i);                // keep the browser's selection in step with the viewer
        }
    }

    void SetViewing(bool v)
    {
        viewing = v;
        SuspendLayout();
        if (v) { canvas.Visible = true; browse.Visible = false; status.Visible = false; }
        else { browse.Visible = true; status.Visible = true; canvas.Visible = false; }
        ResumeLayout(true);
        if (v) canvas.Focus();
        else { grid.Focus(); lastPane = grid; Text = (grid.Dir ?? "RetroGleek") + " - RetroGleek"; UpdateStatus(); UpdatePreview(); }
    }

    void UpdateStatus()
    {
        UpdatePaneButtons();
        if (grid.Scanning) { status.Text = grid.Recursive ? "  Searching subfolders..." : "  Loading folder..."; return; }
        if (grid.Error != null) { status.Text = "  " + grid.Error; return; }
        List<int> sel = grid.SelectedIndices();
        long bytes = 0;
        foreach (int i in sel) bytes += grid.Items[i].Size;
        int shown = grid.Items.Count, total = grid.TotalCount;
        string s = "  " + (grid.FilterActive ? shown + " of " + total : total.ToString()) + (total == 1 ? " image" : " images");
        if (grid.Recursive) s += grid.Truncated ? " (including subfolders; stopped at " + ThumbGrid.MaxRecursive + ")" : " (including subfolders)";
        if (grid.FilterActive) s += grid.ShowMode != 0 ? " (showing: " + ThumbGrid.ShowNames[grid.ShowMode] + ")" : " (filtered)";
        int tagged = grid.CountTagged();
        if (tagged > 0) s += "   |   " + tagged + " tagged";
        if (sel.Count > 0) s += "   |   " + sel.Count + " selected (" + Util.FmtSize(bytes) + ")";
        if (grid.SortKey != 0 || grid.SortDesc) s += "   |   sorted by " + grid.SortLabel;
        if (sel.Count == 1)
        {
            string why = grid.FailureOf(sel[0]);
            if (why != null) s += "   |   no thumbnail: " + why;
        }
        status.Text = s + "   |   " + grid.Dir;
    }

    // ================================================================ address bar, history, filter, sort

    // dir: 0 = left, 1 = right, 2 = up. The arrow is drawn here, so it never depends on a font having arrow characters,
    // and a disabled button is visibly dimmed.
    Button NavButton(int dir, string tooltip, EventHandler click)
    {
        var b = new Button
        {
            Dock = DockStyle.Left, Width = Util.S(34), TabStop = false, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 52, 52), ForeColor = Color.Gainsboro
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 72, 72);
        b.Click += click;
        b.EnabledChanged += delegate { b.Invalidate(); };
        b.Paint += delegate (object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = b.ClientSize.Width / 2f, cy = b.ClientSize.Height / 2f, r = Math.Min(b.ClientSize.Width, b.ClientSize.Height) * 0.27f;
            float dx = dir == 0 ? -1f : dir == 1 ? 1f : 0f, dy = dir == 2 ? -1f : 0f;          // direction the arrow points
            PointF tip = new PointF(cx + dx * r, cy + dy * r), tail = new PointF(cx - dx * r, cy - dy * r);
            PointF h1 = new PointF(tip.X - dx * r * 0.85f - dy * r * 0.85f, tip.Y - dy * r * 0.85f + dx * r * 0.85f);
            PointF h2 = new PointF(tip.X - dx * r * 0.85f + dy * r * 0.85f, tip.Y - dy * r * 0.85f - dx * r * 0.85f);
            using (var pen = new Pen(b.Enabled ? Color.Gainsboro : Color.FromArgb(100, 100, 100), Math.Max(1.6f, Util.K * 2f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                g.DrawLine(pen, tail, tip);
                g.DrawLines(pen, new[] { h1, tip, h2 });
            }
        };
        navTip.SetToolTip(b, tooltip);
        return b;
    }

    static Label Spacer(int w) { return new Label { Dock = DockStyle.Left, Width = Util.S(w) }; }

    static string ParentDir(string d)
    {
        if (string.IsNullOrEmpty(d)) return null;
        string t = d.TrimEnd('\\', '/');
        return t.Length == 0 ? null : Path.GetDirectoryName(t);       // null for a drive root
    }

    void UpdateNavButtons()
    {
        btnBack.Enabled = hpos > 0;
        btnFwd.Enabled = hpos >= 0 && hpos < hist.Count - 1;
        btnUp.Enabled = ParentDir(grid.Dir) != null;
    }

    // every folder that finishes loading is remembered, except when we got there by going back / forward
    void RecordHistory(Pane p, string dir)
    {
        if (p.PendingNav != null && Util.SameDir(p.PendingNav, dir)) { p.PendingNav = null; return; }
        if (p.HPos >= 0 && Util.SameDir(p.Hist[p.HPos], dir)) return;
        if (p.HPos < p.Hist.Count - 1) p.Hist.RemoveRange(p.HPos + 1, p.Hist.Count - p.HPos - 1);
        p.Hist.Add(dir);
        if (p.Hist.Count > 100) p.Hist.RemoveAt(0);
        p.HPos = p.Hist.Count - 1;
    }

    void GoHistory(int delta)
    {
        int n = hpos + delta;
        if (n < 0 || n >= hist.Count) return;
        hpos = n;
        pendingNav = hist[n];
        ShowFolder(hist[n], null, -1, false);
        tree.Reveal(hist[n]);
        UpdateNavButtons();
    }

    void GoUp()
    {
        string p = ParentDir(grid.Dir);
        if (p == null) return;
        ShowFolder(p, null, -1, false);
        tree.Reveal(p);
    }

    void FocusAddress() { addrBox.Focus(); addrBox.SelectAll(); }

    void RestoreAddress() { addrBox.Text = grid.Dir ?? ""; }

    // Local folders and files only: network paths are never opened from here.
    void CommitAddress()
    {
        string t = Environment.ExpandEnvironmentVariables(addrBox.Text.Trim().Trim('"'));
        if (t.Length == 0) { RestoreAddress(); return; }
        try
        {
            if (t.StartsWith("\\\\") || t.StartsWith("//")) { Warn("Only local drives are supported; network paths are not opened."); RestoreAddress(); return; }
            string full = Path.GetFullPath(t);
            DriveType dt = new DriveInfo(Path.GetPathRoot(full)).DriveType;
            if (dt != DriveType.Fixed && dt != DriveType.Removable && dt != DriveType.CDRom) { Warn("Only local drives are supported."); RestoreAddress(); return; }
            if (Directory.Exists(full)) { ShowFolder(full, null, -1, false); tree.Reveal(full); grid.Focus(); }
            else if (File.Exists(full)) { OpenImage(full); }
            else { Warn("That folder doesn't exist:\n" + full); RestoreAddress(); }
        }
        catch (Exception ex) { Warn(ex.Message); RestoreAddress(); }
    }

    void AddrKeyDown(object s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitAddress(); }
        else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; addrBox.SelectAll(); }
    }

    void FilterKeyDown(object s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; grid.Focus(); }
        else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; filterBox.SelectAll(); }
    }

    // the filter box is always visible: Ctrl+F jumps to it, Esc clears it
    void ShowFilter()
    {
        filterBox.Focus();
        filterBox.SelectAll();
    }

    void CloseFilter()
    {
        filterBox.Text = "";                  // clears the filter through TextChanged
        grid.Focus();
    }

    // keep the sort pull-down and direction arrow in step with changes made elsewhere (right-click menu, reverse button)
    void SyncSortControls()
    {
        suppressSort = true;
        try { if (sortCombo.SelectedIndex != grid.SortKey) sortCombo.SelectedIndex = grid.SortKey; }
        finally { suppressSort = false; }
        sortDirBtn.Invalidate();
    }

    // choosing the current sort again reverses it; date and size start with newest / largest first
    void ChooseSort(int key)
    {
        bool desc = key == grid.SortKey ? !grid.SortDesc : (key == 1 || key == 2 || key == 5 || key == 9);
        grid.SetSort(key, desc);
    }

    void ToggleRetro()
    {
        Retro.Enabled = !Retro.Enabled;
        ReloadAll();
    }

    // ---------------------------------------------------------------- Settings > Clear history

    // Clear history wipes the traces of where you have been: the recent folders, the folder RetroGleek opens at, the
    // recent Copy to / Move to folders and the back / forward list. The folder open now is not remembered either,
    // until you open another one (historyClearedAt). Favourites, marks, presets and other settings stay.
    string historyClearedAt;

    void ClearHistory()
    {
        if (MessageBox.Show(this,
                "Clear the history?\n\n" +
                "This forgets the recent folders, the folder RetroGleek opens at next time, the recent Copy to / Move to folders " +
                "and the back / forward list, and saves that straight away.\n\n" +
                "Your favourite folders, ratings, colour labels and tags, presets and other settings stay. No pictures or folders are touched.",
                "Clear history", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        recentFolders.Clear();
        recentDest.Clear();
        foreach (Pane pn in Panes)                         // both panes' back / forward lists
        {
            pn.Hist.Clear(); pn.HPos = -1; pn.PendingNav = null;
            if (pn.Grid.Dir != null) { pn.Hist.Add(pn.Grid.Dir); pn.HPos = 0; }
        }
        historyClearedAt = grid.Dir ?? "";
        UpdateNavButtons();
        StoreFavourites();                                 // writes the (now empty) recent folders too
        StoreRecentDest();
        Prefs.Set("lastFolder", (string)null);
        Prefs.Save();
        status.Text = "  History cleared (recent folders, last folder, recent Copy to / Move to folders, back / forward)";
    }

    // ================================================================ marks: ratings, colour labels, tag

    // what a mark applies to in the browser: the selected pictures, or the focused one
    List<string> MarkTargets()
    {
        var paths = new List<string>();
        foreach (int i in grid.SelectedIndices()) if (!grid.Items[i].Offline) paths.Add(grid.Items[i].Path);
        int f = grid.FocusIndex;
        if (paths.Count == 0 && f >= 0 && f < grid.Items.Count && !grid.Items[f].Offline) paths.Add(grid.Items[f].Path);
        return paths;
    }

    // stars / label: -1 = leave as it is. toggleTag: tag them all, or untag them all if they all were tagged.
    void DoMark(int stars, int label, bool toggleTag)
    {
        var paths = new List<string>();
        if (viewing) { string p = canvas.CurrentPath; if (p != null && File.Exists(p)) paths.Add(p); }
        else paths = MarkTargets();
        if (paths.Count == 0) return;
        bool? tag = null;
        if (toggleTag)
        {
            bool allTagged = true;
            foreach (string p in paths) { Mark m = Marks.ForFile(p); if (m == null || !m.Tag) { allTagged = false; break; } }
            tag = !allTagged;
        }
        string focusBefore = !viewing && grid.FocusIndex >= 0 && grid.FocusIndex < grid.Items.Count ? grid.Items[grid.FocusIndex].Path : null;
        Marks.Set(paths, stars, label, tag);
        string what = toggleTag ? (tag.Value ? "Tagged" : "Tag removed")
                    : stars >= 0 ? (stars == 0 ? "Rating removed" : "Rating: " + Marks.StarText(stars))
                    : label == 0 ? "Colour label removed" : "Colour label: " + Marks.LabelNames[label];
        canvas.MarksChanged();
        MarksChangedAll();
        UpdatePreview();
        if (viewing)
        {
            canvas.ShowMessage(what);
            if (autoAdvance) canvas.Next();
        }
        else
        {
            // auto-advance; if the Show filter just took the picture out of the list, the next one is already in its place
            bool stillThere = focusBefore != null && grid.FocusIndex >= 0 && grid.FocusIndex < grid.Items.Count
                              && string.Equals(grid.Items[grid.FocusIndex].Path, focusBefore, StringComparison.OrdinalIgnoreCase);
            if (autoAdvance && paths.Count == 1 && stillThere) grid.SelectNext();
            UpdateStatus();
            status.Text = "  " + what + (paths.Count > 1 ? " (" + paths.Count + " pictures)" : "") + "   |   " + status.Text.Trim();
        }
    }

    void SetShow(int mode)
    {
        if (showCombo.SelectedIndex != mode) showCombo.SelectedIndex = mode;     // its handler sets grid.ShowMode
        else grid.ShowMode = mode;
        UpdateStatus();
    }

    void ClearTags()
    {
        var paths = new List<string>();
        foreach (Item it in grid.Items) { Mark m = grid.MarkOf(it); if (m != null && m.Tag) paths.Add(it.Path); }
        if (paths.Count == 0) return;
        if (MessageBox.Show(this, "Remove the tag from the " + paths.Count + (paths.Count == 1 ? " tagged picture" : " tagged pictures") + " shown here?\n\n(Ratings and colour labels stay.)",
                            "Clear tags", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Marks.Set(paths, -1, -1, false);
        MarksChangedAll();
        canvas.MarksChanged();
        UpdatePreview();
        UpdateStatus();
    }

    // ================================================================ screen effect presets (Effects menu, Ctrl+1 .. Ctrl+9)

    const int MaxPresets = 9;

    readonly EffectSet[] presets = new EffectSet[MaxPresets];

    void LoadPresets()
    {
        for (int i = 0; i < MaxPresets; i++) presets[i] = EffectSet.Parse(Prefs.Get("preset" + (i + 1), null));
    }

    void StorePresets()
    {
        for (int i = 0; i < MaxPresets; i++) Prefs.Set("preset" + (i + 1), presets[i] == null ? null : presets[i].Serialize());
    }

    int PresetCount()
    {
        int n = 0;
        foreach (EffectSet p in presets) if (p != null) n++;
        return n;
    }

    // the viewer(s) showing pictures right now: both halves while comparing
    // (before / after: only the right side; the left one always shows the picture as decoded)
    IEnumerable<Canvas> EffectTargets() { return comparing ? cmp.EffectTargets : new[] { canvas }; }

    void ApplyPreset(int i)
    {
        if (i < 0 || i >= MaxPresets) return;
        EffectSet p = presets[i];
        if (p == null)
        {
            if (viewing || comparing) foreach (Canvas c in EffectTargets()) c.ShowMessage("No preset " + (i + 1) + " yet (Effects > Save these effects as a preset)");
            return;
        }
        canvas.UseEffects(p);
        if (comparing) cmp.UsePreset(p);
        foreach (Canvas c in EffectTargets()) c.ShowMessage("Preset " + (i + 1) + ": " + p.Name + "  (" + p.Describe() + ")");
        if (!viewing && !comparing) status.Text = "  Screen effects preset " + (i + 1) + ": " + p.Name + "  (" + p.Describe() + ")";
    }

    void SavePreset()
    {
        Canvas src = comparing ? cmp.PresetSource : canvas;
        EffectSet e = src.CurrentEffects;
        EffectSet a = src.AutoChoice;
        int free = Array.IndexOf(presets, null);
        string suggest = a != null && src.Platform != null ? src.Platform : "Preset " + ((free >= 0 ? free : 0) + 1);   // automatic: the computer's name
        string name = Prompt.Ask(this, "Save screen effects preset", "Name for " + e.Describe() + ":", suggest, false);
        if (name == null) return;
        name = name.Replace("|", "/").Trim();
        if (name.Length == 0) return;
        int slot = -1;
        for (int i = 0; i < MaxPresets; i++)
            if (presets[i] != null && string.Equals(presets[i].Name, name, StringComparison.OrdinalIgnoreCase)) slot = i;
        if (slot >= 0)
        {
            if (MessageBox.Show(this, "Replace the preset \"" + presets[slot].Name + "\" (" + presets[slot].Describe() + ")?", "Save preset",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        else if (free < 0) { Warn("All " + MaxPresets + " presets are in use. Delete one first (Effects > Delete a preset)."); return; }
        else slot = free;
        e.Name = name;
        presets[slot] = e;
        StorePresets();
        Prefs.Save();                                      // kept straight away, not only when RetroGleek closes
        string msg = "Saved preset " + (slot + 1) + ": " + name + "  (" + KeysOf("preset-" + (slot + 1)) + " uses it)";
        foreach (Canvas c in EffectTargets()) c.ShowMessage(msg);
        if (!viewing && !comparing) status.Text = "  " + msg;
    }

    void DeletePreset(int i)
    {
        if (i < 0 || i >= MaxPresets || presets[i] == null) return;
        if (MessageBox.Show(this, "Delete the preset \"" + presets[i].Name + "\"?", "Delete preset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        presets[i] = null;
        StorePresets();
        Prefs.Save();
    }
}
