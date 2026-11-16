// RetroGleek - lossless JPEG rotation (Edit > Rotate the file, Ctrl+Shift+R / Ctrl+Shift+L)
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Turns JPEG photos on disk without re-compressing them, so no quality is lost. Two ways, tried in this order:
//   1. The photo has an EXIF orientation tag: only that tag's value changes (2 bytes in the file); every viewer that
//      honours EXIF (Windows, browsers, RetroGleek) shows it turned. The picture data is not touched at all.
//   2. No orientation tag: GDI+'s lossless JPEG transform turns the compressed blocks themselves. That is only
//      lossless when the width and height are whole multiples of the JPEG block size (8 or 16 pixels); otherwise the
//      photo is left alone. The result is decoded and compared with the original turned in memory before it is kept.
// Either way the new file is written next to the original under a temporary name, checked, and only then put in
// its place (File.Replace). Anything that is not a JPEG, or can't be turned without loss, is skipped with a reason.
static class JpegRotate
{
    public static bool IsJpeg(string path)
    {
        string e = (Path.GetExtension(path) ?? "").ToLowerInvariant();
        return e == ".jpg" || e == ".jpeg" || e == ".jpe" || e == ".jfif";
    }

    // turns = clockwise quarter turns (1, 2 or 3). Throws with a readable reason when the file can't be turned losslessly.
    public static void Rotate(string path, int turns)
    {
        turns &= 3;
        if (turns == 0) return;
        if (!IsJpeg(path)) throw new IOException("only JPEG photos can be turned without loss.");
        if ((File.GetAttributes(path) & Util.CloudMask) != 0) throw new IOException("cloud-only, not stored on this PC.");
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("the file is read-only.");
        byte[] data = File.ReadAllBytes(path);
        string tmp = Path.Combine(Path.GetDirectoryName(path), "~rg" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
        try
        {
            int at, littleEndian;
            int orient = FindOrientation(data, out at, out littleEndian);
            if (orient >= 1 && orient <= 8)
            {
                int now = Compose(orient, turns);
                byte[] copy = (byte[])data.Clone();
                if (littleEndian == 1) { copy[at] = (byte)now; copy[at + 1] = 0; } else { copy[at] = 0; copy[at + 1] = (byte)now; }
                File.WriteAllBytes(tmp, copy);
                CheckOrientation(tmp, now);
            }
            else
            {
                int w, h, mw, mh;
                if (!Frame(data, out w, out h, out mw, out mh)) throw new IOException("its JPEG structure could not be read.");
                if (w % mw != 0 || h % mh != 0)
                    throw new IOException(w + " x " + h + " is not a whole number of " + mw + " x " + mh + " JPEG blocks and it has no orientation tag, so it can't be turned without loss.");
                Transform(data, tmp, turns);
                CheckPixels(data, tmp, turns);
            }
            File.Replace(tmp, path, null, true);           // keeps the original's creation date and attributes
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
    }

    // EXIF orientation after turning the shown picture by more clockwise quarter turns
    // (1, 6, 3, 8 = 0, 90, 180, 270 degrees; 2, 7, 4, 5 = mirrored, then 0, 90, 180, 270 degrees)
    static int Compose(int orient, int turns)
    {
        int[] plain = { 1, 6, 3, 8 }, mirrored = { 2, 7, 4, 5 };
        int r = Array.IndexOf(plain, orient);
        if (r >= 0) return plain[(r + turns) & 3];
        r = Array.IndexOf(mirrored, orient);
        return mirrored[((r < 0 ? 0 : r) + turns) & 3];
    }

    // the value of the EXIF orientation tag (0 = none) and where its 2 bytes are
    static int FindOrientation(byte[] d, out int at, out int little)
    {
        at = -1; little = 1;
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return 0;
        int p = 2;
        while (p + 4 <= d.Length && d[p] == 0xFF)
        {
            int marker = d[p + 1];
            if (marker == 0xD9 || marker == 0xDA) break;                     // end of image / start of the picture data
            int len = (d[p + 2] << 8) | d[p + 3];
            if (len < 2 || p + 2 + len > d.Length) break;
            if (marker == 0xE1 && len >= 16 && d[p + 4] == 'E' && d[p + 5] == 'x' && d[p + 6] == 'i' && d[p + 7] == 'f' && d[p + 8] == 0 && d[p + 9] == 0)
            {
                int t = p + 10;                                             // TIFF header
                int end = p + 2 + len;
                bool le = d[t] == 'I' && d[t + 1] == 'I';
                if (!le && !(d[t] == 'M' && d[t + 1] == 'M')) return 0;
                little = le ? 1 : 0;
                int ifd = t + (int)U32(d, t + 4, le);
                if (ifd < t || ifd + 2 > end) return 0;
                int n = U16(d, ifd, le);
                for (int i = 0; i < n; i++)
                {
                    int e = ifd + 2 + i * 12;
                    if (e + 12 > end) return 0;
                    if (U16(d, e, le) == 0x0112 && U16(d, e + 2, le) == 3)  // orientation, SHORT
                    {
                        at = e + 8;
                        return U16(d, at, le);
                    }
                }
                return 0;
            }
            p += 2 + len;
        }
        return 0;
    }

    static int U16(byte[] d, int i, bool le) { return le ? d[i] | (d[i + 1] << 8) : (d[i] << 8) | d[i + 1]; }
    static uint U32(byte[] d, int i, bool le)
    {
        return le ? (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24)) : (uint)((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]);
    }

    // width, height and the JPEG block (MCU) size from the frame header
    static bool Frame(byte[] d, out int w, out int h, out int mw, out int mh)
    {
        w = h = 0; mw = mh = 8;
        int p = 2;
        while (p + 4 <= d.Length && d[p] == 0xFF)
        {
            int marker = d[p + 1];
            if (marker == 0xD9 || marker == 0xDA) break;
            int len = (d[p + 2] << 8) | d[p + 3];
            if (len < 2 || p + 2 + len > d.Length) break;
            bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (sof && len >= 8)
            {
                h = (d[p + 5] << 8) | d[p + 6];
                w = (d[p + 7] << 8) | d[p + 8];
                int comps = d[p + 9], maxH = 1, maxV = 1;
                for (int c = 0; c < comps && p + 10 + c * 3 + 1 < d.Length; c++)
                {
                    int s = d[p + 10 + c * 3 + 1];
                    maxH = Math.Max(maxH, s >> 4); maxV = Math.Max(maxV, s & 15);
                }
                mw = 8 * maxH; mh = 8 * maxV;
                return w > 0 && h > 0;
            }
            p += 2 + len;
        }
        return false;
    }

    static void Transform(byte[] data, string dest, int turns)
    {
        ImageCodecInfo jpeg = null;
        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpeg = c;
        if (jpeg == null) throw new IOException("Windows' JPEG encoder is not available.");
        EncoderValue v = turns == 1 ? EncoderValue.TransformRotate90 : turns == 2 ? EncoderValue.TransformRotate180 : EncoderValue.TransformRotate270;
        using (var ms = new MemoryStream(data, false))
        using (Image img = Image.FromStream(ms, false, false))
        using (var ps = new EncoderParameters(1))
        {
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Transformation, (long)v);
            img.Save(dest, jpeg, ps);
        }
    }

