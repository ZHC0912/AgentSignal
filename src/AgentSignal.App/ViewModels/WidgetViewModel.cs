using System.Collections.ObjectModel;
using AgentSignal.App.Services;
using AgentSignal.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentSignal.App.ViewModels;

/// <summary>
/// Drives the pill. Polls the sessions directory every ~250ms and maintains both views:
/// the collapsed aggregate (most-urgent colour — red &gt; yellow &gt; green, else off — plus the
/// driving session's timer), and a live <see cref="Sessions"/> list for the expanded view that is
/// reconciled in place (no rebuild → no flicker). A clean click toggles <see cref="IsExpanded"/>.
/// </summary>
public partial class WidgetViewModel : DotsViewModel
{
    private readonly SessionReader _reader = new();
    private readonly DispatcherTimer? _timer;
    private readonly AlertService? _alerts;
    private bool _firstRefresh = true;

    /// <summary>One row per live session, reused across polls so the expanded list never flickers.</summary>
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    [ObservableProperty]
    private int _sessionCount;

    [ObservableProperty]
    private bool _isExpanded;

    /// <param name="alerts">Optional alert service; when supplied, red/green aggregate edges fire alerts.</param>
    /// <param name="live">
    /// When true (default), starts the 250ms poll. Pass false for static previews/tests, then call
    /// <see cref="PollOnce"/> manually to drive the model from outside a dispatcher.
    /// </param>
    public WidgetViewModel(AlertService? alerts = null, bool live = true)
    {
        _alerts = alerts;
        if (!live) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
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
                row = new SessionRowViewModel(s.Tool, s.SessionId);
                Sessions.Add(row);
            }
            row.Observe(s, now);
        }
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (!seen.Contains(Sessions[i].Key))
                Sessions.RemoveAt(i);

        SessionCount = sessions.Count;

        AggregateState prev = State;
        State = Aggregate(sessions);
        TimerText = DrivingTimerText(sessions);
        TickPulse(now);
        FireAlerts(sessions, prev, State);

        if (sessions.Count == 0)
            IsExpanded = false; // nothing left to expand
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
