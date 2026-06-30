namespace AgentSignal.Core;

/// <summary>
/// The state contract. One of these is serialized to
/// <c>~/.agentsignal/sessions/&lt;tool&gt;__&lt;sessionId&gt;.json</c> for every live agent session.
/// The widget only ever reads these files — it knows nothing else about any agent.
/// </summary>
public sealed class SessionState
{
    /// <summary>Agent kind, e.g. "claude". Combined with <see cref="SessionId"/> to key the file.</summary>
    public string Tool { get; set; } = "";

    /// <summary>The agent's session id (from the hook payload).</summary>
    public string SessionId { get; set; } = "";

    /// <summary>green | yellow | red. ("off" is never stored — off means the file is deleted.)</summary>
    public string State { get; set; } = "green";

    /// <summary>The hook event that produced this update (e.g. "PreToolUse"). Optional; for tooltips/debug.</summary>
    public string? Event { get; set; }

    /// <summary>The tool involved when relevant (e.g. "Bash"). Optional; for a tooltip.</summary>
    public string? ToolName { get; set; }

    /// <summary>Agent process id, captured at session start (and re-captured on resume). 0 if unknown.</summary>
    public int Pid { get; set; }

    /// <summary>Epoch seconds of this update.</summary>
    public long Ts { get; set; }

    /// <summary>
    /// Duration of the most recent tool execution in milliseconds, forwarded from PostToolUse.
    /// The widget back-credits this on a red→PostToolUse transition so post-approval work time is
    /// counted, even though no hook fires at the instant the user approves (see Phase 0 findings).
    /// Null on every event other than PostToolUse / PostToolUseFailure.
    /// </summary>
    public long? DurationMs { get; set; }
}
