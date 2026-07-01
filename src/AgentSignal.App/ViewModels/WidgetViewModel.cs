using System.Collections.ObjectModel;
using AgentSignal.App.Models;
using AgentSignal.App.Services;
using AgentSignal.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentSignal.App.ViewModels;

/// <summary>
/// Drives the pill. Polls the sessions directory every ~250ms and maintains both views:
/// the collapsed aggregate (most-urgent colour — red &gt; yellow &gt; green, else off — plus the
/// driving session's timer), and a live <see cref="Sessions"/> list for the expanded view that is
/// reconciled in place (no rebuild → no flicker). The multi-session view is <b>automatic</b>
/// (<see cref="IsExpanded"/> = more than one live session); a clean click on the dots instead toggles
/// the settings gear (<see cref="IsGearVisible"/>). Also owns the widget layout the view binds to:
/// dot direction (<see cref="DotsViewModel.DotsOrientation"/>) and where the timer/gear pills attach
/// (<see cref="AttachDock"/>), plus the collapsible-timer state (<see cref="IsTimerCollapsed"/>).
/// </summary>
public partial class WidgetViewModel : DotsViewModel
{
    private readonly SessionReader _reader = new();
    private readonly DispatcherTimer? _timer;
    private readonly AlertService? _alerts;
    private readonly bool _live;
    private bool _firstRefresh = true;
    private bool _initializing;

    /// <summary>One row per live session, reused across polls so the expanded list never flickers.</summary>
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    [ObservableProperty]
    private int _sessionCount;

    /// <summary>Automatic: true whenever more than one session is live (per-session rows), else the
    /// single aggregate pill. No longer driven by clicks.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>The settings gear is revealed by clicking the dots (independent of sessions).</summary>
    [ObservableProperty]
    private bool _isGearVisible;

    // Vertical (dots stacked in a column) vs horizontal (a row). Driven from config, applied live.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttachDock))]
    [NotifyPropertyChangedFor(nameof(AttachMargin))]
    [NotifyPropertyChangedFor(nameof(SessionsStackOrientation))]
    [NotifyPropertyChangedFor(nameof(TimerHorizontal))]
    [NotifyPropertyChangedFor(nameof(TimerVertical))]
    [NotifyPropertyChangedFor(nameof(GearHorizontal))]
    [NotifyPropertyChangedFor(nameof(GearVertical))]
    private bool _isVertical;

    // Smart pill direction (Behaviour B): the window sets this true when the widget is near the far
    // edge, so the attached pills open inward (toward the screen) instead of clipping off it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttachDock))]
    [NotifyPropertyChangedFor(nameof(AttachMargin))]
    private bool _attachFlip;

    // Timer collapsed behind its chevron. Visual only — the underlying WorkTimer keeps counting — and
    // persisted so it survives a relaunch.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimerShown))]
    [NotifyPropertyChangedFor(nameof(IsTimerChevronShown))]
    private bool _isTimerCollapsed;

    /// <param name="alerts">Optional alert service; when supplied, red/green aggregate edges fire alerts.</param>
    /// <param name="live">
    /// When true (default), starts the 250ms poll. Pass false for static previews/tests, then call
    /// <see cref="PollOnce"/> manually to drive the model from outside a dispatcher.
    /// </param>
    public WidgetViewModel(AlertService? alerts = null, bool live = true)
    {
        _alerts = alerts;
        _live = live;

        // Seed layout state from config so the very first render is already correct (the window pushes
        // live changes later). Guard the persist hook so seeding doesn't write back to disk.
        _initializing = true;
        AppConfig cfg = ConfigService.Instance.Current;
        IsVertical = string.Equals(cfg.Orientation, "Vertical", StringComparison.OrdinalIgnoreCase);
        IsTimerCollapsed = cfg.TimerCollapsed;
        _initializing = false;

        // The timer/chevron visibility depends on both whether there's a value (HasTimer, which tracks
        // TimerText) and the collapse state — keep the two derived flags in sync as either changes.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasTimer) or nameof(TimerText))
            {
                OnPropertyChanged(nameof(IsTimerShown));
                OnPropertyChanged(nameof(IsTimerChevronShown));
            }
        };

        if (!live) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    // ---- Layout the view binds to ------------------------------------------------------------------
    // Where the timer/gear pills attach relative to the dots. Horizontal: below normally, above when
    // near the bottom edge. Vertical: to the right normally, to the left when near the right edge.
    public Dock AttachDock => IsVertical
        ? (AttachFlip ? Dock.Left : Dock.Right)
        : (AttachFlip ? Dock.Top : Dock.Bottom);

    /// <summary>A small gap between the dots and the attached pills, on the facing side only.</summary>
    public Thickness AttachMargin => AttachDock switch
    {
        Dock.Bottom => new Thickness(0, 3, 0, 0),
        Dock.Top => new Thickness(0, 0, 0, 3),
        Dock.Right => new Thickness(3, 0, 0, 0),
        Dock.Left => new Thickness(0, 0, 3, 0),
        _ => default,
    };

    /// <summary>Multiple session rows stack across the dots axis (rows when horizontal, columns when vertical).</summary>
    public Orientation SessionsStackOrientation => IsVertical ? Orientation.Horizontal : Orientation.Vertical;

