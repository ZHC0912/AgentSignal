using System.Text.Json;
using AgentSignal.Core;

namespace AgentSignal.Writer;

/// <summary>
/// Tool-agnostic state writer invoked by agent hooks.
///
///   AgentSignal.Writer &lt;tool&gt; &lt;green|yellow|red|off&gt;   (hook JSON arrives on stdin)
///   AgentSignal.Writer install [tool]                  (merge hooks into ~/.claude/settings.json)
///
/// Records the current state of one session as a file per the state contract. It must be tiny and
/// fast and must never throw in a way that blocks the agent — every error path returns exit 0.
/// No timer logic lives here: the writer only forwards the current state word (and duration_ms).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1 && string.Equals(args[0], "install", StringComparison.OrdinalIgnoreCase))
                return Installer.Run(args.Length >= 2 ? args[1] : "claude");

            return WriteState(args);
        }
        catch (Exception ex)
        {
            // A hook must never break the agent: swallow and exit 0. Only surface the error when
            // explicitly debugging, to a side log that can't affect the agent.
            if (Environment.GetEnvironmentVariable("AGENTSIGNAL_DEBUG") == "1")
            {
                try
                {
                    AgentPaths.EnsureRoot();
                    File.AppendAllText(Path.Combine(AgentPaths.Root, "writer-error.log"),
                        $"{DateTimeOffset.UtcNow:o} {ex}\n");
                }
                catch { /* ignore */ }
            }
            return 0;
        }
    }

    private static int WriteState(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: AgentSignal.Writer <tool> <green|yellow|red|off>");
            return 0;
        }

        string tool = args[0];
        string state = args[1].ToLowerInvariant();

        using JsonDocument? doc = TryParse(ReadStdin());
        JsonElement root = doc?.RootElement ?? default;

        string sessionId = GetString(root, "session_id") ?? "unknown";
        string? eventName = GetString(root, "hook_event_name");
        string? toolName = GetString(root, "tool_name");
        string? source = GetString(root, "source");

        AgentPaths.EnsureSessionsDir();
        string path = AgentPaths.SessionFile(tool, sessionId);

        // "off" = session gone: delete the file and stop.
        if (state == "off")
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            return 0;
        }

        SessionState? existing = TryReadExisting(path);

        // Capture the agent PID at session start / on resume, or whenever we don't already have one
        // (covers hooks installed mid-session, where SessionStart was never seen). Otherwise preserve
        // the PID captured earlier.
        bool freshStart =
            string.Equals(eventName, "SessionStart", StringComparison.Ordinal) ||
            string.Equals(source, "resume", StringComparison.Ordinal);
        int pid = (freshStart || existing is null || existing.Pid <= 0)
            ? ProcessHelper.FindAgentPid(tool)
            : existing.Pid;

        // Forward the actual tool runtime so the widget can back-credit post-approval work time.
        long? durationMs = null;
        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal) ||
            string.Equals(eventName, "PostToolUseFailure", StringComparison.Ordinal))
            durationMs = GetLong(root, "duration_ms");

        var session = new SessionState
        {
            Tool = tool,
            SessionId = sessionId,
            State = state,
            Event = eventName,
            ToolName = toolName,
            Pid = pid,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DurationMs = durationMs,
        };

        WriteAtomic(path, session);
        return 0;
    }

    private static string ReadStdin()
    {
        // Only read when stdin is actually piped (a hook), so a manual run never blocks on the console.
        // Read the raw stream as UTF-8 rather than Console.In, which would decode using the console
        // codepage and mangle the UTF-8 JSON that hooks deliver.
        try
        {
            if (!Console.IsInputRedirected) return "";
            using Stream stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return reader.ReadToEnd();
        }
        catch { return ""; }
    }

    private static JsonDocument? TryParse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonDocument.Parse(s); } catch { return null; }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out JsonElement v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? GetLong(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;
    }

    private static SessionState? TryReadExisting(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.SessionState);
        }
        catch { return null; }
    }

    private static void WriteAtomic(string path, SessionState session)
    {
        string json = JsonSerializer.Serialize(session, AgentJsonContext.Default.SessionState);
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, json);
        // Atomic replace so the widget's 250ms poll never reads a half-written file.
        try { File.Move(tmp, path, overwrite: true); }
        catch
        {
            try { File.Copy(tmp, path, overwrite: true); File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
