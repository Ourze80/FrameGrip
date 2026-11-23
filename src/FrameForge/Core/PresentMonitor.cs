using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FrameGrip.Core;

/// <summary>
/// Misura i frame di tutti i processi ascoltando gli eventi Present di Windows (ETW),
/// la stessa tecnica di PresentMon: nessuna iniezione di codice nei giochi.
/// Se Windows (o un altro programma) ferma la sessione, la si riavvia da sola, con attese crescenti.
/// </summary>
public sealed class PresentMonitor : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    private const int DxgiPresentStart = 42;
    private const int DxgiPresentMpoStart = 55;
    private const int D3D9PresentStart = 1;
    private const int DxgKrnlPresentInfo = 184;
    private const ulong DxgKrnlKeywords = 0x8000001; // Present | Base

    /// <summary>
    /// Nome della sessione ETW. Nelle prove (FRAMEGRIP_SETTINGS) ha il pid in fondo: aprire una sessione con lo stesso
    /// nome chiude quella che c'è, e le prove non devono mai chiudere quella dell'app vera dell'utente.
    /// </summary>
    private static string SessionName => Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS") is { Length: > 0 }
        ? $"FrameGripPresentSession_{Environment.ProcessId}"
        : "FrameGripPresentSession";

    private sealed class ProcEntry
    {
        public readonly FrameStats Stats = new();
        public double LastRuntimeTs = double.NegativeInfinity;
    }

    private readonly ConcurrentDictionary<int, ProcEntry> _procs = new();
    private readonly int _ownPid = Environment.ProcessId;
    private readonly ManualResetEventSlim _stop = new(false);
    private volatile TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _disposed;
    // Ogni sessione conta il tempo da quando parte: si sposta di quanto è partita dopo la prima, così
    // dopo un riavvio i tempi dei frame continuano ad andare avanti invece di ricominciare da zero.
    private double _firstStartWall = double.NaN, _tsShift;

    /// <summary>Perché le misure non funzionano, tradotto nella lingua attuale quando lo si legge.</summary>
    public string? Error => ErrorText?.ToString();
    /// <summary>Lo stesso errore, con la versione inglese per il file di registro.</summary>
    public LocText? ErrorText { get; private set; }
    public bool Running => _session != null && ErrorText == null;

    public PresentMonitor() => EmergencyCleanup.Register(this);

    public void Start()
    {
        if (_thread != null) return;
        try
        {
            Open();
        }
        catch (UnauthorizedAccessException)
        {
            ErrorText = new LocText("Frames.NeedAdmin");   // senza privilegi non serve riprovare
            return;
        }
        catch (Exception ex)
        {
            ErrorText = LocText.Raw(ex.Message);
            AppLog.Write("ETW: could not start: " + ex.Message);
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "FrameGrip ETW" };
        _thread.Start();
    }

    /// <summary>Apre una sessione nuova e la mette in _session.</summary>
    private void Open()
    {
        var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        try
        {
            session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
            session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, DxgKrnlKeywords);
            double wall = FrameStats.WallMs();
            if (double.IsNaN(_firstStartWall)) _firstStartWall = wall;
            else
            {
                // Sessione nuova: orologio spostato, e i frame di prima non si collegano a quelli che arrivano.
                _tsShift = wall - _firstStartWall;
                foreach (var e in _procs.Values) e.Stats.Discontinuity();
            }
            session.Source.AllEvents += OnEvent;
            _session = session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Thread della sessione. Process() torna (o lancia) solo se la sessione si ferma: in quel caso si segnala
    /// l'errore e si riapre dopo un'attesa che raddoppia a ogni tentativo (1 s ... 60 s), mai a ciclo continuo.
    /// </summary>
    private void Loop()
    {
        int backoffMs = 1000;
        while (!_disposed)
        {
            var session = _session;
            if (session != null)
            {
                long started = Environment.TickCount64;
                string why;
                try
                {
                    session.Source.Process();
                    why = "session stopped";
                }
                catch (Exception ex) { why = ex.Message; }
                if (_disposed) break;

                AppLog.Write($"ETW: {why}; restarting in {backoffMs / 1000} s");
                ErrorText = new LocText("Frames.Restarting");
                _session = null;
                try { session.Dispose(); } catch { /* già chiusa */ }
                // Se era rimasta in piedi a lungo, il problema non è "subito dopo l'avvio": si riparte dall'attesa breve.
                if (Environment.TickCount64 - started > 60_000) backoffMs = 1000;
            }

            if (_stop.Wait(backoffMs)) break;
            backoffMs = Math.Min(backoffMs * 2, 60_000);
            try
            {
                Open();
                ErrorText = null;
                AppLog.Write("ETW: session restarted");
            }
            catch (UnauthorizedAccessException)
            {
                ErrorText = new LocText("Frames.NeedAdmin");
                break;
            }
            catch (Exception ex)
            {
                ErrorText = LocText.Raw(ex.Message);
                AppLog.Write("ETW: restart failed: " + ex.Message);
            }
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        Guid provider = e.ProviderGuid;
        bool runtime;

        if (provider == Dxgi)
        {
            if (id != DxgiPresentStart && id != DxgiPresentMpoStart) return;
            runtime = true;
        }
        else if (provider == D3D9)
        {
            if (id != D3D9PresentStart) return;
            runtime = true;
        }
        else if (provider == DxgKrnl)
        {
            if (id != DxgKrnlPresentInfo) return;
            runtime = false;
        }
        else return;

        int pid = e.ProcessID;
        if (pid <= 4 || pid == _ownPid) return;

        var entry = _procs.GetOrAdd(pid, static _ => new ProcEntry());
        double ts = e.TimeStampRelativeMSec + _tsShift;

        if (runtime)
        {
            entry.LastRuntimeTs = ts;
            entry.Stats.AddPresent(ts);
        }
        else if (ts - entry.LastRuntimeTs > 2000)
        {
            // Vulkan/OpenGL: nessun evento DXGI/D3D9, si usa il present del kernel.
            entry.Stats.AddPresent(ts);
        }
    }

    public FrameSnapshot? GetSnapshot(int pid) =>
        _procs.TryGetValue(pid, out var e) ? e.Stats.Snapshot() : null;

    public FrameStats? GetStats(int pid) => _procs.TryGetValue(pid, out var e) ? e.Stats : null;

    /// <summary>
    /// Rimuove i processi che non presentano frame da un po' e che sono chiusi (o fermi da minuti). Mai quelli
    /// usati da un benchmark o da una misura del limite ideale: chi misura tiene lo stesso oggetto FrameStats,
    /// e se lo si buttasse i frame nuovi finirebbero in un altro e la misura resterebbe vuota.
    /// </summary>
    public void Prune()
    {
        long now = Environment.TickCount64;
        foreach (var kv in _procs)
        {
            long idle = now - kv.Value.Stats.LastWallMs;
            if (idle <= 15_000 || kv.Value.Stats.InUse) continue;
            if (idle > 300_000 || !IsAlive(kv.Key)) _procs.TryRemove(kv.Key, out _);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Vero se il processo esiste ancora (nel dubbio, per esempio accesso negato, lo si considera vivo).</summary>
    private static bool IsAlive(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5 /* ERROR_ACCESS_DENIED */;
        try { return !GetExitCodeProcess(h, out uint code) || code == STILL_ACTIVE; }
        finally { CloseHandle(h); }
    }

    public void Dispose()
    {
        _disposed = true;
        _stop.Set();
        try { _session?.Dispose(); } catch { /* sessione già chiusa */ }
        _session = null;
    }
}
