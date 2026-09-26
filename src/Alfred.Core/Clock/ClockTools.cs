using System.Globalization;

namespace Alfred.Core;

/// <summary>Shared by the clock tools.</summary>
public static class ClockPhrases
{
    /// <summary>Commands for another tool: "open Clock", "play the alarm song".</summary>
    public static readonly IReadOnlySet<string> ForeignVerbs = new HashSet<string> { "open", "launch", "go to", "search", "search for", "google", "look up", "play", "watch" };
    public static readonly IReadOnlySet<string> CancelWords = new HashSet<string> { "cancel", "stop", "delete", "remove", "clear", "end", "kill", "dismiss", "disable", "off" };
    public static readonly IReadOnlySet<string> PauseWords = new HashSet<string> { "pause", "hold", "freeze" };
    public static readonly IReadOnlySet<string> ResumeWords = new HashSet<string> { "resume", "continue", "unpause", "restart" };
    public static readonly IReadOnlySet<string> AskWords = new HashSet<string> { "how", "what", "whats", "which", "when", "check", "status", "left", "remaining", "list", "show", "any", "many" };

    /// <summary>Words that are never a timer's or alarm's name.</summary>
    private static readonly HashSet<string> NotLabels = new HashSet<string>
    {
        "a", "an", "the", "my", "set", "start", "new", "another", "create", "make", "timer", "timers", "alarm", "alarms",
        "for", "of", "to", "on", "is", "at", "in", "and", "all", "every", "this", "that", "add", "up", "me", "wake", "please",
        "how", "much", "long", "left", "check", "put", "i", "want", "need", "can", "you", "could", "hey", "okay", "ok",
        "am", "pm", "oclock", "tomorrow", "today", "tonight", "morning", "evening", "afternoon", "night",
        "do", "does", "have", "has", "got", "any", "there", "are", "what", "whats", "which", "when", "list", "show",
    }.Concat(CancelWords).Concat(PauseWords).Concat(ResumeWords).Concat(SpokenDuration.UnitWords).Concat(SpokenWords.NumberWords).ToHashSet();

    /// <summary>
    /// A name the user gave: "set a pasta timer", "timer called pasta", "a 10
    /// minute timer for the pasta". Null when none was given.
    /// </summary>
    public static string? Label(SpokenWords words, string noun, IReadOnlySet<int> taken)
    {
        var w = words.Lower;
        // "called pasta", "named pasta", "labelled pasta".
        var called = w.ToList().FindIndex(x => x is "called" or "named" or "labelled" or "labeled");
        if (called >= 0 && called + 1 < w.Count)
        {
            var rest = Enumerable.Range(called + 1, w.Count - called - 1).Where(i => !taken.Contains(i)).ToList();
            if (rest.Count > 0) return words.Text(rest);
        }
        // "pasta timer": the words just before the noun.
        var nounIndex = w.ToList().FindIndex(x => x == noun || x == noun + "s");
        if (nounIndex >= 0)
        {
            var name = new List<int>();
            for (var i = nounIndex - 1; i >= 0 && !taken.Contains(i) && !NotLabels.Contains(w[i]) && !SpokenWords.IsNumeric(w[i]); i--)
                name.Insert(0, i);
            if (name.Count is > 0 and <= 3) return words.Text(name);
        }
        // "for the pasta" at the end, when it isn't the time or duration.
        var forIndex = w.ToList().LastIndexOf("for");
        if (forIndex >= 0 && !taken.Contains(forIndex + 1) && forIndex + 1 < w.Count)
        {
            var rest = Enumerable.Range(forIndex + 1, w.Count - forIndex - 1).Where(i => w[i] is not ("the" or "my")).ToList();
            if (rest.Count is > 0 and <= 3 && rest.All(i => !taken.Contains(i) && !NotLabels.Contains(w[i]) && !SpokenWords.IsNumeric(w[i])))
                return words.Text(rest);
        }
        return null;
    }

