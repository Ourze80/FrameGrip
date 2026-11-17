using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Solo per le prove: con la variabile FRAMEGRIP_LAYOUT_BENCH (una cartella) l'app disegna ogni stile dell'overlay
/// con pochi e con molti sensori, misura il tempo di ogni aggiornamento dell'immagine per il gioco (OverlayBitmap,
/// lo stesso percorso usato in gioco) e salva le immagini e i tempi nella cartella; poi si chiude.
/// </summary>
internal static class LayoutBench
{
    public static readonly string? Folder = Environment.GetEnvironmentVariable("FRAMEGRIP_LAYOUT_BENCH") is { Length: > 0 } f ? f : null;
    private static readonly string[] Names = { "list", "bar", "grid", "console", "dashboard", "rings", "icons" };
    private const int Updates = 60;

    public static void Run(Settings current, HardwareSnapshot hw)
    {
        if (Folder == null) return;
        Directory.CreateDirectory(Folder);
        // i limiti di grandezza dei pannelli come su uno schermo 1920x1080 (quello di riferimento)
        OverlayView.ScreenOverride = new Size(1920, 1080);
        string tag = Environment.GetEnvironmentVariable("FRAMEGRIP_LAYOUT_BENCH_TAG") ?? "";
        // la grandezza del testo della prova (1 = 100%)
        double scale = double.TryParse(Environment.GetEnvironmentVariable("FRAMEGRIP_LAYOUT_BENCH_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1;
        var report = new StringBuilder();
        report.AppendLine("layout\tcase\tsensors\tsize\tfirst ms\tavg ms\tp95 ms\tmax ms\tvalue px\tvalue cap px\thead px\thead cap px");

        var few = current.Sensors.Select(s => s.Id).ToList();
        // "molti": come chi accende tutto (carico di ogni core, temperature, frequenze, consumi, memoria)
        var many = few.Concat(hw.All
                .Where(r => r.Value != null && r.Type is "Load" or "Temperature" or "Clock" or "Power" or "Data" or "SmallData")
                .Where(r => r.Tag is "CPU" or "GPU" or "RAM" || r.RootId.Contains("cpu") || r.RootId.Contains("gpu") || r.RootId.Contains("ram"))
                .Select(r => r.Id))
            .Distinct().Take(64).ToList();

        foreach (var (caseName, ids) in new[] { ("few", few), ("many", many) })
            for (int layout = 0; layout < Names.Length; layout++)
            {
                var s = Settings.FromJson(current.ToJson());
                s.Layout = layout;
                s.ShowGraph = true;
                s.Scale = scale;
                var keep = current.Sensors.ToDictionary(i => i.Id);
                s.Sensors = ids.Select(id => keep.TryGetValue(id, out var item) ? item : new SensorItem { Id = id }).ToList();

                var bmp = new OverlayBitmap();
                var view = new OverlayView { Settings = s };   // per l'immagine salvata, con la linea del grafico
                var rnd = new Random(7);
                var times = new List<double>();
                var sw = new Stopwatch();
                for (int i = 0; i < Updates; i++)
                {
                    var data = new OverlayData(Jitter(hw, rnd), Frames(rnd), null, "DX12", "Cyberpunk 2077");
                    sw.Restart();
                    bmp.Render(s, data, 1.0);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    view.Update(data);
                }
                var rest = times.Skip(1).OrderBy(t => t).ToList();
                // le grandezze dei caratteri usate davvero nel disegno (la più frequente), in pixel dello schermo
                view.ProbeValues = new(); view.ProbeHeads = new();
                view.LayoutTransform = new ScaleTransform(scale, scale);
                Save(view, Path.Combine(Folder, $"FrameGrip-layout-{Names[layout]}-{caseName}{tag}.png"));
                var face = new Typeface(layout == OverlayView.StyleConsole ? "Cascadia Mono" : "Segoe UI");
                double Mode(Dictionary<double, int> d) => d.Count == 0 ? 0 : d.OrderByDescending(p => p.Value).First().Key * scale;
                double vpx = Mode(view.ProbeValues), hpx = Mode(view.ProbeHeads);
                report.AppendLine(string.Join('\t', Names[layout], caseName, ids.Count, $"{bmp.Width}x{bmp.Height}",
                    times[0].ToString("0.00"), rest.Average().ToString("0.00"), rest[(int)(rest.Count * 0.95)].ToString("0.00"), rest.Max().ToString("0.00"),
                    vpx.ToString("0.0"), (vpx * face.CapsHeight).ToString("0.0"), hpx.ToString("0.0"), (hpx * face.CapsHeight).ToString("0.0")));
            }
        File.WriteAllText(Path.Combine(Folder, $"layout-bench{tag}.txt"), report.ToString());
        AppLog.Write("Layout bench:\n" + report);
        Environment.Exit(0);
    }

    public static readonly string? VerticalFolder = Environment.GetEnvironmentVariable("FRAMEGRIP_VERTICAL_TEST") is { Length: > 0 } v ? v : null;

    /// <summary>
    /// Solo per le prove: i sensori dell'utente (scheda video 8 voci, processore 4, memoria 2, FPS 3) negli stili a
    /// colonna, a 100/125/150/200% e con schermi diversi (1080p, 1440p, 4K con Windows al 150% = 2560x1440 unità WPF
    /// e immagine per il gioco a DPI 1.5). Per ognuno: colonne, misura della finestra sul desktop e dell'immagine per
    /// il gioco. Le immagini (finestra, schermo 4K al 150%) vanno nella cartella.
    /// </summary>
    public static void VerticalTest(Settings current, HardwareSnapshot hw)
    {
        if (VerticalFolder == null) return;
        Directory.CreateDirectory(VerticalFolder);
        var ids = UserIds(hw);
        var report = new StringBuilder();
        report.AppendLine($"sensors: {ids.Count} hardware + 3 FPS  ({string.Join(", ", ids)})");
        report.AppendLine("layout\tscale\tscreen\tcols\twindow px (DIP)\tgame bitmap px (dpi)");
        var screens = new[] { ("1080p", new Size(1920, 1080), 1.0), ("1440p", new Size(2560, 1440), 1.0), ("4K@150%", new Size(2560, 1440), 1.5) };
        foreach (int layout in new[] { OverlayView.StyleGrid, OverlayView.StyleConsole, OverlayView.StyleDashboard, OverlayView.StyleRings, OverlayView.StyleIcons })
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                foreach (var (sname, screen, dpi) in screens)
                {
                    OverlayView.ScreenOverride = screen;
                    var s = UserSettings(current, ids, layout, scale);
                    var data = new OverlayData(hw, Frames(new Random(3)), null, "", null);
                    var bmp = new OverlayBitmap();
                    bmp.Render(s, data, dpi);
                    var view = new OverlayView { Settings = s };
                    view.Update(data);
                    view.LayoutTransform = new ScaleTransform(scale, scale);
                    view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var win = view.DesiredSize;
                    report.AppendLine(string.Join('\t', Names[layout], $"{scale * 100:0}%", sname, view.LastColumns,
                        $"{win.Width:0}x{win.Height:0}", $"{bmp.Width}x{bmp.Height} ({dpi})"));
                    if (sname == "4K@150%")
                        Save(view, Path.Combine(VerticalFolder, $"FrameGrip-vertical-{Names[layout]}-{scale * 100:0}.png"));
                }
        File.WriteAllText(Path.Combine(VerticalFolder, "vertical-test.txt"), report.ToString());
        Environment.Exit(0);
    }

    private static Settings UserSettings(Settings current, List<string> ids, int layout, double scale)
    {
        var s = Settings.FromJson(current.ToJson());
        s.Layout = layout; s.ShowGraph = true; s.Scale = scale;
        s.Sensors = ids.Select(id => new SensorItem { Id = id }).ToList();
        s.FrameItems = new[] { "fps", "avg", "low1", "low01", "ft", "p99", "smooth", "jitter", "stutter" }
            .Select(id => new SensorItem { Id = id, Hidden = id is "fps" or "low1" or "low01" ? null : true }).ToList();
        return s;
    }

    /// <summary>I sensori dell'utente: scheda video 8 voci, processore 4, memoria 2.</summary>
    private static List<string> UserIds(HardwareSnapshot hw)
    {
        string? Pick(string root, string type, params string[] names)
        {
            foreach (var n in names)
            {
                var r = hw.All.FirstOrDefault(x => Root(x, root) && x.Type == type && x.Value != null
                    && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (r != null) return r.Id;
            }
            return hw.All.FirstOrDefault(x => Root(x, root) && x.Type == type && x.Value != null)?.Id;
        }
        // "/vram" è la memoria virtuale di Windows, non la RAM
        static bool Root(SensorReading x, string root) => x.RootId.Contains(root) && !x.RootId.StartsWith("/vram");
        var ids = new[]
        {
            Pick("gpu", "Temperature", "GPU Core"), Pick("gpu", "Load", "GPU Core"), Pick("gpu", "Clock", "GPU Core"),
            Pick("gpu", "SmallData", "GPU Memory Used"), Pick("gpu", "Voltage", "GPU Core"), Pick("gpu", "Power", "GPU Package", "GPU Power"),
            Pick("gpu", "Temperature", "GPU Hot Spot"), Pick("gpu", "Temperature", "GPU Memory Junction"),
            Pick("cpu", "Load", "CPU Total"), Pick("cpu", "Temperature", "Core (Tctl/Tdie)", "CPU Package"),
            Pick("cpu", "Clock", "Cores (Average)"), Pick("cpu", "Power", "Package"),
            Pick("ram", "Data", "Memory Used"), Pick("ram", "Load", "Memory"),
        }.Where(i => i != null).Select(i => i!).Distinct().ToList();
        return ids;
    }

    public static readonly string? VisibilityFolder = Environment.GetEnvironmentVariable("FRAMEGRIP_VISIBILITY_TEST") is { Length: > 0 } v ? v : null;

    /// <summary>
    /// Solo per le prove: ogni stile con sfondo 0% e 80% sopra una scena chiara (cielo) e una scura, con i sensori
    /// dell'utente più il carico dei core (barrette per core). Misura anche il tempo dell'immagine per il gioco.
    /// </summary>
    public static void VisibilityTest(Settings current, HardwareSnapshot hw)
    {
        if (VisibilityFolder == null) return;
        Directory.CreateDirectory(VisibilityFolder);
        OverlayView.ScreenOverride = new Size(1920, 1080);
        var ids = UserIds(hw);
        ids.AddRange(hw.All.Where(r => r.RootId.Contains("cpu") && r.Type == "Load" && r.Name.StartsWith("CPU Core #") && r.Value != null)
            .Select(r => r.Id).Take(16));
        if (WithInfo) { ids.AddRange(InfoIds(hw)); hw = WithGame(hw); }
        ids = ids.Distinct().ToList();
        var report = new StringBuilder("layout\topacity\tavg ms\tp95 ms\n");
        foreach (int layout in Enumerable.Range(0, Names.Length))
            foreach (double opacity in new[] { 0.0, 0.8 })
            {
                var s = UserSettings(current, ids, layout, 1.0);
                s.Opacity = opacity;
                var bmp = new OverlayBitmap();
                var view = new OverlayView { Settings = s };
                var rnd = new Random(7);
                var times = new List<double>();
                var sw = new Stopwatch();
                for (int i = 0; i < Updates; i++)
                {
                    var data = new OverlayData(Jitter(hw, rnd), Frames(rnd), null, "DX12", "Cyberpunk 2077");
                    sw.Restart(); bmp.Render(s, data, 1.0); sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    view.Update(data);
                }
                var rest = times.Skip(1).OrderBy(t => t).ToList();
                report.AppendLine($"{Names[layout]}\t{opacity * 100:0}%\t{rest.Average():0.00}\t{rest[(int)(rest.Count * 0.95)]:0.00}");
                foreach (var scene in new[] { "bright", "dark" })
                    Save(view, Path.Combine(VisibilityFolder, $"FrameGrip-visibility-{Names[layout]}-{opacity * 100:0}-{scene}.png"), Scene(scene == "bright"));
            }
        File.WriteAllText(Path.Combine(VisibilityFolder, "visibility-test.txt"), report.ToString());
        Environment.Exit(0);
    }

    public static readonly string? GridShotFolder = Environment.GetEnvironmentVariable("FRAMEGRIP_GRID_SHOT") is { Length: > 0 } g ? g : null;

    /// <summary>
    /// Solo per le prove: Grid HUD e Ribbon con i sensori dell'utente, sfondo 0% e 80%, sopra un'immagine di gioco
    /// (FRAMEGRIP_BACKDROP: ne usa la striscia a destra, ingrandita a coprire). Nome dei file con FRAMEGRIP_GRID_SHOT_TAG.
    /// </summary>
    public static void GridShot(Settings current, HardwareSnapshot hw)
    {
        if (GridShotFolder == null) return;
        Directory.CreateDirectory(GridShotFolder);
        OverlayView.ScreenOverride = new Size(1920, 1080);
        string tag = Environment.GetEnvironmentVariable("FRAMEGRIP_GRID_SHOT_TAG") ?? "";
        BitmapSource? back = null;
        if (Environment.GetEnvironmentVariable("FRAMEGRIP_BACKDROP") is { Length: > 0 } file && File.Exists(file))
        {
            var full = new BitmapImage(new Uri(file));
            int sx = (int)(full.PixelWidth * 0.66);
            back = new CroppedBitmap(full, new Int32Rect(sx, 0, full.PixelWidth - sx, full.PixelHeight));
        }
        Action<DrawingContext, Rect> scene = back == null ? Scene(true) : (dc, r) =>
        {
            double k = Math.Max(r.Width / back.PixelWidth, r.Height / back.PixelHeight);
            dc.PushClip(new RectangleGeometry(r));
            dc.DrawImage(back, new Rect(r.X, r.Y, back.PixelWidth * k, back.PixelHeight * k));
            dc.Pop();
        };
        var ids = UserIds(hw);
        if (WithInfo)
        {
            ids.AddRange(hw.All.Where(r => r.RootId.Contains("cpu") && r.Type == "Load" && r.Name.StartsWith("CPU Core #") && r.Value != null)
                .Select(r => r.Id).Take(16));
            ids.AddRange(InfoIds(hw));
            ids = ids.Distinct().ToList();
            hw = WithGame(hw);
        }
        string prefix = Environment.GetEnvironmentVariable("FRAMEGRIP_GRID_SHOT_PREFIX") ?? "FrameGrip-";
        double shotScale = double.TryParse(Environment.GetEnvironmentVariable("FRAMEGRIP_GRID_SHOT_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.0;
        var layouts = (Environment.GetEnvironmentVariable("FRAMEGRIP_GRID_SHOT_LAYOUTS") ?? "grid")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => Array.IndexOf(Names, n.Trim())).Where(i => i >= 0).ToArray();
        foreach (int layout in layouts)
            foreach (double opacity in new[] { 0.0, 0.8 })
            {
                var s = UserSettings(current, ids, layout, shotScale);
                s.Opacity = opacity;
                var view = new OverlayView { Settings = s };
                var rnd = new Random(5);
                for (int i = 0; i < 5; i++) view.Update(new OverlayData(Jitter(hw, rnd), Frames(rnd), null, "DirectX 12", "Phantom Blade Zero"));
                Save(view, Path.Combine(GridShotFolder, $"{prefix}{Names[layout]}{tag}-{opacity * 100:0}.png"), scene);
            }
        Environment.Exit(0);
    }

    /// <summary>Per le prove: con FRAMEGRIP_BENCH_INFO=1 si aggiungono il gruppo "Info" e le ventole.</summary>
    private static bool WithInfo => Environment.GetEnvironmentVariable("FRAMEGRIP_BENCH_INFO") == "1";

    private static IEnumerable<string> InfoIds(HardwareSnapshot hw) =>
        hw.All.Where(r => r.RootId.Contains("gpu") && r.Type == "Fan" && r.Value != null).Select(r => r.Id).Take(1)
            .Concat(hw.All.Where(r => r.RootId == SystemInfo.RootId).Select(r => r.Id));

    /// <summary>Un gioco finto agganciato: API e tempo di gioco del gruppo "Info".</summary>
    private static HardwareSnapshot WithGame(HardwareSnapshot hw) =>
        new(hw.All.Select(r => r.Id switch
        {
            SystemInfo.RootId + "/api" => r with { Text = "DX12" },
            SystemInfo.RootId + "/session" => r with { Value = 2537 },
            _ => r,
        }).ToList(), hw.Version);

    /// <summary>Una scena di gioco finta: cielo chiaro con nuvole e prato, oppure notte scura con luci e ombre.</summary>
    private static Action<DrawingContext, Rect> Scene(bool bright) => (dc, r) =>
    {
        var rnd = new Random(bright ? 11 : 12);
        if (bright)
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x7F, 0xC4, 0xF5), Color.FromRgb(0xF4, 0xFA, 0xFF), 90), null, r);
            for (int i = 0; i < 14; i++)
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), null,
                    new Point(r.X + rnd.NextDouble() * r.Width, r.Y + rnd.NextDouble() * r.Height * 0.7), 40 + rnd.Next(80), 18 + rnd.Next(30));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x9C, 0xD0, 0x6A), Color.FromRgb(0xE8, 0xE0, 0x9A), 90), null,
                new Rect(r.X, r.Y + r.Height * 0.72, r.Width, r.Height * 0.28));
        }
        else
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x05, 0x07, 0x0C), Color.FromRgb(0x22, 0x26, 0x2E), 90), null, r);
            for (int i = 0; i < 18; i++)
            {
                byte g = (byte)(20 + rnd.Next(70));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(g, g, (byte)(g + 8))), null,
                    new Rect(r.X + rnd.NextDouble() * r.Width, r.Y + rnd.NextDouble() * r.Height, 20 + rnd.Next(120), 10 + rnd.Next(90)));
            }
            for (int i = 0; i < 6; i++)
                dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(200, 255, 190, 90), Color.FromArgb(0, 255, 190, 90)), null,
                    new Point(r.X + rnd.NextDouble() * r.Width, r.Y + rnd.NextDouble() * r.Height), 30, 30);
        }
    };

    public static readonly string? SizeTestFile = Environment.GetEnvironmentVariable("FRAMEGRIP_SIZE_TEST") is { Length: > 0 } f ? f : null;

    /// <summary>Solo per le prove: grandezze del testo per stile. Lista 120%, poi Grid HUD 90%, poi di nuovo Lista:
    /// la Lista deve tornare a 120% e la sua immagine per il gioco alla stessa misura di prima.</summary>
    public static void SizeTest(Settings current, HardwareSnapshot hw)
    {
        if (SizeTestFile == null) return;
        var s = Settings.FromJson(current.ToJson());
        var data = new OverlayData(hw, Frames(new Random(1)), null, "DX12", "Cyberpunk 2077");
        string Size()
        {
            var bmp = new OverlayBitmap();
            bmp.Render(s, data, 1.0);
            return $"{bmp.Width}x{bmp.Height}";
        }
        var log = new StringBuilder();
        // come sono arrivate le impostazioni dal file (dopo la conversione delle versioni vecchie)
        log.AppendLine($"loaded: version={current.LayoutVersion} layout={current.Layout} Scale={current.Scale:0.00} stored={string.Join(", ", current.SizesByLayout.Select(kv => $"{kv.Key}:{kv.Value.Scale:0.00}"))}");
        s.Layout = 0; s.Scale = 1.2;
        string list1 = Size();
        log.AppendLine($"list  set 1.20 -> Scale={s.Scale:0.00} bitmap={list1}");
        s.Layout = 2;
        log.AppendLine($"grid  first use -> Scale={s.Scale:0.00} (takes current)");
        s.Scale = 0.9;
        string grid = Size();
        log.AppendLine($"grid  set 0.90 -> Scale={s.Scale:0.00} bitmap={grid}");
        s.Layout = 0;
        string list2 = Size();
        log.AppendLine($"list  back     -> Scale={s.Scale:0.00} bitmap={list2}  same={list1 == list2}");
        s.Layout = 2;
        log.AppendLine($"grid  back     -> Scale={s.Scale:0.00} bitmap={Size()}  same={grid == Size()}");
        // A−/A+ di una riga: anche queste per stile
        s.Layout = 0; s.StyleFor("frame:FPS").Size = 1.5;
        if (s.Sensors.Count > 0) s.Sensors[0].Size = 1.3;
        s.Layout = 2; s.StyleFor("frame:FPS").Size = 0.8;
        log.AppendLine($"grid  FPS row={s.StyleOf("frame:FPS")?.Size?.ToString() ?? "1"} sensor0={s.Sensors.FirstOrDefault()?.Size?.ToString() ?? "1"}");
        s.Layout = 0;
        log.AppendLine($"list  FPS row={s.StyleOf("frame:FPS")?.Size?.ToString() ?? "1"} sensor0={s.Sensors.FirstOrDefault()?.Size?.ToString() ?? "1"}");
        s.Layout = 2;
        log.AppendLine($"grid  FPS row={s.StyleOf("frame:FPS")?.Size?.ToString() ?? "1"} sensor0={s.Sensors.FirstOrDefault()?.Size?.ToString() ?? "1"}");
        int theme = s.Theme; s.Theme = theme + 1;
        log.AppendLine($"colour theme {theme}->{s.Theme} -> Scale={s.Scale:0.00} bitmap={Size()}");
        var saved = Settings.FromJson(s.ToJson());
        log.AppendLine($"after save/load: layout={saved.Layout} Scale={saved.Scale:0.00} stored={string.Join(", ", saved.SizesByLayout.Select(kv => $"{kv.Key}:{kv.Value.Scale:0.00}"))}");
        File.WriteAllText(SizeTestFile, log.ToString());
        Environment.Exit(0);
    }

    private static HardwareSnapshot Jitter(HardwareSnapshot hw, Random rnd) =>
        new(hw.All.Select(r => r.Value is { } v ? r with { Value = v * (float)(1 + (rnd.NextDouble() - 0.5) * 0.12) } : r).ToList(), hw.Version);

    private static FrameSnapshot Frames(Random rnd)
    {
        const int n = 240;
        var g = new float[n];
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            g[i] = (float)(7.1 + (rnd.NextDouble() - 0.5) * 1.2 + (rnd.Next(60) == 0 ? 9 : 0));
            x[i] = i / (float)(n - 1);
        }
        double fps = 141 + (rnd.NextDouble() - 0.5) * 8;
        return new FrameSnapshot(fps, 138, 118 + rnd.Next(4), 96 + rnd.Next(4), 1000 / fps, 9.4, 0.42, rnd.Next(3), 97, g, x);
    }

    /// <summary>L'overlay su uno sfondo scuro tipo scena di gioco, a grandezza reale.</summary>
    private static void Save(OverlayView view, string file, Action<DrawingContext, Rect>? backdrop = null)
    {
        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = view.DesiredSize;
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        int w = Math.Max(1, (int)Math.Ceiling(size.Width)), h = Math.Max(1, (int)Math.Ceiling(size.Height));
        var overlay = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        overlay.Render(view);

        const int m = 28;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (backdrop != null) backdrop(dc, new Rect(0, 0, w + 2 * m, h + 2 * m));
            else
            {
                var back = new LinearGradientBrush(Color.FromRgb(0x2B, 0x3E, 0x58), Color.FromRgb(0x6A, 0x5A, 0x6E), 90);
                dc.DrawRectangle(back, null, new Rect(0, 0, w + 2 * m, h + 2 * m));
            }
            dc.DrawImage(overlay, new Rect(m, m, w, h));
        }
        var final = new RenderTargetBitmap(w + 2 * m, h + 2 * m, 96, 96, PixelFormats.Pbgra32);
        final.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
