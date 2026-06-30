using System.Text.Json;
using AgentSignal.App.Models;
using AgentSignal.Core;

namespace AgentSignal.App.Services;

/// <summary>
/// Owns the single in-memory <see cref="AppConfig"/> backed by ~/.agentsignal/config.json. Settings
/// edits mutate <see cref="Current"/> and raise <see cref="Changed"/> so the live widget re-applies
/// colours/opacity/scale/lock without a restart. Tolerant: read/write failures fall back to defaults.
/// </summary>
public sealed class ConfigService
{
    public static ConfigService Instance { get; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Raised after <see cref="Current"/> changes so listeners can apply settings live.</summary>
    public event Action? Changed;

    public AppConfig Current { get; private set; }

    private ConfigService() => Current = Load();

    /// <summary>Mutate the config, persist it, and notify listeners — the standard live-apply path.</summary>
    public void Update(Action<AppConfig> mutate)
    {
        mutate(Current);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Persist <see cref="Current"/> without notifying (e.g. saving a dragged position).</summary>
    public void Save()
    {
        try
        {
            AgentPaths.EnsureRoot();
            File.WriteAllText(AgentPaths.ConfigFile, JsonSerializer.Serialize(Current, Options));
        }
        catch { /* best effort */ }
    }

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(AgentPaths.ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AgentPaths.ConfigFile), Options)
                       ?? new AppConfig();
        }
        catch { /* fall through to defaults */ }
        return new AppConfig();
    }
}
