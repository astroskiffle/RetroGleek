// RetroGleek - the full-canvas viewer
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

// ====================================================================== full-canvas viewer

sealed class Canvas : Control
{
    static readonly Color Bg = Color.FromArgb(24, 24, 24);
    const long MaxPredecodePixels = 100000000L;

    List<string> files = new List<string>();
    int idx = -1;
    readonly Dictionary<string, Pic> cache = new Dictionary<string, Pic>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string want, notice;
    Pic cur;

    int rot, fitMode;                 // fitMode: 0 = free, 1 = fit (never enlarge), 2 = fit (may enlarge)
    double zoom = 1, px, py;          // px,py = offset of image centre from window centre
    bool fast, showInfo = true, dragging;
    int wheelAcc;
    Point last;

    // pixel tools, slideshow and short on-screen messages
    bool showGrid;                         // G: pixel grid (drawn at 8x and above)
    int pixMode;                           // N: 0 = auto (retro pictures sharp, photos smooth), 1 = crisp, 2 = smooth, 3 = sharp
    bool mouseIn;                          // the pointer is over the picture area
    bool loupe;                            // Z held: a magnifier around the pointer

    // ---- filmstrip (J): a row of small thumbnails of the folder along the bottom; click one to show it.
    // Hidden during a slideshow. The picture area is the window minus the strip.
    bool strip;
    ThumbService stripSvc;
    readonly ThumbCache stripCache = new ThumbCache();
    readonly Dictionary<string, long> stripStamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    Control stripHost;
    public bool Filmstrip { get { return strip; } set { strip = value; if (HasImage) { Refit(); Clamp(); } Invalidate(); } }
    bool StripShown { get { return strip && !slideshow && files.Count > 1; } }
    int StripH { get { return StripShown ? Util.S(84) : 0; } }
    int ViewH { get { return !exportSize.IsEmpty ? exportSize.Height : Math.Max(1, ClientSize.Height - StripH); } }   // the height the picture is shown in
    int ViewW { get { return !exportSize.IsEmpty ? exportSize.Width : ClientSize.Width; } }
    Size exportSize = Size.Empty;          // RenderAsShown: the picture is drawn into a bitmap of this size, not the window
    Point mousePt;
    int lhx = -2, lhy = -2;                // last pixel reported under the pointer
    string flash;
    bool slideshow;
    int slideIdx = 2;                      // index into SlideSecs (3 seconds)
    static readonly int[] SlideSecs = { 1, 2, 3, 5, 8, 12, 20, 30 };
    readonly WinTimer slideTimer = new WinTimer();
    readonly WinTimer flashTimer = new WinTimer { Interval = 2200 };
    public bool Slideshow { get { return slideshow; } }

    // ---- True TV aspect (T) and scanlines (K): display only, never in saved or copied pictures
    bool tvAspect;
    int scanMode;                                          // 0 off, 1 light, 2 medium, 3 strong
    static readonly string[] ScanNames = { "off", "light", "medium", "strong" };
    public bool TvAspect { get { return tvAspect; } set { tvAspect = value; fxOff = false; LeaveAuto(); if (HasImage) { Refit(); Clamp(); } Invalidate(); } }
    public int ScanMode { get { return scanMode; } set { scanMode = Math.Max(0, Math.Min(3, value)); fxOff = false; LeaveAuto(); if (HasImage) Refit(); Invalidate(); } }

    // O: every screen effect off at once (TV aspect, scanlines, mask, signal, glow), to compare with the picture as
    // decoded; O again, or changing any effect, brings them back. Not remembered: each start shows the effects.
    bool fxOff;
    public bool EffectsOff { get { return fxOff; } set { fxOff = value; if (HasImage) { Refit(); Clamp(); } Invalidate(); } }

    // ---- automatic effects (A) and presets. Automatic: every retro picture gets the effects of the screen its computer
    // was usually seen on (EffectSet.ForPlatform). Your own settings are kept aside meanwhile: A again brings them back.
    // Changing any single effect while automatic is on keeps what is shown, with your change, as your own settings
    // (except curvature, which automatic never chooses: changing it leaves automatic on).
    bool autoFx;
    EffectSet manualFx;                                    // your own settings while automatic is on
    EffectSet autoChoice;                                  // what automatic chose for the current picture

    public EffectSet CurrentEffects { get { return new EffectSet { Tv = tvAspect, Scan = scanMode, Mask = maskMode, Signal = signalMode, Glow = glowMode, Curve = curveMode }; } }
    public EffectSet ManualEffects { get { return autoFx && manualFx != null ? manualFx.Copy() : CurrentEffects; } }   // what settings.ini keeps
    public EffectSet AutoChoice { get { return autoFx ? autoChoice : null; } }

    public bool AutoEffects
    {
        get { return autoFx; }
        set
        {
            if (value == autoFx) return;
            fxOff = false;
            if (value) { manualFx = CurrentEffects; autoFx = true; ApplyAuto(); }
            else { autoFx = false; autoChoice = null; if (manualFx != null) SetFields(manualFx); manualFx = null; }
            if (HasImage) { Refit(); Clamp(); }
            Invalidate();
        }
    }

    void LeaveAuto() { if (autoFx) { autoFx = false; manualFx = null; autoChoice = null; } }

    void SetFields(EffectSet e)
    {
        tvAspect = e.Tv; scanMode = e.Scan; maskMode = e.Mask; signalMode = e.Signal; glowMode = e.Glow; curveMode = e.Curve;
    }

    // automatic: pick the effects for the current picture's computer (pictures that are not retro keep the last ones)
    void ApplyAuto()
    {
        if (!autoFx || !HasImage) return;
        EffectSet e = EffectSet.ForPlatform(cur.Note, cur.Geom != null ? cur.Geom.OrigW : 0, cur.Geom != null ? cur.Geom.OrigH : 0);
        if (e == null) return;
        e.Curve = manualFx != null ? manualFx.Curve : curveMode;   // curvature is a matter of taste: automatic keeps yours
        autoChoice = e;
        SetFields(e);
    }

    // a preset (or the settings from settings.ini): replaces the effects and ends automatic mode
    public void UseEffects(EffectSet e)
    {
        if (e == null) return;
        LeaveAuto();
        fxOff = false;
        SetFields(e);
        if (HasImage) { Refit(); Clamp(); }
        Invalidate();
    }

    public void ToggleAuto()
    {
        AutoEffects = !autoFx;
        if (!autoFx) { Flash("Automatic screen effects off: your own settings again"); return; }
        Flash(autoChoice != null ? "Automatic screen effects: " + cur.Note + " - " + autoChoice.Name + " (" + autoChoice.Describe() + ")"
                                 : "Automatic screen effects on: each retro picture gets the effects of its computer's usual screen");
    }

    // ---- glow (W): bright areas light up their surroundings, as on a CRT
    int glowMode;                                          // 0 off, 1 soft, 2 strong
    static readonly string[] GlowNames = { "off", "soft", "strong" };
    public int GlowMode { get { return glowMode; } set { glowMode = Math.Max(0, Math.Min(2, value)); fxOff = false; LeaveAuto(); Invalidate(); } }
    bool GlowShown { get { return glowMode > 0 && !fxOff && HasImage && cur.Note != null; } }

    // ---- screen curvature (U): the picture bulges like a CRT's glass, with darker, rounded corners. It bends the
    // picture's pixels, so it switches itself off at 800% and above, where the pixel grid and outline need exact pixels.
    int curveMode;                                         // 0 off, 1 slight, 2 strong
    static readonly string[] CurveNames = { "off", "slight", "strong" };
    // automatic mode stays on when only the curvature changes: it is yours either way (ApplyAuto keeps it)
    public int CurveMode
    {
        get { return curveMode; }
        set
        {
            curveMode = Math.Max(0, Math.Min(2, value));
            fxOff = false;
            if (manualFx != null) manualFx.Curve = curveMode;
            if (autoChoice != null) autoChoice.Curve = curveMode;
            Invalidate();
        }
    }
    bool CurveWanted { get { return curveMode > 0 && !fxOff && HasImage && cur.Note != null; } }
    bool CurveShown { get { return CurveWanted && zoom < 8; } }

    // ---- phosphor mask (M): the fine pattern of red, green and blue phosphors on a CRT, drawn in screen pixels
    int maskMode;                                          // 0 off, 1 aperture grille, 2 slot mask, 3 dot mask
    static readonly string[] MaskNames = { "off", "aperture grille", "slot mask", "dot mask" };
    public int MaskMode { get { return maskMode; } set { maskMode = Math.Max(0, Math.Min(3, value)); fxOff = false; LeaveAuto(); Invalidate(); } }

    // ---- video signal (C): RGB (as decoded), S-Video or composite, as the picture would have reached a TV
    int signalMode;                                        // 0 RGB, 1 S-Video, 2 composite
    static readonly string[] SignalNames = { "RGB (sharp)", "S-Video", "composite" };
    public int SignalMode { get { return signalMode; } set { signalMode = Math.Max(0, Math.Min(2, value)); fxOff = false; LeaveAuto(); Invalidate(); } }
    bool SignalApplies { get { return signalMode > 0 && !fxOff && HasImage && cur.Note != null; } }

    // width of one displayed pixel relative to its height (1 = square, or not known / not wanted)
    double Ax { get { return tvAspect && !fxOff && HasImage && cur.Geom != null ? cur.Geom.PixelAspect : 1.0; } }

    // scanlines apply to retro pictures; one per original screen line. Not shown while the picture is turned on its
    // side (90 / 270 degrees): they would run vertically. They come back when it is upright or upside down again.
    bool ScanWanted { get { return scanMode > 0 && !fxOff && HasImage && cur.Note != null; } }
    bool ScanApplies { get { return ScanWanted && (rot & 1) == 0; } }
    bool MaskWanted { get { return maskMode > 0 && !fxOff && HasImage && cur.Note != null; } }
    bool MaskApplies { get { return MaskWanted && (rot & 1) == 0; } }
    bool ScanShown { get { return ScanApplies && LineHeight * zoom >= 3; } }
    bool MaskShown { get { return MaskApplies && zoom * Math.Min(1.0, Ax) >= 2; } }
    double LineHeight { get { return HasImage && cur.Geom != null && cur.Geom.OrigH > 0 ? (double)cur.H / cur.Geom.OrigH : 1.0; } }

    // ---- side-by-side compare: two viewers follow each other's zoom and position
    public event Action ViewChanged;                       // the user zoomed, panned or chose fit (not raised by SetView)
    bool applyingView;
    void RaiseView() { if (applyingView) return; var h = ViewChanged; if (h != null) h(); }

    public void GetView(out double z, out double x, out double y, out int fm) { z = zoom; x = px; y = py; fm = fitMode; }

    // Takes over another viewer's view. Fit modes are applied as modes (each picture fits its own window);
    // a free zoom takes the same scale and offset, so equal-sized pictures line up pixel for pixel.
    public void SetView(double z, double x, double y, int fm)
    {
        applyingView = true;
        try
        {
            fast = false;
            if (fm != 0) { fitMode = fm; Refit(); }
            else { zoom = z; px = x; py = y; fitMode = 0; Clamp(); }
            Invalidate();
        }
        finally { applyingView = false; }
    }

    // ---- retro preview (the right side of Shift+O on a modern picture): every picture is shown converted in memory
    // for an 8-bit computer (RetroConvert.Preview) and decoded back by RECOIL, so it is a retro picture to the viewer:
    // True TV aspect, scanlines and the other screen effects, and automatic effects, work on it as on a real file.
    // Nothing is written. Changing the choice converts the picture again.
    RetroSim sim;
    int simGen;                                            // pictures decoded for an older choice are thrown away
    public RetroSim Simulation
    {
        get { return sim; }
        set
        {
            sim = value == null ? null : value.Copy();
            simGen++;
            StopAnim();
            if (cur != null && !cache.ContainsValue(cur)) cur.Dispose();
            foreach (Pic p in cache.Values) p.Dispose();
            cache.Clear();
            pending.Clear();
            cur = null; notice = null;
            DropSharp(); DropFx(); DropComposite(); DropGlow();
            if (idx >= 0 && idx < files.Count) GoTo(idx);
            Invalidate();
        }
    }

    // retro preview: how many lines of the converted picture (as RECOIL shows it) one pixel of the original became;
    // 0 while there is none. The other side shows the original at the right side's zoom times this, so both match.
    public double SimScale { get { return HasImage && cur.SimScale > 0 ? cur.SimScale : 0; } }

    // ---- retro preview, "Fill the screen": a frame on the original (left) side showing what the computer's screen
    // shows. Dragging on the picture moves the frame instead of panning; CropDragged reports where it is while you
    // drag, CropDropped when you let go. In pixels of the upright picture (RW x RH: the preview turns and flips nothing).
    RectangleF crop = RectangleF.Empty;
    bool cropDrag;
    PointF cropGrab;                                       // where in the frame the pointer took hold of it
    public RectangleF CropFrame { get { return crop; } set { if (value == crop) return; crop = value; Invalidate(); } }
    public event Action<RectangleF> CropDragged, CropDropped;
    public bool UprightSize(out double w, out double h) { w = RW; h = RH; return HasImage; }

    // the upright picture's pixel position under a window point (no turn or flip in the preview)
    PointF UprightAt(Point s)
    {
        double x0 = ClientSize.Width / 2.0 + px - RW * zoom / 2, y0 = ViewH / 2.0 + py - RH * zoom / 2;
        return new PointF((float)((s.X - x0) / zoom), (float)((s.Y - y0) / zoom));
    }

    void DrawCropFrame(Graphics g)
    {
        double x0 = ClientSize.Width / 2.0 + px - RW * zoom / 2, y0 = ViewH / 2.0 + py - RH * zoom / 2;
        var pic = new RectangleF((float)x0, (float)y0, (float)(RW * zoom), (float)(RH * zoom));
        var fr = new RectangleF((float)(x0 + crop.X * zoom), (float)(y0 + crop.Y * zoom), (float)(crop.Width * zoom), (float)(crop.Height * zoom));
        g.SmoothingMode = SmoothingMode.None;
        using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
        using (var outside = new Region(pic))
        {
            outside.Exclude(fr);
            g.FillRegion(shade, outside);                  // what the screen leaves out is dimmed
        }
        using (var dark = new Pen(Color.Black, Math.Max(3f, Util.S(3))))
        using (var light = new Pen(cropDrag ? Color.FromArgb(255, 210, 80) : Color.White, Math.Max(1f, Util.S(1))))
        {
            g.DrawRectangle(dark, fr.X, fr.Y, fr.Width, fr.Height);
            g.DrawRectangle(light, fr.X, fr.Y, fr.Width, fr.Height);
        }
    }

