namespace FrameGrip;

/// <summary>
/// Scala verticale del grafico del frametime (ms in cima al riquadro), comune all'anteprima e al grafico
/// disegnato nel gioco. Sale subito quando serve (un picco non resta tagliato) e scende piano, con una
/// costante di tempo di circa 1,6 s: così la linea non "respira" a ogni aggiornamento.
/// </summary>
public static class GraphScale
{
    public const double FallPer100Ms = 0.06, SpikeRoom = 1.15, MaxSpikeShown = 6;

    /// <summary>Media e massimo dei frametime che cadono dentro il grafico (x tra 0 e 1).</summary>
    public static void Range(float[] values, float[] xs, int count, out double avg, out double max)
    {
        avg = max = 0;
        int n = 0;
        // Con pochissimi FPS può non esserci nessun frame dentro i bordi: allora valgono quelli a cavallo.
        bool all = true;
        for (int i = 0; i < count; i++) if (xs[i] >= 0 && xs[i] <= 1) { all = false; break; }
        for (int i = 0; i < count; i++)
        {
            if (!all && (xs[i] < 0 || xs[i] > 1)) continue;
            double v = values[i];
            avg += v;
            if (v > max) max = v;
            n++;
        }
        avg = n > 0 ? avg / n : 0;
    }

    /// <summary>La scala voluta: almeno il doppio della media; i picchi fino a 6 volte la media entrano interi
    /// (un caricamento da mezzo secondo invece esce in alto, se no schiaccerebbe tutto il resto per secondi).</summary>
    public static double Target(double avg, double max) =>
        avg <= 0 ? 0 : Math.Max(1, Math.Max(avg * 2, Math.Min(max, avg * MaxSpikeShown) * SpikeRoom));

    /// <summary>Un passo della scala dopo <paramref name="elapsedMs"/> millisecondi.</summary>
    public static double Step(double top, double want, double elapsedMs)
    {
        if (want <= 0) return 0;
        if (want > top) return want;
        double keep = Math.Pow(1 - FallPer100Ms, Math.Clamp(elapsedMs, 0, 1000) / 100.0);
        return want + (top - want) * keep;
    }
}

/// <summary>
/// Disegna la linea del frametime direttamente nei pixel dell'immagine dell'overlay (BGRA premoltiplicato),
/// senza WPF: costa pochi microsecondi, così il grafico nel gioco si può aggiornare 60 volte al secondo.
/// Per ogni colonna di pixel si prende il tratto di linea che la attraversa e lo si ispessisce in verticale,
/// con i bordi sfumati (antialiasing).
/// </summary>
public sealed class GraphRaster
{
    private float[] _colMin = Array.Empty<float>(), _colMax = Array.Empty<float>();

    /// <summary>Colore in BGRA (byte) con alfa separato; i pixel scritti sono premoltiplicati.</summary>
    public readonly record struct Ink(byte B, byte G, byte R, byte A);

    /// <param name="px">Immagine intera (righe da width*4 byte).</param>
    /// <param name="left">Riquadro interno del grafico, in pixel dell'immagine.</param>
    /// <param name="top">Scala: ms che corrispondono al bordo alto.</param>
    public void Draw(byte[] px, int width, double left, double topY, double right, double bottomY,
        float[] values, float[] xs, int count, double top, double avg, Ink line, double lineWidth, Ink grid, double gridWidth)
    {
        if (count < 2 || top <= 0 || right - left < 2 || bottomY - topY < 2) return;
        double h = bottomY - topY, w = right - left;
        int c0 = (int)Math.Ceiling(left), c1 = (int)Math.Floor(right);   // colonne interamente dentro
        int cols = Math.Max(0, c1 - c0);
        if (_colMin.Length < cols) { _colMin = new float[cols]; _colMax = new float[cols]; }

        // linea della media
        double avgY = bottomY - h * Math.Min(avg / top, 1.0);
        for (int c = 0; c < cols; c++) Span(px, width, c0 + c, avgY - gridWidth / 2, avgY + gridWidth / 2, topY, bottomY, grid);

        for (int c = 0; c < cols; c++) { _colMin[c] = float.MaxValue; _colMax[c] = float.MinValue; }
        double prevX = 0, prevY = 0;
        for (int i = 0; i < count; i++)
        {
            double x = left + w * xs[i];
            double y = bottomY - h * Math.Min(values[i] / top, 1.0);
            if (i > 0) Segment(prevX, prevY, x, y, c0, cols);
            prevX = x;
            prevY = y;
        }
        double half = lineWidth / 2;
        for (int c = 0; c < cols; c++)
            if (_colMin[c] <= _colMax[c])
                Span(px, width, c0 + c, _colMin[c] - half, _colMax[c] + half, topY, bottomY, line);
    }

    /// <summary>Allarga, per ogni colonna attraversata dal segmento, l'intervallo verticale occupato dalla linea.</summary>
    private void Segment(double x0, double y0, double x1, double y1, int c0, int cols)
    {
        if (x1 < x0) return;
        int a = Math.Max(0, (int)Math.Floor(x0) - c0), b = Math.Min(cols - 1, (int)Math.Floor(x1) - c0);
        double slope = x1 > x0 ? (y1 - y0) / (x1 - x0) : 0;
        for (int c = a; c <= b; c++)
        {
            double xa = Math.Max(x0, c0 + c), xb = Math.Min(x1, c0 + c + 1);
            double ya = x1 > x0 ? y0 + (xa - x0) * slope : y0;
            double yb = x1 > x0 ? y0 + (xb - x0) * slope : y1;
            float lo = (float)Math.Min(ya, yb), hi = (float)Math.Max(ya, yb);
            if (lo < _colMin[c]) _colMin[c] = lo;
            if (hi > _colMax[c]) _colMax[c] = hi;
        }
    }

    /// <summary>Colora la colonna x tra y0 e y1 (frazioni di pixel sfumate), dentro [clipTop, clipBottom].</summary>
    private static void Span(byte[] px, int width, int x, double y0, double y1, double clipTop, double clipBottom, Ink ink)
    {
        y0 = Math.Max(y0, clipTop);
        y1 = Math.Min(y1, clipBottom);
        if (y1 <= y0 || x < 0 || x >= width) return;
        int r0 = (int)Math.Floor(y0), r1 = (int)Math.Ceiling(y1);
        for (int r = r0; r < r1; r++)
        {
            double cover = Math.Min(y1, r + 1) - Math.Max(y0, r);
            if (cover <= 0) continue;
            int a = (int)(ink.A * Math.Min(cover, 1.0) + 0.5);
            if (a <= 0) continue;
            int i = (r * width + x) * 4;
            if (i < 0 || i + 3 >= px.Length) continue;
            int inv = 255 - a;
            px[i] = (byte)((ink.B * a + px[i] * inv + 127) / 255);
            px[i + 1] = (byte)((ink.G * a + px[i + 1] * inv + 127) / 255);
            px[i + 2] = (byte)((ink.R * a + px[i + 2] * inv + 127) / 255);
            px[i + 3] = (byte)((255 * a + px[i + 3] * inv + 127) / 255);
        }
    }
}
