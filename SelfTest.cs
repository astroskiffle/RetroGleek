// RetroGleek - the self-test: RetroGleek's file writers tried on generated pictures, in a temporary folder
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// ====================================================================== self-test

// Help > Run the self-test, RetroGleek.exe --selftest or RetroGleekCli.exe --selftest.
// Every part of RetroGleek that writes or changes a picture file is tried on pictures made here, in a new folder of
// its own under the Windows temporary folder, which is deleted afterwards. Nothing else on the PC is read or written,
// and nothing goes online. Each writer already checks its own work before it keeps a file; the self-test checks the
// results again from the outside (read back with another decoder, compared pixel by pixel, counted), and checks that
// the safety rules hold: nothing is overwritten, and a file that can't be changed safely is left exactly as it was.
// Meant for after a change to the code: a build that passes behaves as before.
static class SelfTest
{
    sealed class Failure : Exception { public Failure(string m) : base(m) { } }

    // the screen of each RetroConvert format (as RetroConvert.Render makes it) and the most colours it can show
    static readonly int[] FmtW = { 256, 160, 320, 160, 320, 320, 320, 256, 256 };
    static readonly int[] FmtH = { 192, 200, 200, 200, 200, 200, 256, 192, 212 };
    static readonly int[] FmtColours = { 16, 16, 16, 16, 4, 16, 32, 16, 256 };

