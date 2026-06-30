namespace AgentSignal.App.ViewModels;

/// <summary>The single colour the aggregate pill shows: most-urgent-wins (red &gt; yellow &gt; green).</summary>
public enum AggregateState
{
    Off,
    Green,
    Yellow,
    Red,
}
