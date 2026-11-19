// RetroGleek - the main window, continued: dual-pane mode (two folders side by side, one folder tree)
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.
//
// View > Dual pane (Ctrl+Shift+2) shows two folders side by side. Each pane has its own folder, filter, sort, Show
// choice, Subfolders switch, Details / Thumbnails view, thumbnail size, back / forward history and auto-refresh; the
// address bar, the filter bar and the folder tree are shared and follow the active pane (outlined in blue). Click a
// pane, or press Tab, to make it active. Everything that works on "the pictures" (menus, keys, marks, Properties,
// the preview pane, the viewer, compare) works on the active pane. In single-pane mode only the first pane is shown
// and RetroGleek behaves exactly as before.
//
// Moving pictures between the panes: drag them from one pane to the other (Explorer's rules: the same drive moves,
// another drive copies; hold Ctrl to copy, Shift to move), or Shift+F5 / Shift+F6 copy / move the selection to the
// other pane's folder. Files dragged in from Explorer are copied (hold Shift to move them). Nothing is ever
// overwritten, local drives only, and a move can be undone with Ctrl+Z: it is the same copy and move as everywhere.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

partial class MainForm
{
    // one browser pane: its grid and Details header, and what belongs to its folder
    sealed class Pane
    {
        public ThumbGrid Grid;
        public DetailHeader Header;
        public Panel Host;                                 // holds the grid; its top edge shows which pane is active
        public readonly List<string> Hist = new List<string>();
        public int HPos = -1;
        public string PendingNav;
        // auto-refresh: watches the pane's folder and rescans it shortly after something changes there. Uses
        // Windows' own change notifications for local drives; nothing goes online.
        public FileSystemWatcher Watcher;
        public string WatchedDir;
        public bool WatchedRecursive;
        public WinTimer RefreshTimer;
    }

    Pane paneA, paneB, act;
    SplitContainer split;
    bool dual, wantDual;
    double splitRatio = 0.5;
    bool settingSplit, userSplit;                          // userSplit: the bar is being dragged by hand

    IEnumerable<Pane> Panes { get { return new[] { paneA, paneB }; } }
    Pane Other(Pane p) { return p == paneA ? paneB : paneA; }

    void MarksChangedAll() { foreach (Pane p in Panes) p.Grid.MarksChanged(); }

