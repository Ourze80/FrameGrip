using System.Windows;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Gli stili Grid HUD e Console (Dashboard, Anelli e Icone sono in OverlayPanels). Regola comune: le voci sono sempre nell'ordine di "Personalizza"
/// (componenti e sensori, nessun valore spostato in cima), con il nome che vede l'utente (il suo, se l'ha scritto).
/// Le colonne si spezzano tra un componente e l'altro; un componente si divide solo se è più alto di una colonna,
/// e allora la sua intestazione si ripete in cima alla colonna nuova ("GPU (segue)" / "[ gpu + ]").
/// Le barrette per core stanno nel punto in cui l'utente ha messo i core (voce segnaposto, vedi OverlayCompact).
/// </summary>
public sealed partial class OverlayView
{
    // ------------------------------------------------------------ voci piazzate (lista riusata: niente allocazioni)

    /// <summary>Una voce piazzata: Cell = -1 intestazione del componente (Cont: ripetuta in una colonna nuova).</summary>
    private struct Placed
    {
        public Row Row;
        public int Cell, Col;
        public bool Cont;
        public double Y, H;
    }

    private readonly List<Placed> _placed = new();
    private readonly List<Row> _groups = new();

    /// <summary>I componenti da mostrare, nell'ordine dell'utente (senza il titolo del gioco).</summary>
    private List<Row> Groups()
    {
        _groups.Clear();
        foreach (var r in Shown)
            if (r.Tag is not ("API" or "REC") && r.Cells.Length > 0) _groups.Add(r);
        return _groups;
    }

    private bool HasTitle()
    {
        foreach (var r in Shown) if (r.Tag is "API" or "REC") return true;
        return false;
    }

    private static int GridLines(Row r, int perLine) => r.Grid is { Length: > 0 } g ? (g.Length + perLine - 1) / perLine : 0;

    // Le altezze seguono le grandezze scelte con A−/A+: la voce la sua, l'intestazione quella del componente.
    // Nel Grid HUD le percentuali hanno sotto una barretta sottile: la loro riga è un poco più alta (_pctExtra).
    private double ItemH(Row r, int i, double rowH, int perLine) =>
        IsGrid(r.Cells[i]) ? Math.Max(1, GridLines(r, perLine)) * rowH * r.Cells[i].Scale + 4
        : (rowH + (IsPct(r.Cells[i]) ? _pctExtra : 0)) * r.Cells[i].Scale;

    private double _pctExtra;
    private static bool IsPct(in Cell c) => c.LongUnit == "%";

    private static double HeadH(Row r, double hdrH) => hdrH * Math.Max(1, r.Scale);

    private double GroupH(Row r, double rowH, double hdrH, int perLine)
    {
        double h = HeadH(r, hdrH);
        for (int i = 0; i < r.Cells.Length; i++) h += ItemH(r, i, rowH, perLine);
        return h;
    }

    /// <summary>
    /// Mette i componenti in colonne alte al massimo colH. Un componente va a capo intero se non ci sta più ma ci
    /// starebbe in una colonna nuova; se è più alto di una colonna si divide, ripetendo l'intestazione. Un'intestazione
    /// non resta mai da sola in fondo a una colonna. Restituisce il numero di colonne; colsH è la più alta.
    /// </summary>
    private int FlowColumns(List<Row> groups, double colH, double rowH, double hdrH, int perLine, out double colsH)
    {
        _placed.Clear();
        int col = 0;
        double used = 0;
        colsH = 0;
        foreach (var r in groups)
        {
            double hh = HeadH(r, hdrH);
            double gc = GroupH(r, rowH, hdrH, perLine);
            double first = hh + (r.Cells.Length > 0 ? ItemH(r, 0, rowH, perLine) : 0);
            if (used > 0 && ((used + gc > colH + 0.01 && gc <= colH + 0.01) || used + first > colH + 0.01)) { col++; used = 0; }
            _placed.Add(new Placed { Row = r, Cell = -1, Col = col, Y = used, H = hh });
            used += hh;
            for (int i = 0; i < r.Cells.Length; i++)
            {
                double h = ItemH(r, i, rowH, perLine);
                if (used + h > colH + 0.01 && used > hh + 0.5)
                {
                    col++; used = 0;
                    _placed.Add(new Placed { Row = r, Cell = -1, Cont = true, Col = col, Y = 0, H = hh });
                    used = hh;
                }
                _placed.Add(new Placed { Row = r, Cell = i, Col = col, Y = used, H = h });
                used += h;
                colsH = Math.Max(colsH, used);
            }
            colsH = Math.Max(colsH, used);
        }
        return col + 1;
    }

