using System.IO;
using System.Text;

namespace FrameGrip.Core;

/// <summary>
/// Scritture che non lasciano mai un file a metà: si scrive una copia accanto (.tmp), la si forza sul disco e poi
/// la si mette al posto dell'originale, che resta come .bak. In lettura, un file rovinato non si perde: se ne tiene
/// una copia (nome.corrupt-data.ext) e si prova il .bak.
/// </summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string text, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var w = new StreamWriter(fs, encoding ?? new UTF8Encoding(false)))
        {
            w.Write(text);
            w.Flush();
            fs.Flush(true);   // su disco davvero, non solo nella cache di Windows
        }
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); }
            catch (IOException)
            {
                // Alcuni dischi (rete, FAT) non sanno fare Replace: copia di sicurezza a mano e poi spostamento.
                File.Copy(path, path + ".bak", overwrite: true);
                File.Move(tmp, path, overwrite: true);
            }
        }
        else File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Legge e interpreta un file. Se il file c'è ma non si legge (rovinato), lo copia da parte, lo scrive nel
    /// registro e prova la copia .bak. Null se non c'è niente di valido (o se il file non esiste).
    /// </summary>
    /// <param name="what">Nome per il registro (per esempio "Settings").</param>
    public static T? Read<T>(string path, Func<string, T?> parse, string what) where T : class
    {
        bool hadMain = File.Exists(path);
        if (hadMain)
        {
            try
            {
                var value = parse(File.ReadAllText(path));
                if (value != null) return value;
                throw new InvalidDataException("empty content");
            }
            catch (Exception ex)
            {
                string kept = KeepCorrupt(path);
                AppLog.Write($"{what}: {Path.GetFileName(path)} is damaged ({ex.Message}); copy kept as {Path.GetFileName(kept)}, trying the backup");
            }
        }

        string bak = path + ".bak";
        if (!File.Exists(bak)) return null;
        try
        {
            var value = parse(File.ReadAllText(bak));
            if (value != null)
            {
                AppLog.Write($"{what}: restored from {Path.GetFileName(bak)}" + (hadMain ? "" : " (main file missing)"));
                return value;
            }
        }
        catch (Exception ex) { AppLog.Write($"{what}: backup is damaged too ({ex.Message})"); }
        return null;
    }

    /// <summary>Copia un file rovinato accanto all'originale con la data nel nome. Restituisce il nuovo percorso.</summary>
    public static string KeepCorrupt(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string kept = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
        try { File.Copy(path, kept, overwrite: true); }
        catch (Exception ex) { AppLog.Write($"Could not keep a copy of {path}: {ex.Message}"); }
        return kept;
    }
}
