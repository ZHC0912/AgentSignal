using System.Diagnostics;
using AgentSignal.Core;

namespace AgentSignal.Writer;

/// <summary>
/// Antigravity-specific glue for the tool-agnostic writer. IDE ONLY: everything here is built on
/// what Phase 0 verified against the Antigravity IDE (adapters/antigravity/phase0/FINDINGS.md).
/// The Antigravity CLI is future/unverified — do not assume any of this holds there.
/// </summary>
internal static class AntigravityAdapter
{
    public const string ToolName = "antigravity";

    /// <summary>~/.gemini/antigravity/conversations — one SQLite db per conversation; the FILENAME
    /// is the conversation id (Phase 0 §2), which doubles as the session key.</summary>
    public static string ConversationsDir =>
        Path.Combine(AgentPaths.Home, ".gemini", "antigravity", "conversations");

    /// <summary>Held open (exclusive) by the running poller daemon; probed by hooks to avoid
    /// spawning a second instance.</summary>
    public static string PollerLockFile => Path.Combine(AgentPaths.Root, "antigravity-poller.lock");

    /// <summary>
    /// Resolve the session key for a hook firing. The stdin payload was never captured in Phase 0
    /// (the logger never executed — FINDINGS §5), so this tries every plausible source in order:
    /// stdin `conversation_id` / `session_id`, the documented ANTIGRAVITY_CONVERSATION_ID env var,
    /// and finally the most recently modified conversation db (a firing hook means that conversation
    /// was active within the last second or two, so its db was just touched). With AGENTSIGNAL_DEBUG=1
    /// the chosen source is logged so the live test can pin down which one actually works.
    /// </summary>
    public static string ResolveSessionId(string? stdinConversationId, string? stdinSessionId)
    {
        (string? id, string source) = Pick();
        if (Environment.GetEnvironmentVariable("AGENTSIGNAL_DEBUG") == "1")
        {
            try
            {
                AgentPaths.EnsureRoot();
                File.AppendAllText(Path.Combine(AgentPaths.Root, "antigravity-writer.log"),
                    $"{DateTimeOffset.UtcNow:o} session id '{id ?? "unknown"}' via {source}\n");
            }
            catch { /* debug only */ }
        }
        return id ?? "unknown";

        (string?, string) Pick()
        {
            if (!string.IsNullOrEmpty(stdinConversationId)) return (stdinConversationId, "stdin conversation_id");
            if (!string.IsNullOrEmpty(stdinSessionId)) return (stdinSessionId, "stdin session_id");
            string? env = Environment.GetEnvironmentVariable("ANTIGRAVITY_CONVERSATION_ID");
            if (!string.IsNullOrEmpty(env)) return (env, "ANTIGRAVITY_CONVERSATION_ID env");
            string? newest = NewestConversationId();
            return newest is not null ? (newest, "newest conversation db (fallback)") : (null, "none");
        }
    }

    /// <summary>The conversation whose db was most recently written. Fallback session key only —
    /// correct whenever a single conversation is active (a hook just fired for it), ambiguous if two
    /// run concurrently.</summary>
    public static string? NewestConversationId()
    {
        try
        {
            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string db in Directory.EnumerateFiles(ConversationsDir, "*.db"))
            {
                DateTime t = File.GetLastWriteTimeUtc(db);
                // The -wal often carries the newest writes without touching the db's own mtime.
                string wal = db + "-wal";
                if (File.Exists(wal))
                {
                    DateTime w = File.GetLastWriteTimeUtc(wal);
                    if (w > t) t = w;
                }
                if (t > bestTime) { bestTime = t; best = Path.GetFileNameWithoutExtension(db); }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>
    /// Agent PID for liveness: prefer the IDE's main process (exe name contains "antigravity") so the
    /// pill goes off when the IDE closes; fall back to the hook engine host (language_server.exe) if
    /// the walk never reaches an "antigravity"-named ancestor.
    /// </summary>
    public static int FindPid()
    {
        int pid = ProcessHelper.FindAgentPid("antigravity");
        return pid != 0 ? pid : ProcessHelper.FindAgentPid("language_server");
    }

    /// <summary>
    /// Make sure the red-light poller daemon is running (spawn it detached if the lock is free).
    /// Called on every antigravity hook write — a cheap probe when the daemon already runs. Never
    /// throws: the poller is best-effort (yellow/green still work without it).
    /// </summary>
    public static void EnsurePollerRunning()
    {
        try
        {
            string? self = Environment.ProcessPath;
            // Only self-spawn when running as the real deployed writer binary — under `dotnet run`
            // ProcessPath is dotnet.exe and "poll antigravity" would mean nothing to it.
            if (self is null ||
                !Path.GetFileNameWithoutExtension(self).Contains("AgentSignal.Writer", StringComparison.OrdinalIgnoreCase))
                return;
            if (IsPollerLockHeld()) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = self,
                Arguments = "poll antigravity",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AgentPaths.Root,
            });
        }
        catch { /* best effort */ }
    }

    private static bool IsPollerLockHeld()
    {
        try
        {
            AgentPaths.EnsureRoot();
            using var probe = new FileStream(PollerLockFile,
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false; // we could take it → nobody holds it
        }
        catch (IOException) { return true; }
    }
}
