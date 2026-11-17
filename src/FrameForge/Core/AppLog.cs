using System.IO;

namespace FrameGrip.Core;

/// <summary>
/// Diario dell'app: %LOCALAPPDATA%\FrameGrip\app.log. Serve a capire dopo cosa è successo
/// (quale gioco è stato agganciato, cosa non ha funzionato) senza dover riprodurre il problema.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameGrip", "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024) File.Delete(FilePath);   // non cresce all'infinito
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* il diario non deve mai far cadere l'app */ }
    }
}
