using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Solo per le schermate di prova (FRAMEGRIP_IDEAL_DEMO=stato): mette la scheda "Limite ideale" in uno stato
/// che una prova vera non raggiunge facilmente (errori, risultato con cali, confronto). Il risultato viene da una
/// traccia sintetica analizzata con lo stesso codice della misura vera. Le misure finte si scrivono solo se
/// FRAMEGRIP_MEASURES punta a un file di prova.
/// </summary>
public static class IdealDemo
{
    public static void Apply(IdealMeasure ideal, Settings s, GamesPage games)
    {
        var page = games.Detail;
        string? want = Environment.GetEnvironmentVariable("FRAMEGRIP_IDEAL_DEMO");
        if (string.IsNullOrEmpty(want)) return;
        var game = new GameInfo(0, "demogame.exe", "Demo Game", 144, WindowMode.Borderless, null, 1);
        var result = IdealLimit.Analyze(new IdealInput { Raw = Trace(), Count = Trace().Length, RefreshHz = 144, Vrr = false, FgMultiplier = 1 });
        if (want.Equals("compare", StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable("FRAMEGRIP_MEASURES") is { Length: > 0 } && MeasureStore.ForGame(game.Exe).Count == 0)
            {
                string[] names = { "Ultra, DLSS Quality", "High, DLSS Balanced", "Medium, native" };
                double[] speed = { 1.0, 0.82, 0.7 };
                for (int i = 0; i < 3; i++)
                {
                    var tr = Trace(speed[i], i * 7 + 3);
                    var r = IdealLimit.Analyze(new IdealInput { Raw = tr, Count = tr.Length, RefreshHz = 144, Vrr = false, FgMultiplier = 1 });
                    var m = MeasureStore.From(r, game.Exe, game.Name, names[i]);
                    m.Date = DateTime.Now.AddMinutes(-40 + i * 12);
                    MeasureStore.Add(m);
                }
            }
            games.OpenExe(game.Exe, game.Name);
            page.OpenCompare(game.Exe);
            return;
        }
        if (Enum.TryParse<IdealState>(want, true, out var st))
        {
            games.OpenExe(game.Exe, game.Name);
            ideal.Demo(st, game, st == IdealState.Result ? result : null, st == IdealState.GameClosed ? 48 : 41);
        }
    }

    /// <summary>Due minuti a circa 140 FPS con cali periodici, più o meno marcati.</summary>
    private static float[] Trace(double speed = 1.0, int seed = 1)
    {
        var rnd = new Random(seed);
        var l = new List<float>();
        double t = 0;
        while (t < 130_000)
        {
            double ft = 7.0 / speed + rnd.NextDouble() * 0.6;
            int sec = (int)(t / 1000);
            if (sec % 4 == 0 && rnd.NextDouble() < 0.01) ft *= 1.45;
            if (sec % 17 == 3 && rnd.NextDouble() < 0.0015) ft = 22 + rnd.NextDouble() * 10;
            l.Add((float)ft);
            t += ft;
        }
        return l.ToArray();
    }
}
