using AgentSignal.App.Services;
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

    /// <summary>True while the green dot should blink (a run just finished); drives the .pulse style.</summary>
    [ObservableProperty]
    private bool _isGreenPulsing;

    private DateTime _pulseUntilUtc = DateTime.MinValue;

    public bool IsGreenActive => State == AggregateState.Green;
    public bool IsYellowActive => State == AggregateState.Yellow;
    public bool IsRedActive => State == AggregateState.Red;
    public bool HasAgent => State != AggregateState.Off;
    public bool HasTimer => TimerText.Length > 0;

    // Reusable per-state pulse. Today only green uses it: entering green starts a configurable blink;
    // any other transition cancels it at once (so green→yellow leaves no leftover animation). A future
    // "pulse while yellow" would add an IsYellowPulsing flag and reuse this same _pulseUntilUtc /
    // TickPulse timing and the .dot.pulse animation in DotsView.
    partial void OnStateChanged(AggregateState oldValue, AggregateState newValue)
    {
        if (newValue == AggregateState.Green && oldValue != AggregateState.Green)
            StartGreenPulse();
        else
            IsGreenPulsing = false;
    }

    private void StartGreenPulse()
    {
        double secs = Math.Clamp(ConfigService.Instance.Current.BlinkOnGreenSeconds, 0, 5);
        if (secs <= 0) { IsGreenPulsing = false; return; }
        _pulseUntilUtc = DateTime.UtcNow.AddSeconds(secs);
        IsGreenPulsing = true;
    }

    /// <summary>Called from the 250ms poll: ends the blink once its window has elapsed, then it
    /// settles to the steady glow. Driving this off the existing poll avoids a second timer and keeps
    /// it working in the headless --watch path.</summary>
    protected void TickPulse(DateTime nowUtc)
    {
        if (IsGreenPulsing && nowUtc >= _pulseUntilUtc)
            IsGreenPulsing = false;
    }

    /// <summary>Format active work time as m:ss, switching to h:mm:ss once past an hour (§8).</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }
}
