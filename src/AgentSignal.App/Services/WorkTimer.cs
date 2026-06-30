using AgentSignal.Core;

namespace AgentSignal.App.Services;

/// <summary>
/// Per-session "active work time" stopwatch, driven entirely by state transitions (CLAUDE.md §8).
/// <list type="bullet">
///   <item><b>yellow</b> — accumulate wall-clock time.</item>
///   <item><b>red</b> — pause (hold the value); the user is answering a permission prompt.</item>
///   <item><b>green</b> — freeze (keep showing the last run's time).</item>
///   <item>new run (green/off → yellow via UserPromptSubmit) — reset to 0 and start again.</item>
/// </list>
/// On the <b>red → PostToolUse</b> transition the forwarded <see cref="SessionState.DurationMs"/> is
/// back-credited: the approved tool's execution time elapsed while we were showing red (no hook fires
/// at the instant of approval — see Phase 0), so we add it back, while the time spent waiting on the
/// prompt stays excluded. This is locked Decision #1. (A PostToolUse during an ordinary yellow run is
/// already counted tick-by-tick, so it is deliberately <i>not</i> back-credited there.)
///
/// All time comes from the injected <paramref name="nowUtc"/>, so the logic is deterministic/testable.
/// </summary>
public sealed class WorkTimer
{
    private double _accumulatedMs;
    private bool _running;
    private DateTime _lastTickUtc;
    private string _prevState = ""; // "" = never observed yet

    /// <summary>Active work time measured so far for the current (or last finished) run.</summary>
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(_accumulatedMs);

    /// <summary>True while the timer is counting (the session is yellow).</summary>
    public bool Running => _running;

    /// <summary>
    /// True once any work time exists, so a brand-new idle (green) session with no run yet shows
    /// nothing rather than a misleading "0:00".
    /// </summary>
    public bool HasValue => _running || _accumulatedMs > 0;

    /// <summary>Fold one observation of a session's state into the timer. Call every poll tick.</summary>
    public void Observe(SessionState s, DateTime nowUtc)
    {
        string cur = s.State;

        // Bank the wall-clock elapsed since the previous observation, but only while we were running.
        // (Doing this every tick — not just on transitions — is what makes a long yellow stretch count.)
        if (_running)
            _accumulatedMs += (nowUtc - _lastTickUtc).TotalMilliseconds;
        _lastTickUtc = nowUtc;

        if (_prevState.Length == 0)
        {
            // First sighting: no history to reconstruct. Start counting only if already working.
            _running = cur == "yellow";
            _accumulatedMs = 0;
            _prevState = cur;
            return;
        }

        if (cur != _prevState)
        {
            switch (cur)
            {
                case "yellow" when _prevState == "red":
                    // Resume after a permission prompt. Back-credit the approved tool's execution time
                    // (it ran while we showed red), but never the time spent waiting on the prompt.
                    if (s.DurationMs is long ms && ms > 0 &&
                        s.Event is "PostToolUse" or "PostToolUseFailure")
                        _accumulatedMs += ms;
                    _running = true;
                    break;

                case "yellow":
                    // New run (green/off → yellow): reset and start counting.
                    _accumulatedMs = 0;
                    _running = true;
                    break;

                case "red":   // pause — hold the value while the user answers
                case "green": // freeze — keep showing the last run's time
                    _running = false;
                    break;
            }
            _prevState = cur;
        }
    }
}
