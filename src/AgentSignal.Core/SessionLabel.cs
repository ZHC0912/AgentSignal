namespace AgentSignal.Core;

/// <summary>
/// Builds the human label shown on every pill: the session's folder name plus its agent, e.g.
/// "calorie-tracker · Claude" or "my-app · Antigravity". The folder comes from
/// <see cref="SessionState.Cwd"/> (captured by the writer from the hook payload); the agent from the
/// existing tool key, so a new adapter gets a label for free.
///
/// Two forms: <see cref="Initials"/> is what the pill shows at rest — just the two initials, e.g.
/// "A · C" — so the widget stays tiny; <see cref="Full"/> is the whole thing, which the label
/// expands to in place while the pointer is over the pill (and which the settings list shows).
/// </summary>
public static class SessionLabel
{

    /// <summary>The separator between folder and agent.</summary>
    public const string Separator = " · ";

    /// <summary>The full, untruncated label: "folder · Tool" (or just "Tool" when no cwd is known).</summary>
    public static string Full(string tool, string? cwd)
    {
        string folder = FolderName(cwd);
        string agent = ToolDisplay(tool);
        return folder.Length == 0 ? agent : folder + Separator + agent;
    }

    /// <summary>
    /// The resting pill label: the folder's initial and the agent's initial, e.g. "A · C" for
    /// "AgentSignal · Claude". With no cwd there is no folder half, so it degrades to the agent's
    /// initial alone ("C").
    /// </summary>
    public static string Initials(string tool, string? cwd)
    {
        string folder = Initial(FolderName(cwd));
        string agent = Initial(ToolDisplay(tool));
        return folder.Length == 0 ? agent : folder + Separator + agent;
    }

    /// <summary>First letter or digit of a name, upper-cased — so ".config" initials as "C" and
    /// "2048-game" as "2". Empty in, empty out.</summary>
    private static string Initial(string name)
    {
        foreach (char c in name)
            if (char.IsLetterOrDigit(c))
                return char.ToUpperInvariant(c).ToString();
        return name.Length > 0 ? name[..1] : "";
    }

    /// <summary>
    /// The last path segment of the session's working directory — the project folder. Handles trailing
    /// separators, mixed separators (a hook may report either), and a bare drive root ("C:\" → "C:").
    /// Returns "" when nothing usable is known.
    /// </summary>
    public static string FolderName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";
        string path = cwd.Trim().Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0) return "";           // cwd was "/" (or "\")
        int slash = path.LastIndexOf('/');
        string leaf = slash >= 0 ? path[(slash + 1)..] : path;
        return leaf;                                // "C:" for a drive root, which is honest enough
    }

    /// <summary>Display form of the tool key: "claude" → "Claude", "antigravity" → "Antigravity".</summary>
    public static string ToolDisplay(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return "Agent";
        string t = tool.Trim();
        return char.IsUpper(t[0]) ? t : char.ToUpperInvariant(t[0]) + t[1..];
    }
}