    /// <summary>Come FlowColumns, ma con le colonne il più possibile pari (stesso numero di colonne, meno alte),
    /// senza dividere un componente che starebbe intero in una colonna.</summary>
    private int FlowBalanced(List<Row> groups, double colH, double rowH, double hdrH, int perLine, out double colsH)
    {
        int cols = FlowColumns(groups, colH, rowH, hdrH, perLine, out colsH);
        if (cols <= 1) return cols;
        double total = 0, biggest = 0;
        foreach (var r in groups) { double g = GroupH(r, rowH, hdrH, perLine); total += g; biggest = Math.Max(biggest, g); }
        double lo = total / cols, hi = colH;
        if (biggest <= colH) lo = Math.Max(lo, biggest);
        if (lo >= hi) return cols;
        if (FlowColumns(groups, lo, rowH, hdrH, perLine, out _) != cols)
        {
            for (int it = 0; it < 12; it++)
            {
                double mid = (lo + hi) / 2;
                if (FlowColumns(groups, mid, rowH, hdrH, perLine, out _) == cols) hi = mid; else lo = mid;
            }
        }
        else hi = lo;
        return FlowColumns(groups, hi, rowH, hdrH, perLine, out colsH);
    }

    // ------------------------------------------------------------ barrette per core

    private readonly Dictionary<(string, string, string, bool), string> _barLabels = new();

    /// <summary>"Cores 1–8" (o "c01-08" nella console) per una riga di barrette.</summary>
    private string BarLabel(string stem, Cell[] g, int from, int to, bool console)
    {
        var key = (stem, g[from].Name, g[to].Name, console);
        if (_barLabels.TryGetValue(key, out var s)) return s;
        s = console ? "c" + g[from].Name.PadLeft(2, '0') + "-" + g[to].Name.PadLeft(2, '0')
                    : stem + " " + g[from].Name + "–" + g[to].Name;
        return _barLabels[key] = s;
    }

    private double BarLabelW(Row r, in Cell c, int perLine, double size, bool console)
    {
        double w = 0;
        if (r.Grid is not { Length: > 0 } g) return Make(c.Name, size, _theme.Unit).Width;
        for (int from = 0; from < g.Length; from += perLine)
            w = Math.Max(w, Make(BarLabel(c.Name, g, from, Math.Min(g.Length, from + perLine) - 1, console), size, _theme.Unit).Width);
        return w;
    }

    /// <summary>Barrette verticali: binario nello strato fisso, riempimento dal basso in quello che cambia.</summary>
    private void Bars(DrawingContext dc, Cell[] g, int from, int to, double x, double y, double bw, double gap, double bh, Brush color)
    {
        for (int k = from; k <= to; k++)
        {
            double bx = x + (k - from) * (bw + gap);
            Fill(dc, false, Track, new Rect(bx, y, bw, bh), 1);
            double f = Fraction(g[k]);
            if (double.IsNaN(f)) continue;
            double h = Math.Max(1.5, bh * f);
            Fill(dc, true, f >= 0.9 ? _theme.Pace : (g[k].Color ?? color), new Rect(bx, y + bh - h, bw, h), 1);
        }
    }

