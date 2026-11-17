using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Il ritmo dell'overlay nel gioco, su un thread suo con un timer preciso (60 al secondo):
/// - legge i frame dalla DLL (GameHook.Pump) a ogni giro;
/// - ridisegna la linea del grafico (GraphRaster) e manda al gioco solo quella striscia di righe;
/// - quando arriva un'immagine nuova con i testi (10 al secondo, da OverlayRenderer) la manda intera;
/// - ogni 6 giri chiede all'interfaccia il suo aggiornamento (Tick10Hz).
/// Così né i testi lenti da disegnare né un momento di lavoro dell'interfaccia fanno scattare il grafico.
/// </summary>
public sealed class OverlayPump : IDisposable
{
    public const int Hz = 60;
    private const double PeriodMs = 1000.0 / Hz;
    public const int TicksPer10Hz = Hz / 10;

    private readonly GameHook _hook;
    private readonly Thread _thread;
    private volatile bool _stop;

    /// <summary>Chiamato dal thread dell'overlay 10 volte al secondo: chi lo riceve passa il lavoro al suo thread.</summary>
    public event Action? Tick10Hz;

    // Impostazioni lette dal thread dell'overlay (scritte dall'interfaccia).
    private sealed record Config(bool LimiterEnabled, double LimiterFps, bool OverlayVisible);
    private volatile Config _config = new(false, 60, true);
    public void Configure(bool limiterEnabled, double limiterFps, bool overlayVisible)
    {
        var c = _config;
        if (c.LimiterEnabled != limiterEnabled || c.LimiterFps != limiterFps || c.OverlayVisible != overlayVisible)
            _config = new(limiterEnabled, limiterFps, overlayVisible);
    }

    /// <summary>Un'immagine con i testi, pronta da mandare, più quanto serve per disegnarci sopra il grafico.</summary>
    private sealed class Frame
    {
        public byte[] Pixels = Array.Empty<byte>();
        public int Width, Height, Corner, Margin;
        public bool Graph;                 // c'è un grafico con dati (altrimenti nessuna linea)
        public bool HasRect;               // l'immagine ha il riquadro del grafico (anche senza dati nell'app)
        public double Left, Top, Right, Bottom;   // interno del riquadro del grafico, in pixel
        public int StripY, StripRows;      // righe che il grafico tocca
        public GraphRaster.Ink Line, Grid;
        public double LineWidth, GridWidth;
        public FrameStats? Source;
    }

    private readonly object _frameGate = new();
    private Frame? _pending;                         // l'ultima arrivata, non ancora presa
    private Frame? _current;                         // quella in uso (solo il thread dell'overlay)
    private readonly Stack<Frame> _free = new();    // riuso dei buffer: nessuna allocazione a regime
    private byte[] _work = Array.Empty<byte>();     // immagine + linea, quella che va al gioco
    private readonly GraphRaster _raster = new();
    private readonly float[] _values = new float[4096], _xs = new float[4096];
    private double _top, _lastGraphMs;
    private int _sentLinks = -1;

