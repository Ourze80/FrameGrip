using System.Windows.Media;

namespace FrameGrip;

/// <summary>Un tema colore dell'overlay. Per aggiungerne uno basta una riga in <see cref="All"/>.</summary>
public sealed class Theme
{
    public string Name { get; }
    public Color BgColor { get; }
    public Brush SwatchBg { get; }
    public Brush Value { get; }
    public Brush Unit { get; }
    public Brush Gpu { get; }
    public Brush Cpu { get; }
    public Brush Ram { get; }
    public Brush Other { get; }
    public Brush Fps { get; }
    public Brush Pace { get; }
    public Brush Shadow { get; }
    public Pen GraphPen { get; }
    public Pen GridPen { get; }

    private Theme(string name, string bg, string value, string unit,
        string gpu, string cpu, string ram, string other, string fps, string pace)
    {
        Name = name;
        BgColor = C(bg);
        SwatchBg = B(bg);
        Value = B(value);
        Unit = B(unit);
        Gpu = B(gpu);
        Cpu = B(cpu);
        Ram = B(ram);
        Other = B(other);
        Fps = B(fps);
        Pace = B(pace);

        var shadow = new SolidColorBrush(Color.FromArgb(0xD0, 0, 0, 0));
        shadow.Freeze();
        Shadow = shadow;

        GraphPen = new Pen(B(fps), 1.4) { LineJoin = PenLineJoin.Round };
        GraphPen.Freeze();
        var grid = C(unit);
        grid.A = 0x70;
        var gridBrush = new SolidColorBrush(grid);
        gridBrush.Freeze();
        GridPen = new Pen(gridBrush, 1);
        GridPen.Freeze();
    }

    public Brush Label(string tag)
    {
        string t = tag.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return t switch
        {
            "GPU" => Gpu,
            "CPU" => Cpu,
            "RAM" => Ram,
            "FPS" or "LOW" or "FT" => Fps,
            "PACE" => Pace,
            _ => Other,
        };
    }

    public static Theme Get(int index) => All[Math.Clamp(index, 0, All.Length - 1)];

    //                           nome         sfondo     valori     unità      GPU        CPU        RAM        altro      FPS        pacing
    public static Theme[] All { get; } =
    {
        new("Neon",      "#0A0C10", "#FFFFFF", "#9AA4B2", "#7CE38B", "#5CC8FF", "#C79BFF", "#6FE0D0", "#FFB454", "#FF6B9A"),
        new("Amber",     "#120D05", "#FFE9C2", "#B38A3A", "#FFB000", "#FFB000", "#FFB000", "#FFB000", "#FFD466", "#FF8A3D"),
        new("Ice",  "#08121A", "#F2FAFF", "#7C95A8", "#7FD6FF", "#7FD6FF", "#A9C4FF", "#9FE8E0", "#FFFFFF", "#B7A6FF"),
        new("Terminal", "#041008", "#D6FFE2", "#4FA36A", "#39FF7A", "#39FF7A", "#39FF7A", "#39FF7A", "#B6FF5C", "#FFE45C"),
        new("Red",     "#140808", "#FFFFFF", "#A87878", "#FF5A5A", "#FF5A5A", "#FF8C6B", "#FF8C6B", "#FFD0A8", "#FF4FA3"),
        new("Mono",      "#0C0C0C", "#FFFFFF", "#7D8794", "#B8C0CC", "#B8C0CC", "#B8C0CC", "#B8C0CC", "#FFFFFF", "#B8C0CC"),
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Brush B(string hex)
    {
        var b = new SolidColorBrush(C(hex));
        b.Freeze();
        return b;
    }
}