    /// <summary>Le barrette su più righe da perLine, ognuna con la sua etichetta a sinistra.</summary>
    private void BarLines(DrawingContext dc, Row r, in Cell c, double x, double y, double lineH, double size, Brush label,
        double labelW, double bw, double gap, double bh, int perLine, bool console)
    {
        if (r.Grid is not { Length: > 0 } g) return;
        var color = c.Color ?? LabelBrush(r);
        for (int from = 0, line = 0; from < g.Length; from += perLine, line++)
        {
            int to = Math.Min(g.Length, from + perLine) - 1;
            double ly = y + line * lineH;
            PutFixed(dc, Make(BarLabel(c.Name, g, from, to, console), size, label), x, Baseline(ly, lineH, size));
            Bars(dc, g, from, to, x + labelW + 8, ly + (lineH - bh) / 2, bw, gap, bh, color);
        }
    }

    // ------------------------------------------------------------ misure comuni delle righe "nome ... valore unità"

    private (double Name, double Value, double Unit, double Bars, double Head) RowWidths(List<Row> groups, double nameS, double valS,
        double unitS, double headS, int perLine, double barStep, bool console)
    {
        double nameW = 0, valW = 0, unitW = 0, barsW = 0, headW = 0;
        foreach (var r in groups)
        {
            headW = Math.Max(headW, Make(HeaderText(r, true, console), headS * r.Scale, LabelBrush(r)).Width);
            for (int i = 0; i < r.Cells.Length; i++)
            {
                var c = r.Cells[i];
                if (IsGrid(c))
                {
                    int n = Math.Min(perLine, r.Grid?.Length ?? 0);
                    barsW = Math.Max(barsW, BarLabelW(r, c, perLine, nameS * c.Scale, console) + 8 + n * barStep * c.Scale);
                    continue;
                }
                nameW = Math.Max(nameW, Math.Min(240, Make(console ? Lower(c.Name) : c.Name, nameS * c.Scale, _theme.Value).Width));
                valW = Math.Max(valW, Slot(r, c, valS * c.Scale, 2));
                unitW = Math.Max(unitW, Make(console ? Lower(c.LongUnit) : c.LongUnit, unitS * c.Scale, _theme.Unit).Width);
            }
        }
        return (nameW, valW, unitW, barsW, headW);
    }

    // ------------------------------------------------------------ 2 · Grid HUD