    // Timer pinned to the "start" of the attach strip, gear to the "end", so they sit at opposite
    // corners of the dots (left/right when horizontal; top/bottom when vertical).
    public HorizontalAlignment TimerHorizontal => IsVertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    public VerticalAlignment TimerVertical => IsVertical ? VerticalAlignment.Top : VerticalAlignment.Center;
    public HorizontalAlignment GearHorizontal => IsVertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
    public VerticalAlignment GearVertical => IsVertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;

    // The timer slot shows the readout, or a chevron once collapsed — but only when there's a value.
    public bool IsTimerShown => HasTimer && !IsTimerCollapsed;
    public bool IsTimerChevronShown => HasTimer && IsTimerCollapsed;

    partial void OnIsVerticalChanged(bool value)
    {
        Orientation o = value ? Orientation.Vertical : Orientation.Horizontal;
        DotsOrientation = o;
        foreach (SessionRowViewModel row in Sessions)
            row.DotsOrientation = o;
    }

    partial void OnIsTimerCollapsedChanged(bool value)
    {
        if (_initializing || !_live) return;
        ConfigService.Instance.Update(c => c.TimerCollapsed = value); // persist across relaunch
    }

    /// <summary>Run one poll/reconcile cycle. Used by the live timer and by the --watch diagnostic.</summary>
    public void PollOnce() => Refresh();

    private void Refresh()
    {
        IReadOnlyList<SessionState> sessions = _reader.ReadLive();
        DateTime now = DateTime.UtcNow;

        // Reconcile the row list in place: update existing rows, add new ones, drop ended ones.
        // Never Clear()-then-rebuild — that would flicker the expanded list on every 250ms tick.
        var seen = new HashSet<string>(sessions.Count);
        foreach (SessionState s in sessions)
        {
            string key = Key(s);
            seen.Add(key);
            SessionRowViewModel? row = FindRow(key);
            if (row is null)
            {
                row = new SessionRowViewModel(s.Tool, s.SessionId) { DotsOrientation = DotsOrientation };
                Sessions.Add(row);
            }
            row.Observe(s, now);
        }
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (!seen.Contains(Sessions[i].Key))
                Sessions.RemoveAt(i);

        SessionCount = sessions.Count;
        IsExpanded = sessions.Count > 1; // per-session rows appear automatically for 2+ sessions

        AggregateState prev = State;
        State = Aggregate(sessions);
        TimerText = DrivingTimerText(sessions);
        TickPulse(now);
        FireAlerts(sessions, prev, State);
    }

    // Keys of sessions currently red that we've already alerted for — the per-session red debounce.
    private readonly HashSet<string> _redAlerted = new();

    // Alerts. RED is per-session and debounced: each session firing its own OnRed the instant it
    // enters red, so a SECOND session going red notifies even when the aggregate was already red, and
    // a session can't repeat-fire while it sits red. GREEN stays an aggregate edge: the pill turning
    // green means a run finished. The first poll only seeds state (so a session already red/green at
    // launch doesn't alert).
    private void FireAlerts(IReadOnlyList<SessionState> sessions, AggregateState prev, AggregateState next)
    {
        if (_alerts is null) { _firstRefresh = false; return; }

        var currentlyRed = new HashSet<string>();
        foreach (SessionState s in sessions)
            if (s.State == "red") currentlyRed.Add(Key(s));

        foreach (string key in currentlyRed)
            if (_redAlerted.Add(key) && !_firstRefresh) // newly red this poll (Add seeds silently on first poll)
                _alerts.OnRed(key);

        // Drop keys no longer red so a later return to red re-fires.
        _redAlerted.IntersectWith(currentlyRed);

        if (!_firstRefresh && next == AggregateState.Green && prev is AggregateState.Yellow or AggregateState.Red)
            _alerts.OnGreen();

        _firstRefresh = false;
    }

    private SessionRowViewModel? FindRow(string key)
    {
        foreach (SessionRowViewModel r in Sessions)
            if (r.Key == key) return r;
        return null;
    }

    private static string Key(SessionState s) => s.Tool + "__" + s.SessionId;

    /// <summary>The driving session's timer text for the collapsed pill (red wins, else most-recent).</summary>
    private string DrivingTimerText(IReadOnlyList<SessionState> sessions)
    {
        SessionState? driver = SelectDriver(sessions);
        if (driver is null) return "";
        SessionRowViewModel? row = FindRow(Key(driver));
        return row is { HasTimer: true } ? row.TimerText : "";
    }

    /// <summary>
    /// The session whose timer the collapsed pill displays: a red one needing an answer wins,
    /// otherwise the most recently active session. (Usually there's just one.)
    /// </summary>
    private static SessionState? SelectDriver(IReadOnlyList<SessionState> sessions)
    {
        SessionState? red = null, other = null;
        foreach (SessionState s in sessions)
        {
            if (s.State == "red")
            {
                if (red is null || s.Ts > red.Ts) red = s;
            }
            else if (other is null || s.Ts > other.Ts)
            {
                other = s;
            }
        }
        return red ?? other;
    }

    public static AggregateState Aggregate(IReadOnlyList<SessionState> sessions)
    {
        bool anyYellow = false, anyGreen = false;
        foreach (SessionState s in sessions)
        {
            switch (s.State)
            {
                case "red": return AggregateState.Red; // most urgent wins immediately
                case "yellow": anyYellow = true; break;
                case "green": anyGreen = true; break;
            }
        }
        if (anyYellow) return AggregateState.Yellow;
        if (anyGreen) return AggregateState.Green;
        return AggregateState.Off;
    }
}