    Pane MakePane()
    {
        var p = new Pane();
        ThumbGrid g = new ThumbGrid();
        p.Grid = g;
        p.RefreshTimer = new WinTimer { Interval = 700 };
        p.RefreshTimer.Tick += delegate { p.RefreshTimer.Stop(); AutoRefresh(p); };
        g.GotFocus += delegate { Activate(p); lastPane = g; };
        g.OpenRequested += delegate (int i) { Activate(p); OpenIndex(i); };
        g.ContextRequested += delegate (Point pt, int hit) { Activate(p); ShowGridMenu(pt, hit); };
        g.SelectionChanged += delegate { if (p == act) { UpdateStatus(); UpdatePreview(); } };
        g.AnalyseProgress += delegate (int done, int total)
        {
            if (p == act) status.Text = "  Analysing " + done + " of " + total + " pictures (colours, frames, duplicates)...";
        };
        g.AnalyseFinished += delegate (int n, int bad, int dups)
        {
            if (p != act) return;
            if (n < 0) { UpdateStatus(); return; }
            status.Text = "  Analysed " + n + (n == 1 ? " picture" : " pictures") + ": " + bad + " could not be opened, " +
                          dups + (dups == 1 ? " duplicate" : " duplicates") + "   (Details view: Frames, Colours and Status columns)";
        };
        g.Loaded += delegate (string dir, string err) { OnFolderLoaded(p, dir, err); };
        g.ListChanged += delegate { if (p == act) { UpdateStatus(); SyncCanvas(); SyncSortControls(); UpdatePreview(); } };
        g.TileStepChanged += delegate
        {
            if (p != act) return;
            suppressSize = true;
            try { sizeSlider.Value = g.TileStep; } finally { suppressSize = false; }
        };
        g.FilterCleared += delegate { if (p == act) { suppressFilter = true; filterBox.Text = ""; suppressFilter = false; } };
        g.AllowDrop = true;                                // dual pane: copy / move here; single pane: open the file
        g.DragEnter += delegate (object s, DragEventArgs e) { PaneDragOver(p, e); };
        g.DragOver += delegate (object s, DragEventArgs e) { PaneDragOver(p, e); };
        g.DragDrop += delegate (object s, DragEventArgs e) { PaneDrop(p, e); };
        p.Header = new DetailHeader(g) { Visible = false };
        p.Host = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24) };
        p.Host.Controls.Add(g);                            // fill first, edge-docked after
        p.Host.Controls.Add(p.Header);
        return p;
    }

    // the two panes side by side (the second one hidden in single-pane mode)
    void BuildPanes()
    {
        split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = Util.S(5),
            BackColor = Color.FromArgb(52, 52, 52), Panel2Collapsed = true, TabStop = false
        };
        split.Panel1.BackColor = split.Panel2.BackColor = Color.FromArgb(24, 24, 24);
        split.Panel1.Controls.Add(paneA.Host);
        split.Panel2.Controls.Add(paneB.Host);
        // Only a drag by hand changes the remembered position. The bar also moves by itself (the window is resized,
        // minimised or restored, or the second pane is shown), and those moves must not overwrite it.
        split.SplitterMoving += delegate { if (!settingSplit) userSplit = true; };
        split.SplitterMoved += delegate
        {
            if (!userSplit) return;
            userSplit = false;
            if (settingSplit || !dual || split.Width < Util.S(100)) return;
            splitRatio = Math.Max(0.15, Math.Min(0.85, (double)split.SplitterDistance / split.Width));
        };
        split.SizeChanged += delegate { ApplySplit(); };
        split.MouseUp += delegate { grid.Focus(); };       // the splitter must not keep the keyboard
        MarkActive();
    }

    void ApplySplit()
    {
        if (!dual || split.Width < Util.S(100)) return;
        settingSplit = true;
        try { split.SplitterDistance = Math.Max(Util.S(60), Math.Min(split.Width - Util.S(60), (int)(split.Width * splitRatio))); }
        catch (Exception) { }                             // too narrow just now: keep the old position
        finally { settingSplit = false; }
    }

    // the active pane gets a blue line along its top (dual-pane mode only)
    void MarkActive()
    {
        foreach (Pane p in Panes)
        {
            bool on = dual && p == act;
            p.Host.Padding = new Padding(0, dual ? Util.S(3) : 0, 0, 0);
            p.Host.BackColor = on ? Color.FromArgb(60, 130, 230) : Color.FromArgb(38, 38, 38);
        }
    }

    // View > Dual pane (Ctrl+Shift+2)
    void SetDual(bool on)
    {
        if (on == dual) return;
        dual = on;
        if (!on && act == paneB) Activate(paneA);
        settingSplit = true;
        try { split.Panel2Collapsed = !on; } finally { settingSplit = false; }
        if (on)
        {
            ApplySplit();
            BeginInvoke(new MethodInvoker(ApplySplit));     // once more after the layout has settled (at start-up)
            ThumbGrid b = paneB.Grid;
            if (b.Dir != null && Directory.Exists(b.Dir)) b.Rescan();      // as it was when dual pane was turned off
            else
            {
                string d = LastFolder("pane2Folder") ?? paneA.Grid.Dir;
                if (d != null) b.Load(d, null, -1);                       // its Loaded event starts its auto-refresh
            }
        }
        else Watch(paneB, null);                           // the hidden pane: no auto-refresh
        MarkActive();
        UpdatePaneButtons();
        if (!viewing && !comparing)
        {
            status.Text = on ? "  Dual pane: click a pane (or press " + KeysOf("switch-pane") + ") to make it active; drag pictures across, or " + Keys2("copy-other", "move-other") + " copy / move them to the other pane"
                             : "  Single pane";
            if (on) grid.Focus();
        }
    }

    // makes p the active pane: the address bar, filter bar, tree and status follow it
    void Activate(Pane p)
    {
        if (p == act || (!dual && p != paneA)) return;
        act = p;
        grid = p.Grid;
        suppressFilter = true;
        try { filterBox.Text = grid.Filter; } finally { suppressFilter = false; }
        if (showCombo.SelectedIndex != grid.ShowMode) showCombo.SelectedIndex = grid.ShowMode;   // sets the same mode again: no change
        SyncSortControls();
        suppressSize = true;
        try { sizeSlider.Value = grid.TileStep; } finally { suppressSize = false; }
        SetSubfolders(grid.Recursive, false);
        SetDetailsView(grid.DetailsMode);
        if (!addrBox.Focused) addrBox.Text = grid.Dir ?? "";
        UpdateNavButtons();
        MarkActive();
        if (grid.Dir != null) tree.Reveal(grid.Dir);
        if (!viewing && !comparing) { Text = (grid.Dir ?? "RetroGleek") + " - RetroGleek"; UpdateStatus(); }
        UpdatePreview();
        UpdatePaneButtons();
    }

    // Tab: the other pane
    void SwitchPane()
    {
        if (!dual) return;
        Pane o = Other(act);
        Activate(o);
        o.Grid.Focus();
    }

    // ---- auto-refresh, per pane

    void Watch(Pane p, string dir)
    {
        ThumbGrid g = p.Grid;
        if (dir != null && Util.SameDir(dir, p.WatchedDir) && p.Watcher != null && p.WatchedRecursive == g.Recursive) return;
        if (p.Watcher != null)
        {
            try { p.Watcher.EnableRaisingEvents = false; p.Watcher.Dispose(); } catch (Exception) { }
            p.Watcher = null;
        }
        p.WatchedDir = null;
        p.RefreshTimer.Stop();
        if (dir == null || (p == paneB && !dual)) return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = g.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
            };
            FileSystemEventHandler changed = delegate { Util.Post(this, delegate { FolderChanged(p); }); };
            w.Created += changed; w.Deleted += changed; w.Changed += changed;
            w.Renamed += delegate { Util.Post(this, delegate { FolderChanged(p); }); };
            w.Error += delegate { Util.Post(this, delegate { FolderChanged(p); }); };   // too many changes at once: just rescan
            w.EnableRaisingEvents = true;
            p.Watcher = w; p.WatchedDir = dir; p.WatchedRecursive = g.Recursive;
        }
        catch (Exception) { }                             // e.g. a CD, or a drive that gives no notifications: F5 still works
    }

    void FolderChanged(Pane p)                             // UI thread; waits until the changes settle
    {
        p.RefreshTimer.Interval = p.Grid.Recursive ? 2000 : 700;   // a whole tree of folders: wait a little longer
        p.RefreshTimer.Stop(); p.RefreshTimer.Start();
    }

    void AutoRefresh(Pane p)
    {
        ThumbGrid g = p.Grid;
        if (p.WatchedDir == null || !Util.SameDir(p.WatchedDir, g.Dir)) return;
        if (g.Scanning) { p.RefreshTimer.Start(); return; }   // a scan is running: try again a little later
        if (!Directory.Exists(p.WatchedDir)) { Watch(p, null); g.Rescan(); return; }
        g.Rescan();
    }

    // ---- dragging onto a pane

    // Dual pane: from the other pane (or the tree), Explorer's rules; from Explorer, copy (Shift: move). Never onto the
    // pane's own folder. Single pane: a dropped file opens, as before.
    DragDropEffects PaneEffect(Pane p, DragEventArgs e, out string[] srcs)
    {
        srcs = null;
        try { srcs = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null; }
        catch (Exception) { }
        string dest = p.Grid.Dir;
        if (srcs == null || srcs.Length == 0 || dest == null || p.Grid.DraggingOut || p.Grid.Scanning) return DragDropEffects.None;
        DragDropEffects eff = ChooseEffect(e, srcs, dest);            // None when they are there already, or a folder into itself
        if (eff == DragDropEffects.None || Other(p).Grid.DraggingOut || treeDragging) return eff;
        bool shift = (e.KeyState & 4) != 0, ctrl = (e.KeyState & 8) != 0;
        if (shift && !ctrl && (e.AllowedEffect & DragDropEffects.Move) != 0) return DragDropEffects.Move;
        return (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void PaneDragOver(Pane p, DragEventArgs e)
    {
        if (!dual) { OnDragEnterFiles(p.Grid, e); return; }
        string[] srcs;
        e.Effect = PaneEffect(p, e, out srcs);
    }

    void PaneDrop(Pane p, DragEventArgs e)
    {
        if (!dual) { OnDropFiles(p.Grid, e); return; }
        string[] arr;
        DragDropEffects eff = PaneEffect(p, e, out arr);
        if (eff == DragDropEffects.None) return;
        e.Effect = eff;
        var srcs = new List<string>(arr);
        string to = p.Grid.Dir;
        bool move = eff == DragDropEffects.Move;
        // run after the drop has finished, so the program the files came from (maybe Explorer) isn't kept waiting
        BeginInvoke(new MethodInvoker(delegate { if (move) MoveItems(srcs, to); else CopyItems(srcs, to); }));
    }

    // ---- Shift+F5 / Shift+F6: the selection to the other pane's folder

    bool CanSendToOther() { return dual && !viewing && !comparing && Other(act).Grid.Dir != null && grid.SelectedIndices().Count > 0; }

    void SendToOther(bool move)
    {
        if (!dual || viewing || comparing) return;
        string dest = Other(act).Grid.Dir;
        if (dest == null) { Warn("The other pane has no folder open."); return; }
        if (grid.Dir != null && Util.SameDir(grid.Dir, dest) && !grid.Recursive) { Warn("Both panes show the same folder."); return; }
        lastPane = grid;                                   // the selected pictures, not the folder in the tree
        TransferTo(move, dest);
    }

    // ---- the pane buttons, right of the address bar: dual / single pane, swap the panes' folders, copy and move to
    // the other pane. Each is greyed out until it can be used; the copy / move arrows point at the other pane.

    Button dualBtn, swapBtn, copyOtherBtn, moveOtherBtn, previewBtn;
    Panel paneTools, drivePanel;
    readonly List<Button> driveBtns = new List<Button>();

    // the right half of the address bar (the address box keeps the left half)
    void BuildPaneTools()
    {
        paneTools = new Panel { Dock = DockStyle.Right, BackColor = addrPanel.BackColor };
        drivePanel = new Panel { Dock = DockStyle.Fill, BackColor = addrPanel.BackColor };   // the drives, right of the pane buttons
        drivePanel.Resize += delegate { LayoutDrives(); };
        paneTools.Controls.Add(drivePanel);                // fill first, edge-docked after
        previewBtn = PaneButton("Preview pane: a large preview of the focused picture (" + KeysOf("preview") + ")", DrawPreviewIcon, delegate { SetPreview(!preview.Visible); grid.Focus(); });
        moveOtherBtn = PaneButton("Move the selection to the other pane's folder (" + KeysOf("move-other") + ")", DrawMoveIcon, delegate { SendToOther(true); grid.Focus(); });
        copyOtherBtn = PaneButton("Copy the selection to the other pane's folder (" + KeysOf("copy-other") + ")", DrawCopyIcon, delegate { SendToOther(false); grid.Focus(); });
        swapBtn = PaneButton("Swap the two panes' folders", DrawSwapIcon, delegate { SwapPanes(); grid.Focus(); });
        dualBtn = PaneButton("Dual pane: two folders side by side, or one (" + KeysOf("dual") + ")", DrawDualIcon, delegate { SetDual(!dual); grid.Focus(); });
        // docked from the left: the last added is leftmost
        paneTools.Controls.Add(Spacer(14));               // a wider gap before the drives
        paneTools.Controls.Add(previewBtn); paneTools.Controls.Add(Spacer(4));
        paneTools.Controls.Add(moveOtherBtn); paneTools.Controls.Add(Spacer(4));
        paneTools.Controls.Add(copyOtherBtn); paneTools.Controls.Add(Spacer(4));
        paneTools.Controls.Add(swapBtn); paneTools.Controls.Add(Spacer(4));
        paneTools.Controls.Add(dualBtn); paneTools.Controls.Add(Spacer(10));
        addrPanel.Controls.Add(paneTools);
        addrPanel.Resize += delegate { SizePaneTools(); };
        SizePaneTools();
        UpdatePaneButtons();
    }

    void SizePaneTools()
    {
        int need = Util.S(10 + 5 * 34 + 4 * 4 + 14);
        paneTools.Width = Math.Max(need, addrPanel.ClientSize.Width / 2);
    }

    Button PaneButton(string tooltip, Action<Graphics, Button, Color> draw, EventHandler click)
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
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            draw(e.Graphics, b, b.Enabled ? Color.Gainsboro : Color.FromArgb(100, 100, 100));
        };
        navTip.SetToolTip(b, tooltip);
        return b;
    }

    static Pen IconPen(Color c) { var p = new Pen(c, Math.Max(1.4f, Util.K * 1.6f)); p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round; return p; }

    // two side-by-side panes (filled blue behind them when dual pane is on: the button's own colour)
    static void DrawDualIcon(Graphics g, Button b, Color c)
    {
        float u = Math.Min(b.ClientSize.Width, b.ClientSize.Height) / 16f, cx = b.ClientSize.Width / 2f, cy = b.ClientSize.Height / 2f;
        using (Pen p = IconPen(c))
        {
            g.DrawRectangle(p, cx - 7 * u, cy - 5 * u, 14 * u, 10 * u);
            g.DrawLine(p, cx, cy - 5 * u, cx, cy + 5 * u);
        }
    }

    // arrows both ways
    static void DrawSwapIcon(Graphics g, Button b, Color c)
    {
        float u = Math.Min(b.ClientSize.Width, b.ClientSize.Height) / 16f, cx = b.ClientSize.Width / 2f, cy = b.ClientSize.Height / 2f;
        using (Pen p = IconPen(c))
        {
            g.DrawLine(p, cx - 6 * u, cy - 2.5f * u, cx + 6 * u, cy - 2.5f * u);
            g.DrawLines(p, new[] { new PointF(cx + 3 * u, cy - 5.5f * u), new PointF(cx + 6 * u, cy - 2.5f * u), new PointF(cx + 3 * u, cy + 0.5f * u) });
            g.DrawLine(p, cx + 6 * u, cy + 2.5f * u, cx - 6 * u, cy + 2.5f * u);
            g.DrawLines(p, new[] { new PointF(cx - 3 * u, cy - 0.5f * u), new PointF(cx - 6 * u, cy + 2.5f * u), new PointF(cx - 3 * u, cy + 5.5f * u) });
        }
    }

    // a picture at the active pane's side, an arrow, and the picture at the other side (dashed where it was, for move)
    void DrawTransferIcon(Graphics g, Button b, Color c, bool move)
    {
        float u = Math.Min(b.ClientSize.Width, b.ClientSize.Height) / 16f, cx = b.ClientSize.Width / 2f, cy = b.ClientSize.Height / 2f;
        float dir = act == paneB ? -1f : 1f;                   // towards the other pane
        float sx = cx - dir * 5 * u, tx = cx + dir * 5 * u, s = 2.6f * u;
        using (Pen p = IconPen(c))
        {
            if (move) p.DashStyle = DashStyle.Dot;
            g.DrawRectangle(p, sx - s, cy - s, 2 * s, 2 * s);
            p.DashStyle = DashStyle.Solid;
            g.DrawRectangle(p, tx - s, cy - s, 2 * s, 2 * s);
            float a0 = sx + dir * (s + 1.2f * u), a1 = tx - dir * (s + 1.2f * u);
            g.DrawLine(p, a0, cy, a1, cy);
            g.DrawLines(p, new[] { new PointF(a1 - dir * 1.6f * u, cy - 1.6f * u), new PointF(a1, cy), new PointF(a1 - dir * 1.6f * u, cy + 1.6f * u) });
        }
    }

    void DrawCopyIcon(Graphics g, Button b, Color c) { DrawTransferIcon(g, b, c, false); }
    void DrawMoveIcon(Graphics g, Button b, Color c) { DrawTransferIcon(g, b, c, true); }

    void UpdatePaneButtons()
    {
        if (dualBtn == null) return;
        bool pv = preview != null && preview.Visible;
        previewBtn.BackColor = pv ? Color.FromArgb(60, 130, 230) : Color.FromArgb(52, 52, 52);
        previewBtn.FlatAppearance.MouseOverBackColor = pv ? Color.FromArgb(80, 150, 245) : Color.FromArgb(72, 72, 72);
        string here = grid != null && grid.Dir != null ? Path.GetPathRoot(grid.Dir) : null;
        foreach (Button db in driveBtns)                      // the drive of the active pane's folder stands out
        {
            bool on = here != null && string.Equals(((DriveSpot)db.Tag).Root, here, StringComparison.OrdinalIgnoreCase);
            Color want = on ? Color.FromArgb(70, 70, 70) : Color.FromArgb(52, 52, 52);
            if (db.BackColor != want) db.BackColor = want;
        }
        dualBtn.BackColor = dual ? Color.FromArgb(60, 130, 230) : Color.FromArgb(52, 52, 52);
        dualBtn.FlatAppearance.MouseOverBackColor = dual ? Color.FromArgb(80, 150, 245) : Color.FromArgb(72, 72, 72);
        swapBtn.Enabled = CanSwap();
        bool send = CanSendToOther();
        copyOtherBtn.Enabled = moveOtherBtn.Enabled = send;
        copyOtherBtn.Invalidate(); moveOtherBtn.Invalidate();   // the arrows follow the active pane
    }

    // ---- swapping the two panes' folders

    bool CanSwap()
    {
        return dual && !viewing && !comparing && paneA.Grid.Dir != null && paneB.Grid.Dir != null && !Util.SameDir(paneA.Grid.Dir, paneB.Grid.Dir);
    }

    // each pane opens the other's folder (filters and the Subfolders switch are reset, as for any new folder)
    void SwapPanes()
    {
        if (!CanSwap()) return;
        string a = paneA.Grid.Dir, b = paneB.Grid.Dir;
        foreach (Pane p in Panes) p.Grid.Recursive = false;
        SetSubfolders(false, false);
        paneA.Grid.Load(b, null, -1);
        paneB.Grid.Load(a, null, -1);
        tree.Reveal(act == paneA ? b : a);
    }

    // ---- the drive buttons: one per local drive that is ready (fixed, removable, optical with a disc). Network
    // drives are left out, as in the tree, and so are empty card-reader slots and empty optical drives. Read in the
    // background (asking a card reader whether it is ready can be slow), at start-up and whenever Windows says a drive
    // was plugged in or out. A click opens the drive in the active pane. More drives than fit: the tree has them all.

    sealed class DriveSpot { public string Root, Label, Tip; public DriveType Kind; }

    void RefreshDriveButtons()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            var found = new List<DriveSpot>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    DriveType t = d.DriveType;
                    if (t != DriveType.Fixed && t != DriveType.Removable && t != DriveType.CDRom) continue;
                    try
                    {
                        if (!d.IsReady) continue;
                        string label = "";
                        try { label = d.VolumeLabel; } catch (Exception) { }
                        string name = (label.Length > 0 ? label : t == DriveType.Removable ? "Removable Disk" : t == DriveType.CDRom ? "CD/DVD Drive" : "Local Disk")
                                      + " (" + d.Name.TrimEnd('\\') + ")";
                        string space = "";
                        try { space = "\n" + Util.FmtSize(d.AvailableFreeSpace) + " free of " + Util.FmtSize(d.TotalSize); } catch (Exception) { }
                        found.Add(new DriveSpot { Root = d.Name, Label = d.Name.TrimEnd('\\'), Kind = t, Tip = name + space + "\nClick to open it in the active pane" });
                    }
                    catch (Exception) { }                     // removed while we asked
                }
            }
            catch (Exception) { return; }
            Util.Post(this, delegate { SetDriveButtons(found); });
        });
    }

    void SetDriveButtons(List<DriveSpot> found)
    {
        if (drivePanel == null) return;
        drivePanel.SuspendLayout();
        try
        {
            foreach (Button b in driveBtns) { drivePanel.Controls.Remove(b); b.Dispose(); }
            driveBtns.Clear();
            foreach (DriveSpot d in found)
            {
                DriveSpot spot = d;
                var b = new Button
                {
                    Width = Util.S(46), TabStop = false, FlatStyle = FlatStyle.Flat, Tag = spot,
                    BackColor = Color.FromArgb(52, 52, 52), ForeColor = Color.Gainsboro
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 80);
                b.Click += delegate { OpenDrive(spot.Root); };
                b.Paint += delegate (object sender, PaintEventArgs e) { DrawDriveButton(e.Graphics, b, spot); };
                navTip.SetToolTip(b, spot.Tip);
                driveBtns.Add(b);
                drivePanel.Controls.Add(b);
            }
        }
        finally { drivePanel.ResumeLayout(false); }
        LayoutDrives();
        UpdatePaneButtons();
    }

    // side by side from the left; those that don't fit are hidden
    void LayoutDrives()
    {
        if (drivePanel == null) return;
        int x = 0, gap = Util.S(4), h = drivePanel.ClientSize.Height;
        foreach (Button b in driveBtns)
        {
            b.SetBounds(x, 0, b.Width, h);
            b.Visible = x + b.Width <= drivePanel.ClientSize.Width;
            x += b.Width + gap;
        }
    }

    void OpenDrive(string root)
    {
        if (viewing || comparing) return;
        if (!Directory.Exists(root)) { Warn(root.TrimEnd('\\') + " isn't available right now."); RefreshDriveButtons(); return; }
        ShowFolder(root, null, -1, false);
        tree.Reveal(root);
        grid.Focus();
    }

    // a small drive icon (internal disk, USB stick, disc) and the letter
    static void DrawDriveButton(Graphics g, Button b, DriveSpot d)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = b.ClientSize.Height, u = Math.Min(b.ClientSize.Width, h) / 16f, ix = Util.S(6), cy = h / 2f;
        using (Pen p = IconPen(Color.FromArgb(170, 176, 186)))
        {
            if (d.Kind == DriveType.CDRom)
            {
                g.DrawEllipse(p, ix, cy - 4.5f * u, 9 * u, 9 * u);
                g.DrawEllipse(p, ix + 3.3f * u, cy - 1.2f * u, 2.4f * u, 2.4f * u);
            }
            else if (d.Kind == DriveType.Removable)            // a USB stick: body and plug
            {
                g.DrawRectangle(p, ix + 1.5f * u, cy - 1.5f * u, 6 * u, 6 * u);
                g.DrawRectangle(p, ix + 3 * u, cy - 5 * u, 3 * u, 3.5f * u);
            }
            else
            {
                g.DrawRectangle(p, ix, cy - 3 * u, 9 * u, 6 * u);
                using (var led = new SolidBrush(Color.FromArgb(80, 200, 100))) g.FillRectangle(led, ix + 6 * u, cy + 0.5f * u, 1.6f * u, 1.2f * u);
            }
        }
        TextRenderer.DrawText(g, d.Label, b.Font, new Rectangle((int)(ix + 10 * u), 0, b.ClientSize.Width - (int)(ix + 10 * u), b.ClientSize.Height), Color.Gainsboro,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    // a frame with a picture-like square on its right part
    static void DrawPreviewIcon(Graphics g, Button b, Color c)
    {
        float u = Math.Min(b.ClientSize.Width, b.ClientSize.Height) / 16f, cx = b.ClientSize.Width / 2f, cy = b.ClientSize.Height / 2f;
        using (Pen p = IconPen(c))
        {
            g.DrawRectangle(p, cx - 7 * u, cy - 5 * u, 14 * u, 10 * u);
            g.DrawLine(p, cx + 1 * u, cy - 5 * u, cx + 1 * u, cy + 5 * u);
            using (var br = new SolidBrush(c)) g.FillRectangle(br, cx + 2.6f * u, cy - 2.4f * u, 3 * u, 3 * u);
        }
    }
}
