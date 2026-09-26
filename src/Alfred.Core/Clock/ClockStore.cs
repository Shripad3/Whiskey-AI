using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alfred.Core;

public enum CountdownKind { Timer, Alarm, Reminder }

/// <summary>A timer or an alarm: something that rings at a moment.</summary>
public sealed record Countdown
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public CountdownKind Kind { get; init; }
    public string? Label { get; set; }
    /// <summary>When it rings, while running (local time).</summary>
    public DateTime FireDate { get; set; }
    /// <summary>A timer's length as set (plus any time added), in seconds.</summary>
    public double Duration { get; set; }
    /// <summary>Set while a timer is paused: the seconds it had left.</summary>
    public double? PausedRemaining { get; set; }
    /// <summary>A repeating alarm's days, 1 = Sunday … 7 = Saturday.</summary>
    public HashSet<int>? RepeatDays { get; set; }
    /// <summary>An alarm switched off in Settings: kept, but never rings.</summary>
    public bool? Disabled { get; set; }

    [JsonIgnore] public bool IsPaused => PausedRemaining is not null;
    [JsonIgnore] public bool IsEnabled => Disabled != true;
    /// <summary>Counting down to ring: not paused, not switched off.</summary>
    [JsonIgnore] public bool IsArmed => !IsPaused && IsEnabled;

    public double Remaining(DateTime now) => PausedRemaining ?? Math.Max(0, (FireDate - now).TotalSeconds);

    /// <summary>"pasta timer", "10 minute timer", "7:00 AM alarm", "gym alarm", "reminder to call Mum".</summary>
    [JsonIgnore]
    public string Name => Kind switch
    {
        CountdownKind.Timer => (Label ?? ClockFormat.Adjective(Duration)) + " timer",
        CountdownKind.Reminder => $"reminder to {Label}",
        _ => Label is null ? $"{ClockFormat.Time(FireDate)} alarm" : $"{Label} alarm",
    };
}

public sealed record StopwatchState
{
    /// <summary>Set while running.</summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>Seconds counted before the current run.</summary>
    public double Accumulated { get; set; }
    /// <summary>Each lap's length in seconds.</summary>
    public List<double> Laps { get; set; } = [];

    [JsonIgnore] public bool IsRunning => StartedAt is not null;
    /// <summary>Stopped at zero: nothing to show.</summary>
    [JsonIgnore] public bool IsIdle => StartedAt is null && Accumulated == 0;

    public double Elapsed(DateTime now) => Accumulated + (StartedAt is { } start ? (now - start).TotalSeconds : 0);

    public StopwatchState Copy() => this with { Laps = [.. Laps] };
}

/// <summary>A timer or alarm going off, shown in the pill until stopped.</summary>
public sealed record ClockAlert
{
    public Guid Id { get; init; }
    public CountdownKind Kind { get; init; }
    public string? Label { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Said aloud: "Your pasta timer is done."</summary>
    public string Message { get; init; } = "";
    /// <summary>The Settings "Test" button: nothing to snooze or report as missed.</summary>
    public bool IsTest { get; init; }

    public bool CanSnooze => Kind != CountdownKind.Timer && !IsTest;

    public static ClockAlert From(Countdown countdown)
    {
        if (countdown.Kind == CountdownKind.Timer)
        {
            return new ClockAlert
            {
                Id = countdown.Id, Kind = countdown.Kind, Label = countdown.Label,
                Title = "Timer done", Message = $"Time's up. Your {countdown.Name} is done.",
            };
        }
        if (countdown.Kind == CountdownKind.Reminder)
        {
            return new ClockAlert
            {
                Id = countdown.Id, Kind = countdown.Kind, Label = countdown.Label,
                Title = "Reminder", Message = $"Reminder: {countdown.Label}.",
            };
        }
        var time = ClockFormat.Time(countdown.FireDate);
        return new ClockAlert
        {
            Id = countdown.Id, Kind = countdown.Kind, Label = countdown.Label,
            Title = countdown.Label is { } label ? $"Alarm: {label}" : "Alarm",
            Message = countdown.Label is { } l ? $"It's {time}. {Capitalised(l)}." : $"It's {time}.",
        };
    }

    /// <summary>How an alarm or timer will ring, from Settings.</summary>
    public static ClockAlert Test(CountdownKind kind) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, IsTest = true,
        Title = kind == CountdownKind.Alarm ? "Test alarm" : "Test timer",
        Message = kind == CountdownKind.Alarm ? "This is how your alarms will sound." : "This is how your timers will sound.",
    };

