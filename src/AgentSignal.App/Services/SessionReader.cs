using System.Text.Json;
using AgentSignal.Core;

namespace AgentSignal.App.Services;

/// <summary>
/// Reads the live session files from ~/.agentsignal/sessions — and DELETES the dead ones. A session is
/// live when <see cref="SessionLiveness"/> says so (its agent process is still the one that wrote the
/// file). Anything else is a ghost: previously these were merely skipped, so the directory accumulated
/// months of dead files, and any whose recycled pid happened to match a running process came back as a
/// ghost pill. Now every read is also a sweep; the first read at widget startup clears everything a
/// previous boot left behind. No inactivity timeout: an idle green session stays.
/// </summary>
public sealed class SessionReader
{
    /// <summary>A file that won't parse is normally a write in flight; only one this old is garbage.</summary>
    private static readonly TimeSpan CorruptGrace = TimeSpan.FromSeconds(30);

    private readonly string? _dir;

    /// <param name="sessionsDir">Override the sessions directory (diagnostics/tests read a temp dir);
    /// null = the real ~/.agentsignal/sessions.</param>
    public SessionReader(string? sessionsDir = null) => _dir = sessionsDir;

    private string Dir => _dir ?? AgentPaths.SessionsDir;

    /// <summary>The live sessions. Dead/ghost files found along the way are deleted.</summary>
    public IReadOnlyList<SessionState> ReadLive()
    {
        var result = new List<SessionState>();
        foreach ((string file, SessionState? s, SessionLiveness.Verdict v) in Scan())
        {
            if (s is not null && v.Alive) result.Add(s);
            else Remove(file, s, v.Reason);
        }
        return result;
    }

    /// <summary>Startup sweep: delete every dead session file now, before the widget first renders, so
    /// ghosts from previous boots never flash up as pills. Returns how many files were removed.</summary>
    public int SweepDead()
    {
        int removed = 0;
        foreach ((string file, SessionState? s, SessionLiveness.Verdict v) in Scan())
            if (s is null || !v.Alive)
                removed += Remove(file, s, v.Reason) ? 1 : 0;
        return removed;
    }

    /// <summary>Every session file with its verdict, deleting NOTHING — for the --dump diagnostic.
    /// Unparseable files come back with a null state.</summary>
    public IEnumerable<(string File, SessionState? State, SessionLiveness.Verdict Verdict)> Inspect() => Scan();

    private List<(string, SessionState?, SessionLiveness.Verdict)> Scan()
    {
        var found = new List<(string, SessionState?, SessionLiveness.Verdict)>();
        if (!Directory.Exists(Dir)) return found;

        // One process enumeration per scan, shared by every pid-less file (they're rare, and dead
        // ones are deleted on sight, so this normally never runs at all).
        var startsByTool = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        List<DateTime> AgentStarts(string tool)
        {
            if (!startsByTool.TryGetValue(tool, out List<DateTime>? l))
                startsByTool[tool] = l = ProcessHelper.StartTimesOfProcessesNamed(SessionLiveness.AgentProcessNames(tool));
            return l;
        }

        foreach (string file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            SessionState? s = TryRead(file);
            if (s is null)
            {
                // Mid-write? Leave it for the next tick — unless it's been broken for a while.
                bool stale = DateTime.UtcNow - SafeMtime(file) > CorruptGrace;
                if (stale) found.Add((file, null, new(false, "unparseable JSON")));
                continue;
            }
            found.Add((file, s, SessionLiveness.Check(s, AgentStarts)));
        }
        return found;
    }

    /// <summary>
    /// Delete one dead session file — unless it changed since we judged it (a hook may have just
    /// rewritten it for a resumed session with a fresh pid), in which case the next poll re-judges.
    /// </summary>
    private static bool Remove(string file, SessionState? judged, string reason)
    {
        try
        {
            if (judged is not null)
            {
                SessionState? now = TryRead(file);
                if (now is null || now.Ts != judged.Ts || now.Pid != judged.Pid || now.State != judged.State)
                    return false;
            }
            File.Delete(file);
            Log($"removed {Path.GetFileName(file)} — {reason}");
            return true;
        }
        catch { return false; } // locked by a writer mid-replace, or already gone: next tick
    }

    private static SessionState? TryRead(string file)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), AgentJsonContext.Default.SessionState); }
        catch { return null; }
    }

    private static DateTime SafeMtime(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>With AGENTSIGNAL_DEBUG=1, every removal is traced to ~/.agentsignal/sweep.log so a
    /// live cleanup is verifiable after the fact (the same pattern as alerts.log).</summary>
    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("AGENTSIGNAL_DEBUG") != "1") return;
        try
        {
            AgentPaths.EnsureRoot();
            File.AppendAllText(Path.Combine(AgentPaths.Root, "sweep.log"), $"{DateTimeOffset.Now:o} {line}\n");
        }
        catch { /* debug only */ }
    }
}
