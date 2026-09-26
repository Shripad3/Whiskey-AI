namespace Alfred.Core;

/// <summary>How a hands-free activation started.</summary>
/// <param name="Confirmed">Whisper already heard "Alfred" in the wake segment.</param>
/// <param name="Command">The command said in the same breath ("Alfred, open Spotify"), when the segment held one.</param>
public sealed record WakeContext(bool Confirmed, string? Command);

public abstract record SpeechUpdate
{
    public sealed record Level(float Value) : SpeechUpdate;
    public sealed record EndOfSpeech : SpeechUpdate;
    public sealed record NoSpeech : SpeechUpdate;
}

/// <summary>The microphone and speech-to-text for one activation.</summary>
public interface ISpeechInput
{
    /// <summary>Why listening can't start (no microphone, no Whisper model), or null.</summary>
    string? UnavailableReason();
    /// <summary>Starts capture. Hands-free sessions report end of speech through <paramref name="onUpdate"/>.</summary>
    Task StartAsync(WakeContext? wake, Action<SpeechUpdate> onUpdate);
    /// <summary>Stops capture and returns the transcript ("" for none).</summary>
    Task<string> FinishAsync(CancellationToken cancellationToken);
    Task CancelAsync();
}

/// <summary>Renders state; never decides it. Must not block: post to the UI thread and return.</summary>
public interface IPresenter
{
    void Render(AssistantState state);
    /// <summary>Microphone level while listening, 0…1, for the audio-reactive bars.</summary>
    void AudioLevel(float level);
}

/// <summary>
/// Owns the state machine and is the only thing that talks across layers
/// (spec §4). State changes happen under a lock; work runs outside it and is
/// tagged with a session number, so work from an earlier activation that
/// finishes late is ignored and Escape always gets through.
/// </summary>
public sealed class AssistantCoordinator
{
    /// <summary>How long an alert rings before it counts as missed.</summary>
    public static readonly TimeSpan RingFor = TimeSpan.FromSeconds(60);

    private readonly ISpeechInput _speech;
    private readonly LocalModelEngine _engine;
    private readonly ToolRegistry _registry;
    private readonly IPresenter _presenter;
    private readonly ClockStore _clock;
    private readonly FileServices _files;
    private readonly Func<IEnumerable<Routine>> _routines;
    private readonly object _lock = new();

    private AssistantState _state = AssistantState.IdleState;
    private int _session;
    private DateTime _lastTrigger = DateTime.MinValue;
    private bool _suspended;
    private WakeContext? _wake;
    private string? _pendingConfirmation;
    private TimeSpan _resultDelay = TimeSpan.FromSeconds(3);
    private ClockAlert? _interruptedAlert;
    private readonly Queue<ClockAlert> _queuedAlerts = new();
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _dismissal;

    public AssistantCoordinator(ISpeechInput speech, LocalModelEngine engine, ToolRegistry registry, IPresenter presenter,
        ClockStore clock, FileServices files, Func<IEnumerable<Routine>> routines)
    {
        _speech = speech;
        _engine = engine;
        _registry = registry;
        _presenter = presenter;
        _clock = clock;
        _files = files;
        _routines = routines;
    }

    public AssistantState State
    {
        get
        {
            lock (_lock) return _state;
        }
    }

    /// <summary>Raised after every change, for the tray and the log.</summary>
    public event Action<AssistantState>? StateChanged;

    /// <summary>Suspending (the kill switch) stops all activation and cancels anything in flight.</summary>
    public async Task SetSuspendedAsync(bool suspended)
    {
        lock (_lock) _suspended = suspended;
        if (suspended) await CancelAsync();
    }

    public bool IsSuspended
    {
        get
        {
            lock (_lock) return _suspended;
        }
    }

    /// <summary>Accepted only when idle (or answering a confirmation or an alert), with a 1 s debounce.</summary>
    public async Task ActivateAsync(WakeContext? wake = null)
    {
        int id;
        lock (_lock)
        {
            var answering = false;
            _interruptedAlert = null;
            switch (_state)
            {
                case AssistantState.Confirm:
                    answering = true;
                    break;
                case AssistantState.Alert alert:
                    answering = true;
                    _interruptedAlert = alert.Clock;
                    break;
            }
            if (!(_state is AssistantState.Idle || answering) || (_suspended && _interruptedAlert is null)) return;
            if (!answering)
            {
                _pendingConfirmation = null;
                _files.Discard();
            }
            var now = DateTime.UtcNow;
            if (now - _lastTrigger < TimeSpan.FromSeconds(1)) return;
            _lastTrigger = now;
            id = ++_session;
            // Tokens from an earlier command can't be reused.
            _files.Reset();
            _wake = wake;
        }

        if (_speech.UnavailableReason() is { } speechProblem)
        {
            Apply(new AssistantEvent.Failure(speechProblem, "Model"), id);
            return;
        }
        Apply(new AssistantEvent.Activation(), id);
        try
        {
            await _speech.StartAsync(wake, update => Received(update, id));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Apply(new AssistantEvent.Failure(Describe(e)), id);
        }
    }

