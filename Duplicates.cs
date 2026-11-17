// RetroGleek - finding duplicate and similar pictures across folders (Tools > Find duplicates)
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.
//
// Works from cheap and certain to expensive and fuzzy, each step only on what the one before could not settle:
//   1. Identical files: files are grouped by size; only files that share a size are read whole and get a SHA-256.
//   2. The same picture in different files: the decoded pixels get a SHA-256 (pictures up to 2048 pixels a side), so
//      a PNG saved again as BMP, or a JPEG whose information block changed (a rating written into it), still match.
//      Retro: plain borders around the picture are trimmed (layer by layer) and a whole-number enlargement is
//      undone, so an emulator screenshot at 2x with a border matches the original .scr or .koa exactly.
//   3. Similar pictures (resized, saved again, slightly changed): two 64-bit fingerprints of the picture without its
//      border, made from a 32 x 32 grey copy: a difference hash (dHash, 9 x 8: is each spot brighter than the next?)
//      and a DCT hash (pHash: the 8 x 8 lowest frequencies, above or below their median). Both must be close, in one
//      of 8 orientations (turned or mirrored copies match too), the shapes must agree, and a 16 x 16 colour copy of
//      the two must be close as well; that last check gives the "alike" percentage, and says when only the colours
//      differ. Large pictures that look exactly alike are decoded in full and compared pixel by pixel.
// Groups are built around the best picture of each (largest, lossless, rated, oldest ...), never by chaining, so
// every picture in a group is like its first one. Nothing is changed by finding: the results window can tag the
// pictures you don't want (then Edit > Select tagged), or move them to the Recycle Bin after asking.
// Offline, local drives only; cloud-only files are skipped (never downloaded).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VBFS = Microsoft.VisualBasic.FileIO.FileSystem;
using VBUI = Microsoft.VisualBasic.FileIO.UIOption;
using VBRecycle = Microsoft.VisualBasic.FileIO.RecycleOption;

sealed class DupOptions
{
    public List<string> Folders = new List<string>();
    public bool Subfolders = true;
    public int Mode = 2;             // 0 identical files only, 1 also the same picture in other files, 2 also similar pictures
    public int Strength = 1;         // similar pictures: 0 strict, 1 normal, 2 loose
}

sealed class DupFile
{
    public string Path, Name, Ext;
    public long Size, Modified;
    public int W, H;                 // the picture's size, upright (0 = not read)
    public string Platform;          // retro pictures: the computer
    public string Error;             // why the picture could not be read (then only identical files are found for it)
    public byte[] FileHash;          // SHA-256 of the file (only when another file has the same size)
    public byte[] PixHash;           // SHA-256 of the decoded pixels (pictures up to 2048 a side), or null
    public byte[] CoreHash;          // the same without a plain border and a whole-number enlargement, if that changed anything
    public int TW, TH;               // the size of the part the fingerprints describe (without the border)
    public ulong[] PHash, DHash;     // the fingerprints in the 8 orientations (0 = as it is)
    public byte[] Tiny;              // 16 x 16 RGB of the picture without its border
    public bool Lossless;
    public Mark Mark;                // your marks, read while finding
    public int Root;                 // working value: its group of identical pictures
}

sealed class DupGroup
{
    public readonly List<DupFile> Files = new List<DupFile>();    // the first is the one suggested to keep
    public readonly List<string> How = new List<string>();        // how each one matches the first
    public string KeepWhy;
    public bool Similar;             // some are only alike, not the same picture
}

static class DupFinder
{
    public const int MaxFiles = 20000;
    const int FullMax = 2048;        // pictures up to this size a side are decoded in full and their pixels compared
    const int WorkMax = 512;         // larger ones are read at about this size for the fingerprints

    static readonly string[] LossyExts = { ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".heic", ".heif", ".hif", ".avif", ".jxr", ".wdp", ".hdp" };

    // ---------------------------------------------------------------- finding

    // Runs on a worker thread. progress(text) is called now and then; stop() true ends early (null is returned).
    public static List<DupGroup> Find(DupOptions o, Action<string> progress, Func<bool> stop,
                                      out int pictures, out int skipped, out bool truncated, out List<string> unreadable)
    {
        pictures = 0; skipped = 0; truncated = false;
        unreadable = new List<string>();
        List<DupFile> files = ListFiles(o, progress, stop, out skipped, out truncated);
        if (files == null) return null;
        pictures = files.Count;

        // 1. identical files: SHA-256 for the files that share a size
        var bySize = new Dictionary<long, List<DupFile>>();
        foreach (DupFile f in files)
        {
            List<DupFile> l;
            if (!bySize.TryGetValue(f.Size, out l)) bySize[f.Size] = l = new List<DupFile>();
            l.Add(f);
        }
        var toHash = new List<DupFile>();
        foreach (List<DupFile> l in bySize.Values) if (l.Count > 1 && l[0].Size > 0) toHash.AddRange(l);
        int done = 0;
        if (!Each(toHash, delegate (DupFile f)
            {
                try { f.FileHash = Sha(f.Path); } catch (Exception ex) { f.Error = Util.Why(ex); }
                int n = Interlocked.Increment(ref done);
                if ((n & 15) == 0) progress("Comparing file contents: " + n + " of " + toHash.Count);
            }, stop)) return null;

        var uf = new int[files.Count];
        for (int i = 0; i < uf.Length; i++) { uf[i] = i; files[i].Root = i; }
        var byHash = new Dictionary<string, int>();
        for (int i = 0; i < files.Count; i++)
        {
            if (files[i].FileHash == null) continue;
            string k = Convert.ToBase64String(files[i].FileHash);
            int j;
            if (byHash.TryGetValue(k, out j)) Union(uf, i, j); else byHash[k] = i;
        }

        // 2 and 3: each picture read once (one of a set of identical files is enough)
        var edges = new List<Edge>();
        if (o.Mode >= 1)
        {
            var reps = new List<int>();
            for (int i = 0; i < files.Count; i++) if (Find(uf, i) == i) reps.Add(i);
            done = 0;
            if (!Each(reps, delegate (int i)
                {
                    try { Analyse(files[i]); }
                    catch (Exception ex) { files[i].Error = Util.Why(ex); }
                    int n = Interlocked.Increment(ref done);
                    if ((n & 7) == 0) progress("Reading pictures: " + n + " of " + reps.Count);
                }, stop)) return null;
            for (int i = 0; i < files.Count; i++)                 // the copies share what was read
            {
                int r = Find(uf, i);
                if (r != i) CopyAnalysis(files[r], files[i]);
            }

            // the same pixels, or the same picture without its border / enlargement
            var byPix = new Dictionary<string, int>();
            foreach (int i in reps)
            {
                DupFile f = files[i];
                foreach (byte[] h in new[] { f.PixHash, f.CoreHash ?? f.PixHash })
                {
                    if (h == null) continue;
                    string k = Convert.ToBase64String(h);
                    int j;
                    if (byPix.TryGetValue(k, out j)) Union(uf, i, j); else byPix[k] = i;
                }
            }

            // similar pictures (also: large pictures that look exactly alike, checked pixel by pixel)
            var roots = new List<int>();
            foreach (int i in reps) if (Find(uf, i) == i && files[i].PHash != null) roots.Add(i);
            progress("Comparing " + roots.Count + " pictures...");
            edges = Similar(files, roots, o.Strength, stop);
            if (edges == null) return null;
            var confirmed = new List<Edge>();
            foreach (Edge e in edges)
            {
                if (stop()) return null;
                if (!e.MaybeSame) continue;
                progress("Checking pictures that look exactly alike...");
                if (SamePixels(files[e.A].Path, files[e.B].Path)) { Union(uf, e.A, e.B); confirmed.Add(e); }
            }
            if (o.Mode < 2) edges.Clear();
            foreach (Edge e in confirmed) edges.Remove(e);
        }

        // your marks (for the suggestion which to keep)
        foreach (DupFile f in files)
        {
            try { f.Mark = Marks.Get(f.Path, f.Size, f.Modified); } catch (Exception) { }
            if (f.Error != null) unreadable.Add(f.Name + ": " + f.Error);
        }
        return Groups(files, uf, edges);
    }

