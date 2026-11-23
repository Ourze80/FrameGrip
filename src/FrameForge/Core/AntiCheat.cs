using System.IO;

namespace FrameGrip.Core;

/// <summary>
/// Riconosce i giochi protetti da un anti-cheat, prima di iniettare la DLL: iniettare codice in un gioco protetto
/// può far bannare l'account. In questi giochi overlay e limitatore restano spenti; le misure ETW (FPS) continuano,
/// perché non toccano il processo del gioco.
/// Il verdetto si calcola una volta per eseguibile (poche letture di cartelle) e resta in memoria.
/// La sezione [force-hook] di excluded-processes.txt lo scavalca, gioco per gioco.
/// </summary>
public static class AntiCheat
{
    /// <summary>Cartelle che un anti-cheat mette accanto al gioco (o una/due cartelle sopra).</summary>
    private static readonly string[] MarkerDirs = { "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "GameGuard", "XIGNCODE", "XIGNCODE3" };

    /// <summary>File (anche con *) che un anti-cheat mette accanto al gioco.</summary>
    private static readonly string[] MarkerFiles =
    {
        "EasyAntiCheat*.exe", "start_protected_game.exe", "BEService*.exe", "EAAntiCheat*", "mhypbase.dll",
    };

    /// <summary>
    /// Eseguibili (senza .exe) di giochi con anti-cheat che non lascia tracce accanto all'eseguibile
    /// (VAC, Ricochet, Vanguard, anti-cheat nel kernel...). Con * in fondo vale come inizio del nome.
    /// </summary>
    private static readonly string[] ProtectedExe =
    {
        "cs2", "csgo", "dota2", "deadlock", "valorant*", "r5apex*", "fortniteclient*", "rainbowsix*",
        "cod", "cod2*", "blackops*", "modernwarfare*", "blackopscoldwar", "vanguard",
        "league of legends", "leagueclient*", "destiny2", "escapefromtarkov", "rustclient", "tslgame",
        "gta5", "gta5_enhanced", "overwatch*", "bf2042", "bf6*", "bfv", "bf1", "fc2*", "fifa*", "madden*", "apexlegends*",
        "pubg*", "deltaforce*", "haloinfinite", "lostark", "newworld", "thefinals", "discovery",
        "genshinimpact", "yuanshen", "zenlesszonezero", "starrail", "naraka*", "eldenring", "nightreign",
        "deadbydaylight*", "huntgame", "marvel-win64-shipping", "splitgate*", "xdefiant",
    };

    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Null se si può iniettare; altrimenti il motivo (in inglese, per il registro).
    /// </summary>
    /// <param name="exePath">Percorso completo dell'eseguibile (può mancare per i processi protetti).</param>
    /// <param name="exeName">Nome del processo, senza .exe.</param>
    public static string? Check(string? exePath, string exeName)
    {
        if (UserExclusions.ForceHook.Contains(exeName)) return null;   // scelta esplicita dell'utente
        string key = string.IsNullOrEmpty(exePath) ? exeName : exePath;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var known)) return known;
            if (Cache.Count > 256) Cache.Clear();
            return Cache[key] = Detect(exePath, exeName);
        }
    }

    /// <summary>
    /// Segna un eseguibile come protetto da ora in poi (per esempio quando Windows nega l'accesso per l'iniezione):
    /// non si riprova più finché l'app resta aperta.
    /// </summary>
    public static void MarkProtected(string? exePath, string exeName, string reason)
    {
        if (UserExclusions.ForceHook.Contains(exeName)) return;
        lock (Cache) Cache[string.IsNullOrEmpty(exePath) ? exeName : exePath] = reason;
    }

    /// <summary>Il controllo vero, senza memoria (separato per le prove).</summary>
    public static string? Detect(string? exePath, string exeName)
    {
        string name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;
        foreach (string p in ProtectedExe)
        {
            bool match = p.EndsWith('*')
                ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
                : name.Equals(p, StringComparison.OrdinalIgnoreCase);
            if (match) return $"known protected game ({name})";
        }

        if (string.IsNullOrEmpty(exePath)) return null;
        try
        {
            // La cartella dell'eseguibile e fino a due cartelle sopra (i giochi Unreal stanno in Binaries\Win64).
            string? dir = Path.GetDirectoryName(exePath);
            for (int up = 0; up < 3 && !string.IsNullOrEmpty(dir); up++, dir = Path.GetDirectoryName(dir))
            {
                if (Path.GetPathRoot(dir) is { } root && string.Equals(root.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    break;   // la radice del disco non dice niente su questo gioco
                foreach (string d in MarkerDirs)
                    if (Directory.Exists(Path.Combine(dir, d))) return $"anti-cheat folder {d} in {dir}";
                foreach (string f in MarkerFiles)
                {
                    if (f.Contains('*'))
                    {
                        using var e = Directory.EnumerateFileSystemEntries(dir, f).GetEnumerator();
                        if (e.MoveNext()) return $"anti-cheat file {Path.GetFileName(e.Current)} in {dir}";
                    }
                    else if (File.Exists(Path.Combine(dir, f))) return $"anti-cheat file {f} in {dir}";
                }
            }
        }
        catch (Exception ex) { AppLog.Write("AntiCheat: " + ex.Message); }
        return null;
    }
}