    static void CheckOrientation(string path, int expected)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (Image img = Image.FromStream(fs, false, true))                // validates the data
        {
            byte[] v = img.GetPropertyItem(0x0112).Value;
            int o = (v.Length > 1 && v[0] == 0) ? v[1] : v[0];
            if (o != expected) throw new IOException("the new orientation could not be confirmed, so nothing was changed.");
        }
    }

    // The transformed photo must be the original turned: decoded side by side, they may differ only by the tiny
    // rounding of the JPEG decoder, never by re-compression
    static void CheckPixels(byte[] original, string path, int turns)
    {
        using (var ms = new MemoryStream(original, false))
        using (Image a = Image.FromStream(ms, false, true))
        using (var ba = new Bitmap(a))
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (Image b = Image.FromStream(fs, false, true))
        using (var bb = new Bitmap(b))
        {
            ba.RotateFlip(turns == 1 ? RotateFlipType.Rotate90FlipNone : turns == 2 ? RotateFlipType.Rotate180FlipNone : RotateFlipType.Rotate270FlipNone);
            if (ba.Width != bb.Width || ba.Height != bb.Height) throw new IOException("the turned photo has the wrong size, so nothing was changed.");
            int[] pa = Pixels(ba), pb = Pixels(bb);
            long sum = 0; int worst = 0;
            for (int i = 0; i < pa.Length; i++)
                for (int sh = 0; sh <= 16; sh += 8)
                {
                    int diff = Math.Abs(((pa[i] >> sh) & 255) - ((pb[i] >> sh) & 255));
                    sum += diff; if (diff > worst) worst = diff;
                }
            double mean = (double)sum / Math.Max(1, pa.Length * 3);
            if (mean > 0.5 || worst > 24) throw new IOException("the turned photo did not match the original closely enough (it would have lost quality), so nothing was changed.");
        }
    }

    static int[] Pixels(Bitmap b)
    {
        int[] px = new int[b.Width * b.Height];
        BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < b.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), px, y * b.Width, b.Width);
        }
        finally { b.UnlockBits(bd); }
        return px;
    }
}
