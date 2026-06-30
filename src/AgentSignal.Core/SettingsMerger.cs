using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSignal.Core;

/// <summary>
/// Merges a hooks object into a Claude Code-style <c>settings.json</c> without clobbering existing
/// keys or existing hooks. Appends to each event's hook array rather than replacing it, and is
/// idempotent (re-running never adds a duplicate command). Operates on whatever path it is given —
/// callers must point it at the USER-LEVEL settings file, never settings.local.json (the harness
/// rewrites that and would drop the hooks; see Phase 0 findings).
/// </summary>
public static class SettingsMerger
{
    public static void MergeHooks(string settingsPath, JsonObject hooksToAdd)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        JsonObject root = LoadObject(settingsPath);
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach ((string eventName, JsonNode? incoming) in hooksToAdd)
        {
            if (incoming is not JsonArray incomingGroups) continue;

            if (hooks[eventName] is not JsonArray existingGroups)
            {
                existingGroups = new JsonArray();
                hooks[eventName] = existingGroups;
            }

            foreach (JsonNode? group in incomingGroups)
            {
                string? cmd = CommandsIn(group).FirstOrDefault();
                if (cmd is null) continue;
                if (!ContainsCommand(existingGroups, cmd))
                    existingGroups.Add(group!.DeepClone());
            }
        }

        File.WriteAllText(settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject LoadObject(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string txt = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(txt) && JsonNode.Parse(txt) is JsonObject o)
                    return o;
            }
        }
        catch { /* corrupt/unreadable settings → start fresh rather than fail the install */ }
        return new JsonObject();
    }

    private static bool ContainsCommand(JsonArray groups, string command)
    {
        foreach (JsonNode? g in groups)
            foreach (string c in CommandsIn(g))
                if (string.Equals(c, command, StringComparison.Ordinal))
                    return true;
        return false;
    }

    private static IEnumerable<string> CommandsIn(JsonNode? group)
    {
        if (group is JsonObject o && o["hooks"] is JsonArray hookList)
            foreach (JsonNode? h in hookList)
                if (h is JsonObject ho && ho["command"]?.GetValue<string>() is string c)
                    yield return c;
    }
}
