using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FrameGrip;

/// <summary>
/// Il grafico dei tempi dei frame delle schermate "Limite ideale" e "Confronta misure": una o più serie (240 punti
/// sull'intera misura), la linea del limite scelto, i segni dei cali e le etichette del tempo sotto.
/// Colori dal tema dell'app (si aggiorna da solo se il tema cambia).
/// </summary>
public sealed class FrameGraph : FrameworkElement
{
    public sealed record Series(float[] Values, Brush Brush, double Upto = 1);

    public List<Series> Lines { get; } = new();
    public double CapMs { get; set; }          // linea del limite (0 = nessuna)
    public float[]? Marks { get; set; }        // posizioni dei cali, negative = calo forte
    public double DurationSec { get; set; } = 120;
    public string Label { get; set; } = "";
    public string? Corner { get; set; }        // testo in alto a destra (es. "171 FPS")
    public double YMin { get; set; } = double.NaN;
    public double YMax { get; set; } = double.NaN;

    public FrameGraph()
    {
        Theming.Changed += InvalidateVisual;
        Unloaded += (_, _) => Theming.Changed -= InvalidateVisual;
        Loaded += (_, _) => { Theming.Changed -= InvalidateVisual; Theming.Changed += InvalidateVisual; };
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 30) return;
        var panel = Res("Panel", Brushes.Black);
        var line = Res("Line", Brushes.Gray);
        var dim = Res("FgDim", Brushes.Gray);
        var fg = Res("Fg", Brushes.White);
        var warn = Res("Warn", Brushes.Orange);
        var danger = Res("Danger", Brushes.Red);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRoundedRectangle(panel, new Pen(line, 1), new Rect(0.5, 0.5, w - 1, h - 1), 10, 10);
        const double L = 30, R = 8, T = 22, B = 18;
        var plot = new Rect(L, T, Math.Max(10, w - L - R), Math.Max(10, h - T - B));

        // scala: dai valori, con un po' di margine; la linea del limite deve stare dentro
        double lo = double.MaxValue, hi = 0;
        foreach (var s in Lines)
        {
            int n = (int)Math.Ceiling(s.Values.Length * Math.Clamp(s.Upto, 0, 1));
            for (int i = 0; i < n; i++) { float v = s.Values[i]; if (v <= 0) continue; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        }
        if (CapMs > 0) { lo = Math.Min(lo, CapMs); hi = Math.Max(hi, CapMs); }
        if (hi <= 0) { lo = 4; hi = 12; }
        double yMin = double.IsNaN(YMin) ? Math.Max(0, Math.Floor(lo * 0.8)) : YMin;
        double yMax = double.IsNaN(YMax) ? Math.Ceiling(Math.Max(hi, yMin + 2) * 1.05) : YMax;
        if (yMax - yMin < 2) yMax = yMin + 2;
        double Y(double v) => plot.Bottom - (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin) * plot.Height;

        var gridPen = new Pen(line, 1);
        var small = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Text(string s, double size, Brush b, bool bold = false) =>
            new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                bold ? new Typeface(small.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : small, size, b, ppd);
        for (int k = 0; k <= 2; k++)
        {
            double v = yMin + (yMax - yMin) * k / 2, y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(v.ToString("0", CultureInfo.InvariantCulture), 9.5, dim);
            dc.DrawText(t, new Point(plot.Left - 6 - t.Width, y - t.Height / 2));
        }
        // tempo sotto: 0, metà, fine
        for (int k = 0; k <= 2; k++)
        {
            double sec = DurationSec * k / 2;
            var t = Text($"{(int)sec / 60}:{(int)sec % 60:00}", 9.5, dim);
            double x = plot.Left + plot.Width * k / 2 - (k == 0 ? 0 : k == 2 ? t.Width : t.Width / 2);
            dc.DrawText(t, new Point(x, plot.Bottom + 3));
        }
        if (Label.Length > 0) dc.DrawText(Text(Label, 10, dim), new Point(plot.Left + 4, 5));
        if (Corner != null) { var c = Text(Corner, 12, fg, true); dc.DrawText(c, new Point(w - 10 - c.Width, 4)); }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var s in Lines)
        {
            int n = s.Values.Length;
            int upto = (int)Math.Ceiling(n * Math.Clamp(s.Upto, 0, 1));
            if (upto < 2) continue;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < upto; i++)
                {
                    if (s.Values[i] <= 0) continue;
                    var p = new Point(plot.Left + plot.Width * i / (n - 1), Y(s.Values[i]));
                    if (!started) { ctx.BeginFigure(p, false, false); started = true; } else ctx.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(s.Brush, 1.3) { LineJoin = PenLineJoin.Round }, geo);
        }
        if (CapMs > 0)
        {
            double y = Math.Round(Y(CapMs)) + 0.5;
            dc.DrawLine(new Pen(warn, 1.2) { DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0) }, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        dc.Pop();
        if (Marks != null)
            foreach (var m in Marks)
            {
                double x = plot.Left + plot.Width * Math.Abs(m);
                dc.DrawEllipse(m < 0 ? danger : warn, null, new Point(x, plot.Top - 6), 3, 3);
            }
    }
}
