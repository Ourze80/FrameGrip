using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace FrameGrip.Core;

/// <summary>
/// Gestisce la DLL caricata nei giochi: la inietta (a 32 o 64 bit), le passa limitatore e
/// immagine dell'overlay, e legge i tempi dei frame che misura.
/// Gli offset corrispondono a src/native/shared.h.
/// Usata da due thread: quello dell'interfaccia (aggancio, nome dell'API, stato) e quello dell'overlay
/// (Pump e scrittura delle immagini, a ritmo fisso). Tutto ciò che tocca i collegamenti passa da _gate.
/// </summary>
public sealed class GameHook : IDisposable
{
    // Versione 3: righe cambiate (scritture parziali). La 2 (DLL di prima) è ancora accettata, con sole immagini intere.
    private const int SharedVersion = 3, OldSharedVersion = 2;
    private const int OffApi = 4;
    private const int OffVersion = 0, OffLimiterEnabled = 8, OffOverlayVisible = 12, OffTargetFps = 16, OffHeartbeat = 24;
    private const int OffWriteIndex = 32, OffQpcFrequency = 40, OffRing = 48;
    private const int RingSize = 2048;
    private const int OffOvSeq = 16432, OffOvWidth = 16436, OffOvHeight = 16440, OffOvCorner = 16444, OffOvMargin = 16448;
    private const int OffOvFullSeq = 16452, OffLastDraw = 16456, OffOvDirtyY = 16464, OffOvDirtyRows = 16468;
    private const int OffPixelsV2 = 16464, OffPixelsV3 = 16480;
    // Aggiunte compatibili della versione 3, annunciate dalla DLL in dllCaps.
    private const int OffDllCaps = 16472, OffHdrWhiteNits = 16476, OffGraph = 8405088, GraphBytes = 64;

    /// <summary>Luminanza del bianco dell'overlay nei giochi HDR (nit). La DLL la legge a ogni frame.</summary>
    public volatile float HdrWhiteNits = 200f;
    private const uint CapGraph = 1;
    public const int MaxOverlayWidth = 2048, MaxOverlayHeight = 1024;

    private sealed class Link : IDisposable
    {
        public required MemoryMappedFile Map;
        public required MemoryMappedViewAccessor View;
        public required Process Process;
        public required int Version;
        public readonly FrameStats Stats = new();
        public ulong ReadIndex;
        public double QpcFrequency = 1;
        public long PixelsOffset => Version >= SharedVersion ? OffPixelsV3 : OffPixelsV2;
        /// <summary>La DLL disegna da sé la linea del grafico a ogni frame: l'app le manda solo riquadro e colori.</summary>
        public bool HookGraph;

