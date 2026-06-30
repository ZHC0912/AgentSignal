using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AgentSignal.App.Views;

/// <summary>The reusable widget visual: the rounded pill (collapsed aggregate or expanded rows).</summary>
public partial class PillView : UserControl
{
    public PillView() => InitializeComponent();

    private void OnGearClick(object? sender, RoutedEventArgs e)
    {
        // The Button consumes the pointer, so the window's click-to-collapse never fires — the panel
        // stays expanded and we just open settings.
        e.Handled = true;
        (Application.Current as App)?.ShowSettings();
    }
}
