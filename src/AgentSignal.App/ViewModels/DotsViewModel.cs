using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentSignal.App.ViewModels;

/// <summary>
/// Shared base for anything that renders the three traffic-light dots plus a work timer: the
/// aggregate pill (<see cref="WidgetViewModel"/>) and each per-session row (<see cref="SessionRowViewModel"/>).
/// Both expose the same property names so a single <c>DotsView</c> control renders either one.
/// </summary>
public abstract partial class DotsViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGreenActive))]
    [NotifyPropertyChangedFor(nameof(IsYellowActive))]
    [NotifyPropertyChangedFor(nameof(IsRedActive))]
    [NotifyPropertyChangedFor(nameof(HasAgent))]
    private AggregateState _state = AggregateState.Off;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimer))]
    private string _timerText = "";

    public bool IsGreenActive => State == AggregateState.Green;
    public bool IsYellowActive => State == AggregateState.Yellow;
    public bool IsRedActive => State == AggregateState.Red;
    public bool HasAgent => State != AggregateState.Off;
    public bool HasTimer => TimerText.Length > 0;

    /// <summary>Format active work time as m:ss, switching to h:mm:ss once past an hour (§8).</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }
}
