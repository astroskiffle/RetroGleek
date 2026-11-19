// RetroGleek - the main window, continued: rename, copy, move, paste, delete, new folder, drag and drop
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
    // ================================================================ rename, clipboard, paste

    void ReloadAll()
    {
        TreeNode n = tree.SelectedNode;
        if (NodePath(n) != null) tree.ReloadNode(n);
        if (grid.Dir != null) ShowFolder(grid.Dir, null, -1, true);
    }

    string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.EndsWith(" "))
            return "That name isn't valid. It can't be empty, end with a dot or space, or contain any of  \\ / : * ? \" < > |";
        return null;
    }

    // ================================================================ files changed: one place that refreshes what shows them

    // After RetroGleek itself changes files or folders (copy, move, rename, delete, convert, export, contact sheets,
    // turns, ratings and details written into files, undo, a retro preview saved, Find duplicates), the operation
    // says which folders changed and this brings everything that shows them up to date: each pane whose folder holds
    // one of them (both panes in dual-pane mode), the folder tree's branches for them (folders: true, when folders
    // were made, moved or deleted), the preview pane and the status bar. handled: a pane the operation has already
    // shown again in its own way (selecting the new file, following a moved folder), or null. The folder watchers
    // would catch most of this a moment later; this makes it immediate, and the same for every operation.
    void FilesChanged(IEnumerable<string> dirs, bool folders, ThumbGrid handled)
    {
        var list = new List<string>();
        foreach (string d in dirs) if (!string.IsNullOrEmpty(d) && !list.Exists(x => Util.SameDir(x, d))) list.Add(d);
        if (list.Count == 0) return;
        foreach (Pane p in Panes)
        {
            ThumbGrid g = p.Grid;
            if (g == handled || g.Dir == null || (p == paneB && !dual)) continue;   // the hidden pane rescans when it is shown
            if (!g.Scanning && list.Exists(d => g.Covers(d))) g.Rescan();   // (a scan under way already sees the change)
        }
        if (folders)
            foreach (string d in list)
                foreach (TreeNode n in tree.FindLoadedNodes(d)) tree.RefreshFolderNode(n);
        UpdatePreview();
        if (!viewing && !comparing) UpdateStatus();
    }

    void FilesChanged(string dir, bool folders, ThumbGrid handled) { FilesChanged(new[] { dir }, folders, handled); }

    void FilesChanged(string dir) { FilesChanged(new[] { dir }, false, null); }

    // the folders of these files
    static List<string> FoldersOf(IEnumerable<string> paths)
    {
        var r = new List<string>();
        foreach (string p in paths)
        {
            string d = null;
            try { d = Path.GetDirectoryName(p); } catch (Exception) { }
            if (d != null) r.Add(d);
        }
        return r;
    }

    // A folder was renamed, moved (to: where it is now) or deleted (to: null): a pane other than the active one that
    // shows it, or a folder inside it, goes along (or, when it was deleted, shows the nearest folder still there). The
    // active pane is followed by the operation itself, as before.
    void OtherPanesFollow(string from, string to)
    {
        if (!dual || string.IsNullOrEmpty(from)) return;
        foreach (Pane p in Panes)
        {
            ThumbGrid g = p.Grid;
            if (p == act || g.Dir == null || !InsideOrSame(g.Dir, from)) continue;
            string now = to != null ? to + g.Dir.Substring(from.TrimEnd('\\').Length) : null;
            if (now == null || !Directory.Exists(now))
            {
                now = to != null ? to : Path.GetDirectoryName(from.TrimEnd('\\'));
                while (now != null && !Directory.Exists(now)) now = Path.GetDirectoryName(now);
            }
            if (now != null) { g.Recursive = false; g.Load(now, null, -1); }
        }
    }

    void DoRename(bool inTree)
    {
        if (inTree)
        {
            TreeNode n = tree.SelectedNode;
            string p = NodePath(n);
            if (p == null || IsRootPath(p)) return;
            string nn = Prompt.Ask(this, "Rename folder", "New name:", n.Text, false);
            if (nn == null || nn == n.Text) return;
            string bad = ValidateName(nn);
            if (bad != null) { Warn(bad); return; }
            string dest = Path.Combine(Path.GetDirectoryName(p), nn);
            if (!string.Equals(dest, p, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(dest) || File.Exists(dest))) { Warn("An item with that name already exists."); return; }
            try { Directory.Move(p, dest); }
            catch (Exception ex) { Warn(ex.Message); return; }
            PushUndo("rename of " + Path.GetFileName(p.TrimEnd('\\')), false,
                     new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(p, dest) });
            string gd = grid.Dir;
            n.Text = nn; n.Tag = dest;
            tree.ReloadNode(n);
            if (gd != null && (Util.SameDir(gd, p) || gd.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                ShowFolder(dest + gd.Substring(p.TrimEnd('\\').Length), null, -1, true);
            OtherPanesFollow(p, dest);
            FilesChanged(Path.GetDirectoryName(p), true, grid);
            return;
        }
        List<int> sel = grid.SelectedIndices();
        if (sel.Count != 1) return;
        Item it = grid.Items[sel[0]];
        string name = Prompt.Ask(this, "Rename", "New name:", it.Name, true);
        if (name == null || name == it.Name) return;
        string err = ValidateName(name);
        if (err != null) { Warn(err); return; }
        string target = Path.Combine(Path.GetDirectoryName(it.Path), name);
        if (!string.Equals(target, it.Path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target))) { Warn("A file with that name already exists."); return; }
        try { File.Move(it.Path, target); }
        catch (Exception ex) { Warn(ex.Message); return; }
        PushUndo("rename of " + it.Name, false, new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(it.Path, target) });
        ShowFolder(grid.Dir, target, -1, true);
        FilesChanged(Path.GetDirectoryName(target), false, grid);
    }

    void DoCopy(bool inTree) { PutOnClipboard(inTree, false); }

    // Ctrl+X: like Copy, but marked "move" the way Explorer does it, so Paste here or in Explorer moves the files
    void DoCut(bool inTree) { PutOnClipboard(inTree, true); }

    void PutOnClipboard(bool inTree, bool cut)
    {
        var list = new StringCollection();
        if (inTree)
        {
            string p = NodePath(tree.SelectedNode);
            if (p == null || IsRootPath(p)) return;
            list.Add(p);
        }
        else foreach (int i in grid.SelectedIndices()) list.Add(grid.Items[i].Path);
        if (list.Count == 0) return;
        try
        {
            var d = new DataObject();
            d.SetFileDropList(list);                                // real Explorer-compatible clipboard format
            d.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)(cut ? DragDropEffects.Move : DragDropEffects.Copy))));
            Clipboard.SetDataObject(d, true, 5, 50);
            var marked = new List<string>();
            if (cut) foreach (string p in list) marked.Add(p);
            SetCut(marked);                                         // copying instead of cutting un-fades earlier cut items
            status.Text = "  " + (cut ? "Cut " : "Copied ") + list.Count + (list.Count == 1 ? " item" : " items") + " to the clipboard"
                        + (cut ? " (Paste moves " + (list.Count == 1 ? "it" : "them") + ")" : "");
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    void DoPaste(bool inTree)
    {
        string dest = inTree ? NodePath(tree.SelectedNode) : grid.Dir;
        if (dest == null || !ClipHasFiles()) return;
        var srcs = new List<string>();
        foreach (string s in Clipboard.GetFileDropList()) srcs.Add(s);
        if (srcs.Count == 0) return;
        if (ClipIsCut())
        {
            if (MoveItems(srcs, dest)) { try { Clipboard.Clear(); } catch (Exception) { } SetCut(null); }   // cut files are pasted once, as in Explorer
        }
        else CopyItems(srcs, dest);
    }

    static bool ClipHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch (Exception) { return false; }
    }

    // ================================================================ copy and move

    // ---------------------------------------------------------------- faded "cut" items

    readonly HashSet<string> cutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // marks the items cut with Ctrl+X (null or empty: none). Pictures fade in the grid, folders turn grey in the tree.
    void SetCut(List<string> paths)
    {
        foreach (string p in cutPaths)
            foreach (TreeNode n in tree.FindLoadedNodes(p)) n.ForeColor = Color.Empty;
        cutPaths.Clear();
        if (paths != null) foreach (string p in paths) cutPaths.Add(p.TrimEnd('\\', '/'));
        foreach (string p in cutPaths)
            foreach (TreeNode n in tree.FindLoadedNodes(p)) if (n.Parent != null && n.Parent != tree.PcNode) n.ForeColor = Color.Gray;
        grid.CutPaths = cutPaths;
        grid.Invalidate();
    }

    // When RetroGleek comes back to the front: if the clipboard no longer holds exactly what was cut here (something
    // else was copied, here or in another program, or Explorer pasted it), the items stop looking cut.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (cutPaths.Count == 0) return;
        bool still = false;
        try
        {
            if (ClipIsCut() && Clipboard.ContainsFileDropList())
            {
                var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string p in Clipboard.GetFileDropList()) now.Add(p.TrimEnd('\\', '/'));
                still = now.SetEquals(cutPaths);
            }
        }
        catch (Exception) { still = true; }                 // clipboard busy: leave things as they are
        if (!still) SetCut(null);
    }

    // true when the files on the clipboard were cut (here or in Explorer) rather than copied
    static bool ClipIsCut()
    {
        try
        {
            IDataObject d = Clipboard.GetDataObject();
            var ms = d == null ? null : d.GetData("Preferred DropEffect") as MemoryStream;
            if (ms == null || ms.Length < 4) return false;
            byte[] b = new byte[4];
            ms.Position = 0;
            if (ms.Read(b, 0, 4) != 4) return false;
            return (BitConverter.ToInt32(b, 0) & (int)DragDropEffects.Move) != 0;
        }
        catch (Exception) { return false; }
    }

    // Copying and moving take files from local drives only, and never cloud placeholders (that would download them).
    // Returns null if the item may be used, otherwise the reason.
    internal static string RefuseSource(string s)
    {
        try
        {
            if (s.StartsWith("\\\\")) return "network locations are not used (RetroGleek works with local drives only).";
            string root = Path.GetPathRoot(Path.GetFullPath(s));
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network) return "network drives are not used (local drives only).";
            if (File.Exists(s) && (File.GetAttributes(s) & Util.CloudMask) != 0) return "cloud-only file that isn't stored on this PC, so it was left alone.";
        }
        catch (Exception ex) { return ex.Message; }
        return null;
    }

    static bool InsideOrSame(string dest, string folder)
    {
        string f = folder.TrimEnd('\\', '/');
        return Util.SameDir(dest, f) || dest.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    void CopyItems(List<string> srcs, string dest)
    {
        status.Text = "  Copying " + srcs.Count + (srcs.Count == 1 ? " item" : " items") + "...";
        ThreadPool.QueueUserWorkItem(delegate            // copying never blocks the UI
        {
            var errs = new List<string>();
            foreach (string s in srcs)
            {
                try
                {
                    bool isDir = Directory.Exists(s);
                    if (!isDir && !File.Exists(s)) continue;
                    string why = RefuseSource(s);
                    if (why != null) { errs.Add(Path.GetFileName(s.TrimEnd('\\', '/')) + ": " + why); continue; }
                    string name = Path.GetFileName(s.TrimEnd('\\', '/'));
                    if (name.Length == 0) continue;
                    if (isDir && (Util.SameDir(dest, s) || dest.StartsWith(s.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)))
                    { errs.Add(name + ": a folder can't be copied into itself."); continue; }
                    string target = Util.UniqueName(dest, name, isDir);   // never overwrites anything
                    if (isDir) Util.CopyDir(s, target); else File.Copy(s, target);
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(s) + ": " + ex.Message); }
            }
            Util.Post(this, delegate { PasteDone(dest, errs); });
        });
    }

    // Folders that would go to another drive and hold cloud placeholders (OneDrive and the like): their path, and
    // what they hold. Windows moves a folder to another drive by copying it, which downloads every cloud-only file
    // in it first; a move on the same drive only renames, so nothing is downloaded and nothing is listed here.
    // Counting reads attributes only, so it downloads nothing itself.
    static Dictionary<string, string> CloudFoldersCrossingDrives(List<string> srcs, string dest)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string droot = Path.GetPathRoot(Path.GetFullPath(dest));
        foreach (string raw in srcs)
        {
            string s = raw.TrimEnd('\\', '/');
            try
            {
                if (!Directory.Exists(s) || IsRootPath(s)) continue;
                if (string.Equals(Path.GetPathRoot(Path.GetFullPath(s)), droot, StringComparison.OrdinalIgnoreCase)) continue;
                int files, folders;
                bool more;
                Util.CloudInside(s, out files, out folders, out more);
                if (files + folders > 0 && !found.ContainsKey(s)) found.Add(s, Util.CloudText(files, folders) + (more ? " (or more)" : ""));
            }
            catch (Exception) { }                          // unreadable: the move itself will report the problem
        }
        return found;
    }

    // Moves files and folders into dest with Windows' own file operation (progress window for big moves, works
    // across drives). Never overwrites: a name that is taken becomes "name (2)". Runs on the UI thread, like delete.
    // A folder that would go to another drive with cloud-only files inside is moved only after you say so.
    // Returns true if at least one item was moved.
    bool MoveItems(List<string> srcs, string dest)
    {
        var errs = new List<string>();
        var touched = new List<string>();                  // folders whose contents changed
        string follow = null;                              // the shown folder moved: where it is now
        string gd = grid.Dir;
        int moved = 0;
        var undoPairs = new List<KeyValuePair<string, string>>();
        HashSet<string> leaveOut = null;                   // folders with cloud-only files that you chose to leave where they are
        Cursor = Cursors.WaitCursor;
        Dictionary<string, string> cloudy;
        try { cloudy = CloudFoldersCrossingDrives(srcs, dest); }
        finally { Cursor = Cursors.Default; }
        if (cloudy.Count > 0)
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, string> kv in cloudy)
            {
                if (lines.Count == 8) { lines.Add("  ..."); break; }
                lines.Add("  " + Path.GetFileName(kv.Key) + ":  " + kv.Value);
            }
            bool one = cloudy.Count == 1;
            string ask = "Moving to another drive means Windows copies " + (one ? "this folder" : "these folders") +
                         ", and it would first download what " + (one ? "it holds" : "they hold") +
                         " that is stored only in the cloud (OneDrive or similar):\n\n" + string.Join("\n", lines) + "\n\n" +
                         "Yes:  move anyway (Windows downloads those files; RetroGleek itself never goes online).\n" +
                         "No:  leave " + (one ? "this folder" : "these folders") + " where " + (one ? "it is" : "they are") + " and move the rest.\n" +
                         "Cancel:  move nothing.";
            DialogResult answer = MessageBox.Show(this, ask, "Cloud-only files", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                                                  MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.No) leaveOut = new HashSet<string>(cloudy.Keys, StringComparer.OrdinalIgnoreCase);
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (string raw in srcs)
            {
                string s = raw.TrimEnd('\\', '/');
                bool isDir = Directory.Exists(s);
                if (!isDir && !File.Exists(s)) continue;
                string name = Path.GetFileName(s);
                if (name.Length == 0 || IsRootPath(s)) { errs.Add(s + ": a whole drive can't be moved."); continue; }
                if (leaveOut != null && leaveOut.Contains(s)) { errs.Add(name + ": left where it is (it holds cloud-only files)."); continue; }
                string why = RefuseSource(s);
                if (why != null) { errs.Add(name + ": " + why); continue; }
                string from = Path.GetDirectoryName(s);
                if (Util.SameDir(from, dest)) continue;                            // already there
                if (isDir && InsideOrSame(dest, s)) { errs.Add(name + ": a folder can't be moved into itself."); continue; }
                if (isDir && ProtectedFolder(s) != null) { errs.Add(name + ": Windows' own folders (such as " + ProtectedFolder(s) + ") are not moved."); continue; }
                string target = isDir ? Util.UniquePath(dest, name, "")
                                      : Util.UniquePath(dest, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
                try
                {
                    if (isDir) VBFS.MoveDirectory(s, target, VBUI.AllDialogs, VBCancel.ThrowException);
                    else VBFS.MoveFile(s, target, VBUI.AllDialogs, VBCancel.ThrowException);
                    moved++;
                    undoPairs.Add(new KeyValuePair<string, string>(s, target));
                    touched.Add(from);
                    if (isDir && gd != null && InsideOrSame(gd, s)) follow = target + gd.Substring(s.Length);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errs.Add(name + ": " + ex.Message); }
            }
        }
        finally { Cursor = Cursors.Default; }

        touched.Add(dest);
        if (follow != null) { ShowFolder(follow, null, -1, true); tree.Reveal(follow); }
        foreach (KeyValuePair<string, string> mp in undoPairs) if (Directory.Exists(mp.Value)) OtherPanesFollow(mp.Key, mp.Value);
        FilesChanged(touched, true, follow != null ? grid : null);
        PushUndo("move of " + (moved == 1 ? Path.GetFileName(undoPairs[0].Key) : moved + " items") + " to " + (Path.GetFileName(dest.TrimEnd('\\')).Length > 0 ? Path.GetFileName(dest.TrimEnd('\\')) : dest), true, undoPairs);
        status.Text = "  Moved " + moved + (moved == 1 ? " item" : " items") + " to " + dest + (moved > 0 ? "   (" + KeysOf("undo") + " undoes it)" : "");
        if (errs.Count > 0)
        {
            if (errs.Count > 8) { errs = errs.GetRange(0, 8); errs.Add("..."); }
            Warn("Some items could not be moved:\n\n" + string.Join("\n", errs));
        }
        return moved > 0;
    }

    // ================================================================ drag and drop onto the tree

    // ---------------------------------------------------------------- dropping onto a folder in the tree

    TreeNode dropNode;

    bool treeDragging;                                     // a folder from our own tree is being dragged

    // Dragging a folder in the tree: drop it on another folder (or in Explorer) to move / copy it.
    // Drives, the quick-access entries and Windows' own folders can't be dragged.
    void OnTreeItemDrag(object s, ItemDragEventArgs e)
    {
        var n = e.Item as TreeNode;
        string p = NodePath(n);
        if (n == null || p == null || n.Name == "quick" || n.Name == "fav" || IsRootPath(p)) return;   // shortcuts: never dragged
        if (ProtectedFolder(p) != null) { status.Text = "  " + Path.GetFileName(p) + " is a Windows folder and can't be moved"; return; }
        var list = new StringCollection();
        list.Add(p);
        var data = new DataObject();
        data.SetFileDropList(list);
        treeDragging = true;
        try { tree.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch (Exception) { }
        finally { treeDragging = false; SetDropNode(null); }
    }

    // Windows' own folders (and any folder that contains one) are never moved: returns its name, or null
    static string ProtectedFolder(string folder)
    {
        var special = new List<string>();
        Environment.SpecialFolder[] kinds =
        {
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86
        };
        foreach (Environment.SpecialFolder k in kinds)
        {
            try { string p = Environment.GetFolderPath(k); if (!string.IsNullOrEmpty(p)) special.Add(p); } catch (Exception) { }
        }
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) special.Add(Path.Combine(home, "Downloads"));
        }
        catch (Exception) { }
        foreach (string p in special)
            if (InsideOrSame(p, folder)) return Path.GetFileName(p.TrimEnd('\\'));
        return null;
    }

    void SetDropNode(TreeNode n)
    {
        if (n == dropNode) return;
        if (dropNode != null) { dropNode.BackColor = Color.Empty; dropNode.ForeColor = Color.Empty; }
        dropNode = n;
        if (n != null) { n.BackColor = Color.FromArgb(60, 130, 230); n.ForeColor = Color.White; }
    }

    // Explorer's rules: same drive = move, another drive = copy; Ctrl forces copy, Shift forces move
    static DragDropEffects ChooseEffect(DragEventArgs e, string[] srcs, string dest)
    {
        bool allThere = true;
        foreach (string raw in srcs)
        {
            string s = raw.TrimEnd('\\', '/');
            if (Directory.Exists(s) && InsideOrSame(dest, s)) return DragDropEffects.None;   // into itself
            if (!Util.SameDir(Path.GetDirectoryName(s), dest)) allThere = false;
        }
        if (allThere) return DragDropEffects.None;
        bool ctrl = (e.KeyState & 8) != 0, shift = (e.KeyState & 4) != 0;
        DragDropEffects want;
        if (ctrl && !shift) want = DragDropEffects.Copy;
        else if (shift && !ctrl) want = DragDropEffects.Move;
        else want = string.Equals(Path.GetPathRoot(srcs[0]), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase)
                    ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffect & want) == 0)
            want = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                 : (e.AllowedEffect & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None;
        return want;
    }

    void OnTreeDragOver(object s, DragEventArgs e)
    {
        try
        {
            string[] srcs = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            Point pt = tree.PointToClient(new Point(e.X, e.Y));
            TreeNode n = tree.GetNodeAt(pt);
            string dest = NodePath(n);
            DragDropEffects eff = srcs == null || srcs.Length == 0 || dest == null ? DragDropEffects.None : ChooseEffect(e, srcs, dest);
            e.Effect = eff;
            SetDropNode(eff != DragDropEffects.None ? n : null);

            // scroll the tree while the pointer is near its top or bottom edge
            int edge = Util.S(18);
            TreeNode top = tree.TopNode;
            if (top != null && pt.Y < edge && top.PrevVisibleNode != null) tree.TopNode = top.PrevVisibleNode;
            else if (top != null && pt.Y > tree.ClientSize.Height - edge && top.NextVisibleNode != null)
            {
                TreeNode bottom = tree.GetNodeAt(2, tree.ClientSize.Height - 2);
                if (bottom != null && bottom.NextVisibleNode != null) tree.TopNode = top.NextVisibleNode;
            }
        }
        catch (Exception) { e.Effect = DragDropEffects.None; }
    }

    void OnTreeDrop(object s, DragEventArgs e)
    {
        string[] arr = null; string dest = null;
        DragDropEffects eff = DragDropEffects.None;
        try
        {
            arr = e.Data.GetData(DataFormats.FileDrop) as string[];
            TreeNode n = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));
            dest = NodePath(n);
            if (arr != null && arr.Length > 0 && dest != null) eff = ChooseEffect(e, arr, dest);
        }
        catch (Exception) { }
        SetDropNode(null);
        if (eff == DragDropEffects.None) return;
        e.Effect = eff;
        var srcs = new List<string>(arr);
        string to = dest;
        bool move = eff == DragDropEffects.Move;
        // run after the drop has finished, so the program the files came from (maybe Explorer) isn't kept waiting
        BeginInvoke(new MethodInvoker(delegate { if (move) MoveItems(srcs, to); else CopyItems(srcs, to); }));
    }

    // ================================================================ Copy to / Move to

    // ---------------------------------------------------------------- copy to / move to a folder

    // Ctrl+Shift+Y / Ctrl+Shift+M (or Edit > Copy to / Move to): the selected pictures (or the folder chosen in the
    // tree, or the picture in the viewer) go to a folder you pick, or to one of the last 8 used. The same copy and
    // move as everywhere else: nothing is overwritten, local drives only, and a move can be undone with Ctrl+Z.
    const int MaxRecentDest = 8;

    readonly List<string> recentDest = new List<string>();

    void LoadRecentDest()
    {
        recentDest.Clear();
        for (int i = 1; i <= MaxRecentDest; i++)
        {
            string p = Prefs.Get("recentDest" + i, null);
            if (!string.IsNullOrEmpty(p) && !recentDest.Exists(x => Util.SameDir(x, p))) recentDest.Add(p);
        }
    }

    void StoreRecentDest()
    {
        for (int i = 1; i <= MaxRecentDest; i++) Prefs.Set("recentDest" + i, i <= recentDest.Count ? recentDest[i - 1] : null);
    }

    void RememberDest(string dir)
    {
        recentDest.RemoveAll(x => Util.SameDir(x, dir));
        recentDest.Insert(0, dir);
        while (recentDest.Count > MaxRecentDest) recentDest.RemoveAt(recentDest.Count - 1);
        StoreRecentDest();
    }

    List<string> TransferSources()
    {
        var list = new List<string>();
        if (viewing) { string p = canvas.CurrentPath; if (p != null && File.Exists(p)) list.Add(p); return list; }
        if (lastPane == tree) { string p = NodePath(tree.SelectedNode); if (p != null && !IsRootPath(p)) list.Add(p); return list; }
        foreach (int i in grid.SelectedIndices()) list.Add(grid.Items[i].Path);
        return list;
    }

    // dest null: ask with a folder dialog
    void TransferTo(bool move, string dest)
    {
        List<string> srcs = TransferSources();
        if (srcs.Count == 0) return;
        if (comparing) return;
        string what = srcs.Count == 1 ? "\"" + Path.GetFileName(srcs[0].TrimEnd('\\')) + "\"" : srcs.Count + " items";
        if (dest == null)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = (move ? "Move " : "Copy ") + what + " to this folder. Nothing is ever overwritten: a name that is already taken gets a number added.";
                d.ShowNewFolderButton = true;
                string start = recentDest.Count > 0 && Directory.Exists(recentDest[0]) ? recentDest[0] : grid.Dir;
                if (start != null) d.SelectedPath = start;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                dest = d.SelectedPath;
            }
        }
        if (!Directory.Exists(dest)) { Warn("That folder isn't there any more:\n" + dest); recentDest.RemoveAll(x => Util.SameDir(x, dest)); return; }
        string why = RefuseSource(dest);                      // the same local-drives-only rule as for the source
        if (why != null) { Warn("RetroGleek can't " + (move ? "move" : "copy") + " there: " + why); return; }
        RememberDest(dest);
        string place = Path.GetFileName(dest.TrimEnd('\\'));
        if (place.Length == 0) place = dest;
        if (!move)
        {
            CopyItems(srcs, dest);
            if (viewing) canvas.ShowMessage("Copying " + what + " to " + place);
            return;
        }
        string shown = viewing ? srcs[0] : null;
        bool moved = MoveItems(srcs, dest);
        if (viewing && moved && shown != null && !File.Exists(shown))
        {
            bool left = canvas.RemoveCurrent();               // the viewer moves on to the next picture
            if (!left) SetViewing(false);
            else canvas.ShowMessage("Moved " + what + " to " + place + "   (" + KeysOf("undo") + " in the browser undoes it)");
        }
    }

    void PasteDone(string dest, List<string> errs)
    {
        FilesChanged(dest, true, null);
        if (errs.Count > 0)
        {
            if (errs.Count > 8) { errs = errs.GetRange(0, 8); errs.Add("..."); }
            Warn("Some items could not be copied:\n\n" + string.Join("\n", errs));
        }
    }

    // ================================================================ delete, batch rename, new folder

    void DoDelete(bool inTree)
    {
        var paths = new List<string>();
        int hint = -1;
        if (inTree)
        {
            string p = NodePath(tree.SelectedNode);
            if (p == null || IsRootPath(p)) return;
            paths.Add(p);
        }
        else
        {
            List<int> sel = grid.SelectedIndices();
            if (sel.Count > 0) hint = sel[0];
            foreach (int i in sel) paths.Add(grid.Items[i].Path);
        }
        if (paths.Count == 0) return;
        string msg = paths.Count == 1 ? "Move \"" + Path.GetFileName(paths[0]) + "\" to the Recycle Bin?"
                                      : "Move these " + paths.Count + " items to the Recycle Bin?";
        if (MessageBox.Show(this, msg, "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var deletedDirs = paths.FindAll(x => Directory.Exists(x));

        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p)) VBFS.DeleteDirectory(p, VBUI.OnlyErrorDialogs, VBRecycle.SendToRecycleBin);
                else VBFS.DeleteFile(p, VBUI.OnlyErrorDialogs, VBRecycle.SendToRecycleBin);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Warn(ex.Message); }
        finally { Cursor = Cursors.Default; }

        foreach (string d in deletedDirs) if (!Directory.Exists(d)) OtherPanesFollow(d, null);
        if (inTree)
        {
            TreeNode parent = tree.SelectedNode.Parent;
            string pp = NodePath(parent);
            if (parent != null && pp != null)
            {
                tree.SelectQuiet(parent);
                tree.ReloadNode(parent);
                ShowFolder(pp, null, -1, true);
                FilesChanged(pp, true, grid);
                return;
            }
        }
        if (grid.Dir != null) ShowFolder(grid.Dir, null, hint, true);
        FilesChanged(FoldersOf(paths), deletedDirs.Count > 0, grid);
    }

    // Ctrl+F2 / Tools > Batch rename: the selected pictures, in grid order
    void DoBatchRename()
    {
        if (viewing) return;
        var paths = new List<string>();
        foreach (int i in grid.SelectedIndices()) paths.Add(grid.Items[i].Path);
        if (paths.Count == 0) return;
        List<KeyValuePair<string, string>> done = BatchRename.Show(this, paths, DarkTitle);
        if (done == null || done.Count == 0) return;
        PushUndo("batch rename of " + done.Count + (done.Count == 1 ? " picture" : " pictures"), false, done);
        ShowFolder(grid.Dir, done[0].Value, -1, true);
        var renamed = new List<string>();
        foreach (KeyValuePair<string, string> d in done) renamed.Add(d.Value);
        FilesChanged(FoldersOf(renamed), false, grid);
        status.Text = "  Renamed " + done.Count + (done.Count == 1 ? " picture" : " pictures") + "   (" + KeysOf("undo") + " undoes it)";
    }

    void DoNewFolder()
    {
        TreeNode n = tree.SelectedNode;
        string p = NodePath(n);
        if (p == null) return;
        try
        {
            string target = Path.Combine(p, "New folder");
            for (int i = 2; Directory.Exists(target) || File.Exists(target); i++) target = Path.Combine(p, "New folder (" + i + ")");
            Directory.CreateDirectory(target);
            tree.ReloadNode(n);
            n.Expand();
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    // Del in the viewer: moves the current file to the Recycle Bin and shows the next picture
    void DeleteCurrentPicture()
    {
        string path = canvas.CurrentPath;
        if (path == null || !File.Exists(path)) return;
        bool wasSlideshow = canvas.Slideshow;
        if (wasSlideshow) canvas.StopSlideshow();
        if (MessageBox.Show(this, "Move \"" + Path.GetFileName(path) + "\" to the Recycle Bin?", "Delete",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { VBFS.DeleteFile(path, VBUI.OnlyErrorDialogs, VBRecycle.SendToRecycleBin); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Warn(ex.Message); return; }
        if (File.Exists(path)) return;                     // cancelled in the system dialog
        string dir = Path.GetDirectoryName(path);
        bool left = canvas.RemoveCurrent();
        if (!left) SetViewing(false);
        if (grid.Covers(dir)) ShowFolder(grid.Dir, left ? canvas.Want : null, -1, true);
        FilesChanged(dir, false, grid);
    }

    // ================================================================ open dialog, files dropped from Explorer

    void OpenDialog()
    {
        using (var d = new OpenFileDialog())
        {
            d.Filter = "Images|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff|All files|*.*";
            if (grid.Dir != null) d.InitialDirectory = grid.Dir;
            if (d.ShowDialog(this) == DialogResult.OK) OpenImage(d.FileName);
        }
    }

    void OnDragEnterFiles(object s, DragEventArgs e)
    {
        e.Effect = !grid.DraggingOut && !treeDragging && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDropFiles(object s, DragEventArgs e)
    {
        if (grid.DraggingOut || treeDragging) return;
        string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (f != null && f.Length > 0) OpenImage(f[0]);
    }
}
