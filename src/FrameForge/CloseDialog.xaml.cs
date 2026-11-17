using System.Windows;
using System.Windows.Interop;

namespace FrameGrip;

/// <summary>Domanda fatta la prima volta che si chiude la finestra: nascondere l'app o uscire.</summary>
public partial class CloseDialog : Window
{
    /// <summary>True = nascondi vicino all'orologio, false = esci, null = domanda annullata.</summary>
    public bool? HideToTray { get; private set; }

    public CloseDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Theming.ApplyTitleBar(this);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) { HideToTray = true; Close(); }
    private void Exit_Click(object sender, RoutedEventArgs e) { HideToTray = false; Close(); }
}
