using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AgentSignal.App.ViewModels;

namespace AgentSignal.App.Views;

/// <summary>The reusable widget visual: the dots block plus the attached timer/chevron and gear pills.</summary>
public partial class PillView : UserControl
{
    public PillView() => InitializeComponent();

    private void OnGearClick(object? sender, RoutedEventArgs e)
    {
        // The Button consumes the pointer, so the window's dots-click never fires — we just open settings.
        e.Handled = true;
        (Application.Current as App)?.ShowSettings();
    }

    private void OnTimerClick(object? sender, RoutedEventArgs e)
    {
        // Purely visual hide/show — the WorkTimer keeps counting. The Button consumes the click, so it
        // affects only the timer (never the dots-click gear reveal). Persisted via the VM.
        e.Handled = true;
        if (DataContext is WidgetViewModel vm)
            vm.IsTimerCollapsed = !vm.IsTimerCollapsed;
    }
}