    /// <summary>Keeps a model-supplied value only if the user said it.</summary>
    public static string? Grounded(string? value, ToolContext context)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return context.Transcript is null || Grounding.Mentions(trimmed, context.Transcript) ? trimmed : null;
    }

    public static bool SaidAll(ToolContext context) =>
        context.Transcript is { } t && new SpokenWords(t).ContainsAny(["all", "every", "everything"]);

    /// <summary>
    /// Picks the countdowns a command means: all of them, the named one, or the
    /// only one. Throws when it can't tell.
    /// </summary>
    public static List<Countdown> Select(IReadOnlyList<Countdown> candidates, string? label, bool all, string what)
    {
        if (candidates.Count == 0) throw new ToolException($"You don't have any {what}s");
        if (all || label == "all") return [.. candidates];
        if (!string.IsNullOrEmpty(label))
        {
            var lower = label.ToLowerInvariant();
            var named = candidates.Where(c => (c.Label ?? "").ToLowerInvariant().Contains(lower) || c.Name.ToLowerInvariant().Contains(lower)).ToList();
            return named.Count > 0 ? named : throw new ToolException($"There's no {lower} {what}");
        }
        if (candidates.Count == 1) return [.. candidates];
        throw new ToolException($"Which one? You have {string.Join(", ", candidates.Select(c => c.Name))}");
    }
}

// Timer

public sealed record TimerArguments(string Action, string? Duration = null, string? Label = null);

