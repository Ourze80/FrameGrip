using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <param name="Recording">Tempo trascorso della registrazione benchmark in corso, oppure null.</param>
public sealed record OverlayData(HardwareSnapshot Hardware, FrameSnapshot? Frames, string? Recording = null, string? Api = null, string? Game = null)
{
    /// <summary>Il riquadro della misura del limite ideale (conto alla rovescia, misura in corso, fine), oppure null.</summary>
    public string? Banner { get; init; }
    /// <summary>0 = conto alla rovescia (blu), 1 = misura in corso (rosso), 2 = misura finita (verde).</summary>
    public int BannerKind { get; init; }
    /// <summary>Overlay spento: si disegna solo il riquadro in cima (registrazione, misura), poi sparisce di nuovo.</summary>
    public bool BannerOnly { get; init; }
    /// <summary>Il limite di FPS in uso (null = limitatore spento) e se viene dal profilo del gioco.</summary>
    public int? Limit { get; init; }
    public bool LimitProfile { get; init; }
    public static readonly OverlayData Empty = new(HardwareSnapshot.Empty, null);
}

/// <summary>
/// Disegna l'overlay in uno dei due stili (lista, barra).
/// Usato nella finestra sopra il gioco, nell'anteprima e per l'immagine passata al gioco.
/// Ogni riga può avere etichetta, colore e grandezza propri.
/// </summary>
public sealed partial class OverlayView : FrameworkElement
{
    // Kind: tipo del sensore (es. "Load") o misura dei frame (es. "fps"); Num: il valore come numero (NaN se manca);
    // Key: identificativo stabile (id del sensore o della misura), per la cronologia dei valori.
    private readonly record struct Cell(string Name, string Value, string Unit, string LongUnit, Brush? Color = null, double Scale = 1,
        string Kind = "", double Num = double.NaN, string Key = "", bool Custom = false);
    // Grid: negli stili compatti, le voci "una per core" disegnate come una griglia di barrette (vedi OverlayCompact).
    private readonly record struct Row(string Label, string Tag, Brush? LabelColor, double Scale, double CellScale, Cell[] Cells, Cell[]? Grid = null);

    private const double Pad = 12, GraphH = 58, GraphGap = 8;
    // classico (misure a grandezza 100%)
    private const double MinLabelW = 54, CellW = 86, RowH = 25;
    // lista
    private const double ListNameW = 170, ListValueW = 104, ListRowH = 20, MinListLabelW = 46, ListIndent = 12, ListGroupGap = 4;
    // barra
    private const double BarH = 36, BarPadX = 14, BarGraphW = 150;

    private static readonly Typeface[] Faces =
    {
        Face("Cascadia Mono, Consolas, Courier New"),
        Face("Segoe UI"),
        Face("Bahnschrift, Segoe UI"),
    };

    private Settings? _settings;
    private OverlayData _data = OverlayData.Empty;
    private List<Row> _rows = new();

    // valori validi durante un passaggio di misura/disegno
    private Theme _theme = Theme.All[0];
    private Typeface _face = Faces[0];
    private bool _shadow, _bare;   // _bare: senza sfondo anche le parti grafiche si rinforzano (vedi OverlayLayouts)
    private double _ppd = 1;
    private double _labelW = MinLabelW, _listLabelW = MinListLabelW, _maxScale = 1, _listW = 200;

    public Settings? Settings
    {
        get => _settings;
        set { _settings = value; _heldLength.Clear(); Rebuild(); }
    }

    public void Update(OverlayData data)
    {
        _data = data;
        RotateTextCache();
        SampleMax(data);
        StepGraphScale();
        // Se non cambia nulla di ciò che si vede (i sensori cambiano una volta al secondo, senza gioco il
        // grafico è fermo) non si ridisegna: misurare e disegnare i testi è la parte cara.
        var rows = BuildRows();
        bool graphMoves = !OmitGraphLine && _settings?.ShowGraph == true && data.Frames?.Graph is { Length: >= 2 };
        bool waiting = data.Frames?.Graph is not { Length: >= 2 };
        if (!graphMoves && SameRows(rows, _rows) && (_settings?.ShowGraph != true || waiting == _drawnWaiting)) return;
        _drawnWaiting = waiting;
        _rows = rows;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private bool _drawnWaiting = true;

    private static bool SameRows(List<Row> a, List<Row> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            Row x = a[i], y = b[i];
            if (x.Label != y.Label || x.Tag != y.Tag || !ReferenceEquals(x.LabelColor, y.LabelColor) ||
                x.Scale != y.Scale || x.CellScale != y.CellScale || x.Cells.Length != y.Cells.Length) return false;
            for (int j = 0; j < x.Cells.Length; j++)
                if (!x.Cells[j].Equals(y.Cells[j])) return false;
        }
        return true;
    }

