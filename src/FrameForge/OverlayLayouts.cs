using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Gli stili dell'overlay oltre a Lista e Barra: Grid HUD, Ribbon, Telemetry e Console (tutti,
/// a parte Telemetry, in OverlayStyles.cs; qui le parti comuni e la telemetria).
/// Lo stile è solo la disposizione: i colori vengono sempre dal tema scelto (Theme) e dai colori delle righe.
/// Ogni stile ha un solo metodo che misura (dc nullo) e disegna, così misura e disegno non possono divergere.
/// Le voci restano sempre nell'ordine scelto in "Personalizza" (componenti e sensori); con tanti sensori nessuno
/// stile cresce all'infinito: si va in colonne (o righe) in più, spezzando di preferenza tra un componente e l'altro.
/// Come per Lista e Barra, nomi, etichette, unità e cornici stanno nello strato fisso; valori, barre e curve
/// in quello che cambia (vedi Layer), e le larghezze seguono il valore più lungo visto (HeldLength).
/// </summary>
public sealed partial class OverlayView
{
    // Gli stessi numeri degli stili che questi hanno sostituito (Apex HUD, Pulse, Tiles, Terminal): le impostazioni
    // salvate restano valide e portano allo stile nuovo che ha preso il posto del vecchio.
    public const int StyleList = 0, StyleBar = 1, StyleGrid = 2, StyleConsole = 3, StyleDashboard = 4, StyleRings = 5, StyleIcons = 6;

    // ------------------------------------------------------------ pennelli comuni (creati una volta sola)

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

    private static readonly Brush Hairline = Frozen(Color.FromArgb(40, 255, 255, 255));
    private static readonly Brush TrackSolid = Frozen(Color.FromArgb(40, 255, 255, 255));
    private static readonly Pen EdgePen = FrozenPen(Frozen(Color.FromArgb(28, 255, 255, 255)), 1);
    private static readonly Pen HairPenSolid = FrozenPen(Hairline, 1);

    // Senza sfondo (opacità sotto il 35%, come per il contorno dei testi) le parti grafiche devono restare visibili
    // su qualsiasi scena, chiara o scura: colori più pieni e un bordo scuro sottile, come il contorno dei testi.
    // Con lo sfondo restano tenui.
    private static readonly Brush TrackBare = Frozen(Color.FromArgb(110, 255, 255, 255));
    private static readonly Pen HairPenBare = FrozenPen(Frozen(Color.FromArgb(120, 255, 255, 255)), 1);
    private static readonly Pen EdgeShadow = FrozenPen(Frozen(Color.FromArgb(0xB0, 0, 0, 0)), 1);
    private static readonly Dictionary<double, Pen> LineShadows = new();
    private Brush Track => _bare ? TrackBare : TrackSolid;
    private Pen HairPen => _bare ? HairPenBare : HairPenSolid;
    /// <summary>La trasparenza di un riempimento: quella normale con lo sfondo, una più piena senza.</summary>
    private byte Bare(byte normal, byte bare) => _bare ? bare : normal;

