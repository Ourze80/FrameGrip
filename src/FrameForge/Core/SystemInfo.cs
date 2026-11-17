using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FrameGrip.Core;

/// <summary>
/// Le voci del gruppo "Info" dell'elenco dei sensori: API grafica e tempo di gioco del gioco agganciato, versione
/// del driver della scheda video, velocità della RAM, memoria video e RAM totali. Si scelgono, si ordinano e si
/// personalizzano come gli altri sensori. Tutto è in sola lettura e costa poco: driver, RAM e memorie si leggono
/// una volta sola (registro di Windows e WMI, senza amministratore); API e tempo di gioco li aggiorna l'app.
/// </summary>
public static class SystemInfo
{
    public const string RootId = "/info", Tag = "INFO";

    // Il gioco mostrato, aggiornato dal ciclo principale (thread dell'interfaccia) e letto dal thread dei sensori.
    private static readonly object Gate = new();
    private static int _pid;
    private static bool _playing;
    private static string? _api;
    private static long _sessionStart;

    /// <summary>
    /// La finestra mostrata (pid) e la sua API: è un gioco solo se ha un'API (disegna frame). Il tempo di gioco riparte
    /// quando cambia il gioco; senza gioco resta in pausa (e torna a contare se si rientra nello stesso gioco).
    /// </summary>
    public static void SetGame(int pid, string? api)
    {
        lock (Gate)
        {
            _playing = pid != 0 && api != null;
            if (_playing && pid != _pid) { _pid = pid; _sessionStart = Environment.TickCount64; }
            _api = _playing ? api : null;
        }
    }

    // Letti una volta sola, al primo giro del thread dei sensori.
    private static bool _loaded;
    private static string? _driver;
    private static double? _vramMb, _ramGb, _ramSpeed;

    /// <summary>Memoria video totale in MB (fondo scala di "GPU Memory Used"); null se non nota.</summary>
    public static double? VramTotalMb => _vramMb;
    /// <summary>RAM totale in GB (fondo scala della memoria usata); null se non nota.</summary>
    public static double? RamTotalGb => _ramGb;

    /// <summary>Aggiunge le voci "Info" alla lettura dei sensori (una al secondo).</summary>
    public static void AddReadings(List<SensorReading> list)
    {
        if (!_loaded) { _loaded = true; Load(); }
        bool playing; string? api; long start;
        lock (Gate) { playing = _playing; api = _api; start = _sessionStart; }
        const string group = "System info";
        float? session = playing && start != 0 ? (float)((Environment.TickCount64 - start) / 1000.0) : null;
        list.Add(new SensorReading(RootId + "/api", RootId, Tag, RootId, group, "Api", "Graphics API", null, api));
        list.Add(new SensorReading(RootId + "/session", RootId, Tag, RootId, group, "Session", "Session time", session));
        list.Add(new SensorReading(RootId + "/driver", RootId, Tag, RootId, group, "Driver", "GPU driver", null, _driver));
        list.Add(new SensorReading(RootId + "/ramspeed", RootId, Tag, RootId, group, "MemSpeed", "RAM speed", (float?)_ramSpeed));
        list.Add(new SensorReading(RootId + "/vram", RootId, Tag, RootId, group, "Data", "VRAM total", _vramMb is { } v ? (float)(v / 1024) : null));
        list.Add(new SensorReading(RootId + "/ram", RootId, Tag, RootId, group, "Data", "RAM total", (float?)_ramGb));
    }

    private static void Load()
    {
        try { ReadAdapter(); } catch (Exception ex) { AppLog.Write("Info: adapter: " + ex.Message); }
        try { ReadRamTotal(); } catch (Exception ex) { AppLog.Write("Info: RAM total: " + ex.Message); }
        try { ReadRamSpeed(); } catch (Exception ex) { AppLog.Write("Info: RAM speed: " + ex.Message); }
        AppLog.Write($"Info: driver={_driver ?? "?"} vram={_vramMb?.ToString("0") ?? "?"} MB ram={_ramGb?.ToString("0.0") ?? "?"} GB speed={_ramSpeed?.ToString("0") ?? "?"} MT/s");
    }

    /// <summary>
    /// La scheda video principale dalla classe "Display" del registro: versione del driver e memoria video.
    /// Si preferisce NVIDIA, poi AMD, poi Intel (la scheda dedicata, di solito quella dei giochi).
    /// </summary>
    private static void ReadAdapter()
    {
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Registry.LocalMachine.OpenSubKey(cls);
        if (root == null) return;
        int best = int.MaxValue;
        foreach (var name in root.GetSubKeyNames())
        {
            if (name.Length != 4 || !char.IsDigit(name[0])) continue;
            using var k = root.OpenSubKey(name);
            string desc = k?.GetValue("DriverDesc") as string ?? "";
            string ver = k?.GetValue("DriverVersion") as string ?? "";
            if (ver.Length == 0) continue;
            int rank = desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? 0
                : desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? 1
                : desc.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            if (rank >= best) continue;
            best = rank;
            _driver = rank switch
            {
                0 => NvidiaVersion(ver),
                1 => k?.GetValue("RadeonSoftwareVersion") as string ?? ver,
                _ => ver,
            };
            _vramMb = k?.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long q when q > 0 => q / 1048576.0,
                byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0) / 1048576.0,
                int d when d > 0 => (uint)d / 1048576.0,
                byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0) / 1048576.0,
                _ => _vramMb,
            };
        }
    }

    /// <summary>"32.0.15.9144" diventa "591.44", come la mostra NVIDIA: le ultime cinque cifre.</summary>
    private static string NvidiaVersion(string ver)
    {
        var digits = new string(ver.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return ver;
        string last = digits[^5..];
        return int.Parse(last[..3], CultureInfo.InvariantCulture) + "." + last[3..];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static void ReadRamTotal()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref m)) _ramGb = m.TotalPhys / 1073741824.0;
    }

    /// <summary>Velocità dei moduli (MT/s) come la imposta il BIOS: WMI Win32_PhysicalMemory, una volta sola.</summary>
    private static void ReadRamSpeed()
    {
        using var q = new System.Management.ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
        double best = 0;
        foreach (var o in q.Get())
        {
            double v = Convert.ToDouble(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0, CultureInfo.InvariantCulture);
            best = Math.Max(best, v);
        }
        if (best > 0) _ramSpeed = best;
    }
}