    // The whole picture as it is shown, screen effects included (True TV aspect, signal, scanlines, mask, glow and
    // curvature, as they are set now), drawn at a size of its own rather than the window's: each of the computer's
    // original lines becomes linesScale pixels high. No info bar, grid or frame. For the retro preview's Save (a PNG to
    // share); null if there is no picture. Throws if the effects could not be drawn (not enough memory).
    public Bitmap RenderAsShown(int linesScale)
    {
        if (!HasImage) return null;
        double sz = zoom, sx = px, sy = py;
        int sf = fitMode, srot = rot;
        bool sfast = fast;
        try
        {
            double lines = cur.Geom != null && cur.Geom.OrigH > 0 ? cur.Geom.OrigH : cur.H;
            zoom = Math.Max(1, linesScale) * lines / cur.H;          // RECOIL may already show each line more than once
            px = py = 0; fitMode = 0; fast = false;
            int w = Math.Max(1, (int)Math.Round(RW * zoom)), h = Math.Max(1, (int)Math.Round(RH * zoom));
            exportSize = new Size(w, h);
            DropFx(); DropSharp();
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                bool sharp = pixMode == 3 || (pixMode == 0 && cur.Note != null);
                bool crisp = pixMode == 1 || sharp;
                bool exact = Math.Abs(zoom - 1) < 1e-6 || (pixMode != 2 && zoom >= 3) || (zoom > 1 && crisp);
                bool scan = ScanShown, mask = MaskShown, glow = GlowShown, curve = CurveShown;
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Bg);
                    if (scan || mask || glow || curve)
                    {
                        Rectangle r = PicScreenRect();
                        Bitmap fx = EffectFrame(r, sharp, exact, scan, mask, glow, curve);
                        if (fx == null) throw new OutOfMemoryException();
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.None;
                        g.DrawImage(fx, r, 0, 0, r.Width, r.Height, GraphicsUnit.Pixel);
                    }
                    else { PicTransform(g); DrawPic(g, sharp, exact); }
                }
                return bmp;
            }
            catch (Exception) { bmp.Dispose(); throw; }
        }
        catch (OutOfMemoryException) { throw new InvalidOperationException("There is not enough memory to draw the picture with its screen effects."); }
        finally
        {
            exportSize = Size.Empty;
            zoom = sz; px = sx; py = sy; fitMode = sf; rot = srot; fast = sfast;
            DropFx(); DropSharp();
            Invalidate();
        }
    }

    // forgets the picture(s) and frees their memory (when a comparison ends)
    public void Release()
    {
        StopSlideshow();
        StopAnim();
        if (cur != null && !cache.ContainsValue(cur)) cur.Dispose();
        foreach (Pic p in cache.Values) p.Dispose();
        cache.Clear();
        DropSharp(); DropFx(); DropComposite(); DropGlow();
        stripCache.Clear(); stripStamps.Clear();
        cur = null; want = null; notice = null;
        files = new List<string>(); idx = -1;
        Invalidate();
    }

    // read by the menu bar (check marks, enabled entries)
    public bool HasPicture { get { return HasImage; } }
    public bool FitLock { get { return fitLock; } set { fitLock = value; } }
    public bool Checker { get { return checker; } set { checker = value; Invalidate(); } }
    public bool GridOn { get { return showGrid; } }
    public bool InfoOn { get { return showInfo; } set { showInfo = value; Invalidate(); } }
    public bool ExifOn { get { return showExif; } set { showExif = value; Invalidate(); } }
    public int SlideIndex { get { return slideIdx; } set { slideIdx = Math.Max(0, Math.Min(SlideSecs.Length - 1, value)); } }
    public bool Animated { get { return AnimActive; } }
    public bool AnimPaused { get { return animPaused; } }
    public int FileCount { get { return files.Count; } }
    // 0 = automatic (retro pictures sharp, photos smooth), 1 = crisp, 2 = smooth, 3 = sharp
    public int PixMode { get { return pixMode; } set { pixMode = Math.Max(0, Math.Min(3, value)); Invalidate(); } }

    // Fit-to-Screen (F): on = every picture is scaled to fit the window, off = every picture opens at 100%.
    // Starts off at every launch and is kept only while the program runs (nothing is saved to disk).
    // Zooming by hand only affects the current picture.
    bool fitLock;                          // remembered in settings.ini (off by default)
    bool showExif;                         // E: EXIF details panel (remembered in settings.ini)

    // own animation clock for animated GIFs, so they can be paused (P) and stepped frame by frame (, and .)
    readonly WinTimer animTimer = new WinTimer();
    int frame, frameCount;
    int[] frameDelays;                     // milliseconds per frame
    bool waitingFrame;                     // retro preview: playing, waiting for the next frame to be converted
    bool followFrames;                     // the frame shown is set from outside (ShowFrameOf), never by the own clock

    // Retro preview of an animation: the left side follows the right side's frames, so both always show the same one.
    // FrameShown reports each frame shown (also frame 0 when an animation starts).
    public event Action<int> FrameShown;
    public bool FollowFrames { get { return followFrames; } set { followFrames = value; if (value) animTimer.Stop(); else ScheduleFrame(); } }
    public void ShowFrameOf(int f) { if (AnimActive && f >= 0) ShowFrame(f % frameCount); }
    // the frame shown (0 for a still picture), and the colours an animation's preview keeps for every frame (or null)
    public int SimFrame { get { return AnimActive ? frame : 0; } }
    public void PauseAnimation()
    {
        if (!AnimActive || animPaused) return;
        animPaused = true; waitingFrame = false; animTimer.Stop();
        Flash("Animation paused at frame " + (frame + 1) + "   (P plays it again)");
    }
    public int SimFrameCount { get { return AnimActive ? frameCount : 1; } }
    public int SimFramesReady { get { return AnimActive && cur.Frames != null ? cur.Frames.Count : SimFrameCount; } }
    public object SimPicture { get { return cur; } }   // changes when the preview is made again or another picture is shown

    // Saving the retro preview's animation (UI thread, because the viewer paints these frames): every converted frame
    // as indices into one colour table, with each frame's time. Throws if the animation isn't complete.
    public void SimPlainFrames(out int w, out int h, out int[] palette, out List<byte[]> frames, out int[] delays)
    {
        if (!AnimActive || cur.Frames == null || cur.Frames.Count < frameCount) throw new InvalidOperationException("the animation isn't converted yet.");
        w = cur.Frames[0].Width; h = cur.Frames[0].Height;
        var map = new Dictionary<int, int>();
        var pal = new List<int>();
        frames = new List<byte[]>();
        var row = new int[w];
        for (int f = 0; f < frameCount; f++)
        {
            Bitmap b = cur.Frames[f];
            if (b.Width != w || b.Height != h) throw new InvalidOperationException("the frames are not all the same size.");
            var ix = new byte[w * h];
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, w);
                    for (int x = 0; x < w; x++)
                    {
                        int c = row[x] | unchecked((int)0xFF000000), k;
                        if (!map.TryGetValue(c, out k))
                        {
                            if (pal.Count == 256) throw new InvalidOperationException("the animation has more than 256 colours, more than a GIF can hold.");
                            k = pal.Count; map.Add(c, k); pal.Add(c);
                        }
                        ix[y * w + x] = (byte)k;
                    }
                }
            }
            finally { b.UnlockBits(d); }
            frames.Add(ix);
        }
        palette = pal.ToArray();
        delays = new int[frameCount];
        Array.Copy(frameDelays, delays, Math.Min(frameCount, frameDelays.Length));
    }

    // one frame of the animation drawn with its screen effects, as RenderAsShown draws the frame shown
    public Bitmap RenderFrameAsShown(int f, int linesScale)
    {
        if (!AnimActive || cur.Frames == null || f < 0 || f >= cur.Frames.Count) return null;
        int keep = frame;
        frame = f; cur.Img = cur.Frames[f];
        try { return RenderAsShown(linesScale); }
        finally { frame = keep; cur.Img = cur.Frames[keep]; Invalidate(); }
    }
    public int[] SimLock { get { return cur == null ? null : cur.SimLock; } }
    bool animPaused;
    bool flipX, flipY;                     // H / V: mirror the current picture (reset for the next one)
    bool checker;                          // B: checkerboard behind transparent pictures (remembered in settings.ini)
    TextureBrush checkerBrush;
    int checkerCell;

    readonly WinTimer settle = new WinTimer { Interval = 150 };
    readonly ImageAttributes ia = new ImageAttributes();
    readonly Font font = new Font("Segoe UI", 9f);
    readonly Font big = new Font("Segoe UI", 12f);
    readonly Font tiny = new Font("Segoe UI", 7.5f);

    // K, C, M, W and U (and a click on their badges)
    void CycleScan()
    {
        ScanMode = (scanMode + 1) % 4;
        Flash("Scanlines: " + ScanNames[scanMode] + (scanMode > 0 && HasImage && cur.Note == null ? " (retro pictures only)" : scanMode > 0 ? " (shown from about 300%)" : ""));
    }

    void ToggleTv()
    {
        TvAspect = !tvAspect;
        if (!tvAspect) Flash("True TV aspect off (pixels as RECOIL hands them over)");
        else if (HasImage && cur.Geom != null && cur.Geom.XPpm > 0)
            Flash("True TV aspect on: each pixel " + cur.Geom.PixelAspect.ToString("0.00") + " wide : 1 high, as on a real TV");
        else Flash("True TV aspect on (not known for this picture: shown unchanged)");
    }

    void CycleMask()
    {
        MaskMode = (maskMode + 1) % 4;
        Flash("Phosphor mask: " + MaskNames[maskMode] + (maskMode > 0 && HasImage && cur.Note == null ? " (retro pictures only)" : maskMode > 0 ? " (shown from 200%)" : ""));
    }

    void CycleGlow()
    {
        GlowMode = (glowMode + 1) % 3;
        Flash("Glow: " + GlowNames[glowMode] + (glowMode > 0 && HasImage && cur.Note == null ? " (retro pictures only)" : ""));
    }

    void CycleCurve()
    {
        CurveMode = (curveMode + 1) % 3;
        Flash("Screen curvature: " + CurveNames[curveMode] + (curveMode > 0 && HasImage && cur.Note == null ? " (retro pictures only)" : curveMode > 0 ? " (off at 800% and above)" : ""));
    }

    void CycleSignal()
    {
        SignalMode = (signalMode + 1) % 3;
        Flash("Video signal: " + SignalNames[signalMode] + (signalMode > 0 && HasImage && cur.Note == null ? " (retro pictures only)"
              : signalMode == 1 ? " (colours soften a little)" : signalMode == 2 ? " (dithering blends, as on a TV)" : ""));
    }

    // ---- effect badges: small pictures above the info bar, on retro pictures while the info bar is on (I), in two
    // rows. Lower row, the screen: the video signal's cable (an RGB / SCART plug, the round S-Video plug, the yellow
    // composite plug), the scanlines (a little screen with no, light, medium or strong lines), the phosphor mask
    // (grille stripes, slots or dots), the glow (a light with no, a soft or a strong halo), the curvature (a flat or
    // bulging screen). Upper row, the picture: True TV aspect (a square pixel, or the TV's pixel shape), the enlarged
    // pixels (crisp, sharp or smooth) and automatic effects (a monitor, lit when automatic chose the screen). With
    // the effects off (O), one badge says so instead and a click shows them again. A click on a badge does what its
    // key does (C, K, M, W, U, T, N, A, O). Drawn with GDI+, not font symbols. A badge is dimmed while its effect is
    // set but not shown (too small, too large or turned sideways). ShowBadges false hides them (before / after's
    // plain left side). EffectsClicked(badge): one was clicked (the main window keeps compare and before / after in step).
    public event Action<int> EffectsClicked;
    public bool ShowBadges = true;
    public const int BadgeSignal = 0, BadgeScan = 1, BadgeMask = 2, BadgeGlow = 3, BadgeCurve = 4, BadgeTv = 5, BadgePixels = 6, BadgeAuto = 7, BadgeOff = 8;
    const int BadgeCount = 9;
    readonly Rectangle[] badges = new Rectangle[BadgeCount];   // where they were drawn last (empty: not shown)
    int badgeHot = -1;                                     // the one under the pointer
    static readonly string[] BadgeTips =
    {
        "Click: next video signal (C)", "Click: next scanlines (K)", "Click: next phosphor mask (M)", "Click: next glow (W)", "Click: next curvature (U)",
        "Click: True TV aspect on / off (T)", "Click: crisp, sharp or smooth pixels (N)", "Click: automatic effects on / off (A)", "Click: show the screen effects (O)"
    };

    bool BadgesShown { get { return ShowBadges && showInfo && HasImage && cur.Note != null; } }

    int BadgeAt(Point p)
    {
        for (int i = 0; i < BadgeCount; i++) if (badges[i].Contains(p)) return i;
        return -1;
    }

    void ClearBadges() { for (int i = 0; i < BadgeCount; i++) badges[i] = Rectangle.Empty; }

    void CycleBadge(int which)
    {
        switch (which)
        {
            case BadgeSignal: CycleSignal(); break;
            case BadgeScan: CycleScan(); break;
            case BadgeMask: CycleMask(); break;
            case BadgeGlow: CycleGlow(); break;
            case BadgeCurve: CycleCurve(); break;
            case BadgeTv: ToggleTv(); break;
            case BadgePixels: CyclePixMode(); break;
            case BadgeAuto: ToggleAuto(); break;
            case BadgeOff: EffectsOff = false; Flash("Screen effects on"); break;
        }
    }

    void DrawBadges(Graphics g)
    {
        ClearBadges();
        if (!BadgesShown) return;
        float k = g.DpiY / 96f;
        int barH = (int)(font.GetHeight(g) + 10 * k);       // as DrawInfo
        int w = Util.S(64), h = Util.S(54), gap = Util.S(6), m = Util.S(8);
        int right = ClientSize.Width - m;
        int yLow = ViewH - barH - m - h, yHigh = yLow - gap - h;
        if (yLow < m) return;                               // no room
        if (fxOff)
            badges[BadgeOff] = new Rectangle(right - w, yLow, w, h);
        else
        {
            if (right - 5 * w - 4 * gap < m) return;       // no room for the lower row: none at all
            for (int i = 0; i < 5; i++) badges[i] = new Rectangle(right - (5 - i) * w - (4 - i) * gap, yLow, w, h);
            if (yHigh >= m)
                for (int i = 0; i < 3; i++) badges[BadgeTv + i] = new Rectangle(right - (3 - i) * w - (2 - i) * gap, yHigh, w, h);
        }
        SmoothingMode oldS = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        MachineInfo mi = MachineInfo.For(cur.Note);         // the computer badge: left of the upper row (or of "effects off")
        if (mi != null)
        {
            int mw = Math.Max(2 * w + gap, MachineWidth(g, mi));
            int mx = fxOff ? right - w - gap - mw : yHigh >= m ? right - 3 * w - 3 * gap - mw : -1;
            if (mx >= m) DrawMachine(g, new Rectangle(mx, fxOff ? yLow : yHigh, mw, h), mi);
        }
        int top = yLow;
        for (int i = 0; i < BadgeCount; i++)
            if (!badges[i].IsEmpty) { DrawBadge(g, badges[i], i); top = Math.Min(top, badges[i].Y); }
        top = DrawColoursUsed(g, right, top - gap, Math.Min(5 * w + 4 * gap, right - m), m, mi);   // above the badges
        if (badgeHot >= 0 && badgeHot < BadgeCount && !badges[badgeHot].IsEmpty)   // what a click does
        {
            string tip = BadgeTips[badgeHot];
            if (badgeHot == BadgeAuto && autoFx && autoChoice != null) tip = "Automatic: " + autoChoice.Name + ".  Click: your own effects (A)";
            SizeF ts = g.MeasureString(tip, font);
            var tr = new RectangleF(right - ts.Width - 8 * k, top - ts.Height - 8 * k, ts.Width + 8 * k, ts.Height + 4 * k);
            using (var tb = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(tb, tr);
            g.DrawString(tip, font, Brushes.Gainsboro, tr.X + 4 * k, tr.Y + 2 * k);
        }
        g.SmoothingMode = oldS;
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void DrawBadge(Graphics g, Rectangle r, int which)
    {
        bool known = cur.Geom != null && cur.Geom.XPpm > 0 && cur.Geom.YPpm > 0;
        PaintBadge(g, r, which, badgeHot == which, tiny, new BadgeState
        {
            Signal = signalMode, Scan = scanMode, ScanDim = ScanWanted && !ScanShown, Mask = maskMode, MaskDim = MaskWanted && !MaskShown,
            Glow = glowMode, Curve = curveMode, CurveDim = CurveWanted && !CurveShown, Tv = tvAspect, TvKnown = known,
            Aspect = known ? cur.Geom.PixelAspect : 1.0, PixMode = pixMode, Auto = autoFx
        });
    }

    // One badge, drawn from st (also used by the preview pane, where the badges can't be clicked)
    internal static void PaintBadge(Graphics g, Rectangle r, int which, bool hot, Font tiny, BadgeState st)
    {
        using (GraphicsPath bg = Rounded(r, Util.S(6)))
        {
            using (var b = new SolidBrush(Color.FromArgb(hot ? 215 : 165, 0, 0, 0))) g.FillPath(b, bg);
            using (var pen = new Pen(hot ? Color.FromArgb(60, 130, 230) : Color.FromArgb(90, 255, 255, 255), Math.Max(1f, Util.K))) g.DrawPath(pen, bg);
        }
        int labelH = tiny.Height + Util.S(2);
        var ir = new RectangleF(r.X + Util.S(8), r.Y + Util.S(5), r.Width - Util.S(16), r.Height - labelH - Util.S(8));
        string label;
        switch (which)
        {
            case 0:
                DrawCable(g, ir, st.Signal);
                label = st.Signal == 0 ? "RGB" : st.Signal == 1 ? "S-Video" : "Composite";
                break;
            case 1:
                DrawScreen(g, ir, st.Scan, st.ScanDim);              // too small to show the lines, or turned sideways
                label = st.Scan == 0 ? "No lines" : st.Scan == 1 ? "Light" : st.Scan == 2 ? "Medium" : "Strong";
                break;
            case 2:
                DrawMask(g, ir, st.Mask, st.MaskDim);                // below 200%, or turned sideways
                label = st.Mask == 0 ? "No mask" : st.Mask == 1 ? "Grille" : st.Mask == 2 ? "Slot" : "Dot";
                break;
            case 3:
                DrawGlow(g, ir, st.Glow);
                label = st.Glow == 0 ? "No glow" : st.Glow == 1 ? "Soft" : "Strong";
                break;
            case 4:
                DrawCurve(g, ir, st.Curve, st.CurveDim);             // off at 800% and above
                label = st.Curve == 0 ? "Flat" : st.Curve == 1 ? "Slight" : "Strong";
                break;
            case 5:
                DrawPixelShape(g, ir, st.Tv ? st.Aspect : 1.0, st.Tv && !st.TvKnown);
                label = !st.Tv ? "Square" : st.TvKnown ? "TV " + st.Aspect.ToString("0.00") : "TV (n/a)";
                break;
            case 6:
            {
                int eff = st.PixMode != 0 ? st.PixMode : 3;           // automatic: retro pictures are sharp
                DrawPixels(g, ir, eff);
                string n = eff == 1 ? "Crisp" : eff == 3 ? "Sharp" : "Smooth";
                label = st.PixMode == 0 ? "Auto " + n.ToLowerInvariant() : n;
                break;
            }
            case 7:
                DrawMonitor(g, ir, st.Auto, false);
                label = st.Auto ? "Auto" : "Own";
                break;
            default:
                DrawMonitor(g, ir, false, true);
                label = "Effects off";
                break;
        }
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(label, tiny, Brushes.Gainsboro, new RectangleF(r.X, r.Bottom - labelH - Util.S(2), r.Width, labelH), sf);
    }

    // ---- colours used (above the badges, not clickable): how many colours the picture shown uses, against its
    // computer's palette ("11 of 16 colours used"), with a strip of swatches, most used first, when there are at most
    // 64. Counted once per picture (and frame of an animation), from the picture as decoded.
    Image coloursOf;
    int coloursFrame = -1, coloursCount;
    int[] coloursPal;

    // draws the panel with its bottom at bottom, right-aligned at right, at most maxW wide; returns its top (or bottom
    // if it isn't shown: no room, too many pixels, or the picture can't be read)
    int DrawColoursUsed(Graphics g, int right, int bottom, int maxW, int m, MachineInfo mi)
    {
        int f = AnimActive ? frame : 0;
        if (coloursOf != cur.Img || coloursFrame != f)
        {
            coloursOf = cur.Img; coloursFrame = f; coloursCount = 0; coloursPal = null;
            var bm = cur.Img as Bitmap;
            if (bm != null && (long)bm.Width * bm.Height <= 16000000L)
            {
                var r = new ThumbResult();
                try { ThumbService.CountColours(bm, r); coloursCount = r.Colours; coloursPal = r.Palette; }
                catch (Exception) { coloursCount = 0; coloursPal = null; }
            }
        }
        if (coloursCount <= 0 || maxW < Util.S(120)) return bottom + Util.S(6);
        int pad = Util.S(7), sw = Util.S(12), sg = Util.S(2), lh = tiny.Height + Util.S(2);
        int inner = maxW - 2 * pad, perRow = Math.Max(1, (inner + sg) / (sw + sg));
        int n = coloursPal != null ? coloursPal.Length : 0, rows = (n + perRow - 1) / perRow;
        string text = MachineInfo.UsedText(coloursCount, mi);
        int textW = (int)Math.Ceiling(g.MeasureString(text, tiny).Width);
        int pw = Math.Min(maxW, Math.Max(textW, Math.Min(n, perRow) * (sw + sg) - sg) + 2 * pad);
        int ph = pad + lh + (rows > 0 ? Util.S(3) + rows * (sw + sg) - sg : 0) + pad;
        var pr = new Rectangle(right - pw, bottom - ph, pw, ph);
        if (pr.Y < m) return bottom + Util.S(6);
        using (GraphicsPath bg = Rounded(pr, Util.S(6)))
        {
            using (var b = new SolidBrush(Color.FromArgb(165, 0, 0, 0))) g.FillPath(b, bg);
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1f, Util.K))) g.DrawPath(pen, bg);
        }
        g.DrawString(text, tiny, Brushes.Gainsboro, pr.X + pad, pr.Y + pad);
        SmoothingMode oldS = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        int y0 = pr.Y + pad + lh + Util.S(3);
        for (int i = 0; i < n; i++)
        {
            var sr = new Rectangle(pr.X + pad + (i % perRow) * (sw + sg), y0 + (i / perRow) * (sw + sg), sw, sw);
            using (var b = new SolidBrush(Color.FromArgb(coloursPal[i]))) g.FillRectangle(b, sr);
            using (var pen = new Pen(Color.FromArgb(90, 90, 90))) g.DrawRectangle(pen, sr.X, sr.Y, sr.Width - 1, sr.Height - 1);
        }
        g.SmoothingMode = oldS;
        return pr.Y;
    }

    // The computer badge (not clickable): maker and flags, then the model, then year and palette size
    readonly Font tinyBold = new Font("Segoe UI", 7.5f, FontStyle.Bold);

    int MachineWidth(Graphics g, MachineInfo mi) { return MachineBadgeWidth(g, mi, tiny, tinyBold); }

    void DrawMachine(Graphics g, Rectangle r, MachineInfo mi) { PaintMachine(g, r, mi, tiny, tinyBold); }

    internal static int MachineBadgeWidth(Graphics g, MachineInfo mi, Font tiny, Font tinyBold)
    {
        int flags = mi.FlagList.Length * (Util.S(17) + Util.S(3));
        float a = g.MeasureString(mi.Maker, tinyBold).Width + flags, b = g.MeasureString(mi.Model, tiny).Width, c = g.MeasureString(mi.Facts, tiny).Width;
        return (int)Math.Ceiling(Math.Max(a, Math.Max(b, c))) + Util.S(18);
    }

    internal static void PaintMachine(Graphics g, Rectangle r, MachineInfo mi, Font tiny, Font tinyBold)
    {
        using (GraphicsPath bg = Rounded(r, Util.S(6)))
        {
            using (var b = new SolidBrush(Color.FromArgb(165, 0, 0, 0))) g.FillPath(b, bg);
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1f, Util.K))) g.DrawPath(pen, bg);
        }
        float lh = tiny.GetHeight(g), x = r.X + Util.S(8), y = r.Y + (r.Height - 3 * lh) / 2;
        float fw = Util.S(17), fh = Util.S(11), fx = r.Right - Util.S(8) - fw;
        string[] flags = mi.FlagList;
        for (int i = flags.Length - 1; i >= 0; i--, fx -= fw + Util.S(3))
            MachineInfo.DrawFlag(g, new RectangleF(fx, y + (lh - fh) / 2, fw, fh), flags[i]);
        using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
        {
            g.DrawString(mi.Maker, tinyBold, Brushes.White, new RectangleF(x, y, fx + fw - x, lh), sf);
            g.DrawString(mi.Model, tiny, Brushes.Gainsboro, new RectangleF(x, y + lh, r.Right - Util.S(8) - x, lh), sf);
            g.DrawString(mi.Facts, tiny, Brushes.Silver, new RectangleF(x, y + 2 * lh, r.Right - Util.S(8) - x, lh), sf);
        }
    }

    // one pixel's shape: a square, or the TV's (aspect = width : height, drawn more pronounced so the difference shows)
    static void DrawPixelShape(Graphics g, RectangleF ir, double aspect, bool dim)
    {
        float s = Math.Min(ir.Width, ir.Height) * 0.8f, cx = ir.X + ir.Width / 2, cy = ir.Y + ir.Height / 2;
        int a = dim ? 110 : 255;
        double shown = Math.Max(0.6, Math.Min(1.4, 1 + (aspect - 1) * 4));
        float pw = (float)(s * Math.Min(1.0, shown)), ph = (float)(s * Math.Min(1.0, 1 / shown));
        using (var guide = new Pen(Color.FromArgb(a / 2, 175, 180, 190), Math.Max(1f, s / 24)) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(guide, cx - s / 2, cy - s / 2, s, s);       // a square, for comparison
        using (var b = new SolidBrush(Color.FromArgb(a, 240, 150, 60))) g.FillRectangle(b, cx - pw / 2, cy - ph / 2, pw, ph);
    }

    static Bitmap checker2;                               // 2 x 2 pixels of two colours, enlarged for the pixels badge

    // a 2 x 2 block of pixels enlarged crisp (hard edges), sharp (hard, with soft seams) or smooth (blurred)
    static void DrawPixels(Graphics g, RectangleF ir, int mode)
    {
        if (checker2 == null)
        {
            var b = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
            b.SetPixel(0, 0, Color.FromArgb(240, 150, 60)); b.SetPixel(1, 1, Color.FromArgb(240, 150, 60));
            b.SetPixel(1, 0, Color.FromArgb(60, 110, 200)); b.SetPixel(0, 1, Color.FromArgb(60, 110, 200));
            checker2 = b;
        }
        float s = Math.Min(ir.Width, ir.Height) * 0.9f, x = ir.X + (ir.Width - s) / 2, y = ir.Y + (ir.Height - s) / 2;
        InterpolationMode oi = g.InterpolationMode;
        PixelOffsetMode op = g.PixelOffsetMode;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var ia = new ImageAttributes())
        {
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = mode == 2 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
            g.DrawImage(checker2, new Rectangle((int)x, (int)y, (int)s, (int)s), 0, 0, 2, 2, GraphicsUnit.Pixel, ia);
        }
        if (mode == 3)                                      // sharp: only the seams between pixels are blended
            using (var seam = new Pen(Color.FromArgb(160, 150, 130, 130), Math.Max(1.5f, s / 14)))
            {
                g.DrawLine(seam, x + s / 2, y, x + s / 2, y + s);
                g.DrawLine(seam, x, y + s / 2, x + s, y + s / 2);
            }
        g.InterpolationMode = oi;
        g.PixelOffsetMode = op;
        using (var pen = new Pen(Color.FromArgb(175, 180, 190), Math.Max(1f, s / 24))) g.DrawRectangle(pen, x, y, s, s);
    }

    // a small monitor: lit when automatic effects chose the screen, dark for your own, crossed out when effects are off
    static void DrawMonitor(Graphics g, RectangleF ir, bool lit, bool crossed)
    {
        float w = Math.Min(ir.Width, ir.Height * 1.3f), h = w / 1.3f, x = ir.X + (ir.Width - w) / 2, y = ir.Y + (ir.Height - h) / 2;
        float sh = h * 0.78f;
        var screen = new RectangleF(x, y, w, sh);
        using (GraphicsPath p = Rounded(screen, Math.Max(2f, sh / 7)))
        {
            using (var b = lit ? (Brush)new LinearGradientBrush(screen, Color.FromArgb(110, 200, 255), Color.FromArgb(40, 110, 190), LinearGradientMode.Vertical)
                               : new SolidBrush(Color.FromArgb(45, 48, 55)))
                g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(175, 180, 190), Math.Max(1.5f, h / 12))) g.DrawPath(pen, p);
        }
        using (var stand = new SolidBrush(Color.FromArgb(175, 180, 190)))
        {
            g.FillRectangle(stand, x + w * 0.44f, y + sh, w * 0.12f, h * 0.12f);
            g.FillRectangle(stand, x + w * 0.3f, y + sh + h * 0.12f, w * 0.4f, h * 0.08f);
        }
        if (lit)
            using (var f = new Font("Segoe UI", Math.Max(6f, sh * 0.42f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("A", f, Brushes.White, screen, sf);
        if (crossed)
            using (var pen = new Pen(Color.FromArgb(255, 140, 90), Math.Max(2f, h / 9)))
                g.DrawLine(pen, x + w * 0.15f, y + sh * 0.85f, x + w * 0.85f, y + sh * 0.15f);
    }

    // the phosphors, enlarged: aperture grille stripes, staggered slots, or dots (none: a dark square)
    static void DrawMask(Graphics g, RectangleF ir, int mode, bool dim)
    {
        float s = Math.Min(ir.Width, ir.Height), x = ir.X + (ir.Width - s) / 2, y = ir.Y + (ir.Height - s) / 2;
        var sq = new RectangleF(x, y, s, s);
        int a = dim ? 110 : 255;
        using (GraphicsPath p = Rounded(sq, Math.Max(2f, s / 8)))
        {
            using (var b = new SolidBrush(Color.FromArgb(a, 20, 20, 24))) g.FillPath(b, p);
            if (mode > 0)
            {
                GraphicsState st = g.Save();
                g.SetClip(p, CombineMode.Intersect);
                Color[] rgb = { Color.FromArgb(a, 230, 60, 60), Color.FromArgb(a, 60, 210, 80), Color.FromArgb(a, 70, 120, 245) };
                float col = s / 7.5f;                       // one phosphor's width
                if (mode == 1)                              // aperture grille: unbroken stripes
                {
                    for (int i = 0; i * col < s; i++)
                        using (var b = new SolidBrush(rgb[i % 3])) g.FillRectangle(b, x + i * col + col * 0.12f, y, col * 0.76f, s);
                }
                else if (mode == 2)                         // slot mask: short slots, every other triad half a slot lower
                {
                    float slot = s / 3.2f;
                    for (int i = 0; i * col < s; i++)
                    {
                        float off = ((i / 3) & 1) == 0 ? 0 : slot / 2;
                        using (var b = new SolidBrush(rgb[i % 3]))
                            for (float sy = y - slot + off; sy < y + s; sy += slot)
                                g.FillRectangle(b, x + i * col + col * 0.12f, sy + slot * 0.1f, col * 0.76f, slot * 0.8f);
                    }
                }
                else                                        // dot mask: round dots in triangles
                {
                    float d = s / 4.6f;
                    for (int row = 0; row * d * 0.9f < s + d; row++)
                        for (int c = 0; c * d < s + d; c++)
                        {
                            float cx = x + c * d + ((row & 1) == 0 ? 0 : d / 2), cy = y + row * d * 0.87f;
                            using (var b = new SolidBrush(rgb[(c + row * 2) % 3])) g.FillEllipse(b, cx - d * 0.4f, cy - d * 0.4f, d * 0.8f, d * 0.8f);
                        }
                }
                g.Restore(st);
            }
            using (var pen = new Pen(Color.FromArgb(a, 175, 180, 190), Math.Max(1.5f, s / 14))) g.DrawPath(pen, p);
        }
    }

    // a bright light with no, a soft or a strong halo
    static void DrawGlow(Graphics g, RectangleF ir, int mode)
    {
        float cx = ir.X + ir.Width / 2, cy = ir.Y + ir.Height / 2, s = Math.Min(ir.Width, ir.Height);
        if (mode > 0)
        {
            float hr = s * (mode == 1 ? 0.42f : 0.56f);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - hr, cy - hr, hr * 2, hr * 2);
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterColor = Color.FromArgb(mode == 1 ? 150 : 230, 255, 245, 210);
                    pb.SurroundColors = new[] { Color.FromArgb(0, 255, 245, 210) };
                    g.FillPath(pb, path);
                }
            }
        }
        float d = s * 0.32f;
        using (var b = new SolidBrush(mode == 0 ? Color.FromArgb(150, 150, 150) : Color.FromArgb(255, 255, 250, 235))) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
    }

    // a screen whose sides are straight (flat) or bulge a little or a lot, with darker corners when curved
    static void DrawCurve(Graphics g, RectangleF ir, int mode, bool dim)
    {
        float w = Math.Min(ir.Width, ir.Height * 1.4f), h = w / 1.4f, x = ir.X + (ir.Width - w) / 2, y = ir.Y + (ir.Height - h) / 2;
        int a = dim ? 110 : 255;
        float bx = mode == 0 ? 0 : w * (mode == 1 ? 0.06f : 0.12f), by = mode == 0 ? 0 : h * (mode == 1 ? 0.07f : 0.14f);
        using (var p = new GraphicsPath())
        {
            // each side a curve bowing outwards by bx / by (straight lines when flat)
            p.AddBezier(x, y, x + w / 3, y - by, x + w * 2 / 3, y - by, x + w, y);
            p.AddBezier(x + w, y, x + w + bx, y + h / 3, x + w + bx, y + h * 2 / 3, x + w, y + h);
            p.AddBezier(x + w, y + h, x + w * 2 / 3, y + h + by, x + w / 3, y + h + by, x, y + h);
            p.AddBezier(x, y + h, x - bx, y + h * 2 / 3, x - bx, y + h / 3, x, y);
            p.CloseFigure();
            using (var pb = new PathGradientBrush(p))
            {
                pb.CenterPoint = new PointF(x + w / 2, y + h / 2);
                pb.CenterColor = Color.FromArgb(a, 70, 150, 200);
                pb.SurroundColors = new[] { mode == 0 ? Color.FromArgb(a, 55, 125, 175) : Color.FromArgb(a, 18, 45, 80) };
                g.FillPath(pb, p);
            }
            using (var pen = new Pen(Color.FromArgb(a, 175, 180, 190), Math.Max(1.5f, h / 12))) g.DrawPath(pen, p);
        }
    }

    // the cable's plug: SCART with red, green and blue pins (RGB), the round 4-pin mini-DIN (S-Video), the yellow RCA (composite)
    static void DrawCable(Graphics g, RectangleF ir, int mode)
    {
        float cx = ir.X + ir.Width / 2, cy = ir.Y + ir.Height / 2, s = Math.Min(ir.Width, ir.Height);
        if (mode == 0)
        {
            float w = s * 1.35f, h = s * 0.62f, x = cx - w / 2, y = cy - h / 2, cut = h * 0.35f;
            var pts = new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x + w, y + h - cut), new PointF(x + w - cut, y + h), new PointF(x, y + h) };
            using (var b = new SolidBrush(Color.FromArgb(170, 175, 185))) g.FillPolygon(b, pts);
            using (var pen = new Pen(Color.FromArgb(90, 95, 105), Math.Max(1f, s / 22))) g.DrawPolygon(pen, pts);
            Color[] cols = { Color.FromArgb(235, 60, 60), Color.FromArgb(60, 200, 80), Color.FromArgb(70, 120, 245) };
            float d = h * 0.42f;
            for (int i = 0; i < 3; i++)
                using (var b = new SolidBrush(cols[i])) g.FillEllipse(b, x + w * (0.2f + 0.27f * i) - d / 2, cy - d / 2, d, d);
        }
        else if (mode == 1)
        {
            float d = s * 0.92f;
            using (var b = new SolidBrush(Color.FromArgb(55, 55, 60))) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            using (var pen = new Pen(Color.FromArgb(175, 180, 190), Math.Max(1.5f, s / 12))) g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
            float p = d * 0.14f;
            PointF[] pins = { new PointF(cx - d * 0.2f, cy - d * 0.12f), new PointF(cx + d * 0.2f, cy - d * 0.12f), new PointF(cx - d * 0.26f, cy + d * 0.16f), new PointF(cx + d * 0.26f, cy + d * 0.16f) };
            foreach (PointF q in pins) g.FillEllipse(Brushes.WhiteSmoke, q.X - p / 2, q.Y - p / 2, p, p);
            using (var b = new SolidBrush(Color.FromArgb(175, 180, 190))) g.FillRectangle(b, cx - d * 0.09f, cy + d * 0.28f, d * 0.18f, d * 0.12f);   // the key
        }
        else
        {
            float d = s * 0.92f, inner = d * 0.5f, pin = d * 0.16f;
            using (var b = new SolidBrush(Color.FromArgb(245, 205, 40))) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            using (var b = new SolidBrush(Color.FromArgb(60, 55, 30))) g.FillEllipse(b, cx - inner / 2, cy - inner / 2, inner, inner);
            using (var b = new SolidBrush(Color.FromArgb(220, 220, 225))) g.FillEllipse(b, cx - pin / 2, cy - pin / 2, pin, pin);
        }
    }

    // a little screen with its scanlines: none, light, medium or strong (dim: wanted but not shown at this size)
    static void DrawScreen(Graphics g, RectangleF ir, int mode, bool dim)
    {
        float w = Math.Min(ir.Width, ir.Height * 1.4f), h = w / 1.4f, x = ir.X + (ir.Width - w) / 2, y = ir.Y + (ir.Height - h) / 2;
        var sr = new RectangleF(x, y, w, h);
        int a = dim ? 110 : 255;
        using (GraphicsPath p = Rounded(sr, Math.Max(2f, h / 7)))
        {
            using (var b = new LinearGradientBrush(sr, Color.FromArgb(a, 70, 150, 200), Color.FromArgb(a, 30, 70, 120), LinearGradientMode.Vertical)) g.FillPath(b, p);
            if (mode > 0)
            {
                GraphicsState st = g.Save();
                g.SetClip(p, CombineMode.Intersect);
                float step = Math.Max(3f, h / 6f), thick = step * (mode == 1 ? 0.3f : mode == 2 ? 0.45f : 0.6f);
                int la = (mode == 1 ? 110 : mode == 2 ? 170 : 230) * a / 255;
                using (var lb = new SolidBrush(Color.FromArgb(la, 0, 0, 0)))
                    for (float ly = y + step - thick; ly < y + h; ly += step) g.FillRectangle(lb, x, ly, w, thick);
                g.Restore(st);
            }
            using (var pen = new Pen(Color.FromArgb(a, 175, 180, 190), Math.Max(1.5f, h / 12))) g.DrawPath(pen, p);
        }
    }

    public event Action<string, int> ImageChanged;
    public string Want { get { return want; } }

    // what "Save as PNG" / "Copy picture" use while viewing: the picture's file plus any rotation and mirroring
    // you added on top of its own
    public bool GetSaveSource(out string path, out int extraTurns, out bool fx, out bool fy)
    {
        path = null; extraTurns = 0; fx = fy = false;
        if (cur == null || cur.Img == null) return false;
        path = cur.FullPath;
        extraTurns = (rot - cur.Rot) & 3;
        fx = flipX; fy = flipY;
        return true;
    }

    // the file currently shown (also when it failed to decode), or null
    public string CurrentPath { get { return cur != null ? cur.FullPath : null; } }
    public string Platform { get { return HasImage ? cur.Note : null; } }        // retro pictures: e.g. "Commodore 64"

    // marks of the picture shown (read once per picture; MarksChanged after marking)
    string markPath;
    Mark curMark;
    Mark CurMark
    {
        get
        {
            if (cur == null) return null;
            if (!string.Equals(markPath, cur.FullPath, StringComparison.Ordinal)) { markPath = cur.FullPath; curMark = Marks.ForFile(markPath); }
            return curMark;
        }
    }
    public void MarksChanged() { markPath = null; Invalidate(); }

    // the current file was changed on disk (a lossless turn): read it again and show it as it is now
    public bool ExtraTurns { get { return cur != null && ((rot - cur.Rot) & 3) != 0; } }
    public void Reload()
    {
        if (cur == null || idx < 0 || idx >= files.Count) return;
        string p = cur.FullPath;
        StopAnim();
        Pic c = cur, old;
        if (cache.TryGetValue(p, out old)) { cache.Remove(p); if (old != c) old.Dispose(); }
        cur = null;
        if (c != null) c.Dispose();
        DropSharp(); DropFx(); DropComposite(); DropGlow();
        markPath = null;
        stripStamps.Remove(p);
        GoTo(idx);
    }
    public void Next() { Step(1); }                       // auto-advance after marking

    // The current file was deleted: drop it from the list and show its neighbour. False if nothing is left.
    public bool RemoveCurrent()
    {
        if (cur == null) return files.Count > 0;
        string gone = cur.FullPath;
        int at = files.FindIndex(x => string.Equals(x, gone, StringComparison.OrdinalIgnoreCase));
        files = new List<string>(files);                   // the list may be the grid's own: never change that one
        if (at >= 0) files.RemoveAt(at);
        Pic p;
        if (cache.TryGetValue(gone, out p)) { cache.Remove(gone); if (p != cur) p.Dispose(); }
        StopAnim();
        Pic old = cur;
        cur = null;
        if (old != null) old.Dispose();
        if (files.Count == 0) { idx = -1; want = null; Invalidate(); return false; }
        idx = -1;
        GoTo(Math.Max(0, Math.Min(at < 0 ? 0 : at, files.Count - 1)));
        return true;
    }

    public Canvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Bg; Dock = DockStyle.Fill; TabStop = true; Visible = false;
        ia.SetWrapMode(WrapMode.TileFlipXY);          // no faded edges when scaling
        settle.Tick += delegate { settle.Stop(); fast = false; Invalidate(); };
        flashTimer.Tick += delegate { flashTimer.Stop(); flash = null; Invalidate(); };
        slideTimer.Tick += delegate { Step(1); };
        animTimer.Tick += delegate
        {
            animTimer.Stop();
            if (!AnimActive || followFrames) return;
            if (!ShowFrame(frame + 1)) { waitingFrame = true; return; }   // retro preview: that frame isn't converted yet
            ScheduleFrame();
        };
    }

    // ---------------------------------------------------------------- public API

    // Show one image right away; the folder list arrives later through SetList.
    public void ShowSingle(string path)
    {
        files = new List<string> { path };
        idx = -1;
        if (cur == null || !string.Equals(cur.FullPath, path, StringComparison.OrdinalIgnoreCase)) Clear();
        GoTo(0);
    }

    public void Open(List<string> list, int index)
    {
        files = list; idx = -1;
        if (index < 0 || index >= list.Count) return;
        if (cur == null || !string.Equals(cur.FullPath, list[index], StringComparison.OrdinalIgnoreCase)) Clear();
        GoTo(index);
    }

    public void SetList(List<string> list, string current)
    {
        files = list;
        int i = current == null ? -1 : list.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
        idx = i;
        if (i >= 0) { Prefetch(); Trim(); }
        stripStamps.Clear();                               // the folder was read again: dates may have changed
        if (strip) { if (HasImage) { Refit(); Clamp(); } Invalidate(); }
    }

    public bool HandleKey(Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0;
        int digit = k >= Keys.D2 && k <= Keys.D9 ? (int)k - (int)Keys.D0
                  : k >= Keys.NumPad2 && k <= Keys.NumPad9 ? (int)k - (int)Keys.NumPad0 : 0;
        if (digit >= 2) { ZoomCenter(digit); return true; }                  // 2..9 = 200% .. 900%
        switch (k)
        {
            case Keys.Right: case Keys.PageDown: case Keys.Space: Step(1); return true;
            case Keys.Left: case Keys.PageUp: case Keys.Back: Step(-1); return true;
            case Keys.Home: GoTo(0); return true;
            case Keys.End: GoTo(files.Count - 1); return true;
            case Keys.Add: case Keys.Oemplus: ZoomCenter(NextZoom(1)); return true;
            case Keys.Subtract: case Keys.OemMinus: ZoomCenter(NextZoom(-1)); return true;
            case Keys.F: if (ctrl) break; ToggleFitLock(); return true;
            case Keys.D0: case Keys.NumPad0: SetFit(2); return true;
            case Keys.H: if (ctrl) break; Flip(true); return true;
            case Keys.V: if (ctrl) break; Flip(false); return true;
            case Keys.B: if (ctrl) break; ToggleChecker(); return true;
            case Keys.D1: case Keys.NumPad1: ZoomCenter(1); return true;
            case Keys.R: Rotate(shift ? -1 : 1); return true;
            case Keys.L: Rotate(-1); return true;
            case Keys.I: showInfo = !showInfo; Invalidate(); return true;
            case Keys.E:
                if (ctrl) break;
                showExif = !showExif;
                Invalidate();
                return true;
            case Keys.P: if (ctrl) break; ToggleAnimPause(); return true;
            case Keys.T:
                if (ctrl) break;
                ToggleTv();
                return true;
            case Keys.K:
                if (ctrl) break;
                CycleScan();
                return true;
            case Keys.C:
                if (ctrl) break;
                CycleSignal();
                return true;
            case Keys.J:
                if (ctrl) break;
                Filmstrip = !strip;
                Flash(strip ? (files.Count > 1 ? "Filmstrip on (click a thumbnail to show it)" : "Filmstrip on (shown when the folder has more than one picture)") : "Filmstrip off");
                return true;
            case Keys.Z:
                if (ctrl) break;
                if (!loupe && HasImage) { loupe = true; Invalidate(); }      // shown while Z is held down
                return true;
            case Keys.U:
                if (ctrl) break;
                CycleCurve();
                return true;
            case Keys.W:
                if (ctrl) break;
                CycleGlow();
                return true;
            case Keys.A:
                if (ctrl) break;
                ToggleAuto();
                return true;
            case Keys.O:
                if (ctrl) break;
                EffectsOff = !fxOff;
                Flash(fxOff ? "Screen effects off: the picture as decoded   (O shows them again)" : "Screen effects on");
                return true;
            case Keys.M:
                if (ctrl) break;
                CycleMask();
                return true;
            case Keys.Oemcomma: StepFrame(-1); return true;
            case Keys.OemPeriod: StepFrame(1); return true;
            case Keys.S: if (ctrl) break; ToggleSlideshow(); return true;
            case Keys.G:
                if (ctrl) break;
                showGrid = !showGrid;
                Flash(showGrid ? "Pixel grid on (shown at 800% and above)" : "Pixel grid off");
                Invalidate();
                return true;
            case Keys.N: if (ctrl) break; CyclePixMode(); return true;
            case Keys.Up: if (!slideshow) break; ChangeSlide(1); return true;
            case Keys.Down: if (!slideshow) break; ChangeSlide(-1); return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- navigation / loading

    void Step(int d)
    {
        int n = files.Count;
        if (n > 1) GoTo((((idx < 0 ? 0 : idx) + d) % n + n) % n);
    }

    void GoTo(int i)
    {
        if (i < 0 || i >= files.Count) return;
        if (slideshow) { slideTimer.Stop(); slideTimer.Start(); }      // a manual step restarts the countdown
        idx = i;
        want = files[i];
        Pic p;
        bool have = cache.TryGetValue(want, out p);
        if (have && p.Stamp != FileStamp(want))            // replaced, changed or re-made since it was read: read it again
        {
            cache.Remove(want);
            if (p == cur) { StopAnim(); cur = null; DropSharp(); DropFx(); DropComposite(); DropGlow(); }
            p.Dispose();
            stripStamps.Remove(want);
            have = false;
        }
        if (have) Display(p);
        else
        {
            if (sim != null)                               // retro preview: no older picture in the meantime
            {
                Clear();
                notice = "Making the " + RetroConvert.PreviewShort[Math.Max(0, Math.Min(RetroConvert.PreviewShort.Length - 1, sim.Machine))] + " preview...";
            }
            Request(want);
        }
        Prefetch();
    }

    // the file's size and modified time in one number (0 if it is not there); kept with each decoded picture, so a
    // file that was deleted and made again under the same name (a new conversion, say) is never shown from memory
    static long FileStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.LastWriteTimeUtc.Ticks ^ (fi.Length * 1000003L) : 0;
        }
        catch (Exception) { return 0; }
    }

    void Prefetch()
    {
        int n = files.Count;
        if (n < 2 || idx < 0) return;
        Request(files[(idx + 1) % n]);
        Request(files[(idx + n - 1) % n]);
    }

    bool IsNear(string p)
    {
        int n = files.Count;
        if (idx < 0 || n == 0) return false;
        for (int d = -1; d <= 1; d++)
            if (string.Equals(files[((idx + d) % n + n) % n], p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    void Request(string path)
    {
        if (cache.ContainsKey(path) || pending.Contains(path)) return;
        pending.Add(path);
        Control host = FindForm();
        RetroSim s = sim;
        int gen = simGen;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Pic p = s != null ? DecodeSim(path, s) : Decode(path);
            if (!Util.Post(host, delegate { Loaded(p, gen); })) p.Dispose();
        });
    }

    void Loaded(Pic p, int gen)
    {
        if (gen != simGen) { p.Dispose(); return; }        // made for an earlier retro preview choice
        pending.Remove(p.FullPath);
        if (!IsNear(p.FullPath) || cache.ContainsKey(p.FullPath)) { p.Dispose(); return; }
        if (p.Stamp != FileStamp(p.FullPath))              // the file changed while it was being read: read it again
        {
            string again = p.FullPath;
            p.Dispose();
            Request(again);                                 // stops by itself once the file stays the same
            return;
        }
        cache[p.FullPath] = p;
        if (string.Equals(p.FullPath, want, StringComparison.OrdinalIgnoreCase)) Display(p);
    }

    void StopAnim() { animTimer.Stop(); frameCount = 0; animPaused = false; waitingFrame = false; }

    bool AnimActive { get { return cur != null && cur.Anim && cur.Img != null && frameCount > 1; } }

    void StartAnim(Pic p)
    {
        frame = 0; frameCount = 0; animPaused = false; waitingFrame = false;
        if (p.Frames != null)                              // animated WebP, or the retro preview of an animation: frames
        {                                                  // put together, delays in ms (the preview's frames still arriving)
            frameCount = p.Delays.Length;
            frameDelays = (int[])p.Delays.Clone();
            p.Img = p.Frames[0];
        }
        else
        {
            try
            {
                frameCount = p.Img.GetFrameCount(FrameDimension.Time);
                frameDelays = Exporter.GifDelays(p.Img, frameCount);
                p.Img.SelectActiveFrame(FrameDimension.Time, 0);
            }
            catch (Exception) { frameCount = 0; }
        }
        if (frameCount > 1)
        {
            var h = FrameShown;
            if (h != null) h(0);
            ScheduleFrame();
        }
    }

    void ScheduleFrame()
    {
        animTimer.Stop();
        if (!AnimActive || animPaused || !Visible || followFrames) return;
        animTimer.Interval = Math.Max(10, frameDelays[Math.Min(frame, frameDelays.Length - 1)]);
        animTimer.Start();
    }

    // false when that frame of a retro preview isn't converted yet (nothing changes)
    bool ShowFrame(int f)
    {
        if (!AnimActive) return false;
        int to = ((f % frameCount) + frameCount) % frameCount;
        if (cur.Frames != null && to >= cur.Frames.Count) return false;
        frame = to;
        if (cur.Frames != null) cur.Img = cur.Frames[frame];          // animated WebP or retro preview: show that frame
        else try { cur.Img.SelectActiveFrame(FrameDimension.Time, frame); } catch (Exception) { }
        Invalidate();
        var h = FrameShown;
        if (h != null) h(frame);
        return true;
    }

    void ToggleAnimPause()
    {
        if (!AnimActive) { Flash("This picture is not animated"); return; }   // animated GIF or WebP
        animPaused = !animPaused;
        waitingFrame = false;
        if (animPaused) animTimer.Stop(); else ScheduleFrame();
        Flash(animPaused ? "Animation paused   (, and . step one frame)" : "Animation playing");
    }

    void StepFrame(int d)
    {
        if (!AnimActive) return;
        animPaused = true;
        animTimer.Stop();
        waitingFrame = false;
        if (!ShowFrame(frame + d)) Flash("Frame " + (((frame + d) % frameCount + frameCount) % frameCount + 1) + " is still being converted");
    }

    void Clear() { StopAnim(); cur = null; notice = null; DropSharp(); DropFx(); DropComposite(); DropGlow(); Invalidate(); }

    void Display(Pic p)
    {
        StopAnim();
        cur = p;
        notice = p.Err != null ? "Can't open this file\n" + Path.GetFileName(p.FullPath) + "\n" + p.Err : null;
        rot = p.Rot;
        flipX = flipY = false;
        if (fitLock) fitMode = 2;                          // Fit-to-Screen on: fit the window (small pictures grow)
        else { fitMode = 0; zoom = 1; px = py = 0; }       // off: 100%, centred
        fast = false;
        if (p.Anim && p.Img != null)
        {
            StartAnim(p);
            if (p.Sim != null && p.Frames != null && p.Frames.Count < p.Delays.Length)
            {
                Flash("Converting the animation's " + p.Delays.Length + " frames: it plays as they are ready");
                SimMore(p);
            }
        }
        ApplyAuto();                                       // automatic effects: chosen for this picture's computer
        Refit();
        Trim();
        Invalidate();
        var h = ImageChanged;
        if (h != null) h(p.FullPath, idx);
    }

    // keep only the current picture and its two neighbours in memory
    void Trim()
    {
        var drop = new List<string>();
        foreach (KeyValuePair<string, Pic> kv in cache)
            if (kv.Value != cur && !IsNear(kv.Key)) drop.Add(kv.Key);
        foreach (string k in drop) { cache[k].Dispose(); cache.Remove(k); }
    }

    // retro preview, on worker threads: the picture converted in memory for s's computer, as RECOIL shows the result.
    // An animated GIF or WebP: its first frame now, converted with the colours kept for every frame (PreviewLock, from
    // up to 8 frames spread over the animation); the rest follow in the background once it is shown (SimMore).
    const int SimSampleFrames = 8;
    const long SimMaxBytes = 384L * 1024 * 1024;           // all the converted frames together; the rest are left out

    static Pic DecodeSim(string path, RetroSim s)
    {
        var p = new Pic { FullPath = path, Stamp = FileStamp(path) };
        AnimSource anim = null;
        Bitmap first = null;
        try
        {
            try { anim = AnimSource.Open(path); }
            catch (Exception) { anim = null; }             // a damaged animation: the still decoder below says why
            int[] lockPal = null;
            if (anim != null)
            {
                var sample = new List<Bitmap>();
                try
                {
                    int n = Math.Min(SimSampleFrames, anim.Count);
                    for (int i = 0; i < n; i++) sample.Add(anim.Frame((int)((long)i * anim.Count / n)));
                    lockPal = RetroConvert.PreviewLock(sample, s);
                }
                finally { foreach (Bitmap b in sample) b.Dispose(); }
            }
            double lines;
            int screenH, bytes;
            RetroGeom geom;
            using (Bitmap src = anim != null ? anim.Frame(0) : Exporter.Decode(path, 0))   // upright, as the other side shows it
                first = ConvertSimFrame(src, s, lockPal, out lines, out screenH, out bytes, out geom);
            p.Img = first;
            p.W = first.Width; p.H = first.Height; p.Bytes = bytes;
            p.Note = first.Tag as string;
            p.Geom = geom;
            p.SimScale = lines * p.H / screenH;            // RECOIL may show each screen line more than once
            if (anim != null)
            {
                long per = Math.Max(1L, (long)first.Width * first.Height * 4);
                int total = (int)Math.Max(2, Math.Min(anim.Count, SimMaxBytes / per));
                p.Frames = new List<Bitmap> { first };
                p.Delays = new int[total];
                Array.Copy(anim.Delays, p.Delays, Math.Min(total, anim.Delays.Length));
                for (int i = 0; i < total; i++) if (p.Delays[i] <= 0) p.Delays[i] = 100;
                p.Anim = true;
                p.Sim = s.Copy();
                p.SimLock = lockPal;
            }
        }
        catch (Exception ex)
        {
            if (first != null) first.Dispose();
            p.Img = null; p.Frames = null; p.Anim = false;
            p.Err = ex is OutOfMemoryException ? "Too large to convert." : Util.Why(ex);
        }
        finally { if (anim != null) anim.Dispose(); }
        return p;
    }

    // one picture (or frame) converted for s's computer and decoded by RECOIL; the platform name is in Tag
    static Bitmap ConvertSimFrame(Bitmap src, RetroSim s, int[] lockPal, out double lines, out int screenH, out int bytes, out RetroGeom geom)
    {
        string ext;
        byte[] data = RetroConvert.Preview(src, s, lockPal, out ext, out lines, out screenH);
        string platform;
        Bitmap b = Retro.LoadMemory("preview" + ext, data, out platform, out geom);
        b.Tag = platform;
        bytes = data.Length;
        return b;
    }

    // Retro preview of an animation that is shown: its remaining frames, converted one by one on a worker thread and
    // added as they are ready (playback waits for a frame that isn't there yet). Stops when the picture is let go of
    // (another choice, or it is no longer near). A frame that can't be converted ends the animation there.
    void SimMore(Pic p)
    {
        if (p.SimBusy || p.Dropped || p.Frames == null || p.Sim == null || p.Frames.Count >= p.Delays.Length) return;
        p.SimBusy = true;
        Control host = FindForm();
        int from = p.Frames.Count, total = p.Delays.Length;
        string path = p.FullPath;
        RetroSim s = p.Sim;
        int[] lockPal = p.SimLock;
        ThreadPool.QueueUserWorkItem(delegate
        {
            int done = from;
            try
            {
                using (AnimSource anim = AnimSource.Open(path))
                {
                    for (int i = from; anim != null && i < Math.Min(total, anim.Count) && !p.Dropped; i++)
                    {
                        Bitmap b;
                        double lines; int sh, bytes; RetroGeom g;
                        using (Bitmap src = anim.Frame(i)) b = ConvertSimFrame(src, s, lockPal, out lines, out sh, out bytes, out g);
                        if (!Util.Post(host, delegate { SimFrameReady(p, b); })) { b.Dispose(); return; }
                        done = i + 1;
                    }
                }
            }
            catch (Exception) { }                         // ends the animation at the last good frame
            int end = done;
            Util.Post(host, delegate { SimFramesDone(p, end); });
        });
    }

    // on the UI thread: one more converted frame
    void SimFrameReady(Pic p, Bitmap b)
    {
        if (p.Dropped || p.Frames == null) { b.Dispose(); return; }
        p.Frames.Add(b);
        if (p != cur) return;
        if (waitingFrame && AnimActive)
        {
            waitingFrame = false;
            if (ShowFrame(frame + 1)) ScheduleFrame(); else waitingFrame = true;
        }
        else if (showInfo) Invalidate();                   // the info bar counts the converted frames
        if (p.Frames.Count == frameCount) Flash("All " + frameCount + " frames converted");
    }

    // on the UI thread: the worker stopped; fewer frames than planned means one could not be converted
    void SimFramesDone(Pic p, int end)
    {
        p.SimBusy = false;
        if (p.Dropped || p.Frames == null || p.Frames.Count >= p.Delays.Length) return;
        int n = Math.Max(1, p.Frames.Count);
        if (n < 2) { p.Anim = false; if (p == cur) { StopAnim(); Invalidate(); } return; }
        var d = new int[n];
        Array.Copy(p.Delays, d, n);
        p.Delays = d;
        if (p != cur) return;
        frameCount = n;
        frameDelays = (int[])d.Clone();
        if (frame >= n) ShowFrame(0);
        if (waitingFrame) { waitingFrame = false; if (ShowFrame(frame + 1)) ScheduleFrame(); }
        Flash("Only the first " + n + " frames could be converted");
    }

    // runs on worker threads
    static Pic Decode(string path)
    {
        var p = new Pic { FullPath = path, Stamp = FileStamp(path) };   // taken before reading, so a change during it shows
        Image img = null; MemoryStream ms = null;
        try
        {
            if ((File.GetAttributes(path) & Util.CloudMask) != 0)
                throw new IOException("This file is a cloud placeholder that isn't stored on this PC, so it was not opened. RetroGleek never goes online.");
            if (!Util.IsNative(path) && Retro.Claims(path))
            {
                long size; string platform;
                int frames; RetroGeom geom;
                p.Img = Retro.Load(path, out size, out platform, out frames, out geom);
                p.Bytes = size; p.W = p.Img.Width; p.H = p.Img.Height; p.Note = platform; p.Geom = geom;
                return p;
            }
            if (WinCodec.Claims(path))                         // WebP, AVIF, HEIC, JPEG XL, camera RAW ... (Windows' own decoders)
            {
                if (WebpAnim.IsWebp(path))                     // an animated WebP: every frame, played on the GIF clock
                {
                    List<Bitmap> frames = null;
                    int[] delays;
                    long size;
                    try { if (!WebpAnim.TryLoad(path, out frames, out delays, out size)) frames = null; else { p.Delays = delays; p.Bytes = size; } }
                    catch (Exception) { frames = null; }      // a damaged animation: the still decoder below says why
                    if (frames != null)
                    {
                        p.Frames = frames; p.Img = frames[0]; p.W = frames[0].Width; p.H = frames[0].Height; p.Anim = true;
                        return p;
                    }
                }
                CodecPic c;
                try { c = WinCodec.Load(path, 0, true); }
                catch (Exception cex) { throw new IOException(WinCodec.Why(path, cex)); }
                p.Img = c.Bmp; p.W = c.W; p.H = c.H; p.Rot = c.Rot; p.FileStars = c.Stars;
                p.Bytes = c.Bytes; p.Exif = c.Exif;                // Note stays null: Note marks a retro picture
                return p;
            }
            byte[] bytes = Util.ReadShared(path);             // one fast sequential read, no lock afterwards
            p.Bytes = bytes.Length;
            ms = new MemoryStream(bytes, false);
            img = Image.FromStream(ms, false, false);          // skip colour management + validation = faster
            p.W = img.Width; p.H = img.Height;
            p.Rot = Util.ReadRotation(img);
            p.Exif = ExifInfo.Read(img);                      // read now: the original is dropped after decoding
            if (FileRating.MayHold(path)) p.FileStars = FileRating.Read(path, img);

            bool anim = false;
            try { anim = ImageAnimator.CanAnimate(img); } catch (Exception) { }

            if (!anim && (long)p.W * p.H <= MaxPredecodePixels)
            {
                // Decode now, off the UI thread, into the format GDI+ draws fastest.
                var bmp = new Bitmap(p.W, p.H, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(img, new Rectangle(0, 0, p.W, p.H), 0, 0, p.W, p.H, GraphicsUnit.Pixel);
                }
                img.Dispose(); ms.Dispose();
                p.Img = bmp;
            }
            else { p.Img = img; p.Ms = ms; p.Anim = anim; }    // animated GIF or gigantic image: keep lazy
        }
        catch (Exception ex)
        {
            if (img != null) img.Dispose();
            if (ms != null) ms.Dispose();
            p.Img = null; p.Ms = null;
            p.Err = ex is OutOfMemoryException ? "Unsupported, damaged or too large." : ex.Message;
        }
        return p;
    }

    // ---------------------------------------------------------------- view maths

    bool HasImage { get { return cur != null && cur.Img != null; } }
    // the picture's size on screen at 100% (with True TV aspect, its own width is stretched or squeezed)
    double RW { get { return !HasImage ? 1 : ((rot & 1) == 0 ? cur.W * Ax : cur.H); } }
    double RH { get { return !HasImage ? 1 : ((rot & 1) == 0 ? cur.H : cur.W * Ax); } }

    void Refit()
    {
        if (!HasImage) return;
        if (fitMode == 0) { Clamp(); return; }
        double cw = ClientSize.Width, ch = ViewH;
        if (cw < 1 || ch < 1) return;
        double z = Math.Min(cw / RW, ch / RH);
        if (fitMode == 1 && z > 1) z = 1;
        if (ScanApplies && z >= 2) z = Math.Floor(z);   // scanlines: the largest whole-number size that fits, so every line is equally thick
        zoom = z; px = py = 0;
    }

    void Clamp()
    {
        if (!HasImage) return;
        double mx = Math.Max(0, (RW * zoom - ClientSize.Width) / 2);
        double my = Math.Max(0, (RH * zoom - ViewH) / 2);
        px = Math.Max(-mx, Math.Min(mx, px));
        py = Math.Max(-my, Math.Min(my, py));
    }

    // Low-quality interpolation while the user is interacting, high quality once things settle.
    void Interact()
    {
        fast = true;
        settle.Stop(); settle.Start();
        Invalidate();
    }

    void SetFit(int mode)
    {
        if (!HasImage) return;
        fitMode = mode; fast = false;
        Refit(); Invalidate();
        RaiseView();
    }

    void ZoomTo(double nz, double sx, double sy)
    {
        if (!HasImage) return;
        nz = Math.Max(0.01, Math.Min(64, nz));
        double cx = ClientSize.Width / 2.0, cy = ViewH / 2.0, k = nz / zoom;
        px = (sx - cx) - ((sx - cx) - px) * k;       // keeps the point under the cursor fixed
        py = (sy - cy) - ((sy - cy) - py) * k;
        zoom = nz; fitMode = 0;
        Clamp(); Interact();
        RaiseView();
    }

    void ZoomCenter(double nz) { ZoomTo(nz, ClientSize.Width / 2.0, ViewH / 2.0); }

    void Rotate(int d)
    {
        if (!HasImage) return;
        rot = (rot + d + 4) & 3;
        px = py = 0;
        Refit(); Invalidate();
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Bg);
        ClearBadges();                                     // drawn again below when they are shown

        if (StripShown) g.SetClip(new Rectangle(0, 0, ClientSize.Width, ViewH));
        if (HasImage)
        {
            bool sharp = pixMode == 3 || (pixMode == 0 && cur.Note != null);          // retro pictures are pixel art
            bool crisp = pixMode == 1 || sharp;
            bool exact = Math.Abs(zoom - 1) < 1e-6 || (pixMode != 2 && zoom >= 3) || (zoom > 1 && crisp);
            bool scan = ScanShown, mask = MaskShown, glow = GlowShown, curve = CurveShown;
            if (scan || mask || glow || curve)
            {
                // CRT effects: the picture is drawn into a buffer of its on-screen area, then shaded pixel by pixel
                Rectangle r = PicScreenRect();
                Bitmap fx = r.Width > 0 && r.Height > 0 ? EffectFrame(r, sharp, exact, scan, mask, glow, curve) : null;
                if (fx != null)
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.None;
                    g.DrawImage(fx, r, 0, 0, r.Width, r.Height, GraphicsUnit.Pixel);
                    g.CompositingMode = CompositingMode.SourceOver;
                }
                else { if (checker) DrawChecker(g); PicTransform(g); DrawPic(g, sharp, exact); g.ResetTransform(); }
            }
            else { if (checker) DrawChecker(g); PicTransform(g); DrawPic(g, sharp, exact); g.ResetTransform(); }
            g.PixelOffsetMode = PixelOffsetMode.Half;
            PicTransform(g);                                                         // for the pixel grid and outline

            int hx = -1, hy = -1;
            bool hov = mouseIn && !dragging && PixelAt(mousePt, out hx, out hy);
            if (zoom >= 8 && (showGrid || hov)) DrawPixelTools(g, hov, hx, hy);
            g.ResetTransform();
            if (!crop.IsEmpty) DrawCropFrame(g);
            if (loupe && mouseIn && !dragging) DrawLoupe(g, hov, hx, hy);
            if (showInfo) DrawInfo(g, hov, hx, hy);
            DrawBadges(g);
        }

        if (notice != null)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(notice, big, Brushes.Gray, new RectangleF(20, 0, ClientSize.Width - 40, ClientSize.Height), sf);
        }
        if (StripShown) { g.ResetClip(); DrawStrip(g); }
        if (showExif && HasImage) DrawExif(g);
        if (flash != null) DrawFlash(g);
    }

    int StripCell { get { return Util.S(76); } }

    // first file index shown in the strip, so the current picture sits in the middle
    int StripFirst(out int count)
    {
        int cell = StripCell;
        count = Math.Max(1, ClientSize.Width / cell);
        int first = (idx < 0 ? 0 : idx) - count / 2;
        return Math.Max(0, Math.Min(first, Math.Max(0, files.Count - count)));
    }

    long StripStamp(string path)
    {
        long st;
        if (stripStamps.TryGetValue(path, out st)) return st;
        try { var fi = new FileInfo(path); st = fi.Exists ? fi.LastWriteTimeUtc.Ticks ^ fi.Length : 0; } catch (Exception) { st = 0; }
        stripStamps[path] = st;
        return st;
    }

    void DrawStrip(Graphics g)
    {
        int top = ClientSize.Height - StripH, cell = StripCell, thumb = Util.S(64), count;
        using (var bb = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillRectangle(bb, 0, top, ClientSize.Width, StripH);
        using (var line = new Pen(Color.FromArgb(52, 52, 52))) g.DrawLine(line, 0, top, ClientSize.Width, top);
        int first = StripFirst(out count);
        int x0 = Math.Max(0, (ClientSize.Width - Math.Min(count, files.Count) * cell) / 2);
        var jobs = new List<ThumbJob>();
        InterpolationMode oldI = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.Bilinear;
        for (int k = 0; k < count && first + k < files.Count; k++)
        {
            int i = first + k;
            string p = files[i];
            var box = new Rectangle(x0 + k * cell + (cell - thumb) / 2, top + (StripH - thumb) / 2, thumb, thumb);
            long st = StripStamp(p);
            int bs;
            Bitmap b = stripCache.Get(p, st, out bs);
            if (b != null)
            {
                double f = Math.Min(1.0, Math.Min((double)thumb / b.Width, (double)thumb / b.Height));
                int dw = Math.Max(1, (int)(b.Width * f)), dh = Math.Max(1, (int)(b.Height * f));
                g.DrawImage(b, box.X + (thumb - dw) / 2, box.Y + (thumb - dh) / 2, dw, dh);
            }
            else
            {
                using (var ph = new SolidBrush(Color.FromArgb(44, 44, 44))) g.FillRectangle(ph, box.X + thumb / 6, box.Y + thumb / 6, thumb * 2 / 3, thumb * 2 / 3);
                if (st != 0) jobs.Add(new ThumbJob { Path = p, Size = thumb, Stamp = st });
            }
            if (i == idx)
                using (var sel = new Pen(Color.FromArgb(60, 130, 230), Math.Max(2f, Util.K * 2f))) g.DrawRectangle(sel, box.X - 3, box.Y - 3, thumb + 5, thumb + 5);
        }
        g.InterpolationMode = oldI;
        if (jobs.Count > 0)
        {
            if (stripSvc == null) stripSvc = new ThumbService(1, StripThumb);
            stripHost = FindForm();
            stripSvc.Replace(jobs);
        }
    }

    void StripThumb(ThumbJob j, ThumbResult r)        // worker thread
    {
        Control h = stripHost;
        if (!Util.Post(h, delegate
        {
            if (r.Bmp == null) return;
            stripCache.Put(j.Path, j.Stamp, j.Size, r);
            if (StripShown) Invalidate(new Rectangle(0, ClientSize.Height - StripH, ClientSize.Width, StripH));
        }) && r.Bmp != null) r.Bmp.Dispose();
    }

    // the file index of the strip thumbnail at a window point, or -1
    int StripHit(Point p)
    {
        if (!StripShown || p.Y < ClientSize.Height - StripH) return -1;
        int count, first = StripFirst(out count), cell = StripCell;
        int x0 = Math.Max(0, (ClientSize.Width - Math.Min(count, files.Count) * cell) / 2);
        if (p.X < x0) return -1;
        int i = first + (p.X - x0) / cell;
        return i < files.Count && (p.X - x0) / cell < count ? i : -1;
    }

    // drawn while the picture's transform is active, so everything is in picture-pixel coordinates
    void DrawPixelTools(Graphics g, bool hov, int hx, int hy)
    {
        float inv = (float)(1.0 / zoom);                                               // one screen pixel, in picture units
        if (showGrid)
        {
            using (Matrix m = g.Transform)
            {
                m.Invert();
                PointF[] c = { new PointF(0, 0), new PointF(ClientSize.Width, 0), new PointF(0, ClientSize.Height), new PointF(ClientSize.Width, ClientSize.Height) };
                m.TransformPoints(c);
                float minX = Math.Min(Math.Min(c[0].X, c[1].X), Math.Min(c[2].X, c[3].X));
                float maxX = Math.Max(Math.Max(c[0].X, c[1].X), Math.Max(c[2].X, c[3].X));
                float minY = Math.Min(Math.Min(c[0].Y, c[1].Y), Math.Min(c[2].Y, c[3].Y));
                float maxY = Math.Max(Math.Max(c[0].Y, c[1].Y), Math.Max(c[2].Y, c[3].Y));
                int x0 = Math.Max(0, (int)Math.Floor(minX)), x1 = Math.Min(cur.W, (int)Math.Ceiling(maxX));
                int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(cur.H, (int)Math.Ceiling(maxY));
                using (var pen = new Pen(Color.FromArgb(90, 128, 128, 128), inv))
                {
                    for (int x = x0; x <= x1; x++) g.DrawLine(pen, x, y0, x, y1);
                    for (int y = y0; y <= y1; y++) g.DrawLine(pen, x0, y, x1, y);
                }
            }
        }
        if (hov)
            using (var pen = new Pen(Color.FromArgb(230, 255, 220, 0), 2f * inv)) g.DrawRectangle(pen, hx, hy, 1, 1);
    }

    // ---- Sharp enlargement (sharp-bilinear, as used by RetroArch): at a size that is not a whole number, plain
    // nearest-neighbour makes some pixel columns and rows one screen pixel wider than others. Instead the picture is
    // first enlarged by whole numbers with hard edges (the next whole number above the zoom, per direction), and that
    // copy is then shrunk smoothly to the exact size. Every pixel ends up the same size, and only the one screen pixel
    // where two picture pixels meet is blended. At whole-number sizes nothing changes. Display only.
    const long MaxSharpPixels = 24000000L;                 // about 96 MB for the enlarged copy; above that: crisp
    Bitmap sharpBmp;
    Image sharpOf;
    int sharpFx, sharpFy, sharpFrame = -1;

    void DropSharp()
    {
        if (sharpBmp != null) sharpBmp.Dispose();
        sharpBmp = null; sharpOf = null; sharpFrame = -1;
    }

    // sx, sy = the picture's scale across and down; null when plain drawing is already exact (or the copy is too big)
    Bitmap SharpSource(Image src, int sw, int sh, double sx, double sy)
    {
        const double eps = 1e-4;
        if (sx <= 1 + eps && sy <= 1 + eps) return null;                       // not enlarged: nothing uneven to fix
        bool wholeX = Math.Abs(sx - Math.Round(sx)) < eps, wholeY = Math.Abs(sy - Math.Round(sy)) < eps;
        if (wholeX && wholeY) return null;                                     // whole numbers: nearest is exact
        int fx = wholeX ? (int)Math.Round(sx) : (int)Math.Ceiling(sx);
        int fy = wholeY ? (int)Math.Round(sy) : (int)Math.Ceiling(sy);
        fx = Math.Max(1, fx); fy = Math.Max(1, fy);
        if ((long)sw * fx * sh * fy > MaxSharpPixels) return null;
        int f = AnimActive ? frame : 0;
        if (sharpBmp != null && sharpOf == src && sharpFx == fx && sharpFy == fy && sharpFrame == f) return sharpBmp;
        DropSharp();
        try
        {
            var b = new Bitmap(sw * fx, sh * fy, PixelFormat.Format32bppPArgb);
            using (Graphics tg = Graphics.FromImage(b))
            {
                tg.CompositingMode = CompositingMode.SourceCopy;
                tg.InterpolationMode = InterpolationMode.NearestNeighbor;
                tg.PixelOffsetMode = PixelOffsetMode.Half;
                tg.DrawImage(src, new Rectangle(0, 0, b.Width, b.Height), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
            }
            sharpBmp = b; sharpOf = src; sharpFx = fx; sharpFy = fy; sharpFrame = f;
        }
        catch (Exception) { DropSharp(); }                                     // out of memory: fall back to crisp
        return sharpBmp;
    }

    // ---- Video signal (C): S-Video and composite, worked out once per picture (and frame) and kept.
    // The picture is split into brightness (Y) and colour (U, V), as a PAL / NTSC signal carries them, and each is
    // blurred along the line by the signal's limited bandwidth: colour much more than brightness, and both more for
    // composite than for S-Video. This is what made 1-pixel dithering (checkerboards, stripes) blend into a solid
    // in-between colour on a TV. Blur widths are in pixels of a 320-pixel-wide line, so hires pictures get the same
    // blur on screen. Four samples per pixel across keep the result smooth when enlarged; lines stay separate.
    // Not modelled: cross-colour rainbows and dot crawl, which depend on each machine's exact timing.
    Bitmap compBmp;
    Image compOf;
    int compMode, compFrame = -1;
    const long MaxCompositeSamples = 16000000L;

    void DropComposite()
    {
        if (compBmp != null) compBmp.Dispose();
        compBmp = null; compOf = null; compFrame = -1;
    }

    Bitmap CompositeSource()
    {
        int f = AnimActive ? frame : 0;
        if (compBmp != null && compOf == cur.Img && compMode == signalMode && compFrame == f) return compBmp;
        DropComposite();
        int w = cur.W, h = cur.H;
        int k = (long)w * 4 * h <= MaxCompositeSamples ? 4 : (long)w * 2 * h <= MaxCompositeSamples ? 2 : 1;
        double unit = Math.Max(0.75, w / 320.0) * k;           // samples per pixel of a 320-pixel line
        double sy = (signalMode == 2 ? 0.65 : 0.35) * unit;    // brightness blur (Gaussian sigma, in samples)
        double sc = (signalMode == 2 ? 2.2 : 1.2) * unit;      // colour blur
        try
        {
            int[] src = new int[w * h];
            using (var tmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics tg = Graphics.FromImage(tmp))
                {
                    tg.CompositingMode = CompositingMode.SourceCopy;
                    tg.InterpolationMode = InterpolationMode.NearestNeighbor;
                    tg.PixelOffsetMode = PixelOffsetMode.Half;
                    tg.DrawImage(cur.Img, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
                }
                BitmapData bd = tmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try { for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, src, y * w, w); }
                finally { tmp.UnlockBits(bd); }
            }
            int n = w * k;
            int[] dst = new int[n * h];
            float[] Y = new float[n], U = new float[n], V = new float[n], t = new float[n];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int c = src[y * w + x];
                    float r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                    float yy = 0.299f * r + 0.587f * g + 0.114f * b;
                    float u = 0.492f * (b - yy), v = 0.877f * (r - yy);
                    for (int j = 0; j < k; j++) { Y[x * k + j] = yy; U[x * k + j] = u; V[x * k + j] = v; }
                }
                Blur(Y, t, sy); Blur(U, t, sc); Blur(V, t, sc);
                for (int i = 0; i < n; i++)
                {
                    float yy = Y[i], u = U[i], v = V[i];
                    int r = Clamp255(yy + 1.140f * v), g = Clamp255(yy - 0.395f * u - 0.581f * v), b = Clamp255(yy + 2.032f * u);
                    dst[y * n + i] = (int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b);
                }
            }
            var bmp = new Bitmap(n, h, PixelFormat.Format32bppArgb);
            BitmapData od = bmp.LockBits(new Rectangle(0, 0, n, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(dst, y * n, od.Scan0 + y * od.Stride, n); }
            finally { bmp.UnlockBits(od); }
            compBmp = bmp; compOf = cur.Img; compMode = signalMode; compFrame = f;
        }
        catch (Exception) { DropComposite(); }                     // out of memory: shown as RGB
        return compBmp;
    }

    static int Clamp255(float v) { return v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5f); }

    // Gaussian blur of one line, approximated by three box blurs (edges repeat the end samples)
    static void Blur(float[] a, float[] t, double sigma)
    {
        int rad = (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2);    // box of width 2r+1, three passes
        if (rad < 1) return;
        for (int pass = 0; pass < 3; pass++)
        {
            float[] from = (pass & 1) == 0 ? a : t, to = (pass & 1) == 0 ? t : a;
            int n = from.Length;
            float sum = 0, inv = 1f / (2 * rad + 1);
            for (int i = -rad; i <= rad; i++) sum += from[Math.Max(0, Math.Min(n - 1, i))];
            for (int i = 0; i < n; i++)
            {
                to[i] = sum * inv;
                sum += from[Math.Min(n - 1, i + rad + 1)] - from[Math.Max(0, i - rad)];
            }
        }
        Array.Copy(t, a, a.Length);                                 // three passes end in t
    }

    // ---- Glow (W). Worked out once per picture (and frame): the bright parts of the picture (brightness squared, so
    // dark colours hardly glow) are blurred by about one to two original screen lines. When drawing, that halo is
    // enlarged smoothly to the picture's size on screen and added on top of the picture, after scanlines and mask.
    Bitmap glowBmp, glowLayerBmp;
    Image glowOf;
    int glowFrame = -1;
    int[] glowPix;

    void DropGlow()
    {
        if (glowBmp != null) glowBmp.Dispose();
        if (glowLayerBmp != null) glowLayerBmp.Dispose();
        glowBmp = null; glowLayerBmp = null; glowOf = null; glowFrame = -1; glowPix = null;
    }

    Bitmap GlowSource()
    {
        int f = AnimActive ? frame : 0;
        if (glowBmp != null && glowOf == cur.Img && glowFrame == f) return glowBmp;
        if (glowBmp != null) glowBmp.Dispose();
        glowBmp = null;
        int w = cur.W, h = cur.H;
        if ((long)w * h > 16000000L) return null;
        int[] pix = new int[w * h];
        using (var tmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics tg = Graphics.FromImage(tmp))
            {
                tg.CompositingMode = CompositingMode.SourceCopy;
                tg.InterpolationMode = InterpolationMode.NearestNeighbor;
                tg.PixelOffsetMode = PixelOffsetMode.Half;
                tg.DrawImage(cur.Img, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
            }
            BitmapData bd = tmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, pix, y * w, w); }
            finally { tmp.UnlockBits(bd); }
        }
        var ch = new float[3][];
        for (int c = 0; c < 3; c++) ch[c] = new float[w * h];
        for (int i = 0; i < pix.Length; i++)
        {
            int v = pix[i];
            float r = (v >> 16) & 255, g = (v >> 8) & 255, b = v & 255;
            ch[0][i] = r * r / 255f; ch[1][i] = g * g / 255f; ch[2][i] = b * b / 255f;
        }
        double sigma = Math.Max(1.0, 1.5 * LineHeight);          // about one and a half original screen lines
        var rowBuf = new float[w]; var rowTmp = new float[w];
        var colBuf = new float[h]; var colTmp = new float[h];
        for (int c = 0; c < 3; c++)
        {
            float[] a = ch[c];
            for (int y = 0; y < h; y++) { Array.Copy(a, y * w, rowBuf, 0, w); Blur(rowBuf, rowTmp, sigma); Array.Copy(rowBuf, 0, a, y * w, w); }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) colBuf[y] = a[y * w + x];
                Blur(colBuf, colTmp, sigma);
                for (int y = 0; y < h; y++) a[y * w + x] = colBuf[y];
            }
        }
        for (int i = 0; i < pix.Length; i++)
            pix[i] = (int)(0xFF000000u | (uint)(Clamp255(ch[0][i]) << 16) | (uint)(Clamp255(ch[1][i]) << 8) | (uint)Clamp255(ch[2][i]));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData od = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(pix, y * w, od.Scan0 + y * od.Stride, w); }
        finally { bmp.UnlockBits(od); }
        glowBmp = bmp; glowOf = cur.Img; glowFrame = f;
        return glowBmp;
    }

    // the halo for the window area r, as packed colours (null if it could not be made)
    int[] GlowLayer(Rectangle r)
    {
        Bitmap src = GlowSource();
        if (src == null) return null;
        if (glowLayerBmp == null || glowLayerBmp.Width != r.Width || glowLayerBmp.Height != r.Height)
        {
            if (glowLayerBmp != null) glowLayerBmp.Dispose();
            glowLayerBmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        }
        using (Graphics gg = Graphics.FromImage(glowLayerBmp))
        {
            gg.Clear(Color.Black);
            gg.TranslateTransform(-r.X, -r.Y);
            PicTransform(gg);
            gg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            gg.PixelOffsetMode = PixelOffsetMode.Half;
            gg.DrawImage(src, new Rectangle(0, 0, cur.W, cur.H), 0, 0, cur.W, cur.H, GraphicsUnit.Pixel, ia);
        }
        int n = r.Width * r.Height;
        if (glowPix == null || glowPix.Length != n) glowPix = new int[n];
        BitmapData bd = glowLayerBmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < r.Height; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, glowPix, y * r.Width, r.Width); }
        finally { glowLayerBmp.UnlockBits(bd); }
        return glowPix;
    }

    // the picture's transform: pan, rotate, zoom, True TV aspect, mirror (picture pixels -> window pixels)
    void PicTransform(Graphics g)
    {
        g.TranslateTransform((float)(ViewW / 2.0 + px), (float)(ViewH / 2.0 + py));
        g.RotateTransform(rot * 90f);
        g.ScaleTransform((float)zoom, (float)zoom);
        double ax = Ax;
        if (ax != 1.0) g.ScaleTransform((float)ax, 1f);                                // True TV aspect: the picture's own width
        if (flipX || flipY) g.ScaleTransform(flipX ? -1f : 1f, flipY ? -1f : 1f);     // mirror around the picture's centre
        g.TranslateTransform(-cur.W / 2f, -cur.H / 2f);
    }

    // draws the picture under PicTransform, crisp / sharp / smooth as chosen
    void DrawPic(Graphics g, bool sharp, bool exact)
    {
        g.InterpolationMode = exact ? InterpolationMode.NearestNeighbor
                            : fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        Image src = cur.Img;
        int sw = cur.W, sh = cur.H;
        Bitmap comp = SignalApplies ? CompositeSource() : null;
        if (comp != null)                                  // several samples per pixel across: always drawn the sharp way
        {
            src = comp; sw = comp.Width; sh = comp.Height; sharp = true;
        }
        Bitmap sb = sharp ? SharpSource(src, sw, sh, zoom * Ax * cur.W / sw, zoom * cur.H / sh) : null;
        if (sb != null)
        {
            g.InterpolationMode = fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
            g.DrawImage(sb, new Rectangle(0, 0, cur.W, cur.H), 0, 0, sb.Width, sb.Height, GraphicsUnit.Pixel, ia);
        }
        else
        {
            // whole-number enlargement: nearest keeps the lines sharp; 100% or smaller: average the samples across
            if (comp != null) g.InterpolationMode = zoom > 1 + 1e-6 ? InterpolationMode.NearestNeighbor
                                                 : fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, cur.W, cur.H), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
        }
    }

    // the window pixels the picture covers (only the visible part)
    Rectangle PicScreenRect()
    {
        double w = RW * zoom, h = RH * zoom;
        double x = ViewW / 2.0 + px - w / 2, y = ViewH / 2.0 + py - h / 2;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), x1 = (int)Math.Ceiling(x + w), y1 = (int)Math.Ceiling(y + h);
        return Rectangle.Intersect(Rectangle.FromLTRB(x0, y0, x1, y1), new Rectangle(0, 0, ViewW, ViewH));
    }

    // ---- CRT effects (K scanlines, M phosphor mask): display only, retro pictures, upright or upside down.
    // The visible part of the picture is drawn into a buffer, then each window pixel is shaded:
    //   scanlines: each original screen line is a soft beam, brightest in its middle. Bright colours make a wider beam
    //     that fills more of the dark gap, as on a real CRT; the whole picture is brightened to make up for the gaps.
    //   mask: a fixed pattern of red, green and blue stripes or dots in window pixels, so it stays put while panning.
    // The result is kept and reused until anything that changes it does (zoom, position, picture, frame, options).
    Bitmap fxBmp;
    Image fxOf;
    string fxKey;
    int[] fxPix, fxOut;
    int[] scanTab;                                         // 64 positions across a line x 16 brightness levels, x256
    int scanTabMode = -1;

    void DropFx()
    {
        if (fxBmp != null) fxBmp.Dispose();
        fxBmp = null; fxOf = null; fxKey = null; fxPix = null; fxOut = null;
    }

    Bitmap EffectFrame(Rectangle r, bool sharp, bool exact, bool scan, bool mask, bool glow, bool curve)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string key = zoom.ToString("R", inv) + "|" + px.ToString("R", inv) + "|" + py.ToString("R", inv) + "|" + ViewW + "x" + ViewH
                   + "|" + r.X + "," + r.Y + "," + r.Width + "," + r.Height + "|" + rot + (flipX ? "h" : "") + (flipY ? "v" : "") + "|" + Ax.ToString("R", inv)
                   + "|" + (scan ? scanMode : 0) + "|" + (mask ? maskMode : 0) + "|" + (sharp ? "s" : "") + (exact ? "e" : "") + (fast ? "f" : "")
                   + (checker ? "c" : "") + "|" + (SignalApplies ? signalMode : 0) + "|" + (glow ? glowMode : 0) + "|" + (curve ? curveMode : 0) + "|" + (AnimActive ? frame : 0);
        if (fxBmp != null && fxOf == cur.Img && fxKey == key) return fxBmp;
        try
        {
            if (fxBmp == null || fxBmp.Width != r.Width || fxBmp.Height != r.Height)
            {
                DropFx();
                fxBmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            }
            using (Graphics bg = Graphics.FromImage(fxBmp))
            {
                bg.Clear(Bg);
                bg.TranslateTransform(-r.X, -r.Y);
                if (checker) DrawChecker(bg);
                PicTransform(bg);
                DrawPic(bg, sharp, exact);
            }
            int[] halo = glow ? GlowLayer(r) : null;
            Shade(r, scan, mask, halo);
            if (curve) Warp(r);
            fxOf = cur.Img; fxKey = key;
            return fxBmp;
        }
        catch (Exception) { DropFx(); return null; }                           // out of memory: draw without effects
    }

    // Screen curvature: every window pixel of the visible picture area r looks up the point of the flat picture it
    // shows on a bulging screen (barrel distortion, as in crt-geom style shaders), smoothly between neighbours.
    // The visible area is the screen: zoomed in, the window is the glass. Corners are rounded and darkened.
    // Runs on fxPix (already shaded), after scanlines, mask and glow.
    void Warp(Rectangle r)
    {
        int w = r.Width, h = r.Height, n = w * h;
        if (w < 8 || h < 8) return;
        if (fxOut == null || fxOut.Length != n) fxOut = new int[n];
        double k = curveMode == 2 ? 0.07 : 0.03;                          // how much the glass bulges
        double vigPow = curveMode == 2 ? 0.28 : 0.16;                     // how dark the corners get
        double rad = Math.Max(4, Math.Min(w, h) * (curveMode == 2 ? 0.05 : 0.03));   // corner radius in pixels
        int bgc = Bg.ToArgb();
        int[] src = fxPix;
        for (int y = 0; y < h; y++)
        {
            double v = (y + 0.5) / h * 2 - 1;
            for (int x = 0; x < w; x++)
            {
                double u = (x + 0.5) / w * 2 - 1;
                double su = u * (1 + k * v * v), sv = v * (1 + k * u * u);     // where this pixel looks on the flat picture
                int o = y * w + x;
                if (su <= -1 || su >= 1 || sv <= -1 || sv >= 1) { fxOut[o] = bgc; continue; }
                double fx = (su + 1) / 2 * w - 0.5, fy = (sv + 1) / 2 * h - 0.5;
                int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                double ax = fx - x0, ay = fy - y0;
                int xa = Math.Max(0, Math.Min(w - 1, x0)), xb = Math.Max(0, Math.Min(w - 1, x0 + 1));
                int ya = Math.Max(0, Math.Min(h - 1, y0)), yb = Math.Max(0, Math.Min(h - 1, y0 + 1));
                int c00 = src[ya * w + xa], c10 = src[ya * w + xb], c01 = src[yb * w + xa], c11 = src[yb * w + xb];
                double w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
                double cr = ((c00 >> 16) & 255) * w00 + ((c10 >> 16) & 255) * w10 + ((c01 >> 16) & 255) * w01 + ((c11 >> 16) & 255) * w11;
                double cg = ((c00 >> 8) & 255) * w00 + ((c10 >> 8) & 255) * w10 + ((c01 >> 8) & 255) * w01 + ((c11 >> 8) & 255) * w11;
                double cb = (c00 & 255) * w00 + (c10 & 255) * w10 + (c01 & 255) * w01 + (c11 & 255) * w11;

                // vignette: darker towards the edges of the glass
                double tx = (su + 1) / 2, ty = (sv + 1) / 2;
                double vig = Math.Pow(Math.Max(0, 16 * tx * ty * (1 - tx) * (1 - ty)), vigPow);
                // rounded corners, with a soft one-pixel edge
                double px0 = tx * w, py0 = ty * h;
                double dx = Math.Max(0, Math.Abs(px0 - w / 2.0) - (w / 2.0 - rad)), dy = Math.Max(0, Math.Abs(py0 - h / 2.0) - (h / 2.0 - rad));
                double edge = Math.Max(0, Math.Min(1, rad - Math.Sqrt(dx * dx + dy * dy) + 0.5));
                double f = vig * edge, bgPart = 1 - edge;
                int rr = Clamp255((float)(cr * f + Bg.R * bgPart)), gg = Clamp255((float)(cg * f + Bg.G * bgPart)), bb = Clamp255((float)(cb * f + Bg.B * bgPart));
                fxOut[o] = (int)(0xFF000000u | (uint)(rr << 16) | (uint)(gg << 8) | (uint)bb);
            }
        }
        BitmapData bd = fxBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(fxOut, y * w, bd.Scan0 + y * bd.Stride, w); }
        finally { fxBmp.UnlockBits(bd); }
    }

    void BuildScanTable()
    {
        if (scanTabMode == scanMode && scanTab != null) return;
        double s = scanMode == 1 ? 0.35 : scanMode == 2 ? 0.55 : 0.75;         // how dark the gaps get
        var f = new double[64 * 16];
        double mid = 0;
        for (int p = 0; p < 64; p++)
        {
            double d = Math.Abs((p + 0.5) / 64 - 0.5) * 2;                     // 0 = middle of the line, 1 = its edge
            for (int l = 0; l < 16; l++)
            {
                double lum = (l + 0.5) / 16, h = 0.45 + 0.4 * lum;             // brighter = wider beam
                double w = Math.Exp(-2.2 * (d / h) * (d / h));
                f[p * 16 + l] = 1 - s * (1 - w);
                if (l == 8) mid += f[p * 16 + l] / 64;
            }
        }
        double gain = Math.Min(1.5, 1 / mid);                                  // keep mid-tones about as bright as before
        scanTab = new int[64 * 16];
        for (int i = 0; i < f.Length; i++) scanTab[i] = (int)Math.Round(f[i] * gain * 256);
        scanTabMode = scanMode;
    }

    void Shade(Rectangle r, bool scan, bool mask, int[] halo)
    {
        int w = r.Width, h = r.Height, n = w * h;
        if (fxPix == null || fxPix.Length != n) fxPix = new int[n];
        BitmapData bd = fxBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, fxPix, y * w, w);
            if (scan) BuildScanTable();
            int[] tab = scanTab;
            double lh = LineHeight, cy = ViewH / 2.0 + py;
            bool upside = rot == 2;
            int unit = Util.K >= 1.75f ? 2 : 1;                                 // phosphors twice as wide on very dense screens
            const int Lit = 333, Dim = 166;                                     // mask: 1.3 and 0.65 (x256)
            int gs = glowMode == 2 ? 170 : 100;                                 // glow strength (x256)
            for (int y = 0; y < h; y++)
            {
                int row = 0;
                if (scan)
                {
                    double v = (r.Y + y + 0.5 - cy) / zoom;                     // picture units from the picture's centre
                    if (upside) v = -v;
                    if (flipY) v = -v;
                    double t = (v + cur.H / 2.0) / lh;
                    int p = (int)((t - Math.Floor(t)) * 64);
                    row = Math.Max(0, Math.Min(63, p)) * 16;
                }
                int sy = (r.Y + y) / unit;
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    int c = fxPix[i];
                    int cr = (c >> 16) & 255, cg = (c >> 8) & 255, cb = c & 255;
                    if (scan)
                    {
                        int k = tab[row + (((cr * 77 + cg * 150 + cb * 29) >> 8) >> 4)];
                        cr = cr * k >> 8; cg = cg * k >> 8; cb = cb * k >> 8;
                    }
                    if (mask)
                    {
                        int sx = (r.X + x) / unit, mr, mg, mb;
                        if (maskMode == 3)                                      // dot mask: magenta / green checker
                        {
                            if (((sx + sy) & 1) == 0) { mr = Lit; mg = Dim; mb = Lit; } else { mr = Dim; mg = Lit; mb = Dim; }
                        }
                        else
                        {
                            int ph = sx % 3;                                    // aperture grille: R, G, B stripes
                            mr = ph == 0 ? Lit : Dim; mg = ph == 1 ? Lit : Dim; mb = ph == 2 ? Lit : Dim;
                            if (maskMode == 2 && (sy + ((sx / 3) & 1) * 2) % 4 == 0) { mr = mr * 166 >> 8; mg = mg * 166 >> 8; mb = mb * 166 >> 8; }   // slot mask: staggered breaks
                        }
                        cr = cr * mr >> 8; cg = cg * mg >> 8; cb = cb * mb >> 8;
                    }
                    if (halo != null)                                           // glow: light added on top, like a CRT's glass
                    {
                        int hc = halo[i];
                        cr += ((hc >> 16) & 255) * gs >> 8; cg += ((hc >> 8) & 255) * gs >> 8; cb += (hc & 255) * gs >> 8;
                    }
                    if (cr > 255) cr = 255; if (cg > 255) cg = 255; if (cb > 255) cb = 255;
                    fxPix[i] = (int)(0xFF000000u | (uint)(cr << 16) | (uint)(cg << 8) | (uint)cb);
                }
            }
            for (int y = 0; y < h; y++) Marshal.Copy(fxPix, y * w, bd.Scan0 + y * bd.Stride, w);
        }
        finally { fxBmp.UnlockBits(bd); }
    }

    // stars, label and tag for the info bar, e.g. "    (3 stars)  Red  (tick) tagged" ("" without marks)
    string MarkText()
    {
        Mark m = CurMark;
        if ((m == null || m.Stars == 0) && cur != null && cur.FileStars > 0)          // only the file's own rating
            return "    " + Marks.StarText(cur.FileStars) + " (in the file)" + (m != null && !m.Empty ? "  " + MarkRest(m) : "");
        if (m == null || m.Empty) return "";
        return "    " + (m.Stars > 0 ? Marks.StarText(m.Stars) : "") + (m.Label > 0 ? (m.Stars > 0 ? "  " : "") + Marks.LabelNames[m.Label] : "")
             + (m.Tag ? (m.Stars > 0 || m.Label > 0 ? "  " : "") + "\u2714 tagged" : "");
    }

    // label and tag only (when the stars come from the file)
    static string MarkRest(Mark m)
    {
        return (m.Label > 0 ? Marks.LabelNames[m.Label] : "") + (m.Tag ? (m.Label > 0 ? "  " : "") + "\u2714 tagged" : "");
    }

    void DrawInfo(Graphics g, bool hov, int hx, int hy)
    {
        string s = Path.GetFileName(cur.FullPath) + "    " + cur.W + " x " + cur.H + "    " + Util.FmtSize(cur.Bytes)
                 + MarkText()
                 + (cur.Note != null ? "    " + cur.Note : "") + "    " + (int)Math.Round(zoom * 100) + "%" + (fitLock ? " (Fit)" : "")
                 + (flipX ? "    Flipped H" : "") + (flipY ? "    Flipped V" : "") + "    " + (idx + 1) + " / " + files.Count
                 + (slideshow ? "    Slideshow " + SlideSecs[slideIdx] + " s" : "")
                 + (AnimActive ? "    Frame " + (frame + 1) + " / " + frameCount + (animPaused ? " (paused)" : "")
                    + (cur.Frames != null && cur.Frames.Count < frameCount ? " (" + cur.Frames.Count + " converted)" : "") : "")
                 + (cur.Geom != null && cur.Geom.OrigW > 0 && (cur.Geom.OrigW != cur.W || cur.Geom.OrigH != cur.H) ? "    original " + cur.Geom.OrigW + " x " + cur.Geom.OrigH : "")
                 + (Ax != 1.0 ? "    TV aspect" : "")
                 + (ScanApplies ? (LineHeight * zoom >= 3 ? "    Scanlines " + ScanNames[scanMode] : "    Scanlines (from 300%)")
                    : ScanWanted ? "    Scanlines (off while turned sideways)" : "")
                 + (SignalApplies ? "    " + (signalMode == 1 ? "S-Video" : "Composite") : "")
                 + (GlowShown ? "    Glow " + GlowNames[glowMode] : "")
                 + (CurveShown ? "    Curved" : CurveWanted ? "    Curve (off from 800%)" : "")
                 + (fxOff ? "    Screen effects off (O)" : autoFx && autoChoice != null && cur.Note != null ? "    Auto effects" : "")
                 + (MaskApplies ? (MaskShown ? "    Mask " + MaskNames[maskMode] : "    Mask (from 200%)")
                    : MaskWanted ? "    Mask (off while turned sideways)" : "");
        float k = g.DpiY / 96f;
        int h = (int)(font.GetHeight(g) + 10 * k);
        var bar = new Rectangle(0, ViewH - h, ClientSize.Width, h);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(b, bar);

        float rightEdge = bar.Width - 10 * k;
        if (hov)                                                                       // pixel under the pointer
        {
            Color c = Color.Black;
            bool got = false;
            try { Bitmap bm = cur.Img as Bitmap; if (bm != null) { c = bm.GetPixel(hx, hy); got = true; } } catch (Exception) { }
            string rt = "x " + hx + "  y " + hy;
            if (got)
                rt += "   #" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2")
                    + "   (" + c.R + ", " + c.G + ", " + c.B + (c.A != 255 ? ", a " + c.A : "") + ")";
            SizeF rs = g.MeasureString(rt, font);
            float tx = rightEdge - rs.Width, sw = 14 * k, sy = bar.Y + (bar.Height - sw) / 2;
            g.DrawString(rt, font, Brushes.Gainsboro, tx, bar.Y + (bar.Height - rs.Height) / 2);
            if (got)
            {
                float sx = tx - sw - 6 * k;
                using (var sb = new SolidBrush(Color.FromArgb(255, c))) g.FillRectangle(sb, sx, sy, sw, sw);
                g.DrawRectangle(Pens.Gray, sx, sy, sw, sw);
                rightEdge = sx - 12 * k;
            }
            else rightEdge = tx - 12 * k;
        }
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(s, font, Brushes.Gainsboro, new RectangleF(bar.X + 10 * k, bar.Y, Math.Max(0, rightEdge - 10 * k), bar.Height), sf);
    }

    // E: EXIF details in a box at the top right
    void DrawExif(Graphics g)
    {
        var lines = new List<string>();
        if (cur.Exif != null) lines.AddRange(cur.Exif);
        else lines.Add(cur.Note != null ? "Retro picture (" + cur.Note + "): no EXIF details" : "No EXIF details in this file");
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float k = g.DpiY / 96f, lh = font.GetHeight(g), w = 0;
        foreach (string l in lines) w = Math.Max(w, g.MeasureString(l, font).Width);
        w = Math.Min(w, Math.Max(50, ClientSize.Width - 40 * k));
        var r = new RectangleF(ClientSize.Width - w - 26 * k, 10 * k, w + 16 * k, lines.Count * lh + 12 * k);
        using (var b = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(b, r);
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            for (int i = 0; i < lines.Count; i++)
                g.DrawString(lines[i], font, Brushes.Gainsboro, new RectangleF(r.X + 8 * k, r.Y + 6 * k + i * lh, w, lh), sf);
    }

    void DrawFlash(Graphics g)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float k = g.DpiY / 96f;
        SizeF sz = g.MeasureString(flash, font);
        var r = new RectangleF(10 * k, 10 * k, sz.Width + 16 * k, sz.Height + 10 * k);
        using (var b = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(b, r);
        g.DrawString(flash, font, Brushes.White, r.X + 8 * k, r.Y + 5 * k);
    }

    // ---------------------------------------------------------------- input

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { Refit(); Invalidate(); ScheduleFrame(); } else { StopSlideshow(); animTimer.Stop(); }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Refit();
        Interact();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            ZoomTo(zoom * Math.Pow(1.2, e.Delta / 120.0), e.X, e.Y);
            return;
        }
        wheelAcc += e.Delta;                               // accumulate so touchpads don't skip images
        while (Math.Abs(wheelAcc) >= 120)
        {
            Step(wheelAcc > 0 ? -1 : 1);
            wheelAcc -= wheelAcc > 0 ? 120 : -120;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (StripShown && e.Y >= ClientSize.Height - StripH)       // the filmstrip: show the clicked picture
        {
            int hit = StripHit(e.Location);
            if (e.Button == MouseButtons.Left && hit >= 0 && hit != idx) GoTo(hit);
            return;
        }
        int badge = e.Button == MouseButtons.Left ? BadgeAt(e.Location) : -1;
        if (badge >= 0)                                    // a badge: the next signal / scanlines
        {
            CycleBadge(badge);
            var eh = EffectsClicked;
            if (eh != null) eh(badge);
            return;
        }
        if (e.Button == MouseButtons.Left && HasImage && !crop.IsEmpty)     // retro preview: move the crop frame
        {
            PointF at = UprightAt(e.Location);
            cropDrag = true;
            cropGrab = crop.Contains(at) ? new PointF(at.X - crop.X, at.Y - crop.Y) : new PointF(crop.Width / 2, crop.Height / 2);
            Cursor = Cursors.SizeAll;
            MoveCrop(at);
            return;
        }
        if (e.Button == MouseButtons.Left && HasImage) { dragging = true; last = e.Location; Cursor = Cursors.SizeAll; }
    }

    void MoveCrop(PointF at)
    {
        float x = Math.Max(0, Math.Min((float)RW - crop.Width, at.X - cropGrab.X));
        float y = Math.Max(0, Math.Min((float)RH - crop.Height, at.Y - cropGrab.Y));
        if (x == crop.X && y == crop.Y) return;
        crop = new RectangleF(x, y, crop.Width, crop.Height);
        Invalidate();
        var h = CropDragged;
        if (h != null) h(crop);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (cropDrag) { MoveCrop(UprightAt(e.Location)); return; }
        if (!dragging)
        {
            int hot = BadgeAt(e.Location);
            if (hot != badgeHot)
            {
                badgeHot = hot;
                Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            UpdateHover(e.Location);
            if (loupe) Invalidate();
            return;
        }
        px += e.X - last.X; py += e.Y - last.Y;
        last = e.Location;
        Clamp(); Interact();
        RaiseView();
    }

    // ---------------------------------------------------------------- pixel tools, slideshow, messages

    // The picture pixel under a window point (inverse of the paint transform: pan, then rotate, then zoom).
    bool PixelAt(Point s, out int ix, out int iy)
    {
        ix = iy = 0;
        double a, b;
        if (!PicPoint(s, out a, out b)) return false;
        ix = (int)Math.Floor(a);
        iy = (int)Math.Floor(b);
        return ix >= 0 && iy >= 0 && ix < cur.W && iy < cur.H;
    }

    // the exact point of the picture (in its own pixels, 0..W, 0..H) under a window point
    bool PicPoint(Point s, out double pa, out double pb)
    {
        pa = pb = 0;
        if (!HasImage || zoom <= 0) return false;
        double u = (s.X - (ClientSize.Width / 2.0 + px)) / zoom, v = (s.Y - (ViewH / 2.0 + py)) / zoom, a, b;
        switch (rot & 3)
        {
            case 0: a = u; b = v; break;
            case 1: a = v; b = -u; break;
            case 2: a = -u; b = -v; break;
            default: a = -v; b = u; break;
        }
        if (flipX) a = -a;
        if (flipY) b = -b;
        a /= Ax;                                          // undo the True TV aspect stretch
        pa = a + cur.W / 2.0;
        pb = b + cur.H / 2.0;
        return true;
    }

    // ---- loupe (hold Z): a round magnifier around the pointer, 4 times the current size (at least 400%,
    // at most 6400%), showing the picture's own pixels sharp (no screen effects) with the pixel under the pointer
    // outlined. Gone as soon as Z is let go.
    void DrawLoupe(Graphics g, bool hov, int hx, int hy)
    {
        double a, b;
        if (!PicPoint(mousePt, out a, out b)) return;
        int size = Util.S(240);
        float mag = (float)Math.Min(64, Math.Max(4, zoom * 4));
        var area = new Rectangle(mousePt.X - size / 2, mousePt.Y - size / 2, size, size);
        double ax = Ax;
        double reach = size / (2.0 * mag * Math.Min(1.0, ax)) + 2;                 // picture pixels the loupe can show
        int x0 = Math.Max(0, (int)Math.Floor(a - reach)), y0 = Math.Max(0, (int)Math.Floor(b - reach));
        int x1 = Math.Min(cur.W, (int)Math.Ceiling(a + reach)), y1 = Math.Min(cur.H, (int)Math.Ceiling(b + reach));
        GraphicsState st = g.Save();
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(area);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Bg)) g.FillPath(bg, path);
            g.SetClip(path);
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TranslateTransform(mousePt.X, mousePt.Y);
            g.RotateTransform(rot * 90f);
            g.ScaleTransform(mag, mag);
            if (ax != 1.0) g.ScaleTransform((float)ax, 1f);
            if (flipX || flipY) g.ScaleTransform(flipX ? -1f : 1f, flipY ? -1f : 1f);
            g.TranslateTransform((float)-a, (float)-b);
            if (x1 > x0 && y1 > y0)
                g.DrawImage(cur.Img, new Rectangle(x0, y0, x1 - x0, y1 - y0), x0, y0, x1 - x0, y1 - y0, GraphicsUnit.Pixel, ia);
            if (hov)
                using (var pen = new Pen(Color.FromArgb(230, 255, 220, 0), 2f / mag)) g.DrawRectangle(pen, hx, hy, 1, 1);
        }
        g.Restore(st);
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var ring = new Pen(Color.FromArgb(60, 130, 230), Math.Max(2f, Util.K * 2f))) g.DrawEllipse(ring, area);
        g.SmoothingMode = old;
        string label = (int)Math.Round(mag * 100) + "%";                          // the loupe's own size: picture pixels x mag
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        SizeF ls = g.MeasureString(label, font);
        var lr = new RectangleF(area.X + (area.Width - ls.Width) / 2 - 4, area.Bottom - ls.Height - Util.S(10), ls.Width + 8, ls.Height);
        using (var lb = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(lb, lr);
        g.DrawString(label, font, Brushes.Gainsboro, lr.X + 4, lr.Y);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Z && loupe) { loupe = false; Invalidate(); }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        if (loupe) { loupe = false; Invalidate(); }          // the key-up would go elsewhere
    }

    void UpdateHover(Point p)
    {
        if (StripShown && p.Y >= ClientSize.Height - StripH)       // over the filmstrip: no pixel readout or loupe
        {
            if (mouseIn) { mouseIn = false; lhx = lhy = -2; InvalidateReadout(); if (loupe) Invalidate(); }
            return;
        }
        mouseIn = true; mousePt = p;
        int ix, iy;
        if (!PixelAt(p, out ix, out iy)) { ix = -1; iy = -1; }
        if (ix == lhx && iy == lhy) return;
        lhx = ix; lhy = iy;
        InvalidateReadout();
    }

    // Repaint only the info bar for a moving pointer; the whole picture only when the pixel outline is shown.
    void InvalidateReadout()
    {
        if (zoom >= 8) { Invalidate(); return; }
        int h = font.Height + Util.S(14);
        Invalidate(new Rectangle(0, Math.Max(0, ViewH - h), ClientSize.Width, h));
    }

    // + / - : whole-number steps above 100%, so enlarged pixels stay square; 1.25x steps below
    double NextZoom(int dir)
    {
        const double eps = 1e-6;
        if (dir > 0)
        {
            if (zoom >= 1 - eps) return Math.Floor(zoom + eps) + 1;
            return Math.Min(1.0, zoom * 1.25);
        }
        if (zoom > 1 + eps) return Math.Max(1.0, Math.Ceiling(zoom - eps) - 1);
        return zoom / 1.25;
    }

    public void ToggleFitLock()
    {
        fitLock = !fitLock;
        if (!HasImage) { Flash(fitLock ? "Fit to screen: on" : "Fit to screen: off (100%)"); return; }
        if (fitLock) SetFit(2);
        else { fitMode = 0; zoom = 1; px = py = 0; fast = false; Clamp(); Invalidate(); }
        Flash(fitLock ? "Fit to screen: on (every picture fits the window)" : "Fit to screen: off (every picture opens at 100%)");
    }

    // H always flips left-right and V top-bottom as you see the picture, whatever its rotation
    void Flip(bool horizontal)
    {
        if (!HasImage) return;
        bool local = horizontal != ((rot & 1) == 1);       // turned on its side: the picture's own axes are swapped
        if (local) flipX = !flipX; else flipY = !flipY;
        Flash(horizontal ? "Flipped left-right" : "Flipped top-bottom");
        Invalidate();
    }

    public void ToggleChecker()
    {
        checker = !checker;
        Flash(checker ? "Checkerboard background on (shows transparency)" : "Checkerboard background off");
        Invalidate();
    }

    // checkerboard in window pixels behind the picture's on-screen rectangle
    void DrawChecker(Graphics g)
    {
        int cell = Math.Max(4, Util.S(8));
        if (checkerBrush == null || checkerCell != cell)
        {
            if (checkerBrush != null) checkerBrush.Dispose();
            using (var tileBmp = new Bitmap(cell * 2, cell * 2, PixelFormat.Format32bppArgb))
            {
                using (Graphics tg = Graphics.FromImage(tileBmp))
                using (var light = new SolidBrush(Color.FromArgb(102, 102, 102)))
                using (var dark = new SolidBrush(Color.FromArgb(68, 68, 68)))
                {
                    tg.FillRectangle(dark, 0, 0, cell * 2, cell * 2);
                    tg.FillRectangle(light, 0, 0, cell, cell);
                    tg.FillRectangle(light, cell, cell, cell, cell);
                }
                checkerBrush = new TextureBrush(tileBmp, WrapMode.Tile);   // the brush keeps its own copy
            }
            checkerCell = cell;
        }
        double w = RW * zoom, h = RH * zoom;
        double x = ClientSize.Width / 2.0 + px - w / 2, y = ViewH / 2.0 + py - h / 2;
        g.FillRectangle(checkerBrush, (float)x, (float)y, (float)w, (float)h);
    }

    void CyclePixMode()
    {
        // order: crisp -> sharp -> smooth -> crisp; "auto" starts from what it does for this picture
        int now = pixMode != 0 ? pixMode : cur != null && cur.Note != null ? 3 : 2;
        pixMode = now == 1 ? 3 : now == 3 ? 2 : 1;
        Flash(pixMode == 1 ? "Enlarged pixels: crisp" : pixMode == 3 ? "Enlarged pixels: sharp (even pixels at any size)"
                                                                 : "Enlarged pixels: smooth");
        Invalidate();
    }

    public void ShowMessage(string text) { Flash(text); }

    void Flash(string text)
    {
        flash = text;
        flashTimer.Stop(); flashTimer.Start();
        Invalidate();
    }

    void ToggleSlideshow()
    {
        if (slideshow) { StopSlideshow(); Flash("Slideshow stopped"); return; }
        if (files.Count < 2) { Flash("A slideshow needs at least two pictures"); return; }
        slideshow = true;
        if (strip && HasImage) { Refit(); Clamp(); }        // the filmstrip hides during a slideshow
        slideTimer.Interval = SlideSecs[slideIdx] * 1000;
        slideTimer.Start();
        Flash("Slideshow: " + SlideSecs[slideIdx] + " s per picture   (Up / Down = interval, S or Esc = stop)");
    }

    public void StopSlideshow()
    {
        if (!slideshow) return;
        slideshow = false;
        slideTimer.Stop();
        if (strip && HasImage) { Refit(); Clamp(); }
        Invalidate();
    }

    void ChangeSlide(int dir)                                                          // dir = +1: longer per picture
    {
        slideIdx = Math.Max(0, Math.Min(SlideSecs.Length - 1, slideIdx + dir));
        slideTimer.Interval = SlideSecs[slideIdx] * 1000;
        slideTimer.Stop(); slideTimer.Start();
        Flash("Slideshow: " + SlideSecs[slideIdx] + " s per picture");
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        mouseIn = false; lhx = lhy = -2;
        if (badgeHot >= 0) { badgeHot = -1; Cursor = Cursors.Default; Invalidate(); }
        InvalidateReadout();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false; Cursor = Cursors.Default;
        if (cropDrag)
        {
            cropDrag = false;
            Invalidate();
            var h = CropDropped;
            if (h != null) h(crop);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!HasImage || (StripShown && e.Y >= ClientSize.Height - StripH) || !crop.IsEmpty) return;   // the crop frame: no zoom
        if (BadgeAt(e.Location) >= 0) return;             // a quick second click on a badge just cycles again
        if (Math.Abs(zoom - 1) < 1e-6) SetFit(2); else ZoomTo(1, e.X, e.Y);
    }
}

