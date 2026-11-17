using System.Windows;
using System.Windows.Media;

namespace FrameGrip;

/// <summary>
/// Le icone vettoriali degli stili Dashboard, Anelli e Icone (le stesse delle proposte grafiche): una per tipo di
/// sensore (temperatura, punto caldo, giunzione della memoria, carico, consumo, frequenza, memoria, tensione,
/// ventola...) e una per componente (GPU, CPU, RAM, prestazioni, info). Ogni icona si costruisce una volta per
/// tipo, grandezza e colore e poi si riusa (disegno congelato, centrato in 0,0).
/// </summary>
public sealed partial class OverlayView
{
    private static readonly Brush IconHole = Frozen(Color.FromRgb(0x0B, 0x0E, 0x13));
    private static readonly Brush IconFaint = Frozen(Color.FromArgb(64, 255, 255, 255));
    private readonly Dictionary<(string, double, Brush), Drawing> _icons = new();

    /// <summary>L'icona di un sensore, scelta dal tipo e da qualche parola del nome (es. "Hot Spot").</summary>
    private static string IconOf(in Cell c)
    {
        if (IsGrid(c)) return "cpu";
        string n = c.Name;
        bool Has(string w) => n.Contains(w, StringComparison.OrdinalIgnoreCase);
        return c.Kind switch
        {
            "Temperature" => Has("hot") ? "hot" : Has("junction") || Has("memory") || Has("mem ") ? "junction" : "temp",
            "Load" or "Control" or "Level" or "smooth" => "load",
            "Clock" or "MemSpeed" or "Frequency" => "clock",
            "Power" or "Current" => "power",
            "Voltage" => "volt",
            "Fan" => "fan",
            "Data" or "SmallData" => "mem",
            "Api" => "api",
            "Session" => "timer",
            "Driver" => "driver",
            "fps" or "avg" or "low1" or "low01" => "perf",
            "ft" or "p99" or "jitter" => "timer",
            _ => "info",
        };
    }

    /// <summary>L'icona di un componente (riga dell'overlay).</summary>
    private static string GroupIcon(in Row r) => r.Tag.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') switch
    {
        "GPU" => "gpu", "CPU" => "cpu", "RAM" => "ram", "FPS" => "perf", _ => "info",
    };

    /// <summary>Disegna un'icona centrata in (cx, cy), grande s (strato fisso).</summary>
    private void Icon(DrawingContext? dc, string kind, double cx, double cy, double s, Brush color)
    {
        if (dc == null || !Draws(false)) return;
        s = Math.Round(s * 2) / 2;
        var key = (kind, s, color);
        if (!_icons.TryGetValue(key, out var d)) _icons[key] = d = BuildIcon(kind, s, color);
        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.DrawDrawing(d);
        dc.Pop();
    }

    // ------------------------------------------------------------ costruzione

    private sealed class IconBuilder
    {
        public readonly DrawingGroup Group = new();
        private readonly Brush _c;
        public readonly double K, W;
        public IconBuilder(double s, Brush c) { K = s / 2; W = Math.Max(1.3, s * 0.1); _c = c; }

