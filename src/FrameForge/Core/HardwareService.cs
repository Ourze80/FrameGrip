using LibreHardwareMonitor.Hardware;

namespace FrameGrip.Core;

/// <summary>Una lettura di un sensore. RootId raggruppa le righe dell'overlay, GroupId i nodi dell'elenco.
/// Text: per le voci che non sono numeri (API grafica, versione del driver: vedi SystemInfo).</summary>
public sealed record SensorReading(
    string Id, string RootId, string Tag, string GroupId, string GroupName,
    string Type, string Name, float? Value, string? Text = null);

public sealed class HardwareSnapshot
{
    public static readonly HardwareSnapshot Empty = new(Array.Empty<SensorReading>(), 0);

    public IReadOnlyList<SensorReading> All { get; }
    public IReadOnlyDictionary<string, SensorReading> ById { get; }
    /// <summary>Cambia solo quando cambia l'insieme dei sensori, non i loro valori.</summary>
    public int Version { get; }

    public HardwareSnapshot(IReadOnlyList<SensorReading> all, int version)
    {
        All = all;
        Version = version;
        var map = new Dictionary<string, SensorReading>(all.Count);
        foreach (var r in all) map[r.Id] = r;
        ById = map;
    }
}

/// <summary>
/// Legge tutti i sensori del PC (CPU, GPU NVIDIA/AMD/Intel, scheda madre, dischi, rete, batteria...)
/// tramite LibreHardwareMonitor, una volta al secondo su un thread dedicato.
/// </summary>
public sealed class HardwareService : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private volatile HashSet<string> _selected = new();

    public volatile HardwareSnapshot Current = HardwareSnapshot.Empty;
    /// <summary>Se falso, aggiorna solo l'hardware che ha sensori nell'overlay (meno carico in gioco).</summary>
    public volatile bool PollAll = true;
    /// <summary>
    /// Con PollAll falso, legge anche processore e scheda video (non solo l'hardware dell'overlay): servono alla
    /// misura del limite ideale (carico della GPU, VRAM, core più carico).
    /// </summary>
    public volatile bool PollCpuGpu;

    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _idle;
    /// <summary>
    /// Vero quando nessuno mostra i sensori (finestra nascosta, nessun overlay): si leggono ogni 5 secondi
    /// invece che ogni secondo. Appena torna falso la lettura riparte subito.
    /// </summary>
    public bool Idle
    {
        get => _idle;
        set
        {
            if (_idle == value) return;
            _idle = value;
            if (!value) _wake.Set();
        }
    }
    private volatile LocText? _error;
    /// <summary>Perché i sensori non funzionano, tradotto nella lingua attuale quando lo si legge.</summary>
    public string? Error => _error?.ToString();
    public volatile bool Ready;

    public HardwareService()
    {
        // sotto il normale: la lettura dei sensori non deve mai togliere tempo al gioco
        _thread = new Thread(Run) { IsBackground = true, Name = "FrameGrip sensors", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void SetSelected(IEnumerable<string> ids) => _selected = new HashSet<string>(ids);

    /// <summary>
    /// Temperature e consumi del processore passano dal driver PawnIO. Se è installato ma fermo
    /// lo si avvia (serve l'amministratore): senza, il processore mostra solo il carico.
    /// </summary>
    private void StartSensorDriver() => DriverCode = SensorDriver.Start();

    /// <summary>Esito dell'avvio del driver dei sensori (vedi SensorDriver.Start); -2 = non ancora provato.</summary>
    public volatile int DriverCode = -2;

    private readonly HashSet<string> _loggedErrors = new();   // solo dal thread dei sensori

    private void Run()
    {
        Computer computer;
        try { StartSensorDriver(); }
        catch (Exception ex) { AppLog.Write("Sensors: driver start failed: " + ex.Message); }
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
                IsMotherboardEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = true,
                IsControllerEnabled = true, IsBatteryEnabled = true, IsPsuEnabled = true,
            };
            computer.Open();
        }
        catch (Exception ex)
        {
            _error = new LocText("Sensors.Unavailable", ex.Message);
            AppLog.Write("Sensors: could not open: " + ex);
            return;
        }

        var rootOf = new Dictionary<string, string>();
        bool first = true;
        int cycle = 0;
        var waits = new[] { _stop.WaitHandle, _wake };

        while (!_stop.IsSet)
        {
            try
            {
                bool all = PollAll || first;
                bool cpuGpu = PollCpuGpu;
                var roots = new HashSet<string>();
                foreach (var id in _selected)
                    if (rootOf.TryGetValue(id, out var r)) roots.Add(r);
                cycle++;

                var list = new List<SensorReading>(256);
                foreach (var hw in computer.Hardware.ToArray())
                {
                    string rootId = hw.Identifier.ToString();
                    bool update = all || roots.Contains(rootId)
                        || (cpuGpu && hw.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);
                    // Interrogare i dischi li sveglia e può dare micro-scatti nei giochi: se nessun loro
                    // sensore è nell'overlay, li si legge solo ogni 30 secondi (bastano per l'elenco).
                    if (update && !first && hw.HardwareType == HardwareType.Storage && !roots.Contains(rootId) && cycle % 30 != 0)
                        update = false;
                    Collect(hw, rootId, TagOf(hw.HardwareType.ToString()), update, list);
                }
                SystemInfo.AddReadings(list);   // il gruppo "Info": API, tempo di gioco, driver, RAM

                int version = list.Count;
                rootOf.Clear();
                foreach (var r in list)
                {
                    rootOf[r.Id] = r.RootId;
                    version = unchecked(version * 31 + r.Id.GetHashCode());
                }

                Current = new HardwareSnapshot(list, version);
                Ready = true;
                if (first)
                {
                    // Una volta sola: quali componenti e quali sensori del processore si vedono.
                    AppLog.Write("Sensors: hardware = " + string.Join(" | ",
                        computer.Hardware.Select(h => $"{h.HardwareType}:{h.Name} ({h.Sensors.Length} sensors, {h.SubHardware.Length} sub)")));
                    foreach (var h in computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
                        AppLog.Write("Sensors: CPU = " + string.Join(", ",
                            h.Sensors.Select(s => $"{s.SensorType} {s.Name}={(s.Value.HasValue ? s.Value.Value.ToString("0.#") : "none")}")));
                }
                first = false;
            }
            catch (Exception ex)
            {
                // hardware collegato/scollegato durante la lettura: si riprova al giro dopo.
                // Ogni errore diverso si scrive nel diario una volta sola (prima si perdeva in silenzio).
                string key = ex.GetType().Name + ": " + ex.Message;
                if (_loggedErrors.Count < 50 && _loggedErrors.Add(key)) AppLog.Write("Sensors: read error: " + ex);
            }

            WaitHandle.WaitAny(waits, _idle ? 5000 : 1000);
        }

        try { computer.Close(); } catch { }
    }

    private static void Collect(IHardware hw, string rootId, string tag, bool update, List<SensorReading> list, string? parentName = null)
    {
        if (update) hw.Update();
        string groupId = hw.Identifier.ToString();
        foreach (var s in hw.Sensors)
            list.Add(new SensorReading(s.Identifier.ToString(), rootId, tag, groupId, parentName == null ? hw.Name : $"{parentName} ({hw.Name})",
                s.SensorType.ToString(), s.Name, s.Value));
        // I sensori della scheda madre stanno nel suo chip di controllo (es. "Nuvoton NCT6701D"):
        // nell'elenco si mostra il nome della scheda, con il chip tra parentesi.
        foreach (var sub in hw.SubHardware)
            Collect(sub, rootId, tag, update, list, parentName ?? hw.Name);
    }

    private static string TagOf(string hardwareType) => hardwareType switch
    {
        "Cpu" => "CPU",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU",
        "Memory" => "RAM",
        "Motherboard" or "SuperIO" or "EmbeddedController" => "MB",
        "Storage" => "SSD",
        "Network" => "NET",
        "Cooler" => "FAN",
        "Psu" => "PSU",
        "Battery" => "BAT",
        _ => "SYS",
    };

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(3000);
    }
}
