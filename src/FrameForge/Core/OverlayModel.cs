namespace FrameGrip.Core;

/// <summary>Una riga dell'overlay: un componente hardware con i suoi sensori scelti.</summary>
public sealed class OverlayGroup
{
    public required string Key { get; init; }           // identificativo dell'hardware
    public required string Tag { get; init; }           // GPU, CPU, RAM... (decide il colore del tema)
    public required string DefaultLabel { get; init; }  // Tag, con un numero se ce n'è più d'uno
    public string HardwareName { get; init; } = "";     // nome del modello, es. "AMD Ryzen 7 9800X3D"
    public List<(SensorItem Item, SensorReading Reading)> Sensors { get; } = new();
}

/// <summary>Decide come i sensori scelti si raggruppano in righe. Usato dall'overlay e dalle impostazioni.</summary>
public static class OverlayModel
{
    /// <summary>Righe dedicate ai frame: chiave di stile, etichetta predefinita.</summary>
    public static readonly (string Key, string Label)[] FrameGroups =
    {
        ("frame:FPS", "FPS"), ("frame:LOW", "LOW"), ("frame:FT", "FT"), ("frame:PACE", "FRAME PACING"), ("frame:API", "Game name"),
    };

    /// <summary>Etichetta predefinita di una riga dei frame, dato il suo tag interno (FPS, LOW, FT, PACE).</summary>
    public static string FrameLabel(string tag)
    {
        foreach (var (key, label) in FrameGroups)
            if (key == "frame:" + tag) return label;
        return tag;
    }

    /// <summary>Tutte le misure dei frame: identificativo, nome predefinito e pulsante che le accende.</summary>
    public static readonly (string Id, string Name, string Category, string Description)[] FrameMetrics =
    {
        ("fps", "FPS", "FPS", "Frames per second right now (last second). Higher is better."),
        ("avg", "Average FPS", "FPS", "Average frames per second over the last 10 seconds."),
        ("low1", "1% low", "LOW", "Average of the slowest 1% of frames. Shows how deep the dips go."),
        ("low01", "0.1% low", "LOW", "Average of the slowest 0.1% of frames. Shows the worst hitches."),
        ("ft", "Frametime", "FT", "Time needed to draw one frame, in milliseconds. Lower is better."),
        ("p99", "Frametime p99", "FT", "99% of frames are faster than this. Close to Frametime means smooth."),
        ("smooth", "Smoothness", "PACE", "How evenly frames are paced. 100% means perfectly regular."),
        ("jitter", "Jitter", "PACE", "Average change in frametime from one frame to the next. Lower is better."),
        ("stutter", "Stutter", "PACE", "Frames that took more than twice as long as normal, in the last 10 seconds."),
        ("limit", "Limit", "LIMIT", "The FPS limit in use right now, from the game's profile or the general one."),
    };

    /// <summary>La spiegazione nella lingua dell'interfaccia (Strings, "Metric.*"); il testo qui sopra è la riserva inglese.</summary>
    public static string FrameDescription(string id)
    {
        foreach (var m in FrameMetrics)
        {
            if (m.Id != id) continue;
            string key = "Metric." + id;
            string text = Loc.T(key);
            return text == key ? m.Description : text;
        }
        return "";
    }

    public static string FrameName(string id)
    {
        foreach (var m in FrameMetrics)
            if (m.Id == id) return m.Name;
        return id;
    }

    /// <summary>Tutte le misure dei frame, accese e spente, nell'ordine dell'utente.</summary>
    public static List<SensorItem> AllFrameItems(Settings s)
    {
        // L'elenco salvato contiene sempre tutte le misure note: quelle nuove si aggiungono in fondo.
        // Al primo passaggio restano spente quelle che l'utente aveva spento con i vecchi pulsanti di gruppo.
        foreach (var m in FrameMetrics)
        {
            if (s.FrameItems.Any(i => i.Id == m.Id)) continue;
            bool on = m.Category switch
            {
                "FPS" => s.ShowFps,
                "LOW" => s.ShowLows,
                "FT" => s.ShowFrametime,
                "LIMIT" => false,   // nuova e facoltativa: parte spenta
                _ => s.ShowPacing,
            };
            s.FrameItems.Add(new SensorItem { Id = m.Id, Hidden = on ? null : true });
        }
        s.FrameItems.RemoveAll(i => !FrameMetrics.Any(m => m.Id == i.Id));
        return s.FrameItems;
    }

    /// <summary>Le misure dei frame accese, nell'ordine dell'utente. Stanno tutte sotto la riga FPS.</summary>
    public static List<SensorItem> VisibleFrameItems(Settings s) =>
        AllFrameItems(s).Where(i => i.Hidden != true).ToList();

    public static List<OverlayGroup> Groups(Settings settings, HardwareSnapshot hw)
    {
        var groups = new List<OverlayGroup>();
        var tags = new List<string>();
        foreach (var item in settings.Sensors)
        {
            if (!hw.ById.TryGetValue(item.Id, out var r)) continue;
            var g = groups.FirstOrDefault(x => x.Key == r.RootId);
            if (g == null)
            {
                tags.Add(r.Tag);
                // Il nome del modello è quello del componente principale (non di un suo sotto-chip).
                string name = hw.All.FirstOrDefault(x => x.GroupId == r.RootId)?.GroupName ?? r.GroupName;
                g = new OverlayGroup { Key = r.RootId, Tag = r.Tag, DefaultLabel = r.Tag, HardwareName = name };
                groups.Add(g);
            }
            g.Sensors.Add((item, r));
        }

        // Due schede video o due dischi: GPU1, GPU2...
        var result = new List<OverlayGroup>(groups.Count);
        var seen = new Dictionary<string, int>();
        foreach (var g in groups)
        {
            if (tags.Count(t => t == g.Tag) <= 1)
            {
                result.Add(g);
                continue;
            }
            int n = seen[g.Tag] = seen.GetValueOrDefault(g.Tag) + 1;
            var numbered = new OverlayGroup { Key = g.Key, Tag = g.Tag, DefaultLabel = g.Tag + n, HardwareName = g.HardwareName };
            numbered.Sensors.AddRange(g.Sensors);
            result.Add(numbered);
        }
        return result;
    }
}
