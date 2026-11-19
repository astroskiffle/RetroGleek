// RetroGleek - PictureAdjust.cs
// Brightness, contrast, saturation and hue for the picture going INTO a retro conversion (Convert to a retro format
// and the retro preview), and the sliders that set them. The original file is never changed: the adjustment is
// made while the picture is drawn onto the computer's screen, before any colour is chosen, so the new file (and
// what the preview shows) is of the adjusted picture. The black borders stay black.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

sealed class PictureAdjust
{
    public int Brightness;       // -100 .. 100 (up to half the range darker / lighter)
    public int Contrast;         // -100 (flat grey) .. 100 (three times as steep)
    public int Saturation;       // -100 (black and white) .. 100 (twice as strong)
    public int Hue;              // -180 .. 180 degrees round the colour wheel

    public bool IsNeutral { get { return Brightness == 0 && Contrast == 0 && Saturation == 0 && Hue == 0; } }

    public PictureAdjust Copy() { return new PictureAdjust { Brightness = Brightness, Contrast = Contrast, Saturation = Saturation, Hue = Hue }; }

    public static PictureAdjust Clamped(int brightness, int contrast, int saturation, int hue)
    {
        return new PictureAdjust
        {
            Brightness = Math.Max(-100, Math.Min(100, brightness)), Contrast = Math.Max(-100, Math.Min(100, contrast)),
            Saturation = Math.Max(-100, Math.Min(100, saturation)), Hue = Math.Max(-180, Math.Min(180, hue))
        };
    }

    // how steep the contrast is: 0 .. 3, 1 = unchanged
    public static float ContrastFactor(int contrast) { return contrast >= 0 ? 1f + contrast / 50f : 1f + contrast / 100f; }
    public static int ContrastFrom(float factor) { return (int)Math.Round(factor >= 1 ? (factor - 1) * 50 : (factor - 1) * 100); }

    // e.g. "contrast +20, saturation +30" (empty when neutral)
    public string Text()
    {
        string s = "";
        if (Brightness != 0) s += (s.Length > 0 ? ", " : "") + "brightness " + Signed(Brightness);
        if (Contrast != 0) s += (s.Length > 0 ? ", " : "") + "contrast " + Signed(Contrast);
        if (Saturation != 0) s += (s.Length > 0 ? ", " : "") + "saturation " + Signed(Saturation);
        if (Hue != 0) s += (s.Length > 0 ? ", " : "") + "hue " + Signed(Hue) + "\u00B0";
        return s;
    }

    static string Signed(int v) { return (v > 0 ? "+" : "") + v; }

    // The whole adjustment as one GDI+ colour matrix (row vectors: [r g b a 1] x M, values 0..1): contrast round the
    // middle grey, then brightness, then hue turned and saturation changed round the grey axis (both keep each
    // colour's brightness). It is drawn with the picture, so only the picture's pixels change, never the borders.
    public ColorMatrix Matrix()
    {
        float cf = ContrastFactor(Contrast);
        float off = (0.5f - 0.5f * cf) + Brightness * 0.5f / 100f;      // brightness: up to half the range each way
        double th = Hue * Math.PI / 180;
        float c = (float)Math.Cos(th), sn = (float)Math.Sin(th);
        float[,] h =                                                     // hue turn (column vectors: out = h x in)
        {
            { .213f + c * .787f - sn * .213f, .715f - c * .715f - sn * .715f, .072f - c * .072f + sn * .928f },
            { .213f - c * .213f + sn * .143f, .715f + c * .285f + sn * .140f, .072f - c * .072f - sn * .283f },
            { .213f - c * .213f - sn * .787f, .715f - c * .715f + sn * .715f, .072f + c * .928f + sn * .072f }
        };
        float s = 1f + Saturation / 100f;
        float[,] sat =
        {
            { .213f + .787f * s, .715f - .715f * s, .072f - .072f * s },
            { .213f - .213f * s, .715f + .285f * s, .072f - .072f * s },
            { .213f - .213f * s, .715f - .715f * s, .072f + .928f * s }
        };
        var m = new float[5][];
        for (int i = 0; i < 5; i++) m[i] = new float[5];
        for (int row = 0; row < 3; row++)                                // (sat x h), transposed for row vectors
            for (int col = 0; col < 3; col++)
            {
                float v = 0;
                for (int k = 0; k < 3; k++) v += sat[row, k] * h[k, col];
                m[col][row] = v * cf;
            }
        m[3][3] = 1;
        for (int j = 0; j < 3; j++) m[4][j] = off;                       // each row of sat x h adds up to 1
        m[4][4] = 1;
        return new ColorMatrix(m);
    }
}