/// <summary>Countdown timers, several at once, optionally named. They ring in the pill with a sound and the voice.</summary>
public sealed class TimerTool(ClockStore store) : AssistantTool<TimerArguments>
{
    public override string Name => "timer";
    public override string Title => "Timer";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "timer", "timers", "countdown", "time" };

    public override string Description =>
        "Countdown timers. \"set a timer for 10 minutes\" → start, duration \"10 minutes\". " +
        "\"how long is left on the pasta timer\" → status, label \"pasta\". \"cancel the timer\" → cancel.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("action", "What to do", Choices: ["start", "cancel", "pause", "resume", "add", "status"]),
        new("duration", "How long, as the user said it, e.g. \"10 minutes\"", Required: false),
        new("label", "The timer's name, only if the user gave one, e.g. \"pasta\"", Required: false),
    ];

    protected override string Target(TimerArguments a) => string.Join(" · ", new[] { a.Label, a.Duration }.OfType<string>());

    protected override Task<ToolResult> ExecuteAsync(TimerArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var label = ClockPhrases.Grounded(arguments.Label, context) is { } l && l != "all" ? l : null;
        var timers = store.Snapshot.Timers;
        var all = ClockPhrases.SaidAll(context);
        switch (arguments.Action)
        {
            case "start":
            {
                var seconds = Duration(arguments.Duration, context) ?? throw new ToolException("For how long? Say “set a timer for 10 minutes”");
                if (seconds < 1 || seconds > ClockStore.Limit.TotalSeconds) throw new ToolException("Timers can run for up to a week");
                var timer = store.AddTimer(seconds, label);
                return Done($"{ClockAlert.Capitalised(timer.Name)} set for {ClockFormat.Spoken(seconds)}");
            }
            case "cancel":
            {
                var chosen = ClockPhrases.Select(timers, label, all, "timer");
                store.Remove(chosen.Select(c => c.Id));
                return Done(chosen.Count == 1 ? $"Cancelled the {chosen[0].Name}" : $"Cancelled {chosen.Count} timers");
            }
            case "pause":
            {
                var chosen = ClockPhrases.Select(timers.Where(t => !t.IsPaused).ToList(), label, all, "running timer");
                foreach (var t in chosen) store.Pause(t.Id);
                return Done(chosen.Count == 1
                    ? $"Paused the {chosen[0].Name} with {ClockFormat.Spoken(chosen[0].Remaining(store.Now))} left"
                    : $"Paused {chosen.Count} timers");
            }
            case "resume":
            {
                var chosen = ClockPhrases.Select(timers.Where(t => t.IsPaused).ToList(), label, all, "paused timer");
                foreach (var t in chosen) store.Resume(t.Id);
                return Done(chosen.Count == 1 ? $"Resumed the {chosen[0].Name}" : $"Resumed {chosen.Count} timers");
            }
            case "add":
            {
                var seconds = Duration(arguments.Duration, context) ?? throw new ToolException("How much time should I add?");
                var chosen = ClockPhrases.Select(timers, label, false, "timer");
                foreach (var t in chosen) store.Extend(t.Id, seconds);
                var timer = store.Snapshot.Timers.FirstOrDefault(t => t.Id == chosen[0].Id);
                return Done($"Added {ClockFormat.Spoken(seconds)}. {ClockFormat.Spoken(timer?.Remaining(store.Now) ?? seconds)} left", answer: true);
            }
            default:
                return Done(Status(timers, label, store.Now), answer: true);
        }
    }

    private static Task<ToolResult> Done(string text, bool answer = false) => Task.FromResult(new ToolResult(text, answer));

    public static string Status(IReadOnlyList<Countdown> timers, string? label, DateTime now)
    {
        var list = timers.ToList();
        if (label?.ToLowerInvariant() is { } lower)
        {
            var named = list.Where(t => t.Name.ToLowerInvariant().Contains(lower)).ToList();
            if (named.Count > 0) list = named;
        }
        if (list.Count == 0) return "You don't have any timers";
        if (list.Count == 1)
            return $"{ClockFormat.Spoken(list[0].Remaining(now))} left on your {list[0].Name}" + (list[0].IsPaused ? ". It's paused" : "");
        return string.Join(". ", list.Select(t => $"{t.Name}: {ClockFormat.Spoken(t.Remaining(now))}{(t.IsPaused ? ", paused" : "")}"));
    }

    /// <summary>
    /// The model's duration when the user said that length, even in other words
    /// ("90 minutes" for "an hour and a half"). Otherwise the one duration the
    /// user said; with several there is no guessing which.
    /// </summary>
    private static double? Duration(string? spoken, ToolContext context)
    {
        var model = spoken is null ? null : SpokenDuration.Parse(spoken);
        if (context.Transcript is null) return model;
        var said = SpokenDuration.All(context.Transcript);
        if (model is { } m && said.Contains(m)) return m;
        return said.Count == 1 ? said[0] : null;
    }

    /// <summary>
    /// "set a timer for 10 minutes", "10 minute pasta timer", "how long is
    /// left", "cancel all timers", "add 5 minutes to the timer".
    /// </summary>
    protected override TimerArguments? Direct(DirectCommand command)
    {
        if (ClockPhrases.ForeignVerbs.Contains(command.Verb)) return null;
        var words = new SpokenWords(command.Original);
        var asksLeft = words.ContainsAny(["left", "remaining"]) && (command.Text.StartsWith("how", StringComparison.Ordinal) || command.Text.StartsWith("what", StringComparison.Ordinal));
        // "time 5 minutes for my tea".
        var timeVerb = command.Text.StartsWith("time ", StringComparison.Ordinal) && SpokenDuration.Find(words) is not null;
        if (!(words.ContainsAny(["timer", "timers", "countdown"]) || asksLeft || timeVerb)) return null;
        if (words.ContainsAny(["stopwatch", "alarm", "alarms", "remind", "reminder"])) return null;
        var found = SpokenDuration.Find(words);
        var taken = found is { } f ? Enumerable.Range(f.Start, f.End - f.Start).ToHashSet() : [];
        var duration = found is { } d ? words.Text(Enumerable.Range(d.Start, d.End - d.Start)) : null;
        var label = ClockPhrases.Label(words, "timer", taken);
        if (words.ContainsAny(["all", "every"])) label = "all";

        var first = words.Count > 0 ? words.Lower[0] : "";
        string action;
        if (words.ContainsAny(ClockPhrases.CancelWords)) action = "cancel";
        else if (words.ContainsAny(ClockPhrases.PauseWords)) action = "pause";
        else if (words.ContainsAny(ClockPhrases.ResumeWords)) action = "resume";
        else if (words.ContainsAny(["add", "extend"]) || (first == "another" && found is not null && words.Contains("to"))) action = "add";
        else if (words.ContainsAny(ClockPhrases.AskWords) || asksLeft) action = "status";
        else action = "start";
        return new TimerArguments(action, duration, label);
    }
}

// Alarm

public sealed record AlarmArguments(string Action, string? Time = null, string? Label = null, string? Repeats = null);