// The computer badge's facts: who made the machine a retro picture belongs to, where it was designed (one or two
// flags), the year it came out and how many colours its palette offers. Keyed by RECOIL's platform name (cur.Note).
// Plain maker names only, no logos. A blank year or palette means it isn't known well enough to show. "PC" and
// "Unknown" have no entry, so no badge. Flags: US, GB, JP, SU (Soviet Union), RU, PL, CZ, ES, PT, BR.
sealed class MachineInfo
{
    public readonly string Maker, Model, Flags;
    public readonly int Year;
    public readonly long Colours;

    MachineInfo(string maker, string model, int year, long colours, string flags)
    {
        Maker = maker; Model = model; Year = year; Colours = colours; Flags = flags;
    }

    const long M16 = 16777216;

    static readonly Dictionary<string, MachineInfo> all = new Dictionary<string, MachineInfo>(StringComparer.Ordinal)
    {
        { "Amiga", new MachineInfo("Commodore", "Amiga", 1985, 4096, "US") },
        { "Amiga DCTV", new MachineInfo("Digital Creations", "DCTV for the Amiga", 1991, M16, "US") },
        { "Amiga HAM-E", new MachineInfo("Black Belt Systems", "HAM-E for the Amiga", 1991, M16, "US") },
        { "Amstrad CPC", new MachineInfo("Amstrad", "CPC", 1984, 27, "GB") },
        { "Apple II", new MachineInfo("Apple", "Apple II", 1977, 16, "US") },
        { "Apple IIe", new MachineInfo("Apple", "Apple IIe", 1983, 16, "US") },
        { "Apple IIGS", new MachineInfo("Apple", "Apple IIGS", 1986, 4096, "US") },
        { "Apple Macintosh", new MachineInfo("Apple", "Macintosh", 1984, 2, "US") },
        { "Atari 8-bit", new MachineInfo("Atari", "400 / 800 / XL / XE", 1979, 256, "US") },
        { "Atari 8-bit VBXE", new MachineInfo("Atari + VBXE", "8-bit with VBXE", 2006, 2097152, "US PL") },
        { "Atari Portfolio", new MachineInfo("Atari", "Portfolio", 1989, 2, "US GB") },
        { "Atari ST", new MachineInfo("Atari", "ST", 1985, 512, "US") },
        { "Atari STE", new MachineInfo("Atari", "STE", 1989, 4096, "US") },
        { "Atari TT", new MachineInfo("Atari", "TT030", 1990, 4096, "US") },
        { "Atari Falcon", new MachineInfo("Atari", "Falcon030", 1992, 262144, "US") },
        { "BBC Micro", new MachineInfo("Acorn", "BBC Micro", 1981, 8, "GB") },
        { "Commodore VIC-20", new MachineInfo("Commodore", "VIC-20", 1980, 16, "US") },
        { "Commodore 16/116/Plus4", new MachineInfo("Commodore", "C16 / 116 / Plus/4", 1984, 121, "US") },
        { "Commodore 64", new MachineInfo("Commodore", "C64", 1982, 16, "US") },
        { "Commodore 128", new MachineInfo("Commodore", "C128", 1985, 16, "US") },
        { "Electronika BK", new MachineInfo("Elektronika", "BK-0010", 1985, 4, "SU") },
        { "Electronika MC 0515", new MachineInfo("Elektronika", "MS 0515", 0, 0, "SU") },
        { "FM Towns", new MachineInfo("Fujitsu", "FM Towns", 1989, M16, "JP") },
        { "HP 48", new MachineInfo("Hewlett-Packard", "HP 48 calculator", 1990, 2, "US") },
        { "MSX", new MachineInfo("ASCII / Microsoft", "MSX", 1983, 16, "JP US") },
        { "MSX2", new MachineInfo("ASCII", "MSX2", 1985, 512, "JP") },
        { "MSX2+", new MachineInfo("ASCII", "MSX2+", 1988, 19268, "JP") },
        { "MSX V9990 VDP", new MachineInfo("Yamaha V9990", "GFX9000 for MSX", 1994, 32768, "JP") },
        { "Oric", new MachineInfo("Tangerine", "Oric", 1983, 8, "GB") },
        { "NEC PC-80", new MachineInfo("NEC", "PC-8001", 1979, 8, "JP") },
        { "NEC PC-88", new MachineInfo("NEC", "PC-8801", 1981, 8, "JP") },
        { "NEC PC-88 VA", new MachineInfo("NEC", "PC-88 VA", 1987, 65536, "JP") },
        { "NEC PC-98", new MachineInfo("NEC", "PC-9801", 1982, 4096, "JP") },
        { "PlayStation", new MachineInfo("Sony", "PlayStation", 1994, M16, "JP") },
        { "Psion Series 3", new MachineInfo("Psion", "Series 3", 1991, 2, "GB") },
        { "SAM Coupe", new MachineInfo("Miles Gordon Technology", "SAM Coupe", 1989, 128, "GB") },
        { "Sharp X68000", new MachineInfo("Sharp", "X68000", 1987, 65536, "JP") },
        { "Tandy 1000", new MachineInfo("Tandy", "Tandy 1000", 1984, 16, "US") },
        { "TRS-80", new MachineInfo("Tandy", "TRS-80", 1977, 2, "US") },
        { "TRS-80 Color Computer", new MachineInfo("Tandy", "TRS-80 Color Computer", 1980, 9, "US") },
        { "Timex 2048", new MachineInfo("Timex", "TC 2048", 1984, 15, "PT GB") },
        { "Vector-06C", new MachineInfo("Temirazov / Sokolov", "Vector-06C", 1987, 256, "SU") },
        { "ZX81", new MachineInfo("Sinclair", "ZX81", 1981, 2, "GB") },
        { "ZX Spectrum", new MachineInfo("Sinclair", "ZX Spectrum", 1982, 15, "GB") },
        { "ZX Spectrum ULAplus", new MachineInfo("ULAplus", "ZX Spectrum with ULAplus", 2009, 256, "GB") },
        { "ZX Spectrum Profi", new MachineInfo("Profi", "ZX Spectrum Profi", 1990, 256, "SU") },
        { "ZX Spectrum Next", new MachineInfo("SpecNext", "ZX Spectrum Next", 2017, 512, "GB BR") },
        { "ZX Evolution", new MachineInfo("NedoPC", "ZX Evolution", 2009, 32768, "RU") },
        { "ZX-Uno", new MachineInfo("ZX-Uno team", "ZX-Uno", 2016, 256, "ES") },
        { "eLeMeNt ZX", new MachineInfo("LMN128", "eLeMeNt ZX", 0, M16, "CZ") },
    };

