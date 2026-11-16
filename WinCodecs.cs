// RetroGleek - pictures decoded by the codecs installed in Windows (WebP, AVIF, HEIC, JPEG XL, JPEG XR, camera RAW)
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.
//
// Uses the Windows Imaging Component through WPF's imaging classes (PresentationCore.dll and WindowsBase.dll, part of
// the .NET Framework that ships with Windows). Nothing is bundled and there is no native import of our own: the
// decoders are Microsoft's, installed with Windows or as free extensions from the Microsoft Store, and Microsoft
// keeps them up to date. If a PC has no decoder for a format, the picture says which extension to install;
// RetroGleek never opens the Store or goes online itself.
//
// Also used for faster thumbnails of very large JPEG photos: Windows' JPEG decoder can decode straight to a half,
// a quarter or an eighth of the full size, which is much quicker than decoding every pixel and scaling down.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WI = System.Windows.Media.Imaging;
using WM = System.Windows.Media;

sealed class CodecPic
{
    public Bitmap Bmp;           // decoded (and, if asked, scaled) picture, not yet turned
    public int W, H;             // the picture's full size, before any scaling or turning
    public int Rot;              // quarter turns clockwise to honour its orientation tag (camera RAW and JPEG XR only)
    public int Stars;            // star rating stored in the file, 0 = none
    public int Frames = 1;       // frames in the file (an animated WebP shows its first frame)
    public string Format;        // e.g. "WebP (Windows codec)"
    public long Bytes;           // the file's size (it was read whole, so no second look at the file is needed)
    public List<string> Exif;    // readable camera details for the viewer's E panel (full-size decodes only), or null
}

static class WinCodec
{
    const long MaxPixels = 100000000L;

