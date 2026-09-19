using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AgentSignal.App.ViewModels;

namespace AgentSignal.App.Views;

/// <summary>The reusable widget visual: the dots block plus the attached timer/chevron and gear pills.</summary>
public partial class PillView : UserControl
{
    public PillView() => InitializeComponent();

    /// <summary>
    /// Pointer entered/left a pill (the whole widget for the single pill, one row in the multi-session
    /// view): the label chip expands in place to the full "folder · Tool" and collapses back to its
    /// initials. Handled here rather than by binding <c>IsPointerOver</c> so the headless --anchor-test
    /// can drive the same flag and prove the dots don't budge.
    ///
    /// The sender's DataContext is the WidgetViewModel for the single pill and that row's
    /// SessionRowViewModel inside the ItemsControl template — both are DotsViewModels, so one handler
    /// serves both and a row only ever expands its OWN label.
    /// </summary>
    private void OnLabelHoverChanged(object? sender, PointerEventArgs e)
    {
        // Keyed off WHICH event fired rather than IsPointerOver, so it can't depend on whether Avalonia
        // updates that property before or after raising the leave event.
        if (sender is Control { DataContext: DotsViewModel vm })
            vm.IsLabelExpanded = e.RoutedEvent == PointerEnteredEvent;
    }

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