/// <summary>Alarms at a time of day. They ring like timers, and can be snoozed.</summary>
public sealed class AlarmTool(ClockStore store) : AssistantTool<AlarmArguments>
{
    public override string Name => "alarm";
    public override string Title => "Alarm";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "alarm", "alarms", "wake", "snooze" };

    public override string Description =>
        "Alarms at a time of day. \"set an alarm for 7 am\" → set, time \"7 am\". \"wake me up tomorrow at 6:30\" → set, " +
        "time \"tomorrow at 6:30\". \"what alarms do I have\" → list. \"cancel my 7 am alarm\" → cancel, time \"7 am\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("action", "What to do", Choices: ["set", "cancel", "list"]),
        new("time", "When, as the user said it, e.g. \"7 am\" or \"tomorrow at 6:30\"", Required: false),
        new("label", "The alarm's name, only if the user gave one, e.g. \"gym\"", Required: false),
        new("repeats", "Only if the alarm repeats, as the user said it, e.g. \"every weekday\"", Required: false),
    ];

    protected override string Target(AlarmArguments a) => string.Join(" · ", new[] { a.Label, a.Time }.OfType<string>());

    protected override Task<ToolResult> ExecuteAsync(AlarmArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var label = ClockPhrases.Grounded(arguments.Label, context) is { } l && l != "all" ? l : null;
        var alarms = store.Snapshot.Alarms;
        var now = store.Now;
        switch (arguments.Action)
        {
            case "set":
            {
                if (When(arguments.Time, now, context) is not { } found || !found.When.HasTime)
                    throw new ToolException("For what time? Say “set an alarm for 7 am”");
                if (RepeatDays(arguments.Repeats, context) is { } days)
                {
                    // "every weekday at 7" is 7 am unless said otherwise: read
                    // the hour as said, as with "tomorrow at 7".
                    var clock = SpokenWhen.Parse("tomorrow " + found.Text, now)?.Date ?? found.When.Date;
                    var first = AlarmRepeat.Next(now, clock, days) ?? throw new ToolException("I couldn't work out when that alarm rings");
                    var repeating = store.AddAlarm(first, label, days);
                    return Done($"Alarm set for {ClockFormat.Time(first)} {AlarmRepeat.Describe(days)}{Named(repeating)}");
                }
                if (found.When.Date <= now || found.When.Date - now > ClockStore.Limit) throw new ToolException("Alarms can be set for up to a week ahead");
                var alarm = store.AddAlarm(found.When.Date, label);
                return Done($"Alarm set for {ClockFormat.When(alarm.FireDate, now)}{Named(alarm)}");
            }
            case "cancel":
            {
                IReadOnlyList<Countdown> candidates = alarms;
                // "cancel my 7 am alarm": the alarm at that time.
                if (When(arguments.Time, now, context) is { When.HasTime: true } at)
                {
                    candidates = alarms.Where(a => a.FireDate.Hour == at.When.Date.Hour && a.FireDate.Minute == at.When.Date.Minute).ToList();
                    if (candidates.Count == 0) throw new ToolException($"There's no alarm at {ClockFormat.Time(at.When.Date)}");
                }
                var chosen = ClockPhrases.Select(candidates, label, ClockPhrases.SaidAll(context), "alarm");
                store.Remove(chosen.Select(c => c.Id));
                return Done(chosen.Count == 1 ? $"Cancelled the alarm for {ClockFormat.When(chosen[0].FireDate, now)}" : $"Cancelled {chosen.Count} alarms");
            }
            default:
            {
                if (alarms.Count == 0) return Done("You don't have any alarms", answer: true);
                var list = alarms.OrderBy(a => a.FireDate).Select(a =>
                    (a.RepeatDays is { } days ? $"{ClockFormat.Time(a.FireDate)} {AlarmRepeat.Describe(days)}" : ClockFormat.When(a.FireDate, now))
                    + (a.Label is { } name ? $" for {name}" : "") + (a.IsEnabled ? "" : " (off)")).ToList();
                return Done(list.Count == 1 ? $"You have an alarm {At(list[0])}" : $"You have {list.Count} alarms: {string.Join(", ", list)}", answer: true);
            }
        }
    }

    private static Task<ToolResult> Done(string text, bool answer = false) => Task.FromResult(new ToolResult(text, answer));

    private static string Named(Countdown alarm) => alarm.Label is { } label ? $" ({label})" : "";

    /// <summary>"at 7:00 AM", "tomorrow at 7:00 AM".</summary>
    private static string At(string when) => when.Length > 0 && char.IsDigit(when[0]) ? $"at {when}" : when;

    /// <summary>
    /// The model's days when the user said them, even in other words. A command
    /// with one alarm uses the days it said; with several ("7 on weekdays and 8
    /// on weekends") the model's split is the only one there is, so it must check out.
    /// </summary>
    private static HashSet<int>? RepeatDays(string? spoken, ToolContext context)
    {
        var model = spoken is null ? null : AlarmRepeat.Parse(spoken);
        if (context.Transcript is null) return model;
        if (AlarmRepeat.Parse(context.Transcript) is not { } said) return null;
        if (model is not null && model.IsSubsetOf(said)) return model;
        if (SpokenWhen.ClockTimes(context.Transcript).Count > 1)
            throw new ToolException("I couldn't tell which days each alarm is for; set them one at a time");
        return said;
    }

    /// <summary>The model's time when the user said it, with the text to read it from; otherwise the one time the user said.</summary>
    private static (SpokenWhen When, string Text)? When(string? spoken, DateTime now, ToolContext context)
    {
        if (context.Transcript is not { } transcript)
            return spoken is not null && SpokenWhen.Parse(spoken, now) is { } parsed ? (parsed, spoken) : null;
        if (spoken is not null && SpokenWhen.Parse(spoken, now) is { } when
            && (Grounding.Mentions(spoken, transcript) || (when.HasTime && SpokenWhen.WasSaid(when.Date, transcript))))
            return (when, spoken);
        if (SpokenWhen.ClockTimes(transcript).Count > 1) return null;
        return SpokenWhen.Parse(transcript, now) is { } said ? (said, transcript) : null;
    }

    /// <summary>
    /// "set an alarm for 7 am", "wake me up at 6:30 tomorrow", "alarm for 7
    /// called gym", "cancel my 7 am alarm", "what alarms do I have".
    /// </summary>
    protected override AlarmArguments? Direct(DirectCommand command)
    {
        if (ClockPhrases.ForeignVerbs.Contains(command.Verb)) return null;
        var words = new SpokenWords(command.Original);
        var wake = command.Text.StartsWith("wake me", StringComparison.Ordinal);
        if (!(words.ContainsAny(["alarm", "alarms"]) || wake)) return null;
        if (words.ContainsAny(["timer", "timers", "stopwatch", "remind", "reminder"])) return null;
        // "every Monday at 7": the days repeat, so only the time is parsed.
        var repeating = AlarmRepeat.Find(words);
        IReadOnlySet<int>? consumed = repeating is { } r
            ? SpokenWhen.Time(words, 0, r.Consumed)?.Consumed
            : SpokenWhen.Find(words, store.Now)?.Consumed;
        var taken = new HashSet<int>(consumed ?? new HashSet<int>());
        if (repeating is { } rr) taken.UnionWith(rr.Consumed);
        var time = consumed is null ? null : words.Text(consumed);
        var label = ClockPhrases.Label(words, "alarm", taken);
        if (words.ContainsAny(["all", "every"])) label = "all";

        string action;
        if (words.ContainsAny(ClockPhrases.CancelWords) || command.Text.Contains("turn off", StringComparison.Ordinal)) action = "cancel";
        else if (!wake && words.ContainsAny(["what", "whats", "which", "when", "list", "show", "any", "check", "many"])) action = "list";
        else action = "set";
        return new AlarmArguments(action, time, label, repeating is { } rep ? words.Text(rep.Consumed) : null);
    }
}