    public static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}

/// <summary>Everything the tray and the pill show.</summary>
public sealed record ClockSnapshot(IReadOnlyList<Countdown> Countdowns, StopwatchState Stopwatch)
{
    public IReadOnlyList<Countdown> Timers => Countdowns.Where(c => c.Kind == CountdownKind.Timer).ToList();
    public IReadOnlyList<Countdown> Alarms => Countdowns.Where(c => c.Kind == CountdownKind.Alarm).ToList();
    public IReadOnlyList<Countdown> Reminders => Countdowns.Where(c => c.Kind == CountdownKind.Reminder).ToList();
    public bool IsEmpty => Countdowns.Count == 0 && Stopwatch.IsIdle;
    /// <summary>Anything whose display changes every second.</summary>
    public bool IsTicking => Timers.Any(t => !t.IsPaused) || Stopwatch.IsRunning;
}

public sealed record ClockNotification(string Id, string Title, string Body, DateTime Date);

/// <summary>
/// Scheduled toasts, the backup for when the app isn't running to ring itself
/// (spec §8). Implemented by the Windows app.
/// </summary>
public interface IClockNotifier
{
    /// <summary>Replaces every scheduled clock notification with these.</summary>
    void Schedule(IReadOnlyList<ClockNotification> items);
    void DeliverNow(ClockNotification item);
}

/// <summary>
/// Timers, alarms and the stopwatch. Kept on disk, so they survive a quit or
/// crash, and rung by the app itself. A toast is scheduled a few seconds
/// after each as a backup, and replaced whenever anything changes.
/// </summary>
public sealed class ClockStore : IDisposable
{
    /// <summary>Longest timer or furthest alarm accepted.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromDays(7);
    /// <summary>The backup toast's delay after the app should have rung.</summary>
    public static readonly TimeSpan BackupDelay = TimeSpan.FromSeconds(5);
    /// <summary>Overdue by more than this at launch: the toast covered it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    public const int SnoozeMinutes = 9;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class State
    {
        public List<Countdown> Countdowns { get; set; } = [];
        public StopwatchState Stopwatch { get; set; } = new();
    }

    private readonly string? _file;
    private readonly object _lock = new();
    private readonly Func<DateTime> _clock;
    private State _state;
    private Action<ClockAlert>? _onFire;
    private Action<ClockSnapshot>? _onChange;
    private IClockNotifier? _notifier;
    private Timer? _timer;

    /// <param name="file">Null keeps everything in memory (tests).</param>
    /// <param name="clock">The local time now; tests can move it.</param>
    public ClockStore(string? file, Func<DateTime>? clock = null)
    {
        _file = file;
        _clock = clock ?? (() => DateTime.Now);
        _state = Load(file) ?? new State();
    }

    private static State? Load(string? file)
    {
        try
        {
            return file is not null && File.Exists(file) ? JsonSerializer.Deserialize<State>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // A damaged file must not stop the app; alarms start empty.
        }
    }

    public DateTime Now => _clock();

    /// <summary>Starts ringing. Call once the handlers are ready.</summary>
    public void Start(IClockNotifier? notifier, Action<ClockSnapshot> onChange, Action<ClockAlert> onFire)
    {
        lock (_lock)
        {
            _notifier = notifier;
            _onChange = onChange;
            _onFire = onFire;
            _timer ??= new Timer(_ => FireDue());
        }
        // Long overdue (the app wasn't running): the toast told the user
        // already; ringing now, hours late, would only confuse.
        var now = Now;
        Mutate(s => s.Countdowns.RemoveAll(c => c.IsArmed && c.FireDate < now - StaleAfter));
    }

