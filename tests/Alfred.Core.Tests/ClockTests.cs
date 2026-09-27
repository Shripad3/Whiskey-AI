using System.Globalization;
using Alfred.Core;

namespace Alfred.Core.Tests;

/// <summary>Ported from the Mac's ClockTests.swift.</summary>
public class SpokenDurationTests
{
    [Theory]
    [InlineData("10 minutes", 600)]
    [InlineData("an hour and a half", 5400)]
    [InlineData("half an hour", 1800)]
    [InlineData("1 hour and 30 minutes", 5400)]
    [InlineData("1 hour 30 minutes", 5400)]
    [InlineData("ninety seconds", 90)]
    [InlineData("1.5 hours", 5400)]
    [InlineData("twenty five minutes", 1500)]
    [InlineData("twenty-five minutes", 1500)]
    [InlineData("a minute", 60)]
    [InlineData("one and a half hours", 5400)]
    [InlineData("quarter of an hour", 900)]
    [InlineData("2 mins", 120)]
    [InlineData("set a timer for 3 minutes please", 180)]
    public void Parses(string said, double seconds) => Assert.Equal(seconds, SpokenDuration.Parse(said));

    [Theory]
    [InlineData("set a timer")]
    [InlineData("at 7")]
    [InlineData("the 10 commandments")]
    public void Rejects(string said) => Assert.Null(SpokenDuration.Parse(said));

    [Fact]
    public void Formats()
    {
        Assert.Equal("1 hour 30 minutes", ClockFormat.Spoken(5400));
        Assert.Equal("9 minutes 41 seconds", ClockFormat.Spoken(581));
        Assert.Equal("1 second", ClockFormat.Spoken(1));
        Assert.Equal("10 minute", ClockFormat.Adjective(600));
        Assert.Equal("9:41", ClockFormat.Clock(581));
        Assert.Equal("1:02:03", ClockFormat.Clock(3723));
        Assert.Equal("1:23.4", ClockFormat.Stopwatch(83.45));
    }
}

public class SpokenWhenTests
{
    /// <summary>Saturday 26 September 2026, 10:00.</summary>
    public static readonly DateTime Now = new(2026, 9, 26, 10, 0, 0);

    private static DateTime Date(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0);

    public static TheoryData<string, DateTime> Cases() => new()
    {
        { "7 am", Date(27, 7) },
        { "7 a.m.", Date(27, 7) },
        { "7am", Date(27, 7) },
        { "for 7", Date(26, 19) },
        { "at 9", Date(26, 21) },
        { "tomorrow at 6:30", Date(27, 6, 30) },
        { "tonight at 8", Date(26, 20) },
        { "noon", Date(26, 12) },
        { "at midnight", Date(27, 0) },
        { "half past 5", Date(26, 17, 30) },
        { "quarter to 8", Date(26, 19, 45) },
        { "20 past 11", Date(26, 11, 20) },
        { "in 20 minutes", Date(26, 10, 20) },
        { "on Monday at 9 am", Date(28, 9) },
        { "7:30 pm", Date(26, 19, 30) },
        { "seven thirty am", Date(27, 7, 30) },
        { "730 am", Date(27, 7, 30) },
        { "tomorrow morning at 7", Date(27, 7) },
        { "at 6 in the evening", Date(26, 18) },
        { "at 7 o'clock", Date(26, 19) },
    };

    [Theory, MemberData(nameof(Cases))]
    public void Parses(string said, DateTime expected)
    {
        var when = SpokenWhen.Parse(said, Now);
        Assert.NotNull(when);
        Assert.Equal(expected, when.Date);
        Assert.True(when.HasTime);
    }

    [Fact]
    public void DayWithoutTime()
    {
        var when = SpokenWhen.Parse("tomorrow", Now)!;
        Assert.False(when.HasTime);
        Assert.Equal(Date(27, 0), when.Date);
    }

    [Theory]
    [InlineData("buy 2 apples")]
    [InlineData("call Mum")]
    [InlineData("10 minutes")]
    public void NoTime(string said) => Assert.Null(SpokenWhen.Parse(said, Now));

    [Fact]
    public void PrefersTheSpokenTimeOverOtherNumbers() => Assert.Equal(Date(26, 18), SpokenWhen.Parse("set the table for 4 at 6", Now)!.Date);

