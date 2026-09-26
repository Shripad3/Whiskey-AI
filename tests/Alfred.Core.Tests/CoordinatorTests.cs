using Alfred.Core;

namespace Alfred.Core.Tests;

/// <summary>Ported from the Mac's StateMachineTests.swift.</summary>
public class StateMachineTests
{
    private static readonly ResultItem Item = new("file_1", "a.png", "", "photo");

    private static AssistantState? Next(AssistantState state, AssistantEvent e) => StateMachine.Transition(state, e);

    [Fact]
    public void HappyPath()
    {
        AssistantState state = new AssistantState.Idle();
        var steps = new (AssistantEvent Event, AssistantState Expected)[]
        {
            (new AssistantEvent.Activation(), new AssistantState.Listening("")),
            (new AssistantEvent.Partial("open"), new AssistantState.Listening("open")),
            (new AssistantEvent.Endpoint("open spotify"), new AssistantState.Thinking("open spotify")),
            (new AssistantEvent.ToolCall("openApp", "Open app", "Spotify"), new AssistantState.Acting("openApp", "Open app", "Spotify")),
            (new AssistantEvent.Done("Opened Spotify", []), new AssistantState.Result("Opened Spotify")),
            (new AssistantEvent.Dismiss(), new AssistantState.Idle()),
        };
        foreach (var (e, expected) in steps)
        {
            state = Next(state, e)!;
            Assert.Equal(expected, state);
        }
    }

    [Fact]
    public void CancelFromAnyNonIdleState()
    {
        foreach (var state in new AssistantState[]
                 {
                     new AssistantState.Listening(""), new AssistantState.Thinking("x"), new AssistantState.Acting("a", "A", "x"),
                     new AssistantState.Result("done"), new AssistantState.Error("e", null),
                 })
            Assert.IsType<AssistantState.Idle>(Next(state, new AssistantEvent.Cancel()));
        Assert.Null(Next(new AssistantState.Idle(), new AssistantEvent.Cancel()));
    }

    [Fact]
    public void FailureShowsErrorFromBusyStates()
    {
        foreach (var state in new AssistantState[] { new AssistantState.Idle(), new AssistantState.Listening(""), new AssistantState.Thinking("x"), new AssistantState.Acting("a", "A", "x") })
            Assert.Equal(new AssistantState.Error("boom", null), Next(state, new AssistantEvent.Failure("boom")));
    }

    [Fact]
    public void IgnoredEvents()
    {
        Assert.Null(Next(new AssistantState.Thinking("x"), new AssistantEvent.Dismiss()));
        Assert.Null(Next(new AssistantState.Thinking("x"), new AssistantEvent.Activation()));
        Assert.Null(Next(new AssistantState.Result("x"), new AssistantEvent.Activation()));
        Assert.Null(Next(new AssistantState.Thinking("x"), new AssistantEvent.Partial("x y")));
        Assert.Null(Next(new AssistantState.Result("x"), new AssistantEvent.Selected("y")));
        Assert.Null(Next(new AssistantState.Thinking("x"), new AssistantEvent.Ring(ClockAlert.Test(CountdownKind.Alarm))));
    }

    [Fact]
    public void ListsConfirmationsAndAlerts()
    {
        Assert.IsType<AssistantState.List>(Next(new AssistantState.Acting("f", "F", ""), new AssistantEvent.Done("Found 1", [Item])));
        Assert.Equal(new AssistantState.Result("Opened a.png"), Next(new AssistantState.List("Found 1", [Item]), new AssistantEvent.Selected("Opened a.png")));
        Assert.IsType<AssistantState.Confirm>(Next(new AssistantState.Acting("f", "F", ""), new AssistantEvent.NeedsConfirmation("Move 2 files?", [Item])));
        Assert.IsType<AssistantState.Listening>(Next(new AssistantState.Confirm("q", [Item]), new AssistantEvent.Activation()));
        Assert.IsType<AssistantState.Alert>(Next(new AssistantState.Idle(), new AssistantEvent.Ring(ClockAlert.Test(CountdownKind.Alarm))));
        Assert.IsType<AssistantState.Listening>(Next(new AssistantState.Alert(ClockAlert.Test(CountdownKind.Alarm)), new AssistantEvent.Activation()));
        Assert.Equal(new AssistantState.Reply("It's 20°"), Next(new AssistantState.Acting("w", "W", ""), new AssistantEvent.Answer("It's 20°")));
    }
}

