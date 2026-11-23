using System.Globalization;
using System.IO;
using System.Text;

namespace FrameGrip.Core;

/// <summary>Un campione al secondo durante la misura: a che frame eravamo e quanto erano carichi GPU, VRAM e CPU.</summary>
public readonly record struct LoadSample(int FrameIndex, float GpuLoad, float VramPct, float MaxCoreLoad);

/// <summary>Una delle tre proposte: il limite mostrato (Cap), quello dei frame veri (BaseCap), cali al minuto.</summary>
public sealed record IdealOption(int Cap, int BaseCap, double DipsPerMin, double PctBelow);

public enum Stability { Excellent, Good, Fair, Poor }

public sealed class IdealInput
{
    public float[] Raw = Array.Empty<float>();
    public int Count;
    public double RefreshHz;
    public bool Vrr;
    public int FgMultiplier = 1;
    public IReadOnlyList<LoadSample> Samples = Array.Empty<LoadSample>();
    public double MinCleanSec = 60;
    public int MinFrames = 4000;
    public double WarmupMs = 5000;
}

public sealed class IdealResult
{
    public bool TooShort;
    public double CleanSec, TotalSec;
    public int CleanFrames, FgMultiplier = 1;
    public IdealOption Recommended = null!, Balanced = null!, Max = null!;
    public double Ceiling;            // tetto dello schermo (FPS mostrati)
    public bool ScreenIsLimit;        // il consigliato è il tetto dello schermo
    public double AvgFps, Low1Fps, Low01Fps, BaseAvgFps;   // FPS mostrati (frame veri × moltiplicatore)
    public double P50, P95, P99, P999;                     // ms dei frame veri
    public double StabilityValue;
    public Stability Stability;
    public bool Below30;
    public string? Bound;             // "gpu", "cpu" o null
    public double BoundLoad;
    public int Dips, DipsVram, DipsCpu, DipsUnclear;       // cali al limite consigliato; le cause contano anche gli scatti brevi
    public int Hitches;               // scatti brevi del gioco: nessun limite li toglie, non contano nei cali al minuto
    public float[] Graph = Array.Empty<float>();           // ms dei frame veri, massimo per tratto
    public float[] Marks = Array.Empty<float>();           // posizione dei cali (0..1); negativa = calo forte
}

/// <summary>
/// L'analisi del "limite ideale": dagli intervalli tra un frame e l'altro registrati mentre si gioca trova il limite
/// di FPS più alto che il PC tiene senza cali. Tutto è calcolato qui, fuori dal thread dell'interfaccia.
/// </summary>
public static class IdealLimit
{
    public const int GraphPoints = 240;
    private const double GapMs = 500, GapMarginMs = 1000, DipFactor = 1.25;
    // Calo "vero" (un limite più basso lo toglie): almeno 4 frame lenti di fila oppure 50 ms in tutto.
    private const int DipMinFrames = 4;
    private const double DipMinMs = 50;
    // Scatto breve del gioco: al massimo 3 frame sopra 1,5 volte la mediana, il più lungo oltre 2 volte la mediana
    // (cioè più lento anche di un limite a metà degli FPS mediani): un limite non lo toglie.
    private const int HitchMaxFrames = 3;
    private const double HitchRunFactor = 1.5, HitchFactor = 2.0;

