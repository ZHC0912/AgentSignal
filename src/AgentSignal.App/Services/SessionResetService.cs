using System.Text.Json;
using AgentSignal.Core;

namespace AgentSignal.App.Services;

/// <summary>
/// Manual reset (the escape hatch for the accepted Esc-mid-tool stuck-yellow gap, Decision #3's
/// conservative guard): force every current session to GREEN as if its turn completed. The session
/// files are rewritten in place through the same state contract the writer uses (state=green,
/// event="ManualReset", ts=now, pid preserved), so the widget's normal machinery does the rest —
/// each WorkTimer sees yellow→green and freezes at its current value (shown as the last run's time),
/// and the next real event simply overwrites the file (UserPromptSubmit starts a fresh yellow+timer).
/// The "ManualReset" event marker lets the view models keep the green QUIET: no green alert and no
/// green-blink, because a manual clear is not a real finish.
/// </summary>
public static class SessionResetService
{
    /// <summary>The event written into a reset session file; view models key quiet-green off it.</summary>
    public const string EventName = "ManualReset";

    /// <summary>Force all non-green sessions green. Returns how many files were rewritten.</summary>
    public static int ForceGreen(DateTime nowUtc) => ForceGreen(AgentPaths.SessionsDir, nowUtc);

    /// <summary>Directory-parameterised core so --reset-test can prove it against a temp dir.</summary>
    public static int ForceGreen(string sessionsDir, DateTime nowUtc)
    {
        if (!Directory.Exists(sessionsDir)) return 0;

        int rewritten = 0;
        foreach (string file in Directory.EnumerateFiles(sessionsDir, "*.json"))
        {
            SessionState? s;
            try
            {
                s = JsonSerializer.Deserialize(File.ReadAllText(file), AgentJsonContext.Default.SessionState);
            }
            catch
            {
                continue; // mid-write or corrupt; nothing to reset
            }
            if (s is null || s.State == "green") continue; // already idle — leave its ts/event alone

            // Yellow AND red both clear: reset is a deliberate "everything is idle" from the user.
            // (A red whose prompt is in fact still live self-corrects — answering it fires the next
            // hook, which rewrites the file and the widget follows.) Pid is preserved for liveness.
            s.State = "green";
            s.Event = EventName;
            s.ToolName = null;
            s.DurationMs = null;
            s.Ts = (long)(nowUtc - DateTime.UnixEpoch).TotalSeconds;
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(s, AgentJsonContext.Default.SessionState));
                rewritten++;
            }
            catch
            {
                // the writer may be mid-update; the next hook event supersedes a failed reset anyway
            }
        }
        return rewritten;
    }
}