    [Fact]
    public void FindsEveryTimeSaid()
    {
        Assert.Equal([7, 8], SpokenWhen.ClockTimes("wake me up at seven on weekdays and eight on weekends").Select(t => t.Hour));
        Assert.Equal([10, 20, 30], SpokenDuration.All("one of 10 seconds one for 20 seconds and another for 30 seconds"));
    }
}

public class ClockDirectTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    private (string Tool, System.Text.Json.Nodes.JsonObject Args)? Match(string said) => _world.Direct(said);

    [Fact]
    public void Timers()
    {
        var args = Match("Set a timer for 10 minutes.")!.Value;
        Assert.Equal("timer", args.Tool);
        Assert.Equal("start", args.Args.Str("action"));
        Assert.Equal("10 minutes", args.Args.Str("duration"));
        Assert.Null(args.Args.Str("label"));
        Assert.Equal("pasta", Match("set a pasta timer for 10 minutes")!.Value.Args.Str("label"));
        Assert.Equal("eggs", Match("set a timer for 5 minutes for the eggs")!.Value.Args.Str("label"));
        Assert.Null(Match("set a 10 minute timer")!.Value.Args.Str("label"));
        Assert.Equal("cancel", Match("cancel the timer")!.Value.Args.Str("action"));
        Assert.Equal("pasta", Match("stop the pasta timer")!.Value.Args.Str("label"));
        Assert.Equal("all", Match("cancel all timers")!.Value.Args.Str("label"));
        Assert.Equal("status", Match("how much time is left")!.Value.Args.Str("action"));
        Assert.Equal("status", Match("how long is left on my timer")!.Value.Args.Str("action"));
        Assert.Equal("pause", Match("pause the timer")!.Value.Args.Str("action"));
        Assert.Equal("resume", Match("resume the timer")!.Value.Args.Str("action"));
        var add = Match("add 5 minutes to the timer")!.Value.Args;
        Assert.Equal("add", add.Str("action"));
        Assert.Equal("5 minutes", add.Str("duration"));
        var tea = Match("could you time 5 minutes for my tea")!.Value.Args;
        Assert.Equal("start", tea.Str("action"));
        Assert.Equal("tea", tea.Str("label"));
        Assert.NotEqual("timer", Match("start the stopwatch")?.Tool);
    }

    [Fact]
    public void Alarms()
    {
        var args = Match("Set an alarm for 7 a.m.")!.Value;
        Assert.Equal("alarm", args.Tool);
        Assert.Equal("set", args.Args.Str("action"));
        Assert.NotNull(SpokenWhen.Parse(args.Args.Str("time")!, DateTime.Now));
        Assert.Equal("set", Match("wake me up at 6:30 tomorrow")!.Value.Args.Str("action"));
        Assert.Equal("list", Match("what alarms do I have")!.Value.Args.Str("action"));
        var cancel = Match("cancel my 7 am alarm")!.Value.Args;
        Assert.Equal("cancel", cancel.Str("action"));
        Assert.NotNull(cancel.Str("time"));
        Assert.Equal("gym", Match("set a gym alarm for 6")!.Value.Args.Str("label"));
        Assert.Equal("gym", Match("set an alarm for 7 called gym")!.Value.Args.Str("label"));
        Assert.Equal("all", Match("turn off all alarms")!.Value.Args.Str("label"));
        var list = Match("do I have any alarms set")!.Value.Args;
        Assert.Equal("list", list.Str("action"));
        Assert.Null(list.Str("label"));
    }

    [Theory]
    [InlineData("start a stopwatch", "start")]
    [InlineData("start the stop watch", "start")]
    [InlineData("stop the stopwatch", "stop")]
    [InlineData("pause the stopwatch", "stop")]
    [InlineData("lap", "lap")]
    [InlineData("reset the stopwatch", "reset")]
    [InlineData("restart the stopwatch", "restart")]
    [InlineData("how long has the stopwatch been running", "status")]
    [InlineData("resume the stopwatch", "resume")]
    public void Stopwatches(string said, string action)
    {
        var match = Match(said)!.Value;
        Assert.Equal("stopwatch", match.Tool);
        Assert.Equal(action, match.Args.Str("action"));
    }

    [Fact]
    public void Reminders()
    {
        var args = Match("Remind me to call Mum at 6.")!.Value.Args;
        Assert.Equal("call Mum", args.Str("task"));
        Assert.Equal("at 6", args.Str("when"));
        args = Match("remind me tomorrow to buy milk")!.Value.Args;
        Assert.Equal("buy milk", args.Str("task"));
        Assert.Equal("tomorrow", args.Str("when"));
        args = Match("remind me in 20 minutes to check the oven")!.Value.Args;
        Assert.Equal("check the oven", args.Str("task"));
        Assert.Equal("in 20 minutes", args.Str("when"));
        args = Match("set a reminder to pay rent on Monday")!.Value.Args;
        Assert.Equal("pay rent", args.Str("task"));
        Assert.Equal("on Monday", args.Str("when"));
        args = Match("remind me that I need to email Sam")!.Value.Args;
        Assert.Equal("email Sam", args.Str("task"));
        Assert.Null(args.Str("when"));
    }

    [Theory]
    [InlineData("what time is it", "time", null)]
    [InlineData("What's the time in Tokyo?", "time", "tokyo")]
    [InlineData("what's the date", "date", null)]
    [InlineData("what day is it", "date", null)]
    public void Time(string said, string what, string? place)
    {
        var match = Match(said)!.Value;
        Assert.Equal("currentTime", match.Tool);
        Assert.Equal(what, match.Args.Str("what"));
        Assert.Equal(place, match.Args.Str("place"));
    }

    [Theory]
    [InlineData("set a timer for an hour and a half", "timer")]
    [InlineData("set a timer for 1 hour and 30 minutes", "timer")]
    [InlineData("remind me to buy bread and milk", "reminder")]
    [InlineData("start the stopwatch", "stopwatch")]
    [InlineData("stop the timer", "timer")]
    [InlineData("set an alarm for 7 am", "alarm")]
    [InlineData("what time is it", "currentTime")]
    [InlineData("stop", "controlSpotify")]
    public void RoutesDirectly(string said, string tool) => Assert.Equal(tool, Match(said)?.Tool);

    [Fact]
    public void CompoundCommandsStillGoToTheModel() => Assert.Null(Match("set a timer for 10 minutes and open Spotify"));
}

