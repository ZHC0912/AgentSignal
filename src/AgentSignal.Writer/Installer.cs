using System.Reflection;
using System.Text.Json.Nodes;
using AgentSignal.Core;

namespace AgentSignal.Writer;

/// <summary>
/// Deploys the writer binary into ~/.agentsignal and merges the named adapter's hooks into the
/// USER-LEVEL ~/.claude/settings.json (never settings.local.json). The adapter hook definition is an
/// embedded copy of adapters/&lt;tool&gt;/hooks.template.json with a {{WRITER_PATH}} placeholder.
/// </summary>
internal static class Installer
{
    public static int Run(string tool)
    {
        AgentPaths.EnsureRoot();

        string deployed = DeploySelf();
        string writerForHook = deployed.Replace('\\', '/'); // forward slashes work in bash & PowerShell

        if (string.Equals(tool, AntigravityAdapter.ToolName, StringComparison.OrdinalIgnoreCase))
            return RunAntigravity(deployed, writerForHook);

        string template = LoadTemplate(tool).Replace("{{WRITER_PATH}}", writerForHook);
        if (JsonNode.Parse(template) is not JsonObject hooks)
        {
            Console.Error.WriteLine($"AgentSignal: '{tool}' hook template is not a JSON object.");
            return 1;
        }

        string settingsPath = Path.Combine(ClaudeDir(), "settings.json");
        SettingsMerger.MergeHooks(settingsPath, hooks);

        Console.WriteLine($"AgentSignal: writer deployed to {deployed}");
        Console.WriteLine($"AgentSignal: '{tool}' hooks merged into {settingsPath}");
        return 0;
    }

    /// <summary>
    /// Antigravity (IDE only — the CLI is future/unverified). Installs to exactly ONE hook scope,
    /// the global ~/.gemini/config/hooks.json: the engine also loads the workspace scope
    /// (&lt;root&gt;/.agents/hooks.json) and BOTH fire for every event, so registering twice would
    /// double-fire the writer (Phase 0 §1). The hook command path must be UNQUOTED — Antigravity
    /// splits the command itself and passes quote characters through literally, which breaks the
    /// invocation (Phase 0 §5) — so a deployed path containing a space cannot be installed.
    /// </summary>
    private static int RunAntigravity(string deployed, string writerForHook)
    {
        if (writerForHook.Contains(' '))
        {
            Console.Error.WriteLine(
                $"AgentSignal: the writer deployed to '{deployed}', which contains a space. " +
                "Antigravity passes quotes literally, so hook command paths cannot be quoted — " +
                "the install cannot proceed from a home directory with spaces in its path.");
            return 1;
        }

        string template = LoadTemplate(AntigravityAdapter.ToolName).Replace("{{WRITER_PATH}}", writerForHook);
        if (JsonNode.Parse(template) is not JsonObject incoming)
        {
            Console.Error.WriteLine("AgentSignal: antigravity hook template is not a JSON object.");
            return 1;
        }

        // hooks.json shape: { "<hook name>": { "<Event>": [ { matcher, hooks: [...] } ] } }.
        // Merge = set/replace our own top-level "agentsignal" key (idempotent; re-install updates a
        // moved writer path) while preserving any other hook entries the user has.
        string hooksPath = Path.Combine(AgentPaths.Home, ".gemini", "config", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
        JsonObject root = new();
        try
        {
            if (File.Exists(hooksPath) &&
                JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject existing)
                root = existing;
        }
        catch { /* corrupt/unreadable → start fresh rather than fail */ }

        foreach ((string key, JsonNode? value) in incoming)
            root[key] = value?.DeepClone();
        File.WriteAllText(hooksPath,
            root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"AgentSignal: writer deployed to {deployed}");
        Console.WriteLine($"AgentSignal: 'antigravity' hooks merged into {hooksPath} (global scope ONLY — do not also add a workspace .agents/hooks.json copy; both scopes fire)");
        Console.WriteLine("AgentSignal: red comes from the conversation-db poller; hook writes spawn it automatically (nothing to start by hand)");
        if (root.ContainsKey("agentsignal-phase0-log"))
            Console.WriteLine("AgentSignal: NOTE — the Phase 0 logging hooks are still installed; run adapters/antigravity/phase0/uninstall-phase0.ps1 to remove them");
        return 0;
    }

    /// <summary>Copy the running executable into ~/.agentsignal so hooks reference a stable path.</summary>
    private static string DeploySelf()
    {
        string? src = Environment.ProcessPath;
        if (string.IsNullOrEmpty(src))
            throw new InvalidOperationException("cannot resolve the writer's own executable path");

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string dest = Path.Combine(AgentPaths.Root, "AgentSignal.Writer" + ext);

        string srcFull = Path.GetFullPath(src);
        string destFull = Path.GetFullPath(dest);
        if (!string.Equals(srcFull, destFull, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(srcFull, destFull, overwrite: true);
            // Native sidecars: single-file publish puts native libraries BESIDE the exe (e_sqlite3
            // for the Antigravity poller's SQLite reads) — they must travel with it or the poller
            // dies on its first db open.
            string srcDir = Path.GetDirectoryName(srcFull)!;
            foreach (string lib in Directory.EnumerateFiles(srcDir, "*.dll"))
                File.Copy(lib, Path.Combine(AgentPaths.Root, Path.GetFileName(lib)), overwrite: true);
        }
        return destFull;
    }

    private static string LoadTemplate(string tool)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"{tool}-hooks.json", StringComparison.OrdinalIgnoreCase));
        if (resName is null)
            throw new InvalidOperationException($"no embedded hook template for tool '{tool}'");

        using Stream stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ClaudeDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}