    /// <summary>Hold-to-talk release is the endpoint.</summary>
    public void Release()
    {
        int id;
        CancellationTokenSource work;
        lock (_lock)
        {
            if (_state is not AssistantState.Listening || _work is not null) return;
            id = _session;
            work = _work = new CancellationTokenSource();
        }
        _ = Task.Run(() => ProcessAsync(id, work.Token));
    }

    /// <summary>A typed command, as if spoken (the tray's "Type a command").</summary>
    public async Task RunTypedAsync(string transcript)
    {
        int id;
        CancellationTokenSource work;
        lock (_lock)
        {
            if (_state is not AssistantState.Idle || _suspended) return;
            id = ++_session;
            _files.Reset();
            _wake = null;
            work = _work = new CancellationTokenSource();
        }
        Apply(new AssistantEvent.Activation(), id);
        await ProcessAsync(id, work.Token, typed: transcript);
    }

    /// <summary>The user picked a row from a result list.</summary>
    public async Task SelectAsync(string itemId)
    {
        int id;
        lock (_lock)
        {
            if (_state is not AssistantState.List) return;
            id = _session;
            _dismissal?.Cancel();
        }
        try
        {
            Apply(new AssistantEvent.Selected(await _files.SelectAsync(itemId)), id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Apply(new AssistantEvent.Failure(Describe(e)), id);
        }
    }

    /// <summary>The pill's Confirm or Cancel button for a pending batch.</summary>
    public void ResolveConfirmation(bool confirmed)
    {
        int id;
        string token;
        lock (_lock)
        {
            if (_state is not AssistantState.Confirm || _pendingConfirmation is null) return;
            token = _pendingConfirmation;
            _pendingConfirmation = null;
            id = _session;
            _dismissal?.Cancel();
        }
        if (!confirmed)
        {
            _files.Discard();
            Apply(new AssistantEvent.Cancel(), id);
            return;
        }
        try
        {
            lock (_lock) _resultDelay = TimeSpan.FromSeconds(5);
            Apply(new AssistantEvent.Selected(_files.Confirm(token)), id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Apply(new AssistantEvent.Failure(Describe(e)), id);
        }
    }

    /// <summary>A timer or alarm is due: ring now, or as soon as the pill is free.</summary>
    public void Ring(ClockAlert alert)
    {
        int id;
        lock (_lock)
        {
            if (_state is not AssistantState.Idle)
            {
                _queuedAlerts.Enqueue(alert);
                return;
            }
            id = ++_session;
        }
        Apply(new AssistantEvent.Ring(alert), id);
    }

    /// <summary>The pill's Stop or Snooze button.</summary>
    public void ResolveAlert(bool snooze)
    {
        int id;
        ClockAlert alert;
        lock (_lock)
        {
            if (_state is not AssistantState.Alert ringing) return;
            alert = ringing.Clock;
            id = _session;
        }
        if (snooze && alert.CanSnooze) _clock.Snooze(alert);
        Apply(new AssistantEvent.Dismiss(), id);
    }

    /// <summary>Escape: back to Idle from any non-idle state, aborting in-flight work.</summary>
    public async Task CancelAsync()
    {
        int id;
        lock (_lock)
        {
            if (_state is AssistantState.Idle) return;
            _pendingConfirmation = null;
            _work?.Cancel();
            _work = null;
            id = _session;
        }
        _files.Discard();
        await _speech.CancelAsync();
        Apply(new AssistantEvent.Cancel(), id);
    }

    private async Task ProcessAsync(int id, CancellationToken cancellationToken, string? typed = null)
    {
        try
        {
            var transcript = typed ?? await _speech.FinishAsync(cancellationToken);
            WakeContext? wake;
            lock (_lock) wake = _wake;
            if (typed is null && wake is { Command: { Length: > 0 } sameBreath } && transcript.Trim().Length == 0)
                transcript = sameBreath;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Apply(new AssistantEvent.Silence(), id);
                return;
            }
            if (wake is { Confirmed: true })
            {
                // Whisper already heard "Alfred"; only tidy the transcript.
                transcript = WakePhrase.CommandAfterConfirmedWake(transcript);
            }
            else if (wake is not null)
            {
                if (WakePhrase.Command(transcript) is not { } command)
                {
                    Apply(new AssistantEvent.Silence(), id); // a false detection
                    return;
                }
                transcript = command;
            }
            Apply(new AssistantEvent.Endpoint(transcript), id);
            await HandleAsync(transcript, id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // CancelAsync already returned to Idle.
        }
        catch (Exception e)
        {
            Apply(new AssistantEvent.Failure(Describe(e), (e as AssistantFailure)?.SettingsPage), id);
        }
        finally
        {
            lock (_lock)
            {
                if (_session == id) _work = null;
            }
        }
    }

    private async Task HandleAsync(string transcript, int id, CancellationToken cancellationToken)
    {
        // Spoken over a ringing alert: "stop" and "snooze" are for it; anything
        // else is a new command (the alert is already silenced).
        ClockAlert? alert;
        string? token;
        lock (_lock)
        {
            alert = _interruptedAlert;
            _interruptedAlert = null;
            token = _pendingConfirmation;
            _pendingConfirmation = null;
        }
        if (alert is not null && AlertReplies.Interpret(transcript) is { } reply)
        {
            if (reply == AlertReply.Snooze && alert.CanSnooze)
            {
                var date = _clock.Snooze(alert);
                Apply(new AssistantEvent.TextOnly($"Snoozed until {ClockFormat.Time(date)}"), id);
            }
            else
            {
                Apply(new AssistantEvent.TextOnly(alert.Kind == CountdownKind.Timer ? "Timer stopped" : $"{ClockAlert.Capitalised(alert.Kind.ToString().ToLowerInvariant())} stopped"), id);
            }
            return;
        }

        // A pending batch: "yes" applies it, "no" drops it, anything else is a
        // new command and the batch is dropped.
        if (token is not null)
        {
            switch (FileServices.Answer(transcript))
            {
                case true:
                    Apply(new AssistantEvent.ToolCall("organiseFiles", "Files", "Confirmed"), id);
                    lock (_lock) _resultDelay = TimeSpan.FromSeconds(5);
                    Apply(new AssistantEvent.Done(_files.Confirm(token), []), id);
                    return;
                case false:
                    _files.Discard();
                    Apply(new AssistantEvent.TextOnly("Cancelled. Nothing was changed."), id);
                    return;
                default:
                    _files.Discard();
                    break;
            }
        }

        var tools = _registry.EnabledTools();
        var plan = Routines.Match(transcript, _routines()) is { } routine
            ? Routines.Plan(routine, tools)
            : await _engine.PlanAsync(transcript, tools, cancellationToken);
        if (plan.Steps.Count == 0 && plan.Reply is { } text)
        {
            Apply(new AssistantEvent.TextOnly(text), id);
            return;
        }
        if (plan.Routine is { } run)
        {
            await RunRoutine(run, plan.Steps, id, cancellationToken);
            return;
        }
        if (plan.Steps.Count == 0) throw new AssistantFailure("I can't do that yet");

        var outcomes = new List<ToolResult>();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = plan.Steps[i];
            Apply(new AssistantEvent.ToolCall(step.Tool.Name, step.Tool.Title, step.Target()), id);
            // A failing step leaves the remaining steps unexecuted (spec §4).
            outcomes.Add(await step.ExecuteAsync(i == plan.Steps.Count - 1, cancellationToken));
        }
        var last = outcomes[^1];
        var summary = string.Join(" · ", outcomes.Select(o => o.Text));
        if (last.Confirmation is { } confirmation)
        {
            lock (_lock) _pendingConfirmation = confirmation;
            Apply(new AssistantEvent.NeedsConfirmation(summary, last.Items), id);
            return;
        }
        if (outcomes.Count == 1 && last.IsAnswer)
        {
            Apply(new AssistantEvent.Answer(summary), id);
            return;
        }
        if (last.Undoable) lock (_lock) _resultDelay = TimeSpan.FromSeconds(5);
        Apply(new AssistantEvent.Done(summary, last.Items), id);
    }

    /// <summary>
    /// A routine runs every step even when one fails (Spotify being closed
    /// shouldn't stop VS Code opening), then speaks its closing line, or says
    /// what didn't work.
    /// </summary>
    private async Task RunRoutine(RoutineRun routine, IReadOnlyList<PlannedStep> steps, int id, CancellationToken cancellationToken)
    {
        var failures = routine.Skipped.Select(s => $"{s} (turned off)").ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[i];
            Apply(new AssistantEvent.ToolCall(step.Tool.Name, step.Tool.Title, step.Target()), id);
            try
            {
                await step.ExecuteAsync(i == steps.Count - 1, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add($"{step.Tool.Title} “{step.Target()}”: {Describe(e)}");
            }
        }
        var ran = steps.Count + routine.Skipped.Count - failures.Count;
        if (failures.Count == 0)
            Apply(new AssistantEvent.Answer(routine.Closing ?? $"{routine.Name}: done"), id);
        else if (ran == 0)
            Apply(new AssistantEvent.Failure($"{routine.Name} didn't work. {string.Join(" · ", failures)}"), id);
        else
            Apply(new AssistantEvent.Answer($"{(routine.Closing is null ? "" : routine.Closing + " ")}But {failures.Count} step{(failures.Count == 1 ? "" : "s")} didn't work: {string.Join(" · ", failures)}"), id);
    }

    private void Received(SpeechUpdate update, int id)
    {
        switch (update)
        {
            case SpeechUpdate.Level level:
                bool listening;
                lock (_lock) listening = id == _session && _state is AssistantState.Listening;
                if (listening) _presenter.AudioLevel(level.Value);
                break;
            case SpeechUpdate.EndOfSpeech or SpeechUpdate.NoSpeech:
                // No speech after waking: the command may still be there, said
                // in one breath before the detection. Finishing transcribes it.
                bool current;
                lock (_lock) current = id == _session;
                if (current) Release();
                break;
        }
    }

    private void Apply(AssistantEvent e, int id)
    {
        AssistantState next;
        lock (_lock)
        {
            if (id != _session || StateMachine.Transition(_state, e) is not { } state) return;
            next = _state = state;
            ScheduleDismissal(next, id);
            // Inside the lock, so renders reach the presenter in order; it only posts.
            _presenter.Render(next);
        }
        StateChanged?.Invoke(next);
        if (next is AssistantState.Idle)
        {
            ClockAlert? queued = null;
            lock (_lock)
            {
                if (_queuedAlerts.Count > 0) queued = _queuedAlerts.Dequeue();
            }
            if (queued is not null) Ring(queued);
        }
    }

    /// <summary>Called under the lock.</summary>
    private void ScheduleDismissal(AssistantState state, int id)
    {
        _dismissal?.Cancel();
        _dismissal = null;
        TimeSpan delay;
        switch (state)
        {
            case AssistantState.Result:
                delay = _resultDelay;
                _resultDelay = TimeSpan.FromSeconds(3);
                break;
            case AssistantState.Confirm: delay = TimeSpan.FromSeconds(20); break;
            case AssistantState.Reply reply: delay = TimeSpan.FromSeconds(Math.Clamp(reply.Text.Length / 15.0, 4, 12)); break;
            case AssistantState.List: delay = TimeSpan.FromSeconds(10); break;
            case AssistantState.Error: delay = TimeSpan.FromSeconds(5); break;
            case AssistantState.Alert: delay = RingFor; break;
            default: return;
        }
        var cancel = _dismissal = new CancellationTokenSource();
        _ = Task.Delay(delay, cancel.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Dismiss(id);
        }, TaskScheduler.Default);
    }

    private void Dismiss(int id)
    {
        lock (_lock)
        {
            if (id != _session) return;
            switch (_state)
            {
                case AssistantState.Alert alert:
                    _clock.Missed(alert.Clock); // Rang for a minute with nobody there.
                    break;
                case AssistantState.Confirm:
                    _pendingConfirmation = null; // Unanswered: the batch is dropped, nothing changes.
                    _files.Discard();
                    break;
            }
        }
        Apply(new AssistantEvent.Dismiss(), id);
    }

    /// <summary>A sentence for the user, whatever went wrong.</summary>
    public static string Describe(Exception e) => e switch
    {
        ToolException or AssistantFailure => e.Message,
        UnauthorizedAccessException => "Windows didn't allow that",
        IOException io => $"That didn't work ({io.Message})",
        _ => $"Something went wrong ({e.Message})",
    };
}