public class ClockStoreTests : IDisposable
{
    private readonly World _world = new();
    private ClockStore Store => _world.Clock;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task TimersStartReportAndCancel()
    {
        var result = await _world.Say("set a pasta timer for 10 minutes");
        Assert.Equal("Pasta timer set for 10 minutes", result.Text);
        Assert.Equal("pasta", Assert.Single(Store.Snapshot.Timers).Label);

        var status = await _world.Run("timer", null, ("action", "status"));
        Assert.True(status.IsAnswer);
        Assert.EndsWith("left on your pasta timer", status.Text);

        await _world.Run("timer", null, ("action", "cancel"));
        Assert.Empty(Store.Snapshot.Timers);
    }

    [Fact]
    public async Task AmbiguousCancelAsksWhich()
    {
        Store.AddTimer(60, "tea");
        Store.AddTimer(600, "pasta");
        await Assert.ThrowsAsync<ToolException>(() => _world.Run("timer", null, ("action", "cancel")));
        await _world.Run("timer", null, ("action", "cancel"), ("label", "tea"));
        Assert.Equal(["pasta"], Store.Snapshot.Timers.Select(t => t.Label));
    }

    [Fact]
    public void PauseKeepsTheRemainingTime()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0);
        var store = new ClockStore(null, () => now);
        var timer = store.AddTimer(600, null);
        now = now.AddSeconds(100);
        store.Pause(timer.Id);
        Assert.Equal(500, store.Snapshot.Timers[0].Remaining(now));
        now = now.AddSeconds(900);
        store.Resume(timer.Id);
        Assert.Equal(now.AddSeconds(500), store.Snapshot.Timers[0].FireDate);
    }

    [Fact]
    public void DueCountdownsRingOnce()
    {
        var rung = new List<ClockAlert>();
        Store.Start(null, _ => { }, rung.Add);
        Store.AddTimer(-2, "tea");
        Store.FireDue();
        Store.FireDue();
        Assert.Equal("Time's up. Your tea timer is done.", Assert.Single(rung).Message);
        Assert.Empty(Store.Snapshot.Countdowns);
    }

    [Fact]
    public void SnoozeMakesANewAlarm()
    {
        var alarm = Store.AddAlarm(DateTime.Now.AddSeconds(-1), "gym");
        var date = Store.Snooze(ClockAlert.From(alarm));
        Assert.InRange((date - DateTime.Now).TotalMinutes, 8.9, 9.1);
        Assert.Contains(Store.Snapshot.Alarms, a => a.Label == "gym" && a.FireDate == date);
    }

    [Fact]
    public async Task StopwatchStartsLapsAndStops()
    {
        await _world.Run("stopwatch", null, ("action", "start"));
        Assert.True(Store.Snapshot.Stopwatch.IsRunning);
        Assert.StartsWith("Lap 1:", (await _world.Run("stopwatch", null, ("action", "lap"))).Text);
        Assert.StartsWith("Stopwatch stopped at", (await _world.Run("stopwatch", null, ("action", "stop"))).Text);
        Assert.False(Store.Snapshot.Stopwatch.IsRunning);
        await _world.Run("stopwatch", null, ("action", "reset"));
        Assert.True(Store.Snapshot.Stopwatch.IsIdle);
    }

    [Theory]
    [InlineData("stop", AlertReply.Stop)]
    [InlineData("okay thanks", AlertReply.Stop)]
    [InlineData("I'm up", AlertReply.Stop)]
    [InlineData("turn it off", AlertReply.Stop)]
    [InlineData("snooze", AlertReply.Snooze)]
    [InlineData("snooze for a bit", AlertReply.Snooze)]
    [InlineData("turn off the lights", null)]
    [InlineData("open Spotify", null)]
    public void AlertReplies(string said, AlertReply? reply) => Assert.Equal(reply, Alfred.Core.AlertReplies.Interpret(said));

    [Fact]
    public void SurvivesARestart()
    {
        var file = Path.Combine(_world.Tree.Base, "clock.json");
        var first = new ClockStore(file);
        first.AddAlarm(DateTime.Now.AddHours(2), "gym", [2, 3]);
        first.AddTimer(300, "tea");
        var second = new ClockStore(file);
        Assert.Equal("gym", Assert.Single(second.Snapshot.Alarms).Label);
        Assert.Equal([2, 3], second.Snapshot.Alarms[0].RepeatDays!.Order());
        Assert.Equal("tea", Assert.Single(second.Snapshot.Timers).Label);
    }

    [Fact]
    public void DamagedFileStartsEmpty()
    {
        var file = _world.Tree.File("clock.json", _world.Tree.Base, "{ not json");
        Assert.True(new ClockStore(file).Snapshot.IsEmpty);
    }
}

