namespace Alfred.Core;

/// <summary>What the pill shows. The coordinator owns the current value; presenters only render it.</summary>
public abstract record AssistantState
{
    public sealed record Idle : AssistantState;
    public sealed record Listening(string Partial) : AssistantState;
    public sealed record Thinking(string Transcript) : AssistantState;
    public sealed record Acting(string Tool, string Title, string Target) : AssistantState;
    public sealed record Result(string Text) : AssistantState;
    /// <summary>A result that answers in words rather than reporting an action ("Hello"). Spoken unless speech is off.</summary>
    public sealed record Reply(string Text) : AssistantState;
    /// <summary>A result with items to choose from (found files).</summary>
    public sealed record List(string Text, IReadOnlyList<ResultItem> Items) : AssistantState;
    /// <summary>A change waiting for yes or no (a batch of files).</summary>
    public sealed record Confirm(string Text, IReadOnlyList<ResultItem> Items) : AssistantState;
    /// <summary>A timer, alarm or reminder going off, until stopped.</summary>
    public sealed record Alert(ClockAlert Clock) : AssistantState;
    public sealed record Error(string Message, string? SettingsPage) : AssistantState;

    public static readonly AssistantState IdleState = new Idle();
}

public abstract record AssistantEvent
{
    public sealed record Activation : AssistantEvent;
    public sealed record Partial(string Text) : AssistantEvent;
    public sealed record Endpoint(string Transcript) : AssistantEvent;
    /// <summary>Capture ended with no speech: back to Idle without invoking the model.</summary>
    public sealed record Silence : AssistantEvent;
    public sealed record Cancel : AssistantEvent;
    public sealed record ToolCall(string Tool, string Title, string Target) : AssistantEvent;
    public sealed record TextOnly(string Text) : AssistantEvent;
    public sealed record Done(string Text, IReadOnlyList<ResultItem> Items) : AssistantEvent;
    public sealed record NeedsConfirmation(string Text, IReadOnlyList<ResultItem> Items) : AssistantEvent;
    /// <summary>A tool answered a question: shown and spoken like a reply.</summary>
    public sealed record Answer(string Text) : AssistantEvent;
    /// <summary>The user picked an item from a list; the outcome of acting on it.</summary>
    public sealed record Selected(string Text) : AssistantEvent;
    public sealed record Failure(string Message, string? SettingsPage = null) : AssistantEvent;
    public sealed record Dismiss : AssistantEvent;
    /// <summary>A timer, alarm or reminder is due.</summary>
    public sealed record Ring(ClockAlert Clock) : AssistantEvent;
}

public static class StateMachine
{
    /// <summary>
    /// The next state, or null when the event is not valid in <paramref name="state"/>
    /// and must be ignored. Failures are visible from every busy state.
    /// </summary>
    public static AssistantState? Transition(AssistantState state, AssistantEvent e) => (state, e) switch
    {
        (AssistantState.Idle, AssistantEvent.Activation) => new AssistantState.Listening(""),
        (AssistantState.Idle, AssistantEvent.Ring r) => new AssistantState.Alert(r.Clock),
        // "Alfred, stop" / "Alfred, snooze": the sound stops as soon as the user speaks.
        (AssistantState.Alert, AssistantEvent.Activation) => new AssistantState.Listening(""),
        (AssistantState.Listening, AssistantEvent.Partial p) => new AssistantState.Listening(p.Text),
        (AssistantState.Listening, AssistantEvent.Endpoint p) => new AssistantState.Thinking(p.Transcript),
        (AssistantState.Listening, AssistantEvent.Silence) => AssistantState.IdleState,
        (AssistantState.Thinking or AssistantState.Acting, AssistantEvent.ToolCall t) => new AssistantState.Acting(t.Tool, t.Title, t.Target),
        (AssistantState.Thinking, AssistantEvent.TextOnly t) => new AssistantState.Reply(t.Text),
        (AssistantState.Acting, AssistantEvent.Done d) => d.Items.Count == 0 ? new AssistantState.Result(d.Text) : new AssistantState.List(d.Text, d.Items),
        (AssistantState.Acting, AssistantEvent.Answer a) => new AssistantState.Reply(a.Text),
        (AssistantState.Acting, AssistantEvent.NeedsConfirmation c) => new AssistantState.Confirm(c.Text, c.Items),
        (AssistantState.List or AssistantState.Confirm, AssistantEvent.Selected s) => new AssistantState.Result(s.Text),
        // Answering by voice: "Alfred, yes".
        (AssistantState.Confirm, AssistantEvent.Activation) => new AssistantState.Listening(""),
        (AssistantState.Idle or AssistantState.Listening or AssistantState.Thinking or AssistantState.Acting or AssistantState.List
            or AssistantState.Confirm, AssistantEvent.Failure f) => new AssistantState.Error(f.Message, f.SettingsPage),
        (AssistantState.Result or AssistantState.Reply or AssistantState.List or AssistantState.Confirm or AssistantState.Error
            or AssistantState.Alert, AssistantEvent.Dismiss) => AssistantState.IdleState,
        (AssistantState.Idle, AssistantEvent.Cancel) => null,
        (_, AssistantEvent.Cancel) => AssistantState.IdleState,
        _ => null,
    };
}
