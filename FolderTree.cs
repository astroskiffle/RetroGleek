// RetroGleek - the folder tree: This PC, its quick-access folders, favourites and drives, loaded as you open them
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

// ====================================================================== the folder tree

// The folder tree on the left of the browser. Folders are listed on a worker thread the first time a branch opens,
// and kept up to date by themselves (one watcher per local drive, folder changes only), without closing the branches
// that are open. Drives appear and disappear as they are plugged in or out (the main window passes on Windows'
// message: RefreshDrives). Network drives are never listed.
//
// The main window learns of a chosen folder through FolderChosen (at once for a click, a moment later for the arrow
// keys, so scrubbing through folders stays quick), and asks the tree to show a folder (Reveal), to list a folder
// again after a file operation (ReloadNode, RefreshFolderNode, FindLoadedNodes) and to show the favourites
// (SetFavourites). Each node's Tag is its folder's path (NodePath); "This PC" has none.
sealed class FolderTree : TreeView
{
    // ---- set by the main window
    public Action<string> FolderChosen;                    // a folder was chosen (clicked, or reached with the keys)
    public Func<string> ShownFolder;                       // the folder the browser shows
    public Action<string> ShownFolderGone;                 // ... was deleted or moved away: show this folder (its parent) instead

    const string Dummy = "\u0001dummy";                    // the placeholder child of a folder not listed yet

    TreeNode pcNode;
    readonly Dictionary<TreeNode, List<Action>> waiting = new Dictionary<TreeNode, List<Action>>();
    readonly Dictionary<TreeNode, string> driveDefault = new Dictionary<TreeNode, string>();
    readonly WinTimer navTimer = new WinTimer { Interval = 180 };
    string navPath;
    int revealToken;
    bool suppress;                                         // selecting a node without opening its folder