// Four sliders (brightness, contrast, saturation, hue) with their values, and Auto and Reset buttons. Changed is
// raised once the sliders rest for a moment (a conversion takes a little while), or at once for Auto and Reset.
// AutoClicked: the owner works out the levels (it has the picture) and sets Value.
sealed class AdjustPanel : Panel
{
    public event EventHandler Changed;
    public event EventHandler AutoClicked;

    static readonly string[] Names = { "Brightness", "Contrast", "Saturation", "Hue" };
    readonly DarkSlider[] sliders = new DarkSlider[4];
    readonly Label[] values = new Label[4];
    readonly Timer timer = new Timer { Interval = 250 };
    readonly ToolTip tip = new ToolTip();
    bool syncing;

    public AdjustPanel(Color back, Color text, Font font, int width)
    {
        BackColor = back;
        Width = width;
        int rowH = Util.S(26), labelW = Util.S(82), valueW = Util.S(54);
        string[] tips =
        {
            "Lighter or darker before converting",
            "More or less difference between light and dark: more contrast gives the computer's few colours clearer steps",
            "Stronger or paler colours: these computers' colours are strong, so a little more often helps; all the way down gives black and white",
            "Turns every colour round the colour wheel (in degrees), e.g. to steer skin or sky towards a colour the computer has"
        };
        for (int i = 0; i < 4; i++)
        {
            var l = new Label { Left = 0, Top = i * rowH, Width = labelW, Height = rowH, Text = Names[i] + ":", TextAlign = ContentAlignment.MiddleLeft, ForeColor = text, Font = font };
            var s = new DarkSlider { Left = labelW, Top = i * rowH + (rowH - Util.S(18)) / 2, Width = width - labelW - valueW - Util.S(6), Height = Util.S(18) };
            s.Minimum = i == 3 ? -180 : -100; s.Maximum = i == 3 ? 180 : 100;
            var v = new Label { Left = s.Right + Util.S(6), Top = i * rowH, Width = valueW, Height = rowH, TextAlign = ContentAlignment.MiddleLeft, ForeColor = text, Font = font };
            sliders[i] = s; values[i] = v;
            Controls.Add(l); Controls.Add(s); Controls.Add(v);
            tip.SetToolTip(l, tips[i]); tip.SetToolTip(s, tips[i] + " (double-click: back to 0)");
            int ii = i;
            s.ValueChanged += delegate
            {
                ShowValue(ii);
                if (syncing) return;
                timer.Stop(); timer.Start();
            };
            s.DoubleClick += delegate { s.Value = 0; };
        }
        int by = 4 * rowH + Util.S(6);
        Func<string, int, Button> button = delegate (string t, int x)
        {
            var b = new Button { Text = t, Left = x, Top = by, Width = Util.S(80), Height = Util.S(26), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(52, 52, 52), ForeColor = text, Font = font, TabStop = false };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 72, 72);
            Controls.Add(b);
            return b;
        };
        Button auto = button("Auto", labelW), reset = button("Reset", labelW + Util.S(88));
        tip.SetToolTip(auto, "Brightness and contrast that stretch the picture's darkest and lightest parts to the full range");
        tip.SetToolTip(reset, "Everything back to 0: the picture as it is");
        auto.Click += delegate { var h = AutoClicked; if (h != null) h(this, EventArgs.Empty); };
        reset.Click += delegate { Value = new PictureAdjust(); Fire(); };
        Height = by + Util.S(30);
        timer.Tick += delegate { timer.Stop(); Fire(); };
        for (int i = 0; i < 4; i++) ShowValue(i);
    }

    void ShowValue(int i)
    {
        int v = sliders[i].Value;
        values[i].Text = (v > 0 ? "+" : "") + v + (i == 3 ? "\u00B0" : "");
    }

    void Fire()
    {
        timer.Stop();
        var h = Changed;
        if (h != null) h(this, EventArgs.Empty);
    }

    // the values shown; setting them raises nothing (AutoClicked's owner calls SetAndApply instead)
    public PictureAdjust Value
    {
        get { return PictureAdjust.Clamped(sliders[0].Value, sliders[1].Value, sliders[2].Value, sliders[3].Value); }
        set
        {
            PictureAdjust a = value ?? new PictureAdjust();
            syncing = true;
            try
            {
                sliders[0].Value = a.Brightness; sliders[1].Value = a.Contrast; sliders[2].Value = a.Saturation; sliders[3].Value = a.Hue;
            }
            finally { syncing = false; }
            timer.Stop();
            for (int i = 0; i < 4; i++) ShowValue(i);
        }
    }

    // new values (Auto), and Changed at once
    public void SetAndApply(PictureAdjust a) { Value = a; Fire(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tip.Dispose(); }
        base.Dispose(disposing);
    }
}
