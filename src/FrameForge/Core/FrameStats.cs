namespace FrameGrip.Core;

/// <summary>Metriche calcolate sugli ultimi secondi di frame di un processo.</summary>
public sealed record FrameSnapshot(
    double Fps,          // media sull'ultimo secondo
    double AvgFps,       // media sulla finestra (10 s)
    double Low1Fps,      // media dell'1% dei frame peggiori
    double Low01Fps,     // media dello 0,1% dei frame peggiori
    double FrametimeMs,  // frametime medio sull'ultimo secondo
    double P99Ms,        // 99° percentile del frametime
    double JitterMs,     // variazione media tra frame consecutivi
    int Stutters,        // frame oltre il doppio della mediana
    double PacingPct,    // 100 = frame perfettamente regolari
    float[] Graph,       // frametime del grafico, in ordine cronologico
    float[] GraphX);     // posizione di ciascuno nel grafico: 0 = bordo sinistro, 1 = destro (può uscire di poco dai bordi)

/// <summary>Buffer circolare dei frametime di un processo. Thread-safe.</summary>
public sealed class FrameStats
{
    private const int Cap = 8192;
    private const double WindowMs = 10_000;
    /// <summary>Da qui in su l'intervallo tra due frame è uno scatto vero, che però non entra nella stima del ritmo.</summary>
    public const double HitchMs = 1000;
    /// <summary>Da qui in su il gioco non stava presentando frame (pausa, caricamento, alt-tab): l'intervallo non conta.</summary>
    public const double PauseMs = 5000;

    private readonly double[] _ts = new double[Cap];
    private readonly float[] _ft = new float[Cap];
    private readonly object _lock = new();
    private int _head, _count;
    private double _lastTs = -1;
    private long _lastWallMs;

    public long LastWallMs => Interlocked.Read(ref _lastWallMs);

    // Registrazione di un benchmark: tutti i frame, non solo gli ultimi secondi.
    private const int MaxRecorded = 4_000_000;
    private List<float>? _recording;

    public void BeginRecording()
    {
        lock (_lock) _recording = new List<float>(16384);
    }

    public List<float> EndRecording()
    {
        lock (_lock)
        {
            var result = _recording ?? new List<float>();
            _recording = null;
            return result;
        }
    }

    // ---- Misura del "limite ideale" (IdealMeasure) ----
    // Gli intervalli grezzi tra un present e l'altro, pause comprese, in un buffer preparato da chi misura:
    // nessuna allocazione mentre si gioca. La pulizia (pause, raffiche della frame generation) la fa l'analisi.
    private float[]? _measure;
    private int _measureCount;

    public void BeginMeasure(float[] buffer)
    {
        lock (_lock) { _measure = buffer; _measureCount = 0; }
    }

    /// <summary>Riprende una misura già iniziata (dopo "misura troppo breve"), tenendo quello che c'è.</summary>
    public void ResumeMeasure(float[] buffer, int count)
    {
        lock (_lock) { _measure = buffer; _measureCount = Math.Min(count, buffer.Length); }
    }

    public void EndMeasure()
    {
        lock (_lock) _measure = null;
    }

    /// <summary>Vero mentre un benchmark o una misura del limite ideale stanno usando questi frame: non va buttato.</summary>
    public bool InUse
    {
        get { lock (_lock) return _recording != null || _measure != null; }
    }

    /// <summary>
    /// L'orologio dei frame ricomincia (per esempio la sessione ETW è ripartita): il prossimo frame non ha un
    /// intervallo valido rispetto all'ultimo visto, e la differenza tra gli orologi va ricalcolata.
    /// </summary>
    public void Discontinuity()
    {
        lock (_lock)
        {
            _lastRawTs = -1;
            _burstCount = 0;
            _clockOffset = double.NaN;
        }
    }

    /// <summary>Quanti intervalli sono stati scritti nel buffer della misura (si leggono solo quelli prima di questo indice).</summary>
    public int MeasureCount => Volatile.Read(ref _measureCount);

    /// <summary>Con la frame generation riconosciuta: quanti fotogrammi mostrati per ognuno vero (2, 3 o 4), altrimenti 1.</summary>
    public int FrameGenMultiplier
    {
        get
        {
            lock (_lock)
            {
                if (!FrameGeneration || _ringCount == 0) return 1;
                double share = (double)_shortCount / _ringCount;
                return Math.Clamp((int)Math.Round(1 / Math.Max(0.05, 1 - share)), 2, 4);
            }
        }
    }

