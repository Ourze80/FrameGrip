using System.Diagnostics;

namespace FrameGrip.Core;

public enum IdealState { Idle, Setup, LimiterNotice, NoGame, Countdown, Measuring, Analyzing, Result, TooShort, GameClosed }

/// <summary>Il gioco agganciato visto dall'app: serve alla misura e ai profili.</summary>
/// <param name="RefreshHz">Frequenza intera, per le scritte.</param>
/// <param name="RefreshRate">Frequenza esatta (59,94...), per i calcoli; 0 = non nota, la si cerca dalla finestra del gioco.</param>
public sealed record GameInfo(int Pid, string Exe, string Name, int RefreshHz, WindowMode Mode, FrameStats? Stats, int FgDetected,
    string? Path = null, string? Store = null, string? AppId = null, double RefreshRate = 0);

/// <summary>
/// La misura del "limite ideale": preparazione, conto alla rovescia, raccolta dei frame, analisi, risultato.
/// Gira nel thread dell'interfaccia (Tick, 10 volte al secondo) ma lavora poco: i frame li scrive FrameStats in un
/// buffer preparato una volta sola, qui si aggiornano solo progresso, grafico dal vivo e tre numeri.
/// L'analisi vera (IdealLimit) gira su un altro thread.
/// </summary>
public sealed class IdealMeasure
{
    public const int CountdownSeconds = 5;
    private const int BufferSize = 600_000;   // 5 minuti a 2000 present al secondo

    public IdealState State { get; private set; } = IdealState.Idle;
    public event Action? Changed;

    // gioco e condizioni della misura
    public GameInfo? Game { get; private set; }
    public int FgMultiplier { get; set; } = 1;
    public bool Vrr { get; set; }
    public bool Borderless { get; private set; }
    public double DurationSec { get; private set; } = 120;
    public double MinCleanSec { get; private set; } = 60;
    public int MinFrames { get; private set; } = 4000;

    // raccolta
    private float[]? _buffer;
    private FrameStats? _source;
    private Process? _process;
    private int _processed;
    private double _elapsedMs, _cleanMs;
    private long _countdownStart, _lastFrameTick, _doneTick, _cancelTick;
    private int _countdownSec = CountdownSeconds;
    /// <summary>Partita con il tasto in gioco: i riquadri nel gioco dicono anche come fermarla.</summary>
    public bool FromKey { get; private set; }
    public bool Paused { get; private set; }
    public double CleanSec => _cleanMs / 1000;
    public double Progress => Math.Clamp(_cleanMs / 1000 / DurationSec, 0, 1);
    public int CountdownLeft => Math.Max(0, _countdownSec - (int)((Environment.TickCount64 - _countdownStart) / 1000));

    // numeri dal vivo (senza allocazioni: istogramma a 0,1 ms e 240 tratti di grafico)
    private readonly int[] _hist = new int[10_001];
    private int _frames, _stutters;
    public readonly float[] LiveGraph = new float[IdealLimit.GraphPoints];
    public double LiveAvgFps => _cleanMs > 0 ? _frames * 1000 / _cleanMs * FgMultiplierShown : 0;
    public int LiveStutters => _stutters;
    private int FgMultiplierShown => 1;   // i present grezzi sono già i fotogrammi mostrati

    // carico una volta al secondo
    private readonly LoadSample[] _samples = new LoadSample[1200];
    private int _sampleCount;

    public IdealResult? Result { get; private set; }
    public int Selected { get; set; }
    public double ClosedSeconds { get; private set; }

    public bool SuspendsLimiter => State is IdealState.Countdown or IdealState.Measuring;
    public bool Busy => State is IdealState.Countdown or IdealState.Measuring or IdealState.Analyzing;

    private void Set(IdealState s) { State = s; Changed?.Invoke(); }

    public void OpenSetup() => Set(IdealState.Setup);
    public void Close() { Stop(); Set(IdealState.Idle); }
    public void ShowNotice(IdealState s) => Set(s);

