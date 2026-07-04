using AgentSignal.App.Services;
using AgentSignal.Core;

namespace AgentSignal.App.ViewModels;

/// <summary>
/// One live agent session in the expanded view: its own colour and work timer. The widget keys these
/// by <see cref="Key"/> and reuses the instance across polls (updating it in place) so the expanded
/// list never rebuilds and therefore never flickers on the 250ms tick.
/// </summary>
public sealed class SessionRowViewModel : DotsViewModel
{
    private readonly WorkTimer _timer = new();

    public string Key { get; }
    public string Tool { get; }
    public string SessionId { get; }

    public SessionRowViewModel(string tool, string sessionId)
    {
        Tool = tool;
        SessionId = sessionId;
        Key = tool + "__" + sessionId;
    }

    /// <summary>Fold this poll's state into the row: advance the timer, recolour, refresh the text.
    /// The displayed colour IS the file state — no display-side guessing (Decision #3 was reversed:
    /// the light only changes on real events; a stuck yellow is cleared manually via Ctrl+Alt+R).</summary>
    public void Observe(SessionState s, DateTime nowUtc)
    {
        _timer.Observe(s, nowUtc);
        // No celebration blink for a green that isn't a real finish: a manually reset session
        // (event=ManualReset) was cleared by the user, not completed.
        QuietGreen = s.Event == SessionResetService.EventName;
        State = s.State switch
        {
            "red" => AggregateState.Red,
            "yellow" => AggregateState.Yellow,
            "green" => AggregateState.Green,
            _ => AggregateState.Off,
        };
        TimerText = _timer.HasValue ? FormatElapsed(_timer.Elapsed) : "";
        TickPulse(nowUtc);
    }
}
