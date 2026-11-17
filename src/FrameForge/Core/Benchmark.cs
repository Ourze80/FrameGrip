using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FrameGrip.Core;

public sealed record BenchmarkResult(
    string Game, DateTime Started, TimeSpan Duration, int Frames,
    double AvgFps, double Low1Fps, double Low01Fps, double MinFps,
    double P99Ms, double JitterMs, int Stutters, double PacingPct,
    string CsvPath, string SummaryPath);

/// <summary>
/// Registra una sessione di gioco: ogni singolo frame più l'andamento dei sensori.
/// Alla fine scrive un CSV con tutti i frametime e un riepilogo leggibile.
/// </summary>
public sealed class Benchmark
{
    private sealed class SensorStat
    {
        public required string Label;
        public required string Type;
        public double Sum, Min = double.MaxValue, Max = double.MinValue;
        public int Count;
    }

    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, SensorStat> _sensors = new();
    private FrameStats?[] _sources = Array.Empty<FrameStats?>();
    private string _game = "";
    private DateTime _started;

    public bool IsRecording { get; private set; }
    /// <summary>Il gioco che si sta registrando (resta lo stesso anche se il primo piano cambia).</summary>
    public string Game => _game;
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>
    /// Cartelle in cui provare a salvare, in ordine. Documenti può essere bloccata da Windows
    /// ("Accesso alle cartelle controllato"): in quel caso si ripiega sui dati locali dell'app.
    /// </summary>
    private static readonly string[] Folders =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FrameGrip", "Benchmark"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameGrip", "Benchmark"),
    };

    /// <summary>Cartella dell'ultimo salvataggio riuscito (o la preferita, se non si è ancora salvato).</summary>
    public static string Folder { get; private set; } = Folders[0];

    /// <param name="sources">Le misure dei frame disponibili per il gioco, in ordine di preferenza.</param>
    public void Start(string game, params FrameStats?[] sources)
    {
        _game = game;
        _sources = sources;
        _sensors.Clear();
        foreach (var s in sources) s?.BeginRecording();
        _started = DateTime.Now;
        _clock.Restart();
        IsRecording = true;
    }

    /// <summary>Da chiamare una volta al secondo durante la registrazione.</summary>
    public void Sample(string id, string label, string type, float? value)
    {
        if (!IsRecording || value is not { } v || float.IsNaN(v)) return;
        if (!_sensors.TryGetValue(id, out var stat)) _sensors[id] = stat = new SensorStat { Label = label, Type = type };
        stat.Sum += v;
        stat.Count++;
        stat.Min = Math.Min(stat.Min, v);
        stat.Max = Math.Max(stat.Max, v);
    }

    public BenchmarkResult? Stop()
    {
        if (!IsRecording) return null;
        IsRecording = false;
        _clock.Stop();

        // Si usa la prima misura che ha davvero dei frame (quella interna al gioco, se c'è).
        List<float> frames = new();
        foreach (var s in _sources)
        {
            var recorded = s?.EndRecording();
            if (frames.Count == 0 && recorded is { Count: > 0 }) frames = recorded;
        }
        _sources = Array.Empty<FrameStats?>();
        if (frames.Count < 2) return new BenchmarkResult(_game, _started, _clock.Elapsed, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "");

        int n = frames.Count;
        double sum = 0, jitterSum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += frames[i];
            if (i > 0) jitterSum += Math.Abs(frames[i] - frames[i - 1]);
        }
        double mean = sum / n, jitter = jitterSum / (n - 1);
        var sorted = frames.ToArray();
        Array.Sort(sorted);
        double median = sorted[n / 2];
        double limit = Math.Max(median * 2, median + 4);
        int stutters = sorted.Count(f => f > limit);

        var result = new BenchmarkResult(_game, _started, TimeSpan.FromMilliseconds(sum), n,
            AvgFps: 1000.0 / mean,
            Low1Fps: 1000.0 / WorstMean(sorted, 0.01),
            Low01Fps: 1000.0 / WorstMean(sorted, 0.001),
            MinFps: 1000.0 / sorted[n - 1],
            P99Ms: sorted[Math.Min(n - 1, (int)(n * 0.99))],
            JitterMs: jitter,
            Stutters: stutters,
            PacingPct: Math.Clamp(100.0 * (1.0 - jitter / mean), 0, 100),
            CsvPath: "",
            SummaryPath: "");

        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("frame,tempo_ms,frametime_ms\n", n * 24);
        double t = 0;
        for (int i = 0; i < n; i++)
        {
            t += frames[i];
            csv.Append(i + 1).Append(',').Append(t.ToString("0.###", inv)).Append(',')
               .Append(frames[i].ToString("0.###", inv)).Append('\n');
        }
        // Il risultato resta valido anche se il salvataggio fallisce: i percorsi restano vuoti.
        string safeName = string.Concat(_game.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string summary = Describe(result, true);
        foreach (string folder in Folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string stem = Path.Combine(folder, $"{safeName}_{_started:yyyy-MM-dd_HH-mm-ss}");
                File.WriteAllText(stem + ".csv", csv.ToString());
                File.WriteAllText(stem + "_summary.txt", summary, new UTF8Encoding(true)); // con BOM: accenti corretti ovunque
                Folder = folder;
                return result with { CsvPath = stem + ".csv", SummaryPath = stem + "_summary.txt" };
            }
            catch { /* cartella non scrivibile: si prova la successiva */ }
        }
        return result;
    }

    /// <summary>Riepilogo leggibile; con i sensori quando è destinato al file.</summary>
    public string Describe(BenchmarkResult r, bool withSensors)
    {
        var it = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (withSensors)
        {
            sb.AppendLine($"FrameGrip - benchmark of {r.Game}");
            sb.AppendLine($"Started: {r.Started:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
        }
        sb.AppendLine($"Duration          {r.Duration.TotalSeconds.ToString("0.0", it)} s  ({r.Frames} frames)");
        sb.AppendLine($"Average FPS       {r.AvgFps.ToString("0.0", it)}");
        sb.AppendLine($"1% low            {r.Low1Fps.ToString("0.0", it)}");
        sb.AppendLine($"0.1% low          {r.Low01Fps.ToString("0.0", it)}");
        sb.AppendLine($"Minimum FPS       {r.MinFps.ToString("0.0", it)}");
        sb.AppendLine($"Frametime p99     {r.P99Ms.ToString("0.00", it)} ms");
        sb.AppendLine($"Smoothness        {r.PacingPct.ToString("0", it)}%  (jitter {r.JitterMs.ToString("0.00", it)} ms)");
        sb.Append($"Stutter           {r.Stutters}");
        if (withSensors && _sensors.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine("Sensors (average / maximum)");
            foreach (var s in _sensors.Values.Where(s => s.Count > 0))
            {
                var (avg, unit) = SensorFormat.Format(s.Type, (float)(s.Sum / s.Count));
                var (max, _) = SensorFormat.Format(s.Type, (float)s.Max);
                sb.AppendLine($"{s.Label,-34} {avg,8} / {max,-8} {unit}");
            }
        }
        return sb.ToString();
    }

    private static double WorstMean(float[] sortedAsc, double fraction)
    {
        int n = sortedAsc.Length;
        int take = Math.Max(1, (int)Math.Ceiling(n * fraction));
        double s = 0;
        for (int i = n - take; i < n; i++) s += sortedAsc[i];
        return s / take;
    }
}