    /// <summary>Una tabella fitta: una riga per sensore, componenti uno sotto l'altro in una colonna che cresce verso
    /// il basso (una seconda solo se lo schermo non basta), grafico in fondo.</summary>
    private Size LayoutGrid(DrawingContext? dc, bool graph)
    {
        const double P = 12, ColGap = 14, NameS = NameSize, ValS = ValueSize, UnitS = UnitSize, HeadS = HeadSize, GraphHt = 40;
        const int PerLine = 8;
        var groups = Groups();
        var (nameW, valW, unitW, barsW, headW) = RowWidths(groups, NameS, ValS, UnitS, HeadS, PerLine, 12, false);
        double colW = Math.Max(Math.Max(8 + nameW + 14 + valW + 2 + unitW + 6, barsW + 12), Math.Max(headW + 40, 170));
        double titleH = HasTitle() ? TitleLine(null, 0, 0, colW) + 8 : 0;
        double graphH = graph ? GraphHt + 8 : 0;

        // Una sola colonna fino all'altezza utile dello schermo; oltre, colonne bilanciate tra i componenti.
        const double rowH = 21, hdrH = 28, PctBar = 3, PctExtra = 2;
        double colH = Math.Max(6 * rowH, MaxPanelTall - 2 * P - titleH - graphH);
        _pctExtra = PctExtra;
        int cols = LastColumns = FlowBalanced(groups, colH, rowH, hdrH, PerLine, out double colsH);
        _pctExtra = 0;
        double width = 2 * P + cols * colW + (cols - 1) * ColGap;
        double height = 2 * P + titleH + colsH + graphH;
        var size = new Size(width, height);
        if (dc == null) return size;

        double top = P;
        if (titleH > 0) { TitleLine(dc, P, top, width - 2 * P); top += titleH; }
        foreach (var p in _placed)
        {
            var r = p.Row;
            double x = P + p.Col * (colW + ColGap), y = top + p.Y;
            var color = LabelBrush(r);
            if (p.Cell < 0)
            {
                Fill(dc, false, color, new Rect(x, y + 4, 3, p.H - 7));
                var head = Make(HeaderText(r, p.Cont, false), HeadS * r.Scale, color);
                Probe(ProbeHeads, HeadS * r.Scale);
                double hb = Baseline(y + 2, p.H - 2, HeadS * r.Scale);
                PutFixed(dc, head, x + 9, hb);
                if (Draws(false) && x + 9 + head.Width + 8 < x + colW)
                {
                    double ly = Math.Round(y + 2 + (p.H - 2) / 2) + 0.5;
                    Line(dc, HeaderPen(color), new Point(x + 9 + head.Width + 8, ly), new Point(x + colW, ly));
                }
                continue;
            }
            var c = r.Cells[p.Cell];
            if (IsGrid(c))
            {
                double gs = c.Scale;
                BarLines(dc, r, c, x + 8, y + 2, rowH * gs, NameS * gs, _theme.Unit, BarLabelW(r, c, PerLine, NameS * gs, false), 9 * gs, 3 * gs, 13 * gs, PerLine, false);
                continue;
            }
            // Le percentuali hanno sotto la riga una barretta sottile (dal nome al valore) che si riempie nel colore
            // del componente: niente dietro al testo, che resta leggibile su qualsiasi scena.
            double textH = p.H;
            if (IsPct(c))
            {
                double bh = PctBar * c.Scale;
                textH = p.H - PctExtra * c.Scale;
                var track = new Rect(x + 8, y + p.H - bh - 0.5, colW - 14, bh);
                PctTrack(dc, track);
                if (Fraction(c) is var f && !double.IsNaN(f) && f > 0)
                    PctFill(dc, new Rect(track.X, track.Y, Math.Max(bh, track.Width * Math.Min(1, f)), bh), f >= 0.9 ? _theme.Pace : (c.Color ?? color));
            }
            double b = Baseline(y, textH, ValS * c.Scale);
            PutFixed(dc, Make(c.Name, NameS * c.Scale, _theme.Value), x + 8, b);
            double vr = x + colW - 6 - unitW - 2;
            PutValue(dc, c, ValS * c.Scale, vr, b);
            PutFixed(dc, Make(c.LongUnit, UnitS * c.Scale, _theme.Unit), vr + 2, b);
        }
        if (graph) DrawGraph(dc, new Rect(P, top + colsH + 8, width - 2 * P, GraphHt), false);
        return size;
    }

    // ------------------------------------------------------------ 3 · Console

    private readonly Dictionary<int, string> _dots = new();
    private string Dots(int n) => _dots.TryGetValue(n, out var s) ? s : _dots[n] = new string('.', n);