    public static IdealResult Analyze(IdealInput input)
    {
        var res = new IdealResult { FgMultiplier = Math.Max(1, input.FgMultiplier) };
        int n = Math.Min(input.Count, input.Raw.Length);
        var raw = input.Raw;
        int m = res.FgMultiplier;

        // 1) Tempo di ogni frame, pause e primi secondi da scartare.
        var end = new double[n];
        double t = 0;
        for (int i = 0; i < n; i++) { t += raw[i]; end[i] = t; }
        res.TotalSec = t / 1000;
        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = end[i] - raw[i] >= input.WarmupMs && raw[i] <= GapMs;
        // ogni pausa (> 500 ms) toglie anche un secondo prima e uno dopo: caricamenti e menu sporcano i frame vicini
        for (int i = 0; i < n; i++)
        {
            if (raw[i] <= GapMs) continue;
            double from = end[i] - raw[i] - GapMarginMs, to = end[i] + GapMarginMs;
            for (int j = i - 1; j >= 0 && end[j] > from; j--) keep[j] = false;
            for (int j = i + 1; j < n && end[j] - raw[j] < to; j++) keep[j] = false;
        }

        // 2) Frame veri: con la frame generation si sommano i fotogrammi a gruppi (vero + generati).
        var baseFt = new List<float>(n / m + 1);
        var baseRaw = new List<int>(n / m + 1);     // indice grezzo di ogni frame vero (per i campioni di carico)
        var segStart = new List<bool>(n / m + 1);   // primo frame di un tratto pulito (i cali non si uniscono tra tratti)
        bool newSeg = true;
        for (int i = 0; i < n;)
        {
            if (!keep[i]) { newSeg = true; i++; continue; }
            int k = 0; double sum = 0;
            while (k < m && i + k < n && keep[i + k]) { sum += raw[i + k]; k++; }
            if (k < m) { i += Math.Max(1, k); newSeg = true; continue; }
            baseFt.Add((float)sum); baseRaw.Add(i); segStart.Add(newSeg);
            newSeg = false;
            i += m;
        }
        int bn = baseFt.Count;
        double cleanMs = 0;
        foreach (var f in baseFt) cleanMs += f;
        res.CleanSec = cleanMs / 1000;
        res.CleanFrames = bn;
        if (res.CleanSec < input.MinCleanSec || bn < input.MinFrames || bn < 10)
        {
            res.TooShort = true;
            res.Recommended = res.Balanced = res.Max = new IdealOption(0, 0, 0, 0);
            return res;
        }

        // 3) Percentili, medie, stabilità.
        var sorted = baseFt.ToArray();
        Array.Sort(sorted);
        double Pct(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * bn) - 1, 0, bn - 1)];
        res.P50 = Pct(0.50); res.P95 = Pct(0.95); res.P99 = Pct(0.99); res.P999 = Pct(0.999);
        res.BaseAvgFps = bn * 1000 / cleanMs;
        res.AvgFps = res.BaseAvgFps * m;
        res.Low1Fps = 1000 / WorstMean(sorted, 0.01) * m;
        res.Low01Fps = 1000 / WorstMean(sorted, 0.001) * m;
        res.StabilityValue = (res.P99 - res.P50) / res.P50;
        res.Stability = res.StabilityValue <= 0.25 ? Stability.Excellent : res.StabilityValue <= 0.45 ? Stability.Good
            : res.StabilityValue <= 0.8 ? Stability.Fair : Stability.Poor;
        res.Below30 = res.AvgFps < 30;

        // 3b) Scatti brevi del gioco (shader che si compilano, caricamenti in streaming...): si contano a parte.
        var hitch = new bool[bn];
        var hitchAt = new List<int>();
        for (int i = 0; i < bn;)
        {
            if (baseFt[i] <= HitchRunFactor * res.P50) { i++; continue; }
            int j = i, top = i;
            while (j < bn && baseFt[j] > HitchRunFactor * res.P50 && (j == i || !segStart[j]))
            {
                if (baseFt[j] > baseFt[top]) top = j;
                j++;
            }
            if (j - i <= HitchMaxFrames && baseFt[top] > HitchFactor * res.P50)
            {
                for (int k = i; k < j; k++) hitch[k] = true;
                hitchAt.Add(top);
            }
            i = j;
        }
        res.Hitches = hitchAt.Count;
        double hitchMs = HitchFactor * res.P50;

        // 4) Le tre proposte: si "rigioca" la misura con ogni limite e si contano i cali al minuto.
        double refresh = input.RefreshHz > 0 ? input.RefreshHz : 0;
        res.Ceiling = refresh <= 0 ? 500 : input.Vrr ? refresh - 3 : refresh;
        int maxBase = Math.Max(20, (int)Math.Floor(res.Ceiling / m));
        double minutes = res.CleanSec / 60;
        var dpm = new double[maxBase + 1];
        for (int c = 20; c <= maxBase; c++) dpm[c] = CountDips(baseFt, segStart, hitch, hitchMs, 1000.0 / c, null) / minutes;
        // Quello che il gioco regge davvero (FPS mostrati): un limite più alto non si raggiunge e non serve a niente.
        double hold95 = 1000.0 * m / res.P95, hold90 = 1000.0 * m / Pct(0.90);
        IdealOption Option(int cap)
        {
            int baseCap = Math.Max(1, cap / m);
            double d = baseCap <= maxBase && baseCap >= 20 && baseCap * m == cap ? dpm[baseCap]
                : CountDips(baseFt, segStart, hitch, hitchMs, 1000.0 * m / cap, null) / minutes;
            return new IdealOption(cap, baseCap, d, BelowShare(sorted, 1000.0 * m / cap));
        }
        int Pick(double threshold, double hold)
        {
            int best = 20;
            for (int c = maxBase; c >= 20; c--) if (dpm[c] <= threshold) { best = c; break; }
            int cap = best * m;
            int limit = (int)Math.Min(Math.Floor(hold), Math.Floor(res.Ceiling));
            cap = Math.Min(cap, limit);
            // senza VRR si arrotonda allo schermo (o a metà, un terzo...), ma mai sopra quello che il gioco regge
            if (!input.Vrr && refresh > 0) cap = Snap(cap, refresh, limit);
            if (m > 1) cap -= cap % m;   // con la frame generation: un multiplo dei frame veri
            return Math.Max(20, cap);
        }
        int rec = Pick(1.0, hold95), bal = Pick(3.0, hold90), max = Pick(15.0, double.MaxValue);
        // ordine sempre crescente (due possono coincidere: l'interfaccia lo dice)
        bal = Math.Max(bal, rec);
        max = Math.Max(max, bal);
        res.Recommended = Option(rec);
        res.Balanced = Option(bal);
        res.Max = Option(max);
        // "lo schermo è il limite" solo se il gioco regge davvero il tetto dello schermo
        res.ScreenIsLimit = refresh > 0 && hold95 >= Math.Floor(res.Ceiling) && rec >= Math.Floor(res.Ceiling) - m;

        // 5) Chi limita: GPU quasi sempre piena, oppure un core della CPU al massimo.
        var smp = input.Samples;
        if (smp.Count > 0)
        {
            double gpu = 0, cpu = 0; int ng = 0, nc = 0;
            foreach (var s in smp)
            {
                if (s.GpuLoad >= 0) { gpu += s.GpuLoad; ng++; }
                if (s.MaxCoreLoad >= 0) { cpu += s.MaxCoreLoad; nc++; }
            }
            if (ng > 0 && gpu / ng >= 95) { res.Bound = "gpu"; res.BoundLoad = gpu / ng; }
            else if (nc > 0 && cpu / nc >= 95) { res.Bound = "cpu"; res.BoundLoad = cpu / nc; }
        }

        // 6) Cause dei cali al limite consigliato (prova): VRAM quasi piena, un core al 100%, altrimenti non chiaro.
        var dipAt = new List<int>();
        CountDips(baseFt, segStart, hitch, hitchMs, 1000.0 * m / res.Recommended.Cap, dipAt);
        res.Dips = dipAt.Count;
        var causeAt = new List<int>(dipAt);
        causeAt.AddRange(hitchAt);
        causeAt.Sort();
        var marks = new List<float>();
        double run = 0;
        var startOf = new double[bn];
        for (int i = 0; i < bn; i++) { startOf[i] = run; run += baseFt[i]; }
        foreach (int b in causeAt)
        {
            var s = SampleAt(smp, baseRaw[b]);
            if (s is { VramPct: > 95 }) res.DipsVram++;
            else if (s is { MaxCoreLoad: >= 98 }) res.DipsCpu++;
            else res.DipsUnclear++;
            if (marks.Count < 80)
            {
                float x = (float)(startOf[b] / cleanMs);
                marks.Add(baseFt[b] > 2 * 1000.0 * m / res.Recommended.Cap ? -Math.Max(x, 1e-4f) : x);
            }
        }
        res.Marks = marks.ToArray();

        // 7) Il grafico: il frame più lento di ogni tratto (si vedono i cali anche ridotto a 240 punti).
        var g = new float[GraphPoints];
        for (int i = 0; i < bn; i++)
        {
            int k = Math.Min(GraphPoints - 1, (int)(startOf[i] / cleanMs * GraphPoints));
            if (baseFt[i] > g[k]) g[k] = baseFt[i];
        }
        for (int k = 1; k < GraphPoints; k++) if (g[k] == 0) g[k] = g[k - 1];
        res.Graph = g;
        return res;
    }

    /// <summary>Senza VRR: lo schermo, la sua metà, un terzo o un quarto, se il valore è entro l'8%; altrimenti resta.</summary>
    public static int Snap(int cap, double refresh) => Snap(cap, refresh, int.MaxValue);

    /// <summary>Come sopra, ma senza mai salire oltre limit.</summary>
    public static int Snap(int cap, double refresh, int limit)
    {
        foreach (int d in new[] { 1, 2, 3, 4 })
        {
            double v = Math.Floor(refresh / d);
            if (Math.Abs(cap - v) <= 0.08 * v && v <= limit) return (int)v;
        }
        return cap;
    }

    /// <summary>
    /// Cali con un limite che dà un frame ogni capMs: frame più lenti di 1,25 volte quell'intervallo; più frame lenti
    /// di fila sono un calo solo. Con il limite i frame più veloci aspettano, quelli lenti restano come sono.
    /// </summary>
    /// <remarks>
    /// Conta solo i cali sostenuti: almeno 4 frame oltre la soglia, oppure 50 ms in tutto purché nessun frame sia uno
    /// scatto (oltre hitchMs): 1-3 frame lunghissimi sono uno scatto del gioco anche dentro una scena pesante.
    /// </remarks>
    private static int CountDips(List<float> ft, List<bool> segStart, bool[] hitch, double hitchMs, double capMs, List<int>? at)
    {
        double th = DipFactor * capMs;
        int dips = 0, runLen = 0, runStart = 0;
        double runMs = 0, runMax = 0;
        bool counted = false;
        for (int i = 0; i < ft.Count; i++)
        {
            if (segStart[i]) { runLen = 0; runMs = 0; runMax = 0; counted = false; }
            if (hitch[i]) continue;   // gli scatti brevi non sono cali: non contano e non spezzano un calo in corso
            if (ft[i] > th)
            {
                if (runLen == 0) { runStart = i; counted = false; }
                runLen++; runMs += ft[i]; runMax = Math.Max(runMax, ft[i]);
                if (!counted && (runLen >= DipMinFrames || (runMs >= DipMinMs && runMax <= hitchMs))) { dips++; at?.Add(runStart); counted = true; }
            }
            else { runLen = 0; runMs = 0; runMax = 0; counted = false; }
        }
        return dips;
    }

    private static double WorstMean(float[] sorted, double share)
    {
        int k = Math.Max(1, (int)Math.Round(sorted.Length * share));
        double s = 0;
        for (int i = sorted.Length - k; i < sorted.Length; i++) s += sorted[i];
        return s / k;
    }

    private static double BelowShare(float[] sorted, double ms)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (sorted[mid] <= ms) lo = mid + 1; else hi = mid; }
        return 100.0 * lo / sorted.Length;
    }

    private static LoadSample? SampleAt(IReadOnlyList<LoadSample> s, int rawIndex)
    {
        if (s.Count == 0) return null;
        LoadSample best = s[0];
        foreach (var x in s) { if (x.FrameIndex <= rawIndex) best = x; else break; }
        return best;
    }

    // ------------------------------------------------------------ prova su tracce finte

    /// <summary>Solo per le prove (FRAMEGRIP_IDEAL_SELFTEST): tracce note e risultati attesi, scritti in un file.</summary>
    public static bool SelfTest(string file)
    {
        var sb = new StringBuilder();
        bool allOk = true;
        void Check(string name, bool ok, string detail)
        {
            allOk &= ok;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        }
        string Desc(IdealResult r) => r.TooShort ? $"too short (clean {r.CleanSec:0.0} s, {r.CleanFrames} frames)" :
            string.Create(CultureInfo.InvariantCulture,
                $"rec {r.Recommended.Cap} ({r.Recommended.DipsPerMin:0.00}/min) bal {r.Balanced.Cap} ({r.Balanced.DipsPerMin:0.00}/min) max {r.Max.Cap} ({r.Max.DipsPerMin:0.00}/min) avg {r.AvgFps:0.0} low1 {r.Low1Fps:0.0} P50 {r.P50:0.00} P99 {r.P99:0.00} stab {r.Stability} clean {r.CleanSec:0.0}s screenLimit {r.ScreenIsLimit}");
        var rnd = new Random(3);

        // 1) Frame regolari a 144 FPS, schermo 165 Hz con VRR: tutto sta sotto il tetto (162).
        var a = Trace(180, ms => 6.94 + (rnd.NextDouble() - 0.5) * 0.4);
        var r1 = Analyze(new IdealInput { Raw = a, Count = a.Length, RefreshHz = 165, Vrr = true });
        Check("steady 144 fps, 165 Hz VRR: rec at what the game holds", r1.Recommended.Cap is >= 139 and <= 144 && r1.Max.Cap == 162 && !r1.ScreenIsLimit && r1.Stability == Stability.Excellent, Desc(r1));
        var r1b = Analyze(new IdealInput { Raw = a, Count = a.Length, RefreshHz = 120, Vrr = true });
        Check("steady 144 fps on 120 Hz VRR: screen is the limit", r1b.Recommended.Cap == 117 && r1b.ScreenIsLimit, Desc(r1b));
        var r1c = Analyze(new IdealInput { Raw = a, Count = a.Length, RefreshHz = 144, Vrr = false });
        Check("steady ~144 fps, 144 Hz no VRR: snap never rounds up", r1c.Recommended.Cap <= 1000.0 / r1c.P95 && r1c.Recommended.Cap >= 135, Desc(r1c));

        // 2) Cali sostenuti noti: 7 ms con 8 frame da 10 ms ogni 5 s (12/min), 8 da 14 ms ogni 30 s (2/min), 8 da 25 ms
        //    ogni 60 s (1/min). Senza VRR a 144 Hz: consigliato 89 (restano solo i 25 ms), equilibrato 125 (3/min), massimo 144 (15/min).
        float[] Steps()
        {
            var l = new List<float>();
            double clock = 0, next10 = 5000, next14 = 30000, next25 = 60000;
            while (clock < 240_000)
            {
                double ft = 7;
                if (clock >= next25) { ft = 25; next25 += 60000; }
                else if (clock >= next14) { ft = 14; next14 += 30000; }
                else if (clock >= next10) { ft = 10; next10 += 5000; }
                for (int k = 0; k < (ft > 7 ? 8 : 1); k++) { l.Add((float)ft); clock += ft; }
            }
            return l.ToArray();
        }
        var b = Steps();
        var r2 = Analyze(new IdealInput { Raw = b, Count = b.Length, RefreshHz = 144, Vrr = false });
        Check("periodic dips, 144 Hz no VRR", r2.Recommended.Cap == 89 && r2.Balanced.Cap == 125 && r2.Max.Cap == 144, Desc(r2));
        var r2v = Analyze(new IdealInput { Raw = b, Count = b.Length, RefreshHz = 144, Vrr = true });
        Check("periodic dips, 144 Hz VRR", r2v.Recommended.Cap == 89 && r2v.Balanced.Cap == 125 && r2v.Max.Cap == 141, Desc(r2v));

        // 3) Frame generation x2: frame veri da 14,5 ms consegnati come 14,1 + 0,4 ms. Con x2 i frame veri reggono 86,
        //    il tetto di 165 Hz VRR è 162 = 81 veri × 2; senza il moltiplicatore i "cali" sarebbero ovunque.
        var fg = new List<float>();
        for (double clock = 0; clock < 150_000; clock += 14.5) { fg.Add((float)(14.1 + (rnd.NextDouble() - 0.5) * 0.3)); fg.Add(0.4f); }
        var c = fg.ToArray();
        var r3 = Analyze(new IdealInput { Raw = c, Count = c.Length, RefreshHz = 165, Vrr = true, FgMultiplier = 2 });
        Check("frame generation x2, 165 Hz VRR", r3.Recommended.Cap is >= 132 and <= 138 && r3.Recommended.Cap % 2 == 0 && r3.Recommended.BaseCap * 2 == r3.Recommended.Cap && Math.Abs(r3.BaseAvgFps - 69) < 1.5 && r3.Max.Cap == 162,
            Desc(r3) + $" base avg {r3.BaseAvgFps:0.0}");
        var r3n = Analyze(new IdealInput { Raw = c, Count = c.Length, RefreshHz = 165, Vrr = true, FgMultiplier = 1 });
        Check("same trace without the multiplier looks broken (control)", r3n.Stability == Stability.Poor && r3n.P50 < 1 && r3n.Hitches > 1000, Desc(r3n) + $" hitches {r3n.Hitches}");

        // 4) Pause: 8 ms con tre pause da 3 s (menu): tolte le pause e un secondo per lato, più i primi 5 s.
        var p = new List<float>();
        double ck = 0; int pauses = 0;
        while (ck < 130_000)
        {
            if (pauses < 3 && ck > 30_000 * (pauses + 1)) { p.Add(3000); ck += 3000; pauses++; continue; }
            p.Add(8); ck += 8;
        }
        var d = p.ToArray();
        var r4 = Analyze(new IdealInput { Raw = d, Count = d.Length, RefreshHz = 144, Vrr = false });
        double expect = (130_000 - 9000 - 6000 - 5000) / 1000.0;
        Check("pauses removed", Math.Abs(r4.CleanSec - expect) < 1.0 && r4.Recommended.Cap == 125, Desc(r4) + $" expected clean ≈ {expect:0.0}s");

        // 5) Troppo breve: 50 s di gioco.
        var e = Trace(50, ms => 8);
        var r5 = Analyze(new IdealInput { Raw = e, Count = e.Length, RefreshHz = 144 });
        Check("too short (50 s)", r5.TooShort, Desc(r5));

        // 6) Cause dei cali: VRAM piena nella prima metà.
        var samples = new List<LoadSample>();
        for (int i = 0; i < b.Length; i += 140) samples.Add(new LoadSample(i, 97, i < b.Length / 2 ? 98 : 60, 70));
        var r6 = Analyze(new IdealInput { Raw = b, Count = b.Length, RefreshHz = 144, Samples = samples });
        Check("causes and GPU bound", r6.Bound == "gpu" && r6.DipsVram > 0 && r6.DipsUnclear > 0 && r6.DipsVram + r6.DipsCpu + r6.DipsUnclear == r6.Dips + r6.Hitches,
            $"bound {r6.Bound} {r6.BoundLoad:0} vram {r6.DipsVram} cpu {r6.DipsCpu} unclear {r6.DipsUnclear} of {r6.Dips} dips + {r6.Hitches} hitches");

        // 8) Pochi scatti brevi (uno da 25-30 ms al minuto, come la traccia d'esempio): non abbassano il limite.
        var hr = new Random(5);
        var h1 = new List<float>();
        double hc = 0, nextH = 20_000;
        while (hc < 180_000)
        {
            double ft = 7.0 + hr.NextDouble() * 0.6;
            if (hc >= nextH) { ft = 25 + hr.NextDouble() * 5; nextH += 60_000; }
            h1.Add((float)ft); hc += ft;
        }
        var hh = h1.ToArray();
        var r8 = Analyze(new IdealInput { Raw = hh, Count = hh.Length, RefreshHz = 144, Vrr = false });
        Check("rare hitches keep the cap", r8.Recommended.Cap is >= 128 and <= 136 && r8.Max.Cap == 144 && r8.Hitches >= 2 && r8.Hitches <= 3 && r8.Stability == Stability.Excellent,
            Desc(r8) + $" hitches {r8.Hitches}");
        var r8v = Analyze(new IdealInput { Raw = hh, Count = hh.Length, RefreshHz = 165, Vrr = true });
        Check("rare hitches, 165 Hz VRR", r8v.Recommended.Cap is >= 128 and <= 136 && r8v.Max.Cap == 162, Desc(r8v) + $" hitches {r8v.Hitches}");

        // 9) Tempesta di scatti (shader che si compilano): uno o due frame da 20-40 ms ogni mezzo secondo.
        //    Tanti scatti e stabilità scarsa, ma il limite resta alto perché nessun calo è sostenuto.
        var s9 = new List<float>();
        double sc = 0, nextS = 0;
        while (sc < 180_000)
        {
            if (sc >= nextS)
            {
                int k = 1 + hr.Next(2);
                for (int q = 0; q < k; q++) { float v = (float)(20 + hr.NextDouble() * 20); s9.Add(v); sc += v; }
                nextS = sc + 500;
                continue;
            }
            float f9 = (float)(7.0 + hr.NextDouble() * 0.4); s9.Add(f9); sc += f9;
        }
        var st = s9.ToArray();
        var r9 = Analyze(new IdealInput { Raw = st, Count = st.Length, RefreshHz = 144, Vrr = false });
        Check("hitch storm: many hitches, low stability, cap not crippled", r9.Hitches > 200 && r9.Stability >= Stability.Fair && r9.Recommended.Cap >= 120,
            Desc(r9) + $" hitches {r9.Hitches}");

        // 10) Tempesta più un calo sostenuto (scena pesante: 3 s a 16 ms ogni 30 s): il calo abbassa ancora il consigliato.
        var s10 = new List<float>();
        double c10 = 0;
        for (int i = 0; i < st.Length && c10 < 180_000; i++)
        {
            bool heavy = c10 % 30_000 > 27_000;
            float v = heavy && st[i] < 10 ? 16f : st[i];
            s10.Add(v); c10 += v;
        }
        var t10 = s10.ToArray();
        var r10 = Analyze(new IdealInput { Raw = t10, Count = t10.Length, RefreshHz = 144, Vrr = false });
        Check("storm + sustained dips: sustained dips lower the cap", r10.Recommended.Cap is >= 55 and <= 80 && r10.Max.Cap >= 120, Desc(r10) + $" hitches {r10.Hitches}");

        // 7) Arrotondamento senza VRR.
        Check("snap 140 -> 144", Snap(140, 144) == 144, Snap(140, 144).ToString());
        Check("snap 79 -> 82 (165/2)", Snap(79, 165) == 82, Snap(79, 165).ToString());
        Check("snap 100 stays", Snap(100, 144) == 100, Snap(100, 144).ToString());
        Check("snap 60 at 59.94 Hz -> 59 (never above the screen)", Snap(60, 59.94) == 59, Snap(60, 59.94).ToString());
        Check("snap 59 at 59.94 Hz stays 59 (not 58)", Snap(59, 59.94, 59) == 59, Snap(59, 59.94, 59).ToString());

        sb.AppendLine(allOk ? "ALL PASS" : "SOME FAILED");
        File.WriteAllText(file, sb.ToString());
        return allOk;
    }

    private static float[] Trace(double seconds, Func<double, double> ft)
    {
        var l = new List<float>();
        for (double ms = 0; ms < seconds * 1000;) { float v = (float)ft(ms); l.Add(v); ms += v; }
        return l.ToArray();
    }
}