// Stopwatch

public sealed record StopwatchArguments(string Action);

public sealed class StopwatchTool(ClockStore store) : AssistantTool<StopwatchArguments>
{
    public override string Name => "stopwatch";
    public override string Title => "Stopwatch";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "stopwatch", "lap", "laps" };

    public override string Description =>
        "A stopwatch. \"start a stopwatch\" → start. \"stop the stopwatch\" → stop. \"how long has the stopwatch been running\" → status. " +
        "\"lap\" → lap.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
        [new("action", "What to do", Choices: ["start", "stop", "resume", "reset", "restart", "lap", "status"])];

    protected override string Target(StopwatchArguments a) => a.Action;

    protected override Task<ToolResult> ExecuteAsync(StopwatchArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var now = store.Now;
        var current = store.Snapshot.Stopwatch;
        switch (arguments.Action)
        {
            case "start" or "restart":
                if (arguments.Action == "start" && current.IsRunning)
                    return Done($"The stopwatch is already running: {ClockFormat.Spoken(current.Elapsed(now))}", answer: true);
                // A new start times from zero; "resume" continues.
                store.UpdateStopwatch(w =>
                {
                    w.StartedAt = now;
                    w.Accumulated = 0;
                    w.Laps.Clear();
                });
                return Done(arguments.Action == "restart" ? "Stopwatch restarted" : "Stopwatch started");
            case "stop":
                if (!current.IsRunning)
                {
                    if (current.IsIdle) throw new ToolException("The stopwatch isn't running");
                    return Done($"The stopwatch is stopped at {ClockFormat.Spoken(current.Elapsed(now))}", answer: true);
                }
                var stopped = store.UpdateStopwatch(w =>
                {
                    w.Accumulated = w.Elapsed(now);
                    w.StartedAt = null;
                });
                return Done($"Stopwatch stopped at {Reading(stopped.Elapsed(now))}", answer: true);
            case "resume":
                if (current.IsRunning) return Done("The stopwatch is already running");
                store.UpdateStopwatch(w => w.StartedAt = now);
                return Done(current.IsIdle ? "Stopwatch started" : $"Stopwatch resumed at {ClockFormat.Stopwatch(current.Accumulated)}");
            case "reset":
                store.ResetStopwatch();
                return Done("Stopwatch reset");
            case "lap":
                if (!current.IsRunning) throw new ToolException("The stopwatch isn't running");
                var watch = store.UpdateStopwatch(w => w.Laps.Add(w.Elapsed(now) - w.Laps.Sum()));
                return Done($"Lap {watch.Laps.Count}: {Reading(watch.Laps[^1])}", answer: true);
            default:
                if (current.IsIdle) return Done("The stopwatch isn't running", answer: true);
                return Done($"{Reading(current.Elapsed(now))}{(current.IsRunning ? "" : ", stopped")}", answer: true);
        }
    }

    private static Task<ToolResult> Done(string text, bool answer = false) => Task.FromResult(new ToolResult(text, answer));

    /// <summary>"1 minute 23 seconds", with tenths under a minute: "12.4 seconds".</summary>
    public static string Reading(double elapsed) =>
        elapsed < 60 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} seconds", elapsed) : ClockFormat.Spoken(elapsed);

    /// <summary>"start a stopwatch", "stop the stopwatch", "lap", "reset the stopwatch".</summary>
    protected override StopwatchArguments? Direct(DirectCommand command)
    {
        if (ClockPhrases.ForeignVerbs.Contains(command.Verb)) return null;
        var words = new SpokenWords(command.Original);
        var text = words.Joined;
        if (text is "lap" or "split") return new StopwatchArguments("lap");
        if (!words.Contains("stopwatch")) return null;
        var rest = words.Lower.Where(w => w != "stopwatch").ToHashSet();
        string action;
        if (rest.Contains("restart") || text.Contains("start over", StringComparison.Ordinal) || text.Contains("start again", StringComparison.Ordinal)) action = "restart";
        else if (rest.Overlaps(["reset", "clear", "zero"])) action = "reset";
        else if (rest.Overlaps(["lap", "split"])) action = "lap";
        else if (rest.Overlaps(["stop", "pause", "end", "halt", "finish"])) action = "stop";
        else if (rest.Overlaps(["resume", "continue", "unpause"])) action = "resume";
        else if (rest.Overlaps(["how", "what", "whats", "check", "status", "time", "long", "reading"])) action = "status";
        else action = "start";
        return new StopwatchArguments(action);
    }
}
