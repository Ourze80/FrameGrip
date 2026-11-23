using System.IO;

namespace FrameGrip.Core;

/// <summary>
/// Giochi che si sono chiusi per un errore subito dopo l'iniezione della DLL: da quel momento non si agganciano più
/// (overlay e limitatore spenti, misure ETW sempre attive). Elenco in %APPDATA%\FrameGrip\auto-excluded.txt,
/// un eseguibile per riga (senza .exe); l'utente può cancellare una riga per riprovare, oppure usare [force-hook]
/// in excluded-processes.txt. Con FRAMEGRIP_SETTINGS (prove) il file sta accanto alle impostazioni di prova.
/// </summary>
public static class AutoExclusions
{
    public const string FileName = "auto-excluded.txt";

    private const string Header =
        "# FrameGrip: games that crashed right after the overlay was hooked into them.\n" +
        "# FrameGrip no longer hooks them (overlay and FPS limiter off; FPS measuring still works).\n" +
        "# One process name (without .exe) per line. Delete a line to try again, or list the game\n" +
        "# under [force-hook] in excluded-processes.txt.\n";

    private static readonly object Gate = new();
    private static HashSet<string>? _names;

    public static string FilePath => Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS") is { Length: > 0 } custom
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(custom))!, FileName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameGrip", FileName);

    public static bool Contains(string exeName)
    {
        lock (Gate) return Names().Contains(Strip(exeName));
    }

    public static void Add(string exeName, string why)
    {
        string name = Strip(exeName);
        lock (Gate)
        {
            if (!Names().Add(name)) return;
            try
            {
                bool exists = File.Exists(FilePath);
                if (!exists) Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, (exists ? "" : Header.Replace("\n", Environment.NewLine)) + name + Environment.NewLine);
            }
            catch (Exception ex) { AppLog.Write("Auto-exclusions: " + ex.Message); }
        }
        AppLog.Write($"Hook: {name} auto-excluded ({why}); remove it from {FilePath} to try again");
    }

    private static string Strip(string n) => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

    private static HashSet<string> Names()
    {
        if (_names != null) return _names;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length > 0 && !line.StartsWith('#')) set.Add(Strip(line));
                }
        }
        catch (Exception ex) { AppLog.Write("Auto-exclusions: " + ex.Message); }
        return _names = set;
    }
}
