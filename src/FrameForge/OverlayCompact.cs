using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Le voci "compattate" degli stili Grid HUD, Console, Dashboard, Anelli e Icone (Lista e Barra restano come sono):
/// - nomi brevi: senza il nome del componente davanti ("CPU Core #1" → "Core 1") e con le parole lunghe abbreviate
///   ("Temperature" → "Temp", "Package" → "Pkg"...); un nome scritto dall'utente vale sempre così com'è;
/// - le serie "una per core" (almeno quattro voci dello stesso tipo che differiscono solo per il numero):
///   il carico diventa una griglia di barrette con il massimo e la media, le altre (frequenze, consumi...)
///   una sola riga con l'intervallo minimo–massimo.
/// Il piano (quali voci si uniscono e come si chiamano) dipende solo dai nomi: si calcola una volta e si tiene;
/// a ogni aggiornamento si rifanno solo i valori riassunti.
/// </summary>
public sealed partial class OverlayView
{
    /// <summary>Solo per le prove: lo schermo da usare per i limiti di grandezza del pannello al posto di quello vero.</summary>
    internal static Size? ScreenOverride;

    // Lo schermo del gioco, se non è il principale (in unità WPF). Lo scrive la finestra principale e lo legge anche
    // il thread che disegna l'immagine per il gioco: un oggetto intero, così non si legge mai a metà.
    private static volatile object? _gameScreen;
    internal static Size? GameScreen { get => _gameScreen as Size?; set => _gameScreen = value; }

    private static double ScreenW => ScreenOverride?.Width ?? GameScreen?.Width ?? SystemParameters.PrimaryScreenWidth;
    private static double ScreenH => ScreenOverride?.Height ?? GameScreen?.Height ?? SystemParameters.PrimaryScreenHeight;

    // ------------------------------------------------------------ righe mostrate dagli stili compatti

    /// <summary>Tipo della voce segnaposto che negli stili sta al posto delle voci per core: lì vanno le barrette.</summary>
    internal const string GridKind = "§grid";

    private static bool IsGrid(in Cell c) => ReferenceEquals(c.Kind, GridKind) || c.Kind == GridKind;

    private List<Row>? _shown;
    private List<Row>? _shownFor;

    /// <summary>Le righe con nomi brevi e serie riassunte (rifatte solo quando cambiano le righe).</summary>
    private List<Row> Shown
    {
        get
        {
            if (_shown == null || !ReferenceEquals(_shownFor, _rows)) { _shown = BuildShown(_rows); _shownFor = _rows; }
            return _shown;
        }
    }

    private void AppendShownSignature(StringBuilder sb)
    {
        foreach (var row in Shown)
        {
            if (row.Tag is "API" or "REC" or "FPS") continue;
            sb.Append('\n');
            foreach (var c in row.Cells) sb.Append(c.Name).Append('|').Append(HeldLength(row, c, 1)).Append('\t');
            if (row.Grid != null) sb.Append("grid").Append(row.Grid.Length);
        }
    }

    private sealed class PlanItem
    {
        public int Index;            // voce singola, o la prima della serie
        public int[]? Members;       // serie: le voci riunite
        public bool Bars;            // serie del carico: griglia di barrette + massimo e media
        public string Name = "", Name2 = "", Stem = "";
    }

    private sealed class RowPlan
    {
        public string[] Short = Array.Empty<string>();
        public readonly List<PlanItem> Items = new();
        public int OutCount;
        public int[]? Grid;
        public string[]? GridNames;
    }

    private readonly Dictionary<int, RowPlan> _plans = new();