public class RepeatingAlarmTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("set an alarm for 7 every day", new[] { 1, 2, 3, 4, 5, 6, 7 })]
    [InlineData("wake me up at 6:30 on weekdays", new[] { 2, 3, 4, 5, 6 })]
    [InlineData("alarm at 9 every weekend", new[] { 1, 7 })]
    [InlineData("set an alarm for 7 every Monday and Wednesday", new[] { 2, 4 })]
    [InlineData("set an alarm for 8 on Fridays", new[] { 6 })]
    public void ParsesDays(string said, int[] days)
    {
        Assert.Equal(days, AlarmRepeat.Parse(said)!.Order());
        var args = _world.Direct(said)!.Value.Args;
        Assert.Equal("set", args.Str("action"));
        Assert.NotNull(args.Str("repeats"));
        Assert.NotNull(args.Str("time"));
    }

    [Fact]
    public void OneOffDaysDontRepeat() => Assert.Null(AlarmRepeat.Parse("set an alarm for Monday at 7"));

    [Fact]
    public void DescribesDays()
    {
        Assert.Equal("every day", AlarmRepeat.Describe(new HashSet<int>(Enumerable.Range(1, 7))));
        Assert.Equal("on weekdays", AlarmRepeat.Describe(new HashSet<int> { 2, 3, 4, 5, 6 }));
        Assert.Equal("every Monday and Wednesday", AlarmRepeat.Describe(new HashSet<int> { 2, 4 }));
        Assert.Equal("every Monday and Sunday", AlarmRepeat.Describe(new HashSet<int> { 1, 2 }));
    }

    [Fact]
    public void NextSkipsOtherDays() =>
        Assert.Equal(new DateTime(2026, 9, 28, 7, 0, 0), AlarmRepeat.Next(SpokenWhenTests.Now, new DateTime(2026, 9, 26, 7, 0, 0), AlarmRepeat.Weekdays));

    [Fact]
    public void RepeatingAlarmComesBack()
    {
        _world.Clock.Start(null, _ => { }, _ => { });
        _world.Clock.AddAlarm(DateTime.Now.AddSeconds(-1), null, [.. Enumerable.Range(1, 7)]);
        _world.Clock.FireDue();
        Assert.True(Assert.Single(_world.Clock.Snapshot.Alarms).FireDate > DateTime.Now);
    }

    [Fact]
    public async Task WeekdayAlarmIsInTheMorning()
    {
        var result = await _world.Say("wake me up at 7 every weekday");
        Assert.Contains("on weekdays", result.Text);
        Assert.Equal(7, Assert.Single(_world.Clock.Snapshot.Alarms).FireDate.Hour);
    }

    [Fact]
    public void CompoundCheckAllowsListedDays()
    {
        Assert.False(DirectMatcher.IsCompound("set an alarm for 7 every monday and wednesday"));
        Assert.True(DirectMatcher.IsCompound("set an alarm for 7 and open spotify"));
    }
}