    public ClockSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                var now = Now;
                return new ClockSnapshot(
                    _state.Countdowns.Select(c => c with { RepeatDays = c.RepeatDays is null ? null : [.. c.RepeatDays] })
                        .OrderBy(c => c.Remaining(now)).ToList(),
                    _state.Stopwatch.Copy());
            }
        }
    }

    // Timers and alarms

    public Countdown AddTimer(double seconds, string? label)
    {
        var timer = new Countdown { Kind = CountdownKind.Timer, Label = label, FireDate = Now.AddSeconds(seconds), Duration = seconds };
        Mutate(s => s.Countdowns.Add(timer));
        return timer;
    }

    public Countdown AddAlarm(DateTime date, string? label, HashSet<int>? repeatDays = null)
    {
        var alarm = new Countdown { Kind = CountdownKind.Alarm, Label = label, FireDate = date, RepeatDays = repeatDays };
        Mutate(s => s.Countdowns.Add(alarm));
        return alarm;
    }

    /// <summary>A reminder Alfred rings itself, when no task list is connected.</summary>
    public Countdown AddReminder(DateTime date, string text)
    {
        var reminder = new Countdown { Kind = CountdownKind.Reminder, Label = text, FireDate = date };
        Mutate(s => s.Countdowns.Add(reminder));
        return reminder;
    }

    /// <summary>A new alarm at the next hour:minute (Settings' Add Alarm).</summary>
    public Countdown AddAlarm(int hour, int minute, string? label, HashSet<int>? days)
    {
        var now = Now;
        var alarm = new Countdown
        {
            Kind = CountdownKind.Alarm, Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            FireDate = SpokenWhen.At(now, hour, minute), RepeatDays = days is { Count: > 0 } ? days : null,
        };
        alarm.FireDate = NextRing(alarm, now);
        Mutate(s => s.Countdowns.Add(alarm));
        return alarm;
    }

    /// <summary>Switches an alarm on or off without deleting it. Switching on moves it to the next time it can ring.</summary>
    public void SetEnabled(Guid id, bool enabled)
    {
        var now = Now;
        Mutate(s =>
        {
            if (s.Countdowns.Find(c => c.Id == id) is not { } alarm) return;
            alarm.Disabled = enabled ? null : true;
            if (enabled) alarm.FireDate = NextRing(alarm, now);
        });
    }

    /// <summary>Changes an alarm's time, name or days (Settings). Null or empty days: rings once.</summary>
    public void UpdateAlarm(Guid id, int hour, int minute, string? label, HashSet<int>? days)
    {
        var now = Now;
        Mutate(s =>
        {
            if (s.Countdowns.Find(c => c.Id == id) is not { } alarm) return;
            alarm.FireDate = SpokenWhen.At(alarm.FireDate, hour, minute);
            alarm.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            alarm.RepeatDays = days is { Count: > 0 } ? days : null;
            alarm.FireDate = NextRing(alarm, now);
        });
    }

    /// <summary>
    /// When an alarm next rings from now: its own time if still ahead, else the
    /// next matching day (repeating) or the same time tomorrow (once).
    /// </summary>
    public static DateTime NextRing(Countdown alarm, DateTime now)
    {
        if (alarm.RepeatDays is { } days) return AlarmRepeat.Next(now, alarm.FireDate, days) ?? alarm.FireDate;
        if (alarm.FireDate > now) return alarm.FireDate;
        var today = SpokenWhen.At(now, alarm.FireDate.Hour, alarm.FireDate.Minute);
        return today > now ? today : today.AddDays(1);
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        Mutate(s => s.Countdowns.RemoveAll(c => set.Contains(c.Id)));
    }

    public void Pause(Guid id)
    {
        var now = Now;
        Mutate(s =>
        {
            if (s.Countdowns.Find(c => c.Id == id) is { IsPaused: false } timer) timer.PausedRemaining = timer.Remaining(now);
        });
    }

    public void Resume(Guid id)
    {
        var now = Now;
        Mutate(s =>
        {
            if (s.Countdowns.Find(c => c.Id == id) is not { PausedRemaining: { } remaining } timer) return;
            timer.FireDate = now.AddSeconds(remaining);
            timer.PausedRemaining = null;
        });
    }

    public void Extend(Guid id, double seconds) => Mutate(s =>
    {
        if (s.Countdowns.Find(c => c.Id == id) is not { } timer) return;
        timer.Duration += seconds;
        if (timer.PausedRemaining is { } paused) timer.PausedRemaining = paused + seconds;
        else timer.FireDate = timer.FireDate.AddSeconds(seconds);
    });

    /// <summary>Rings again in a few minutes, as the same alarm.</summary>
    public DateTime Snooze(ClockAlert alert)
    {
        var date = Now.AddMinutes(SnoozeMinutes);
        Mutate(s => s.Countdowns.Add(new Countdown { Kind = alert.Kind, Label = alert.Label, FireDate = date }));
        return date;
    }

    /// <summary>Rang and nobody stopped it: leave a toast to find later.</summary>
    public void Missed(ClockAlert alert)
    {
        if (alert.IsTest) return;
        IClockNotifier? notifier;
        lock (_lock) notifier = _notifier;
        notifier?.DeliverNow(new ClockNotification($"missed-{alert.Id}", alert.Title, alert.Message, Now));
    }

    // Stopwatch

    /// <summary>The tray's Stop / Resume.</summary>
    public void ToggleStopwatch()
    {
        var now = Now;
        UpdateStopwatch(watch =>
        {
            if (watch.IsRunning)
            {
                watch.Accumulated = watch.Elapsed(now);
                watch.StartedAt = null;
            }
            else
            {
                watch.StartedAt = now;
            }
        });
    }

    public void ResetStopwatch() => UpdateStopwatch(w =>
    {
        w.StartedAt = null;
        w.Accumulated = 0;
        w.Laps.Clear();
    });

    public StopwatchState UpdateStopwatch(Action<StopwatchState> change)
    {
        StopwatchState result = new();
        Mutate(s =>
        {
            change(s.Stopwatch);
            result = s.Stopwatch.Copy();
        });
        return result;
    }

    // Ringing

    /// <summary>Rings whatever is due now. Public for tests and for waking from sleep.</summary>
    public void FireDue()
    {
        var now = Now;
        var due = new List<Countdown>();
        Mutate(s =>
        {
            due = s.Countdowns.Where(c => c.IsArmed && c.FireDate <= now.AddMilliseconds(50)).ToList();
            var ids = due.Select(c => c.Id).ToHashSet();
            s.Countdowns.RemoveAll(c => ids.Contains(c.Id));
            // A repeating alarm comes back for its next day.
            foreach (var alarm in due)
            {
                if (alarm.RepeatDays is not { } days || AlarmRepeat.Next(Max(now, alarm.FireDate), alarm.FireDate, days) is not { } next) continue;
                s.Countdowns.Add(alarm with { FireDate = next });
            }
        });
        Action<ClockAlert>? onFire;
        lock (_lock) onFire = _onFire;
        foreach (var countdown in due.OrderBy(c => c.FireDate)) onFire?.Invoke(ClockAlert.From(countdown));
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private void Mutate(Action<State> change)
    {
        lock (_lock) change(_state);
        Changed();
    }

    private void Changed()
    {
        var snapshot = Snapshot;
        Action<ClockSnapshot>? onChange;
        IClockNotifier? notifier;
        lock (_lock)
        {
            onChange = _onChange;
            notifier = _notifier;
        }
        Save();
        Reschedule();
        notifier?.Schedule(snapshot.Countdowns.Where(c => c.IsArmed).Select(c =>
        {
            var alert = ClockAlert.From(c);
            return new ClockNotification(c.Id.ToString(), alert.Title, alert.Message, c.FireDate + BackupDelay);
        }).ToList());
        onChange?.Invoke(snapshot);
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_state, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next change tries again.
        }
    }

    /// <summary>
    /// One timer wakes at the next countdown, checking at least every minute
    /// so sleep, wake and clock changes can't strand it.
    /// </summary>
    private void Reschedule()
    {
        lock (_lock)
        {
            if (_timer is null) return;
            var next = _state.Countdowns.Where(c => c.IsArmed).Select(c => (DateTime?)c.FireDate).Min();
            if (next is null)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            var wait = next.Value - Now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            if (wait > TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
            _timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

public enum AlertReply { Stop, Snooze }

public static class AlertReplies
{
    private static readonly HashSet<string> Stops =
    [
        "stop", "ok", "okay", "dismiss", "thanks", "thank", "off", "cancel", "enough", "quiet", "silence",
        "shut", "done", "awake", "got", "alright",
    ];

    /// <summary>What the user said over a ringing alert.</summary>
    public static AlertReply? Interpret(string transcript)
    {
        var words = new SpokenWords(transcript);
        var text = words.Joined;
        if (words.Contains("snooze") || text.Contains("few more minutes", StringComparison.Ordinal)) return AlertReply.Snooze;
        if (text is "im up" or "i m up" or "i am up") return AlertReply.Stop;
        // Short replies only: "stop", "okay thanks", "turn it off". Longer
        // ones ("turn off the lights") are new commands.
        return words.Count <= 3 && words.Lower.Any(Stops.Contains) ? AlertReply.Stop : null;
    }
}

/// <summary>Repeating alarms: "every day", "on weekdays", "every Monday and Wednesday".</summary>
public static class AlarmRepeat
{
    public static readonly IReadOnlySet<int> Weekdays = new HashSet<int> { 2, 3, 4, 5, 6 };
    private static readonly string[] Names = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>Days (1 = Sunday) and the words that said them.</summary>
    public static (HashSet<int> Days, HashSet<int> Consumed)? Find(SpokenWords words)
    {
        var w = words.Lower;
        var days = new HashSet<int>();
        var consumed = new HashSet<int>();
        for (var index = 0; index < w.Count; index++)
        {
            var word = w[index];
            var previous = index > 0 ? w[index - 1] : "";
            switch (word)
            {
                case "daily" or "everyday":
                    days.UnionWith(Enumerable.Range(1, 7));
                    consumed.Add(index);
                    break;
                case "day" when previous == "every":
                    days.UnionWith(Enumerable.Range(1, 7));
                    consumed.UnionWith([index - 1, index]);
                    break;
                case "weekdays" or "workdays":
                    days.UnionWith(Weekdays);
                    consumed.Add(index);
                    break;
                case "weekday" when previous is "every" or "on":
                    days.UnionWith(Weekdays);
                    consumed.Add(index);
                    break;
                case "weekends":
                    days.UnionWith([1, 7]);
                    consumed.Add(index);
                    break;
                case "weekend" when previous is "every" or "on" or "at":
                    days.UnionWith([1, 7]);
                    consumed.Add(index);
                    break;
                default:
                {
                    // "every Monday", "on Mondays", "Monday and Wednesday".
                    var singular = word.EndsWith('s') ? word[..^1] : word;
                    var day = Array.IndexOf(Names, singular);
                    if (day < 0) continue;
                    var plural = word != singular;
                    var every = previous == "every";
                    var listed = index > 1 && previous is "and" or "or" && days.Count > 0;
                    if (!(plural || every || listed)) continue;
                    days.Add(day + 1);
                    consumed.Add(index);
                    if (listed) consumed.Add(index - 1);
                    break;
                }
            }
            if (consumed.Contains(index) && previous is "every" or "on" or "at") consumed.Add(index - 1);
        }
        return days.Count == 0 ? null : (days, consumed);
    }

    public static HashSet<int>? Parse(string text) => Find(new SpokenWords(text))?.Days;

    public static int Weekday(DateTime date) => (int)date.DayOfWeek + 1;

    /// <summary>The first of <paramref name="days"/> after <paramref name="now"/>, at <paramref name="time"/>'s hour and minute.</summary>
    public static DateTime? Next(DateTime now, DateTime time, IReadOnlySet<int> days)
    {
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = now.AddDays(offset);
            if (!days.Contains(Weekday(day))) continue;
            var date = SpokenWhen.At(day, time.Hour, time.Minute);
            if (date > now) return date;
        }
        return null;
    }

    /// <summary>"every day", "on weekdays", "at weekends", "every Monday and Wednesday".</summary>
    public static string Describe(IReadOnlySet<int> days)
    {
        if (days.Count == 7) return "every day";
        if (days.SetEquals(Weekdays)) return "on weekdays";
        if (days.SetEquals([1, 7])) return "at weekends";
        var named = days.OrderBy(d => (d + 5) % 7).Select(d => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Names[d - 1])).ToList();
        return "every " + (named.Count > 1 ? string.Join(", ", named[..^1]) + " and " + named[^1] : named[0]);
    }
}
