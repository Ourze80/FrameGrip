using System.Windows;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Gli stili a riquadri: Dashboard (un riquadro per sensore, due per riga), Anelli (ogni valore in un anello che si
/// riempie rispetto al suo fondo scala) e Icone (una striscia fitta con l'icona al posto del nome). Regole comuni:
/// - un blocco per componente, nell'ordine di "Personalizza" (le prestazioni sono un blocco come gli altri, con il
///   numero grande e il grafico); se il grafico è acceso ma le prestazioni no, il grafico è un blocco a sé in fondo;
/// - una colonna che cresce verso il basso fino all'altezza utile dello schermo, poi una seconda (tra un blocco e
///   l'altro);
/// - ogni riquadro ha un fondo scuro semitrasparente e un bordo propri: si legge anche con lo sfondo allo 0%;
/// - testi con le grandezze di base della Lista, moltiplicate dalla grandezza della riga (A−/A+), titoli compresi.
/// </summary>
public sealed partial class OverlayView
{
    // ------------------------------------------------------------ colori e pennelli dei riquadri

    private static readonly Brush TileFill = Frozen(Color.FromArgb(208, 0x0B, 0x0E, 0x13));
    private static readonly Pen TileEdge = FrozenPen(Frozen(Color.FromArgb(235, 84, 86, 90)), 1);
    private static readonly Brush TileFaint = Frozen(Color.FromArgb(48, 255, 255, 255));
    private static readonly Pen TileFaintPen = FrozenPen(TileFaint, 1);
    private static readonly Brush TileTrack = Frozen(Color.FromArgb(52, 255, 255, 255));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly Brush HotBrush = Frozen(Color.FromRgb(0xFF, 0x4D, 0x4D));

    private struct PanelBlock { public int Index, Col; public double Y, H; }
    private readonly List<PanelBlock> _panelBlocks = new();

    private Size LayoutDashboard(DrawingContext? dc, bool graph) => LayoutPanels(dc, graph, StyleDashboard);
    private Size LayoutRings(DrawingContext? dc, bool graph) => LayoutPanels(dc, graph, StyleRings);
    private Size LayoutIcons(DrawingContext? dc, bool graph) => LayoutPanels(dc, graph, StyleIcons);

    /// <summary>Disposizione comune: blocchi uno sotto l'altro, una colonna nuova solo quando lo schermo non basta.</summary>
    private Size LayoutPanels(DrawingContext? dc, bool graph, int style)
    {
        const double P = 8, Gap = 6, ColGap = 8;
        var groups = Groups();
        double colW = style switch { StyleDashboard => 296, StyleRings => 304, _ => 212 };
        bool hasFps = false;
        foreach (var r in groups)
        {
            if (r.Tag == "FPS") hasFps = true;
            colW = Math.Max(colW, BlockWidth(style, r));
        }
        bool graphBlock = graph && !hasFps;
        double titleH = HasTitle() ? TitleLine(null, 0, 0, colW) + 6 : 0;
        double colH = Math.Max(200, MaxPanelTall - 2 * P - titleH);

        _panelBlocks.Clear();
        int col = 0, count = groups.Count + (graphBlock ? 1 : 0);
        double y = 0, colsH = 0;
        for (int i = 0; i < count; i++)
        {
            double h = i < groups.Count ? Block(null, style, groups[i], 0, 0, colW, graph) : GraphBlock(null, 0, 0, colW);
            if (y > 0 && y + h > colH) { col++; y = 0; }
            _panelBlocks.Add(new PanelBlock { Index = i, Col = col, Y = y, H = h });
            colsH = Math.Max(colsH, y + h);
            y += h + Gap;
        }
        int cols = LastColumns = col + 1;
        var size = new Size(2 * P + cols * colW + (cols - 1) * ColGap, 2 * P + titleH + colsH);
        if (dc == null) return size;

        double top = P;
        if (titleH > 0) { TitleLine(dc, P + 6, top, size.Width - 2 * P); top += titleH; }
        foreach (var b in _panelBlocks)
        {
            double x = P + b.Col * (colW + ColGap), by = top + b.Y;
            if (b.Index < groups.Count) Block(dc, style, groups[b.Index], x, by, colW, graph);
            else GraphBlock(dc, x, by, colW);
        }
        return size;
    }

    private double Block(DrawingContext? dc, int style, Row r, double x, double y, double w, bool graph) => style switch
    {
        StyleDashboard => r.Tag == "FPS" ? DashHero(dc, r, x, y, w, graph) : DashGroup(dc, r, x, y, w),
        StyleRings => r.Tag == "FPS" ? RingHero(dc, r, x, y, w, graph) : RingGroup(dc, r, x, y, w),
        _ => r.Tag == "FPS" ? IconHero(dc, r, x, y, w, graph) : IconGroup(dc, r, x, y, w),
    };

    /// <summary>Il grafico dei frame da solo, quando le prestazioni non sono mostrate.</summary>
    private double GraphBlock(DrawingContext? dc, double x, double y, double w)
    {
        const double H = 40;
        if (dc != null)
        {
            Tile(dc, x, y, w, H + 16);
            DrawGraph(dc, new Rect(x + 8, y + 8, w - 16, H), false);
        }
        return H + 16;
    }

    // ------------------------------------------------------------ misure

    /// <summary>La larghezza minima di una colonna perché i testi di questo componente ci stiano.</summary>
    private double BlockWidth(int style, Row r)
    {
        double need = 0, hs = r.Scale;
        need = Math.Max(need, 40 * hs + Make(HeaderText(r, false, false), HeadSize * hs, LabelBrush(r)).Width + 20 * hs);
        if (r.Tag == "FPS")
        {
            int big = BigIndex(r);
            var bc = r.Cells[big];
            double bigS = BigSize(style) * bc.Scale;
            double left = style == StyleRings ? 2 * 44 * bc.Scale + 24 * bc.Scale
                : 12 + SlotFace(r, bc, bigS) + 6 + Make("FPS", UnitSize * bc.Scale, _theme.Unit).Width + 12;
            double right = 0;
            if (style == StyleIcons)
            {
                // nelle Icone le altre misure vanno sotto il numero grande, due per riga, col nome piccolo
                for (int i = 0; i < r.Cells.Length; i++)
                    if (i != big)
                    {
                        var c = r.Cells[i];
                        right = Math.Max(right, Make(c.Name, UnitSize * c.Scale, _theme.Unit).Width + 6 + Slot(r, c, ValueSize * c.Scale, 2) + UnitW(c, c.Scale) + 10);
                    }
                return Math.Max(need, Math.Max(left + 70, 2 * right + 14));
            }
            for (int i = 0; i < r.Cells.Length; i++)
                if (i != big) right = Math.Max(right, ListLineW(r, r.Cells[i]));
            return Math.Max(need, left + 14 + right);
        }
        for (int i = 0; i < r.Cells.Length; i++)
        {
            var c = r.Cells[i];
            double s = c.Scale;
            if (IsGrid(c)) { need = Math.Max(need, 120 * s + Make(c.Name, NameSize * s, _theme.Unit).Width); continue; }
            double val = Slot(r, c, ValueSize * s, 2), unit = UnitW(c, s);
            switch (style)
            {
                case StyleDashboard:
                    // nome sopra, valore sotto a destra: due riquadri per riga
                    double nameW = Math.Min(200, Make(c.Name, TileName * s, _theme.Unit).Width);
                    double tile = 32 * s + Math.Max(nameW, val + unit) + 10 * s;
                    need = Math.Max(need, 2 * tile + 4);
                    break;
                case StyleRings:
                    need = Math.Max(need, 16 + 4 * RingCellW(r, c));   // quattro anelli per riga
                    break;
                default:
                    double cw = 24 * s + TagW(r, i, s) + val + unit + 8 * s;
                    need = Math.Max(need, 2 * cw + 10);
                    break;
            }
        }
        return need;
    }

    private const double TileName = NameSize * 0.92;   // i nomi nei riquadri: un filo più piccoli dei valori

    private double UnitW(in Cell c, double s) => c.LongUnit.Length == 0 ? 0 : Make(c.LongUnit, UnitSize * s, _theme.Unit).Width + 2;

    /// <summary>Una riga "nome ... valore unità" della colonna accanto al numero grande.</summary>
    private double ListLineW(Row r, in Cell c) =>
        Make(c.Name, TileName * c.Scale, _theme.Unit).Width + 10 + Slot(r, c, ValueSize * c.Scale, 2) + UnitW(c, c.Scale);

    private static double BigSize(int style) => style switch
    {
        StyleDashboard => ValueSize * 3.4,
        StyleRings => ValueSize * 2.4,
        _ => ValueSize * 2.6,
    };

    /// <summary>La voce del numero grande: gli FPS se ci sono, altrimenti la prima.</summary>
    private static int BigIndex(Row r)
    {
        for (int i = 0; i < r.Cells.Length; i++) if (r.Cells[i].Kind == "fps") return i;
        return 0;
    }

    // ------------------------------------------------------------ pezzi comuni

    private void Tile(DrawingContext? dc, double x, double y, double w, double h, double radius = 6)
    {
        if (dc == null || !Draws(false)) return;
        dc.DrawRoundedRectangle(TileFill, TileEdge, new Rect(x + 0.5, y + 0.5, w - 1, h - 1), radius, radius);
    }

    /// <summary>Il colore del valore: le temperature diventano ambra da 80 °C e rosse da 90 °C.</summary>
    private Brush PanelValueBrush(in Cell c) =>
        c.Kind == "Temperature" && !double.IsNaN(c.Num) ? (c.Num >= 90 ? HotBrush : c.Num >= 80 ? WarnBrush : ValueBrush(c)) : ValueBrush(c);

    /// <summary>Il colore di anelli e barre: quello del componente, o quello d'allarme per le temperature.</summary>
    private Brush GaugeBrush(in Cell c, Brush group) =>
        c.Kind == "Temperature" && !double.IsNaN(c.Num) ? (c.Num >= 90 ? HotBrush : c.Num >= 80 ? WarnBrush : c.Color ?? group) : c.Color ?? group;

    /// <summary>Valore a destra (strato che cambia), con la grandezza registrata per la prova.</summary>
    private void PanelValue(DrawingContext? dc, in Cell c, double size, double right, double baseline)
    {
        if (dc == null || !Draws(true)) return;
        var t = Make(c.Value, size, PanelValueBrush(c));
        Put(dc, t, right - t.Width, baseline, true);
        Probe(ProbeValues, size);
    }

    private void PanelValueCentered(DrawingContext? dc, in Cell c, double size, double cx, double baseline)
    {
        if (dc == null || !Draws(true)) return;
        var t = Make(c.Value, size, PanelValueBrush(c));
        Put(dc, t, cx - t.Width / 2, baseline, true);
        Probe(ProbeValues, size);
    }

    /// <summary>Valore e unità con l'unità che finisce in "right": l'unità sta ferma, il valore le si allinea.</summary>
    private void ValueUnit(DrawingContext? dc, in Cell c, double s, double right, double baseline)
    {
        double uw = UnitW(c, s);
        if (uw > 0) PutFixed(dc, Make(c.LongUnit, UnitSize * s, _theme.Unit), right - uw + 2, baseline);
        PanelValue(dc, c, ValueSize * s, right - uw, baseline);
    }

    /// <summary>L'intestazione di un blocco: icona del componente e nome, grandi quanto la riga (A−/A+).</summary>
    private double Head(DrawingContext? dc, Row r, double x, double y, double h, double icon, Brush color)
    {
        double hs = r.Scale;
        Icon(dc, GroupIcon(r), x + icon * 0.5, y + h / 2, icon, color);
        var t = Make(HeaderText(r, false, false), HeadSize * hs, color);
        if (dc != null) Probe(ProbeHeads, HeadSize * hs);
        PutFixed(dc, t, x + icon + 6 * hs, Baseline(y, h, HeadSize * hs));
        return x + icon + 6 * hs + t.Width;
    }

    /// <summary>Le barrette dei core in una fascia: etichetta a sinistra, una barretta per core.</summary>
    private double CoreStrip(DrawingContext? dc, Row r, in Cell c, double x, double y, double w, double lineH, bool label)
    {
        if (r.Grid is not { Length: > 0 } g) return 0;
        double s = c.Scale, gap = 2 * s;
        var lt = Make(c.Name, TileName * s, _theme.Unit);
        double bx = x + (label ? lt.Width + 8 * s : 0), avail = x + w - bx;
        int perLine = g.Length;
        while (perLine > 4 && (avail - (perLine - 1) * gap) / perLine < 5 * s) perLine = (perLine + 1) / 2;
        int lines = (g.Length + perLine - 1) / perLine;
        double h = lines * lineH;
        if (dc == null) return h;
        if (label) PutFixed(dc, lt, x, Baseline(y, lineH, TileName * s));
        double bw = (avail - (perLine - 1) * gap) / perLine, bh = lineH - 6 * s;
        var color = c.Color ?? LabelBrush(r);
        for (int from = 0, line = 0; from < g.Length; from += perLine, line++)
            Bars(dc, g, from, Math.Min(g.Length, from + perLine) - 1, bx, y + line * lineH + 3 * s, bw, gap, bh, color);
        return h;
    }

    /// <summary>Una barra orizzontale sottile che si riempie (binario fisso, riempimento che cambia).</summary>
    private void ThinBar(DrawingContext? dc, Rect track, double f, Brush color)
    {
        if (dc == null) return;
        Fill(dc, false, TileTrack, track, track.Height / 2);
        if (!double.IsNaN(f) && f > 0)
            Fill(dc, true, color, new Rect(track.X, track.Y, Math.Max(track.Height, track.Width * f), track.Height), track.Height / 2);
    }

    /// <summary>Una riga "nome ... valore unità" (colonna accanto al numero grande).</summary>
    private void ListLine(DrawingContext? dc, Row r, in Cell c, double x, double y, double w, double h)
    {
        double s = c.Scale, b = Baseline(y, h, ValueSize * s);
        PutFixed(dc, MakeTrimmed(c.Name, TileName * s, _theme.Unit, Math.Max(20, w * 0.55)), x, b);
        ValueUnit(dc, c, s, x + w, b);
    }

    /// <summary>Il numero grande (FPS) nel carattere dei numeri, allineato a destra in uno spazio fisso.</summary>
    private void BigNumber(DrawingContext? dc, Row r, in Cell c, double size, double right, double baseline, bool center = false)
    {
        if (dc == null || !Draws(true)) return;
        var t = MakeFace(c.Value, size, c.Color ?? LabelBrush(r), Faces[2]);   // come nelle proposte: nel colore delle prestazioni
        Put(dc, t, center ? right - t.Width / 2 : right - t.Width, baseline, true);
    }

    private double SlotFace(Row r, in Cell c, double size) =>
        MakeFace(new string('0', HeldLength(r, c, 3)), size, _theme.Value, Faces[2]).Width;

    private FormattedText MakeFace(string text, double size, Brush brush, Typeface face)
    {
        var key = new TextKey(text, size, brush, face, _ppd);
        if (_texts.TryGetValue(key, out var t)) return t;
        return _texts[key] = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            face, size, brush, _ppd);
    }

    /// <summary>Un testo su al massimo due righe, centrato in maxWidth (i nomi sotto gli anelli).</summary>
    private FormattedText MakeWrapped(string text, double size, Brush brush, double maxWidth)
    {
        var key = new TextKey(text, size, brush, _face, _ppd, -maxWidth);
        if (_texts.TryGetValue(key, out var t)) return t;
        t = MakeFresh(text, size, brush);
        t.MaxTextWidth = maxWidth;
        t.MaxLineCount = 2;
        t.Trimming = TextTrimming.CharacterEllipsis;
        t.TextAlignment = TextAlignment.Center;
        return _texts[key] = t;
    }

    // ------------------------------------------------------------ archi (anelli e icona del carico)

    private readonly Dictionary<(double, double, double, double, int), Geometry> _arcs = new();
    private readonly Dictionary<(Brush, double), Pen> _roundPens = new();

    /// <summary>Un arco già pronto per posizione e ampiezza (al mezzo grado): niente geometrie nuove a regime.</summary>
    private Geometry ArcAt(double cx, double cy, double r, double from, double sweep)
    {
        int q = (int)Math.Round(sweep * 2);
        var key = (cx, cy, r, from, q);
        if (_arcs.TryGetValue(key, out var g)) return g;
        if (_arcs.Count > 4000) _arcs.Clear();
        return _arcs[key] = ArcGeometry(cx, cy, r, from, q / 2.0);
    }

    private Pen RoundPen(Brush b, double w)
    {
        if (_roundPens.TryGetValue((b, w), out var p)) return p;
        p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return _roundPens[(b, w)] = p;
    }

    /// <summary>L'icona del carico: un cerchio che si riempie (il binario è nell'icona, l'arco cambia).</summary>
    private void LoadArc(DrawingContext? dc, double cx, double cy, double s, double f, Brush b)
    {
        if (dc == null || !Draws(true) || double.IsNaN(f) || f <= 0) return;
        s = Math.Round(s * 2) / 2;
        double k = s / 2, w = Math.Max(1.3, s * 0.1) * 1.6;
        dc.DrawGeometry(null, RoundPen(b, w), ArcAt(cx, cy, 0.78 * k, -90, 360 * Math.Max(0.02, f)));
    }

    /// <summary>Un anello da 270° aperto in basso: binario fisso, arco del valore che cambia.</summary>
    private void Ring(DrawingContext? dc, double cx, double cy, double r, double w, double f, Brush b)
    {
        if (dc == null) return;
        if (Draws(false)) dc.DrawGeometry(null, RoundPen(TileTrack, w), ArcAt(cx, cy, r, 135, 270));
        if (Draws(true) && !double.IsNaN(f) && f > 0)
            dc.DrawGeometry(null, RoundPen(b, w), ArcAt(cx, cy, r, 135, 270 * Math.Max(0.02, f)));
    }

    /// <summary>L'icona di una voce, con l'arco del carico se è un carico.</summary>
    private void CellIcon(DrawingContext? dc, in Cell c, double cx, double cy, double s, Brush color)
    {
        string kind = IconOf(c);
        Icon(dc, kind, cx, cy, s, color);
        if (kind == "load") LoadArc(dc, cx, cy, s, Fraction(c), GaugeBrush(c, color));
    }

    /// <summary>Le voci che hanno un fondo scala (barra o anello); le altre (testi, totali) no.</summary>
    private static bool HasRange(in Cell c) => c.Kind switch
    {
        "Load" or "Control" or "Level" or "Humidity" or "smooth" or "Temperature" or "ft" or "p99" => true,
        "fps" or "avg" or "low1" or "low01" or "Clock" or "Power" or "Fan" or "Frequency" or "Current" => true,
        "SmallData" => c.Key.StartsWith("/gpu", StringComparison.Ordinal) && SystemInfo.VramTotalMb != null,
        "Data" => c.Key.StartsWith("/ram", StringComparison.Ordinal) && SystemInfo.RamTotalGb != null,
        _ => false,
    };

    // ------------------------------------------------------------ 4 · Dashboard

    private const double DashTH = 42, DashGap = 4;

    private double DashGroup(DrawingContext? dc, Row r, double x, double y, double w)
    {
        var color = LabelBrush(r);
        double hs = r.Scale, hh = 28 * hs;
        if (dc != null)
        {
            Tile(dc, x, y, w, hh);
            Fill(dc, false, color, new Rect(x + 4, y + hh - 2.5, w - 8, 2));
            Head(dc, r, x + 9 * hs, y, hh - 2, 16 * hs, color);
        }
        double cy = y + hh + DashGap, cw = (w - DashGap) / 2;
        int pend = -1;
        for (int i = 0; i < r.Cells.Length; i++)
        {
            var c = r.Cells[i];
            if (IsGrid(c))
            {
                if (pend >= 0) { cy += DashTile(dc, r, pend, x, cy, w, DashTH * r.Cells[pend].Scale) + DashGap; pend = -1; }
                cy += DashCores(dc, r, c, x, cy, w) + DashGap;
            }
            else if (pend < 0) pend = i;
            else
            {
                double th = DashTH * Math.Max(r.Cells[pend].Scale, c.Scale);
                DashTile(dc, r, pend, x, cy, cw, th);
                DashTile(dc, r, i, x + cw + DashGap, cy, cw, th);
                cy += th + DashGap;
                pend = -1;
            }
        }
        if (pend >= 0) cy += DashTile(dc, r, pend, x, cy, w, DashTH * r.Cells[pend].Scale) + DashGap;
        return cy - DashGap - y;
    }

    private double DashTile(DrawingContext? dc, Row r, int i, double x, double y, double w, double h)
    {
        if (dc == null) return h;
        var c = r.Cells[i];
        var color = LabelBrush(r);
        double s = c.Scale;
        Tile(dc, x, y, w, h);
        CellIcon(dc, c, x + 16 * s, y + h / 2 - 1, 17 * s, c.Color ?? color);
        double nx = x + 32 * s, right = x + w - 10 * s;
        PutFixed(dc, MakeTrimmed(c.Name, TileName * s, _theme.Unit, Math.Max(20, right - nx)), nx, y + 16 * s);
        ValueUnit(dc, c, s, right, y + 33 * s);
        string kind = IconOf(c);
        if (kind is "load" or "mem" && HasRange(c))
            ThinBar(dc, new Rect(nx, y + h - 5.5 * s, right - nx, 2 * s), Fraction(c), GaugeBrush(c, color));
        return h;
    }

    private double DashCores(DrawingContext? dc, Row r, in Cell c, double x, double y, double w)
    {
        double s = c.Scale;
        double bars = CoreStrip(null, r, c, x + 32 * s, y + 6 * s, w - 42 * s, 30 * s, true);
        double h = Math.Max(DashTH * s, bars + 12 * s);
        if (dc == null) return h;
        Tile(dc, x, y, w, h);
        Icon(dc, "cpu", x + 16 * s, y + h / 2, 17 * s, c.Color ?? LabelBrush(r));
        CoreStrip(dc, r, c, x + 32 * s, y + 6 * s, w - 42 * s, 30 * s, true);
        return h;
    }

    /// <summary>Le prestazioni: FPS in grande, le altre misure a destra, il grafico sotto.</summary>
    private double DashHero(DrawingContext? dc, Row r, double x, double y, double w, bool graph)
    {
        var color = LabelBrush(r);
        double hs = r.Scale, hh = 26 * hs;
        int big = BigIndex(r);
        var bc = r.Cells[big];
        double bs = bc.Scale, bigS = BigSize(StyleDashboard) * bs;
        var probe = MakeFace("0", bigS, _theme.Value, Faces[2]);
        double bigH = bigS * 1.02;
        double slot = SlotFace(r, bc, bigS);
        double leftW = 12 + slot + 6 + Make("FPS", UnitSize * bs, _theme.Unit).Width + 12;
        double rh = 0;
        for (int i = 0; i < r.Cells.Length; i++) if (i != big) rh += 22 * r.Cells[i].Scale;
        double bodyH = Math.Max(bigH, rh);
        double gh = graph ? 38 : 0;
        double h = hh + bodyH + (graph ? 6 + gh : 0) + 10;
        if (dc == null) return h;

        Tile(dc, x, y, w, h);
        Head(dc, r, x + 9 * hs, y + 2, hh - 2, 16 * hs, color);
        double top = y + hh, bb = top + (bodyH - bigH) / 2 + probe.Baseline - bigS * 0.13;
        BigNumber(dc, r, bc, bigS, x + 12 + slot, bb);
        PutFixed(dc, Make(bc.Kind == "fps" ? "FPS" : bc.LongUnit, UnitSize * bs, _theme.Unit), x + 12 + slot + 6, bb);
        double ry = top + (bodyH - rh) / 2, rx = x + leftW + 6;
        for (int i = 0; i < r.Cells.Length; i++)
        {
            if (i == big) continue;
            double lh = 22 * r.Cells[i].Scale;
            ListLine(dc, r, r.Cells[i], rx, ry, x + w - 10 - rx, lh);
            ry += lh;
        }
        if (graph) DrawGraph(dc, new Rect(x + 10, top + bodyH + 6, w - 20, gh), false);
        return h;
    }

    // ------------------------------------------------------------ 5 · Anelli

    private const double RingR = 25, RingW = 4.5;

    /// <summary>Raggio dell'anello: quello di base, o più grande se il valore più lungo visto non ci sta dentro.</summary>
    private double RingRadius(Row r, in Cell c)
    {
        double s = c.Scale;
        return Math.Max(RingR * s, Slot(r, c, ValueSize * s, 2) / 2 + RingW * s + 3 * s);
    }

    /// <summary>Larghezza di una cella: l'anello, o il valore per le voci senza fondo scala (testi, totali).</summary>
    private double RingCellW(Row r, in Cell c) =>
        HasRange(c) ? 2 * RingRadius(r, c) + 12 * c.Scale : Math.Max(2 * RingR * c.Scale, Slot(r, c, ValueSize * c.Scale, 2)) + 12 * c.Scale;

    /// <summary>Il raggio comune del componente: tutti gli anelli in fila, nomi sulla stessa riga.</summary>
    private double GroupRingR(Row r)
    {
        double R = 0;
        foreach (var c in r.Cells) if (!IsGrid(c)) R = Math.Max(R, HasRange(c) ? RingRadius(r, c) : RingR * c.Scale);
        return R;
    }

    private double RingGroup(DrawingContext? dc, Row r, double x, double y, double w)
    {
        var color = LabelBrush(r);
        double hs = r.Scale, hh = 26 * hs, pad = 8;
        double inner = w - 2 * pad;
        // larghezza uguale per tutte le celle del componente: quante ne stanno per riga
        double cellW = 0;
        foreach (var c in r.Cells) if (!IsGrid(c)) cellW = Math.Max(cellW, RingCellW(r, c));
        int perRow = cellW > 0 ? Math.Max(1, Math.Min(4, (int)(inner / cellW))) : 4;
        cellW = inner / perRow;

        if (dc != null)
        {
            // prima il riquadro (sotto a tutto), con l'altezza misurata
            double th = RingGroup(null, r, x, y, w);
            Tile(dc, x, y, w, th);
            double hx = Head(dc, r, x + 9 * hs, y + 2, hh - 2, 15 * hs, color);
            double ly = Math.Round(y + 1 + hh / 2) + 0.5;
            if (Draws(false) && hx + 8 < x + w - 10)
                Line(dc, HeaderPen(color), new Point(hx + 8, ly), new Point(x + w - 10, ly));
        }
        double cy = y + hh + 4;
        int start = -1;
        for (int i = 0; i <= r.Cells.Length; i++)
        {
            bool end = i == r.Cells.Length;
            bool grid = !end && IsGrid(r.Cells[i]);
            if (start < 0 && !end && !grid) start = i;
            if (start >= 0 && (end || grid || i - start == perRow))
            {
                cy += RingLine(dc, r, start, i, x + pad, cy, cellW, GroupRingR(r), color);
                start = !end && !grid ? i : -1;
            }
            if (grid)
            {
                var c = r.Cells[i];
                double lh = 26 * c.Scale;
                cy += CoreStrip(dc, r, c, x + pad + 4, cy + 2, inner - 8, lh, true) + 6;
            }
        }
        return cy - y + 4;
    }

    /// <summary>Una riga di anelli (celle da "from" a "to" escluso).</summary>
    private double RingLine(DrawingContext? dc, Row r, int from, int to, double x, double y, double cellW, double groupR, Brush color)
    {
        double nameH = 0;
        for (int i = from; i < to; i++)
        {
            var c = r.Cells[i];
            nameH = Math.Max(nameH, MakeWrapped(c.Name, TileName * 0.95 * c.Scale, _theme.Unit, cellW - 4).Height);
        }
        double rowH = 2 * groupR + 8 + nameH + 4;
        if (dc == null) return rowH;
        for (int i = from; i < to; i++)
        {
            var c = r.Cells[i];
            double s = c.Scale, R = RingRadius(r, c);
            double cx = x + (i - from + 0.5) * cellW, cy = y + groupR + 2;
            var gc = GaugeBrush(c, c.Color ?? color);
            var name = MakeWrapped(c.Name, TileName * 0.95 * s, _theme.Unit, cellW - 4);
            if (HasRange(c))
            {
                Ring(dc, cx, cy, R - RingW * s / 2, RingW * s, Fraction(c), gc);
                PanelValueCentered(dc, c, ValueSize * s, cx, cy + 4 * s);
                if (c.LongUnit.Length > 0)
                {
                    var u = Make(c.LongUnit, UnitSize * 0.9 * s, _theme.Unit);
                    PutFixed(dc, u, cx - u.Width / 2, cy + 15 * s);
                }
                Icon(dc, IconOf(c) == "load" ? "info" : IconOf(c), cx, cy + R * 0.93, 10 * s, c.Color ?? color);
            }
            else
            {
                Icon(dc, IconOf(c), cx, cy - 9 * s, 15 * s, c.Color ?? color);
                PanelValueCentered(dc, c, ValueSize * s, cx, cy + 10 * s);
                if (c.LongUnit.Length > 0)
                {
                    var u = Make(c.LongUnit, UnitSize * 0.9 * s, _theme.Unit);
                    PutFixed(dc, u, cx - u.Width / 2, cy + 21 * s);
                }
            }
            PutFixed(dc, name, cx - (cellW - 4) / 2, y + 2 * groupR + 8 + name.Baseline);
        }
        return rowH;
    }

    private double RingHero(DrawingContext? dc, Row r, double x, double y, double w, bool graph)
    {
        var color = LabelBrush(r);
        double hs = r.Scale, hh = 26 * hs;
        int big = BigIndex(r);
        var bc = r.Cells[big];
        double bs = bc.Scale, R = 44 * bs, rw = 7 * bs, bigS = BigSize(StyleRings) * bs;
        double ringH = 2 * R + 8;
        double rh = 0;
        for (int i = 0; i < r.Cells.Length; i++) if (i != big) rh += 22 * r.Cells[i].Scale;
        double bodyH = Math.Max(ringH, rh);
        double gh = graph ? 34 : 0;
        double h = hh + bodyH + (graph ? 6 + gh : 0) + 10;
        if (dc == null) return h;

        Tile(dc, x, y, w, h);
        double hx = Head(dc, r, x + 9 * hs, y + 2, hh - 2, 15 * hs, color);
        if (Draws(false) && hx + 8 < x + w - 10)
            Line(dc, HeaderPen(color), new Point(hx + 8, Math.Round(y + 1 + hh / 2) + 0.5), new Point(x + w - 10, Math.Round(y + 1 + hh / 2) + 0.5));
        double top = y + hh, cx = x + 12 + R, cy = top + (bodyH - ringH) / 2 + R + 4;
        Ring(dc, cx, cy, R, rw, Fraction(bc), bc.Color ?? color);
        var probe = MakeFace("0", bigS, _theme.Value, Faces[2]);
        BigNumber(dc, r, bc, bigS, cx, cy + probe.Baseline - probe.Height / 2 + 1, center: true);
        var u = Make(bc.Kind == "fps" ? "FPS" : bc.LongUnit, UnitSize * bs, _theme.Unit);
        PutFixed(dc, u, cx - u.Width / 2, cy + R * 0.62);
        double ry = top + (bodyH - rh) / 2, rx = x + 12 + 2 * R + 20;
        for (int i = 0; i < r.Cells.Length; i++)
        {
            if (i == big) continue;
            double lh = 22 * r.Cells[i].Scale;
            ListLine(dc, r, r.Cells[i], rx, ry, x + w - 12 - rx, lh);
            ry += lh;
        }
        if (graph) DrawGraph(dc, new Rect(x + 10, top + bodyH + 6, w - 20, gh), false);
        return h;
    }

    // ------------------------------------------------------------ 6 · Icone

    private const double IconRH = 25;

    /// <summary>
    /// Se due voci dello stesso componente hanno la stessa icona, accanto all'icona va una sigla di due lettere presa
    /// dal nome (es. "Hot Spot" → "HS"), altrimenti non si distinguerebbero.
    /// </summary>
    private string? IconTag(Row r, int i)
    {
        string kind = IconOf(r.Cells[i]);
        for (int j = 0; j < r.Cells.Length; j++)
            if (j != i && !IsGrid(r.Cells[j]) && IconOf(r.Cells[j]) == kind) return Tag2(r.Cells[i].Name);
        return null;
    }

    private readonly Dictionary<string, string> _tags2 = new();

    private string Tag2(string name)
    {
        if (_tags2.TryGetValue(name, out var t)) return t;
        var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2) t = (char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[1][0])).ToString();
        else if (name.Length >= 2) t = char.ToUpperInvariant(name[0]) + name.Substring(1, 1).ToLowerInvariant();
        else t = name;
        return _tags2[name] = t;
    }

    private double TagW(Row r, int i, double s) =>
        IconTag(r, i) is { } t ? Make(t, UnitSize * s, _theme.Unit).Width + 4 * s : 0;

    private double IconGroup(DrawingContext? dc, Row r, double x, double y, double w)
    {
        var color = LabelBrush(r);
        double hs = r.Scale, hh = 22 * hs;
        double cw = (w - 12) / 2;
        // altezza: intestazione, righe da due, fasce dei core
        if (dc != null)
        {
            double th = IconGroup(null, r, x, y, w);
            Tile(dc, x, y, w, th, 5);
            Fill(dc, false, color, new Rect(x + 1.5, y + 4, 3, th - 8), 1.5);
            var head = Make(HeaderText(r, false, false), HeadSize * 0.92 * hs, color);
            Probe(ProbeHeads, HeadSize * 0.92 * hs);
            PutFixed(dc, head, x + 10, Baseline(y + 1, hh - 3, HeadSize * 0.92 * hs));
            if (Draws(false)) dc.DrawRectangle(TileFaint, null, new Rect(x + 8, Math.Round(y + hh), w - 16, 1));
        }
        double cy = y + hh + 2;
        int pend = -1;
        for (int i = 0; i < r.Cells.Length; i++)
        {
            var c = r.Cells[i];
            if (IsGrid(c))
            {
                if (pend >= 0) { cy += IconRow(dc, r, pend, -1, x + 8, cy, cw); pend = -1; }
                cy += CoreStrip(dc, r, c, x + 10, cy + 1, w - 18, 22 * c.Scale, false) + 3;
            }
            else if (pend < 0) pend = i;
            else { cy += IconRow(dc, r, pend, i, x + 8, cy, cw); pend = -1; }
        }
        if (pend >= 0) cy += IconRow(dc, r, pend, -1, x + 8, cy, cw);
        return cy - y + 4;
    }

    /// <summary>Una riga di due voci (b = -1: una sola).</summary>
    private double IconRow(DrawingContext? dc, Row r, int a, int b, double x, double y, double cw)
    {
        double s = Math.Max(r.Cells[a].Scale, b >= 0 ? r.Cells[b].Scale : 0), h = IconRH * s;
        if (dc == null) return h;
        IconCell(dc, r, a, x, y, cw, h);
        if (b >= 0)
        {
            IconCell(dc, r, b, x + cw + 4, y, cw, h);
            if (Draws(false)) dc.DrawRectangle(TileFaint, null, new Rect(Math.Round(x + cw + 1), y + 5, 1, h - 10));
        }
        return h;
    }

    private void IconCell(DrawingContext? dc, Row r, int i, double x, double y, double w, double h)
    {
        var c = r.Cells[i];
        var color = c.Color ?? LabelBrush(r);
        double s = c.Scale;
        CellIcon(dc, c, x + 8 * s, y + h / 2, 14 * s, color);
        double b = Baseline(y, h, ValueSize * s);
        if (IconTag(r, i) is { } tag) PutFixed(dc, Make(tag, UnitSize * s, _theme.Unit), x + 18 * s, b);
        ValueUnit(dc, c, s, x + w - 4 * s, b);
    }

    private double IconHero(DrawingContext? dc, Row r, double x, double y, double w, bool graph)
    {
        int big = BigIndex(r);
        var bc = r.Cells[big];
        double bs = bc.Scale, bigS = BigSize(StyleIcons) * bs;
        var probe = MakeFace("0", bigS, _theme.Value, Faces[2]);
        double topH = bigS * 1.05 + 6;
        double cw = (w - 12) / 2;
        int others = r.Cells.Length - 1;
        double rowsH = 0;   // righe da due
        for (int i = 0, k = 0; i < r.Cells.Length; i++)
        {
            if (i == big) continue;
            if (k % 2 == 0) rowsH += 22 * r.Cells[i].Scale;
            k++;
        }
        double h = topH + rowsH + 8;
        if (dc == null) return h;

        var color = LabelBrush(r);
        Tile(dc, x, y, w, h, 5);
        Fill(dc, false, color, new Rect(x + 1.5, y + 4, 3, h - 8), 1.5);
        double slot = SlotFace(r, bc, bigS);
        double bb = y + 4 + probe.Baseline - bigS * 0.1;
        BigNumber(dc, r, bc, bigS, x + 12 + slot, bb);
        PutFixed(dc, Make(bc.Kind == "fps" ? "FPS" : bc.LongUnit, UnitSize * bs, _theme.Unit), x + 12 + slot + 5, bb);
        if (graph)
        {
            double gx = x + 12 + slot + 5 + Make("FPS", UnitSize * bs, _theme.Unit).Width + 10;
            if (x + w - 10 - gx > 30) DrawGraph(dc, new Rect(gx, y + 8, x + w - 10 - gx, topH - 12), true);
        }
        double ry = y + topH + 2;
        for (int i = 0, k = 0; i < r.Cells.Length; i++)
        {
            if (i == big) continue;
            var c = r.Cells[i];
            double lh = 22 * c.Scale, cx = x + 10 + (k % 2) * (cw + 2);
            double b = Baseline(ry, lh, ValueSize * c.Scale);
            PutFixed(dc, MakeTrimmed(c.Name, UnitSize * c.Scale, _theme.Unit, cw * 0.5), cx, b);
            ValueUnit(dc, c, c.Scale, cx + cw - 6, b);
            if (k % 2 == 1 || k == others - 1) ry += lh;
            k++;
        }
        return h;
    }
}