/// <summary>Several clock steps planned by the model from one sentence.</summary>
public class MultiStepClockTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task WeekdaysAndWeekendsGetTheirOwnTimes()
    {
        const string said = "Wake me up at seven on weekdays and eight on weekends";
        // What the Mac's model actually sent (reworded; not literally said).
        var one = await _world.Run("alarm", said, ("action", "set"), ("time", "weekdays at 7 am"), ("repeats", "every weekday"));
        var two = await _world.Run("alarm", said, ("action", "set"), ("time", "weekends at 8 am"), ("repeats", "every weekend"));
        Assert.EndsWith("on weekdays", one.Text);
        Assert.EndsWith("at weekends", two.Text);
        var alarms = _world.Clock.Snapshot.Alarms;
        Assert.Equal(7, alarms.Single(a => a.RepeatDays!.SetEquals(AlarmRepeat.Weekdays)).FireDate.Hour);
        Assert.Equal(8, alarms.Single(a => a.RepeatDays!.SetEquals([1, 7])).FireDate.Hour);
    }

    [Fact]
    public async Task ATimeTheUserNeverSaidIsRefused() =>
        await Assert.ThrowsAsync<ToolException>(() => _world.Run("alarm", "Wake me up at seven on weekdays and eight on weekends",
            ("action", "set"), ("time", "9 am"), ("repeats", "every weekday")));

    [Fact]
    public async Task ThreeTimersKeepTheirLengths()
    {
        const string said = "run three timers one of 10 seconds one for 20 seconds and another for 30 seconds";
        foreach (var duration in new[] { "10 seconds", "20 seconds", "half a minute" })
            await _world.Run("timer", said, ("action", "start"), ("duration", duration));
        Assert.Equal([10.0, 20, 30], _world.Clock.Snapshot.Timers.Select(t => t.Duration).Order());
    }
}

public class AlarmSettingsTests
{
    [Fact]
    public void SwitchedOffAlarmsDontRingButAreKept()
    {
        using var store = new ClockStore(null);
        var rung = 0;
        // Switched off before the store starts ringing: once started, its own
        // timer could ring the overdue alarm before the test switches it off.
        var alarm = store.AddAlarm(DateTime.Now.AddSeconds(-1), null);
        store.SetEnabled(alarm.Id, false);
        store.Start(null, _ => { }, _ => rung++);
        store.FireDue();
        Assert.Equal(0, rung);
        Assert.False(Assert.Single(store.Snapshot.Alarms).IsEnabled);
    }

