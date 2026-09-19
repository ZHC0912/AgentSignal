using System.Collections.ObjectModel;
using System.Linq;
using AgentSignal.App.Models;
using AgentSignal.App.Services;
using AgentSignal.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    private readonly SessionReader _reader;
    private readonly DispatcherTimer? _timer;
    private readonly AlertService? _alerts;
    private readonly bool _live;
    private bool _firstRefresh = true;
    private bool _initializing;

    /// <summary>The VISIBLE rows — what the expanded view renders. A subset of <see cref="AllSessions"/>
    /// (hidden sessions are left out), kept in sync IN PLACE so the list never rebuilds → never flickers.</summary>
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    /// <summary>EVERY tracked session, hidden ones included — the model keeps observing a hidden
    /// session (its timer runs, its state stays current) so unhiding it is instant and correct. This
    /// is also what Settings → Session Tracking lists, live.</summary>
    public ObservableCollection<SessionRowViewModel> AllSessions { get; } = new();

    /// <summary>Keys ("&lt;tool&gt;__&lt;sessionId&gt;") the user has hidden. Persisted, so a hidden pill
    /// stays hidden across new hook events AND across a relaunch — but only until that exact session
    /// ends, at which point the key is pruned (a brand-new session in the same folder shows normally).</summary>
    private readonly HashSet<string> _hidden;

    /// <summary>Count of VISIBLE sessions (hidden ones don't drive the pill).</summary>
    [ObservableProperty]
    private int _sessionCount;

    /// <summary>Automatic: true whenever more than one session is live (per-session rows), else the
    /// single aggregate pill. No longer driven by clicks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimerShown))]
    [NotifyPropertyChangedFor(nameof(IsTimerChevronShown))]
    [NotifyPropertyChangedFor(nameof(IsStripGearVisible))]
    [NotifyPropertyChangedFor(nameof(IsLabelShown))]
    private bool _isExpanded;

    /// <summary>The settings gear is revealed by clicking the dots (independent of sessions).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStripGearVisible))]
    [NotifyPropertyChangedFor(nameof(IsEndGearVisible))]
    private bool _isGearVisible;

    /// <summary>The attach-strip gear shows only for the single aggregate pill. In the expanded view
    /// the gear joins the session stack instead: HORIZONTAL puts it INSIDE the bottom row's band
    /// (right-aligned beside that row's timer — see <see cref="SessionRowViewModel.ShowsGear"/> — so
    /// the timer appearing/disappearing can never displace it); VERTICAL keeps it after the last
    /// column (<see cref="IsEndGearVisible"/>), where the columns' heights can't move it either.</summary>
    public bool IsStripGearVisible => IsGearVisible && !IsExpanded;

    /// <summary>The gear pill at the END of the expanded session stack — vertical orientation only
    /// (top-aligned beside the last column, a spot no timer can displace). Horizontal instead anchors
    /// the gear inside the bottom row's band via <see cref="SessionRowViewModel.ShowsGear"/>.</summary>
    public bool IsEndGearVisible => IsGearVisible && IsVertical;

    // Vertical (dots stacked in a column) vs horizontal (a row). Driven from config, applied live.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttachDock))]
    [NotifyPropertyChangedFor(nameof(AttachMargin))]
    [NotifyPropertyChangedFor(nameof(SessionsStackOrientation))]
    [NotifyPropertyChangedFor(nameof(LabelDock))]
    [NotifyPropertyChangedFor(nameof(LabelMargin))]
    [NotifyPropertyChangedFor(nameof(TimerHorizontal))]
    [NotifyPropertyChangedFor(nameof(TimerVertical))]
    [NotifyPropertyChangedFor(nameof(GearHorizontal))]
    [NotifyPropertyChangedFor(nameof(GearVertical))]
    [NotifyPropertyChangedFor(nameof(GearMargin))]
    [NotifyPropertyChangedFor(nameof(TimerChevronGlyph))]
    [NotifyPropertyChangedFor(nameof(IsEndGearVisible))]
    private bool _isVertical;

    // Smart pill direction (Behaviour B): the window sets this true when the widget is near the far
    // edge, so the attached pills open inward (toward the screen) instead of clipping off it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttachDock))]
    [NotifyPropertyChangedFor(nameof(AttachMargin))]
    [NotifyPropertyChangedFor(nameof(TimerHorizontal))]
    [NotifyPropertyChangedFor(nameof(GearHorizontal))]
    [NotifyPropertyChangedFor(nameof(TimerChevronGlyph))]
    [NotifyPropertyChangedFor(nameof(LabelDock))]
    [NotifyPropertyChangedFor(nameof(LabelMargin))]
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
    /// <param name="sessionsDir">Override the sessions directory (diagnostics only, e.g. --label-test
    /// against a temp dir); null = the real ~/.agentsignal/sessions.</param>
    public WidgetViewModel(AlertService? alerts = null, bool live = true, string? sessionsDir = null)
    {
        _alerts = alerts;
        _live = live;
        _reader = new SessionReader(sessionsDir);

        // Seed layout state from config so the very first render is already correct (the window pushes
        // live changes later). Guard the persist hook so seeding doesn't write back to disk.
        _initializing = true;
        AppConfig cfg = ConfigService.Instance.Current;
        IsVertical = string.Equals(cfg.Orientation, "Vertical", StringComparison.OrdinalIgnoreCase);
        IsTimerCollapsed = cfg.TimerCollapsed;
        _hidden = new HashSet<string>(cfg.HiddenSessions ?? new List<string>(), StringComparer.Ordinal);
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
            else if (e.PropertyName is nameof(HasLabel) or nameof(FullLabel))
            {
                OnPropertyChanged(nameof(IsLabelShown));
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

    /// <summary>
    /// Where the label chip attaches: the edge OPPOSITE the timer/gear strip when horizontal (strip
    /// below → label above, and vice versa near the bottom edge), and always above the column when
    /// vertical (the strip is beside it there, so the top is free). Docked before everything else, so
    /// it spans the widget's full width and the dots keep their own anchor.
    /// </summary>
    public Dock LabelDock => IsVertical
        ? Dock.Top
        : (AttachFlip ? Dock.Bottom : Dock.Top);

    /// <summary>A small nudge in from the dots pill's left edge so the label doesn't sit hard against it.</summary>
    private const double LabelInset = 8;

    /// <summary>Footprint of the attach strip when it docks to the LEFT of the dots (vertical + flip):
    /// the fixed 84px reservation slot plus its 3px facing gap. The label is offset by it so the chip
    /// still lines up with the dots column rather than floating above the strip.</summary>
    private const double VerticalStripWidth = 87;

    /// <summary>The label chip's own gap: the same 3px the strip uses on the side facing the dots, plus
    /// the inward nudge (and the strip offset when the strip sits to the left of the dots). The chip is
    /// always LEFT-anchored inside its band, so expanding it on hover grows it rightward from a fixed
    /// left edge — in place, and with nothing below it to push.</summary>
    public Thickness LabelMargin
    {
        get
        {
            double left = LabelInset + (IsVertical && AttachFlip ? VerticalStripWidth : 0);
            return LabelDock == Dock.Bottom
                ? new Thickness(left, 3, 0, 0)
                : new Thickness(left, 0, 0, 3);
        }
    }

    /// <summary>The single pill's label chip. Hidden while expanded — each session row then carries
    /// its own label, so a second (driver-hopping) copy would be redundant.</summary>
    public bool IsLabelShown => HasLabel && !IsExpanded;

    /// <summary>Multiple session rows stack across the dots axis (rows when horizontal, columns when vertical).</summary>
    public Orientation SessionsStackOrientation => IsVertical ? Orientation.Horizontal : Orientation.Vertical;

    // Horizontal: timer pinned to the strip's left, gear to its right — opposite corners of the dots.
    // Vertical: both pills HUG the dots-facing edge of the strip (left edge when the strip is on the
    // right of the dots, right edge when flipped to the left) so they sit close beside the column
    // instead of floating in the 84px-wide reservation, and the gear stacks DIRECTLY BELOW the
    // timer/chevron (GearMargin = the slot's 26px + a 4px gap) so the pair reads as one tight cluster.
    public HorizontalAlignment TimerHorizontal => IsVertical
        ? (AttachFlip ? HorizontalAlignment.Right : HorizontalAlignment.Left)
        : HorizontalAlignment.Left;
    public VerticalAlignment TimerVertical => IsVertical ? VerticalAlignment.Top : VerticalAlignment.Center;
    public HorizontalAlignment GearHorizontal => IsVertical
        ? (AttachFlip ? HorizontalAlignment.Right : HorizontalAlignment.Left)
        : HorizontalAlignment.Right;
    public VerticalAlignment GearVertical => IsVertical ? VerticalAlignment.Top : VerticalAlignment.Center;
    public Thickness GearMargin => IsVertical ? new Thickness(0, 30, 0, 0) : default;

    // The attach-strip timer slot shows the driving-session readout, or a chevron once collapsed — but
    // only when there's a value AND we're not expanded. In the expanded (2+ session) view each row
    // carries its own independent timer, so the single shared readout is hidden to avoid a redundant,
    // driver-hopping second number.
    public bool IsTimerShown => HasTimer && !IsTimerCollapsed && !IsExpanded;
    public bool IsTimerChevronShown => HasTimer && IsTimerCollapsed && !IsExpanded;

    // Collapse chevron points the way the timer would reappear: '‹' when the strip opens leftward
    // (vertical widget flipped to the left near the right edge), otherwise the default '›'. The other
    // three layouts (horizontal below/above, vertical-right) all read correctly as '›'.
    public string TimerChevronGlyph => AttachDock == Dock.Left ? "‹" : "›";

    partial void OnIsVerticalChanged(bool value)
    {
        Orientation o = value ? Orientation.Vertical : Orientation.Horizontal;
        DotsOrientation = o;
        foreach (SessionRowViewModel row in AllSessions)
            row.DotsOrientation = o;
        UpdateRowGear();
    }

    partial void OnIsGearVisibleChanged(bool value) => UpdateRowGear();

    /// <summary>Anchor the expanded-view gear for HORIZONTAL orientation: exactly the LAST row (and
    /// only while the gear is revealed) carries it inside its band. Re-run whenever the row list, the
    /// gear visibility or the orientation changes — the last row moves as sessions come and go.
    /// Vertical rows never carry it (the stack-end gear, <see cref="IsEndGearVisible"/>, does).</summary>
    private void UpdateRowGear()
    {
        bool show = IsGearVisible && !IsVertical;
        for (int i = 0; i < Sessions.Count; i++)
            Sessions[i].ShowsGear = show && i == Sessions.Count - 1;
    }

    partial void OnIsTimerCollapsedChanged(bool value)
    {
        // One collapse preference for the whole widget: the expanded rows mirror it (their chips and
        // chevrons swap together, and the state carries over when sessions drop back to one).
        foreach (SessionRowViewModel row in AllSessions)
            row.IsTimerCollapsed = value;
        if (_initializing || !_live) return;
        ConfigService.Instance.Update(c => c.TimerCollapsed = value); // persist across relaunch
    }

    // ---- Hide / show one session -------------------------------------------------------------------
    // Hiding is per EXACT session (tool + session id), never per folder: a new session in the same
    // directory gets a different id and appears normally. A hidden session keeps being tracked — the
    // row stays in AllSessions and keeps observing — it just doesn't render a pill and doesn't count
    // toward the aggregate colour, the driving timer or the alerts.

    /// <summary>Right-click → "Hide" on the SINGLE (collapsed) pill: hides whichever session that pill
    /// is currently showing. Each expanded row has its own Hide command for its own session.</summary>
    [RelayCommand]
    private void HideDriving()
    {
        if (_drivingRow is not null)
            _drivingRow.IsShown = false;
    }

    /// <summary>A row's visibility changed (right-click → Hide, or the Settings checkbox): persist the
    /// hidden set and re-sync the visible pills immediately, rather than waiting for the next poll.</summary>
    private void OnRowShownChanged(SessionRowViewModel row)
    {
        bool changed = row.IsShown ? _hidden.Remove(row.Key) : _hidden.Add(row.Key);
        if (changed) PersistHidden();

        SyncVisibleRows();
        UpdateRowGear();
        SessionCount = Sessions.Count;
        IsExpanded = Sessions.Count > 1;
        // The pill it was driving may have just vanished (or come back) — the next poll recomputes
        // colour/timer/label from the live files in a few ms, which is soon enough and keeps ONE code
        // path for the aggregate.
    }

    /// <summary>Drop hidden keys whose session is no longer live, so the set survives a relaunch but
    /// never outlives the session it refers to.</summary>
    private void PruneHidden(HashSet<string> liveKeys)
    {
        if (_hidden.Count == 0) return;
        if (_hidden.RemoveWhere(k => !liveKeys.Contains(k)) > 0)
            PersistHidden();
    }

    private void PersistHidden()
    {
        if (_initializing || !_live) return;
        ConfigService.Instance.Update(c => c.HiddenSessions = _hidden.ToList());
    }

    /// <summary>Mirror the shown subset of <see cref="AllSessions"/> into <see cref="Sessions"/>
    /// IN PLACE (insert/move/remove), preserving instances so the bound list never rebuilds.</summary>
    private void SyncVisibleRows()
    {
        int target = 0;
        foreach (SessionRowViewModel row in AllSessions)
        {
            if (!row.IsShown) continue;
            int current = IndexOfFrom(row, target);
            if (current < 0) Sessions.Insert(target, row);
            else if (current != target) Sessions.Move(current, target);
            target++;
        }
        for (int i = Sessions.Count - 1; i >= target; i--)
            Sessions.RemoveAt(i);
    }

    private int IndexOfFrom(SessionRowViewModel row, int start)
    {
        for (int i = start; i < Sessions.Count; i++)
            if (ReferenceEquals(Sessions[i], row)) return i;
        return -1;
    }

    /// <summary>The session states whose rows are currently shown (hidden ones filtered out).</summary>
    private List<SessionState> VisibleOnly(IReadOnlyList<SessionState> sessions)
    {
        var visible = new List<SessionState>(sessions.Count);
        foreach (SessionState s in sessions)
            if (!_hidden.Contains(Key(s)))
                visible.Add(s);
        return visible;
    }

    /// <summary>The hidden keys as they stand right now — for diagnostics (--label-test asserts that a
    /// key is dropped once its session ends).</summary>
    public IReadOnlyCollection<string> HiddenKeys => _hidden;

    /// <summary>Run one poll/reconcile cycle. Used by the live timer and by the --watch diagnostic.</summary>
    public void PollOnce() => Refresh();

    /// <summary>
    /// Manual reset (Settings button / Ctrl+Alt+R): force every current session green as if its turn
    /// completed — each timer freezes at its current value as the last run's time. The rewritten files
    /// make it real (not display-only), so the next UserPromptSubmit starts a fresh yellow+timer
    /// normally. One-shot quiet: the green edge this produces fires no alert and no blink.
    /// </summary>
    public void ResetAllSessions()
    {
        if (SessionResetService.ForceGreen(DateTime.UtcNow) == 0)
            return; // nothing was cleared — don't muffle a later real finish
        _quietReset = true;
        Refresh(); // apply immediately (and consume the quiet flag on this pass)
    }

    // One-shot: the refresh right after a manual reset must not celebrate the forced green.
    private bool _quietReset;

    private void Refresh()
    {
        IReadOnlyList<SessionState> sessions = _reader.ReadLive();
        DateTime now = DateTime.UtcNow;

        // Reconcile the TRACKED row list in place: update existing rows, add new ones, drop ended ones.
        // Never Clear()-then-rebuild — that would flicker the expanded list on every 250ms tick.
        // Hidden sessions are reconciled and observed exactly like any other: hiding is a display
        // decision, so the timer keeps running underneath and unhiding is instantly correct.
        var seen = new HashSet<string>(sessions.Count);
        foreach (SessionState s in sessions)
        {
            string key = Key(s);
            seen.Add(key);
            SessionRowViewModel? row = FindRow(key);
            if (row is null)
            {
                row = new SessionRowViewModel(s.Tool, s.SessionId, OnRowShownChanged, shown: !_hidden.Contains(key))
                {
                    DotsOrientation = DotsOrientation,
                    IsTimerCollapsed = IsTimerCollapsed,
                };
                AllSessions.Add(row);
            }
            row.Observe(s, now);
        }
        for (int i = AllSessions.Count - 1; i >= 0; i--)
            if (!seen.Contains(AllSessions[i].Key))
                AllSessions.RemoveAt(i);

        // A hide lasts only as long as its session: once the session file is gone, drop the key so a
        // brand-new session (new id, even the same folder) is never born hidden.
        PruneHidden(seen);
        SyncVisibleRows();

        // Only VISIBLE sessions drive the pill — colour, timer and alerts. A hidden session the user
        // dismissed must not light the widget red or beep from behind the curtain.
        List<SessionState> visible = VisibleOnly(sessions);
        UpdateRowGear(); // adds/removals can change which row is last (the gear's horizontal anchor)

        SessionCount = visible.Count;
        IsExpanded = visible.Count > 1; // per-session rows appear automatically for 2+ sessions

        // The DISPLAYED colour honours the stale-yellow demotion (Decision #3); alerts are keyed to
        // the REAL states below, so a demotion (a guess) never beeps, and if the demotion was wrong
        // (the turn was still working) the eventual real finish still fires its green alert.
        AggregateState prevReal = _realState;
        _realState = Aggregate(visible);
        // Quiet green = no celebration blink for the green right after a manual reset (a clear,
        // not a finish). The displayed State IS the real aggregate — no display-side demotion
        // (Decision #3 reversed: the light only changes on real events).
        QuietGreen = _quietReset;
        State = _realState;
        UpdateDriving(visible); // the collapsed pill's timer AND label come from the driving session
        TickPulse(now);
        FireAlerts(visible, prevReal, _realState);
        _quietReset = false; // one-shot, consumed by the pass that follows the reset
    }

    // The previous poll's aggregate, kept for computing the alert edges.
    private AggregateState _realState = AggregateState.Off;

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

        // The green edge alerts on a real finish only — a manual reset forces the same edge but is
        // the user clearing state, not a run completing, so it stays silent (_quietReset one-shot).
        if (!_firstRefresh && !_quietReset &&
            next == AggregateState.Green && prev is AggregateState.Yellow or AggregateState.Red)
            _alerts.OnGreen();

        _firstRefresh = false;
    }

    private SessionRowViewModel? FindRow(string key)
    {
        foreach (SessionRowViewModel r in AllSessions)
            if (r.Key == key) return r;
        return null;
    }

    private static string Key(SessionState s) => s.Tool + "__" + s.SessionId;

    /// <summary>The row the collapsed pill is currently showing (red wins, else most recent). Kept so
    /// the right-click → Hide on the single pill knows which exact session to hide.</summary>
    private SessionRowViewModel? _drivingRow;

    /// <summary>Point the collapsed pill at its driving session: its timer text and its label
    /// (folder · tool). With nothing visible, both clear and the chips disappear.</summary>
    private void UpdateDriving(IReadOnlyList<SessionState> sessions)
    {
        SessionState? driver = SelectDriver(sessions);
        _drivingRow = driver is null ? null : FindRow(Key(driver));
        if (_drivingRow is null)
        {
            TimerText = "";
            ClearLabel();
            return;
        }
        TimerText = _drivingRow.HasTimer ? _drivingRow.TimerText : "";
        InitialsLabel = _drivingRow.InitialsLabel;
        FullLabel = _drivingRow.FullLabel;
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