    /// <summary>Durata (minuti scelti; FRAMEGRIP_IDEAL_SECONDS la accorcia per le prove, con soglie in proporzione).</summary>
    private void ApplyDuration(int minutes)
    {
        DurationSec = minutes * 60;
        MinCleanSec = 60; MinFrames = 4000;
        if (double.TryParse(Environment.GetEnvironmentVariable("FRAMEGRIP_IDEAL_SECONDS"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 5)
        {
            DurationSec = s;
            MinCleanSec = Math.Min(60, s * 0.5);
            MinFrames = (int)Math.Min(4000, s * 20);
        }
    }

    /// <summary>Parte il conto alla rovescia: da qui il limitatore è sospeso.</summary>
    public void Start(GameInfo game, int minutes, bool vrr, int fg, int countdown = CountdownSeconds, bool fromKey = false)
    {
        _countdownSec = countdown; FromKey = fromKey; _cancelTick = 0; _doneTick = 0;
        Game = game; Vrr = vrr; FgMultiplier = Math.Clamp(fg, 1, 4);
        Borderless = game.Mode is WindowMode.Borderless or WindowMode.Windowed;
        ApplyDuration(minutes);
        AppLog.Write($"Ideal: start {game.Exe} {minutes}min fg x{FgMultiplier}{(fromKey ? " (tasto)" : "")}");
        Result = null; Selected = 0;
        _countdownStart = Environment.TickCount64;
        Set(IdealState.Countdown);
    }

    private void BeginCollect()
    {
        var g = Game!;
        _buffer ??= new float[BufferSize];
        _source = g.Stats;
        try { _process = Process.GetProcessById(g.Pid); } catch { _process = null; }
        _processed = 0; _elapsedMs = _cleanMs = 0; _frames = _stutters = 0; _sampleCount = 0;
        Array.Clear(_hist); Array.Clear(LiveGraph);
        _lastFrameTick = Environment.TickCount64;
        Paused = false;
        _source?.BeginMeasure(_buffer);
        Set(IdealState.Measuring);
    }

    /// <summary>Dopo "troppo breve": si continua con i frame già raccolti.</summary>
    public void Resume()
    {
        if (_buffer == null || _source == null || Game == null) { Set(IdealState.Setup); return; }
        DurationSec = Math.Max(DurationSec, _cleanMs / 1000 + 30);
        _source.ResumeMeasure(_buffer, _processed);
        _lastFrameTick = Environment.TickCount64;
        Set(IdealState.Measuring);
    }

    public void Cancel() { Stop(); Set(IdealState.Setup); }

    /// <summary>
    /// Il tasto in gioco premuto di nuovo: con abbastanza frame la misura finisce e dà il risultato,
    /// altrimenti si annulla (e il gioco lo dice per qualche secondo). Vero se parte l'analisi.
    /// </summary>
    public bool StopByKey(bool sound)
    {
        if (State == IdealState.Measuring && CleanSec >= MinCleanSec && _frames >= MinFrames)
        {
            _source?.EndMeasure();
            Analyze(sound);
            return true;
        }
        if (State is IdealState.Countdown or IdealState.Measuring)
        {
            Stop();
            _cancelTick = Environment.TickCount64;
            Set(IdealState.Setup);
        }
        return false;
    }

    private void Stop()
    {
        _source?.EndMeasure();
        _process?.Dispose(); _process = null;
    }

    /// <summary>Testo del riquadro in gioco (overlay), null se non c'è niente da dire.</summary>
    public string? Banner(out int kind)
    {
        kind = 0;
        switch (State)
        {
            case IdealState.Countdown: return FromKey ? Loc.F("Ideal.BannerCountdownKey", CountdownLeft) : Loc.F("Ideal.BannerCountdown", CountdownLeft);
            case IdealState.Measuring:
                kind = 1;
                return Paused ? Loc.T("Ideal.BannerPaused") : Loc.F("Ideal.BannerRun", Clock(CleanSec), Clock(DurationSec));
        }
        long now = Environment.TickCount64;
        if (_doneTick != 0 && now - _doneTick < 10_000 && State == IdealState.Result && Result != null)
        {
            kind = 2;   // il numero si vede subito, senza uscire dal gioco
            return Loc.F("Ideal.BannerDoneN", Result.Recommended.Cap);
        }
        if (_doneTick != 0 && now - _doneTick < 10_000) { kind = 2; return Loc.T("Ideal.BannerDone"); }
        if (_cancelTick != 0 && now - _cancelTick < 6_000) return Loc.F("Ideal.BannerCancelled", (int)MinCleanSec);
        return null;
    }

    public static string Clock(double sec) => $"{(int)sec / 60}:{(int)sec % 60:00}";

    /// <summary>10 volte al secondo dal ciclo principale; sample = un campione di carico, una volta al secondo.</summary>
    public void Tick(LoadSample? sample, bool sound)
    {
        if (State == IdealState.Countdown)
        {
            if (CountdownLeft <= 0)
            {
                BeginCollect();
                if (sound) System.Media.SystemSounds.Asterisk.Play();
            }
            else Changed?.Invoke();
            return;
        }
        if (State != IdealState.Measuring || _buffer == null) return;

        int count = _source?.MeasureCount ?? 0;
        long now = Environment.TickCount64;
        if (count > _processed) _lastFrameTick = now;
        for (int i = _processed; i < count; i++)
        {
            float dt = _buffer[i];
            _elapsedMs += dt;
            if (_elapsedMs - dt < 5000 || dt > 500) continue;   // primi 5 secondi e pause: non contano
            _cleanMs += dt;
            _frames++;
            _hist[Math.Min(_hist.Length - 1, (int)(dt * 10))]++;
            if (_frames > 30 && dt > 2 * _cleanMs / _frames) _stutters++;
            int k = Math.Min(LiveGraph.Length - 1, (int)(_cleanMs / 1000 / DurationSec * LiveGraph.Length));
            if (dt > LiveGraph[k]) LiveGraph[k] = dt;
        }
        _processed = count;
        bool paused = now - _lastFrameTick > 1500;
        if (paused != Paused) Paused = paused;
        if (sample is { } s && _sampleCount < _samples.Length) _samples[_sampleCount++] = s with { FrameIndex = count };

        bool closed = _process == null || HasExited(_process);
        if (closed)
        {
            ClosedSeconds = CleanSec;
            Stop();
            if (CleanSec >= MinCleanSec && _frames >= MinFrames) Analyze(sound);
            else Set(IdealState.GameClosed);
            return;
        }
        if (CleanSec >= DurationSec || count >= _buffer.Length - 1)
        {
            _source?.EndMeasure();
            Analyze(sound);
            return;
        }
        Changed?.Invoke();
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return false; }
    }

    /// <summary>1% low dal vivo, dall'istogramma (media dei frame più lenti).</summary>
    public double LiveLow1Fps
    {
        get
        {
            if (_frames < 100) return 0;
            int need = Math.Max(1, _frames / 100), got = 0;
            double sum = 0;
            for (int b = _hist.Length - 1; b >= 0 && got < need; b--)
            {
                int take = Math.Min(_hist[b], need - got);
                sum += take * (b + 0.5) / 10;
                got += take;
            }
            return got > 0 ? 1000 / (sum / got) : 0;
        }
    }

    /// <summary>
    /// La frequenza esatta dello schermo del gioco (59,94 Hz → il limite consigliato è 59, non 60). Si usa solo se
    /// è vicina a quella intera che vede l'interfaccia (stesso schermo); altrimenti quella intera.
    /// </summary>
    private static double ExactRefresh(GameInfo? g)
    {
        if (g == null) return 0;
        double exact = g.RefreshRate;
        if (exact <= 0)
        {
            try
            {
                using var p = Process.GetProcessById(g.Pid);
                if (p.MainWindowHandle != IntPtr.Zero) exact = DisplayInfo.RefreshRate(p.MainWindowHandle);
            }
            catch { /* gioco chiuso: vale quella intera */ }
        }
        return exact > 0 && (g.RefreshHz <= 0 || Math.Abs(exact - g.RefreshHz) < 1.5) ? exact : g.RefreshHz;
    }

    private void Analyze(bool sound)
    {
        Set(IdealState.Analyzing);
        var input = new IdealInput
        {
            Raw = _buffer!, Count = _processed, RefreshHz = ExactRefresh(Game), Vrr = Vrr, FgMultiplier = FgMultiplier,
            Samples = _samples.Take(_sampleCount).ToArray(), MinCleanSec = MinCleanSec, MinFrames = MinFrames,
        };
        var ctx = SynchronizationContext.Current;
        Task.Run(() => IdealLimit.Analyze(input)).ContinueWith(t =>
        {
            void Done()
            {
                if (State != IdealState.Analyzing) return;
                if (t.Exception != null) { AppLog.Write("Ideal: " + t.Exception.GetBaseException().Message); Set(IdealState.Setup); return; }
                Result = t.Result;
                AppLog.Write($"Ideal: clean {Result.CleanSec:0.0}s frames {Result.CleanFrames} rec {Result.Recommended.Cap} bal {Result.Balanced.Cap} max {Result.Max.Cap} fg x{Result.FgMultiplier}");
                if (Result.TooShort) { _cancelTick = Environment.TickCount64; Set(IdealState.TooShort); return; }
                Stop();
                _doneTick = Environment.TickCount64;
                if (sound) System.Media.SystemSounds.Exclamation.Play();
                Set(IdealState.Result);
            }
            if (ctx != null) ctx.Post(_ => Done(), null); else Done();
        });
    }

    /// <summary>Per le schermate di prova: un risultato già pronto (FRAMEGRIP_IDEAL_DEMO).</summary>
    public void Demo(IdealState state, GameInfo game, IdealResult? result, double closedSec)
    {
        Game = game; Result = result; ClosedSeconds = closedSec; DurationSec = 120; MinCleanSec = 60;
        _cleanMs = closedSec * 1000;
        Set(state);
    }

    public IdealOption? SelectedOption => Result == null ? null : Selected switch { 1 => Result.Balanced, 2 => Result.Max, _ => Result.Recommended };
}