    // ---- Frame generation (DLSS FG, FSR FG, Lossless Scaling...) ----
    // Il gioco consegna il fotogramma vero e quelli generati uno attaccato all'altro ("a raffica"),
    // poi è la scheda video a distribuirli sullo schermo a intervalli regolari. Misurando le consegne
    // si vedrebbe un ritmo a singhiozzo (17 ms, 0,4 ms, 17 ms...) che sullo schermo non esiste.
    // Quando si riconosce questo ritmo, ogni raffica viene distribuita in parti uguali sul tempo che
    // la separa dalla successiva: è una stima di ciò che si vede, non una misura esatta dello schermo.
    private const int CadenceWindow = 64;
    private readonly bool[] _shortRing = new bool[CadenceWindow];
    private int _ringPos, _ringCount, _shortCount;
    private double _lastRawTs = -1, _longEma;
    private double _burstStart;
    private int _burstCount;

    /// <summary>Vero se negli ultimi fotogrammi si è riconosciuto il ritmo "a raffica" della frame generation.</summary>
    public bool FrameGeneration { get; private set; }

    // ---- Grafico ----
    // Il grafico mostra gli ultimi GraphWindowMs di tempo (non un numero fisso di frame): così a ogni
    // aggiornamento scorre sempre della stessa quantità, a qualunque FPS. Il bordo destro è "adesso"
    // meno un piccolo ritardo (almeno GraphDelayMs), perché i frame arrivano all'app a gruppi (dalla DLL 60 volte al secondo, da ETW anche ogni secondo; con la frame generation
    // una raffica dopo): con quel piccolo ritardo la linea arriva sempre fino al bordo.
    // "Adesso" va espresso nell'orologio dei frame (QPC del gioco o tempo della sessione ETW): la
    // differenza tra i due orologi è la più piccola vista tra arrivo e istante del frame.
    public const double GraphWindowMs = 2500, GraphDelayMs = 60;
    private double _clockOffset = double.NaN, _offsetWall, _batchEma = 100;

    /// <summary>Millisecondi dell'orologio usato per confrontare l'arrivo dei frame con il loro istante.</summary>
    public static double WallMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public void AddPresent(double tsMs) => AddPresent(tsMs, WallMs());

    /// <param name="wallMs">Istante in cui il frame arriva all'app (WallMs); separato solo per le prove.</param>
    public void AddPresent(double tsMs, double wallMs)
    {
        lock (_lock)
        {
            Interlocked.Exchange(ref _lastWallMs, Environment.TickCount64);
            // Il ritardo d'arrivo non può essere negativo: il minimo osservato è la differenza tra gli orologi.
            // Sale piano (1% del tempo trascorso) per seguire un eventuale riavvio dell'orologio dei frame.
            double sample = wallMs - tsMs;
            if (double.IsNaN(_clockOffset) || sample > _clockOffset + 2000) _clockOffset = sample;
            else _clockOffset = Math.Min(_clockOffset + (wallMs - _offsetWall) * 0.01, sample);
            // Ogni quanto arrivano i gruppi di frame: circa 17 ms dalla DLL (letta 60 volte al secondo), fino a circa un secondo da ETW.
            // Il ritardo del grafico si adatta, così la linea non si accorcia tra un gruppo e l'altro.
            double gap = wallMs - _offsetWall;
            if (gap > 5) _batchEma = _batchEma * 0.9 + Math.Min(gap, 1500) * 0.1;
            _offsetWall = wallMs;

            if (_lastRawTs < 0) { _lastRawTs = tsMs; return; }
            double dt = tsMs - _lastRawTs;
            if (dt <= 0) return;
            _lastRawTs = tsMs;
            if (_measure != null && _measureCount < _measure.Length) { _measure[_measureCount] = (float)dt; Volatile.Write(ref _measureCount, _measureCount + 1); }

            if (dt >= PauseMs)
            {
                // Pausa vera (caricamento, alt-tab, gioco ridotto a icona): non è un fotogramma e interrompe la raffica in corso.
                _burstCount = 0;
                return;
            }
            if (dt >= HitchMs)
            {
                // Scatto lungo (1-5 s): il gioco si è bloccato davvero, quindi conta (1%/0,1% low, stutter, benchmark).
                // Interrompe la raffica della frame generation ma non entra nella stima del ritmo.
                _burstCount = 0;
                Emit(tsMs, dt);
                return;
            }

            bool isShort = _longEma > 0 && dt < 0.35 * _longEma;
            // Un singolo scatto (es. 45 ms a 120 FPS veri) non deve gonfiare la stima: altrimenti tutti gli
            // intervalli normali sembrano "corti", la frame generation non si riconosce più e la stima non
            // scende più (si aggiorna solo con gli intervalli lunghi). Per questo un intervallo conta al più 3 volte la stima.
            double longDt = _longEma > 0 ? Math.Min(dt, 3 * _longEma) : dt;
            if (!isShort) _longEma = longDt > _longEma ? (_longEma + longDt) / 2 : _longEma * 0.9 + longDt * 0.1;

            if (_ringCount == CadenceWindow && _shortRing[_ringPos]) _shortCount--;
            _shortRing[_ringPos] = isShort;
            if (isShort) _shortCount++;
            _ringPos = (_ringPos + 1) % CadenceWindow;
            if (_ringCount < CadenceWindow) _ringCount++;

            // x2 = metà degli intervalli è corta, x3 = due terzi, x4 = tre quarti.
            double shortShare = (double)_shortCount / _ringCount;
            bool fg = _ringCount >= 32 && shortShare is >= 0.3 and <= 0.85;
            if (fg != FrameGeneration)
            {
                FrameGeneration = fg;
                if (!fg) FlushBurst();
            }

            if (!fg)
            {
                Emit(tsMs, dt);
            }
            else if (isShort)
            {
                if (_burstCount > 0) _burstCount++;   // fa parte della raffica in corso
                else Emit(tsMs, dt);
            }
            else
            {
                // Comincia una nuova raffica: quella precedente ora ha una durata nota.
                if (_burstCount > 0)
                {
                    double period = tsMs - _burstStart;
                    for (int i = 1; i <= _burstCount; i++)
                        Emit(_burstStart + period * i / _burstCount, period / _burstCount);
                }
                _burstStart = tsMs;
                _burstCount = 1;
            }
        }
    }