    // Scala verticale del grafico: vedi GraphScale. Qui avanza di un passo per aggiornamento dei dati
    // (10 al secondo), mai a ogni disegno.
    private double _graphTop;

    private double TargetScale(out double avg, out double max)
    {
        var f = _data.Frames;
        if (f == null) { avg = max = 0; return 0; }
        GraphScale.Range(f.Graph, f.GraphX, f.Graph.Length, out avg, out max);
        return GraphScale.Target(avg, max);
    }

    private void StepGraphScale() => _graphTop = GraphScale.Step(_graphTop, TargetScale(out _, out _), 100);

    /// <summary>
    /// Per l'immagine passata al gioco: il riquadro del grafico si disegna senza linee (cornice ed
    /// etichetta sì), perché la linea la disegna GraphRaster 60 volte al secondo.
    /// </summary>
    public bool OmitGraphLine { get; set; }
    /// <summary>Dove è finito il riquadro del grafico nell'ultimo disegno (unità WPF); vuoto se non c'è.</summary>
    public Rect LastGraphRect { get; private set; } = Rect.Empty;
    /// <summary>Colori della linea e della griglia del tema attuale, con lo spessore della linea (unità WPF).</summary>
    public (Color Line, double LineWidth, Color Grid, double GridWidth) GraphInk
    {
        get
        {
            var t = Theme.Get(_settings?.Theme ?? 0);
            return (((SolidColorBrush)t.GraphPen.Brush).Color, t.GraphPen.Thickness, ((SolidColorBrush)t.GridPen.Brush).Color, t.GridPen.Thickness);
        }
    }

    /// <summary>Quale parte disegnare: tutto, solo quella fissa (sfondo, etichette, nomi, unità, cornici) o solo
    /// quella che cambia a ogni aggiornamento (valori). L'immagine per il gioco ridisegna la parte fissa solo
    /// quando cambia: preparare e disegnare i testi è la parte cara (decine di millisecondi in tutto).</summary>
    public enum Layer { All, Fixed, Changing }
    public Layer RenderLayer { get; set; }

    private bool Draws(bool changing) =>
        RenderLayer == Layer.All || (RenderLayer == Layer.Changing) == changing;