    public OverlayPump(GameHook hook)
    {
        _hook = hook;
        _thread = new Thread(Run) { IsBackground = true, Name = "FrameGrip overlay", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Consegna un'immagine nuova con i testi (da qualunque thread). Copia i pixel in un buffer riusato:
    /// chi chiama può riscrivere subito i suoi.
    /// </summary>
    public void Submit(OverlayBitmap bmp, int corner, int margin, FrameStats? source, bool hasGraphData)
    {
        Frame f;
        lock (_frameGate) f = _free.Count > 0 ? _free.Pop() : new Frame();
        int bytes = bmp.Width * bmp.Height * 4;
        if (f.Pixels.Length < bytes) f.Pixels = new byte[bytes];
        Buffer.BlockCopy(bmp.Pixels, 0, f.Pixels, 0, bytes);
        f.Width = bmp.Width;
        f.Height = bmp.Height;
        f.Corner = corner;
        f.Margin = margin;
        f.Source = source;
        var g = bmp.GraphRect;
        f.Graph = hasGraphData && !g.IsEmpty;
        f.HasRect = !g.IsEmpty;
        if (!g.IsEmpty)
        {
            double k = bmp.Scale;
            var ink = bmp.GraphInk;
            // L'interno del riquadro: la cornice (spessa 1 unità) resta quella dell'immagine.
            double inset = ink.GridWidth * k;
            f.Left = g.X + inset; f.Top = g.Y + inset; f.Right = g.Right - inset; f.Bottom = g.Bottom - inset;
            f.StripY = Math.Clamp((int)Math.Floor(f.Top), 0, f.Height);
            f.StripRows = Math.Clamp((int)Math.Ceiling(f.Bottom), 0, f.Height) - f.StripY;
            f.Line = new(ink.Line.B, ink.Line.G, ink.Line.R, ink.Line.A);
            f.Grid = new(ink.Grid.B, ink.Grid.G, ink.Grid.R, ink.Grid.A);
            f.LineWidth = ink.LineWidth * k;
            f.GridWidth = ink.GridWidth * k;
        }
        else
        {
            f.StripY = f.StripRows = 0;
        }
        lock (_frameGate)
        {
            if (_pending != null) _free.Push(_pending);
            _pending = f;
        }
    }

    // ------------------------------------------------------------ thread dell'overlay

    private void Run()
    {
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        double next = FrameStats.WallMs(), nextUi = next;
        bool fast = false;
        while (!_stop)
        {
            // 60 giri al secondo servono solo se l'app deve ridisegnare lei la linea del grafico per un gioco
            // (DLL vecchia). Altrimenti bastano 10: la linea la disegna la DLL a ogni frame, e i testi
            // cambiano al più 10 volte al secondo.
            double period = fast ? PeriodMs : 100;
            next += period;
            double now = FrameStats.WallMs();
            if (next < now - period) next = now + period;   // rimasto indietro (PC sospeso): si riparte
            WaitUntil(timer, next);
            if (_stop) break;

            double t0 = FrameStats.WallMs();
            try
            {
                var c = _config;
                _hook.Pump(c.LimiterEnabled, c.LimiterFps, c.OverlayVisible);
                if (c.OverlayVisible) Send(t0);
                fast = c.OverlayVisible && _current is { Graph: true } && _hook.AnyAppDrawnGraph;
            }
            catch (Exception ex)
            {
                AppLog.Write("Overlay thread: " + ex.Message);
            }
            if (t0 >= nextUi - 2)
            {
                nextUi = Math.Max(nextUi + 100, t0 + 50);
                Tick10Hz?.Invoke();
            }
        }
        if (timer != IntPtr.Zero) CloseHandle(timer);
    }

    private static void WaitUntil(IntPtr timer, double targetMs)
    {
        for (;;)
        {
            double remaining = targetMs - FrameStats.WallMs();
            if (remaining <= 0) return;
            if (timer != IntPtr.Zero)
            {
                long due = -(long)(remaining * 10_000);   // unità da 100 ns, relativo
                if (SetWaitableTimerEx(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                {
                    WaitForSingleObject(timer, 1000);
                    return;
                }
            }
            Thread.Sleep(1);
        }
    }

    /// <summary>Manda al gioco l'immagine nuova (intera) o, se non è cambiata, la sola striscia del grafico.</summary>
    private void Send(double nowMs)
    {
        bool fresh = false;
        lock (_frameGate)
        {
            if (_pending != null)
            {
                if (_current != null) _free.Push(_current);
                _current = _pending;
                _pending = null;
                fresh = true;
            }
        }
        var f = _current;
        if (f == null || !_hook.HasLinks) return;
        // Un gioco appena agganciato non ha ancora nessuna immagine: gli si manda quella intera.
        int links = _hook.LinksVersion;
        if (links != _sentLinks) { fresh = true; _sentLinks = links; }
        int bytes = f.Width * f.Height * 4;
        int row = f.Width * 4;

        bool appDrawn = _hook.AnyAppDrawnGraph;
        if (fresh)
        {
            // Ai giochi la cui DLL disegna la linea da sé va l'immagine senza linea, con riquadro e colori.
            if (_hook.AnyHookDrawnGraph)
                _hook.WriteOverlay(f.Pixels, f.Width, f.Height, f.Corner, f.Margin, 0, 0, GameHook.GraphTarget.HookDrawn, HookGraph(f));
            if (appDrawn)
            {
                if (_work.Length < bytes) _work = new byte[bytes];
                Buffer.BlockCopy(f.Pixels, 0, _work, 0, bytes);
                DrawGraph(f, nowMs);
                _hook.WriteOverlay(_work, f.Width, f.Height, f.Corner, f.Margin, f.StripY, f.StripRows, GameHook.GraphTarget.AppDrawn);
            }
        }
        else if (appDrawn && f.Graph && f.StripRows > 0)
        {
            // Si rimette la striscia com'era nell'immagine (senza linea) e si ridisegna la linea.
            Buffer.BlockCopy(f.Pixels, f.StripY * row, _work, f.StripY * row, f.StripRows * row);
            DrawGraph(f, nowMs);
            _hook.WriteRows(_work, f.Width, f.StripY, f.StripRows);
        }
    }

    private static GameHook.GraphParams HookGraph(Frame f)
    {
        static uint Pack(GraphRaster.Ink c) => c.B | (uint)c.G << 8 | (uint)c.R << 16 | (uint)c.A << 24;
        return new GameHook.GraphParams(f.HasRect, (float)f.Left, (float)f.Top, (float)f.Right, (float)f.Bottom,
            Pack(f.Line), Pack(f.Grid), (float)f.LineWidth, (float)f.GridWidth, (float)FrameStats.GraphWindowMs,
            (float)GraphScale.FallPer100Ms, (float)GraphScale.SpikeRoom, (float)GraphScale.MaxSpikeShown);
    }

    private void DrawGraph(Frame f, double nowMs)
    {
        if (!f.Graph || f.Source == null) return;
        int n = f.Source.CopyGraph(nowMs, _values, _xs);
        if (n < 2) return;
        GraphScale.Range(_values, _xs, n, out double avg, out double max);
        _top = GraphScale.Step(_top, GraphScale.Target(avg, max), _lastGraphMs == 0 ? 0 : nowMs - _lastGraphMs);
        _lastGraphMs = nowMs;
        _raster.Draw(_work, f.Width, f.Left, f.Top, f.Right, f.Bottom, _values, _xs, n, _top, avg, f.Line, f.LineWidth, f.Grid, f.GridWidth);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002, TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Disegna l'immagine con i testi dell'overlay (WPF, RenderTargetBitmap: qualche millisecondo) su un thread
/// suo, così l'interfaccia non si ferma. Riceve l'ultima richiesta (le precedenti non ancora fatte si
/// scartano) e consegna il risultato a OverlayPump.
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private sealed record Job(string SettingsJson, OverlayData Data, double Dpi, int Corner, int Margin, FrameStats? Source);

    private readonly OverlayPump _pump;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile Job? _request;
    private volatile bool _stop;

    public OverlayRenderer(OverlayPump pump)
    {
        _pump = pump;
        _thread = new Thread(Run) { IsBackground = true, Name = "FrameGrip overlay image" };
        _thread.SetApartmentState(ApartmentState.STA);   // WPF disegna solo da thread STA
        _thread.Start();
    }

    /// <param name="settingsJson">Le impostazioni come testo: il thread di disegno ne tiene una copia sua.</param>
    public void Request(string settingsJson, OverlayData data, double dpi, int corner, int margin, FrameStats? source)
    {
        _request = new Job(settingsJson, data, dpi, corner, margin, source);
        _wake.Set();
    }

    private void Run()
    {
        var bmp = new OverlayBitmap();
        Settings? settings = null;
        string json = "";
        while (!_stop)
        {
            _wake.WaitOne();
            var r = Interlocked.Exchange(ref _request, null);
            if (r == null || _stop) continue;
            try
            {
                if (settings == null || r.SettingsJson != json)
                {
                    settings = Settings.FromJson(r.SettingsJson);
                    json = r.SettingsJson;
                }
                bool ok = bmp.Render(settings, r.Data, r.Dpi);
                if (ok && bmp.Changed)
                {
                    bool graphData = r.Data.Frames?.Graph is { Length: >= 2 };
                    _pump.Submit(bmp, r.Corner, r.Margin, r.Source, graphData);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Overlay image: " + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(500);
    }
}
