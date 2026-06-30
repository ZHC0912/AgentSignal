using System.Text.Json.Serialization;

namespace AgentSignal.Core;

/// <summary>
/// Source-generated (reflection-free, AOT/trim-safe) JSON for the state contract.
/// camelCase property names produce the exact contract fields:
/// tool, sessionId, state, event, toolName, pid, ts, durationMs.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionState))]
public partial class AgentJsonContext : JsonSerializerContext;