    public FolderTree()
    {
        Dock = DockStyle.Left; Width = Util.S(280); BorderStyle = BorderStyle.None;
        BackColor = Color.FromArgb(32, 32, 32); ForeColor = Color.Gainsboro;
        HideSelection = false; ShowLines = false; FullRowSelect = true; HotTracking = true;
        ItemHeight = Util.S(22); Indent = Util.S(16); Font = new Font("Segoe UI", 9.5f);
        var icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Util.S(16), Util.S(16)) };
        for (int k = 0; k < 4; k++) icons.Images.Add(DrawIcon(k, Util.S(16)));
        ImageList = icons;
        Util.Dark(this);
        BeforeExpand += delegate (object s, TreeViewCancelEventArgs e) { EnsureLoaded(e.Node, null); };
        AfterSelect += OnTreeSelect;
        navTimer.Tick += delegate { navTimer.Stop(); if (navPath != null && FolderChosen != null) FolderChosen(navPath); };
        treeTimer.Tick += delegate { treeTimer.Stop(); ApplyTreeChanges(); };
    }

    // "This PC", the root of everything (BuildRoots makes it)
    public TreeNode PcNode { get { return pcNode; } }

    // the first time the window is shown: open This PC, read the drives' names, start watching for folder changes
    public void Start()
    {
        pcNode.Expand();
        LabelDrives();
        StartTreeWatch();
    }

    // Quick access: Pictures (else the last entry), selected as the place to start when there is no last folder
    public TreeNode StartNode()
    {
        TreeNode q = null;
        foreach (TreeNode n in pcNode.Nodes) if (n.Name == "quick" && n.Text == "Pictures") q = n;
        if (q == null && pcNode.Nodes.Count > 0) q = pcNode.Nodes[pcNode.Nodes.Count - 1];
        return q;
    }

    // ---------------------------------------------------------------- the rest, as it was in the main window

    static Bitmap DrawIcon(int kind, int s)
    {
        var b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            float u = s / 16f;
            if (kind == 0)        // folder
                using (var back = new SolidBrush(Color.FromArgb(214, 160, 40)))
                using (var front = new SolidBrush(Color.FromArgb(246, 202, 84)))
                {
                    g.FillRectangle(back, 1 * u, 3 * u, 6 * u, 3 * u);
                    g.FillRectangle(back, 1 * u, 4 * u, 14 * u, 9 * u);
                    g.FillRectangle(front, 1 * u, 6 * u, 14 * u, 7 * u);
                }
            else if (kind == 1)   // drive
                using (var body = new SolidBrush(Color.FromArgb(170, 176, 186)))
                using (var led = new SolidBrush(Color.FromArgb(80, 200, 100)))
                {
                    g.FillRectangle(body, 1 * u, 5 * u, 14 * u, 7 * u);
                    g.FillRectangle(led, 11 * u, 9 * u, 2 * u, 1.5f * u);
                }
            else if (kind == 3)   // favourite: a yellow star
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var pts = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    double ang = -Math.PI / 2 + i * Math.PI / 5, r = (i & 1) == 0 ? 7.2 : 3.0;
                    pts[i] = new PointF((float)(8 + r * Math.Cos(ang)) * u, (float)(8.4 + r * Math.Sin(ang)) * u);
                }
                using (var star = new SolidBrush(Color.FromArgb(245, 200, 70))) g.FillPolygon(star, pts);
            }
            else                  // this PC
                using (var screen = new SolidBrush(Color.FromArgb(70, 140, 230)))
                using (var stand = new SolidBrush(Color.FromArgb(170, 176, 186)))
                {
                    g.FillRectangle(screen, 2 * u, 2 * u, 12 * u, 8 * u);
                    g.FillRectangle(stand, 6 * u, 11 * u, 4 * u, 2 * u);
                    g.FillRectangle(stand, 4 * u, 13 * u, 8 * u, 1.5f * u);
                }
        }
        return b;
    }

    static TreeNode DummyNode() { return new TreeNode("") { Name = Dummy }; }

    TreeNode MakeNode(string path, string text, string name, int icon)
    {
        var n = new TreeNode(text) { Tag = path, Name = name, ImageIndex = icon, SelectedImageIndex = icon };
        n.Nodes.Add(DummyNode());
        return n;
    }

    public void BuildRoots()
    {
        pcNode = new TreeNode("This PC") { Name = "pc", ImageIndex = 2, SelectedImageIndex = 2 };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[][] quick =
        {
            new[] { "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) },
            new[] { "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
            new[] { "Downloads", Path.Combine(home, "Downloads") },
            new[] { "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) }
        };
        foreach (string[] q in quick)
            if (!string.IsNullOrEmpty(q[1]) && Directory.Exists(q[1])) pcNode.Nodes.Add(MakeNode(q[1], q[0], "quick", 0));

        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            string kind = DriveKind(d.DriveType);
            if (kind == null) continue;                          // no network or virtual drives
            pcNode.Nodes.Add(MakeDriveNode(d.Name, kind));
        }
        Nodes.Add(pcNode);
    }

    static string DriveKind(DriveType t)
    {
        if (t == DriveType.Fixed) return "Local Disk";
        if (t == DriveType.Removable) return "Removable Disk";
        if (t == DriveType.CDRom) return "CD/DVD Drive";
        return null;
    }

    TreeNode MakeDriveNode(string root, string kind)
    {
        TreeNode n = MakeNode(root, kind + " (" + root.TrimEnd('\\') + ")", "drive", 1);
        driveDefault[n] = kind;
        return n;
    }

    // One watcher per local drive, reporting folder changes only (files are the grid's business). Changes are
    // collected on the watcher threads, then applied on the UI thread half a second later, and only to folders
    // the tree has already loaded. Existing branches are updated in place, so expanded folders stay open.
    readonly List<FileSystemWatcher> treeWatchers = new List<FileSystemWatcher>();

    readonly object treeSync = new object();

    readonly HashSet<string> treeDirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    bool treeDirtyAll;

    readonly WinTimer treeTimer = new WinTimer { Interval = 500 };

    public void StartTreeWatch()
    {
        foreach (FileSystemWatcher w in treeWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch (Exception) { } }
        treeWatchers.Clear();
        var roots = new List<string>();
        foreach (TreeNode n in pcNode.Nodes) if (n.Name == "drive") roots.Add((string)n.Tag);
        ThreadPool.QueueUserWorkItem(delegate                 // asking a card reader whether it is ready can be slow
        {
            var ready = new List<string>();
            foreach (string r in roots)
            {
                try
                {
                    var d = new DriveInfo(r);
                    if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)) ready.Add(r);
                }
                catch (Exception) { }
            }
            Util.Post(this, delegate
            {
                foreach (string rr in ready)
                {
                    try
                    {
                        var fw = new FileSystemWatcher(rr) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName, InternalBufferSize = 65536 };
                        fw.Created += delegate (object s, FileSystemEventArgs e) { TreeDirty(Path.GetDirectoryName(e.FullPath)); };
                        fw.Deleted += delegate (object s, FileSystemEventArgs e) { TreeDirty(Path.GetDirectoryName(e.FullPath)); };
                        fw.Renamed += delegate (object s, RenamedEventArgs e)
                        {
                            TreeDirty(Path.GetDirectoryName(e.OldFullPath));
                            TreeDirty(Path.GetDirectoryName(e.FullPath));
                        };
                        fw.Error += delegate { TreeDirty(null); };   // too many changes at once: check every loaded folder
                        fw.EnableRaisingEvents = true;
                        treeWatchers.Add(fw);
                    }
                    catch (Exception) { }                     // a drive without change notifications: F5 still works
                }
            });
        });
    }

    // watcher threads: remember which folder's subfolders changed (null = don't know, check all)
    void TreeDirty(string dir)
    {
        bool first;
        lock (treeSync)
        {
            first = treeDirty.Count == 0 && !treeDirtyAll;
            if (dir == null || treeDirty.Count > 2000) treeDirtyAll = true;
            else treeDirty.Add(dir);
        }
        if (first) Util.Post(this, delegate { treeTimer.Stop(); treeTimer.Start(); });
    }

    void ApplyTreeChanges()
    {
        List<string> dirs; bool all;
        lock (treeSync)
        {
            dirs = new List<string>(treeDirty);
            all = treeDirtyAll;
            treeDirty.Clear(); treeDirtyAll = false;
        }
        if (all)
        {
            var loaded = new List<TreeNode>();
            CollectLoaded(pcNode, loaded);
            foreach (TreeNode n in loaded) RefreshFolderNode(n);
            return;
        }
        foreach (string d in dirs)
            foreach (TreeNode n in FindLoadedNodes(d)) RefreshFolderNode(n);
    }

    void CollectLoaded(TreeNode n, List<TreeNode> into)
    {
        foreach (TreeNode c in n.Nodes)
        {
            if (!(c.Tag is string) || IsUnloaded(c)) continue;
            into.Add(c);
            CollectLoaded(c, into);
        }
    }

    // every loaded node showing this folder: under its drive, and under a quick-access entry (Pictures ...)
    public List<TreeNode> FindLoadedNodes(string path)
    {
        var r = new List<TreeNode>();
        if (string.IsNullOrEmpty(path)) return r;
        foreach (TreeNode top in pcNode.Nodes)
        {
            string tp = top.Tag as string;
            if (tp == null) continue;
            TreeNode n = Descend(top, tp, path);
            if (n != null) r.Add(n);
        }
        return r;
    }

    TreeNode Descend(TreeNode top, string topPath, string path)
    {
        if (Util.SameDir(topPath, path)) return top;
        string tp = topPath.TrimEnd('\\');
        if (!path.StartsWith(tp + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        TreeNode cur = top;
        string at = tp;
        foreach (string part in path.Substring(tp.Length + 1).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsUnloaded(cur)) return null;
            at = at + "\\" + part;
            TreeNode next = null;
            foreach (TreeNode c in cur.Nodes)
                if (Util.SameDir(c.Tag as string, at)) { next = c; break; }
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    // Lists the folder again (worker thread) and merges the result into its loaded branch
    public void RefreshFolderNode(TreeNode n)
    {
        string path = n == null ? null : n.Tag as string;
        if (path == null || IsUnloaded(n) || waiting.ContainsKey(n)) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<string> dirs = ListDirs(path);
            bool exists = Directory.Exists(path);
            Util.Post(this, delegate { MergeChildren(n, path, dirs, exists); });
        });
    }

    static bool IsUnder(TreeNode n, TreeNode ancestor)
    {
        for (TreeNode p = n; p != null; p = p.Parent) if (p == ancestor) return true;
        return false;
    }

    // Adds new subfolders and removes vanished ones, keeping every other node (and what is expanded under it)
    void MergeChildren(TreeNode n, string path, List<string> dirs, bool exists)
    {
        if (n.TreeView == null || !Util.SameDir(n.Tag as string, path) || IsUnloaded(n) || waiting.ContainsKey(n)) return;
        if (!exists) return;                                  // the folder itself went: its parent's update removes it
        var want = new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase);
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gone = new List<TreeNode>();
        foreach (TreeNode c in n.Nodes)
        {
            string cp = c.Tag as string;
            if (cp != null && want.Contains(cp)) have.Add(cp); else gone.Add(c);
        }
        bool added = dirs.Exists(x => !have.Contains(x));
        if (gone.Count == 0 && !added) return;

        TreeNode sel = SelectedNode;
        bool selGone = false;
        foreach (TreeNode g in gone) if (sel != null && IsUnder(sel, g)) selGone = true;
        string gd = ShownFolder == null ? null : ShownFolder();
        bool shownGone = false;
        foreach (TreeNode g in gone)
        {
            string gp = g.Tag as string;
            if (gp != null && gd != null && (Util.SameDir(gd, gp) || gd.StartsWith(gp.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) shownGone = true;
        }
        BeginUpdate();
        try
        {
            if (selGone) SelectQuiet(n);
            foreach (TreeNode g in gone) { waiting.Remove(g); n.Nodes.Remove(g); }
            for (int i = 0; i < dirs.Count; i++)
            {
                if (have.Contains(dirs[i])) continue;
                n.Nodes.Insert(Math.Min(i, n.Nodes.Count), MakeNode(dirs[i], Path.GetFileName(dirs[i]), "dir", 0));
            }
        }
        finally { EndUpdate(); }
        if (shownGone && (gd == null || !Directory.Exists(gd))) { if (ShownFolderGone != null) ShownFolderGone(path); }   // the shown folder went: show its parent
    }

    public void RefreshDrives()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            var now = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    string kind = DriveKind(d.DriveType);
                    if (kind != null) now.Add(new KeyValuePair<string, string>(d.Name, kind));
                }
            }
            catch (Exception) { return; }
            Util.Post(this, delegate { ApplyDrives(now); });
        });
    }

    void ApplyDrives(List<KeyValuePair<string, string>> now)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> d in now) present.Add(d.Key);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gone = new List<TreeNode>();
        foreach (TreeNode n in pcNode.Nodes)
        {
            if (n.Name != "drive") continue;
            string r = (string)n.Tag;
            if (present.Contains(r)) existing.Add(r); else gone.Add(n);
        }
        bool added = now.Exists(x => !existing.Contains(x.Key));
        if (gone.Count == 0 && !added) return;
        BeginUpdate();
        try
        {
            foreach (TreeNode g in gone)
            {
                if (SelectedNode != null && IsUnder(SelectedNode, g)) SelectQuiet(pcNode);
                waiting.Remove(g); driveDefault.Remove(g);
                pcNode.Nodes.Remove(g);
            }
            foreach (KeyValuePair<string, string> d in now)
            {
                if (existing.Contains(d.Key)) continue;
                int at = pcNode.Nodes.Count;                  // keep drives in letter order, after the quick-access entries
                for (int i = 0; i < pcNode.Nodes.Count; i++)
                {
                    TreeNode c = pcNode.Nodes[i];
                    if (c.Name == "drive" && string.Compare((string)c.Tag, d.Key, StringComparison.OrdinalIgnoreCase) > 0) { at = i; break; }
                }
                pcNode.Nodes.Insert(at, MakeDriveNode(d.Key, d.Value));
            }
        }
        finally { EndUpdate(); }
        LabelDrives();
        StartTreeWatch();
    }

    // volume labels can be slow to query (card readers, optical drives), so fetch them off-thread
    public void LabelDrives()
    {
        var nodes = new List<TreeNode>();
        foreach (TreeNode n in pcNode.Nodes) if (n.Name == "drive") nodes.Add(n);
        ThreadPool.QueueUserWorkItem(delegate
        {
            foreach (TreeNode n in nodes)
            {
                string root = (string)n.Tag, label = "";
                try { var d = new DriveInfo(root); if (d.IsReady) label = d.VolumeLabel; } catch (Exception) { }
                if (label.Length == 0) continue;
                string text = label + " (" + root.TrimEnd('\\') + ")";
                TreeNode nn = n;
                Util.Post(this, delegate { nn.Text = text; });
            }
        });
    }

    static List<string> ListDirs(string path)
    {
        var r = new List<string>();
        try
        {
            foreach (DirectoryInfo d in new DirectoryInfo(path).EnumerateDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                r.Add(d.FullName);
            }
            r.Sort(delegate (string a, string b) { return Util.NatCmp(Path.GetFileName(a), Path.GetFileName(b)); });
        }
        catch (Exception) { }                                    // access denied, disc removed, ...
        return r;
    }

    static bool IsUnloaded(TreeNode n) { return n.Nodes.Count == 1 && n.Nodes[0].Name == Dummy; }

    // Fill a node's children on a worker thread; callbacks run on the UI thread afterwards.
    public void EnsureLoaded(TreeNode n, Action done)
    {
        if (!IsUnloaded(n)) { if (done != null) done(); return; }
        List<Action> w;
        if (waiting.TryGetValue(n, out w)) { if (done != null) w.Add(done); return; }
        w = new List<Action>();
        if (done != null) w.Add(done);
        waiting[n] = w;
        n.Nodes[0].Text = "Loading...";
        string path = (string)n.Tag;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<string> dirs = ListDirs(path);
            Util.Post(this, delegate { Populate(n, dirs); });
        });
    }

    void Populate(TreeNode n, List<string> dirs)
    {
        if (!waiting.ContainsKey(n)) return;                     // node was reloaded meanwhile
        BeginUpdate();
        n.Nodes.Clear();
        foreach (string d in dirs) n.Nodes.Add(MakeNode(d, Path.GetFileName(d), "dir", 0));
        EndUpdate();
        List<Action> w = waiting[n];
        waiting.Remove(n);
        foreach (Action a in w) a();
    }

    public void ReloadNode(TreeNode n)
    {
        bool exp = n.IsExpanded;
        waiting.Remove(n);
        n.Nodes.Clear();
        n.Nodes.Add(DummyNode());
        if (exp) n.Expand();
    }

    TreeNode FindDrive(string root)
    {
        foreach (TreeNode n in pcNode.Nodes)
            if (n.Name == "drive" && Util.SameDir((string)n.Tag, root)) return n;
        return null;
    }

    TreeNode FindLoadedNode(string path)
    {
        string root = Path.GetPathRoot(path);
        TreeNode cur = root == null ? null : FindDrive(root);
        if (cur == null) return null;
        if (Util.SameDir(root, path)) return cur;
        foreach (string part in path.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsUnloaded(cur)) return null;
            TreeNode next = null;
            foreach (TreeNode c in cur.Nodes)
                if (string.Equals(c.Text, part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    // Expand and highlight a folder in the tree (asynchronously, one level at a time).
    public void Reveal(string dir)
    {
        int token = ++revealToken;
        try
        {
            dir = Path.GetFullPath(dir);
            string root = Path.GetPathRoot(dir);
            TreeNode n = FindDrive(root);
            if (n == null) return;
            string[] parts = dir.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            RevealStep(n, parts, 0, token);
        }
        catch (Exception) { }
    }

    void RevealStep(TreeNode n, string[] parts, int i, int token)
    {
        if (token != revealToken) return;
        if (i == parts.Length) { SelectQuiet(n); return; }
        EnsureLoaded(n, delegate
        {
            if (token != revealToken) return;
            n.Expand();
            TreeNode next = null;
            foreach (TreeNode c in n.Nodes)
                if (string.Equals(c.Text, parts[i], StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next == null) { SelectQuiet(n); return; }
            RevealStep(next, parts, i + 1, token);
        });
    }

    public void SelectQuiet(TreeNode n)
    {
        suppress = true;
        try { SelectedNode = n; n.EnsureVisible(); }
        finally { suppress = false; }
    }

    void OnTreeSelect(object s, TreeViewEventArgs e)
    {
        if (suppress) return;
        string p = NodePath(e.Node);
        if (p == null) return;
        if (e.Action == TreeViewAction.ByKeyboard) { navPath = p; navTimer.Stop(); navTimer.Start(); }   // debounce arrow-key scrubbing
        else if (FolderChosen != null) FolderChosen(p);
    }

    public static string NodePath(TreeNode n) { return n == null ? null : n.Tag as string; }

    public static bool IsRootPath(string p) { return Util.SameDir(Path.GetPathRoot(p), p); }

    public static string FavText(string dir)
    {
        string n = Path.GetFileName(dir.TrimEnd('\\', '/'));
        return n.Length > 0 ? n : dir;
    }

    // the favourites' nodes in the tree: right after the quick-access entries
    public void SetFavourites(IList<string> favourites)
    {
        if (pcNode == null) return;
        ShowNodeToolTips = true;
        suppress = true;                                   // no folder change while the nodes are swapped
        BeginUpdate();
        try
        {
            TreeNode sel = SelectedNode;
            bool selWasFav = sel != null && sel.Name == "fav";
            for (int i = pcNode.Nodes.Count - 1; i >= 0; i--) if (pcNode.Nodes[i].Name == "fav") { waiting.Remove(pcNode.Nodes[i]); pcNode.Nodes.RemoveAt(i); }
            int at = 0;
            for (int i = 0; i < pcNode.Nodes.Count; i++) if (pcNode.Nodes[i].Name == "quick") at = i + 1;
            foreach (string f in favourites)
            {
                TreeNode n = MakeNode(f, FavText(f), "fav", 3);
                n.ToolTipText = f;
                pcNode.Nodes.Insert(at++, n);
            }
            if (selWasFav) SelectedNode = pcNode;
        }
        finally { EndUpdate(); suppress = false; }
    }
}
