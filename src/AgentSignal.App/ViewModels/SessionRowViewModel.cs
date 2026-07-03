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

    /// <summary>True while this row is displayed green only because its yellow went stale (Decision #3).
    /// The underlying file still says yellow and the WorkTimer is still counting.</summary>
    public bool IsDemoted { get; private set; }

    /// <summary>Fold this poll's state into the row: advance the timer, recolour, refresh the text.</summary>
    public void Observe(SessionState s, DateTime nowUtc)
    {
        // The timer always observes the REAL file state — the stale-yellow demotion below is
        // display-only, so a falsely demoted session keeps accumulating and snaps back intact.
        _timer.Observe(s, nowUtc);
        AggregateState real = s.State switch
        {
            "red" => AggregateState.Red,
            "yellow" => AggregateState.Yellow,
            "green" => AggregateState.Green,
            _ => AggregateState.Off,
        };
        IsDemoted = real == AggregateState.Yellow && StaleYellow.IsDemoted(s, nowUtc);
        // No celebration blink for a green that isn't a real finish: a demotion is a guess, and a
        // manually reset session (event=ManualReset) was cleared by the user, not completed.
        QuietGreen = IsDemoted || s.Event == SessionResetService.EventName;
        State = IsDemoted ? AggregateState.Green : real;
        TimerText = _timer.HasValue ? FormatElapsed(_timer.Elapsed) : "";
        TickPulse(nowUtc);
    }
}
