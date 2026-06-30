using System.Text.Json;
using AgentSignal.Core;

namespace AgentSignal.App.Services;

/// <summary>
/// Reads the live session files from ~/.agentsignal/sessions. A session is "live" if its file exists
/// and (when a PID is known) its process is still running — so a hard-killed agent that never fired
/// SessionEnd is dropped rather than lingering. No inactivity timeout: an idle green session stays.
/// </summary>
public sealed class SessionReader
{
    public IReadOnlyList<SessionState> ReadLive()
    {
        var result = new List<SessionState>();
        string dir = AgentPaths.SessionsDir;
        if (!Directory.Exists(dir)) return result;

        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            SessionState? s;
            try
            {
                s = JsonSerializer.Deserialize(File.ReadAllText(file), AgentJsonContext.Default.SessionState);
            }
            catch
            {
                continue; // mid-write or corrupt; pick it up next tick
            }
            if (s is null) continue;
            if (s.Pid > 0 && !ProcessHelper.IsAlive(s.Pid)) continue; // stale/hard-killed

            result.Add(s);
        }
        return result;
    }
}
