using System.IO;

namespace FrameGrip.Core;

/// <summary>
/// Diario dell'app: %LOCALAPPDATA%\FrameGrip\app.log. Serve a capire dopo cosa è successo
/// (quale gioco è stato agganciato, cosa non ha funzionato) senza dover riprodurre il problema.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    // FRAMEGRIP_LOG sceglie un altro file (solo per le prove: non scrivono nel diario dell'utente)
    private static readonly string FilePath = Environment.GetEnvironmentVariable("FRAMEGRIP_LOG") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameGrip", "app.log");
    private static readonly string OldPath = Path.ChangeExtension(FilePath, ".old.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                // non cresce all'infinito, ma la parte vecchia resta in app.old.log (serve a capire un problema appena passato)
                if (info.Exists && info.Length > 512 * 1024) File.Move(FilePath, OldPath, overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* il diario non deve mai far cadere l'app */ }
    }
}