        public void Dispose()
        {
            View.Dispose();
            Map.Dispose();
            Process.Dispose();
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Link> _links = new();
    private readonly Dictionary<int, (int Count, long LastMs)> _attempts = new();
    private readonly string _dir = AppContext.BaseDirectory;
    private int _injecting;   // 1 mentre un iniettore sta lavorando (fuori dal thread dell'interfaccia)

    /// <summary>L'ultimo errore di aggancio, tradotto nella lingua attuale quando lo si legge.</summary>
    public string? Error => _error?.ToString();
    private volatile LocText? _error;
    private volatile int _errorPid;   // il processo a cui si riferisce _error
    /// <summary>Motivo registrato quando Windows nega l'apertura del processo per l'iniezione.</summary>
    private const string NoAccessReason = "access denied by Windows";
    /// <summary>Un gioco che si chiude per un errore entro questo tempo dall'iniezione non si aggancia più.</summary>
    private const int EarlyExitMs = 10_000;
    public bool HasLinks { get { lock (_gate) return _links.Count > 0; } }

    public GameHook() => EmergencyCleanup.Register(this);
    /// <summary>Cresce a ogni gioco agganciato: chi manda le immagini sa che al nuovo va mandata quella intera.</summary>
    public int LinksVersion { get; private set; }
    public bool IsHooked(int pid) { lock (_gate) return _links.ContainsKey(pid); }

    /// <summary>Vero se la DLL sta disegnando l'overlay dentro quel gioco in questo momento.</summary>
    public bool IsDrawing(int pid)
    {
        lock (_gate) return _links.TryGetValue(pid, out var link) && Drawing(link);
    }

    private static bool Drawing(Link link)
    {
        try { return (ulong)Environment.TickCount64 - link.View.ReadUInt64(OffLastDraw) < 700; }
        catch { return false; }
    }

    /// <summary>
    /// Un gioco agganciato che sta disegnando l'overlay in questo momento, preferendo <paramref name="preferred"/>;
    /// 0 se nessuno.
    /// </summary>
    public int DrawingPid(int preferred)
    {
        lock (_gate)
        {
            if (preferred != 0 && _links.TryGetValue(preferred, out var p) && Drawing(p)) return preferred;
            foreach (var (pid, link) in _links)
                if (Drawing(link)) return pid;
            return 0;
        }
    }

    /// <summary>
    /// Aggancia il processo: riusa una DLL già presente, altrimenti la inietta. L'iniettore gira su un
    /// thread a parte (può metterci secondi): l'interfaccia e l'overlay non si fermano ad aspettarlo.
    /// </summary>
    public void EnsureHooked(int pid)
    {
        if (IsHooked(pid) || TryAttach(pid)) return;
        if (_errorPid != pid) { _error = null; _errorPid = pid; }   // l'errore di un altro gioco non vale per questo
        if (Volatile.Read(ref _injecting) == 1) return;

        // Giochi protetti da anti-cheat o che si sono chiusi subito dopo un aggancio: mai iniettare.
        string? path = GameLibrary.ProcessPath(pid);
        string exeName = path != null ? Path.GetFileNameWithoutExtension(path) : ProcessName(pid);
        if (exeName.Length == 0) return;
        bool forced = UserExclusions.ForceHook.Contains(exeName);
        string? protectedBy = AntiCheat.Check(path, exeName);
        if (protectedBy != null)
        {
            if (_error == null) AppLog.Write($"Hook: {exeName} (pid {pid}) not hooked: {protectedBy}");
            _error = new LocText(protectedBy.StartsWith(NoAccessReason, StringComparison.Ordinal) ? "Hook.NoAccess" : "Hook.AntiCheat");
            return;
        }
        if (!forced && AutoExclusions.Contains(exeName))
        {
            if (_error == null) AppLog.Write($"Hook: {exeName} (pid {pid}) not hooked: listed in {AutoExclusions.FileName}");
            _error = new LocText("Hook.AutoExcluded");
            return;
        }

        long now = Environment.TickCount64;
        lock (_gate)
        {
            _attempts.TryGetValue(pid, out var a);
            if (a.Count >= 3 || a.Count > 0 && now - a.LastMs < 5000) return;
            _attempts[pid] = (a.Count + 1, now);
        }

        string bits = Is32Bit(pid) ? "32" : "64";
        string hook = Path.Combine(_dir, $"FrameGripHook{bits}.dll");
        string injector = Path.Combine(_dir, $"ffinject{bits}.exe");
        if (!File.Exists(hook) || !File.Exists(injector))
        {
            _error = new LocText("Hook.Missing", bits);
            return;
        }

        Volatile.Write(ref _injecting, 1);
        Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(injector, $"{pid} \"{hook}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                if (p == null) return;
                int attempt;
                lock (_gate) attempt = _attempts.TryGetValue(pid, out var a) ? a.Count : 1;
                if (!p.WaitForExit(12000))
                {
                    // L'iniettore è bloccato (il gioco non risponde): lo si chiude, l'ExitCode non esiste ancora.
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                    _error = new LocText("Hook.Timeout");
                    AppLog.Write($"Hook: injector timed out on {exeName} (pid {pid}, {bits}-bit), attempt {attempt}: killed");
                    return;
                }
                int code = p.ExitCode;
                _error = code switch
                {
                    0 => null,
                    3 => new LocText("Hook.NoAccess"),
                    8 => new LocText("Hook.Timeout"),   // il caricamento nel gioco non è finito in tempo
                    _ => new LocText("Hook.Code", code),
                };
                AppLog.Write($"Hook: injected into {exeName} (pid {pid}, {bits}-bit), attempt {attempt}, injector code {code}");
                if (code == 3)
                {
                    // Windows nega l'accesso al processo: gioco protetto (o avviato come amministratore). Non si riprova.
                    AntiCheat.MarkProtected(path, exeName, NoAccessReason);
                    return;
                }
                if (code == 0)
                {
                    TryAttach(pid);
                    WatchEarlyExit(pid, exeName, forced);
                }
            }
            catch (Exception ex)
            {
                _error = new LocText("Hook.Failed", ex.Message);
            }
            finally
            {
                Volatile.Write(ref _injecting, 0);
            }
        });
    }

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return ""; }
    }

    /// <summary>
    /// Dopo un'iniezione riuscita: se il gioco si chiude per un errore (codice d'eccezione, per esempio 0xC0000005)
    /// entro EarlyExitMs, lo si mette nell'elenco dei giochi da non agganciare più. Una chiusura normale (codice 0
    /// o comunque non d'errore, come i giochi che si riavviano da Steam) non conta.
    /// </summary>
    private static void WatchEarlyExit(int pid, string exeName, bool forced)
    {
        Process game;
        try { game = Process.GetProcessById(pid); }
        catch { return; }   // già chiuso prima ancora di poterlo guardare: non si sa come
        long injectedAt = Environment.TickCount64;
        Task.Run(async () =>
        {
            using (game)
            {
                try
                {
                    using var cts = new CancellationTokenSource(EarlyExitMs);
                    await game.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }   // ancora vivo dopo 10 s: tutto bene
                catch { return; }
                int code;
                try { code = game.ExitCode; } catch { return; }
                long after = Environment.TickCount64 - injectedAt;
                bool crash = unchecked((uint)code) >= 0x80000000u;   // NTSTATUS d'errore o d'avviso (eccezioni, breakpoint)
                AppLog.Write($"Hook: {exeName} (pid {pid}) exited {after} ms after injection, code 0x{code:X8}" + (crash ? " (crash)" : ""));
                if (crash && !forced) AutoExclusions.Add(exeName, $"crashed {after} ms after injection, code 0x{code:X8}");
            }
        });
    }

    private static bool Is32Bit(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return Native.IsWow64Process(h, out bool wow) && wow; }
        finally { Native.CloseHandle(h); }
    }

    private bool TryAttach(int pid)
    {
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            map = MemoryMappedFile.OpenExisting($"Local\\FrameGripHook_{pid}", MemoryMappedFileRights.ReadWrite);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);   // tutto il blocco, qualunque versione
            int version = (int)view.ReadUInt32(OffVersion);
            if (version != SharedVersion && version != OldSharedVersion) throw new InvalidDataException();
            long pixels = version >= SharedVersion ? OffPixelsV3 : OffPixelsV2;
            if (view.Capacity < pixels + (long)MaxOverlayWidth * MaxOverlayHeight * 4) throw new InvalidDataException();

            var link = new Link { Map = map, View = view, Process = Process.GetProcessById(pid), Version = version };
            link.QpcFrequency = Math.Max(1, view.ReadInt64(OffQpcFrequency));
            link.HookGraph = version >= SharedVersion && view.Capacity >= OffGraph + GraphBytes && (view.ReadUInt32(OffDllCaps) & CapGraph) != 0;
            link.ReadIndex = view.ReadUInt64(OffWriteIndex); // i frame vecchi non interessano
            lock (_gate)
            {
                if (_links.ContainsKey(pid)) { link.Dispose(); return true; }
                _links[pid] = link;
                LinksVersion++;
            }
            if (version == OldSharedVersion) AppLog.Write($"Hook: pid {pid} has an older hook DLL (shared v2): full-image updates only");
            return true;
        }
        catch
        {
            view?.Dispose();
            map?.Dispose();
            return false; // la DLL non c'è (ancora), oppure è di una versione diversa
        }
    }

    /// <summary>
    /// Da chiamare spesso (thread dell'overlay): aggiorna limitatore e "battito" in tutti i giochi
    /// agganciati e raccoglie i nuovi frame misurati.
    /// </summary>
    public void Pump(bool limiterEnabled, double targetFps, bool overlayVisible)
    {
        List<int>? dead = null;
        double wall = FrameStats.WallMs();
        lock (_gate)
        {
            foreach (var (pid, link) in _links)
            {
                try
                {
                    if (link.Process.HasExited)
                    {
                        (dead ??= new()).Add(pid);
                        continue;
                    }

                    var v = link.View;
                    v.Write(OffTargetFps, targetFps);
                    v.Write(OffLimiterEnabled, limiterEnabled ? 1u : 0u);
                    v.Write(OffOverlayVisible, overlayVisible ? 1u : 0u);
                    v.Write(OffHeartbeat, (ulong)Environment.TickCount64);
                    // Bianco dell'overlay nei giochi HDR: campo libero anche nelle DLL v3 che non lo conoscono.
                    if (link.Version >= SharedVersion) v.Write(OffHdrWhiteNits, HdrWhiteNits);

                    ulong written = v.ReadUInt64(OffWriteIndex);
                    if (written - link.ReadIndex > RingSize) link.ReadIndex = written - RingSize;
                    for (ulong i = link.ReadIndex; i < written; i++)
                    {
                        long qpc = v.ReadInt64(OffRing + (long)(i % RingSize) * 8);
                        link.Stats.AddPresent(qpc * 1000.0 / link.QpcFrequency, wall);
                    }
                    link.ReadIndex = written;
                }
                catch
                {
                    (dead ??= new()).Add(pid);
                }
            }

            if (dead == null) return;
            foreach (int pid in dead)
            {
                _links[pid].Dispose();
                _links.Remove(pid);
                _attempts.Remove(pid);
            }
        }
    }

    /// <summary>
    /// Passa ai giochi agganciati l'immagine intera dell'overlay (BGRA premoltiplicato, righe da width*4 byte).
    /// dirtyY/dirtyRows dicono quali righe toccheranno le prossime scritture parziali (WriteRows).
    /// </summary>
    public void WriteOverlay(byte[] pixels, int width, int height, int corner, int margin, int dirtyY = 0, int dirtyRows = 0,
        GraphTarget target = GraphTarget.All, in GraphParams graph = default)
    {
        if (width <= 0 || height <= 0 || width > MaxOverlayWidth || height > MaxOverlayHeight) return;
        int bytes = width * height * 4;
        lock (_gate)
        {
            foreach (var link in _links.Values)
            {
                if (target == GraphTarget.HookDrawn && !link.HookGraph || target == GraphTarget.AppDrawn && link.HookGraph) continue;
                try
                {
                    var v = link.View;
                    uint seq = v.ReadUInt32(OffOvSeq) | 1;      // dispari: scrittura in corso
                    v.Write(OffOvSeq, seq);
                    Thread.MemoryBarrier();
                    v.Write(OffOvWidth, (uint)width);
                    v.Write(OffOvHeight, (uint)height);
                    v.Write(OffOvCorner, (uint)corner);
                    v.Write(OffOvMargin, (uint)margin);
                    if (link.Version >= SharedVersion)
                    {
                        v.Write(OffOvDirtyY, (uint)dirtyY);
                        v.Write(OffOvDirtyRows, (uint)dirtyRows);
                        v.Write(OffOvFullSeq, seq + 1);
                    }
                    if (link.HookGraph) WriteGraph(v, graph);
                    v.SafeMemoryMappedViewHandle.WriteArray((ulong)link.PixelsOffset, pixels, 0, bytes);
                    Thread.MemoryBarrier();
                    v.Write(OffOvSeq, seq + 1);                  // pari: immagine completa
                }
                catch { /* il gioco si sta chiudendo: se ne occupa Pump */ }
            }
        }
    }

    /// <summary>A quali giochi va un'immagine: tutti, solo quelli la cui DLL disegna la linea, solo gli altri.</summary>
    public enum GraphTarget { All, HookDrawn, AppDrawn }

    /// <summary>Il grafico che la DLL disegna da sé (FFShared::Graph): riquadro interno in pixel dell'immagine,
    /// colori in byte B, G, R, A, spessori in pixel e la regola della scala (vedi GraphScale).</summary>
    public readonly record struct GraphParams(bool Enabled, float Left, float Top, float Right, float Bottom,
        uint LineColor, uint GridColor, float LineWidth, float GridWidth, float WindowMs, float FallPer100Ms, float SpikeRoom, float MaxSpike);

    private static void WriteGraph(MemoryMappedViewAccessor v, in GraphParams g)
    {
        v.Write(OffGraph + 0, g.Enabled ? 1u : 0u);
        v.Write(OffGraph + 4, g.Left);
        v.Write(OffGraph + 8, g.Top);
        v.Write(OffGraph + 12, g.Right);
        v.Write(OffGraph + 16, g.Bottom);
        v.Write(OffGraph + 20, g.LineColor);
        v.Write(OffGraph + 24, g.GridColor);
        v.Write(OffGraph + 28, g.LineWidth);
        v.Write(OffGraph + 32, g.GridWidth);
        v.Write(OffGraph + 36, g.WindowMs);
        v.Write(OffGraph + 40, g.FallPer100Ms);
        v.Write(OffGraph + 44, g.SpikeRoom);
        v.Write(OffGraph + 48, g.MaxSpike);
    }

    /// <summary>Vero se c'è almeno un gioco agganciato la cui DLL non disegna la linea (la deve mettere l'app).</summary>
    public bool AnyAppDrawnGraph { get { lock (_gate) { foreach (var l in _links.Values) if (!l.HookGraph) return true; return false; } } }
    /// <summary>Vero se c'è almeno un gioco agganciato la cui DLL disegna da sé la linea.</summary>
    public bool AnyHookDrawnGraph { get { lock (_gate) { foreach (var l in _links.Values) if (l.HookGraph) return true; return false; } } }

    /// <summary>
    /// Riscrive solo le righe [y0, y0+rows) dell'immagine già passata con WriteOverlay (stessa misura,
    /// righe dentro la striscia dichiarata lì). Le DLL vecchie (v2) non la ricevono: per loro valgono
    /// solo le immagini intere.
    /// </summary>
    public void WriteRows(byte[] pixels, int width, int y0, int rows)
    {
        if (rows <= 0) return;
        int row = width * 4;
        lock (_gate)
        {
            foreach (var link in _links.Values)
            {
                if (link.Version < SharedVersion || link.HookGraph) continue;   // la linea la disegna la DLL
                try
                {
                    var v = link.View;
                    uint seq = v.ReadUInt32(OffOvSeq) | 1;
                    v.Write(OffOvSeq, seq);
                    Thread.MemoryBarrier();
                    v.SafeMemoryMappedViewHandle.WriteArray((ulong)(link.PixelsOffset + (long)y0 * row), pixels, y0 * row, rows * row);
                    Thread.MemoryBarrier();
                    v.Write(OffOvSeq, seq + 1);
                }
                catch { }
            }
        }
    }

    public FrameSnapshot? GetSnapshot(int pid)
    {
        FrameStats? stats = GetStats(pid);
        return stats?.Snapshot();
    }

    /// <summary>Nome dell'API grafica con cui il gioco sta presentando i frame (null se non agganciato).</summary>
    public string? GetApiName(int pid)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(pid, out var link)) return null;
            try
            {
                return link.View.ReadInt32(OffApi) switch
                {
                    1 => "DirectX",
                    2 => "DirectX 12",
                    3 => "DirectX 9",
                    4 => "OpenGL",
                    5 => "Vulkan",
                    6 => "DirectX 11",
                    7 => "DirectX 10",
                    _ => null,
                };
            }
            catch { return null; }
        }
    }

    public FrameStats? GetStats(int pid)
    {
        lock (_gate) return _links.TryGetValue(pid, out var link) ? link.Stats : null;
    }

    public void Dispose()
    {
        // Spegne limitatore e overlay nei giochi ancora aperti prima di uscire.
        lock (_gate)
        {
            foreach (var link in _links.Values)
            {
                try
                {
                    link.View.Write(OffLimiterEnabled, 0u);
                    link.View.Write(OffOverlayVisible, 0u);
                }
                catch { }
                link.Dispose();
            }
            _links.Clear();
        }
    }
}
