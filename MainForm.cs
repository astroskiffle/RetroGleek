// RetroGleek - the main window: layout, settings (settings.ini) and window chrome
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

// ====================================================================== main window

sealed partial class MainForm : Form
{
    // ================================================================ fields and layout


    Panel browse;

    Splitter splitter;

    Panel right, addrPanel, filterPanel;

    TextBox addrBox, filterBox;

    Button btnBack, btnFwd, btnUp, sortDirBtn;

    DarkCombo sortCombo, showCombo;

    bool autoAdvance;                                      // after marking a picture, move to the next one

    DarkSlider sizeSlider;

    ToolStripMenuItem gDetails, gSortDesc;

    readonly ToolStripMenuItem[] gSort = new ToolStripMenuItem[4];

    readonly ToolTip navTip = new ToolTip();

    // back / forward history: the active pane's own (MainForm.Dual.cs)
    List<string> hist { get { return act.Hist; } }

    int hpos { get { return act.HPos; } set { act.HPos = value; } }

    string pendingNav { get { return act.PendingNav; } set { act.PendingNav = value; } }

    bool suppressFilter, suppressSort, suppressSize;

    FolderTree tree;

    ThumbGrid grid;                                        // the active pane's grid (the only one in single-pane mode)

    Canvas canvas;

    Label status;



    ContextMenuStrip gridMenu, treeMenu;

    ToolStripMenuItem gInvert, gCut, tCut;

    Button subBtn, viewBtn;

    DetailHeader detailHeader { get { return act.Header; } }

    PreviewPane preview;                                   // Alt+P: large preview of the focused picture (off by default)

    Splitter previewSplit;

    ToolStripMenuItem gBatch, gFrames, gCompare;

    ToolStripMenuItem gOpen, gRename, gCopy, gPaste, gSave, gDelete, tRename, tCopy, tPaste, tDelete, tNew, gRetro, tRetro;




    string startArg;


    bool viewing, full;

    Control lastPane;

