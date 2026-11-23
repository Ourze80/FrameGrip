using System.IO;
using System.Text.Json;

namespace FrameGrip.Core;

/// <summary>Una misura del limite ideale salvata: solo il riassunto (niente frame grezzi), per confrontarle.</summary>
public sealed class SavedMeasure
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Game { get; set; } = "";
    public DateTime Date { get; set; }
    public int Recommended { get; set; }
    public int Balanced { get; set; }
    public int Max { get; set; }
    public double DipsRecommended { get; set; }
    public double DipsBalanced { get; set; }
    public double DipsMax { get; set; }
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double P999 { get; set; }
    public double Seconds { get; set; }
    public int FgMultiplier { get; set; } = 1;
    public float[] Graph { get; set; } = Array.Empty<float>();
}

/// <summary>Le misure salvate, in un file a parte nella cartella delle impostazioni (non nel file delle impostazioni,
/// che l'app riscrive spesso). Thread-safe: All restituisce una copia, le modifiche passano da Gate.</summary>
public static class MeasureStore
{
    private static readonly object Gate = new();
    private static List<SavedMeasure>? _all;

    private static string FilePath =>
        Environment.GetEnvironmentVariable("FRAMEGRIP_MEASURES") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameGrip", "measures.json");

    /// <summary>L'elenco caricato (da chiamare sotto Gate). Un file rovinato si tiene da parte e si prova il .bak.</summary>
    private static List<SavedMeasure> Loaded()
    {
        if (_all != null) return _all;
        try { _all = SafeFile.Read(FilePath, json => JsonSerializer.Deserialize<List<SavedMeasure>>(json), "Measures"); }
        catch (Exception ex) { AppLog.Write("Measures: " + ex.Message); }
        return _all ??= new List<SavedMeasure>();
    }

    /// <summary>Copia dell'elenco: si può scorrere mentre un altro thread aggiunge o toglie misure.</summary>
    public static List<SavedMeasure> All
    {
        get { lock (Gate) return new List<SavedMeasure>(Loaded()); }
    }

    public static List<SavedMeasure> ForGame(string exe) =>
        All.Where(m => string.Equals(m.Exe, exe, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Date).ToList();

    /// <summary>Le misure di un gioco visto con più eseguibili (libreria, profilo, cronologia).</summary>
    public static List<SavedMeasure> ForKeys(ICollection<string> exes) =>
        All.Where(m => exes.Contains(m.Exe, StringComparer.OrdinalIgnoreCase)).OrderByDescending(m => m.Date).ToList();

    public static void Add(SavedMeasure m) { lock (Gate) Loaded().Add(m); Save(); }

    public static void Remove(SavedMeasure m) { lock (Gate) Loaded().Remove(m); Save(); }

    public static void Save()
    {
        lock (Gate)
        {
            // .tmp, scrittura forzata su disco e scambio (la versione di prima resta come .bak)
            try { SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(Loaded())); }
            catch (Exception ex) { AppLog.Write("Measures: " + ex.Message); }
        }
    }

    public static SavedMeasure From(IdealResult r, string exe, string game, string name) => new()
    {
        Name = name, Exe = exe, Game = game, Date = DateTime.Now,
        Recommended = r.Recommended.Cap, Balanced = r.Balanced.Cap, Max = r.Max.Cap,
        DipsRecommended = Math.Round(r.Recommended.DipsPerMin, 2), DipsBalanced = Math.Round(r.Balanced.DipsPerMin, 2),
        DipsMax = Math.Round(r.Max.DipsPerMin, 2),
        AvgFps = Math.Round(r.AvgFps, 1), Low1Fps = Math.Round(r.Low1Fps, 1), Low01Fps = Math.Round(r.Low01Fps, 1),
        P50 = Math.Round(r.P50, 3), P95 = Math.Round(r.P95, 3), P99 = Math.Round(r.P99, 3), P999 = Math.Round(r.P999, 3),
        Seconds = Math.Round(r.CleanSec, 1), FgMultiplier = r.FgMultiplier,
        Graph = r.Graph.Select(v => MathF.Round(v, 2)).ToArray(),
    };
}