    // Runs every test (any thread). progress gets each result line as it comes (or null). Returns the number of
    // tests that failed; lines gets every result line and a summary.
    public static int Run(Action<string> progress, List<string> lines)
    {
        int passed = 0, failed = 0;
        var watch = Stopwatch.StartNew();
        string dir = Path.Combine(Path.GetTempPath(), "RetroGleek self-test " + Guid.NewGuid().ToString("N").Substring(0, 8));
        Action<string> say = delegate (string s) { lines.Add(s); if (progress != null) progress(s); };
        say("RetroGleek " + Program.Version + " self-test, in " + dir);
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { say("FAIL  the temporary folder could not be made: " + ex.Message); return 1; }

        Action<string, Action> test = delegate (string name, Action body)
        {
            var t = Stopwatch.StartNew();
            try { body(); passed++; say("ok    " + name + "  (" + t.ElapsedMilliseconds + " ms)"); }
            catch (Exception ex) { failed++; say("FAIL  " + name + ": " + (ex is Failure ? ex.Message : ex.GetType().Name + ": " + ex.Message)); }
        };

        try
        {
            string photo = Path.Combine(dir, "photo.png");
            using (Bitmap b = Picture(240, 180, false)) b.Save(photo, ImageFormat.Png);

            // ---- lossless PNG export
            test("Save as PNG: truecolour with transparency", delegate { PngRoundTrip(dir, "alpha.png", Picture(200, 150, true)); });
            test("Save as PNG: opaque truecolour", delegate { PngRoundTrip(dir, "opaque.png", Picture(200, 150, false)); });
            test("Save as PNG: few colours (indexed)", delegate { PngRoundTrip(dir, "indexed.png", Stripes(96, 64, 7)); });
            test("Save as PNG: never overwrites", delegate
            {
                string p = Path.Combine(dir, "keep.png");
                using (Bitmap a = Stripes(32, 32, 3)) Exporter.SavePng(a, p, false);
                byte[] before = File.ReadAllBytes(p);
                bool refused = false;
                using (Bitmap b = Stripes(32, 32, 5))
                    try { Exporter.SavePng(b, p, false); } catch (IOException) { refused = true; }
                Expect(refused, "a second save to the same name was not refused");
                Expect(Same(before, File.ReadAllBytes(p)), "the existing file was changed");
                Expect(Directory.GetFiles(dir, "*.tmp").Length == 0, "a temporary file was left behind");
            });

            // ---- colour counting (the viewer's and preview pane's "colours used")
            test("Colour count and swatches", delegate
            {
                using (Bitmap b = Stripes(64, 40, 5))
                {
                    var r = new ThumbResult();
                    ThumbService.CountColours(b, r);
                    Expect(r.Colours == 5, "counted " + r.Colours + " colours, not 5");
                    Expect(r.Palette != null && r.Palette.Length == 5, "the swatch strip does not hold 5 colours");
                }
            });

            // ---- converting into retro formats: every format, every dithering, both framings
            for (int f = 0; f < RetroConvert.FormatNames.Length; f++)
                for (int d = 0; d < RetroConvert.DitherNames.Length; d++)
                {
                    int ff = f, dd = d;
                    bool fill = d == 1;
                    test("Convert: " + Short(RetroConvert.FormatNames[f]) + ", " + RetroConvert.DitherShort[d] + (fill ? ", fill" : ""),
                         delegate { ConvertOne(photo, dir, ff, dd, fill); });
                }
            // Clean edges with some dithering mixed in (ZX Spectrum): each amount and kind makes a good file
            foreach (int amount in new[] { 0, 50, 100 })
                foreach (int pattern in new[] { 0, 1 })
                {
                    if (amount == 0 && pattern == 1) continue;        // 0%: no dithering of either kind
                    int aa = amount, pp = pattern;
                    test("Convert: ZX Spectrum, clean edges + " + amount + "% " + (pattern == 1 ? "error diffusion" : "ordered"),
                         delegate { ConvertOne(photo, dir, 0, 3, false, aa, pp); });
                }
            // Clean edges on every other format, with error diffusion mixed in
            for (int f = 1; f < RetroConvert.FormatNames.Length; f++)
            {
                int ff = f;
                test("Convert: " + Short(RetroConvert.FormatNames[f]) + ", clean edges + 50% error diffusion", delegate { ConvertOne(photo, dir, ff, 3, false, 50, 1); });
            }
            test("Clean edges: more dithering shades a gradient more finely", delegate
            {
                using (Bitmap g = Gradient(256, 192))
                {
                    int[] flat, half, full;
                    RetroConvert.ToZx(g, false, 3, out flat, null, 0, 0);
                    RetroConvert.ToZx(g, false, 3, out half, null, 50, 0);
                    RetroConvert.ToZx(g, false, 3, out full, null, 100, 0);
                    int c0 = Changes(flat), c50 = Changes(half), c100 = Changes(full);
                    Expect(c50 > c0 && c100 > c50, "colour changes along the rows: " + c0 + " at 0%, " + c50 + " at 50%, " + c100 + " at 100% (they should grow)");
                }
            });
            // adjusting the picture before converting (brightness, contrast, saturation, hue)
            test("Convert: ZX Spectrum, picture adjusted", delegate
            {
                string sub = Path.Combine(dir, "convert");
                Directory.CreateDirectory(sub);
                var adj = PictureAdjust.Clamped(20, 40, 50, 30);
                string dest = RetroConvert.ConvertFile(photo, new ConvertOptions { Format = 0, Dither = 1, Adjust = adj, DestDir = sub });
                Expect(File.Exists(dest), "no file was made");
            });
            test("Adjust: brightness lightens, the black borders stay black", delegate
            {
                using (Bitmap g = Gradient(256, 96))                   // half the screen's height: black borders above and below
                {
                    string ext; double lpp; int screenH;
                    var plain = new RetroSim { Machine = 0, Dither = 0 };
                    var light = plain.Copy(); light.Adjust = PictureAdjust.Clamped(60, 0, 0, 0);
                    int[] a = Shown(RetroConvert.Preview(g, plain, null, out ext, out lpp, out screenH), ext);
                    int[] b = Shown(RetroConvert.Preview(g, light, null, out ext, out lpp, out screenH), ext);
                    Expect(Luma(b, 72, 120) > Luma(a, 72, 120) + 10, "the picture did not get lighter (" + (int)Luma(a, 72, 120) + " became " + (int)Luma(b, 72, 120) + ")");
                    Expect(Luma(b, 0, 8) < 1 && Luma(b, 184, 192) < 1, "the black borders were lightened too");
                }
            });
            test("Adjust: Auto stretches a dull picture", delegate
            {
                using (var dull = new Bitmap(120, 80, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(dull))
                    using (var br = new LinearGradientBrush(new Rectangle(0, 0, 120, 80), Color.FromArgb(100, 100, 100), Color.FromArgb(150, 150, 150), 0f))
                        g.FillRectangle(br, 0, 0, 120, 80);
                    PictureAdjust a = RetroConvert.AutoLevels(dull, null);
                    Expect(a.Contrast > 50, "Auto gave contrast " + a.Contrast + " for a picture with little contrast");
                    Expect(Math.Abs(a.Brightness) < 15, "Auto moved the brightness of a picture centred on grey by " + a.Brightness);
                }
            });
            test("Convert: never overwrites (a taken name gets a number)", delegate
            {
                var o = new ConvertOptions { Format = 0, Dither = 1, DestDir = dir };
                string first = RetroConvert.ConvertFile(photo, o);
                byte[] before = File.ReadAllBytes(first);
                string second = RetroConvert.ConvertFile(photo, o);
                Expect(!string.Equals(first, second, StringComparison.OrdinalIgnoreCase), "the second file took the first one's name");
                Expect(Same(before, File.ReadAllBytes(first)), "the first file was changed");
            });

            // ---- the retro preview (in memory) and its animation colours
            for (int m = 0; m < RetroConvert.PreviewFormats.Length; m++)
            {
                int mm = m;
                test("Retro preview: " + Short(RetroConvert.PreviewNames[m]), delegate { PreviewOne(mm); });
            }
            test("Retro preview: animation colours kept for every frame", delegate
            {
                using (Bitmap a = Picture(160, 120, false))
                using (Bitmap b = Stripes(160, 120, 6))
                {
                    var sample = new List<Bitmap> { a, b };
                    foreach (int m in new[] { 1, 3, 4 })              // C64 multicolour, CPC mode 0, CPC mode 1
                    {
                        int[] lockPal = RetroConvert.PreviewLock(sample, m, false, 0.5, 0.5);
                        Expect(lockPal != null && lockPal.Length > 0, "no colours were chosen for " + Short(RetroConvert.PreviewNames[m]));
                        string ext; double lpp; int screenH;
                        foreach (Bitmap fr in sample)
                        {
                            byte[] data = RetroConvert.Preview(fr, m, false, 1, 0.5, 0.5, lockPal, out ext, out lpp, out screenH);
                            string platform; RetroGeom geom;
                            using (Bitmap back = Retro.LoadMemory("frame" + ext, data, out platform, out geom)) { }
                        }
                    }
                }
            });

            test("Retro preview: Clean edges keeps one position for every frame", delegate
            {
                using (Bitmap a = Picture(200, 120, false))
                using (Bitmap b = Stripes(200, 120, 4))
                {
                    var sample = new List<Bitmap> { a, b };
                    foreach (int m in new[] { 0, 1, 2, 5 })           // ZX Spectrum, C64 multicolour and hires, MSX
                    {
                        string name = Short(RetroConvert.PreviewNames[m]);
                        int[] pos = RetroConvert.PreviewLock(sample, m, false, 0.5, 0.5, 3);
                        int at = m == 1 ? 1 : 0;                      // the C64 multicolour's background comes first
                        Expect(pos != null && pos.Length == at + 2, "no position was chosen for the animation (" + name + ")");
                        Expect(pos[at] >= -4 && pos[at] <= 3 && pos[at + 1] >= -4 && pos[at + 1] <= 3, "the picture was moved too far (" + name + "): " + pos[at] + ", " + pos[at + 1]);
                        string ext; double lpp; int screenH;
                        foreach (Bitmap fr in sample)
                        {
                            byte[] data = RetroConvert.Preview(fr, m, false, 3, 0.5, 0.5, pos, out ext, out lpp, out screenH);
                            string platform; RetroGeom geom;
                            using (Bitmap back = Retro.LoadMemory("frame" + ext, data, out platform, out geom))
                                Expect(back.Width > 0 && back.Height > 0, "RECOIL could not show the frame (" + name + ")");
                        }
                    }
                }
            });

            // ---- the animated GIF writer
            test("Animated GIF: frames, merging and pixels", delegate { GifTest(dir); });

            // ---- star ratings and details written into JPEG and PNG files
            test("Rating into a JPEG", delegate { RatingTest(dir, "rated.jpg", ImageFormat.Jpeg); });
            test("Rating into a PNG", delegate { RatingTest(dir, "rated.png", ImageFormat.Png); });
            test("Title, description, keywords into a JPEG", delegate { DetailsTest(dir, "details.jpg", ImageFormat.Jpeg); });
            test("Title, description, keywords into a PNG", delegate { DetailsTest(dir, "details.png", ImageFormat.Png); });

            // ---- the lossless JPEG turn
            test("Lossless JPEG turn", delegate
            {
                string p = Path.Combine(dir, "turn.jpg");
                using (Bitmap b = Picture(64, 48, false)) b.Save(p, ImageFormat.Jpeg);   // whole JPEG blocks, no orientation tag
                JpegRotate.Rotate(p, 1);
                using (Bitmap back = Exporter.Decode(p, 0)) Expect(back.Width == 48 && back.Height == 64, "the photo is " + back.Width + " x " + back.Height + " after a quarter turn, not 48 x 64");
                JpegRotate.Rotate(p, 3);
                using (Bitmap back = Exporter.Decode(p, 0)) Expect(back.Width == 64 && back.Height == 48, "the photo did not turn back");
            });
            test("Lossless JPEG turn: refused when not whole blocks, file unchanged", delegate
            {
                string p = Path.Combine(dir, "odd.jpg");
                using (Bitmap b = Picture(50, 30, false)) b.Save(p, ImageFormat.Jpeg);
                byte[] before = File.ReadAllBytes(p);
                bool refused = false;
                try { JpegRotate.Rotate(p, 1); } catch (IOException) { refused = true; }
                Expect(refused, "a turn that can't be lossless was not refused");
                Expect(Same(before, File.ReadAllBytes(p)), "the photo was changed");
            });
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) { say("note  the temporary folder could not be deleted: " + ex.Message); }
        }
        say(failed == 0
            ? "All " + passed + " tests passed (" + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s)."
            : failed + " of " + (passed + failed) + " tests FAILED.");
        return failed;
    }

    // the results for a message box: the summary, and every failure (the full list is long)
    public static string Report(List<string> lines, int failed)
    {
        var sb = new StringBuilder();
        if (lines.Count > 0) sb.Append(lines[0]).Append("\n\n");
        foreach (string l in lines)
            if (l.StartsWith("FAIL", StringComparison.Ordinal) || l.StartsWith("note", StringComparison.Ordinal)) sb.Append(l).Append("\n");
        if (failed > 0) sb.Append("\n");
        if (lines.Count > 1) sb.Append(lines[lines.Count - 1]);
        if (failed > 0) sb.Append("\n\nRetroGleekCli.exe --selftest lists every test.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- the tests

    static void PngRoundTrip(string dir, string name, Bitmap src)
    {
        using (src)
        {
            string p = Path.Combine(dir, name);
            Exporter.SavePng(src, p, false);
            using (Bitmap back = Exporter.Decode(p, 0))
            {
                Expect(back.Width == src.Width && back.Height == src.Height, "the saved picture has a different size");
                int[] a = Pixels(src), b = Pixels(back);
                for (int i = 0; i < a.Length; i++)
                {
                    bool clearA = (a[i] >> 24) == 0, clearB = (b[i] >> 24) == 0;     // fully clear: its colour doesn't count
                    if (clearA && clearB) continue;
                    if (a[i] != b[i]) throw new Failure("pixel " + (i % src.Width) + ", " + (i / src.Width) + " differs after saving (" + a[i].ToString("X8") + " became " + b[i].ToString("X8") + ")");
                }
            }
        }
    }

    static void ConvertOne(string photo, string dir, int f, int dither, bool fill, int amount = 25, int pattern = 0)
    {
        string sub = Path.Combine(dir, "convert");
        Directory.CreateDirectory(sub);
        string dest = RetroConvert.ConvertFile(photo, new ConvertOptions { Format = f, Dither = dither, Fill = fill, Amount = amount, Pattern = pattern, DestDir = sub });
        Expect(File.Exists(dest), "no file was made");
        long bytes; string platform; int frames; RetroGeom geom;
        using (Bitmap b = Retro.Load(dest, out bytes, out platform, out frames, out geom))
        {
            Expect(!string.IsNullOrEmpty(platform), "RECOIL names no computer for the new file");
            Expect(b.Width % FmtW[f] == 0 && b.Height % FmtH[f] == 0, "RECOIL shows it as " + b.Width + " x " + b.Height + ", not a whole multiple of " + FmtW[f] + " x " + FmtH[f]);
            var r = new ThumbResult();
            ThumbService.CountColours(b, r);
            Expect(r.Colours >= 2, "the picture has only " + r.Colours + " colour");
            Expect(r.Colours <= FmtColours[f], "the picture uses " + r.Colours + " colours, more than the " + FmtColours[f] + " the format can show");
        }
    }

    // a smooth grey-blue ramp from left to right
    static Bitmap Gradient(int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(10, 10, 40), Color.FromArgb(220, 220, 255), 0f))
            g.FillRectangle(br, 0, 0, w, h);
        return b;
    }

    // the screen a preview's bytes show, as RECOIL decodes them, scaled to 256 pixels across
    static int[] Shown(byte[] data, string ext)
    {
        string platform; RetroGeom geom;
        using (Bitmap b = Retro.LoadMemory("adjust" + ext, data, out platform, out geom))
        {
            var px = new int[256 * 192];
            for (int y = 0; y < 192; y++)
                for (int x = 0; x < 256; x++) px[y * 256 + x] = b.GetPixel(x * b.Width / 256, y * b.Height / 192).ToArgb();
            return px;
        }
    }

    // the average brightness (0..255) of screen lines y0 to y1 (256 pixels a row)
    static double Luma(int[] px, int y0, int y1)
    {
        double t = 0;
        for (int i = y0 * 256; i < y1 * 256; i++) t += ((px[i] >> 16) & 255) * 0.299 + ((px[i] >> 8) & 255) * 0.587 + (px[i] & 255) * 0.114;
        return t / ((y1 - y0) * 256);
    }

    // how often a pixel differs from the one to its left (256 pixels a row)
    static int Changes(int[] px)
    {
        int n = 0;
        for (int i = 1; i < px.Length; i++) if ((i & 255) != 0 && px[i] != px[i - 1]) n++;
        return n;
    }

    static void PreviewOne(int m)
    {
        using (Bitmap src = Picture(300, 200, false))
            foreach (bool fill in new[] { false, true })
            {
                string ext; double lines; int screenH;
                byte[] data = RetroConvert.Preview(src, m, fill, 2, 0.3, 0.7, null, out ext, out lines, out screenH);
                Expect(data != null && data.Length > 0, "no picture was made");
                Expect(lines > 0 && screenH > 0, "the preview's scale is missing");
                string platform; RetroGeom geom;
                using (Bitmap b = Retro.LoadMemory("preview" + ext, data, out platform, out geom))
                    Expect(b.Width > 0 && b.Height > 0, "RECOIL could not show the preview");
            }
    }

    static void GifTest(string dir)
    {
        const int w = 40, h = 30;
        int[] palette = { 0x000000, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFFFF };
        var frames = new List<byte[]>();
        for (int f = 0; f < 4; f++)
        {
            var px = new byte[w * h];
            int g = f == 2 ? 1 : f;                            // frame 2 repeats frame 1: it is merged into it
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(((i % w) / 8 + (i / w) / 6 + g) % palette.Length);
            if (f == 3) for (int i = 0; i < w * 5; i++) px[i] = 4;    // a change in the top rows only
            frames.Add(px);
        }
        string p = Path.Combine(dir, "anim.gif");
        int written = GifWriter.Save(p, w, h, palette, frames, new[] { 100, 120, 80, 200 });
        Expect(written == 3, written + " frames were written, not 3 (the repeated frame should be merged)");
        using (var img = Image.FromFile(p))
        {
            Expect(img.Width == w && img.Height == h, "the GIF has the wrong size");
            int n = img.GetFrameCount(FrameDimension.Time);
            Expect(n == 3, "Windows reads " + n + " frames, not 3");
            int[] check = { 0, 1, 3 };                         // GIF frame k shows source frame check[k]
            for (int k = 0; k < n; k++)
            {
                img.SelectActiveFrame(FrameDimension.Time, k);
                using (var b = new Bitmap(img))
                {
                    int[] got = Pixels(b);
                    byte[] want = frames[check[k]];
                    for (int i = 0; i < got.Length; i++)
                        if ((got[i] & 0xFFFFFF) != palette[want[i]]) throw new Failure("frame " + (k + 1) + " differs at " + (i % w) + ", " + (i / w));
                }
            }
            PropertyItem delays = img.GetPropertyItem(0x5100);
            int second = BitConverter.ToInt32(delays.Value, 4);
            Expect(second == 20, "the merged frame shows for " + second * 10 + " ms, not 200 ms (120 + 80)");
        }
    }

    static void RatingTest(string dir, string name, ImageFormat fmt)
    {
        string p = Path.Combine(dir, name);
        using (Bitmap b = Picture(64, 48, false)) b.Save(p, fmt);
        int[] before = DecodedPixels(p);
        RatingWriter.Write(p, 4);
        Expect(RatingOf(p) == 4, "the rating reads back as " + RatingOf(p) + ", not 4");
        Expect(SamePixels(before, DecodedPixels(p)), "the picture changed");
        RatingWriter.Write(p, 2);
        Expect(RatingOf(p) == 2, "a changed rating reads back as " + RatingOf(p) + ", not 2");
        RatingWriter.Write(p, 0);
        Expect(RatingOf(p) == 0, "a removed rating still reads back as " + RatingOf(p));
        Expect(SamePixels(before, DecodedPixels(p)), "the picture changed");
    }

    static void DetailsTest(string dir, string name, ImageFormat fmt)
    {
        string p = Path.Combine(dir, name);
        using (Bitmap b = Picture(64, 48, false)) b.Save(p, fmt);
        int[] before = DecodedPixels(p);
        RatingWriter.Write(p, 3);                              // another XMP property, which must be kept
        var want = new XmpFields
        {
            Title = "Self-test \u00e9t\u00e9 <&> \"quoted\"",
            Description = "Two lines:\nsecond line",
            Author = "Ada; Grace",
            Copyright = "(c) 2026 nobody",
            Keywords = XmpFields.SplitList("one; two; three; two")
        };
        XmpFields.Write(p, want);
        XmpFields back = ReadFields(p);
        Expect(back.SameAs(want.Normalized()), "the details read back differently (title \"" + back.Title + "\")");
        Expect(back.Keywords.Count == 3, "the keywords were not de-duplicated");
        Expect(RatingOf(p) == 3, "the rating was lost when the details were written");
        Expect(SamePixels(before, DecodedPixels(p)), "the picture changed");
        XmpFields.Write(p, new XmpFields());                   // empty fields remove the details
        back = ReadFields(p);
        Expect(back.Title == "" && back.Description == "" && back.Author == "" && back.Copyright == "" && back.Keywords.Count == 0, "removed details are still there");
        Expect(RatingOf(p) == 3, "the rating was lost when the details were removed");
        Expect(Directory.GetFiles(dir, "*.tmp").Length == 0, "a temporary file was left behind");
    }

    // ---------------------------------------------------------------- helpers

    static void Expect(bool ok, string why) { if (!ok) throw new Failure(why); }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static bool SamePixels(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // "ZX Spectrum .scr  -  256 x 192 ..." -> "ZX Spectrum .scr"
    static string Short(string name)
    {
        int i = name.IndexOf("  -  ", StringComparison.Ordinal);
        return i > 0 ? name.Substring(0, i) : name;
    }

    static int RatingOf(string p)
    {
        using (var ms = new MemoryStream(File.ReadAllBytes(p)))
        using (var img = Image.FromStream(ms))
            return FileRating.Read(p, img);
    }

    static XmpFields ReadFields(string p)
    {
        byte[] d = File.ReadAllBytes(p);
        return XmpFields.Read(JpegRotate.IsJpeg(p) ? JpegRating.ReadXmp(d) : PngRating.ReadXmp(d));
    }

    static int[] DecodedPixels(string p)
    {
        using (Bitmap b = Exporter.Decode(p, 0)) return Pixels(b);
    }

    // straight-alpha ARGB, row by row
    static int[] Pixels(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var px = new int[w * h];
        BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), px, y * w, w); }
        finally { b.UnlockBits(d); }
        return px;
    }

    static Bitmap FromPixels(int[] px, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), w); }
        finally { b.UnlockBits(d); }
        return b;
    }

    // a colourful test picture: smooth gradients, a checker pattern and a few solid shapes; with alpha, transparency
    // that fades from left to right, with a fully clear and a fully solid band
    static Bitmap Picture(int w, int h, bool alpha)
    {
        var px = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int r = x * 255 / (w - 1), g = y * 255 / (h - 1), b = ((x / 8 + y / 8) & 1) != 0 ? 200 : 40;
                int dx = x - w / 3, dy = y - h / 2;
                if (dx * dx + dy * dy < (h / 4) * (h / 4)) { r = 250; g = 220; b = 20; }      // a yellow disc
                if (x > w * 2 / 3 && y > h / 3 && y < h * 2 / 3) { r = 20; g = 60; b = 230; } // a blue block
                int a = 255;
                if (alpha) a = x < w / 8 ? 0 : x > w * 7 / 8 ? 255 : (x - w / 8) * 255 / (w * 3 / 4);
                px[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        return FromPixels(px, w, h);
    }

    // vertical stripes of n distinct opaque colours
    static Bitmap Stripes(int w, int h, int n)
    {
        var px = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int k = x * n / w;
                px[y * w + x] = unchecked((int)0xFF000000) | ((k * 53 % 256) << 16) | ((k * 97 % 256) << 8) | (255 - k * 31 % 256);
            }
        return FromPixels(px, w, h);
    }
}