    // camera RAW files (Microsoft's Raw Image Extension). .raw and .rwl are left to RECOIL, which claims them.
    static readonly HashSet<string> RawExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".ori", ".rw2", ".raf",
          ".pef", ".srw", ".3fr", ".erf", ".kdc", ".dcr", ".mrw", ".x3f", ".iiq", ".mos", ".mef" };

    public static readonly HashSet<string> Exts = MakeExts();

    static HashSet<string> MakeExts()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".webp", ".avif", ".heic", ".heif", ".hif", ".jxl", ".jxr", ".wdp", ".hdp" };
        foreach (string e in RawExts) s.Add(e);
        return s;
    }

    public static bool Claims(string path) { return Exts.Contains(Path.GetExtension(path) ?? ""); }
    public static bool ClaimsExt(string ext) { return ext != null && Exts.Contains(ext); }
    public static bool IsRaw(string path) { return RawExts.Contains(Path.GetExtension(path) ?? ""); }

    // a short name for the format, e.g. "WebP", "HEIF", "Camera RAW (NEF)"
    public static string Name(string path)
    {
        string e = (Path.GetExtension(path) ?? "").ToLowerInvariant();
        switch (e)
        {
            case ".webp": return "WebP";
            case ".avif": return "AVIF";
            case ".heic": case ".heif": case ".hif": return "HEIF";
            case ".jxl": return "JPEG XL";
            case ".jxr": case ".wdp": case ".hdp": return "JPEG XR";
        }
        return "Camera RAW (" + e.TrimStart('.').ToUpperInvariant() + ")";
    }

    // what to install when Windows has no decoder for this file
    public static string Missing(string path)
    {
        string e = (Path.GetExtension(path) ?? "").ToLowerInvariant();
        string what;
        switch (e)
        {
            case ".webp": what = "\"WebP Image Extensions\""; break;
            case ".avif": what = "\"AV1 Video Extension\" (and \"HEIF Image Extensions\" if it isn't installed)"; break;
            case ".heic": case ".heif": case ".hif": what = "\"HEIF Image Extensions\" and \"HEVC Video Extensions\""; break;
            case ".jxl": what = "\"JPEG XL Image Extension\" (Windows 11 24H2 or later)"; break;
            case ".jxr": case ".wdp": case ".hdp": return "Windows' JPEG XR decoder is missing on this PC.";
            default: what = "\"Raw Image Extension\""; break;
        }
        return "Windows has no " + Name(path) + " decoder on this PC. Install " + what +
               " from the Microsoft Store, then open the picture again.";
    }

    // The decoding error as a readable message (a missing decoder names the extension to install).
    public static string Why(string path, Exception ex)
    {
        if (ex is NotSupportedException) return Missing(path);
        var ce = ex as COMException;
        if (ce != null && (uint)ce.ErrorCode == 0x88982F50u) return Missing(path);    // WINCODEC_ERR_COMPONENTNOTFOUND
        if (ex is FileFormatException || ex is ArgumentException) return "Damaged, or not really a " + Name(path) + " file.";
        if (ex is OutOfMemoryException) return "Too large to open.";
        return ex.Message;
    }

    // Decodes the first frame. maxSide > 0: decode straight to a picture no larger than maxSide x maxSide (much
    // faster for big photos); 0 = full size, and the camera details for the E panel are read too.
    // premultiplied: Format32bppPArgb (fastest to draw), else Format32bppArgb (exact pixels, for saving).
    // Throws on any problem (use Why for the message). Any thread.
    public static CodecPic Load(string path, int maxSide, bool premultiplied)
    {
        byte[] data = Util.ReadShared(path);
        var r = new CodecPic { Format = Name(path) + " (Windows codec)", Bytes = data.Length };
        using (var ms = new MemoryStream(data, false))         // open until the pixels are copied (decoded on demand)
        {
            WI.BitmapDecoder dec = WI.BitmapDecoder.Create(ms, WI.BitmapCreateOptions.IgnoreColorProfile |
                                                              WI.BitmapCreateOptions.PreservePixelFormat,
                                                           WI.BitmapCacheOption.None);
            if (dec.Frames.Count == 0) throw new FileFormatException("The file holds no picture.");
            WI.BitmapFrame f = dec.Frames[0];
            r.Frames = dec.Frames.Count;
            r.W = f.PixelWidth; r.H = f.PixelHeight;
            if (r.W <= 0 || r.H <= 0) throw new FileFormatException("The file holds no picture.");
            ReadMeta(path, f, r, maxSide <= 0);
            WI.BitmapSource src = f;
            if (maxSide > 0 && Math.Max(r.W, r.H) > maxSide)
                src = Scaled(data, r.W >= r.H, maxSide) ?? Shrink(f, maxSide);
            else if ((long)r.W * r.H > MaxPixels) throw new IOException("This picture is too large to open.");
            r.Bmp = ToBitmap(src, premultiplied);
        }
        return r;
    }

    // Scaled decoding: the decoder itself works at the smaller size where it can (JPEG, RAW previews ...).
    static WI.BitmapSource Scaled(byte[] data, bool wide, int maxSide)
    {
        try
        {
            var bi = new WI.BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(data, false);
            bi.CacheOption = WI.BitmapCacheOption.OnLoad;
            bi.CreateOptions = WI.BitmapCreateOptions.IgnoreColorProfile;
            if (wide) bi.DecodePixelWidth = maxSide; else bi.DecodePixelHeight = maxSide;   // the other side keeps the shape
            bi.EndInit();
            return bi;
        }
        catch (Exception) { return null; }
    }

    static WI.BitmapSource Shrink(WI.BitmapSource f, int maxSide)
    {
        double s = (double)maxSide / Math.Max(f.PixelWidth, f.PixelHeight);
        return new WI.TransformedBitmap(f, new WM.ScaleTransform(s, s));
    }

    // orientation (camera RAW and JPEG XR only: the HEIF, AVIF, JPEG XL and WebP decoders hand pictures over
    // the right way up already), star rating, and (details = true) the camera details for the E panel
    static void ReadMeta(string path, WI.BitmapFrame f, CodecPic r, bool details)
    {
        WI.BitmapMetadata md = null;
        try { md = f.Metadata as WI.BitmapMetadata; } catch (Exception) { }
        if (md == null) return;
        try { int s = md.Rating; if (s > 0 && s <= 5) r.Stars = s; } catch (Exception) { }
        string e = (Path.GetExtension(path) ?? "").ToLowerInvariant();
        bool turns = IsRaw(path) || e == ".jxr" || e == ".wdp" || e == ".hdp";
        if (turns)
        {
            try
            {
                object o = md.GetQuery("System.Photo.Orientation");
                int v = o == null ? 1 : Convert.ToInt32(o);
                r.Rot = v == 3 ? 2 : v == 6 ? 1 : v == 8 ? 3 : 0;
            }
            catch (Exception) { }
        }
        if (details) r.Exif = ExifLines(md, turns);
    }

    // ---- camera details for the E panel, in the same words and order as ExifInfo.Read gives them for JPEG photos.
    // Read through Windows' photo metadata names (System.Photo.ExposureTime ...), which work the same for every
    // format whose decoder can read EXIF (HEIF, AVIF, camera RAW, JPEG XR, WebP ...). Never throws; anything missing or
    // unreadable is left out. GPS coordinates are shown only on screen; nothing is written or sent.
    static List<string> ExifLines(WI.BitmapMetadata md, bool withOrientation)
    {
        var r = new List<string>();
        try
        {
            string make = Str(md, "System.Photo.CameraManufacturer"), model = Str(md, "System.Photo.CameraModel");
            string cam = model;
            if (make != null && (model == null || !model.StartsWith(make, StringComparison.OrdinalIgnoreCase)))
                cam = model == null ? make : make + " " + model;
            Add(r, "Camera", cam);
            Add(r, "Lens", Str(md, "System.Photo.LensModel"));
            Add(r, "Taken", Taken(md));

            double v;
            if (Ratio(Q(md, "System.Photo.ExposureTime"), out v) && v > 0)
                Add(r, "Exposure", v < 1 ? "1/" + Math.Round(1 / v) + " s" : v.ToString("0.#") + " s");
            if (Ratio(Q(md, "System.Photo.FNumber"), out v) && v > 0) Add(r, "Aperture", "f/" + v.ToString("0.0"));
            int n;
            if (Whole(Q(md, "System.Photo.ISOSpeed"), out n) && n > 0) Add(r, "ISO", n.ToString());
            if (Ratio(Q(md, "System.Photo.FocalLength"), out v) && v > 0)
            {
                string fl = v.ToString("0.#") + " mm";
                if (Whole(Q(md, "System.Photo.FocalLengthInFilm"), out n) && n > 0) fl += "  (" + n + " mm in 35 mm terms)";
                Add(r, "Focal length", fl);
            }
            if (Ratio(Q(md, "System.Photo.ExposureBias"), out v) && Math.Abs(v) > 1e-9) Add(r, "Exposure bias", (v > 0 ? "+" : "") + v.ToString("0.##") + " EV");
            if (Whole(Q(md, "System.Photo.Flash"), out n)) Add(r, "Flash", (n & 1) != 0 ? "fired" : "did not fire");
            if (withOrientation && Whole(Q(md, "System.Photo.Orientation"), out n) && n >= 2 && n <= 8)
            {
                string[] o = { "", "", "mirrored", "rotated 180 degrees", "rotated 180 degrees, mirrored", "rotated 90 degrees, mirrored",
                               "rotated 90 degrees", "rotated 270 degrees, mirrored", "rotated 270 degrees" };
                Add(r, "Orientation", o[n]);
            }
            Add(r, "Software", Str(md, "System.ApplicationName"));
            Add(r, "Artist", Str(md, "System.Author"));
            Add(r, "Copyright", Str(md, "System.Copyright"));
            Add(r, "Description", Str(md, "System.Title"));

            double lat, lon;
            if (Gps(md, "System.GPS.Latitude", "System.GPS.LatitudeRef", out lat) && Gps(md, "System.GPS.Longitude", "System.GPS.LongitudeRef", out lon))
                Add(r, "GPS", Math.Abs(lat).ToString("0.00000") + (lat < 0 ? " S" : " N") + ", " + Math.Abs(lon).ToString("0.00000") + (lon < 0 ? " W" : " E"));
        }
        catch (Exception) { }
        return r.Count > 0 ? r : null;
    }

    static void Add(List<string> r, string label, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.Length > 80) value = value.Substring(0, 77) + "...";
        r.Add(label + ":  " + value);
    }

    // one metadata value, or null (not there, or the decoder can't read that name)
    static object Q(WI.BitmapMetadata md, string name)
    {
        try { return md.GetQuery(name); } catch (Exception) { return null; }
    }

    // text, or several texts (System.Author) joined; control characters removed
    static string Str(WI.BitmapMetadata md, string name)
    {
        object o = Q(md, name);
        string s = o as string;
        string[] many = o as string[];
        if (s == null && many != null) s = string.Join("; ", many);
        if (s == null) return null;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (c >= ' ' && c != '\uFFFD') sb.Append(c);
        s = sb.ToString().Trim();
        return s.Length > 0 ? s : null;
    }

    // Windows hands EXIF rational numbers over as 64-bit numbers: numerator in the low 32 bits, denominator in the
    // high 32 bits (unsigned, or signed for the exposure bias)
    static bool Ratio(object o, out double v)
    {
        v = 0;
        if (o == null) return false;
        try
        {
            if (o is ulong)
            {
                ulong u = (ulong)o;
                uint num = (uint)(u & 0xFFFFFFFFUL), den = (uint)(u >> 32);
                if (den == 0) return false;
                v = (double)num / den;
                return true;
            }
            if (o is long)
            {
                long l = (long)o;
                int num = unchecked((int)(l & 0xFFFFFFFFL)), den = unchecked((int)(l >> 32));
                if (den == 0) return false;
                v = (double)num / den;
                return true;
            }
            if (o is double || o is float || o is decimal) { v = Convert.ToDouble(o); return !double.IsNaN(v) && !double.IsInfinity(v); }
        }
        catch (Exception) { }
        return false;
    }

    static bool Whole(object o, out int n)
    {
        n = 0;
        if (o == null) return false;
        try
        {
            ushort[] us = o as ushort[];
            if (us != null) { if (us.Length == 0) return false; n = us[0]; return true; }
            if (o is byte || o is sbyte || o is short || o is ushort || o is int || o is uint || o is long || o is ulong)
            {
                n = Convert.ToInt32(o);
                return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    // the date taken, written as in the EXIF panel for JPEGs (2026-11-16 10:22:05)
    static string Taken(WI.BitmapMetadata md)
    {
        object o = Q(md, "System.Photo.DateTaken");
        DateTime dt = DateTime.MinValue;
        bool got = false;
        try
        {
            if (o is System.Runtime.InteropServices.ComTypes.FILETIME)
            {
                var ft = (System.Runtime.InteropServices.ComTypes.FILETIME)o;
                dt = DateTime.FromFileTime(((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime);
                got = true;
            }
            else if (o is DateTime) { dt = (DateTime)o; got = true; }
        }
        catch (Exception) { }
        if (got) return dt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        try { string s = md.DateTaken; return string.IsNullOrEmpty(s) ? null : s; } catch (Exception) { return null; }
    }

    // degrees / minutes / seconds -> signed decimal degrees (south and west are negative)
    static bool Gps(WI.BitmapMetadata md, string valueName, string refName, out double deg)
    {
        deg = 0;
        object o = Q(md, valueName);
        ulong[] u = o as ulong[];
        if (u == null || u.Length < 3) return false;
        double d, m, s;
        if (!Ratio(u[0], out d) || !Ratio(u[1], out m) || !Ratio(u[2], out s)) return false;
        deg = d + m / 60 + s / 3600;
        if (deg == 0 || deg > 180) return false;
        string rf = Str(md, refName);
        if (rf != null && (rf.StartsWith("S") || rf.StartsWith("W"))) deg = -deg;
        return true;
    }

    // straight into a GDI+ bitmap's memory (no unsafe code, no copy in between)
    static Bitmap ToBitmap(WI.BitmapSource src, bool premultiplied)
    {
        var conv = new WI.FormatConvertedBitmap(src, premultiplied ? WM.PixelFormats.Pbgra32 : WM.PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        if ((long)w * h > MaxPixels) throw new IOException("This picture is too large to open.");
        PixelFormat pf = premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb;
        var bmp = new Bitmap(w, h, pf);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, pf);
        try
        {
            int stride = bd.Stride;                      // w * 4 for 32-bit pixels, top row first
            conv.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), bd.Scan0, stride * h, stride);
        }
        catch (Exception) { bmp.UnlockBits(bd); bmp.Dispose(); throw; }
        bmp.UnlockBits(bd);
        return bmp;
    }

    // Faster thumbnails of big JPEG photos: worth it when the photo is at least twice the wanted size each way.
    public static bool WorthScaling(int w, int h, int size) { return size > 0 && Math.Min(w, h) >= 2 * size && (long)w * h >= 4000000L; }

    // A JPEG decoded straight to about maxSide (not turned); null if Windows can't (the caller uses GDI+ instead).
    public static Bitmap ScaledJpeg(string path, int w, int h, int maxSide)
    {
        try
        {
            WI.BitmapSource s = Scaled(Util.ReadShared(path), w >= h, maxSide);
            return s == null ? null : ToBitmap(s, true);
        }
        catch (Exception) { return null; }
    }
}