    private List<Row> BuildShown(List<Row> rows)
    {
        var shown = new List<Row>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Tag is "API" or "REC" or "FPS" || row.Cells.Length == 0) { shown.Add(row); continue; }
            var plan = PlanFor(row);
            var cells = new Cell[plan.OutCount];
            int o = 0;
            foreach (var it in plan.Items)
            {
                if (it.Members == null)
                {
                    var c = row.Cells[it.Index];
                    cells[o++] = c.Custom ? c : c with { Name = plan.Short[it.Index] };
                    continue;
                }
                // minimo, massimo e media della serie
                int lo = -1, hi = -1, n = 0;
                double sum = 0;
                foreach (int m in it.Members)
                {
                    double v = row.Cells[m].Num;
                    if (double.IsNaN(v)) continue;
                    if (lo < 0 || v < row.Cells[lo].Num) lo = m;
                    if (hi < 0 || v > row.Cells[hi].Num) hi = m;
                    sum += v; n++;
                }
                var first = row.Cells[it.Members[0]];
                if (it.Bars)
                {
                    var peak = hi >= 0 ? row.Cells[hi] : first;
                    cells[o++] = first with { Name = it.Name, Value = hi >= 0 ? peak.Value : "—", Num = hi >= 0 ? peak.Num : double.NaN, Key = "", Custom = false };
                    double avg = n > 0 ? sum / n : double.NaN;
                    string avgText = n > 0 ? SensorFormat.Format(first.Kind, (float)avg).Value : "—";
                    cells[o++] = first with { Name = it.Name2, Value = avgText, Num = avg, Key = "", Custom = false };
                    // al posto delle voci per core, nello stesso punto dell'ordine: le barrette (vedi Row.Grid)
                    cells[o++] = first with { Name = it.Stem, Value = "", Num = double.NaN, Kind = GridKind, Key = "", Custom = false };
                }
                else
                {
                    string value = lo < 0 ? "—" : row.Cells[lo].Value == row.Cells[hi].Value ? row.Cells[lo].Value
                        : row.Cells[lo].Value + "–" + row.Cells[hi].Value;
                    cells[o++] = first with { Name = it.Name, Value = value, Num = hi >= 0 ? row.Cells[hi].Num : double.NaN, Key = "", Custom = false };
                }
            }
            Cell[]? grid = null;
            if (plan.Grid is { } g)
            {
                grid = new Cell[g.Length];
                for (int k = 0; k < g.Length; k++) grid[k] = row.Cells[g[k]] with { Name = plan.GridNames![k] };
            }
            shown.Add(row with { Cells = cells, Grid = grid });
        }
        return shown;
    }

    private RowPlan PlanFor(Row row)
    {
        var hc = new HashCode();
        hc.Add(row.Label); hc.Add(row.Tag);
        foreach (var c in row.Cells) { hc.Add(c.Name); hc.Add(c.Kind); hc.Add(c.Custom); }
        int key = hc.ToHashCode();
        if (_plans.TryGetValue(key, out var plan)) return plan;
        if (_plans.Count > 64) _plans.Clear();
        return _plans[key] = MakePlan(row);
    }

    private static readonly Regex Numbered = new(@"^(.*?)(\d+)(\D*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static RowPlan MakePlan(Row row)
    {
        int n = row.Cells.Length;
        var plan = new RowPlan { Short = new string[n] };
        for (int i = 0; i < n; i++) plan.Short[i] = row.Cells[i].Custom ? row.Cells[i].Name : ShortName(row.Cells[i].Name, row.Label, row.Tag);

        // Nomi uguali nello stesso componente (per esempio "GPU Core" carico, temperatura e frequenza):
        // si aggiunge il tipo a quelli che non sono un carico, poi un numero se servisse ancora.
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                if (row.Cells[i].Custom) continue;
                bool dup = false;
                for (int j = 0; j < n && !dup; j++) dup = j != i && string.Equals(plan.Short[j], plan.Short[i], StringComparison.OrdinalIgnoreCase);
                if (!dup) continue;
                if (pass == 0) { if (KindWord(row.Cells[i].Kind) is { } w) plan.Short[i] += " " + w; }
                else
                {
                    int k = 1;
                    for (int j = 0; j < i; j++) if (string.Equals(plan.Short[j], plan.Short[i], StringComparison.OrdinalIgnoreCase)) k++;
                    if (k > 1) plan.Short[i] += " " + k;
                }
            }

        // Le serie: stesso tipo e stesso nome a parte un numero, almeno quattro. La prima voce del componente
        // resta sempre sola (è il valore principale negli stili che ne mostrano uno grande).
        var series = new Dictionary<(string, string, string), List<int>>();
        var parts = new (string P, string N, string S)?[n];
        for (int i = 1; i < n; i++)
        {
            if (row.Cells[i].Custom) continue;
            var m = Numbered.Match(plan.Short[i]);
            if (!m.Success) continue;
            parts[i] = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            var k = (m.Groups[1].Value, m.Groups[3].Value, row.Cells[i].Kind);
            if (!series.TryGetValue(k, out var list)) series[k] = list = new List<int>();
            list.Add(i);
        }
        var memberOf = new Dictionary<int, List<int>>();
        foreach (var list in series.Values)
            if (list.Count >= 4) foreach (int i in list) memberOf[i] = list;

        bool gridTaken = false;
        var done = new HashSet<List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (!memberOf.TryGetValue(i, out var list)) { plan.Items.Add(new PlanItem { Index = i }); plan.OutCount++; continue; }
            if (!done.Add(list)) continue;
            var (p, _, s) = parts[list[0]]!.Value;
            string stem = p.Trim();
            string lo = parts[list[0]]!.Value.N, hi = parts[list[^1]]!.Value.N;
            bool bars = !gridTaken && row.Cells[i].Kind == "Load" && s.Trim().Length == 0;
            if (bars)
            {
                gridTaken = true;
                string plural = stem.EndsWith('s') ? stem : stem + "s";
                plan.Items.Add(new PlanItem { Index = i, Members = list.ToArray(), Bars = true, Name = plural + " peak", Name2 = plural + " mean", Stem = plural });
                plan.OutCount += 3;
                plan.Grid = list.ToArray();
                plan.GridNames = list.Select(m => parts[m]!.Value.N).ToArray();
            }
            else
            {
                plan.Items.Add(new PlanItem { Index = i, Members = list.ToArray(), Name = (stem + " " + lo + "–" + hi + s).Trim() });
                plan.OutCount++;
            }
        }
        return plan;
    }

    private static string? KindWord(string kind) => kind switch
    {
        "Load" => null,
        "Temperature" => "temp",
        "Clock" => "clk",
        "Power" => "pwr",
        "Data" or "SmallData" => "used",
        "Fan" => "fan",
        "Voltage" => "volt",
        "Control" => "ctrl",
        "Throughput" => "rate",
        _ => kind.Length > 0 ? kind.ToLowerInvariant() : null,
    };

    // prima le frasi (ordine importante), poi le parole singole
    private static readonly (string From, string To)[] Phrases =
    {
        ("Dedicated Memory Used", "ded."), ("Shared Memory Used", "shared"), ("Dedicated Memory", "ded."), ("Shared Memory", "shared"),
        ("Memory Used", "mem used"), ("Memory Available", "mem avail"), ("Memory Controller", "mem ctrl"),
        ("Video Engine", "video"), ("Video Decode", "decode"), ("Video Encode", "encode"), ("Bus Interface", "bus"),
        ("Board Power", "board pwr"), ("(Tctl/Tdie)", "Tctl"), ("Hot Spot", "hotspot"),
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Temperature"] = "temp", ["Package"] = "pkg", ["Frequency"] = "clk", ["Clock"] = "clk", ["Memory"] = "mem",
        ["Power"] = "pwr", ["Usage"] = "load", ["Utilization"] = "load", ["Effective"] = "eff.", ["Average"] = "avg",
        ["Available"] = "avail", ["Used"] = "used", ["Total"] = "total", ["Controller"] = "ctrl", ["Dedicated"] = "ded.",
        ["Security"] = "sec.", ["Maximum"] = "max", ["Minimum"] = "min",
    };

    /// <summary>"CPU Core #1" → "Core 1", "GPU Memory Used" → "Mem used", "Core #2 (Effective)" → "Core 2 eff.".</summary>
    internal static string ShortName(string name, string label, string tag)
    {
        string s = name.Trim();
        foreach (var prefix in new[] { label, tag })
            if (prefix.Length > 0 && s.Length > prefix.Length + 1 && s.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            {
                s = s[(prefix.Length + 1)..];
                break;
            }
        foreach (var (from, to) in Phrases) s = s.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        s = s.Replace("#", "").Replace("(", " ").Replace(")", " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            if (Words.TryGetValue(words[i], out var w)) words[i] = w;
        s = string.Join(' ', words);
        if (s.Length == 0) return name;
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
