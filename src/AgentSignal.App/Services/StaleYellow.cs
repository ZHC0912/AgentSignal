using AgentSignal.Core;

namespace AgentSignal.App.Services;

/// <summary>
/// Stale-yellow → green display demotion (locked Decision #3). No Claude hook fires on a user
/// interrupt (Esc) — Stop explicitly skips it (open feature request #9516) — and a tool can also end
/// a turn silently with no Stop (#29881), so an aborted turn leaves its session file yellow forever.
/// Verified live 2026-07-03: an Esc-abort produced ZERO events in the full-event capture.
///
/// Fallback: a session that has sat yellow with no file update for <see cref="ThresholdSeconds"/> is
/// *displayed* green. This is presentation-only — the WorkTimer and state data are never touched, so
/// if a later event proves the session was actually still working the display snaps back to yellow
/// with the timing intact.
///
/// In-flight-tool guard: a session whose last event is a PreToolUse (i.e. no matching PostToolUse has
/// overwritten it) is NEVER demoted — a long install/build fires nothing between PreToolUse and
/// PostToolUse and must stay yellow. Accepted gap: an Esc that lands mid-tool swallows the pending
/// PostToolUse, so that abort is indistinguishable from a running tool and stays yellow.
///
/// Only yellow demotes; §13 liveness (green → off) is untouched.
/// </summary>
public static class StaleYellow
{
    public const int ThresholdSeconds = 30;

    /// <summary>True when this session should be displayed green despite its file saying yellow.</summary>
    public static bool IsDemoted(SessionState s, DateTime nowUtc)
    {
        if (s.State != "yellow") return false;
        if (s.Event == "PreToolUse") return false; // in-flight tool guard — never demote a running tool
        DateTime updatedUtc = DateTime.UnixEpoch.AddSeconds(s.Ts);
        return (nowUtc - updatedUtc).TotalSeconds >= ThresholdSeconds;
    }
}
