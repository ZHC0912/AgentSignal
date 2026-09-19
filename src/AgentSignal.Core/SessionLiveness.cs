namespace AgentSignal.Core;

/// <summary>
/// THE rule for "is this session file a real, running session?" — shared by the widget's reader/sweep,
/// the manual reset and the Antigravity poller so they can never disagree.
///
/// The liveness rule is still "SessionEnd OR process-dead → off" with NO inactivity timeout (an idle
/// green session stays forever). What changed is how "process-dead" is decided. Checking only that
/// SOME process owns the stored pid was wrong: Windows recycles small pids after every boot, so a
/// session file from months ago kept matching whatever unrelated process later drew the same number,
/// and rendered as a ghost pill. Identity is now pid + start time:
///
///   <b>A session's agent was running when the session last wrote its file (<see cref="SessionState.Ts"/>),
///   so a process that STARTED AFTER that write cannot be its agent.</b>
///
/// That's exact, not a heuristic: while the real agent lives, nothing else can hold its pid, so the
/// only process that can own the pid AND predate the last write is the agent itself.
/// </summary>
public static class SessionLiveness
{
    /// <summary>The id the writer used to fall back to when a payload carried no session_id. It never
    /// identified a real session (every such event merged into one file), so it is never live.</summary>
    public const string UnknownSessionId = "unknown";

    /// <summary>Slack between the writer's whole-second <c>ts</c> and a process's precise start time.</summary>
    private const int ClockSkewSeconds = 5;

    /// <summary>Verdict for one session file: alive or not, plus a human reason for diagnostics/logs.</summary>
    public readonly record struct Verdict(bool Alive, string Reason);

    /// <summary>
    /// Decide whether <paramref name="s"/> is a live session. <paramref name="agentStartTimes"/> lets a
    /// caller share ONE process enumeration across many pid-less files (it is only consulted when the
    /// file has no pid); omit it and it is computed on demand.
    /// </summary>
    public static Verdict Check(SessionState s, Func<string, List<DateTime>>? agentStartTimes = null)
    {
        if (string.IsNullOrWhiteSpace(s.SessionId) || s.SessionId == UnknownSessionId)
            return new(false, "no session id (\"unknown\") — never a real session");

        DateTime lastWrite = DateTime.UnixEpoch.AddSeconds(s.Ts).AddSeconds(ClockSkewSeconds);

        if (s.Pid > 0)
        {
            if (!ProcessHelper.IsAlive(s.Pid))
                return new(false, $"pid {s.Pid} is not running");

            DateTime? started = ProcessHelper.StartTimeUtc(s.Pid);
            if (started is null)
                // Running but not inspectable. An agent runs as the user and is always inspectable, so
                // this is rare; stay conservative and keep it rather than risk hiding a real session.
                return new(true, $"pid {s.Pid} running (start time unreadable)");

            return started <= lastWrite
                ? new(true, $"pid {s.Pid} running since before the last event")
                : new(false, $"pid {s.Pid} was REUSED — that process started {started:yyyy-MM-dd HH:mm} UTC, after the session's last event ({DateTime.UnixEpoch.AddSeconds(s.Ts):yyyy-MM-dd HH:mm} UTC)");
        }

        // No pid was captured, so there is nothing to probe directly. The session can only still be
        // running if some process of its agent kind existed at its last write.
        List<DateTime> starts = agentStartTimes?.Invoke(s.Tool) ?? ProcessHelper.StartTimesOfProcessesNamed(AgentProcessNames(s.Tool));
        foreach (DateTime t in starts)
            if (t <= lastWrite)
                return new(true, "no pid, but an agent process old enough to own it is running");
        return new(false, starts.Count == 0
            ? "no pid and no agent process is running"
            : "no pid, and every running agent process started after the session's last event");
    }

    /// <summary>Process-name fragments that identify an agent kind — used when a session has no pid.</summary>
    public static IReadOnlyList<string> AgentProcessNames(string tool) => tool.ToLowerInvariant() switch
    {
        // Native installs run claude.exe; npm installs run the CLI under node.
        "claude" => new[] { "claude", "node" },
        // The IDE itself, or its hook-engine host.
        "antigravity" => new[] { "antigravity", "language_server" },
        _ => new[] { tool },
    };
}
