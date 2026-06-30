namespace AgentSignal.Core;

/// <summary>
/// Single source of truth for every path AgentSignal touches. Cross-platform: home resolves via
/// <see cref="Environment.SpecialFolder.UserProfile"/> (USERPROFILE on Windows, $HOME elsewhere).
/// </summary>
public static class AgentPaths
{
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>~/.agentsignal</summary>
    public static string Root => Path.Combine(Home, ".agentsignal");

    /// <summary>~/.agentsignal/sessions</summary>
    public static string SessionsDir => Path.Combine(Root, "sessions");

    /// <summary>~/.agentsignal/config.json</summary>
    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>~/.agentsignal/sessions/&lt;tool&gt;__&lt;sessionId&gt;.json</summary>
    public static string SessionFile(string tool, string sessionId) =>
        Path.Combine(SessionsDir, $"{Sanitize(tool)}__{Sanitize(sessionId)}.json");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
    public static void EnsureSessionsDir() => Directory.CreateDirectory(SessionsDir);

    /// <summary>
    /// Reduce a tool/session id to characters that are always safe in a file name. Session ids are
    /// normally GUID-like, but this is defensive against odd ids and path traversal.
    /// </summary>
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            buf[i] = (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? c : '-';
        }
        return new string(buf);
    }
}
