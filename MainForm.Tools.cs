// RetroGleek - the main window, continued: Properties, PNG export, lossless turns, conversion, duplicates, ratings, audit, contact sheets
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
    // ================================================================ Properties

    // ---------------------------------------------------------------- File > Properties (Metadata.cs)

    // the picture whose details are shown: the one in the viewer, or the focused (else the first selected) one
    string PropertiesTarget()
    {
        if (comparing) return null;
        if (viewing) return canvas.CurrentPath;
        int f = grid.FocusIndex;
        Item it = f >= 0 && f < grid.Items.Count && grid.Items[f].Sel ? grid.Items[f] : null;
        if (it == null) { List<int> sel = grid.SelectedIndices(); if (sel.Count > 0) it = grid.Items[sel[0]]; }
        return it == null ? null : it.Path;
    }

    // one window per picture; several can be open at once
    void ShowProperties()
    {
        string p = PropertiesTarget();
        if (p == null) return;
        if (!File.Exists(p)) { Warn("That picture isn't there any more:\n" + p); return; }
        var f = new MetaForm(p, DarkTitle);
        f.FileChanged = delegate (string changed)
        {
            if (viewing && string.Equals(canvas.CurrentPath, changed, StringComparison.OrdinalIgnoreCase)) canvas.Reload();
            FilesChanged(Path.GetDirectoryName(changed));
        };
        f.Show(this);
    }

    // ================================================================ save as PNG (lossless), copy picture, export frames

    sealed class ExportJob
    {
        public string Src;
        public string DestFile;      // exact target (single picture, overwrite already confirmed)
        public string DestDir;       // target folder (several pictures; names are made unique)
        public int Turns;            // extra clockwise quarter turns added in the viewer
        public bool FlipX, FlipY;    // mirroring added in the viewer
    }

    void DoSavePng()
    {
        List<int> sel = grid.SelectedIndices();
        if (sel.Count == 0) return;
        var jobs = new List<ExportJob>();
        foreach (int i in sel) jobs.Add(new ExportJob { Src = grid.Items[i].Path });
        SavePngJobs(jobs);
    }

    void SaveCurrentPng()
    {
        string path; int turns; bool fx, fy;
        if (!canvas.GetSaveSource(out path, out turns, out fx, out fy)) { Warn("There is no picture to save."); return; }
        SavePngJobs(new List<ExportJob> { new ExportJob { Src = path, Turns = turns, FlipX = fx, FlipY = fy } });
    }

    // Ctrl+C in the viewer: puts the picture, as you see it (rotation and mirroring included), on the clipboard.
    // Nothing is written to disk.
    void CopyCurrentPicture()
    {
        string path; int turns; bool fx, fy;
        if (!canvas.GetSaveSource(out path, out turns, out fx, out fy)) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Bitmap bmp = null; string err = null;
            try { bmp = Exporter.Decode(path, turns, fx, fy); }
            catch (Exception ex) { err = ex.Message; }
            Bitmap b = bmp; string e2 = err;
            if (!Util.Post(this, delegate { CopyDone(b, e2); }) && b != null) b.Dispose();
        });
    }

    void CopyDone(Bitmap bmp, string err)
    {
        if (bmp == null) { Warn("The picture could not be copied:\n\n" + err); return; }
        try
        {
            var d = new DataObject();
            d.SetImage(bmp);
            Clipboard.SetDataObject(d, true, 5, 50);       // copy = true: the clipboard keeps its own copy
            canvas.ShowMessage("Picture copied to the clipboard");
        }
        catch (Exception ex) { Warn(ex.Message); }
        finally { bmp.Dispose(); }
    }

    // Ctrl+Shift+S: every frame of animated GIFs / every page of multi-page TIFFs, as separate lossless PNGs
    void DoExportFrames()
    {
        var jobs = new List<ExportJob>();
        if (viewing)
        {
            string path; int turns; bool fx, fy;
            if (!canvas.GetSaveSource(out path, out turns, out fx, out fy)) { Warn("There is no picture to export."); return; }
            jobs.Add(new ExportJob { Src = path, Turns = turns, FlipX = fx, FlipY = fy });
        }
        else foreach (int i in grid.SelectedIndices()) jobs.Add(new ExportJob { Src = grid.Items[i].Path });
        if (jobs.Count == 0) return;
        string destDir;
        using (var d = new FolderBrowserDialog())
        {
            d.Description = "Save every frame of " + (jobs.Count == 1 ? Path.GetFileName(jobs[0].Src) : "the " + jobs.Count + " selected pictures") +
                            " as lossless PNG into this folder. Existing files are never overwritten.";
            d.SelectedPath = Path.GetDirectoryName(jobs[0].Src); d.ShowNewFolderButton = true;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            destDir = d.SelectedPath;
        }
        status.Text = "  Exporting frames...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            int frames = 0, pictures = 0;
            string first = null;
            var single = new List<string>();
            var errs = new List<string>();
            foreach (ExportJob j in jobs)
            {
                string name = Path.GetFileName(j.Src);
                try
                {
                    string f1;
                    int n = Exporter.ExportFrames(j.Src, j.Turns, j.FlipX, j.FlipY, destDir,
                        delegate (int k, int total) { Util.Post(this, delegate { status.Text = "  Exporting " + name + ": frame " + k + " of " + total + "..."; }); },
                        out f1);
                    if (n == 0) single.Add(name);
                    else { frames += n; pictures++; if (first == null) first = f1; }
                }
                catch (Exception ex) { errs.Add(name + ": " + ex.Message); }
            }
            Util.Post(this, delegate { ExportFramesDone(frames, pictures, first, destDir, single, errs); });
        });
    }

    void ExportFramesDone(int frames, int pictures, string first, string destDir, List<string> single, List<string> errs)
    {
        status.Text = "  Saved " + frames + (frames == 1 ? " frame" : " frames") + " from " + pictures + (pictures == 1 ? " picture" : " pictures") +
                      " as PNG   |   " + destDir;
        if (first != null) FilesChanged(destDir);
        var msg = new List<string>();
        if (single.Count > 0)
        {
            List<string> s = single.Count > 8 ? single.GetRange(0, 8) : single;
            msg.Add((pictures == 0 ? "Nothing was exported: " : "Skipped, ") + (single.Count == 1 ? "this picture has" : "these pictures have") +
                    " only one frame (use Save as PNG, " + KeysOf("save-png") + ", for those):\n" + string.Join("\n", s) + (single.Count > 8 ? "\n..." : ""));
        }
        if (errs.Count > 0)
        {
            List<string> e = errs.Count > 8 ? errs.GetRange(0, 8) : errs;
            msg.Add("Some pictures could not be exported:\n" + string.Join("\n", e) + (errs.Count > 8 ? "\n..." : ""));
        }
        if (msg.Count > 0) Warn(string.Join("\n\n", msg));
    }

    // ================================================================ audit, contact sheet

    // Ctrl+Shift+A / Tools > Folder audit: every picture file in the open folder (not subfolders) checked and listed
    // in one self-contained HTML page, saved where the user chooses
    bool auditBusy;

    void DoAudit(bool thumbs)
    {
        string dir = grid.Dir;
        if (dir == null || auditBusy) return;
        string dest;
        using (var d = new SaveFileDialog())
        {
            d.Title = "Save the folder audit report";
            d.Filter = "Web page (offline)|*.html";
            d.DefaultExt = "html"; d.AddExtension = true; d.OverwritePrompt = true;
            d.InitialDirectory = dir;
            d.FileName = FolderAudit.DefaultFileName(dir, thumbs, false);
            if (d.ShowDialog(this) != DialogResult.OK) return;
            dest = d.FileName;
        }
        auditBusy = true;
        status.Text = "  Auditing " + dir + "...";
        DateTime when = DateTime.Now;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null;
            int pictures = 0, failed = 0, skipped = 0;
            try
            {
                List<AuditEntry> list = FolderAudit.Scan(dir, thumbs,
                    delegate (int i, int n, string name) { Util.Post(this, delegate { status.Text = "  Auditing " + i + " of " + n + ": " + name; }); },
                    out skipped);
                pictures = list.Count;
                foreach (AuditEntry e in list) if (e.Status == "Failed") failed++;
                string html = FolderAudit.Page(dir, list, skipped, when, thumbs);
                FolderAudit.WriteReport(dest, html, false, true);          // the user agreed to replace an old one
            }
            catch (Exception ex) { err = ex.Message; }
            Util.Post(this, delegate { AuditDone(dest, pictures, failed, err); });
        });
    }

    // Tools > Contact sheet: lossless PNG index sheets of the selected pictures (at most 2,000; 100 per page)
    bool sheetBusy;

    void DoContactSheet()
    {
        if (viewing || sheetBusy) return;
        var paths = new List<string>();
        foreach (int i in grid.SelectedIndices()) if (!grid.Items[i].Offline) paths.Add(grid.Items[i].Path);
        if (paths.Count == 0) { Warn("Select the pictures for the contact sheet first (" + KeysOf("select-all") + " selects them all)."); return; }
        if (paths.Count > ContactSheet.MaxPictures)
        {
            Warn(paths.Count + " pictures are selected. A contact sheet takes at most " + ContactSheet.MaxPictures + " (20 pages of 100), " +
                 "so it stays quick to make and to open. Select fewer, or use the folder audit report (" + KeysOf("audit") + ") for a whole collection.");
            return;
        }
        string chosen; bool overwrite;
        SheetOptions o = ContactSheet.Ask(this, paths.Count, grid.Dir, DarkTitle, out chosen, out overwrite);
        if (o == null) return;
        string destDir = Path.GetDirectoryName(chosen);
        string why = RefuseSource(destDir);
        if (why != null) { Warn("The contact sheet can't be saved there: " + why); return; }
        long low, high, px;
        ContactSheet.Estimate(paths.Count, o.Size, out low, out high, out px);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(chosen)));
            if (drive.IsReady && drive.AvailableFreeSpace < high + 64L * 1024 * 1024)
            {
                Warn("There may not be enough free space on " + drive.Name + " (" + ContactSheet.Mb(drive.AvailableFreeSpace) + " free; the sheets need up to " +
                     ContactSheet.Mb(high) + "). Nothing was written.");
                return;
            }
        }
        catch (Exception) { }
        sheetBusy = true;
        string title = grid.Dir ?? "";
        status.Text = "  Making the contact sheet...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<string> written = null;
            string err = null;
            var errs = new List<string>();
            int lastTick = 0;
            try
            {
                written = ContactSheet.Make(paths, title, o, chosen, overwrite, delegate (int k, int n, int pg, int pages)
                {
                    if (Environment.TickCount - lastTick < 150 && k < n) return;           // no flood of status updates
                    lastTick = Environment.TickCount;
                    Util.Post(this, delegate { status.Text = "  Contact sheet: picture " + k + " of " + n + (pages > 1 ? " (page " + pg + " of " + pages + ")" : "") + "..."; });
                }, errs);
            }
            catch (Exception ex) { err = ex.Message; }
            Util.Post(this, delegate { SheetDone(written, err, errs); });
        });
    }

    void SheetDone(List<string> written, string err, List<string> errs)
    {
        sheetBusy = false;
        int n = written == null ? 0 : written.Count;
        if (n > 0)
        {
            status.Text = "  Saved the contact sheet: " + n + (n == 1 ? " page" : " pages") + "   |   " + written[0] + (n > 1 ? "  ..." : "");
            FilesChanged(Path.GetDirectoryName(written[0]));
        }
        else UpdateStatus();
        var msg = new List<string>();
        if (err != null) msg.Add("The contact sheet could not be finished" + (n > 0 ? " (" + n + " pages were saved)" : "") + ":\n" + err);
        if (errs.Count > 0)
        {
            List<string> e = errs.Count > 8 ? errs.GetRange(0, 8) : errs;
            msg.Add("These pictures could not be opened (they are marked on the sheet):\n" + string.Join("\n", e) + (errs.Count > 8 ? "\n..." : ""));
        }
        if (msg.Count > 0) Warn(string.Join("\n\n", msg));
    }

    // ================================================================ lossless turns, conversion, duplicates, ratings

    // ---------------------------------------------------------------- lossless JPEG turn (changes files, always asks)

    bool rotateBusy;

    // turns = clockwise quarter turns: the selected JPEG photos (browser) or the photo shown (viewer)
    void RotateFiles(int turns)
    {
        turns &= 3;
        if (turns == 0 || comparing || rotateBusy) return;
        var paths = new List<string>();
        if (viewing) { string p = canvas.CurrentPath; if (p != null) paths.Add(p); }
        else foreach (int i in grid.SelectedIndices()) if (!grid.Items[i].Offline) paths.Add(grid.Items[i].Path);
        if (paths.Count == 0) return;
        List<string> jpegs = paths.FindAll(JpegRotate.IsJpeg);
        if (jpegs.Count == 0)
        {
            Warn("Only JPEG photos can be turned on disk without loss." + (viewing ? "\n\n" + KeysOf("rotate-cw") + " and " + KeysOf("rotate-ccw") + " turn the view; Save as PNG (" + KeysOf("save-png") + ") keeps that turn in a lossless PNG copy." : ""));
            return;
        }
        string how = turns == 1 ? "90 degrees clockwise" : turns == 3 ? "90 degrees counter-clockwise" : "180 degrees";
        string what = jpegs.Count == 1 ? "\"" + Path.GetFileName(jpegs[0]) + "\"" : jpegs.Count + " JPEG photos";
        string msg = "Turn " + what + " " + how + "?\n\n" +
                     "This changes " + (jpegs.Count == 1 ? "the file" : "the files") + " on disk, without re-compressing, so no quality is lost: " +
                     "photos with an orientation tag just get a new tag value, others are turned block by block (only when their size allows it; " +
                     "otherwise they are left alone). Each new file is checked before it replaces the old one." +
                     (paths.Count > jpegs.Count ? "\n\n" + (paths.Count - jpegs.Count) + " other selected pictures are not JPEGs and are left alone." : "");
        if (MessageBox.Show(this, msg, "Turn the file", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        rotateBusy = true;
        status.Text = "  Turning " + what + "...";
        if (viewing) canvas.ShowMessage("Turning " + what + "...");
        ThreadPool.QueueUserWorkItem(delegate
        {
            int done = 0;
            var errs = new List<string>();
            foreach (string p in jpegs)
            {
                try
                {
                    var fi = new FileInfo(p);
                    long oldSize = fi.Length, oldMod = fi.LastWriteTimeUtc.Ticks;
                    JpegRotate.Rotate(p, turns);
                    Marks.Rebind(p, oldSize, oldMod);              // ratings, labels and tags stay with the photo
                    done++;
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(p) + ": " + Util.Why(ex)); }
            }
            int d2 = done;
            Util.Post(this, delegate { RotateDone(d2, errs, paths); });
        });
    }

    void RotateDone(int done, List<string> errs, List<string> paths)
    {
        rotateBusy = false;
        if (viewing) { canvas.Reload(); if (done > 0) canvas.ShowMessage("Turned and saved (lossless)"); }
        FilesChanged(FoldersOf(paths), false, null);
        status.Text = "  Turned " + done + (done == 1 ? " photo" : " photos") + " (lossless)" + (errs.Count > 0 ? ", " + errs.Count + " left as they were" : "");
        if (errs.Count > 0)
        {
            List<string> e = errs.Count > 8 ? errs.GetRange(0, 8) : errs;
            Warn((done > 0 ? "Some photos were left as they were:" : "Nothing was turned:") + "\n\n" + string.Join("\n", e) + (errs.Count > 8 ? "\n..." : ""));
        }
    }

    // ---------------------------------------------------------------- converting to a retro format (new files only)

    bool convertBusy;

    // Ctrl+Shift+E / Tools > Convert to a retro format: the selected pictures (browser) or the picture shown (viewer)
    // become new files in a retro format (RetroConvert). The originals are never changed; nothing is overwritten.
    void DoConvert()
    {
        if (comparing || convertBusy) return;
        var paths = new List<string>();
        if (viewing) { string p = canvas.CurrentPath; if (p != null && File.Exists(p)) paths.Add(p); }
        else foreach (int i in grid.SelectedIndices()) if (!grid.Items[i].Offline) paths.Add(grid.Items[i].Path);
        if (paths.Count == 0) { Warn("Select the pictures to convert first (" + KeysOf("select-all") + " selects them all)."); return; }
        ConvertOptions o = RetroConvert.Ask(this, paths, Path.GetDirectoryName(paths[0]), DarkTitle);
        if (o == null) return;
        string why = RefuseSource(o.DestDir);                // local drives only, as for every other file operation
        if (why != null) { Warn("RetroGleek can't save there: " + why); return; }
        convertBusy = true;
        status.Text = "  Converting " + paths.Count + (paths.Count == 1 ? " picture" : " pictures") + "...";
        if (viewing) canvas.ShowMessage("Converting...");
        ThreadPool.QueueUserWorkItem(delegate
        {
            int done = 0, k = 0;
            string last = null;
            var errs = new List<string>();
            foreach (string p in paths)
            {
                k++;
                int kk = k;
                Util.Post(this, delegate { status.Text = "  Converting " + kk + " of " + paths.Count + "..."; });
                try { last = RetroConvert.ConvertFile(p, o); done++; }
                catch (Exception ex) { errs.Add(Path.GetFileName(p) + ": " + Util.Why(ex)); }
            }
            int d2 = done;
            string l2 = last;
            Util.Post(this, delegate { ConvertDone(d2, paths.Count, l2, o.DestDir, errs); });
        });
    }

    void ConvertDone(int done, int total, string last, string destDir, List<string> errs)
    {
        convertBusy = false;
        status.Text = "  Converted " + done + " of " + total + (total == 1 ? " picture" : " pictures") + "   |   " + destDir;
        if (viewing) canvas.ShowMessage(done > 0 ? "Converted: " + Path.GetFileName(last) : "Nothing was converted");
        if (done > 0) FilesChanged(destDir);
        if (errs.Count > 0)
        {
            List<string> e = errs.Count > 8 ? errs.GetRange(0, 8) : errs;
            Warn((done > 0 ? "Some pictures could not be converted:" : "Nothing was converted:") + "\n\n" + string.Join("\n", e) + (errs.Count > 8 ? "\n..." : ""));
        }
    }

    // ---------------------------------------------------------------- finding duplicates (Tools > Find duplicates)

    // the results window, while it is open (only one at a time)
    DupForm dupForm;

    // Tools > Find duplicates: asks where and what to look for, then shows the groups found in a window of its own
    // (Duplicates.cs). Nothing changes until you tag the pictures or move them to the Recycle Bin there.
    void DoFindDuplicates()
    {
        if (dupForm != null && !dupForm.IsDisposed) { dupForm.Activate(); return; }
        DupOptions o = DupFinder.Ask(this, grid.Dir, DarkTitle);
        if (o == null) return;
        var f = new DupForm(o, DarkTitle);
        f.OpenPicture = delegate (string p)
        {
            if (!File.Exists(p)) { Warn("That picture isn't there any more:\n" + p); return; }
            if (comparing) EndCompare();
            OpenImage(p);
            Activate();
        };
        f.ShowInFolder = delegate (string p)
        {
            if (comparing) EndCompare();
            if (viewing) SetViewing(false);
            string d = Path.GetDirectoryName(p);
            if (!Directory.Exists(d)) return;
            ShowFolder(d, File.Exists(p) ? p : null, -1, false);
            tree.Reveal(d);
            Activate();
        };
        f.Compare = delegate (string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) { Warn("One of those pictures isn't there any more."); return; }
            ComparePaths(a, b);
            Activate();
        };
        f.MarksChanged = delegate { MarksChangedAll(); canvas.MarksChanged(); UpdatePreview(); if (!viewing && !comparing) UpdateStatus(); };
        f.FilesDeleted = delegate (List<string> gone) { FilesChanged(FoldersOf(gone), false, null); };
        f.FormClosed += delegate { dupForm = null; };
        dupForm = f;
        f.Show();
    }

    // ---------------------------------------------------------------- star ratings into the files (changes files, always asks)

    bool ratingBusy;

    // Edit > Rating > Write my ratings into the files: your RetroGleek star rating is copied into the selected JPEG
    // and PNG files (browser) or the picture shown (viewer), so other programs see it. Pictures without a RetroGleek
    // rating, and other formats, are left alone. Marks stay with the files (their size and date change).
    void WriteRatingsToFiles()
    {
        if (comparing || ratingBusy) return;
        var paths = new List<string>();
        if (viewing) { string p = canvas.CurrentPath; if (p != null) paths.Add(p); }
        else foreach (int i in grid.SelectedIndices()) if (!grid.Items[i].Offline) paths.Add(grid.Items[i].Path);
        if (paths.Count == 0) return;
        var jobs = new List<KeyValuePair<string, int>>();
        int unrated = 0, otherFormat = 0;
        foreach (string p in paths)
        {
            if (!RatingWriter.CanWrite(p)) { otherFormat++; continue; }
            Mark m = Marks.ForFile(p);
            if (m == null || m.Stars == 0) { unrated++; continue; }
            jobs.Add(new KeyValuePair<string, int>(p, m.Stars));
        }
        string left = (unrated > 0 ? "\n\n" + unrated + (unrated == 1 ? " picture has" : " pictures have") + " no RetroGleek rating and " + (unrated == 1 ? "is" : "are") + " left alone." : "") +
                      (otherFormat > 0 ? "\n\n" + otherFormat + (otherFormat == 1 ? " picture is" : " pictures are") + " not JPEG or PNG and " + (otherFormat == 1 ? "is" : "are") + " left alone." : "");
        if (jobs.Count == 0)
        {
            Warn("There is nothing to write: ratings are written into JPEG and PNG files that you have rated in RetroGleek (" + KeysRange("rate-1", "rate-5") + ")." + left);
            return;
        }
        string what = jobs.Count == 1 ? "\"" + Path.GetFileName(jobs[0].Key) + "\" (" + Marks.Describe(new Mark { Stars = jobs[0].Value }) + ")" : jobs.Count + " pictures";
        string msg = "Write your star rating into " + what + "?\n\n" +
                     "This changes " + (jobs.Count == 1 ? "the file" : "the files") + " on disk, so Windows Explorer, Lightroom and other programs can see the rating. " +
                     "Only the information block changes (XMP, and the Windows rating tags if there are any); the picture data is copied byte for byte, " +
                     "so no quality is lost. Each new file is checked before it replaces the old one. Colour labels and tags stay in RetroGleek only." + left;
        if (MessageBox.Show(this, msg, "Write ratings into the files", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        ratingBusy = true;
        status.Text = "  Writing ratings into " + what + "...";
        if (viewing) canvas.ShowMessage("Writing the rating into the file...");
        ThreadPool.QueueUserWorkItem(delegate
        {
            int done = 0;
            var errs = new List<string>();
            foreach (KeyValuePair<string, int> j in jobs)
            {
                try
                {
                    var fi = new FileInfo(j.Key);
                    long oldSize = fi.Length, oldMod = fi.LastWriteTimeUtc.Ticks;
                    RatingWriter.Write(j.Key, j.Value);
                    Marks.Rebind(j.Key, oldSize, oldMod);           // ratings, labels and tags stay with the file
                    done++;
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(j.Key) + ": " + Util.Why(ex)); }
            }
            int d2 = done;
            Util.Post(this, delegate { RatingsDone(d2, errs, paths); });
        });
    }

    void RatingsDone(int done, List<string> errs, List<string> paths)
    {
        ratingBusy = false;
        if (viewing) { canvas.Reload(); if (done > 0) canvas.ShowMessage("Rating written into the file"); }
        FilesChanged(FoldersOf(paths), false, null);
        status.Text = "  Wrote the rating into " + done + (done == 1 ? " file" : " files") + (errs.Count > 0 ? ", " + errs.Count + " left as they were" : "");
        if (errs.Count > 0)
        {
            List<string> e = errs.Count > 8 ? errs.GetRange(0, 8) : errs;
            Warn((done > 0 ? "Some files were left as they were:" : "Nothing was written:") + "\n\n" + string.Join("\n", e) + (errs.Count > 8 ? "\n..." : ""));
        }
    }

    // ================================================================ PNG export jobs

    void AuditDone(string dest, int pictures, int failed, string err)
    {
        auditBusy = false;
        if (err != null) { UpdateStatus(); Warn("The audit could not be saved:\n\n" + err); return; }
        status.Text = "  Audit saved: " + pictures + (pictures == 1 ? " picture file" : " picture files") +
                      (failed > 0 ? ", " + failed + " could not be opened" : "") + "   |   " + dest;
        MessageBox.Show(this, "The folder audit was saved as\n\n" + dest + "\n\n" + pictures + (pictures == 1 ? " picture file" : " picture files") +
                        " checked" + (failed > 0 ? ", " + failed + " could not be opened" : "") + ".\n\n" +
                        "Double-click the file to open it in your web browser. It works offline and loads nothing from the internet.",
                        "Folder audit", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SavePngJobs(List<ExportJob> jobs)
    {
        string firstDir = Path.GetDirectoryName(jobs[0].Src);
        if (jobs.Count == 1)
        {
            using (var d = new SaveFileDialog())
            {
                d.Title = "Save as PNG (lossless)";
                d.Filter = "PNG image (lossless)|*.png";
                d.DefaultExt = "png"; d.AddExtension = true; d.OverwritePrompt = true;
                d.InitialDirectory = firstDir;
                d.FileName = Path.GetFileNameWithoutExtension(jobs[0].Src) + ".png";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                jobs[0].DestFile = d.FileName;
            }
        }
        else
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Save " + jobs.Count + " pictures as lossless PNG into this folder. Existing files are never overwritten.";
                d.SelectedPath = firstDir; d.ShowNewFolderButton = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                foreach (ExportJob j in jobs) j.DestDir = d.SelectedPath;
            }
        }
        StartExport(jobs);
    }

    // decoding, encoding and verifying run on a background thread, so the window stays responsive
    void StartExport(List<ExportJob> jobs)
    {
        status.Text = "  Saving PNG...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            int ok = 0;
            string last = null;
            var errs = new List<string>();
            for (int i = 0; i < jobs.Count; i++)
            {
                ExportJob j = jobs[i];
                int n = i + 1;
                Util.Post(this, delegate { status.Text = "  Saving PNG " + n + " of " + jobs.Count + "..."; });
                try
                {
                    using (Bitmap bmp = Exporter.Decode(j.Src, j.Turns, j.FlipX, j.FlipY))
                    {
                        string dest = j.DestFile != null ? j.DestFile
                                    : Util.UniquePath(j.DestDir, Path.GetFileNameWithoutExtension(j.Src), ".png");
                        Exporter.SavePng(bmp, dest, j.DestFile != null);
                        last = dest; ok++;
                    }
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(j.Src) + ": " + ex.Message); }
            }
            Util.Post(this, delegate { ExportDone(ok, jobs.Count, last, errs); });
        });
    }

    void ExportDone(int ok, int total, string last, List<string> errs)
    {
        status.Text = "  Saved " + ok + " of " + total + (total == 1 ? " picture" : " pictures") + " as PNG" +
                      (last != null ? "   |   " + Path.GetDirectoryName(last) : "");
        bool shown = last != null && !viewing && grid.Covers(Path.GetDirectoryName(last));
        if (shown) ShowFolder(grid.Dir, ok == 1 ? last : null, -1, true);   // show the new file(s)
        if (last != null) FilesChanged(Path.GetDirectoryName(last), false, shown ? grid : null);
        if (errs.Count > 0)
        {
            if (errs.Count > 8) { errs = errs.GetRange(0, 8); errs.Add("..."); }
            Warn("Some pictures could not be saved:\n\n" + string.Join("\n", errs));
        }
    }
}