    /// <summary>Chiude la raffica rimasta in sospeso quando il ritmo "a raffica" finisce.</summary>
    private void FlushBurst()
    {
        if (_burstCount > 0 && _longEma > 0)
            for (int i = 1; i <= _burstCount; i++)
                Emit(_burstStart + _longEma * i / _burstCount, _longEma / _burstCount);
        _burstCount = 0;
    }

    /// <summary>Registra un fotogramma così come arriva sullo schermo (istante e durata).</summary>
    private void Emit(double tsMs, double dt)
    {
        if (dt <= 0) return;
        _ts[_head] = tsMs;
        _ft[_head] = (float)dt;
        _head = (_head + 1) % Cap;
        if (_count < Cap) _count++;
        if (_recording is { Count: < MaxRecorded }) _recording.Add((float)dt);
        _lastTs = tsMs;
    }

    public FrameSnapshot? Snapshot() => Snapshot(WallMs());

    /// <param name="wallMs">"Adesso" nell'orologio di WallMs; separato solo per le prove.</param>
    public FrameSnapshot? Snapshot(double wallMs)
    {
        // Frame della finestra, dal più recente al più vecchio, in un buffer preso in prestito (chiamata frequente:
        // niente allocazioni grandi a ogni aggiornamento). Restano nuovi solo i due array del grafico, che chi
        // riceve lo snapshot tiene.
        float[] frames = System.Buffers.ArrayPool<float>.Shared.Rent(Cap);
        try
        {
            int n = 0, n1s = 0;
            double sum1s = 0;
            float[] graph, graphX;

            lock (_lock)
            {
                if (_count < 2 || Environment.TickCount64 - _lastWallMs > 1500) return null;
                (graph, graphX) = GraphPoints(wallMs);
                double newest = _lastTs;
                for (int k = 0; k < _count; k++)
                {
                    int idx = ((_head - 1 - k) % Cap + Cap) % Cap;
                    double age = newest - _ts[idx];
                    if (age > WindowMs) break;
                    float ft = _ft[idx];
                    frames[n++] = ft;
                    if (age <= 1000) { n1s++; sum1s += ft; }
                }
            }

            if (n < 2 || n1s == 0) return null;

            double sum = 0, jitterSum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += frames[i];
                if (i > 0) jitterSum += Math.Abs(frames[i] - frames[i - 1]);
            }
            double mean = sum / n;
            double jitter = jitterSum / (n - 1);

            var sorted = frames;
            Array.Sort(sorted, 0, n); // crescente: i peggiori sono in fondo
            double median = sorted[n / 2];
            double p99 = sorted[Math.Min(n - 1, (int)(n * 0.99))];

            int stutters = 0;
            double stutterLimit = Math.Max(median * 2, median + 4);
            for (int i = n - 1; i >= 0 && sorted[i] > stutterLimit; i--) stutters++;

            return new FrameSnapshot(
                Fps: 1000.0 * n1s / sum1s,
                AvgFps: 1000.0 / mean,
                Low1Fps: 1000.0 / WorstMean(sorted, n, 0.01),
                Low01Fps: 1000.0 / WorstMean(sorted, n, 0.001),
                FrametimeMs: sum1s / n1s,
                P99Ms: p99,
                JitterMs: jitter,
                Stutters: stutters,
                PacingPct: Math.Clamp(100.0 * (1.0 - jitter / mean), 0, 100),
                Graph: graph,
                GraphX: graphX);
        }
        finally { System.Buffers.ArrayPool<float>.Shared.Return(frames); }
    }

    /// <summary>
    /// I frame del grafico in ordine cronologico, con la loro posizione orizzontale. Si prende anche un
    /// frame oltre ciascun bordo, così la linea arriva ai lati (chi disegna la ritaglia). Da chiamare sotto _lock.
    /// </summary>
    private (float[] Values, float[] X) GraphPoints(double wallMs)
    {
        GraphSpan(wallMs, int.MaxValue, out double edge, out int first, out int last);
        int n = Math.Max(0, last - first + 1);
        var values = new float[n];
        var x = new float[n];
        FillGraph(edge, first, last, values, x);
        return (values, x);
    }

    /// <summary>
    /// Come i punti dello snapshot, ma copiati in array già pronti (nessuna allocazione): serve al
    /// grafico disegnato 60 volte al secondo. Restituisce quanti punti ha scritto (0 = niente da mostrare).
    /// </summary>
    public int CopyGraph(double wallMs, float[] values, float[] x)
    {
        lock (_lock)
        {
            if (_count < 2 || Environment.TickCount64 - _lastWallMs > 1500) return 0;
            GraphSpan(wallMs, values.Length, out double edge, out int first, out int last);
            FillGraph(edge, first, last, values, x);
            return Math.Max(0, last - first + 1);
        }
    }

    private void GraphSpan(double wallMs, int maxPoints, out double edge, out int first, out int last)
    {
        double newest = _lastTs;
        double delay = Math.Max(GraphDelayMs, _batchEma + 50);
        edge = double.IsNaN(_clockOffset) ? newest : wallMs - _clockOffset - delay;
        // Se l'orologio stimato è fuori strada (nessun frame prima del bordo, o l'ultimo è vecchio di secondi
        // per una pausa), il bordo destro torna sull'ultimo frame: il grafico resta comunque leggibile.
        if (edge < _ts[Index(_count - 1)] || edge - newest > GraphWindowMs / 2) edge = newest;

        int start = 0;
        while (start < _count && _ts[Index(start)] > edge) start++;
        first = Math.Max(0, start - 1);                    // l'ultimo frame più nuovo del bordo destro
        last = start;
        double left = edge - GraphWindowMs;
        while (last < _count - 1 && _ts[Index(last)] >= left && last - first + 1 < maxPoints) last++;   // si ferma sul primo oltre il bordo sinistro
    }

    private void FillGraph(double edge, int first, int last, float[] values, float[] x)
    {
        int n = Math.Max(0, last - first + 1);
        for (int i = 0; i < n; i++)
        {
            int idx = Index(last - i);   // dal più vecchio al più recente
            values[i] = _ft[idx];
            x[i] = (float)(1.0 - (edge - _ts[idx]) / GraphWindowMs);
        }
    }

    /// <summary>Posizione nel buffer del k-esimo frame più recente (0 = l'ultimo).</summary>
    private int Index(int k) => ((_head - 1 - k) % Cap + Cap) % Cap;

    /// <param name="n">Quanti valori validi ci sono all'inizio dell'array (può essere più lungo: buffer in prestito).</param>
    private static double WorstMean(float[] sortedAsc, int n, double fraction)
    {
        int take = Math.Max(1, (int)Math.Ceiling(n * fraction));
        double s = 0;
        for (int i = n - take; i < n; i++) s += sortedAsc[i];
        return s / take;
    }
}
