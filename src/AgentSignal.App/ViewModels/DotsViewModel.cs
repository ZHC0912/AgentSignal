using AgentSignal.App.Services;
using AgentSignal.Core;
using Avalonia.Layout;
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

    /// <summary>
    /// The RESTING label: just the two initials, "A · C" (folder · agent), so the pill stays tiny.
    /// Empty when nothing is being shown (no session), which hides the label chip entirely.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelText))]
    private string _initialsLabel = "";

    /// <summary>The whole "folder · Tool" — what the label expands to on hover, and what
    /// Settings → Session Tracking lists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLabel))]
    [NotifyPropertyChangedFor(nameof(LabelText))]
    private string _fullLabel = "";

    /// <summary>
    /// True while the pointer is over this pill (the view sets it on enter/leave): the label chip then
    /// shows <see cref="FullLabel"/> instead of <see cref="InitialsLabel"/> — the label expands IN
    /// PLACE, replacing the old static tooltip. The chip grows rightward inside its own band; the dots,
    /// timer and gear are laid out independently of it and never move (proved by --anchor-test).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelText))]
    private bool _isLabelExpanded;

    /// <summary>What the label chip renders right now: initials at rest, the full label on hover.</summary>
    public string LabelText => IsLabelExpanded ? FullLabel : InitialsLabel;

    /// <summary>Lay the three dots out in a row (Horizontal) or a column (Vertical). Set from config by
    /// <see cref="WidgetViewModel"/> and propagated to each session row so every DotsView matches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowTimerHorizontal))]
    private Orientation _dotsOrientation = Orientation.Horizontal;

    private DateTime _pulseUntilUtc = DateTime.MinValue;

    /// <summary>When true, the next entry into green starts no celebration blink. Set (every tick,
    /// BEFORE assigning <see cref="State"/>) for a green that isn't a real finish — today that's the
    /// manual reset (event=ManualReset): the user cleared the state, nothing completed. A real green
    /// transition assigns this false first, so the blink behaves exactly as before.</summary>
    protected bool QuietGreen { get; set; }

    /// <summary>Where a per-session row's timer chip sits inside its reserved slot below the dots:
    /// pinned LEFT under a horizontal dots row (matching the single pill's timer, which pins to the
    /// strip's left edge), centred under a vertical dots column.</summary>
    public HorizontalAlignment RowTimerHorizontal =>
        DotsOrientation == Orientation.Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;


    public bool IsGreenActive => State == AggregateState.Green;
    public bool IsYellowActive => State == AggregateState.Yellow;
    public bool IsRedActive => State == AggregateState.Red;
    public bool HasAgent => State != AggregateState.Off;
    public bool HasTimer => TimerText.Length > 0;
    public bool HasLabel => FullLabel.Length > 0;

    /// <summary>Set both label forms from a session's tool + working directory (see <see cref="SessionLabel"/>).
    /// Assigning identical values raises nothing, so this is safe to call on every 250ms poll.</summary>
    protected void SetLabel(string tool, string? cwd)
    {
        InitialsLabel = SessionLabel.Initials(tool, cwd);
        FullLabel = SessionLabel.Full(tool, cwd);
    }

    /// <summary>Clear the label (nothing to show).</summary>
    protected void ClearLabel()
    {
        InitialsLabel = "";
        FullLabel = "";
        IsLabelExpanded = false;
    }

    // Reusable per-state pulse. Today only green uses it: entering green starts a configurable blink;
    // any other transition cancels it at once (so green→yellow leaves no leftover animation). A future
    // "pulse while yellow" would add an IsYellowPulsing flag and reuse this same _pulseUntilUtc /
    // TickPulse timing and the .dot.pulse animation in DotsView.
    partial void OnStateChanged(AggregateState oldValue, AggregateState newValue)
    {
        if (newValue == AggregateState.Green && oldValue != AggregateState.Green && !QuietGreen)
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