    // the pictures in the chosen folders (local drives, no hidden files or folders, no cloud-only files)
    static List<DupFile> ListFiles(DupOptions o, Action<string> progress, Func<bool> stop, out int skipped, out bool truncated)
    {
        skipped = 0; truncated = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DupFile>();
        int lastPing = Environment.TickCount;
        foreach (string top in o.Folders)
        {
            var stack = new Stack<string>();
            stack.Push(top);
            while (stack.Count > 0)
            {
                if (stop()) return null;
                string d = stack.Pop();
                try
                {
                    foreach (FileInfo fi in new DirectoryInfo(d).EnumerateFiles())
                    {
                        FileAttributes a = fi.Attributes;
                        if ((a & FileAttributes.Hidden) != 0 || !Util.Listed(fi.Extension)) continue;
                        if ((a & Util.CloudMask) != 0) { skipped++; continue; }
                        if (!seen.Add(fi.FullName)) continue;          // folders that overlap: each file once
                        string ext = fi.Extension.ToLowerInvariant();
                        list.Add(new DupFile { Path = fi.FullName, Name = fi.Name, Ext = ext, Size = fi.Length, Modified = fi.LastWriteTimeUtc.Ticks,
                                               Lossless = Array.IndexOf(LossyExts, ext) < 0 });
                        if (list.Count >= MaxFiles) { truncated = true; return list; }
                    }
                }
                catch (Exception) { }                                 // access denied, removed meanwhile ...
                if (Environment.TickCount - lastPing > 300) { lastPing = Environment.TickCount; progress("Listing pictures: " + list.Count); }
                if (!o.Subfolders) continue;
                try
                {
                    foreach (DirectoryInfo sd in new DirectoryInfo(d).EnumerateDirectories())
                        if ((sd.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0) stack.Push(sd.FullName);
                }
                catch (Exception) { }
            }
        }
        return list;
    }

    // runs work for every item on a few threads; false if stopped
    static bool Each<T>(List<T> items, Action<T> work, Func<bool> stop)
    {
        if (items.Count == 0) return !stop();
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)) };
        ParallelLoopResult r = Parallel.For(0, items.Count, po, delegate (int i, ParallelLoopState st)
        {
            if (stop()) { st.Stop(); return; }
            work(items[i]);
        });
        return r.IsCompleted && !stop();
    }

    static byte[] Sha(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            return sha.ComputeHash(fs);
    }

    static int Find(int[] uf, int i)
    {
        while (uf[i] != i) { uf[i] = uf[uf[i]]; i = uf[i]; }
        return i;
    }

    static void Union(int[] uf, int a, int b)
    {
        a = Find(uf, a); b = Find(uf, b);
        if (a != b) uf[Math.Max(a, b)] = Math.Min(a, b);
    }

    static void CopyAnalysis(DupFile from, DupFile to)
    {
        to.W = from.W; to.H = from.H; to.Platform = from.Platform; to.PixHash = from.PixHash; to.CoreHash = from.CoreHash;
        to.TW = from.TW; to.TH = from.TH; to.PHash = from.PHash; to.DHash = from.DHash; to.Tiny = from.Tiny;
        if (to.Error == null) to.Error = from.Error;
    }

    // ---------------------------------------------------------------- reading one picture

    static void Analyse(DupFile f)
    {
        bool full;
        using (Bitmap b = Load(f, out full))
        {
            int w = b.Width, h = b.Height;
            int[] px = Pixels(b);
            if (full)
            {
                f.PixHash = PixelHash(px, w, h);
                int cx, cy, cw, ch;
                Trim(px, w, h, 0, out cx, out cy, out cw, out ch);
                int[] core = Crop(px, w, cx, cy, cw, ch);
                int rw = cw, rh = ch;
                core = Reduce(core, ref rw, ref rh);
                if (rw != w || rh != h) f.CoreHash = PixelHash(core, rw, rh);
            }
            int tx, ty, tw, th;
            Trim(px, w, h, 16, out tx, out ty, out tw, out th);              // a tolerance: JPEG edges are never quite plain
            f.TW = tw; f.TH = th;
            float[] rgb = Small(b, tx, ty, tw, th);                          // 32 x 32 RGB
            var grey = new float[32 * 32];
            for (int i = 0; i < grey.Length; i++) grey[i] = 0.299f * rgb[i * 3] + 0.587f * rgb[i * 3 + 1] + 0.114f * rgb[i * 3 + 2];
            f.PHash = new ulong[8]; f.DHash = new ulong[8];
            for (int k = 0; k < 8; k++)
            {
                float[] g = Orient(grey, 32, 1, k);
                f.PHash[k] = PHash(g);
                f.DHash[k] = DHash(g);
            }
            f.Tiny = new byte[16 * 16 * 3];                                   // 2 x 2 averages of the 32 x 32 copy
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        float s = rgb[((y * 2) * 32 + x * 2) * 3 + c] + rgb[((y * 2) * 32 + x * 2 + 1) * 3 + c]
                                + rgb[((y * 2 + 1) * 32 + x * 2) * 3 + c] + rgb[((y * 2 + 1) * 32 + x * 2 + 1) * 3 + c];
                        f.Tiny[(y * 16 + x) * 3 + c] = (byte)Math.Max(0, Math.Min(255, (int)(s / 4 + 0.5f)));
                    }
        }
    }

    // The picture upright, as a 32-bit bitmap: in full (full = true) up to FullMax a side, otherwise about WorkMax.
    static Bitmap Load(DupFile f, out bool full)
    {
        string path = f.Path;
        if (!Util.IsNative(path) && Retro.Claims(path))
        {
            long bytes; string platform; int frames; RetroGeom geom;
            Bitmap r = Retro.Load(path, out bytes, out platform, out frames, out geom);
            f.Platform = platform; f.W = r.Width; f.H = r.Height; f.Lossless = true;
            full = true;
            return r;
        }
        if (WinCodec.Claims(path))
        {
            CodecPic c;
            try { c = WinCodec.Load(path, FullMax, false); }
            catch (Exception ex) { throw new IOException(WinCodec.Why(path, ex)); }
            Bitmap b = c.Bmp;
            full = Math.Max(c.W, c.H) <= FullMax;
            Turn(b, c.Rot);
            f.W = (c.Rot & 1) == 0 ? c.W : c.H; f.H = (c.Rot & 1) == 0 ? c.H : c.W;
            if (full) return b;
            using (b) return Shrink(b, WorkMax);
        }
        byte[] data = Util.ReadShared(path);
        using (var ms = new MemoryStream(data, false))
        using (Image img = Image.FromStream(ms, false, false))
        {
            int rot = Util.ReadRotation(img) & 3;
            f.W = (rot & 1) == 0 ? img.Width : img.Height; f.H = (rot & 1) == 0 ? img.Height : img.Width;
            full = Math.Max(img.Width, img.Height) <= FullMax;
            Bitmap b;
            if (full) b = Exporter.ToArgb(img);
            else
            {
                Bitmap small = JpegRotate.IsJpeg(path) ? WinCodec.ScaledJpeg(path, img.Width, img.Height, WorkMax) : null;
                if (small != null) { using (small) b = Shrink(small, WorkMax); }
                else b = Shrink(img, WorkMax);
            }
            Turn(b, rot);
            return b;
        }
    }

    static void Turn(Bitmap b, int rot)
    {
        rot &= 3;
        if (rot != 0) b.RotateFlip(rot == 1 ? RotateFlipType.Rotate90FlipNone : rot == 2 ? RotateFlipType.Rotate180FlipNone : RotateFlipType.Rotate270FlipNone);
    }

    // a copy no larger than max a side (smooth, as for thumbnails)
    static Bitmap Shrink(Image img, int max)
    {
        double s = Math.Min(1.0, (double)max / Math.Max(img.Width, img.Height));
        int w = Math.Max(1, (int)Math.Round(img.Width * s)), h = Math.Max(1, (int)Math.Round(img.Height * s));
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        using (var ia = new ImageAttributes())
        {
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, new Rectangle(0, 0, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
        }
        return b;
    }

    static int[] Pixels(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var px = new int[w * h];
        BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), px, y * w, w); }
        finally { b.UnlockBits(d); }
        return px;
    }

    // SHA-256 of the size and the pixels (a fully transparent pixel's hidden colour does not count)
    static byte[] PixelHash(int[] px, int w, int h)
    {
        var buf = new byte[8 + px.Length * 4];
        BitConverter.GetBytes(w).CopyTo(buf, 0);
        BitConverter.GetBytes(h).CopyTo(buf, 4);
        for (int i = 0; i < px.Length; i++)
        {
            int c = px[i];
            if (((uint)c >> 24) == 0) c = 0;
            buf[8 + i * 4] = (byte)c; buf[9 + i * 4] = (byte)(c >> 8); buf[10 + i * 4] = (byte)(c >> 16); buf[11 + i * 4] = (byte)(c >> 24);
        }
        using (var sha = SHA256.Create()) return sha.ComputeHash(buf);
    }

    // The part inside plain borders: rows and columns at the edges that are all one colour are left out, layer after
    // layer (an emulator's border, then the picture's own black edge), so every copy ends at the same place.
    // tol = how far a colour may stray in each channel (0 = exactly). The whole picture if it is all one colour.
    static void Trim(int[] px, int w, int h, int tol, out int x0, out int y0, out int cw, out int ch)
    {
        int l = 0, t = 0, r = w - 1, b = h - 1;
        for (int layer = 0; layer < 4 && r - l >= 8 && b - t >= 8; layer++)
        {
            int c = px[t * w + l];
            int l0 = l, t0 = t, r0 = r, b0 = b;
            while (t < b && RowIs(px, w, t, l, r, c, tol)) t++;
            while (b > t && RowIs(px, w, b, l, r, c, tol)) b--;
            while (l < r && ColIs(px, w, l, t, b, c, tol)) l++;
            while (r > l && ColIs(px, w, r, t, b, c, tol)) r--;
            if (l == l0 && t == t0 && r == r0 && b == b0) break;
        }
        if (r - l < 7 || b - t < 7) { l = 0; t = 0; r = w - 1; b = h - 1; }   // almost nothing left: keep it all
        x0 = l; y0 = t; cw = r - l + 1; ch = b - t + 1;
    }

    static bool Near(int a, int b, int tol)
    {
        if (tol == 0) return a == b;
        return Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tol && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tol && Math.Abs((a & 255) - (b & 255)) <= tol;
    }

    static bool RowIs(int[] px, int w, int y, int l, int r, int c, int tol)
    {
        for (int x = l; x <= r; x++) if (!Near(px[y * w + x], c, tol)) return false;
        return true;
    }

    static bool ColIs(int[] px, int w, int x, int t, int b, int c, int tol)
    {
        for (int y = t; y <= b; y++) if (!Near(px[y * w + x], c, tol)) return false;
        return true;
    }

    static int[] Crop(int[] px, int w, int x0, int y0, int cw, int ch)
    {
        var r = new int[cw * ch];
        for (int y = 0; y < ch; y++) Array.Copy(px, (y0 + y) * w + x0, r, y * cw, cw);
        return r;
    }

    // Undoes a whole-number enlargement, across and down separately (RECOIL shows C64 multicolour pixels twice as
    // wide; screenshots are often 2x or 3x): repeated until nothing more can be undone, so every copy ends the same.
    static int[] Reduce(int[] px, ref int w, ref int h)
    {
        for (int round = 0; round < 6; round++)
        {
            int fx = 1, fy = 1;
            for (int f = 8; f >= 2 && fx == 1; f--) if (w % f == 0 && w / f >= 8 && RunsAcross(px, w, h, f)) fx = f;
            for (int f = 8; f >= 2 && fy == 1; f--) if (h % f == 0 && h / f >= 8 && RunsDown(px, w, h, f)) fy = f;
            if (fx == 1 && fy == 1) break;
            int nw = w / fx, nh = h / fy;
            var r = new int[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++) r[y * nw + x] = px[(y * fy) * w + x * fx];
            px = r; w = nw; h = nh;
        }
        return px;
    }

    static bool RunsAcross(int[] px, int w, int h, int f)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x += f)
            {
                int c = px[row + x];
                for (int k = 1; k < f; k++) if (px[row + x + k] != c) return false;
            }
        }
        return true;
    }

    static bool RunsDown(int[] px, int w, int h, int f)
    {
        for (int y = 0; y < h; y += f)
            for (int k = 1; k < f; k++)
            {
                int a = y * w, b = (y + k) * w;
                for (int x = 0; x < w; x++) if (px[a + x] != px[b + x]) return false;
            }
        return true;
    }

    // the part (x, y, w, h) of b shrunk to 32 x 32, as RGB floats (transparency over black)
    static float[] Small(Bitmap b, int x, int y, int w, int h)
    {
        var r = new float[32 * 32 * 3];
        using (var s = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(s))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(b, new Rectangle(0, 0, 32, 32), x, y, w, h, GraphicsUnit.Pixel, ia);
            }
            int[] px = Pixels(s);
            for (int i = 0; i < px.Length; i++)
            {
                r[i * 3] = (px[i] >> 16) & 255; r[i * 3 + 1] = (px[i] >> 8) & 255; r[i * 3 + 2] = px[i] & 255;
            }
        }
        return r;
    }

    // a square n x n picture (ch values per spot) in one of 8 orientations: k & 3 quarter turns, k & 4 mirrored first
    static float[] Orient(float[] src, int n, int chn, int k)
    {
        var r = new float[src.Length];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int sx, sy;
                Map(k, x, y, n, out sx, out sy);
                for (int c = 0; c < chn; c++) r[(y * n + x) * chn + c] = src[(sy * n + sx) * chn + c];
            }
        return r;
    }

    static void Map(int k, int x, int y, int n, out int sx, out int sy)
    {
        if ((k & 4) != 0) x = n - 1 - x;
        switch (k & 3)
        {
            case 1: sx = y; sy = n - 1 - x; break;
            case 2: sx = n - 1 - x; sy = n - 1 - y; break;
            case 3: sx = n - 1 - y; sy = x; break;
            default: sx = x; sy = y; break;
        }
    }

    static float[][] dct;                                    // [u][x] = cos((2x + 1) u pi / 64), u < 8

    // pHash: the 8 x 8 lowest frequencies of the 32 x 32 grey copy, each above or below their median
    static ulong PHash(float[] g)
    {
        float[][] c = dct;
        if (c == null)
        {
            c = new float[8][];
            for (int u = 0; u < 8; u++)
            {
                c[u] = new float[32];
                for (int x = 0; x < 32; x++) c[u][x] = (float)Math.Cos((2 * x + 1) * u * Math.PI / 64);
            }
            dct = c;
        }
        var rows = new float[32 * 8];
        for (int y = 0; y < 32; y++)
            for (int u = 0; u < 8; u++)
            {
                float s = 0;
                for (int x = 0; x < 32; x++) s += g[y * 32 + x] * c[u][x];
                rows[y * 8 + u] = s;
            }
        var f = new float[64];
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                float s = 0;
                for (int y = 0; y < 32; y++) s += rows[y * 8 + u] * c[v][y];
                f[v * 8 + u] = s;
            }
        var sorted = new float[63];
        Array.Copy(f, 1, sorted, 0, 63);                     // the median leaves out the average brightness (f[0])
        Array.Sort(sorted);
        float med = sorted[31];
        ulong bits = 0;
        for (int i = 1; i < 64; i++) if (f[i] > med) bits |= 1UL << i;
        return bits;
    }

    // dHash: a 9 x 8 grey copy (sampled from the 32 x 32 one); is each spot darker than the one to its right?
    static ulong DHash(float[] g)
    {
        var s = new float[9 * 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 9; x++)
            {
                float fx = (x + 0.5f) * 32f / 9f - 0.5f, fy = (y + 0.5f) * 4f - 0.5f;
                int x0 = Math.Max(0, Math.Min(31, (int)Math.Floor(fx))), y0 = Math.Max(0, Math.Min(31, (int)Math.Floor(fy)));
                int x1 = Math.Min(31, x0 + 1), y1 = Math.Min(31, y0 + 1);
                float ax = Math.Max(0, Math.Min(1, fx - x0)), ay = Math.Max(0, Math.Min(1, fy - y0));
                s[y * 9 + x] = g[y0 * 32 + x0] * (1 - ax) * (1 - ay) + g[y0 * 32 + x1] * ax * (1 - ay) + g[y1 * 32 + x0] * (1 - ax) * ay + g[y1 * 32 + x1] * ax * ay;
            }
        ulong bits = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (s[y * 9 + x] < s[y * 9 + x + 1]) bits |= 1UL << (y * 8 + x);
        return bits;
    }

    static readonly byte[] Pop16 = MakePop();

    static byte[] MakePop()
    {
        var p = new byte[65536];
        for (int i = 1; i < 65536; i++) p[i] = (byte)(p[i >> 1] + (i & 1));
        return p;
    }

    static int Bits(ulong v)
    {
        return Pop16[(int)(v & 0xFFFF)] + Pop16[(int)((v >> 16) & 0xFFFF)] + Pop16[(int)((v >> 32) & 0xFFFF)] + Pop16[(int)(v >> 48)];
    }

    // ---------------------------------------------------------------- comparing

    sealed class Edge
    {
        public int A, B;             // indexes into the file list
        public int Alike;            // percent
        public bool Colours;         // alike, but in other colours
        public int Orientation;      // B turned / mirrored to match A
        public bool MaybeSame;       // a large picture that looks exactly alike: compared pixel by pixel
    }

    // how far apart two fingerprints (pHash, dHash) and the 16 x 16 copies (grey, colour) may be: strict, normal, loose
    static readonly int[] MaxP = { 4, 8, 12 }, MaxD = { 6, 10, 14 };
    static readonly float[] MaxGrey = { 6f, 10f, 16f };

    static List<Edge> Similar(List<DupFile> files, List<int> roots, int strength, Func<bool> stop)
    {
        int sIdx = Math.Max(0, Math.Min(2, strength));
        int maxP = MaxP[sIdx], maxD = MaxD[sIdx];
        float maxGrey = MaxGrey[sIdx];
        var edges = new List<Edge>();
        var lnA = new double[roots.Count];
        for (int i = 0; i < roots.Count; i++) lnA[i] = Math.Log(Math.Max(1, files[roots[i]].TW) / (double)Math.Max(1, files[roots[i]].TH));
        for (int i = 0; i < roots.Count; i++)
        {
            if ((i & 63) == 0 && stop()) return null;
            DupFile a = files[roots[i]];
            ulong pa = a.PHash[0], da = a.DHash[0];
            for (int j = i + 1; j < roots.Count; j++)
            {
                DupFile b = files[roots[j]];
                int best = -1, bestBits = 99;
                for (int k = 0; k < 8; k++)
                {
                    double shape = (k & 1) == 0 ? lnA[i] - lnA[j] : lnA[i] + lnA[j];      // turned on its side: width and height swap
                    if (Math.Abs(shape) > 0.12) continue;
                    int bp = Bits(pa ^ b.PHash[k]);
                    if (bp > maxP || Bits(da ^ b.DHash[k]) > maxD) continue;
                    if (bp < bestBits) { bestBits = bp; best = k; }
                }
                if (best < 0) continue;
                float grey, colour;
                TinyDiff(a.Tiny, b.Tiny, best, out grey, out colour);
                if (grey > maxGrey) continue;
                var e = new Edge { A = roots[i], B = roots[j], Orientation = best };
                e.Colours = colour - grey > 12f;
                e.Alike = Math.Max(0, Math.Min(100, (int)Math.Round(100 - (e.Colours ? grey : colour) * 100 / 255.0)));
                e.MaybeSame = best == 0 && bestBits == 0 && colour <= 1.5f && a.PixHash == null && b.PixHash == null && a.W == b.W && a.H == b.H;
                edges.Add(e);
            }
        }
        return edges;
    }

    // mean differences of two 16 x 16 copies (b in orientation k): of their brightness, and of their colours
    static void TinyDiff(byte[] a, byte[] b, int k, out float grey, out float colour)
    {
        double sg = 0, sc = 0;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int sx, sy;
                Map(k, x, y, 16, out sx, out sy);
                int ia = (y * 16 + x) * 3, ib = (sy * 16 + sx) * 3;
                float ga = 0.299f * a[ia] + 0.587f * a[ia + 1] + 0.114f * a[ia + 2];
                float gb = 0.299f * b[ib] + 0.587f * b[ib + 1] + 0.114f * b[ib + 2];
                sg += Math.Abs(ga - gb);
                sc += (Math.Abs(a[ia] - b[ib]) + Math.Abs(a[ia + 1] - b[ib + 1]) + Math.Abs(a[ia + 2] - b[ib + 2])) / 3.0;
            }
        grey = (float)(sg / 256); colour = (float)(sc / 256);
    }

    // two large pictures decoded in full and compared pixel by pixel
    static bool SamePixels(string a, string b)
    {
        try
        {
            using (Bitmap x = Exporter.Decode(a, 0))
            using (Bitmap y = Exporter.Decode(b, 0))
            {
                if (x.Width != y.Width || x.Height != y.Height) return false;
                int[] p = Pixels(x), q = Pixels(y);
                for (int i = 0; i < p.Length; i++)
                    if (p[i] != q[i] && (((uint)p[i] >> 24) != 0 || ((uint)q[i] >> 24) != 0)) return false;
                return true;
            }
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- groups

    // the better of two pictures to keep (negative: a is better), and the reason
    static int Better(DupFile a, DupFile b, out string why)
    {
        why = "the original retro file";
        if ((a.Platform != null) != (b.Platform != null)) return a.Platform != null ? -1 : 1;
        why = "the largest picture";
        long pa = (long)a.W * a.H, pb = (long)b.W * b.H;
        if (pa != pb) return pa > pb ? -1 : 1;
        why = "lossless";
        if (a.Lossless != b.Lossless) return a.Lossless ? -1 : 1;
        why = "has your marks";
        int ma = MarkScore(a.Mark), mb = MarkScore(b.Mark);
        if (ma != mb) return ma > mb ? -1 : 1;
        why = "the largest file";
        if (a.Size != b.Size) return a.Size > b.Size ? -1 : 1;
        why = "the oldest";
        if (a.Modified != b.Modified) return a.Modified < b.Modified ? -1 : 1;
        why = "first by name";
        return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
    }

    static int MarkScore(Mark m) { return m == null ? 0 : m.Stars * 4 + (m.Tag ? 2 : 0) + (m.Label > 0 ? 1 : 0); }

    static List<DupGroup> Groups(List<DupFile> files, int[] uf, List<Edge> edges)
    {
        // clusters of the same picture, best first in each
        var clusters = new Dictionary<int, List<DupFile>>();
        for (int i = 0; i < files.Count; i++)
        {
            int r = Find(uf, i);
            List<DupFile> l;
            if (!clusters.TryGetValue(r, out l)) clusters[r] = l = new List<DupFile>();
            l.Add(files[i]);
        }
        string tmp;
        foreach (List<DupFile> l in clusters.Values) l.Sort(delegate (DupFile a, DupFile b) { return Better(a, b, out tmp); });

        // similar clusters: the best edge between each pair of clusters
        var near = new Dictionary<int, Dictionary<int, Edge>>();
        foreach (Edge e in edges)
        {
            int a = Find(uf, e.A), b = Find(uf, e.B);
            if (a == b) continue;
            Link(near, a, b, e); Link(near, b, a, e);
        }

        // greedy, best cluster first: each takes the clusters still free that are like it
        var order = new List<int>(clusters.Keys);
        order.Sort(delegate (int a, int b) { return Better(clusters[a][0], clusters[b][0], out tmp); });
        var taken = new HashSet<int>();
        var groups = new List<DupGroup>();
        foreach (int c in order)
        {
            if (taken.Contains(c)) continue;
            taken.Add(c);
            var g = new DupGroup();
            List<DupFile> head = clusters[c];
            DupFile keep = head[0];
            foreach (DupFile f in head) { g.Files.Add(f); g.How.Add(f == keep ? "keep" : Relation(keep, f)); }
            Dictionary<int, Edge> mine;
            if (near.TryGetValue(c, out mine))
            {
                var others = new List<int>(mine.Keys);
                others.Sort(delegate (int a, int b) { return mine[b].Alike.CompareTo(mine[a].Alike); });
                foreach (int d in others)
                {
                    if (taken.Contains(d)) continue;
                    taken.Add(d);
                    Edge e = mine[d];
                    string how = e.Alike + "% alike" + (e.Colours ? ", other colours" : "") + (e.Orientation != 0 ? ", turned or mirrored" : "");
                    foreach (DupFile f in clusters[d]) { g.Files.Add(f); g.How.Add(how); }
                    g.Similar = true;
                }
            }
            if (g.Files.Count < 2) continue;
            string why = "the only one";
            if (g.Files.Count > 1)
            {
                DupFile second = null;
                for (int i = 1; i < g.Files.Count; i++) { string w; if (second == null || Better(g.Files[i], second, out w) < 0) second = g.Files[i]; }
                Better(keep, second, out why);
            }
            g.KeepWhy = why;
            groups.Add(g);
        }
        // the biggest waste of space first
        groups.Sort(delegate (DupGroup a, DupGroup b)
        {
            long wa = 0, wb = 0;
            for (int i = 1; i < a.Files.Count; i++) wa += a.Files[i].Size;
            for (int i = 1; i < b.Files.Count; i++) wb += b.Files[i].Size;
            return wb.CompareTo(wa);
        });
        return groups;
    }

    static void Link(Dictionary<int, Dictionary<int, Edge>> near, int a, int b, Edge e)
    {
        Dictionary<int, Edge> m;
        if (!near.TryGetValue(a, out m)) near[a] = m = new Dictionary<int, Edge>();
        Edge old;
        if (!m.TryGetValue(b, out old) || old.Alike < e.Alike) m[b] = e;
    }

    // how a picture of the same cluster matches the one to keep
    static string Relation(DupFile keep, DupFile f)
    {
        if (keep.FileHash != null && f.FileHash != null && Same(keep.FileHash, f.FileHash)) return "identical file";
        if (keep.PixHash != null && f.PixHash != null && Same(keep.PixHash, f.PixHash)) return "same pixels, another file";
        return keep.PixHash != null && f.PixHash != null ? "same picture, other size or border" : "same pixels, another file";
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ---------------------------------------------------------------- the options dialog

    static DupOptions last;

    // what to search and how; folder = the open folder (suggested); null = cancelled
    public static DupOptions Ask(IWin32Window owner, string folder, Action<Form> darkTitle)
    {
        Color panelBg = Color.FromArgb(38, 38, 38), text = Color.Gainsboro, boxBg = Color.FromArgb(52, 52, 52);
        var o = new DupOptions();
        if (last != null) { o.Subfolders = last.Subfolders; o.Mode = last.Mode; o.Strength = last.Strength; o.Folders.AddRange(last.Folders); }
        if (o.Folders.Count == 0 && folder != null) o.Folders.Add(folder);
        using (var f = new Form())
        {
            f.AutoScaleMode = AutoScaleMode.None;
            f.Font = new Font("Segoe UI", 9.5f);
            f.Text = "Find duplicates";
            f.StartPosition = FormStartPosition.CenterParent;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.ShowInTaskbar = false; f.MinimizeBox = false; f.MaximizeBox = false;
            f.BackColor = panelBg; f.ForeColor = text;
            int pad = Util.S(14), w = Util.S(520);
            if (darkTitle != null) f.HandleCreated += delegate { darkTitle(f); };
            int y = pad;
            Func<string, int, Label> label = delegate (string s, int h)
            {
                var l = new Label { Left = pad, Top = y, Width = w, Height = h, Text = s, ForeColor = Color.Silver };
                f.Controls.Add(l);
                y += h + Util.S(4);
                return l;
            };
            label("Finds pictures that are the same, or alike, in these folders. Nothing is changed: you choose what happens " +
                  "to them afterwards. Cloud-only files are skipped.", Util.S(40));
            var folders = new ListBox { Left = pad, Top = y, Width = w - Util.S(110), Height = Util.S(110), BackColor = boxBg, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
            foreach (string d in o.Folders) folders.Items.Add(d);
            f.Controls.Add(folders);
            Func<string, int, Button> button = delegate (string s, int top)
            {
                var b = new Button { Text = s, Left = pad + w - Util.S(100), Top = top, Width = Util.S(100), Height = Util.S(28), FlatStyle = FlatStyle.Flat, BackColor = boxBg, ForeColor = text };
                b.FlatAppearance.BorderSize = 0;
                f.Controls.Add(b);
                return b;
            };
            Button add = button("Add folder...", y), remove = button("Remove", y + Util.S(34));
            add.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "Add a folder to search for duplicates (local drives only).";
                    d.ShowNewFolderButton = false;
                    if (folders.Items.Count > 0) d.SelectedPath = (string)folders.Items[folders.Items.Count - 1];
                    if (d.ShowDialog(f) != DialogResult.OK) return;
                    string why = LocalOnly(d.SelectedPath);
                    if (why != null) { MessageBox.Show(f, why, "Find duplicates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    foreach (object it in folders.Items) if (Util.SameDir((string)it, d.SelectedPath)) return;
                    folders.Items.Add(d.SelectedPath);
                }
            };
            remove.Click += delegate { if (folders.SelectedIndex >= 0) folders.Items.RemoveAt(folders.SelectedIndex); };
            y += folders.Height + Util.S(8);
            var subs = new CheckBox { Left = pad, Top = y, Width = w, Height = Util.S(24), Text = "Include subfolders (up to " + MaxFiles.ToString("N0") + " pictures in all)", Checked = o.Subfolders, FlatStyle = FlatStyle.Flat, ForeColor = text };
            f.Controls.Add(subs);
            y += Util.S(32);
            label("Find", Util.S(20));
            var modeGroup = new Panel { Left = 0, Top = y, Width = w + pad, Height = Util.S(76), BackColor = panelBg };
            f.Controls.Add(modeGroup);
            string[] modes =
            {
                "Identical files only (fastest: the same bytes)",
                "Also the same picture in other files (same pixels; retro screenshots enlarged or with a border)",
                "Also similar pictures (resized, saved again, turned, slightly changed)"
            };
            var rModes = new RadioButton[3];
            for (int i = 0; i < 3; i++)
            {
                rModes[i] = new RadioButton { Left = pad + Util.S(6), Top = i * Util.S(24), Width = w - Util.S(6), Height = Util.S(22), Text = modes[i], Checked = o.Mode == i, FlatStyle = FlatStyle.Flat, ForeColor = text };
                modeGroup.Controls.Add(rModes[i]);
            }
            y += Util.S(82);
            var strengthLab = new Label { Left = pad + Util.S(26), Top = y, Width = Util.S(130), Height = Util.S(26), Text = "How alike:", TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver };
            var strength = new DarkCombo { Left = pad + Util.S(160), Top = y, Width = Util.S(300), Height = Util.S(26), BackColor = boxBg, ForeColor = Color.White };
            strength.SetItems("Strict (nearly the same)", "Normal", "Loose (more matches, check them)");
            strength.SelectedIndex = o.Strength;
            f.Controls.Add(strengthLab); f.Controls.Add(strength);
            Action sync = delegate { strength.Enabled = rModes[2].Checked; strengthLab.Enabled = rModes[2].Checked; };
            foreach (RadioButton r in rModes) r.CheckedChanged += delegate { sync(); };
            sync();
            y += Util.S(40);

            var ok = new Button { Text = "Find", DialogResult = DialogResult.OK, Width = Util.S(96), Height = Util.S(30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 130, 230), ForeColor = Color.White };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = Util.S(96), Height = Util.S(30), FlatStyle = FlatStyle.Flat, BackColor = boxBg, ForeColor = text };
            ok.FlatAppearance.BorderSize = 0; cancel.FlatAppearance.BorderSize = 0;
            f.ClientSize = new Size(w + pad * 2, y + ok.Height + pad);
            cancel.Left = f.ClientSize.Width - pad - cancel.Width; ok.Left = cancel.Left - Util.S(8) - ok.Width;
            ok.Top = cancel.Top = y;
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (f.DialogResult != DialogResult.OK) return;
                if (folders.Items.Count == 0)
                {
                    MessageBox.Show(f, "Add at least one folder to search.", "Find duplicates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
                foreach (object it in folders.Items)
                {
                    string why = Directory.Exists((string)it) ? LocalOnly((string)it) : "This folder isn't there any more:\n" + it;
                    if (why != null) { MessageBox.Show(f, why, "Find duplicates", MessageBoxButtons.OK, MessageBoxIcon.Warning); e.Cancel = true; return; }
                }
            };
            if (f.ShowDialog(owner) != DialogResult.OK) return null;
            o.Folders.Clear();
            foreach (object it in folders.Items) o.Folders.Add((string)it);
            o.Subfolders = subs.Checked;
            for (int i = 0; i < 3; i++) if (rModes[i].Checked) o.Mode = i;
            o.Strength = Math.Max(0, strength.SelectedIndex);
            last = o;
            return o;
        }
    }

    // null if RetroGleek may search there (local drives only), otherwise why not
    static string LocalOnly(string dir)
    {
        try
        {
            if (dir.StartsWith("\\\\")) return "Network locations are not searched (RetroGleek works with local drives only).";
            string root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network) return "Network drives are not searched (local drives only).";
        }
        catch (Exception ex) { return ex.Message; }
        return null;
    }
}

// ====================================================================== the results window

// Shows the groups found, each with the picture suggested to keep first (in bold, unticked) and why; the others
// ticked. You can change the ticks, keep another one, compare two side by side, open one, or show it in its folder.
// Then: tag the ticked pictures (Edit > Select tagged takes over), or move them to the Recycle Bin (asks first, and
// never every picture of a group). A window of its own: the main window stays usable meanwhile.
sealed class DupForm : Form
{
    public Action<string> OpenPicture, ShowInFolder;
    public Action<string, string> Compare;
    public Action MarksChanged;
    public Action<List<string>> FilesDeleted;

    readonly DupOptions opts;
    readonly ListView list;
    readonly ImageList thumbs;
    readonly Label head;
    readonly Button stopBtn, keepBtn, compareBtn, showBtn, tagBtn, deleteBtn;
    volatile bool stopping;
    bool busy = true;
    List<DupGroup> groups;
    readonly Font bold;
    readonly int thumbSize;

    public DupForm(DupOptions o, Action<Form> darkTitle)
    {
        opts = o;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9.5f);
        bold = new Font(Font, FontStyle.Bold);
        Text = "Find duplicates - RetroGleek";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(38, 38, 38); ForeColor = Color.Gainsboro;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(Util.S(1100), wa.Width * 9 / 10), Math.Min(Util.S(700), wa.Height * 9 / 10));
        MinimumSize = new Size(Util.S(640), Util.S(360));
        KeyPreview = true;
        if (darkTitle != null) HandleCreated += delegate { darkTitle(this); };
        Color boxBg = Color.FromArgb(52, 52, 52);

        thumbSize = Util.S(48);
        thumbs = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(thumbSize, thumbSize) };
        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false,
            ShowGroups = true, SmallImageList = thumbs, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(32, 32, 32), ForeColor = Color.Gainsboro
        };
        Util.Dark(list);
        foreach (string[] c in new[] { new[] { "Picture", "230" }, new[] { "Match", "200" }, new[] { "Size", "90" }, new[] { "File", "80" },
                                       new[] { "Marks", "90" }, new[] { "Modified", "120" }, new[] { "Folder", "320" } })
            list.Columns.Add(c[0], Util.S(int.Parse(c[1])));
        list.ItemChecked += delegate { if (!busy) UpdateHead(); };
        list.SelectedIndexChanged += delegate { UpdateButtons(); };
        list.DoubleClick += delegate { DupFile f = Selected(); if (f != null && OpenPicture != null) OpenPicture(f.Path); };

        head = new Label { Dock = DockStyle.Top, Height = Util.S(52), Padding = new Padding(Util.S(10), Util.S(6), Util.S(10), 0), ForeColor = Color.Gainsboro, Text = "Starting..." };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Util.S(46), Padding = new Padding(Util.S(8), Util.S(8), Util.S(8), 0), BackColor = Color.FromArgb(38, 38, 38), WrapContents = false };
        Func<string, Color, Button> button = delegate (string s, Color back)
        {
            var b = new Button { Text = s, AutoSize = true, Height = Util.S(30), FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White, Margin = new Padding(0, 0, Util.S(8), 0) };
            b.FlatAppearance.BorderSize = 0;
            bar.Controls.Add(b);
            return b;
        };
        stopBtn = button("Stop", boxBg);
        keepBtn = button("Keep this one", boxBg);
        compareBtn = button("Compare the two selected", boxBg);
        showBtn = button("Show in its folder", boxBg);
        tagBtn = button("Tag the ticked", Color.FromArgb(60, 130, 230));
        deleteBtn = button("Move the ticked to the Recycle Bin...", Color.FromArgb(150, 70, 60));
        Button close = button("Close", boxBg);
        stopBtn.Click += delegate { stopping = true; stopBtn.Enabled = false; head.Text = "Stopping..."; };
        keepBtn.Click += delegate { KeepSelected(); };
        compareBtn.Click += delegate
        {
            if (list.SelectedItems.Count != 2 || Compare == null) return;
            Compare(((DupFile)list.SelectedItems[0].Tag).Path, ((DupFile)list.SelectedItems[1].Tag).Path);
        };
        showBtn.Click += delegate { DupFile f = Selected(); if (f != null && ShowInFolder != null) ShowInFolder(f.Path); };
        tagBtn.Click += delegate { TagChecked(); };
        deleteBtn.Click += delegate { DeleteChecked(); };
        close.Click += delegate { Close(); };
        KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

        Controls.Add(list);                                 // fill first, edge-docked after
        Controls.Add(bar);
        Controls.Add(head);
        UpdateButtons();
        Shown += delegate { Start(); };
        FormClosing += delegate { stopping = true; };
    }

    DupFile Selected() { return list.SelectedItems.Count == 1 ? (DupFile)list.SelectedItems[0].Tag : null; }

    void UpdateButtons()
    {
        bool done = !busy && groups != null;
        stopBtn.Visible = busy;
        keepBtn.Enabled = done && list.SelectedItems.Count == 1;
        compareBtn.Enabled = done && list.SelectedItems.Count == 2;
        showBtn.Enabled = done && list.SelectedItems.Count == 1;
        tagBtn.Enabled = deleteBtn.Enabled = done && list.CheckedItems.Count > 0;
    }

    void Start()
    {
        DupOptions o = opts;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<DupGroup> found = null;
            int pictures = 0, skipped = 0;
            bool truncated = false;
            List<string> unreadable = null;
            string err = null;
            int lastPing = 0;
            try
            {
                found = DupFinder.Find(o, delegate (string s)
                {
                    if (Environment.TickCount - lastPing < 150) return;
                    lastPing = Environment.TickCount;
                    Util.Post(this, delegate { if (busy && !stopping) head.Text = s; });
                }, delegate { return stopping; }, out pictures, out skipped, out truncated, out unreadable);
            }
            catch (Exception ex) { err = Util.Why(ex); }
            int p2 = pictures, s2 = skipped;
            bool t2 = truncated;
            List<string> u2 = unreadable;
            Util.Post(this, delegate { Finished(found, p2, s2, t2, u2, err); });
        });
    }

    string summary;

    void Finished(List<DupGroup> found, int pictures, int skipped, bool truncated, List<string> unreadable, string err)
    {
        busy = false;
        if (err != null) { head.Text = "The search stopped: " + err; UpdateButtons(); return; }
        if (found == null) { head.Text = "Stopped. Nothing was changed."; UpdateButtons(); return; }
        groups = found;
        busy = true;                                       // no counting while the list fills
        list.BeginUpdate();
        try
        {
            int n = 0;
            foreach (DupGroup g in groups)
            {
                n++;
                var lg = new ListViewGroup("Group " + n + ": " + g.Files.Count + " pictures" + (g.Similar ? " (some only alike)" : "") +
                                           "   -   keep " + g.Files[0].Name + " (" + g.KeepWhy + ")");
                list.Groups.Add(lg);
                for (int i = 0; i < g.Files.Count; i++)
                {
                    DupFile f = g.Files[i];
                    var it = new ListViewItem(f.Name, lg) { Tag = f, Checked = i != 0 };
                    if (i == 0) it.Font = bold;
                    it.SubItems.Add(i == 0 ? "keep: " + g.KeepWhy : g.How[i]);
                    it.SubItems.Add(f.W > 0 ? f.W + " x " + f.H : "");
                    it.SubItems.Add(Util.FmtSize(f.Size));
                    it.SubItems.Add(Marks.Describe(f.Mark) == "no marks" ? "" : Marks.Describe(f.Mark));
                    it.SubItems.Add(new DateTime(f.Modified, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    it.SubItems.Add(Path.GetDirectoryName(f.Path));
                    list.Items.Add(it);
                }
            }
        }
        finally { list.EndUpdate(); busy = false; }
        summary = pictures.ToString("N0") + " pictures searched" + (truncated ? " (stopped at " + DupFinder.MaxFiles.ToString("N0") + ")" : "") +
                  (skipped > 0 ? ", " + skipped + " cloud-only skipped" : "") +
                  (unreadable != null && unreadable.Count > 0 ? ", " + unreadable.Count + " could not be read (only identical copies found for those)" : "") + ".";
        if (groups.Count == 0) head.Text = summary + "\nNo duplicates found.";
        UpdateHead();
        UpdateButtons();
        MakeThumbs();
    }

    void UpdateHead()
    {
        if (groups == null || groups.Count == 0) { UpdateButtons(); return; }
        long bytes = 0;
        foreach (ListViewItem it in list.CheckedItems) bytes += ((DupFile)it.Tag).Size;
        int pics = list.Items.Count;
        head.Text = summary + "  " + groups.Count + (groups.Count == 1 ? " group, " : " groups, ") + pics + " pictures.\n" +
                    "Ticked: " + list.CheckedItems.Count + " (" + Util.FmtSize(bytes) + "). The one to keep is in bold. Double-click opens a picture; nothing changes until you tag or move.";
        UpdateButtons();
    }

    // thumbnails in the background, as RetroGleek makes them for the browser
    void MakeThumbs()
    {
        var items = new List<KeyValuePair<ListViewItem, string>>();
        foreach (ListViewItem it in list.Items) items.Add(new KeyValuePair<ListViewItem, string>(it, ((DupFile)it.Tag).Path));
        int size = thumbSize;
        ThreadPool.QueueUserWorkItem(delegate
        {
            foreach (KeyValuePair<ListViewItem, string> kv in items)
            {
                if (stopping || IsDisposed) return;
                Bitmap sq = null;
                try
                {
                    ThumbResult r = ThumbService.Make(kv.Value, size);
                    if (r.Bmp != null)
                        using (r.Bmp)
                        {
                            sq = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                            using (Graphics g = Graphics.FromImage(sq))
                            {
                                g.Clear(Color.FromArgb(32, 32, 32));
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g.DrawImage(r.Bmp, (size - r.Bmp.Width) / 2, (size - r.Bmp.Height) / 2, r.Bmp.Width, r.Bmp.Height);
                            }
                        }
                }
                catch (Exception) { }
                if (sq == null) continue;
                Bitmap b = sq;
                ListViewItem it = kv.Key;
                if (!Util.Post(this, delegate
                {
                    if (IsDisposed) { b.Dispose(); return; }
                    thumbs.Images.Add(b);                  // the image list owns it from now on
                    it.ImageIndex = thumbs.Images.Count - 1;
                })) b.Dispose();
            }
        });
    }

    // the selected picture becomes the one to keep in its group: unticked and bold, the others ticked
    void KeepSelected()
    {
        if (list.SelectedItems.Count != 1) return;
        ListViewItem keep = list.SelectedItems[0];
        ListViewGroup g = keep.Group;
        if (g == null) return;
        busy = true;
        try
        {
            foreach (ListViewItem it in g.Items)
            {
                it.Checked = it != keep;
                it.Font = it == keep ? bold : Font;
            }
        }
        finally { busy = false; }
        UpdateHead();
    }

    List<string> CheckedPaths()
    {
        var r = new List<string>();
        foreach (ListViewItem it in list.CheckedItems) r.Add(((DupFile)it.Tag).Path);
        return r;
    }

    void TagChecked()
    {
        List<string> paths = CheckedPaths().FindAll(File.Exists);
        if (paths.Count == 0) return;
        Marks.Set(paths, -1, -1, true);
        if (MarksChanged != null) MarksChanged();
        head.Text = "Tagged " + paths.Count + (paths.Count == 1 ? " picture" : " pictures") + ". In the browser, Edit > Select tagged selects them, " +
                    "ready to move, copy or delete; the Show pull-down can list only them.";
    }

    void DeleteChecked()
    {
        // never every picture of a group
        foreach (ListViewGroup g in list.Groups)
        {
            if (g.Items.Count == 0) continue;
            bool any = false;
            foreach (ListViewItem it in g.Items) if (!it.Checked) any = true;
            if (!any)
            {
                MessageBox.Show(this, "Every picture of \"" + g.Header + "\" is ticked. Untick at least one in each group, so one copy is kept.",
                                "Find duplicates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        List<string> paths = CheckedPaths();
        if (paths.Count == 0) return;
        if (MessageBox.Show(this, "Move the " + paths.Count + " ticked " + (paths.Count == 1 ? "picture" : "pictures") + " to the Recycle Bin?",
                            "Find duplicates", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var gone = new List<string>();
        var errs = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (string p in paths)
            {
                try
                {
                    if (File.Exists(p)) VBFS.DeleteFile(p, VBUI.OnlyErrorDialogs, VBRecycle.SendToRecycleBin);
                    if (!File.Exists(p)) gone.Add(p);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errs.Add(Path.GetFileName(p) + ": " + ex.Message); }
            }
        }
        finally { Cursor = Cursors.Default; }
        var goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
        busy = true;
        list.BeginUpdate();
        try
        {
            for (int i = list.Items.Count - 1; i >= 0; i--)
                if (goneSet.Contains(((DupFile)list.Items[i].Tag).Path)) list.Items.RemoveAt(i);
            foreach (ListViewGroup g in list.Groups)          // a group with one picture left is done
                if (g.Items.Count == 1) g.Items[0].Checked = false;
        }
        finally { list.EndUpdate(); busy = false; }
        if (FilesDeleted != null && gone.Count > 0) FilesDeleted(gone);
        UpdateHead();
        head.Text = "Moved " + gone.Count + (gone.Count == 1 ? " picture" : " pictures") + " to the Recycle Bin.\n" + head.Text;
        if (errs.Count > 0)
        {
            if (errs.Count > 8) { errs = errs.GetRange(0, 8); errs.Add("..."); }
            MessageBox.Show(this, "Some pictures could not be moved:\n\n" + string.Join("\n", errs), "Find duplicates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { thumbs.Dispose(); bold.Dispose(); }
        base.Dispose(disposing);
    }
}