    /// <summary>Riassunto di tutto ciò che sta nella parte fissa: se non cambia, la si può riusare.</summary>
    public string FixedSignature
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(_settings?.Theme).Append('|').Append(_settings?.Font).Append('|').Append(_settings?.Opacity)
              .Append('|').Append(_settings?.Layout).Append('|').Append(_settings?.ShowGraph).Append('|').Append(RenderSize);
            foreach (var row in _rows)
            {
                sb.Append('\n').Append(row.Label).Append('|').Append(row.Tag).Append('|').Append(row.Scale).Append('|')
                  .Append(row.CellScale).Append('|').Append(LabelBrush(row));
                foreach (var cell in row.Cells)
                    sb.Append('\t').Append(cell.Name).Append('|').Append(cell.Unit).Append('|').Append(cell.LongUnit).Append('|')
                      .Append(cell.Scale).Append('|').Append(HeldLength(row, cell, 1));
            }
            if (_settings?.Layout >= StyleGrid) AppendShownSignature(sb);   // voci riassunte degli stili compatti
            return sb.ToString();
        }
    }

    /// <summary>Riassunto di ciò che sta nella parte che cambia (valori, scritta "max", attesa dei dati):
    /// se è uguale all'ultima volta, l'immagine non va ridisegnata.</summary>
    public string ChangingSignature()
    {
        var sb = new System.Text.StringBuilder(256);
        foreach (var row in _rows)
            foreach (var cell in row.Cells)
                sb.Append(cell.Value).Append('\t');
        var g = _data.Frames?.Graph;
        if (_settings?.ShowGraph != true) sb.Append('-');
        else if (g == null || g.Length < 2) sb.Append('w');
        else
        {
            TargetScale(out _, out double max);
            sb.Append(max.ToString("0.0", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // Larghezze che non devono "ballare" quando un valore passa, per esempio, da 99 a 100: per ogni voce si
    // ricorda il numero di caratteri più lungo visto, finché non cambiano le impostazioni.
    private readonly Dictionary<string, int> _heldLength = new();

    private int HeldLength(Row row, Cell cell, int min)
    {
        string key = row.Tag + "|" + cell.Name;
        int len = Math.Max(cell.Value.Length, min);
        if (_heldLength.TryGetValue(key, out int held) && held > len) return held;
        _heldLength[key] = len;
        return len;
    }

    public void Refresh()
    {
        _heldLength.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        _rows = BuildRows();
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ------------------------------------------------------------ contenuto

    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        var s = _settings;
        if (s == null) return rows;

        if (_data.Recording != null)
            rows.Add(new Row(Loc.T("Overlay.Rec"), "REC", RecBrush, 1.0, 1.0, new[] { new Cell(Loc.T("Overlay.RecTime"), _data.Recording, "", "", RecBrush) }));
        // Il riquadro della misura usa la stessa riga in cima della registrazione: si vede in ogni stile.
        if (_data.Banner != null)
        {
            var bb = _data.BannerKind switch { 1 => RecBrush, 2 => GoodBrush, _ => AccentBrush };
            rows.Add(new Row(_data.Banner, "REC", bb, 1.0, 1.0, new[] { new Cell("", "", "", "", bb) }));
        }
        if (_data.BannerOnly) return rows;

        // In cima: il nome del gioco riconosciuto fa da titolo, sotto la sua API grafica.
        // Se l'utente ha scritto un'etichetta sua nel riquadro "Personalizza", vale quella.
        if (s.ShowApi)
        {
            var style = s.StyleOf("frame:API");
            rows.Add(new Row(Custom(style?.Label) ?? _data.Game ?? Loc.T("Overlay.NoGame"), "API", ParseBrush(style?.Color), ScaleOf(style), ScaleOf(style),
                new[] { new Cell("API", _data.Api ?? "—", "", "") }));
        }

        // Sensori scelti dall'utente, una riga per componente hardware.
        foreach (var g in OverlayModel.Groups(s, _data.Hardware))
        {
            var style = s.StyleOf(g.Key);
            var cells = new Cell[g.Sensors.Count];
            for (int i = 0; i < cells.Length; i++)
            {
                var (item, r) = g.Sensors[i];
                var (value, unit) = SensorFormat.Format(r);
                cells[i] = new Cell(Custom(item.Name) ?? r.Name, value, unit, unit, ParseBrush(item.Color),
                    Math.Clamp(item.Size ?? 1.0, 0.5, 3.0), r.Type, r.Value is { } v ? v : double.NaN, item.Id, Custom(item.Name) != null);
            }
            rows.Add(new Row(Custom(style?.Label) ?? g.DefaultLabel, g.Tag, ParseBrush(style?.Color), ScaleOf(style), 1.0, cells));   // la grandezza della riga hardware vale solo per la sua etichetta
        }

        // Tutte le misure dei frame stanno sotto un'unica riga FPS e si personalizzano come i sensori:
        // nome, colore, grandezza e ordine di ciascuna.
        var f = _data.Frames;
        var frameCells = new List<Cell>();
        foreach (var item in OverlayModel.VisibleFrameItems(s))
        {
            var (value, shortUnit, unit) = item.Id switch
            {
                "fps" => (F0(f?.Fps), "fps", "fps"),
                "avg" => (F0(f?.AvgFps), "avg", "fps"),
                "low1" => (F0(f?.Low1Fps), "1%", "fps"),
                "low01" => (F0(f?.Low01Fps), "0.1%", "fps"),
                "ft" => (F1(f?.FrametimeMs), "ms", "ms"),
                "p99" => (F1(f?.P99Ms), "p99", "ms"),
                "smooth" => (F0(f?.PacingPct), "%", "%"),
                "jitter" => (F2(f?.JitterMs), "±ms", "ms"),
                "limit" => (_data.Limit is { } lim ? lim.ToString(CultureInfo.InvariantCulture) : "—", "fps", "fps"),
                _ => (f == null ? "—" : f.Stutters.ToString(CultureInfo.InvariantCulture), "stut", ""),
            };
            string cellName = Custom(item.Name) ?? OverlayModel.FrameName(item.Id);
            if (item.Id == "limit" && _data.Limit != null) cellName += " · " + Loc.T(_data.LimitProfile ? "Overlay.LimitProfile" : "Overlay.LimitGlobal");
            frameCells.Add(new Cell(cellName, value, shortUnit, unit,
                ParseBrush(item.Color), Math.Clamp(item.Size ?? 1.0, 0.5, 3.0), item.Id, FrameNumber(f, item.Id), item.Id, Custom(item.Name) != null));
        }
        if (frameCells.Count > 0)
        {
            var style = s.StyleOf("frame:FPS");
            rows.Add(new Row(Custom(style?.Label) ?? "FPS", "FPS", ParseBrush(style?.Color), ScaleOf(style), 1.0, frameCells.ToArray()));
        }
        return rows;
    }

    private static readonly Brush RecBrush = MakeRecBrush();
    private static readonly Brush GoodBrush = Frozen(0x43, 0xE0, 0x6A), AccentBrush = Frozen(0x4C, 0x9B, 0xFF);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static Brush MakeRecBrush()
    {
        var b = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x45));
        b.Freeze();
        return b;
    }

    private static double ScaleOf(GroupStyle? style) => Math.Clamp(style?.Size ?? 1.0, 0.5, 3.0);

    private static string? Custom(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static readonly Dictionary<string, Brush?> BrushCache = new();

    /// <summary>Colore scelto dall'utente ("#RRGGBB"); null se assente o non valido.</summary>
    public static Brush? ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        lock (BrushCache) return ParseBrushLocked(hex);   // usata anche dal thread che disegna l'overlay del gioco
    }

    private static Brush? ParseBrushLocked(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached)) return cached;
        Brush? brush = null;
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            brush = b;
        }
        catch { /* testo che non è un colore */ }
        return BrushCache[hex] = brush;
    }

    // ------------------------------------------------------------ misura

    private void Prepare(Settings s)
    {
        _theme = Theme.Get(s.Theme);
        // Lo stile Console è sempre a caratteri di larghezza fissa.
        _face = s.Layout == StyleConsole ? Faces[0] : Faces[Math.Clamp(s.Font, 0, Faces.Length - 1)];
        _shadow = s.Opacity < 0.35;
        _bare = _shadow;   // senza sfondo anche barre, linee e puntini si rinforzano (vedi OverlayLayouts)
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // La colonna delle etichette si adatta all'etichetta più lunga (es. il nome del processore).
        _labelW = MinLabelW;
        _listLabelW = MinListLabelW;
        _maxScale = 1;
        _listW = 200;
        foreach (var row in _rows)
        {
            _labelW = Math.Max(_labelW, Make(row.Label, 13 * row.Scale, _theme.Value).Width + 14);
            _listLabelW = Math.Max(_listLabelW, Make(row.Label, 13 * row.Scale, _theme.Value).Width + 14);
            _maxScale = Math.Max(_maxScale, row.Scale * 0.8);
            foreach (var cell in row.Cells)
            {
                double c = row.CellScale * cell.Scale;
                _maxScale = Math.Max(_maxScale, c);
                // larghezza reale della riga in stile Lista: il valore resta vicino al suo nome
                double valueW = Make(new string('0', HeldLength(row, cell, 4)), ValueSize * c, _theme.Value).Width;
                double line = ListIndent + Make(cell.Name, NameSize * c, _theme.Unit).Width + 20 + valueW + 3 + Make(cell.LongUnit, UnitSize * c, _theme.Unit).Width;
                _listW = Math.Max(_listW, Math.Min(line, 520));
            }
        }
    }

    private Brush LabelBrush(Row row) => row.LabelColor ?? _theme.Label(row.Tag);
    private Brush ValueBrush(Cell cell) => cell.Color ?? _theme.Value;

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = _settings;
        if (s == null) return default;
        bool graph = s.ShowGraph && !_data.BannerOnly;
        if (_rows.Count == 0 && !graph) return default;
        Prepare(s);

        switch (LayoutOf(s))
        {
            default:
            {
                // Intestazione con il nome del componente, poi un valore per riga sotto di essa.
                double height = 0;
                foreach (var r in _rows)
                {
                    height += ListRowH * r.Scale + ListGroupGap;
                    foreach (var cell in r.Cells) height += ListRowH * r.CellScale * cell.Scale;
                }
                double width = Pad * 2 + Math.Max(_listLabelW, _listW);
                return new Size(width, Pad * 2 + height + GraphExtra(graph, height));
            }
            case 1:
                return new Size(LayoutBar(null, graph), BarH * _maxScale);
            case StyleGrid: return LayoutGrid(null, graph);
            case StyleDashboard: return LayoutDashboard(null, graph);
            case StyleRings: return LayoutRings(null, graph);
            case StyleIcons: return LayoutIcons(null, graph);
            case StyleConsole: return LayoutConsole(null, graph);
        }
    }

    /// <summary>Solo il riquadro in cima: sempre nello stile a lista, il più piccolo.</summary>
    private int LayoutOf(Settings s) => _data.BannerOnly ? 0 : s.Layout;

    private static double GraphExtra(bool graph, double rowsHeight) =>
        graph ? GraphH + (rowsHeight > 0 ? GraphGap : 0) : 0;

    // ------------------------------------------------------------ disegno

    protected override void OnRender(DrawingContext dc)
    {
        var s = _settings;
        var size = RenderSize;
        LastGraphRect = Rect.Empty;
        if (s == null || size.Width < 1 || size.Height < 1) return;
        Prepare(s);

        var c = _theme.BgColor;
        var bg = new SolidColorBrush(Color.FromArgb((byte)(s.Opacity * 255), c.R, c.G, c.B));
        if (Draws(false)) DrawPanel(dc, LayoutOf(s), bg, size);

        bool graph = s.ShowGraph && !_data.BannerOnly;
        switch (LayoutOf(s))
        {
            default: RenderList(dc, size, graph); break;
            case 1: LayoutBar(dc, graph); break;
            case StyleGrid: LayoutGrid(dc, graph); break;
            case StyleDashboard: LayoutDashboard(dc, graph); break;
            case StyleRings: LayoutRings(dc, graph); break;
            case StyleIcons: LayoutIcons(dc, graph); break;
            case StyleConsole: LayoutConsole(dc, graph); break;
        }
    }

    private void RenderList(DrawingContext dc, Size size, bool graph)
    {
        double y = Pad;
        foreach (var row in _rows)
        {
            // Intestazione: il nome del componente da solo sulla sua riga.
            double k = row.Scale;
            Put(dc, Make(row.Label, HeadSize * k, LabelBrush(row)), Pad, Baseline(y, ListRowH * k, HeadSize * k));
            Probe(ProbeHeads, HeadSize * k);
            y += ListRowH * k;

            // Sotto, un valore per riga.
            foreach (var cell in row.Cells)
            {
                double c = row.CellScale * cell.Scale;
                double baseline = Baseline(y, ListRowH * c, ValueSize * c);

                var unit = Make(cell.LongUnit, UnitSize * c, _theme.Unit);
                double right = size.Width - Pad;
                Put(dc, unit, right - unit.Width, baseline);
                double valueRight = right - unit.Width - (unit.Width > 0 ? 3 : 0);
                if (Draws(true))
                {
                    var value = Make(cell.Value, ValueSize * c, ValueBrush(cell));
                    Put(dc, value, valueRight - value.Width, baseline, true);
                    Probe(ProbeValues, ValueSize * c);
                }

                // Il nome si accorcia in base allo spazio del valore più lungo visto (non di quello attuale):
                // così non cambia a ogni aggiornamento e sta nello strato fisso dell'immagine.
                double slotW = Make(new string('0', HeldLength(row, cell, 4)), ValueSize * c, _theme.Value).Width;
                if (Draws(false))
                {
                    var name = MakeTrimmed(cell.Name, NameSize * c, _theme.Unit, Math.Max(20, valueRight - slotW - 8 - (Pad + ListIndent)));
                    Put(dc, name, Pad + ListIndent, baseline);
                }

                y += ListRowH * c;
            }
            y += ListGroupGap;
        }
        if (graph)
        {
            if (y > Pad) y += GraphGap;
            DrawGraph(dc, new Rect(Pad, y, size.Width - Pad * 2, GraphH), false);
        }
    }

    /// <summary>Stile barra: tutto su una riga. Con dc nullo misura soltanto e restituisce la larghezza.</summary>
    private double LayoutBar(DrawingContext? dc, bool graph)
    {
        double barH = BarH * _maxScale;
        double baseline = Baseline(0, barH, ValueSize * _maxScale);
        double x = BarPadX;
        foreach (var row in _rows)
        {
            double k = row.Scale;
            var label = Make(row.Label, NameSize * k, LabelBrush(row));
            if (dc != null) { Put(dc, label, x, baseline); Probe(ProbeHeads, NameSize * k); }
            x += label.Width + 7;

            foreach (var cell in row.Cells)
            {
                // larghezza fissa per numero di cifre: la barra non "balla" quando cambiano i valori
                double c = row.CellScale * cell.Scale;
                double slot = Make(new string('0', HeldLength(row, cell, 2)), ValueSize * c, _theme.Value).Width;
                var value = Make(cell.Value, ValueSize * c, ValueBrush(cell));
                var unit = Make(cell.Unit, UnitSize * c, _theme.Unit);
                if (dc != null)
                {
                    Probe(ProbeValues, ValueSize * c);
                    Put(dc, value, x + slot - value.Width, baseline, true);
                    Put(dc, unit, x + slot + 2, baseline);
                }
                x += slot + 2 + unit.Width + 10;
            }
            x += 10;
        }
        if (_rows.Count > 0) x -= 20;

        if (graph)
        {
            if (_rows.Count > 0) x += 14;
            if (dc != null) DrawGraph(dc, new Rect(x, 6, BarGraphW, barH - 12), true);
            x += BarGraphW;
        }
        return x + BarPadX;
    }

    private void DrawGraph(DrawingContext dc, Rect r, bool compact)
    {
        if (Draws(false))
        {
            if (_bare) dc.DrawRectangle(null, LineShadow(_theme.GridPen.Thickness), r);   // il riquadro resta visibile senza sfondo
            dc.DrawRectangle(null, _theme.GridPen, r);
        }
        LastGraphRect = r;
        var g = _data.Frames?.Graph;
        if (g == null || g.Length < 2)
        {
            var msg = Make(compact ? "frametime" : Loc.T("Overlay.Waiting"), compact ? 10 : 11, _theme.Unit);
            if (Draws(true)) dc.DrawText(msg, new Point(r.X + (r.Width - msg.Width) / 2, r.Y + (r.Height - msg.Height) / 2));
            return;
        }

        // Scala: quella smussata da StepGraphScale; se manca (disegno prima del primo dato) si usa quella voluta.
        double want = TargetScale(out double avg, out double max);
        double top = _graphTop > 0 ? _graphTop : Math.Max(want, 1);

        if (OmitGraphLine)
        {
            DrawMaxLabel(dc, r, compact, max);
            return;
        }

        double avgY = r.Bottom - r.Height * Math.Min(avg / top, 1.0);
        Line(dc, _theme.GridPen, new Point(r.X, avgY), new Point(r.Right, avgY));

        // Ogni frame sta dove cade nel tempo (GraphX): il grafico scorre di una quantità costante a ogni
        // aggiornamento. I due frame a cavallo dei bordi fanno arrivare la linea ai lati; il ritaglio li pareggia.
        var xs = _data.Frames!.GraphX;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < g.Length; i++)
            {
                double py = r.Bottom - r.Height * Math.Min(g[i] / top, 1.0);
                var p = new Point(r.X + r.Width * xs[i], py);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.PushClip(new RectangleGeometry(r));
        Curve(dc, _theme.GraphPen, geo);
        dc.Pop();
        DrawMaxLabel(dc, r, compact, max);
    }

    private void DrawMaxLabel(DrawingContext dc, Rect r, bool compact, double max)
    {
        if (compact || !Draws(true)) return;
        // Sempre tutta dentro il riquadro, allineata a destra: se "max 16.0 ms" non ci sta resta "16.0 ms",
        // se non ci sta nemmeno quella non si scrive. Dietro, un fondo scuro perché la curva non la attraversi.
        const double Pad = 4;
        string num = max.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        var maxText = Make("max " + num, 10, _theme.Unit);
        if (maxText.Width + 2 * Pad > r.Width) maxText = Make(num, 10, _theme.Unit);
        if (maxText.Width + 2 * Pad > r.Width || maxText.Height + 2 > r.Height) return;
        var at = new Point(r.Right - maxText.Width - Pad, r.Y + 2);
        dc.DrawRectangle(LabelBack(), null, new Rect(at.X - 3, at.Y, maxText.Width + 6, maxText.Height));
        dc.DrawText(maxText, at);
    }

    private Brush? _labelBack;
    private Color _labelBackOf;
    /// <summary>Il fondo dell'etichetta del grafico: il colore di fondo del tema, quasi pieno (tenuto da parte).</summary>
    private Brush LabelBack()
    {
        var c = _theme.BgColor;
        if (_labelBack == null || _labelBackOf != c)
        {
            var b = new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B));
            b.Freeze();
            _labelBack = b; _labelBackOf = c;
        }
        return _labelBack;
    }

    // ------------------------------------------------------------ testo

    // Preparare un testo (FormattedText) è la parte più cara del disegno: con ~30 voci se ne creavano
    // centinaia per aggiornamento (~10 ms). Etichette, unità e misure non cambiano quasi mai: si tengono
    // quelli usati nell'aggiornamento attuale e nel precedente (due "generazioni"), gli altri si buttano.
    // Anche i valori si ripetono (FPS, percentuali, temperature): si tengono tutti finché non sono troppi.
    // Lo stesso vale per il contorno scuro dei testi senza sfondo (BuildGeometry è la parte più cara di tutte).
    private readonly record struct TextKey(string Text, double Size, Brush Brush, Typeface Face, double Ppd, double MaxWidth = 0);
    private readonly Dictionary<TextKey, FormattedText> _texts = new();
    private readonly Dictionary<FormattedText, Geometry> _shadows = new(ReferenceEqualityComparer.Instance);
    private const int MaxCachedTexts = 1500;

    private void RotateTextCache()
    {
        if (_texts.Count <= MaxCachedTexts) return;
        _texts.Clear();
        _shadows.Clear();
    }

    private FormattedText Make(string text, double size, Brush brush)
    {
        var key = new TextKey(text, size, brush, _face, _ppd);
        if (_texts.TryGetValue(key, out var t)) return t;
        return _texts[key] = MakeFresh(text, size, brush);
    }

    /// <summary>Un testo su una riga sola, accorciato con "…" oltre maxWidth.</summary>
    private FormattedText MakeTrimmed(string text, double size, Brush brush, double maxWidth)
    {
        var key = new TextKey(text, size, brush, _face, _ppd, maxWidth);
        if (_texts.TryGetValue(key, out var t)) return t;
        t = MakeFresh(text, size, brush);
        t.MaxTextWidth = maxWidth;
        t.MaxLineCount = 1;
        t.Trimming = TextTrimming.CharacterEllipsis;
        return _texts[key] = t;
    }

    /// <summary>Un testo nuovo, non condiviso: per quelli che si modificano (es. larghezza massima).</summary>
    private FormattedText MakeFresh(string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, size, brush, _ppd);

    /// <summary>Linea di base comune a tutti i testi di una riga alta rowH, tarata sul corpo più grande.</summary>
    private double Baseline(double y, double rowH, double mainSize)
    {
        var probe = Make("0", mainSize, _theme.Value);
        return y + (rowH - probe.Height) / 2 + probe.Baseline;
    }

    private void Put(DrawingContext dc, FormattedText text, double x, double baseline, bool changing = false)
    {
        if (!Draws(changing)) return;
        double y = baseline - text.Baseline;
        if (_shadow)
        {
            // senza sfondo il testo deve restare leggibile su qualsiasi scena di gioco
            if (!_shadows.TryGetValue(text, out var outline))
            {
                outline = text.BuildGeometry(new Point(0, 0));
                outline.Freeze();
                _shadows[text] = outline;
            }
            dc.PushTransform(new TranslateTransform(x, y));
            dc.DrawGeometry(null, ShadowPen, outline);
            dc.Pop();
        }
        dc.DrawText(text, new Point(x, y));
    }

    private static readonly Pen ShadowPen = MakeShadowPen();

    private static Pen MakeShadowPen()
    {
        var b = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0));
        b.Freeze();
        var p = new Pen(b, 2.6) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static Typeface Face(string family) =>
        new(new FontFamily(family), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static string F0(double? v) => v is { } d ? d.ToString("0", CultureInfo.InvariantCulture) : "—";
    private static string F1(double? v) => v is { } d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    private static string F2(double? v) => v is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "—";
}