/// <summary>A microphone that "hears" a given transcript.</summary>
internal sealed class FakeSpeech : ISpeechInput
{
    public string Transcript { get; set; } = "";
    public string? Problem { get; set; }
    public Action<SpeechUpdate>? Updates { get; private set; }
    public int Cancels { get; private set; }

    public string? UnavailableReason() => Problem;

    public Task StartAsync(WakeContext? wake, Action<SpeechUpdate> onUpdate)
    {
        Updates = onUpdate;
        return Task.CompletedTask;
    }

    public Task<string> FinishAsync(CancellationToken cancellationToken) => Task.FromResult(Transcript);

    public Task CancelAsync()
    {
        Cancels++;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingPresenter : IPresenter
{
    private readonly List<AssistantState> _states = [];
    public IReadOnlyList<AssistantState> States { get { lock (_states) return [.. _states]; } }
    public void Render(AssistantState state) { lock (_states) _states.Add(state); }
    public void AudioLevel(float level) { }
}

public class CoordinatorTests : IDisposable
{
    private readonly World _world = new();
    private readonly FakeSpeech _speech = new();
    private readonly RecordingPresenter _presenter = new();
    private readonly AssistantCoordinator _coordinator;

    public CoordinatorTests()
    {
        var engine = new LocalModelEngine(new FakeModel("""{"steps": []}"""));
        _coordinator = new AssistantCoordinator(_speech, engine, ToolRegistry.Standard(_world.Services), _presenter, _world.Clock, _world.Files,
            () => Routines.Examples);
    }

    public void Dispose() => _world.Dispose();

    private async Task<AssistantState> Say(string transcript, WakeContext? wake = null)
    {
        _speech.Transcript = transcript;
        await _coordinator.ActivateAsync(wake);
        _coordinator.Release();
        return await Settled();
    }

    /// <summary>Waits until the coordinator stops working on the command.</summary>
    private async Task<AssistantState> Settled()
    {
        for (var i = 0; i < 200; i++)
        {
            if (_coordinator.State is not (AssistantState.Listening or AssistantState.Thinking or AssistantState.Acting)) return _coordinator.State;
            await Task.Delay(10);
        }
        return _coordinator.State;
    }

    [Fact]
    public async Task DirectCommandRunsAndShowsItsResult()
    {
        var state = await Say("open spotify");
        Assert.Equal(new AssistantState.Result("Opened Spotify"), state);
        Assert.Equal(["app Spotify"], _world.Launcher.Launched);
        Assert.Contains(_presenter.States, s => s is AssistantState.Acting { Tool: "openApp" });
    }

    [Fact]
    public async Task SilenceGoesBackToIdle() => Assert.IsType<AssistantState.Idle>(await Say("   "));

    [Fact]
    public async Task AnswersBecomeReplies() => Assert.IsType<AssistantState.Reply>(await Say("what time is it"));

    [Fact]
    public async Task SmallTalkReplies() => Assert.IsType<AssistantState.Reply>(await Say("hello"));

    [Fact]
    public async Task FailuresAreShownInWords()
    {
        var state = await Say("open photoshop and then open gmail");
        // The fake model plans nothing for this compound command.
        Assert.Equal(new AssistantState.Error("I can't do that yet", null), state);
    }

    [Fact]
    public async Task RoutinesRunEveryStepAndReportFailures()
    {
        _world.Media.Installed = false;
        var state = await Say("I'm home");
        var reply = Assert.IsType<AssistantState.Reply>(state);
        Assert.StartsWith("Welcome home. But 1 step didn't work: Spotify", reply.Text);
        Assert.Contains("app Visual Studio Code", _world.Launcher.Launched);
    }

    [Fact]
    public async Task WakeWithoutAlfredIsDropped()
    {
        var state = await Say("he offered me coffee", new WakeContext(Confirmed: false, null));
        Assert.IsType<AssistantState.Idle>(state);
        Assert.Empty(_world.Launcher.Launched);
    }

    [Fact]
    public async Task WakeCommandInTheSameBreath()
    {
        var state = await Say("", new WakeContext(Confirmed: true, "open spotify"));
        Assert.Equal(new AssistantState.Result("Opened Spotify"), state);
    }

    [Fact]
    public async Task MissingMicrophoneIsAnError()
    {
        _speech.Problem = "No microphone";
        await _coordinator.ActivateAsync();
        Assert.Equal(new AssistantState.Error("No microphone", "Model"), _coordinator.State);
    }

    [Fact]
    public async Task EscapeCancelsFromAnywhere()
    {
        _speech.Transcript = "open spotify";
        await _coordinator.ActivateAsync();
        Assert.IsType<AssistantState.Listening>(_coordinator.State);
        await _coordinator.CancelAsync();
        Assert.IsType<AssistantState.Idle>(_coordinator.State);
        Assert.Equal(1, _speech.Cancels);
        Assert.Empty(_world.Launcher.Launched);
    }

    [Fact]
    public async Task AlertsRingWhenIdleAndStopBySpeech()
    {
        var alert = ClockAlert.From(_world.Clock.AddAlarm(DateTime.Now, "gym"));
        _coordinator.Ring(alert);
        Assert.IsType<AssistantState.Alert>(_coordinator.State);
        var state = await Say("snooze");
        Assert.StartsWith("Snoozed until", Assert.IsType<AssistantState.Reply>(state).Text);
    }

    [Fact]
    public async Task AlertsWaitForABusyPill()
    {
        _speech.Transcript = "open spotify";
        await _coordinator.ActivateAsync();
        _coordinator.Ring(ClockAlert.Test(CountdownKind.Timer));
        Assert.IsType<AssistantState.Listening>(_coordinator.State);
        await _coordinator.CancelAsync();
        Assert.IsType<AssistantState.Alert>(_coordinator.State);
    }

    [Fact]
    public async Task BatchesAreConfirmedByVoice()
    {
        for (var i = 0; i < 2; i++) _world.Tree.File($"Screenshot {i}.png", _world.Tree.Desktop);
        Assert.IsType<AssistantState.Confirm>(await Say("delete my screenshots"));
        await Task.Delay(1100); // the activation debounce
        var state = await Say("yes");
        Assert.StartsWith("Moved 2 files to the Recycle Bin", Assert.IsType<AssistantState.Result>(state).Text);
        Assert.Empty(Directory.GetFiles(_world.Tree.Desktop));
    }

    [Fact]
    public async Task BatchesAreConfirmedByButton()
    {
        for (var i = 0; i < 2; i++) _world.Tree.File($"Screenshot {i}.png", _world.Tree.Desktop);
        Assert.IsType<AssistantState.Confirm>(await Say("delete my screenshots"));
        _coordinator.ResolveConfirmation(false);
        Assert.IsType<AssistantState.Idle>(_coordinator.State);
        Assert.Equal(2, Directory.GetFiles(_world.Tree.Desktop).Length);
    }

    [Fact]
    public async Task ListRowsOpenThroughTheCoordinator()
    {
        _world.Tree.File("tax 1.pdf");
        _world.Tree.File("tax 2.pdf");
        var list = Assert.IsType<AssistantState.List>(await Say("find my tax files"));
        await _coordinator.SelectAsync(list.Items[0].Id);
        Assert.StartsWith("Opened tax", Assert.IsType<AssistantState.Result>(_coordinator.State).Text);
    }

    [Fact]
    public async Task SuspendedIgnoresActivation()
    {
        await _coordinator.SetSuspendedAsync(true);
        await _coordinator.ActivateAsync();
        Assert.IsType<AssistantState.Idle>(_coordinator.State);
    }

    [Fact]
    public async Task DisabledToolsAreNeverPlanned()
    {
        _world.Settings.Update(s => s.DisabledTools.Add("openApp"));
        var state = await Say("open spotify");
        Assert.IsType<AssistantState.Error>(state);
        Assert.Empty(_world.Launcher.Launched);
    }

    [Fact]
    public async Task TypedCommandsRunLikeSpokenOnes()
    {
        await _coordinator.RunTypedAsync("open youtube");
        Assert.Equal(new AssistantState.Result("Opened www.youtube.com"), _coordinator.State);
    }
}