    FormBorderStyle oldStyle; FormWindowState oldState; Rectangle oldBounds;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    public MainForm(string arg)
    {
        startArg = arg;
        BuildCommands();                                   // every command and its keys (MainForm.Commands.cs)
        AutoScaleMode = AutoScaleMode.None;
        Text = "RetroGleek";
        BackColor = Color.FromArgb(24, 24, 24);
        MinimumSize = new Size(Util.S(560), Util.S(360));
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(wa.Width * 8 / 10, wa.Height * 8 / 10);
        StartPosition = FormStartPosition.CenterScreen;

        tree = new FolderTree();                           // FolderTree.cs: lazy loading, watchers, drives
        tree.FolderChosen = folder => ShowFolder(folder, null, -1, false);
        tree.ShownFolder = () => grid.Dir;
        tree.ShownFolderGone = folder => ShowFolder(folder, null, -1, true);
        tree.NodeMouseClick += OnTreeClick;
        tree.GotFocus += delegate { lastPane = tree; };

        paneA = MakePane();                                // the browser's thumbnails; a second pane for dual-pane mode
        paneB = MakePane();
        act = paneA;
        grid = paneA.Grid;
        lastPane = grid;

        canvas = new Canvas();
        canvas.ImageChanged += OnImageChanged;

        // browse pane: [tree | splitter | grid]; the fill control is added first, edge-docked ones after
        browse = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24) };
        splitter = new Splitter
        {
            Dock = DockStyle.Left, Width = Util.S(5), MinSize = Util.S(140), MinExtra = Util.S(200),
            BackColor = Color.FromArgb(52, 52, 52)
        };
        // address bar (back / forward / up + path) across the top of the browser; filter bar above the thumbnails
        Color panelBg = Color.FromArgb(38, 38, 38), boxBg = Color.FromArgb(52, 52, 52);
        addrBox = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = boxBg, ForeColor = Color.White, Font = new Font("Segoe UI", 10f) };
        addrBox.KeyDown += AddrKeyDown;
        btnBack = NavButton(0, "Back (" + KeysOf("back") + ")", delegate { GoHistory(-1); grid.Focus(); });
        btnFwd = NavButton(1, "Forward (" + KeysOf("forward") + ")", delegate { GoHistory(1); grid.Focus(); });
        btnUp = NavButton(2, "Up one folder (" + KeysJoined(" or ", "up", "up-back") + ")", delegate { GoUp(); grid.Focus(); });
        addrPanel = new Panel { Dock = DockStyle.Top, Height = Util.S(38), BackColor = panelBg, Padding = new Padding(Util.S(6), Util.S(6), Util.S(8), Util.S(6)) };
        addrPanel.Controls.Add(addrBox);
        addrPanel.Controls.Add(Spacer(6)); addrPanel.Controls.Add(btnUp);
        addrPanel.Controls.Add(Spacer(4)); addrPanel.Controls.Add(btnFwd);
        addrPanel.Controls.Add(Spacer(4)); addrPanel.Controls.Add(btnBack);
        BuildPaneTools();                          // the right half: dual pane, swap, copy / move to the other pane

        filterBox = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = boxBg, ForeColor = Color.White, Font = new Font("Segoe UI", 10f) };
        filterBox.TextChanged += delegate { if (!suppressFilter) grid.SetFilter(filterBox.Text); };
        filterBox.KeyDown += FilterKeyDown;
        navTip.SetToolTip(filterBox, "Filter by name: part of a name, or * and ? wildcards; spaces mean \"and\".  " + KeysOf("filter") + " jumps here, Esc clears.");
        var fLab = new Label { Text = "Filter:", Dock = DockStyle.Left, Width = Util.S(50), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver, Font = new Font("Segoe UI", 9.5f) };

        // sort pull-down, direction button and thumbnail-size slider, to the right of the filter box
        int barH = Util.S(28);
        Font barFont = new Font("Segoe UI", 9.5f);
        var sortLab = new Label { Text = "Sort:", Left = 0, Top = 0, Width = Util.S(40), Height = barH, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver, Font = barFont };
        sortCombo = new DarkCombo { Left = Util.S(40), Top = Math.Max(0, (barH - Util.S(26)) / 2), Width = Util.S(136), Height = Util.S(26), BackColor = boxBg, ForeColor = Color.White, Font = barFont };
        sortCombo.SetItems("Name", "Date modified", "Size", "Type", "Platform", "Dimensions", "Frames", "Colours", "Status", "Rating", "Label");
        sortCombo.SelectedIndex = 0;
        sortCombo.SelectedIndexChanged += delegate
        {
            int key = sortCombo.SelectedIndex;
            if (!suppressSort && key >= 0 && key != grid.SortKey) grid.SetSort(key, key == 1 || key == 2 || key == 5 || key == 6 || key == 7 || key == 9);   // newest / largest / most stars first
        };
        sortCombo.DropDownClosed += delegate { grid.Focus(); };
        navTip.SetToolTip(sortCombo, "Sort the pictures by name, date, size, type, platform, dimensions, frames, colours, status, rating or label");
        sortDirBtn = new Button { Left = Util.S(180), Top = Math.Max(0, (barH - Util.S(26)) / 2), Width = Util.S(30), Height = Util.S(26), TabStop = false, FlatStyle = FlatStyle.Flat, BackColor = boxBg };
        sortDirBtn.FlatAppearance.BorderSize = 0;
        sortDirBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 72, 72);
        sortDirBtn.Click += delegate { grid.SetSort(grid.SortKey, !grid.SortDesc); grid.Focus(); };
        sortDirBtn.Paint += delegate (object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = sortDirBtn.ClientSize.Width / 2f, cy = sortDirBtn.ClientSize.Height / 2f;
            float r = Math.Min(sortDirBtn.ClientSize.Width, sortDirBtn.ClientSize.Height) * 0.22f;
            PointF[] tri = grid.SortDesc
                ? new[] { new PointF(cx - r, cy - r * 0.6f), new PointF(cx + r, cy - r * 0.6f), new PointF(cx, cy + r * 0.8f) }     // pointing down
                : new[] { new PointF(cx - r, cy + r * 0.6f), new PointF(cx + r, cy + r * 0.6f), new PointF(cx, cy - r * 0.8f) };   // pointing up
            using (var br = new SolidBrush(Color.Gainsboro)) g.FillPolygon(br, tri);
        };
        navTip.SetToolTip(sortDirBtn, "Sort direction (click to reverse)");
        var sizeLab = new Label { Text = "Size:", Left = Util.S(222), Top = 0, Width = Util.S(38), Height = barH, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver, Font = barFont };
        sizeSlider = new DarkSlider
        {
            Left = Util.S(262), Top = 0, Width = Util.S(150), Height = barH,
            Minimum = 0, Maximum = ThumbGrid.TileStepMax, Value = grid.TileStep, BackColor = panelBg
        };
        sizeSlider.ValueChanged += delegate { if (!suppressSize) grid.TileStep = sizeSlider.Value; };
        sizeSlider.MouseUp += delegate { grid.Focus(); };
        navTip.SetToolTip(sizeSlider, "Thumbnail size (Ctrl + mouse wheel also works)");
        var tools = new Panel { Dock = DockStyle.Right, Width = Util.S(416), BackColor = panelBg };
        tools.Controls.Add(sortLab); tools.Controls.Add(sortCombo); tools.Controls.Add(sortDirBtn);
        tools.Controls.Add(sizeLab); tools.Controls.Add(sizeSlider);

        filterPanel = new Panel { Dock = DockStyle.Top, Height = Util.S(38), BackColor = panelBg, Padding = new Padding(Util.S(8), Util.S(5), Util.S(8), Util.S(5)) };
        subBtn = new Button
        {
            Text = "Subfolders", Dock = DockStyle.Right, Width = Util.S(92), TabStop = false, FlatStyle = FlatStyle.Flat,
            BackColor = boxBg, ForeColor = Color.Gainsboro, Font = barFont
        };
        subBtn.FlatAppearance.BorderSize = 0;
        subBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 72, 72);
        subBtn.Click += delegate { ToggleSubfolders(); grid.Focus(); };
        navTip.SetToolTip(subBtn, "Also list the pictures in all folders below this one (" + KeysOf("subfolders") + ")");
        viewBtn = new Button
        {
            Text = "Details", Dock = DockStyle.Right, Width = Util.S(72), TabStop = false, FlatStyle = FlatStyle.Flat,
            BackColor = boxBg, ForeColor = Color.Gainsboro, Font = barFont
        };
        viewBtn.FlatAppearance.BorderSize = 0;
        viewBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 72, 72);
        viewBtn.Click += delegate { SetDetailsView(!grid.DetailsMode); grid.Focus(); };
        navTip.SetToolTip(viewBtn, "Switch between thumbnails and a details list (" + KeysOf("details") + ")");

        filterPanel.Controls.Add(filterBox);      // fill first, edge-docked after
        filterPanel.Controls.Add(subBtn);
        filterPanel.Controls.Add(new Label { Dock = DockStyle.Right, Width = Util.S(6) });
        filterPanel.Controls.Add(viewBtn);
        filterPanel.Controls.Add(new Label { Dock = DockStyle.Right, Width = Util.S(8) });
        filterPanel.Controls.Add(tools);

        // Show pull-down (marks): all pictures, or only tagged / rated / colour-labelled ones
        var showLab = new Label { Text = "Show:", Left = Util.S(6), Top = 0, Width = Util.S(44), Height = barH, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver, Font = barFont };
        showCombo = new DarkCombo { Left = Util.S(50), Top = Math.Max(0, (barH - Util.S(26)) / 2), Width = Util.S(140), Height = Util.S(26), BackColor = boxBg, ForeColor = Color.White, Font = barFont };
        showCombo.SetItems(ThumbGrid.ShowNames);
        showCombo.SelectedIndex = 0;
        showCombo.SelectedIndexChanged += delegate { if (showCombo.SelectedIndex >= 0) grid.ShowMode = showCombo.SelectedIndex; };
        showCombo.DropDownClosed += delegate { grid.Focus(); };
        navTip.SetToolTip(showCombo, "Show all pictures, or only tagged, rated or colour-labelled ones (" + KeysRange("rate-0", "rate-5") + " rates, " + KeysRange("label-0", "label-5") + " labels, " + KeysOf("tag") + " tags)");
        var showPanel = new Panel { Dock = DockStyle.Right, Width = Util.S(196), BackColor = panelBg };
        showPanel.Controls.Add(showLab); showPanel.Controls.Add(showCombo);
        filterPanel.Controls.Add(showPanel);
        filterPanel.Controls.Add(fLab);

        right = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24) };
        BuildPanes();
        right.Controls.Add(split);                // fill first, edge-docked after: the pane(s), each with its Details header
        preview = new PreviewPane { Dock = DockStyle.Right, Width = Util.S(320), Visible = false };
        previewSplit = new Splitter
        {
            Dock = DockStyle.Right, Width = Util.S(5), MinSize = Util.S(180), MinExtra = Util.S(240),
            BackColor = Color.FromArgb(52, 52, 52), Visible = false
        };
        right.Controls.Add(previewSplit);         // docks after the pane, so it sits on the pane's left edge
        right.Controls.Add(preview);              // beside the grid and its header, below the filter bar
        right.Controls.Add(filterPanel);
        browse.Controls.Add(right);
        browse.Controls.Add(splitter);
        browse.Controls.Add(tree);
        browse.Controls.Add(addrPanel);           // added last: docks first, so it spans the full width

        status = new Label
        {
            Dock = DockStyle.Bottom, Height = Util.S(24), TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(38, 38, 38), ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9f)
        };

        // fill-docked controls first, edge-docked ones after
        Controls.Add(browse);
        Controls.Add(canvas);
        BuildCompare();
        Controls.Add(cmp);
        Controls.Add(status);
        BuildMenuBar();
        Controls.Add(menuBar);                    // added last: docks first, so it spans the whole window
        MainMenuStrip = menuBar;

        BuildMenus();
        tree.BuildRoots();

        driveTimer.Tick += delegate { driveTimer.Stop(); RefreshDrives(); };

        AllowDrop = true;
        foreach (Control c in new Control[] { this, canvas })         // dropping a file here opens it (the panes: MakePane)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnterFiles;
            c.DragDrop += OnDropFiles;
        }
        tree.AllowDrop = true;                                        // dropping onto a folder copies / moves into it
        tree.DragEnter += OnTreeDragOver;
        tree.DragOver += OnTreeDragOver;
        tree.DragLeave += delegate { SetDropNode(null); };
        tree.DragDrop += OnTreeDrop;
        tree.ItemDrag += OnTreeItemDrag;                              // folders can be dragged too
        ApplyPrefs();
    }

    // ================================================================ remembered settings (settings.ini)

    void ApplyPrefs()
    {
        try
        {
            string[] wb = Prefs.Get("window", "").Split(',');
            int x, y, w, h;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var ns = System.Globalization.NumberStyles.Integer;
            if (wb.Length == 4 && int.TryParse(wb[0], ns, inv, out x) && int.TryParse(wb[1], ns, inv, out y) &&
                int.TryParse(wb[2], ns, inv, out w) && int.TryParse(wb[3], ns, inv, out h) &&
                w >= MinimumSize.Width && h >= MinimumSize.Height)
            {
                var r = new Rectangle(x, y, w, h);
                bool onScreen = false;                                // the screen it was on may be gone
                foreach (Screen s in Screen.AllScreens)
                {
                    Rectangle i = Rectangle.Intersect(s.WorkingArea, r);
                    if (i.Width >= Util.S(120) && i.Height >= Util.S(60)) onScreen = true;
                }
                if (onScreen) { StartPosition = FormStartPosition.Manual; Bounds = r; }
            }
            if (Prefs.Bool("maximized", false)) WindowState = FormWindowState.Maximized;
            tree.Width = Util.S(Prefs.Int("treeWidth", 280, 140, 2000));
            Marks.Load();                                          // ratings, colour labels and tags (marks.tsv)
            int sk = Prefs.Int("sort", 0, 0, 10);
            bool needsAnalysis = sk >= 6 && sk <= 8;
            grid.SetSort(needsAnalysis ? 0 : sk, needsAnalysis ? false : Prefs.Bool("sortDesc", false));   // analysis sorts need an analysis first
            grid.GridStep = Prefs.Int("thumbSize", grid.GridStep, 0, ThumbGrid.TileStepMax);
            grid.RowStep = Prefs.Int("rowSize", grid.RowStep, 0, ThumbGrid.TileStepMax);
            grid.ShowDetails = Prefs.Bool("details", grid.ShowDetails);
            SetDetailsView(Prefs.Bool("detailsView", false));
            // the second pane: its own remembered view (like the first pane's, the first time)
            ThumbGrid gb = paneB.Grid;
            int sk2 = Prefs.Int("pane2Sort", grid.SortKey, 0, 10);
            bool needs2 = sk2 >= 6 && sk2 <= 8;
            gb.SetSort(needs2 ? 0 : sk2, needs2 ? false : Prefs.Bool("pane2SortDesc", grid.SortDesc));
            gb.GridStep = Prefs.Int("pane2ThumbSize", grid.GridStep, 0, ThumbGrid.TileStepMax);
            gb.RowStep = Prefs.Int("pane2RowSize", grid.RowStep, 0, ThumbGrid.TileStepMax);
            gb.ShowDetails = Prefs.Bool("pane2Details", grid.ShowDetails);
            gb.DetailsMode = Prefs.Bool("pane2DetailsView", grid.DetailsMode);
            paneB.Header.Visible = gb.DetailsMode;            // (SetDetailsView works on the active pane)
            wantDual = Prefs.Bool("dualPane", false);
            splitRatio = Prefs.Int("dualSplit", 500, 150, 850) / 1000.0;
            canvas.FitLock = Prefs.Bool("fitToScreen", false);
            canvas.Checker = Prefs.Bool("checkerboard", false);
            canvas.PixMode = Prefs.Int("enlargedPixels", 0, 0, 3);
            canvas.InfoOn = Prefs.Bool("infoBar", true);
            canvas.ExifOn = Prefs.Bool("exifPanel", false);
            canvas.SlideIndex = Prefs.Int("slideshowStep", 2, 0, 99);
            canvas.TvAspect = Prefs.Bool("tvAspect", false);
            canvas.ScanMode = Prefs.Int("scanlines", 0, 0, 3);
            canvas.MaskMode = Prefs.Int("mask", 0, 0, 3);
            canvas.SignalMode = Prefs.Int("signal", 0, 0, 2);
            canvas.GlowMode = Prefs.Int("glow", 0, 0, 2);
            canvas.CurveMode = Prefs.Int("curvature", 0, 0, 2);
            canvas.AutoEffects = Prefs.Bool("autoEffects", false);
            simOpts.Machine = Prefs.Int("retroPreviewComputer", 0, 0, RetroConvert.PreviewFormats.Length - 1);
            simOpts.Dither = Prefs.Int("retroPreviewDither", 1, 0, RetroConvert.DitherNames.Length - 1);
            simOpts.Fill = Prefs.Bool("retroPreviewFill", false);
            simOpts.Amount = Prefs.Int("retroPreviewAmount", 25, 0, 100);
            simOpts.Pattern = Prefs.Int("retroPreviewPattern", 0, 0, 1);
            simOpts.Adjust = PictureAdjust.Clamped(Prefs.Int("retroPreviewBrightness", 0, -100, 100), Prefs.Int("retroPreviewContrast", 0, -100, 100),
                                                   Prefs.Int("retroPreviewSaturation", 0, -100, 100), Prefs.Int("retroPreviewHue", 0, -180, 180));
            autoAdvance = Prefs.Bool("autoAdvance", false);
            canvas.Filmstrip = Prefs.Bool("filmstrip", false);
            preview.Width = Util.S(Prefs.Int("previewWidth", 320, 180, 2000));
            SetPreview(Prefs.Bool("previewPane", false));
            LoadPresets();
            LoadRecentDest();
            LoadFavourites();
        }
        catch (Exception) { }                                     // a damaged file just means defaults
    }

    // the last folder, if it is still there (local drives only)
    static string LastFolder() { return LastFolder("lastFolder"); }

    static string LastFolder(string key)
    {
        string p = Prefs.Get(key, null);
        try
        {
            if (string.IsNullOrEmpty(p) || p.StartsWith("\\\\")) return null;
            string root = Path.GetPathRoot(p);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network) return null;
            return Directory.Exists(p) ? p : null;
        }
        catch (Exception) { return null; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        try { SavePrefs(); } catch (Exception) { }
    }

    void SavePrefs()
    {
        cmp.KeepEffectsNow();                              // closing while in before / after keeps what you set there
        Rectangle b; bool max;
        if (full) { b = oldBounds; max = oldState == FormWindowState.Maximized; }
        else { b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds; max = WindowState == FormWindowState.Maximized; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Prefs.Set("window", b.X.ToString(inv) + "," + b.Y.ToString(inv) + "," + b.Width.ToString(inv) + "," + b.Height.ToString(inv));
        Prefs.Set("maximized", max);
        Prefs.Set("treeWidth", (int)Math.Round(tree.Width / Util.K));
        // each pane's own view, whichever one is active now (the first pane under the old names)
        ThumbGrid ga = paneA.Grid, gb = paneB.Grid;
        Prefs.Set("sort", ga.SortKey);
        Prefs.Set("sortDesc", ga.SortDesc);
        Prefs.Set("thumbSize", ga.GridStep);
        Prefs.Set("rowSize", ga.RowStep);
        Prefs.Set("details", ga.ShowDetails);
        Prefs.Set("detailsView", ga.DetailsMode);
        Prefs.Set("pane2Sort", gb.SortKey);
        Prefs.Set("pane2SortDesc", gb.SortDesc);
        Prefs.Set("pane2ThumbSize", gb.GridStep);
        Prefs.Set("pane2RowSize", gb.RowStep);
        Prefs.Set("pane2Details", gb.ShowDetails);
        Prefs.Set("pane2DetailsView", gb.DetailsMode);
        if (!Program.RetroFromArgs) Prefs.Set("retro", Retro.Enabled);
        Prefs.Set("fitToScreen", canvas.FitLock);
        Prefs.Set("checkerboard", canvas.Checker);
        Prefs.Set("enlargedPixels", canvas.PixMode);
        Prefs.Set("infoBar", canvas.InfoOn);
        Prefs.Set("exifPanel", canvas.ExifOn);
        Prefs.Set("slideshowStep", canvas.SlideIndex);
        EffectSet own = canvas.ManualEffects;              // your own settings, also while automatic effects are on
        Prefs.Set("tvAspect", own.Tv);
        Prefs.Set("scanlines", own.Scan);
        Prefs.Set("mask", own.Mask);
        Prefs.Set("signal", own.Signal);
        Prefs.Set("glow", own.Glow);
        Prefs.Set("curvature", own.Curve);
        Prefs.Set("autoEffects", canvas.AutoEffects);
        Prefs.Set("retroPreviewComputer", simOpts.Machine);
        Prefs.Set("retroPreviewDither", simOpts.Dither);
        Prefs.Set("retroPreviewFill", simOpts.Fill);
        Prefs.Set("retroPreviewAmount", simOpts.Amount);
        Prefs.Set("retroPreviewPattern", simOpts.Pattern);
        Prefs.Set("retroPreviewBrightness", simOpts.Adjust.Brightness);
        Prefs.Set("retroPreviewContrast", simOpts.Adjust.Contrast);
        Prefs.Set("retroPreviewSaturation", simOpts.Adjust.Saturation);
        Prefs.Set("retroPreviewHue", simOpts.Adjust.Hue);
        Prefs.Set("autoAdvance", autoAdvance);
        Prefs.Set("filmstrip", canvas.Filmstrip);
        Prefs.Set("previewPane", preview.Visible);
        Prefs.Set("previewWidth", (int)Math.Round(preview.Width / Util.K));
        StorePresets();
        StoreRecentDest();
        StoreFavourites();
        Prefs.Set("lastFolder", historyClearedAt != null ? null : paneA.Grid.Dir);   // after Clear history: not remembered
        Prefs.Set("pane2Folder", historyClearedAt != null ? null : paneB.Grid.Dir);
        Prefs.Set("dualPane", dual);
        Prefs.Set("dualSplit", (int)Math.Round(splitRatio * 1000));
        Prefs.Save();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) { }   // dark title bar
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Application.AddMessageFilter(new WheelRouter(this, tree, paneA.Grid, paneB.Grid));
        tree.Start();
        RefreshDriveButtons();
        string last = string.IsNullOrEmpty(startArg) ? LastFolder() : null;
        if (!string.IsNullOrEmpty(startArg)) OpenImage(startArg);
        else if (last != null) { OpenImage(last); grid.Focus(); }        // where you were last time
        else
        {
            TreeNode q = tree.StartNode();                 // Pictures
            if (q != null) tree.SelectedNode = q;
            grid.Focus();
        }
        if (wantDual) SetDual(true);                       // dual-pane mode, as it was last time
    }

    void ResetPrefs()
    {
        if (MessageBox.Show(this, "Delete the settings file and start with the default settings next time?\n\n" +
                            "(The current window and options stay as they are until you close RetroGleek.)",
                            "Reset settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Prefs.Reset();
        status.Text = "  Settings reset: RetroGleek will start with the default settings next time";
    }

    // ================================================================ window helpers

    void Warn(string msg) { MessageBox.Show(this, msg, "RetroGleek", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

    // dark title bar for RetroGleek's own dialogs
    void DarkTitle(Form f)
    {
        try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch (Exception) { }
    }

    void ToggleFull()
    {
        if (!full)
        {
            Screen sc = Screen.FromControl(this);
            oldStyle = FormBorderStyle; oldState = WindowState;
            oldBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            full = true;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            menuBar.Visible = false;
            Bounds = sc.Bounds;
        }
        else
        {
            full = false;
            menuBar.Visible = true;
            FormBorderStyle = oldStyle;
            WindowState = FormWindowState.Normal;
            Bounds = oldBounds;
            WindowState = oldState;
        }
    }
}