        private Pen P(double w, Brush? b = null)
        {
            var p = new Pen(b ?? _c, w) { LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        public void Add(Geometry g, bool fill, double w = 0, Brush? brush = null, Brush? fillBrush = null)
        {
            g.Freeze();
            Group.Children.Add(new GeometryDrawing(fill ? fillBrush ?? brush ?? _c : null, w > 0 ? P(w, brush) : null, g));
        }

        public Point Pt(double a, double b) => new(a * K, b * K);

        public void Line(double w, params (double X, double Y)[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(Pt(pts[0].X, pts[0].Y), false, false);
                for (int i = 1; i < pts.Length; i++) c.LineTo(Pt(pts[i].X, pts[i].Y), true, true);
            }
            Add(g, false, w);
        }

        public void Poly(IList<Point> pts, bool fill, double w = 0, Brush? fillBrush = null)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], fill, true);
                for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i], true, true);
            }
            Add(g, fill, w, null, fillBrush);
        }

        public void Circle(double x, double y, double r, bool fill, double w = 0, Brush? brush = null, Brush? fillBrush = null) =>
            Add(new EllipseGeometry(Pt(x, y), r * K, r * K), fill, w, brush, fillBrush);

        public void RoundRect(double x0, double y0, double x1, double y1, double r, bool fill, double w = 0) =>
            Add(new RectangleGeometry(new Rect(Pt(x0, y0), Pt(x1, y1)), r * K, r * K), fill, w);

        /// <summary>Arco di cerchio, angoli in gradi (0 = destra, in senso orario).</summary>
        public void Arc(double x, double y, double r, double from, double to, double w)
        {
            Add(ArcGeometry(x * K, y * K, r * K, from, to - from), false, w);
        }
    }

    private static Geometry ArcGeometry(double cx, double cy, double r, double from, double sweep)
    {
        sweep = Math.Clamp(sweep, 0.01, 359.9);
        double a0 = from * Math.PI / 180, a1 = (from + sweep) * Math.PI / 180;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx + Math.Cos(a0) * r, cy + Math.Sin(a0) * r), false, false);
            c.ArcTo(new Point(cx + Math.Cos(a1) * r, cy + Math.Sin(a1) * r), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private static Drawing BuildIcon(string kind, double s, Brush c)
    {
        var b = new IconBuilder(s, c);
        double k = b.K, w = b.W;
        Point P(double x, double y) => b.Pt(x, y);
        switch (kind)
        {
            case "temp":
                b.RoundRect(-.22, -.9, .22, .35, .22, false, w);
                b.Circle(0, .55, .36, true);
                b.Line(w, (0, -.3), (0, .5));
                foreach (var t in new[] { -.55, -.25 }) b.Line(w * .8, (.35, t), (.6, t));
                break;
            case "hot":
                b.Poly(new[] { P(.05, -1), P(.3, -.6), P(.6, -.2), P(.68, .28), P(.45, .72), P(0, .92), P(-.45, .72), P(-.68, .28),
                    P(-.55, -.2), P(-.32, .05), P(-.25, -.45) }, true);
                b.Poly(new[] { P(0, -.05), P(.25, .3), P(.25, .55), P(0, .72), P(-.25, .55), P(-.25, .3) }, true, 0, IconHole);
                break;
            case "junction":
                b.RoundRect(-.55, -.55, .55, .55, .1, false, w);
                foreach (var t in new[] { -.3, 0, .3 })
                {
                    b.Line(w * .8, (t, -.55), (t, -.85));
                    b.Line(w * .8, (t, .55), (t, .85));
                }
                b.Circle(0, .12, .17, true);
                b.Line(w, (0, -.3), (0, .1));
                break;
            case "load":
                // l'arco che si riempie lo disegna LoadArc (strato che cambia): qui solo il binario
                b.Circle(0, 0, .78, false, w, IconFaint);
                break;
            case "power":
                b.Poly(new[] { P(.2, -.95), P(-.6, .12), P(-.02, .12), P(-.2, .95), P(.6, -.18), P(.04, -.18) }, true);
                break;
            case "clock":
                b.Arc(0, .25, .85, 180, 360, w);
                {
                    double a = -50 * Math.PI / 180;
                    b.Line(w * 1.1, (0, .25), (Math.Cos(a) * .7, .25 + Math.Sin(a) * .7));
                }
                b.Circle(0, .25, .16, true);
                foreach (var ang in new[] { 200.0, 270, 340 })
                {
                    double a = ang * Math.PI / 180;
                    b.Line(w * .7, (Math.Cos(a) * .55, .25 + Math.Sin(a) * .55), (Math.Cos(a) * .75, .25 + Math.Sin(a) * .75));
                }
                break;
            case "mem":
            case "ram":
                b.RoundRect(-.95, -.45, .95, .3, .08, false, w);
                foreach (var t in new[] { -.55, 0, .55 }) b.RoundRect(t - .2, -.25, t + .2, .1, 0, true);
                for (int i = 0; i < 7; i++) { double px = -.8 + i * .27; b.Line(w * .7, (px, .3), (px, .55)); }
                break;
            case "volt":
                {
                    var pts = new (double, double)[21];
                    for (int i = 0; i <= 20; i++) pts[i] = ((i / 20.0 - .5) * 1.8, -Math.Sin(i / 20.0 * 2 * Math.PI) * .45);
                    b.Line(w * 1.1, pts);
                }
                break;
            case "fan":
                for (int j = 0; j < 3; j++)
                {
                    double bs = j * 120;
                    var pts = new List<Point>();
                    for (int i = 0; i <= 12; i++)
                    {
                        double t = i / 12.0, ang = (bs + t * 70) * Math.PI / 180;
                        double rr = .18 + .72 * Math.Sqrt(Math.Sin(t * Math.PI));
                        pts.Add(P(Math.Cos(ang) * (.2 + rr * .9), Math.Sin(ang) * (.2 + rr * .9)));
                    }
                    double e = (bs + 70) * Math.PI / 180, s0 = bs * Math.PI / 180;
                    pts.Add(P(Math.Cos(e) * .2, Math.Sin(e) * .2));
                    pts.Add(P(Math.Cos(s0) * .2, Math.Sin(s0) * .2));
                    b.Poly(pts, true);
                }
                b.Circle(0, 0, .18, true, 0, null, IconHole);
                b.Circle(0, 0, .18, false, w * .8);
                break;
            case "cpu":
                b.RoundRect(-.6, -.6, .6, .6, .12, false, w);
                b.RoundRect(-.25, -.25, .25, .25, 0, true);
                foreach (var t in new[] { -.32, 0, .32 })
                {
                    b.Line(w * .8, (t, -.6), (t, -.9)); b.Line(w * .8, (t, .6), (t, .9));
                    b.Line(w * .8, (-.6, t), (-.9, t)); b.Line(w * .8, (.6, t), (.9, t));
                }
                break;
            case "gpu":
                b.RoundRect(-.85, -.55, .95, .5, .1, false, w);
                b.Circle(.35, -.02, .3, false, w);
                b.Circle(.35, -.02, .08, true);
                b.RoundRect(-.6, -.3, -.2, .25, 0, true);
                b.Line(w * .9, (-.55, .5), (-.55, .85), (.3, .85));
                break;
            case "perf":
                b.Circle(0, .12, .72, false, w);
                b.RoundRect(-.2, -.95, .2, -.75, 0, true);
                b.Line(w * 1.1, (0, .12), (.3, -.25));
                break;
            case "timer":
                b.Circle(0, 0, .8, false, w);
                b.Line(w, (0, -.5), (0, 0), (.4, .2));
                break;
            case "api":
                foreach (var dy in new[] { .35, 0, -.35 })
                    b.Poly(new[] { P(0, dy - .32), P(.85, dy), P(0, dy + .32), P(-.85, dy) }, dy != .35, w * .9, IconHole);
                break;
            case "driver":
                {
                    var pts = new List<Point>();
                    for (int i = 0; i < 32; i++)
                    {
                        double a = i / 32.0 * 2 * Math.PI, r = (i / 2) % 2 == 0 ? .82 : .58;
                        pts.Add(P(Math.Cos(a) * r, Math.Sin(a) * r));
                    }
                    b.Poly(pts, false, w);
                    b.Circle(0, 0, .24, false, w);
                }
                break;
            case "latency":
                b.Circle(0, 0, .55, false, w);
                b.Line(w, (0, -.95), (0, -.3)); b.Line(w, (0, .3), (0, .95));
                b.Line(w, (-.95, 0), (-.3, 0)); b.Line(w, (.3, 0), (.95, 0));
                b.Circle(0, 0, .1, true);
                break;
            default:   // info
                b.Circle(0, 0, .8, false, w);
                b.Line(w * 1.2, (0, -.05), (0, .45));
                b.Circle(0, -.35, .1, true);
                break;
        }
        _ = k;
        b.Group.Freeze();
        return b.Group;
    }
}
