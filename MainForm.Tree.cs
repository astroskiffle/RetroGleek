// RetroGleek - the main window, continued: drives plugged in or out, favourite and recent folders (the folder tree
// itself is FolderTree.cs)
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
    // ================================================================ drives plugged in or out

    readonly WinTimer driveTimer = new WinTimer { Interval = 1500 };

    // a USB stick or card was plugged in or removed (Windows tells every window; no extra system calls needed)
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x0219)                                  // WM_DEVICECHANGE
        {
            long w = m.WParam.ToInt64();
            if (w == 0x8000 || w == 0x8004) { driveTimer.Stop(); driveTimer.Start(); }   // device arrived / removed
        }
    }

    // the drive buttons beside the address bar, and the drives in the folder tree
    void RefreshDrives()
    {
        RefreshDriveButtons();
        tree.RefreshDrives();
    }

    // ================================================================ favourites and recent folders

    // ---------------------------------------------------------------- favourite and recent folders

    // Favourites (Ctrl+D, Go > Favourites, right-click a folder in the tree): shortcuts listed in the tree after
    // Desktop, Documents, Downloads and Pictures, with a star. Recent folders: the last 10 folders opened.
    // Both are remembered in settings.ini. A favourite is only a shortcut: removing it never touches the folder.
    const int MaxFavourites = 20, MaxRecentFolders = 10;

    readonly List<string> favourites = new List<string>();

    readonly List<string> recentFolders = new List<string>();

    ToolStripMenuItem tFav, gRate, gProps, gToOther, gMoveOther;

    void LoadFavourites()
    {
        favourites.Clear(); recentFolders.Clear();
        for (int i = 1; i <= MaxFavourites; i++)
        {
            string p = Prefs.Get("favourite" + i, null);
            if (!string.IsNullOrEmpty(p) && !p.StartsWith("\\\\") && !favourites.Exists(x => Util.SameDir(x, p))) favourites.Add(p);
        }
        for (int i = 1; i <= MaxRecentFolders; i++)
        {
            string p = Prefs.Get("recentFolder" + i, null);
            if (!string.IsNullOrEmpty(p) && !p.StartsWith("\\\\") && !recentFolders.Exists(x => Util.SameDir(x, p))) recentFolders.Add(p);
        }
        tree.SetFavourites(favourites);
    }

    void StoreFavourites()
    {
        for (int i = 1; i <= MaxFavourites; i++) Prefs.Set("favourite" + i, i <= favourites.Count ? favourites[i - 1] : null);
        for (int i = 1; i <= MaxRecentFolders; i++) Prefs.Set("recentFolder" + i, i <= recentFolders.Count ? recentFolders[i - 1] : null);
    }

    bool IsFavourite(string dir) { return dir != null && favourites.Exists(x => Util.SameDir(x, dir)); }

    void ToggleFavourite(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        string name = FavText(dir);
        if (IsFavourite(dir))
        {
            favourites.RemoveAll(x => Util.SameDir(x, dir));
            status.Text = "  Removed " + name + " from the favourites (the folder itself is untouched)";
        }
        else
        {
            if (favourites.Count >= MaxFavourites) { Warn("You already have " + MaxFavourites + " favourites. Remove one first (right-click it in the folder tree)."); return; }
            favourites.Add(dir);
            status.Text = "  Added " + name + " to the favourites (in the folder tree and Go > Favourites)";
        }
        StoreFavourites();
        tree.SetFavourites(favourites);
        if (viewing) canvas.ShowMessage(IsFavourite(dir) ? "Added " + name + " to the favourites" : "Removed " + name + " from the favourites");
    }

    static string FavText(string dir) { return FolderTree.FavText(dir); }

    void RememberRecentFolder(string dir)
    {
        recentFolders.RemoveAll(x => Util.SameDir(x, dir));
        recentFolders.Insert(0, dir);
        while (recentFolders.Count > MaxRecentFolders) recentFolders.RemoveAt(recentFolders.Count - 1);
    }

    // from Go > Favourites / Recent folders
    void OpenFolderShortcut(string dir)
    {
        if (!Directory.Exists(dir)) { Warn("That folder isn't available right now (moved, deleted, or on a drive that isn't connected):\n\n" + dir); return; }
        OpenImage(dir);
    }

    // ================================================================ tree helpers

    static string NodePath(TreeNode n) { return FolderTree.NodePath(n); }

    static bool IsRootPath(string p) { return FolderTree.IsRootPath(p); }

    bool IsPlainFolder(TreeNode n)
    {
        string p = NodePath(n);
        return p != null && !IsRootPath(p);
    }
}