    /// <summary>
    /// Un terminale a caratteri fissi: "[ gpu ]" e sotto le voci "nome ...... valore unità", in una colonna che
    /// cresce verso il basso (una seconda solo se lo schermo non basta); in fondo la riga del frametime.
    /// </summary>
    private Size LayoutConsole(DrawingContext? dc, bool graph)
    {
        // nomi, intestazioni e unità alla grandezza dei nomi; i valori a quella dei valori (come nella Lista)
        const double P = 14, FS = NameSize, VS = ValueSize, US = UnitSize, ColGap = 22, GraphHt = 34, TopH = 44;
        const double lh = 21, hdrH = 30;
        const int PerLine = 8;
        double cw = Make("M", FS, _theme.Value).Width;
        double vw = Make("0", VS, _theme.Value).Width;
        var groups = Groups();
        int nameChars = 4, valChars = 2, unitChars = 3;
        double maxS = 1;
        foreach (var r in groups)
            for (int i = 0; i < r.Cells.Length; i++)
            {
                var c = r.Cells[i];
                if (IsGrid(c)) continue;
                nameChars = Math.Max(nameChars, Math.Min(26, c.Name.Length));
                valChars = Math.Max(valChars, HeldLength(r, c, 2));
                unitChars = Math.Max(unitChars, c.LongUnit.Length);
                maxS = Math.Max(maxS, c.Scale);
            }
        var (_, _, _, barsW, headW) = RowWidths(groups, FS, VS, US, FS, PerLine, 12, true);
        double colW = Math.Max(Math.Max(((nameChars + 3) * cw + valChars * vw + (1 + unitChars) * cw) * maxS, barsW), headW + 3 * cw);
        double graphH = graph ? GraphHt + 10 : 0;

        // Una sola colonna fino all'altezza utile dello schermo; oltre, colonne bilanciate tra i componenti.
        double colH = Math.Max(6 * lh, MaxPanelTall - 2 * P - TopH - graphH);
        int cols = LastColumns = FlowBalanced(groups, colH, lh, hdrH, PerLine, out double colsH);
        double width = 2 * P + cols * colW + (cols - 1) * ColGap;
        double height = P + TopH + colsH + graphH + P - 6;
        var size = new Size(width, height);
        if (dc == null) return size;

        // "$ framegrip  Cyberpunk 2077 DX12" e una riga
        var accent = _theme.Label("GPU");
        PutFixed(dc, Make("$", FS, accent), P, P + 12);
        var app = Make("framegrip", FS, _theme.Value);
        PutFixed(dc, app, P + 2 * cw, P + 12);
        if (HasTitle()) TitleLine(dc, P + 2 * cw + app.Width + 2 * cw, P - 1, width - 2 * P - 14 * cw);
        if (Draws(false)) Line(dc, HeaderPen(accent), new Point(6, P + 20.5), new Point(width - 6, P + 20.5));

        double top = P + TopH - 8;
        // i puntini: tenui con lo sfondo; senza, nel colore del componente e ben visibili (hanno il contorno dei testi)
        var leader = _bare ? null : Dim(_theme.Unit, 80);
        foreach (var p in _placed)
        {
            var r = p.Row;
            double x = P + p.Col * (colW + ColGap), y = top + p.Y;
            var color = LabelBrush(r);
            if (p.Cell < 0)
            {
                double hs = FS * r.Scale, hl = lh * Math.Max(1, r.Scale);
                var head = Make(HeaderText(r, p.Cont, true), hs, color);
                Probe(ProbeHeads, hs);
                double hb = Baseline(y + p.H - hl, hl, hs);
                PutFixed(dc, head, x, hb);
                if (Draws(false) && head.Width + 6 < colW)
                {
                    double ly = Math.Round(hb - hs * 0.35) + 0.5;
                    Line(dc, HeaderPen(color), new Point(x + head.Width + 6, ly), new Point(x + colW, ly));
                }
                continue;
            }
            var c = r.Cells[p.Cell];
            if (IsGrid(c))
            {
                double gs = c.Scale;
                BarLines(dc, r, c, x, y + 1, lh * gs, FS * gs, _theme.Unit, 6 * cw * gs, 9 * gs, 3 * gs, 12 * gs, PerLine, true);
                continue;
            }
            double cs = c.Scale;
            double b = Baseline(y, p.H, VS * cs);
            var name = Make(Lower(c.Name), FS * cs, Dim(color, 230));
            PutFixed(dc, name, x, b);
            double ux = x + colW - unitChars * cw * cs;
            double slot = Slot(r, c, VS * cs, 2);
            PutValue(dc, c, VS * cs, ux - cw * 0.6, b);
            PutFixed(dc, Make(Lower(c.LongUnit), US * cs, _theme.Unit), ux, b);
            int nd = (int)((ux - cw * 0.6 - slot - 4 - (x + name.Width + 4)) / cw);
            if (nd > 0) PutFixed(dc, Make(Dots(nd), FS, leader ?? Dim(color, 175)), x + name.Width + 4, b);
        }
        if (graph)
        {
            double gy = top + colsH + 8;
            PutFixed(dc, Make(Lower(OverlayModel.FrameName("ft")), FS, _theme.Unit), P, gy + 14);
            DrawGraph(dc, new Rect(P + 11 * cw, gy, width - 2 * P - 11 * cw, GraphHt), false);
        }
        return size;
    }
}
