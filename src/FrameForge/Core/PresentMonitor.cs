using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FrameGrip.Core;

/// <summary>
/// Misura i frame di tutti i processi ascoltando gli eventi Present di Windows (ETW),
/// la stessa tecnica di PresentMon: nessuna iniezione di codice nei giochi.
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

    private sealed class ProcEntry
    {
        public readonly FrameStats Stats = new();
        public double LastRuntimeTs = double.NegativeInfinity;
    }

    private readonly ConcurrentDictionary<int, ProcEntry> _procs = new();
    private readonly int _ownPid = Environment.ProcessId;
    private TraceEventSession? _session;
    private Thread? _thread;

    /// <summary>Perché le misure non funzionano, tradotto nella lingua attuale quando lo si legge.</summary>
    public string? Error => ErrorText?.ToString();
    /// <summary>Lo stesso errore, con la versione inglese per il file di registro.</summary>
    public LocText? ErrorText { get; private set; }
    public bool Running => _session != null && ErrorText == null;

    public void Start()
    {
        try
        {
            _session = new TraceEventSession("FrameGripPresentSession") { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, DxgKrnlKeywords);
            _session.Source.AllEvents += OnEvent;

            var session = _session;
            _thread = new Thread(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) { ErrorText = LocText.Raw(ex.Message); }
            })
            { IsBackground = true, Name = "FrameGrip ETW" };
            _thread.Start();
        }
        catch (UnauthorizedAccessException)
        {
            ErrorText = new LocText("Frames.NeedAdmin");
            _session = null;
        }
        catch (Exception ex)
        {
            ErrorText = LocText.Raw(ex.Message);
            _session = null;
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
        double ts = e.TimeStampRelativeMSec;

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

    /// <summary>Rimuove i processi che non presentano frame da un po'.</summary>
    public void Prune()
    {
        long now = Environment.TickCount64;
        foreach (var kv in _procs)
            if (now - kv.Value.Stats.LastWallMs > 15_000)
                _procs.TryRemove(kv.Key, out _);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* sessione già chiusa */ }
        _session = null;
    }
}