    public static MachineInfo For(string platform)
    {
        MachineInfo m;
        return platform != null && all.TryGetValue(platform, out m) ? m : null;
    }

    // "1982 \u00B7 15 colours", or whichever half is known
    public string Facts
    {
        get
        {
            string c = null;
            if (Colours >= 1000000) c = (Math.Floor(Colours / 100000.0) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " million colours";
            else if (Colours > 0) c = Colours.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " colours";
            string y = Year > 0 ? Year.ToString() : null;
            return y != null && c != null ? y + " \u00B7 " + c : y ?? c ?? "";
        }
    }

    // "11 of 16 colours used" (the computer's palette only when the picture stays within it)
    public static string UsedText(int colours, MachineInfo mi)
    {
        if (colours >= ThumbService.MaxCount) return colours.ToString("#,0") + " or more colours used";
        return colours.ToString("#,0") + (mi != null && mi.Colours > 0 && mi.Colours < 1000000 && colours <= mi.Colours ? " of " + mi.Colours.ToString("#,0") : "")
               + (colours == 1 ? " colour used" : " colours used");
    }

    public string[] FlagList { get { return Flags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); } }

    // a simplified flag, 3:2, drawn into r (antialiased by the caller)
    public static void DrawFlag(Graphics g, RectangleF r, string code)
    {
        Color red = Color.FromArgb(206, 17, 38), white = Color.White, blue = Color.FromArgb(0, 40, 104);
        GraphicsState st = g.Save();
        g.SetClip(r);
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        switch (code)
        {
            case "US":
                for (int i = 0; i < 7; i++)
                    using (var b = new SolidBrush(i % 2 == 0 ? red : white)) g.FillRectangle(b, x, y + h * i / 7f, w, h / 7f + 0.5f);
                using (var b = new SolidBrush(Color.FromArgb(10, 49, 97))) g.FillRectangle(b, x, y, w * 0.42f, h * 4 / 7f);
                break;
            case "GB":
            {
                using (var b = new SolidBrush(Color.FromArgb(1, 33, 105))) g.FillRectangle(b, r);
                float t = h / 5f;
                using (var p = new Pen(white, t)) { g.DrawLine(p, x, y, x + w, y + h); g.DrawLine(p, x, y + h, x + w, y); }
                using (var p = new Pen(red, t / 2.5f)) { g.DrawLine(p, x, y, x + w, y + h); g.DrawLine(p, x, y + h, x + w, y); }
                using (var b = new SolidBrush(white)) { g.FillRectangle(b, x + w / 2 - t, y, 2 * t, h); g.FillRectangle(b, x, y + h / 2 - t, w, 2 * t); }
                using (var b = new SolidBrush(red)) { g.FillRectangle(b, x + w / 2 - t * 0.6f, y, t * 1.2f, h); g.FillRectangle(b, x, y + h / 2 - t * 0.6f, w, t * 1.2f); }
                break;
            }
            case "JP":
            {
                using (var b = new SolidBrush(white)) g.FillRectangle(b, r);
                float d = h * 0.6f;
                using (var b = new SolidBrush(Color.FromArgb(188, 0, 45))) g.FillEllipse(b, x + (w - d) / 2, y + (h - d) / 2, d, d);
                break;
            }
            case "SU":
            {
                using (var b = new SolidBrush(Color.FromArgb(204, 0, 0))) g.FillRectangle(b, r);
                float s = h * 0.17f, cx = x + w * 0.2f, cy = y + h * 0.24f;      // the small gold star at the hoist
                var pts = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5, rr = i % 2 == 0 ? s : s * 0.4;
                    pts[i] = new PointF(cx + (float)(rr * Math.Cos(a)), cy + (float)(rr * Math.Sin(a)));
                }
                using (var b = new SolidBrush(Color.FromArgb(255, 216, 0))) g.FillPolygon(b, pts);
                break;
            }
            case "RU":
                Stripes(g, r, false, white, Color.FromArgb(0, 57, 166), Color.FromArgb(213, 43, 30));
                break;
            case "PL":
                Stripes(g, r, false, white, Color.FromArgb(220, 20, 60));
                break;
            case "CZ":
                Stripes(g, r, false, white, Color.FromArgb(215, 20, 26));
                using (var b = new SolidBrush(Color.FromArgb(17, 69, 126)))
                    g.FillPolygon(b, new[] { new PointF(x, y), new PointF(x + w / 2, y + h / 2), new PointF(x, y + h) });
                break;
            case "ES":
                using (var b = new SolidBrush(Color.FromArgb(170, 21, 27))) g.FillRectangle(b, r);
                using (var b = new SolidBrush(Color.FromArgb(241, 191, 0))) g.FillRectangle(b, x, y + h / 4, w, h / 2);
                break;
            case "PT":
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 0, 0))) g.FillRectangle(b, r);
                using (var b = new SolidBrush(Color.FromArgb(0, 102, 0))) g.FillRectangle(b, x, y, w * 0.4f, h);
                float d = h * 0.45f;
                using (var b = new SolidBrush(Color.FromArgb(255, 204, 0))) g.FillEllipse(b, x + w * 0.4f - d / 2, y + (h - d) / 2, d, d);
                break;
            }
            case "BR":
            {
                using (var b = new SolidBrush(Color.FromArgb(0, 148, 64))) g.FillRectangle(b, r);
                float mx = w * 0.1f, my = h * 0.12f;
                using (var b = new SolidBrush(Color.FromArgb(254, 223, 0)))
                    g.FillPolygon(b, new[] { new PointF(x + w / 2, y + my), new PointF(x + w - mx, y + h / 2), new PointF(x + w / 2, y + h - my), new PointF(x + mx, y + h / 2) });
                float d = h * 0.46f;
                using (var b = new SolidBrush(Color.FromArgb(0, 39, 118))) g.FillEllipse(b, x + (w - d) / 2, y + (h - d) / 2, d, d);
                break;
            }
        }
        g.Restore(st);
        using (var p = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
    }

    static void Stripes(Graphics g, RectangleF r, bool vertical, params Color[] cols)
    {
        for (int i = 0; i < cols.Length; i++)
            using (var b = new SolidBrush(cols[i]))
                if (vertical) g.FillRectangle(b, r.X + r.Width * i / cols.Length, r.Y, r.Width / cols.Length + 0.5f, r.Height);
                else g.FillRectangle(b, r.X, r.Y + r.Height * i / cols.Length, r.Width, r.Height / cols.Length + 0.5f);
    }
}

// what the effect badges show (Canvas.PaintBadge)
sealed class BadgeState
{
    public int Signal, Scan, Mask, Glow, Curve, PixMode;
    public bool ScanDim, MaskDim, CurveDim, Tv, TvKnown, Auto;
    public double Aspect = 1.0;
}