    [Fact]
    public void SwitchingBackOnMovesAPastAlarmForward()
    {
        using var store = new ClockStore(null);
        var alarm = store.AddAlarm(DateTime.Now.AddHours(-1), null);
        store.SetEnabled(alarm.Id, false);
        store.SetEnabled(alarm.Id, true);
        var fire = store.Snapshot.Alarms[0].FireDate;
        Assert.InRange(fire, DateTime.Now, DateTime.Now.AddDays(1));
    }

    [Fact]
    public void EditingChangesTimeNameAndDays()
    {
        using var store = new ClockStore(null);
        var alarm = store.AddAlarm(7, 0, null, null);
        store.UpdateAlarm(alarm.Id, 6, 45, "Gym", [2, 4]);
        var edited = store.Snapshot.Alarms[0];
        Assert.Equal((6, 45), (edited.FireDate.Hour, edited.FireDate.Minute));
        Assert.Contains(AlarmRepeat.Weekday(edited.FireDate), new[] { 2, 4 });
        Assert.Equal("Gym", edited.Label);
        store.UpdateAlarm(alarm.Id, 6, 45, "", []);
        Assert.Null(store.Snapshot.Alarms[0].Label);
        Assert.Null(store.Snapshot.Alarms[0].RepeatDays);
    }
}

public class ReminderTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task GoesToTheConnectedList()
    {
        var result = await _world.Say("remind me to call Mum at 6");
        Assert.StartsWith("I'll remind you to call Mum at ", result.Text);
        Assert.Equal("Call Mum", Assert.Single(_world.Reminders.Added).Title);
    }

    [Fact]
    public async Task UnsaidTaskIsRefused() =>
        await Assert.ThrowsAsync<ToolException>(() => _world.Run("reminder", "remind me at 6", ("task", "call the bank")));

    [Fact]
    public async Task InAppRemindersRingLikeAlarms()
    {
        var accounts = new Accounts(_world.Settings, _world.Secrets, _world.Launcher, _world.Clock);
        var tool = new ReminderTool(_world.Clock, accounts);
        var result = await tool.ExecuteAsync(Setup.Args(("task", "check the oven"), ("when", "in 20 minutes")),
            new ToolContext("remind me in 20 minutes to check the oven"), CancellationToken.None);
        Assert.StartsWith("I'll remind you to check the oven", result.Text);
        var reminder = Assert.Single(_world.Clock.Snapshot.Reminders);
        Assert.Equal("Reminder: Check the oven.", ClockAlert.From(reminder).Message);
        Assert.True(ClockAlert.From(reminder).CanSnooze);
    }

    [Fact]
    public async Task UndatedInAppReminderAsksWhen()
    {
        var accounts = new Accounts(_world.Settings, _world.Secrets, _world.Launcher, _world.Clock);
        var tool = new ReminderTool(_world.Clock, accounts);
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(Setup.Args(("task", "email Sam")),
            new ToolContext("remind me to email Sam"), CancellationToken.None));
    }

    [Fact]
    public void PhrasesTheTime()
    {
        var now = SpokenWhenTests.Now;
        Assert.Equal("tomorrow", ReminderTool.Phrase(SpokenWhen.Parse("tomorrow", now)!, now));
        Assert.StartsWith("at ", ReminderTool.Phrase(SpokenWhen.Parse("at 6", now)!, now));
        Assert.Equal("on Monday", ReminderTool.Phrase(SpokenWhen.Parse("on Monday", now)!, now));
    }
}

public class CurrentTimeTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task SaysTheTime() => Assert.StartsWith("It's ", (await _world.Say("what time is it")).Text);

    [Fact]
    public async Task KnownCitiesNeedNoNetwork()
    {
        Assert.NotNull(CurrentTimeTool.KnownZone("Tokyo"));
        var result = await _world.Say("what's the time in Tokyo");
        Assert.EndsWith(" in Tokyo", result.Text);
    }

    [Fact]
    public async Task OtherPlacesUseTheGeocoder() => Assert.Contains("Leiden", (await _world.Say("what time is it in Leiden")).Text);

    [Fact]
    public async Task DatesInEnglish()
    {
        var result = await _world.Say("what's the date");
        Assert.Matches(@"^It's (Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday) \d", result.Text);
        _ = CultureInfo.CurrentCulture;
    }
}
