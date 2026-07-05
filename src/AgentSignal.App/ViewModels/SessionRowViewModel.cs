using AgentSignal.App.Services;
using AgentSignal.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentSignal.App.ViewModels;

/// <summary>
/// One live agent session in the expanded view: its own colour and work timer. The widget keys these
/// by <see cref="Key"/> and reuses the instance across polls (updating it in place) so the expanded
/// list never rebuilds and therefore never flickers on the 250ms tick.
/// </summary>
public sealed partial class SessionRowViewModel : DotsViewModel
{
    private readonly WorkTimer _timer = new();

    public string Key { get; }
    public string Tool { get; }
    public string SessionId { get; }

    /// <summary>Mirror of the widget's ONE persisted collapse preference (there is no per-row state):
    /// <see cref="WidgetViewModel"/> seeds it at row creation and pushes changes to every row, so the
    /// expanded rows and the single pill always collapse/restore together. Clicking a row's timer or
    /// chevron toggles the widget property (the PillView-level handler), which round-trips back here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowTimerShown))]
    [NotifyPropertyChangedFor(nameof(IsRowTimerChevronShown))]
    private bool _isTimerCollapsed;

    /// <summary>True only on the LAST row while the gear is revealed and the widget is horizontal:
    /// the settings gear then renders INSIDE this row's band (right-aligned, opposite the left-pinned
    /// timer — the single pill's strip pattern), so the band is its fixed anchor and the timer chip
    /// appearing/disappearing can never displace it. Assigned by <see cref="WidgetViewModel"/> on
    /// every reconcile (the last row can change as sessions come and go).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowBandShown))]
    private bool _showsGear;

    // The chip and its collapse chevron swap in place inside the row's timer slot, exactly like the
    // single pill's strip slot. Both gate on HasTimer so the slot logic (dynamic spacing) is untouched.
    public bool IsRowTimerShown => HasTimer && !IsTimerCollapsed;
    public bool IsRowTimerChevronShown => HasTimer && IsTimerCollapsed;

    /// <summary>The row's band (the slot hanging below the dots) renders while the row has a timer OR
    /// carries the gear — so the gear keeps the same spot whether or not a timer is showing, and
    /// timer-less gear-less rows stay snug (the dynamic tight spacing is unchanged).</summary>
    public bool IsRowBandShown => HasTimer || ShowsGear;

    public SessionRowViewModel(string tool, string sessionId)
    {
        Tool = tool;
        SessionId = sessionId;
        Key = tool + "__" + sessionId;

        // HasTimer feeds both derived flags; it changes with TimerText (same relay the widget VM uses
        // for its strip flags).
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasTimer) or nameof(TimerText))
            {
                OnPropertyChanged(nameof(IsRowTimerShown));
                OnPropertyChanged(nameof(IsRowTimerChevronShown));
                OnPropertyChanged(nameof(IsRowBandShown));
            }
        };
    }

    /// <summary>Fold this poll's state into the row: advance the timer, recolour, refresh the text.
    /// The displayed colour IS the file state — no display-side guessing (Decision #3 was reversed:
    /// the light only changes on real events; a stuck yellow is cleared manually via Ctrl+Alt+R).</summary>
    public void Observe(SessionState s, DateTime nowUtc)
    {
        _timer.Observe(s, nowUtc);
        // No celebration blink for a green that isn't a real finish: a manually reset session
        // (event=ManualReset) was cleared by the user, not completed.
        QuietGreen = s.Event == SessionResetService.EventName;
        State = s.State switch
        {
            "red" => AggregateState.Red,
            "yellow" => AggregateState.Yellow,
            "green" => AggregateState.Green,
            _ => AggregateState.Off,
        };
        TimerText = _timer.HasValue ? FormatElapsed(_timer.Elapsed) : "";
        TickPulse(nowUtc);
    }
}
