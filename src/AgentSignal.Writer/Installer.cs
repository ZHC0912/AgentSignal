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
            File.Copy(srcFull, destFull, overwrite: true);
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