    /// <summary>Penna scura poco più larga della linea data, da passare sotto la linea quando manca lo sfondo.</summary>
    private static Pen LineShadow(double thickness)
    {
        lock (LineShadows)
        {
            if (LineShadows.TryGetValue(thickness, out var p)) return p;
            // un'ombra morbida, non un contorno: poco più larga della linea e semitrasparente
            p = new Pen(Frozen(Color.FromArgb(0x70, 0, 0, 0)), thickness + 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return LineShadows[thickness] = p;
        }
    }

    // Barretta delle percentuali (Grid HUD): binario scuro traslucido e riempimento pieno, estremi arrotondati.
    // Senza sfondo un bordo scuro di 1 px attorno al binario.
    private static readonly Brush PctTrackBrush = Frozen(Color.FromArgb(115, 0, 0, 0));
    private static readonly Pen PctEdge = FrozenPen(Frozen(Color.FromArgb(0x80, 0, 0, 0)), 1);

    private void PctTrack(DrawingContext? dc, Rect r)
    {
        if (dc == null || !Draws(false)) return;
        double rad = r.Height / 2;
        dc.DrawRoundedRectangle(_bare ? PctTrackBrush : TrackSolid, _bare ? PctEdge : null, _bare ? Inset(r, -0.5) : r, rad, rad);
    }

    private void PctFill(DrawingContext? dc, Rect r, Brush b)
    {
        if (dc == null || !Draws(true)) return;
        double rad = r.Height / 2;
        dc.DrawRoundedRectangle(b, null, r, rad, rad);
    }

    /// <summary>Una linea; senza sfondo con sotto il suo contorno scuro.</summary>
    private void Line(DrawingContext dc, Pen pen, Point a, Point b)
    {
        if (_bare) dc.DrawLine(LineShadow(pen.Thickness), a, b);
        dc.DrawLine(pen, a, b);
    }

    /// <summary>Una curva; senza sfondo con sotto il suo contorno scuro.</summary>
    private void Curve(DrawingContext dc, Pen pen, Geometry g)
    {
        if (_bare) dc.DrawGeometry(null, LineShadow(pen.Thickness), g);
        dc.DrawGeometry(null, pen, g);
    }

    private readonly Dictionary<(Brush, byte), Brush> _dimmed = new();

    /// <summary>Lo stesso colore molto trasparente: i segmenti spenti delle barre a LED.</summary>
    private Brush Dim(Brush b, byte alpha = 48)
    {
        if (_dimmed.TryGetValue((b, alpha), out var d)) return d;
        var c = b is SolidColorBrush s ? s.Color : Colors.White;
        return _dimmed[(b, alpha)] = Frozen(Color.FromArgb(alpha, c.R, c.G, c.B));
    }


    // ------------------------------------------------------------ misure comuni

    /// <summary>
    /// Altezza massima di Grid HUD, Console, Dashboard, Anelli e Icone, che restano in una colonna: tutto lo schermo meno i margini
    /// (unità WPF, prima della grandezza scelta). Solo oltre si va a una seconda colonna. ScreenH è in unità WPF
    /// come il pannello, e la grandezza scelta si divide una volta sola: finestra sul desktop e immagine per il
    /// gioco (che moltiplica per grandezza × DPI, come lo schermo in pixel) danno le stesse colonne.
    /// </summary>
    private double MaxPanelTall =>
        Math.Max(260, ScreenH - 2 * ScreenMargin) / Math.Max(0.5, _settings?.Scale ?? 1);
    private const double ScreenMargin = 40;

    // Le grandezze di base del testo, uguali in tutti gli stili (quelle della Lista): al 100% un valore è alto
    // uguale in ogni stile e la grandezza scelta le moltiplica allo stesso modo. Intestazioni e numeri grandi
    // ne derivano.
    internal const double ValueSize = 15, NameSize = 12.5, UnitSize = 10.5, HeadSize = 13;

    private static double FrameNumber(FrameSnapshot? f, string id) => f == null ? double.NaN : id switch
    {
        "fps" => f.Fps, "avg" => f.AvgFps, "low1" => f.Low1Fps, "low01" => f.Low01Fps,
        "ft" => f.FrametimeMs, "p99" => f.P99Ms, "smooth" => f.PacingPct, "jitter" => f.JitterMs,
        "limit" => double.NaN,
        _ => f.Stutters,
    };

    /// <summary>Larghezza riservata al valore: quella del valore più lungo visto, perché nulla "balli".</summary>
    private double Slot(Row row, Cell cell, double size, int min = 3) =>
        Make(new string('0', HeldLength(row, cell, min)), size, _theme.Value).Width;

    // Per la prova delle grandezze (LayoutBench): quante volte ogni grandezza di carattere è stata usata per un
    // valore e per un'intestazione nell'ultimo disegno. Fuori dalla prova restano spente.
    internal Dictionary<double, int>? ProbeValues, ProbeHeads;
    /// <summary>Per le prove: in quante colonne si è disposto l'ultimo pannello (Grid HUD, Telemetry, Console).</summary>
    internal int LastColumns;
    private static void Probe(Dictionary<double, int>? d, double size)
    {
        if (d == null) return;
        size = Math.Round(size, 2);
        d[size] = d.TryGetValue(size, out var n) ? n + 1 : 1;
    }

    /// <summary>Il valore allineato a destra nel suo spazio (strato che cambia).</summary>
    private void PutValue(DrawingContext? dc, Cell cell, double size, double slotRight, double baseline, Brush? brush = null)
    {
        if (dc == null || !Draws(true)) return;
        var t = Make(cell.Value, size, brush ?? ValueBrush(cell));
        Put(dc, t, slotRight - t.Width, baseline, true);
        if (brush == null) Probe(ProbeValues, size);
    }

    private void PutFixed(DrawingContext? dc, FormattedText t, double x, double baseline)
    {
        if (dc != null) Put(dc, t, x, baseline);
    }

    /// <summary>Quanto è "pieno" il valore, da 0 a 1, per barre e LED; NaN se non ha una scala sensata.</summary>
    private double Fraction(Cell cell)
    {
        double v = cell.Num;
        if (double.IsNaN(v)) return double.NaN;
        double f = cell.Kind switch
        {
            "Load" or "Control" or "Level" or "Humidity" or "smooth" => v / 100,
            "Temperature" => v / 100,
            "ft" or "p99" => v / 33.3,
            // memoria usata: rispetto al totale (VRAM dal registro, RAM da Windows); i totali stessi non hanno scala
            "SmallData" when cell.Key.StartsWith("/gpu", StringComparison.Ordinal) && SystemInfo.VramTotalMb is { } vt => v / vt,
            "Data" when cell.Key.StartsWith("/ram", StringComparison.Ordinal) && SystemInfo.RamTotalGb is { } rt => v / rt,
            "Data" or "SmallData" when cell.Key.StartsWith(SystemInfo.RootId, StringComparison.Ordinal) => double.NaN,
            "MemSpeed" or "Session" or "Api" or "Driver" => double.NaN,
            "fps" or "avg" or "low1" or "low01" => v / Math.Max(60, SessionMax(cell) * 1.1),
            _ => SessionMax(cell) is var m && m > 0 ? v / m : double.NaN,
        };
        return double.IsNaN(f) ? f : Math.Clamp(f, 0, 1);
    }

    // Maiuscolo / minuscolo tenuti da parte: i nomi sono sempre gli stessi, niente stringhe nuove a ogni disegno.
    private readonly Dictionary<string, string> _upper = new(), _lower = new();
    private string Caps(string s) => _upper.TryGetValue(s, out var u) ? u : _upper[s] = s.ToUpperInvariant();
    private string Lower(string s) => _lower.TryGetValue(s, out var u) ? u : _lower[s] = s.ToLowerInvariant();

    private readonly Dictionary<(string, bool, int), string> _headers = new();

    /// <summary>Il nome del componente per un'intestazione (style 0: maiuscolo, 1: "[ gpu ]"), con "(segue)" se continua.</summary>
    private string HeaderText(Row r, bool cont, bool console)
    {
        var key = (r.Label, cont, console ? 1 : 0);
        if (_headers.TryGetValue(key, out var s)) return s;
        s = console ? "[ " + r.Label.ToLowerInvariant() + (cont ? " +" : "") + " ]"
                    : r.Label.ToUpperInvariant() + (cont ? " " + Loc.T("Overlay.Cont") : "");
        return _headers[key] = s;
    }

    // Le righe divise per ruolo: misure dei frame, gioco/API, registrazione e componenti hardware.
    // (con nomi brevi e serie riassunte: vedi OverlayCompact)
    private IEnumerable<Row> TitleRows => Shown.Where(r => r.Tag is "API" or "REC");

    // ------------------------------------------------------------ sfondo

    private void DrawPanel(DrawingContext dc, int layout, Brush bg, Size size)
    {
        var r = new Rect(size);
        switch (layout)
        {
            case StyleConsole:
                dc.DrawRoundedRectangle(bg, FrozenOutline(Dim(_theme.Label("GPU"), 120)), Inset(r, 0.75), 4, 4);
                break;
            case StyleGrid:
                dc.DrawRoundedRectangle(bg, EdgePen, Inset(r), 8, 8);
                break;
            case StyleDashboard:
            case StyleRings:
            case StyleIcons:
                // i riquadri hanno già fondo e bordo propri: il pannello è solo lo sfondo scelto
                dc.DrawRoundedRectangle(bg, null, r, 10, 10);
                break;
            default:
                double radius = layout == StyleBar ? 8 : 10;
                dc.DrawRoundedRectangle(bg, null, r, radius, radius);
                break;
        }
    }

    private static Rect Inset(Rect r, double d = 0.5) => new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    private readonly Dictionary<Brush, Pen> _outlines = new();
    private readonly Dictionary<(Brush, bool), Pen> _headerPens = new();

    /// <summary>Riga sottile, mezza trasparente, nel colore del componente (intestazioni della telemetria).</summary>
    private Pen HeaderPen(Brush b)
    {
        if (_headerPens.TryGetValue((b, _bare), out var p)) return p;
        var c = b is SolidColorBrush s ? s.Color : Colors.White;
        return _headerPens[(b, _bare)] = FrozenPen(Frozen(Color.FromArgb(Bare(110, 153), c.R, c.G, c.B)), 1);
    }
    private Pen FrozenOutline(Brush b)
    {
        if (_outlines.TryGetValue(b, out var p)) return p;
        return _outlines[b] = FrozenPen(b, 1.5);
    }

    private void Fill(DrawingContext? dc, bool changing, Brush b, Rect r, double radius = 0)
    {
        if (dc == null || !Draws(changing) || r.Width <= 0 || r.Height <= 0) return;
        var edge = _bare ? EdgeShadow : null;   // senza sfondo un bordo scuro sottile, come il contorno dei testi
        if (radius > 0) dc.DrawRoundedRectangle(b, edge, r, radius, radius); else dc.DrawRectangle(b, edge, r);
    }

    /// <summary>Titolo con il gioco e la sua API (e la registrazione, se in corso), su una riga.</summary>
    private double TitleLine(DrawingContext? dc, double x, double y, double maxW)
    {
        // Senza sfondo il titolo sta sopra il gioco senza riquadro: il contorno dei valori non basta sulle scene chiare
        // (testo piccolo e colore attenuato dell'API). Sotto ci va una targhetta scura, della misura della riga.
        if (dc != null && _bare && Draws(false))
        {
            double w = TitleLine(null, 0, 0, maxW, out double th);
            if (w > 0) dc.DrawRoundedRectangle(TitlePlate, null, new Rect(x - 6, y - 1, w + 12, th + 2), 4, 4);
        }
        TitleLine(dc, x, y, maxW, out double h);
        return h;
    }
    private static readonly Brush TitlePlate = MakeTitlePlate();

    private static Brush MakeTitlePlate()
    {
        var b = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
        b.Freeze();
        return b;
    }

    /// <summary>Disegna (se dc non è null) e misura la riga del titolo: restituisce la larghezza, in h l'altezza.</summary>
    private double TitleLine(DrawingContext? dc, double x, double y, double maxW, out double hOut)
    {
        double cx = x, h = 0, right = x;
        foreach (var row in TitleRows)
        {
            var label = MakeTrimmed(row.Label, 12 * row.Scale, LabelBrush(row), Math.Max(60, maxW * 0.6));
            double b = y + 13 * row.Scale;
            PutFixed(dc, label, cx, b);
            right = cx + label.Width;
            cx += label.Width + 6;
            if (row.Cells.Length > 0)
            {
                var c = row.Cells[0];
                double slot = Slot(row, c, 11.5, 3);
                PutValue(dc, c, 11.5, cx + slot, b, row.Tag == "REC" ? RecBrush : _theme.Unit);
                right = cx + slot;
                cx += slot + 14;
            }
            h = Math.Max(h, 18 * row.Scale);
        }
        hOut = h;
        return right - x;
    }

    // ------------------------------------------------------------ massimi della sessione

    // Il valore più alto visto finora per ogni voce (id del sensore o della misura): fa da fondo scala per
    // frequenze, consumi, ventole e FPS (barre e anelli). Un numero per voce: nessuna allocazione a regime.
    private readonly Dictionary<string, double> _sessionMax = new();
    private HardwareSnapshot? _sampledHardware;

    /// <summary>Aggiorna i massimi a ogni nuova lettura dei sensori (una al secondo) e a ogni dato dei frame.</summary>
    private void SampleMax(OverlayData data)
    {
        var s = _settings;
        if (s == null) return;
        if (!ReferenceEquals(data.Hardware, _sampledHardware))
        {
            _sampledHardware = data.Hardware;
            foreach (var item in s.Sensors)
                if (data.Hardware.ById.TryGetValue(item.Id, out var r) && r.Value is { } v) NoteMax(item.Id, v);
        }
        if (data.Frames is { } f)
            foreach (var item in s.FrameItems)
                if (item.Hidden != true && FrameNumber(f, item.Id) is var n && !double.IsNaN(n)) NoteMax(item.Id, n);
    }

    private void NoteMax(string key, double v)
    {
        if (!_sessionMax.TryGetValue(key, out var m) || v > m) _sessionMax[key] = v;
    }

    private double SessionMax(Cell c) => c.Key.Length > 0 && _sessionMax.TryGetValue(c.Key, out var m) ? m : 0;
}
