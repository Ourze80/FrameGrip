using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// "Tasti rapidi": tutti i tasti dell'app in un posto solo (pagina Opzioni).
/// Ogni riga: cosa fa, il tasto (clic e poi la combinazione), disattiva, torna a quello di fabbrica,
/// e sotto un avviso se il tasto non si può usare. La scelta vera e propria la fa MainWindow, che riceve i tasti.
/// </summary>
public sealed class HotkeysPanel : StackPanel
{
    public static readonly int[] Ids = { 1, 2, 3 };
    private static readonly (string Title, string Desc, int Glyph)[] Info =
    {
        ("Keys.Overlay", "Keys.OverlayDesc", 0xE7B3),
        ("Keys.Record", "Keys.RecordDesc", 0xE7C8),
        ("Keys.Ideal", "Keys.IdealDesc", 0xEC4A),
    };

    private readonly Settings _s;
    private readonly Dictionary<int, Button> _keys = new();
    private readonly Dictionary<int, TextBlock> _notes = new();
    private readonly Dictionary<int, Button> _resets = new();

    public event Action<int>? Capture, Clear, Reset;

    public HotkeysPanel(Settings s)
    {
        _s = s;
        Build();
    }

    public void Build()
    {
        Children.Clear();
        _keys.Clear(); _notes.Clear(); _resets.Clear();
        for (int n = 0; n < Ids.Length; n++)
        {
            int id = Ids[n];
            var (title, desc, glyph) = Info[n];
            var row = new DockPanel { Margin = new Thickness(0, n == 0 ? 0 : 14, 0, 0) };

            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tools, Dock.Right);
            var key = new Button { MinWidth = 150, Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.SemiBold };
            key.SetResourceReference(StyleProperty, "DarkButton");
            key.ToolTip = Loc.T("Keys.ClickTip");
            AutomationProperties.SetName(key, Loc.T(title));
            key.Click += (_, _) => Capture?.Invoke(id);
            tools.Children.Add(key);
            var clear = Mini("", Loc.T("Keys.Clear"));
            clear.Click += (_, _) => Clear?.Invoke(id);
            tools.Children.Add(clear);
            var reset = Mini("", Loc.T("Keys.Reset"));
            reset.Click += (_, _) => Reset?.Invoke(id);
            tools.Children.Add(reset);
            row.Children.Add(tools);

            var icon = Text(char.ConvertFromUtf32(glyph), 15, "AccentText");
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 12, 0);
            row.Children.Add(icon);
            var texts = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            texts.Children.Add(Text(Loc.T(title), 14, "Fg"));
            var d = Text(Loc.T(desc), 12, "FgDim");
            d.TextWrapping = TextWrapping.Wrap;
            d.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(d);
            var note = Text("", 12, "Warn");
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 4, 0, 0);
            note.Visibility = Visibility.Collapsed;
            texts.Children.Add(note);
            row.Children.Add(texts);

            _keys[id] = key; _notes[id] = note; _resets[id] = reset;
            Children.Add(row);
        }
        var hint = Text(Loc.T("Keys.Hint"), 11.5, "FgDim");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 14, 0, 0);
        Children.Add(hint);
    }

    /// <summary>Riscrive tasti e avvisi. capturing = la riga in attesa di un tasto (0 = nessuna).</summary>
    public void Refresh(int capturing, Func<int, string?> note)
    {
        foreach (int id in Ids)
        {
            var (mods, vk, name) = _s.Key(id);
            var def = Settings.DefaultKey(id);
            _keys[id].Content = id == capturing ? Loc.T("Keys.Press") : vk == 0 ? Loc.T("Keys.None") : name;
            _keys[id].SetResourceReference(Control.BorderBrushProperty, id == capturing ? "Accent" : "Line");
            _resets[id].IsEnabled = mods != def.Mods || vk != def.Vk;
            string? text = note(id);
            _notes[id].Text = text ?? "";
            _notes[id].Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public void FocusKey(int id)
    {
        if (_keys.TryGetValue(id, out var b)) b.Focus();
    }

    private static Button Mini(string glyph, string tip)
    {
        var b = new Button
        {
            Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12,
            Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip,
        };
        b.SetResourceReference(StyleProperty, "DarkButton");
        AutomationProperties.SetName(b, tip);
        return b;
    }

    private static TextBlock Text(string text, double size, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }
}
