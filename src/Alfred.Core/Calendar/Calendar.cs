using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alfred.Core;

/// <summary>One event, from whichever calendar is connected. Times are local.</summary>
public sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool IsAllDay);

public interface ICalendarSource
{
    Task<IReadOnlyList<CalendarEvent>> EventsAsync(DateTime start, DateTime end, CancellationToken cancellationToken);
}

/// <summary>Where "remind me to …" goes.</summary>
public interface IReminderSink
{
    /// <summary>"Microsoft To Do", "Google Tasks" or "Alfred".</summary>
    string Name { get; }
    Task AddAsync(string title, SpokenWhen? when, CancellationToken cancellationToken);
}

/// <summary>
/// The connected accounts: Outlook (Microsoft Graph) or Google, one at a
/// time (spec §8). The calendar is read-only; reminders are written to the
/// same account's task list. With neither connected, reminders ring in Alfred.
/// </summary>
public sealed class Accounts : ICalendarSource, IReminderSink
{
    private const string GoogleSecretKey = "google.clientSecret";
    private readonly SettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly ClockStore _clock;

    public OAuthSession Outlook { get; }
    public OAuthSession Google { get; }

    public Accounts(SettingsStore settings, ISecretStore secrets, ILauncher launcher, ClockStore clock)
    {
        _settings = settings;
        _secrets = secrets;
        _clock = clock;
        Outlook = new OAuthSession(
            new("Outlook", "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                "offline_access Calendars.Read Tasks.ReadWrite", "outlook.token", "localhost",
                new Dictionary<string, string>(), RefreshSendsScope: true),
            () => settings.Current.OutlookClientId is { Length: > 0 } id ? (id.Trim(), null) : null,
            secrets, launcher);
        Google = new OAuthSession(
            new("Google", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
                "https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/tasks", "google.token", "127.0.0.1",
                new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" }, RefreshSendsScope: false),
            () => settings.Current.GoogleClientId is { Length: > 0 } id && secrets.Get(GoogleSecretKey) is { Length: > 0 } secret
                ? (id.Trim(), secret) : null,
            secrets, launcher);
    }

    public void SetGoogleSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) _secrets.Delete(GoogleSecretKey);
        else _secrets.Set(GoogleSecretKey, secret.Trim());
    }

    public bool HasGoogleSecret => _secrets.Get(GoogleSecretKey) is not null;

    public OAuthSession? Session(CalendarProvider provider) => provider switch
    {
        CalendarProvider.Outlook => Outlook,
        CalendarProvider.Google => Google,
        _ => null,
    };

    private OAuthSession? Connected => Session(_settings.Current.Calendar) is { IsSignedIn: true } session ? session : null;

    /// <summary>Connects one provider and disconnects the other: one at a time.</summary>
    public async Task ConnectAsync(CalendarProvider provider, CancellationToken cancellationToken)
    {
        var session = Session(provider) ?? throw new ToolException("Choose Outlook or Google first");
        await session.SignInAsync(cancellationToken);
        foreach (var other in new[] { CalendarProvider.Outlook, CalendarProvider.Google }.Where(p => p != provider))
            Session(other)!.SignOut();
        _settings.Update(s => s.Calendar = provider);
    }

    public void Disconnect()
    {
        Outlook.SignOut();
        Google.SignOut();
        _settings.Update(s => s.Calendar = CalendarProvider.None);
    }

    // Calendar

    public async Task<IReadOnlyList<CalendarEvent>> EventsAsync(DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        var session = Connected ?? throw new ToolException("No calendar is connected; connect Outlook or Google under Settings › Calendar");
        return session == Outlook ? await OutlookEvents(start, end, cancellationToken) : await GoogleEvents(start, end, cancellationToken);
    }

    private async Task<IReadOnlyList<CalendarEvent>> OutlookEvents(DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        var url = "https://graph.microsoft.com/v1.0/me/calendarView" +
                  $"?startDateTime={Uri.EscapeDataString(Iso(start))}&endDateTime={Uri.EscapeDataString(Iso(end))}" +
                  "&$select=subject,start,end,isAllDay,isCancelled&$orderby=start/dateTime&$top=100";
        var body = await Outlook.GetAsync(url, new Dictionary<string, string> { ["Prefer"] = "outlook.timezone=\"UTC\"" }, cancellationToken);
        return ParseOutlook(body);
    }

    /// <summary>Graph's calendarView: timed events in UTC (as asked), all-day ones as whole local days.</summary>
    public static IReadOnlyList<CalendarEvent> ParseOutlook(string json)
    {
        var events = new List<CalendarEvent>();
        foreach (var item in JsonNode.Parse(json)?["value"]?.AsArray() ?? [])
        {
            if (item is null || item["isCancelled"]?.GetValue<bool>() == true) continue;
            var allDay = item["isAllDay"]?.GetValue<bool>() ?? false;
            var start = item["start"]?["dateTime"]?.GetValue<string>();
            var end = item["end"]?["dateTime"]?.GetValue<string>();
            if (start is null || end is null) continue;
            DateTime? Parse(string text) => allDay ? LocalDay(text[..Math.Min(10, text.Length)]) : UtcDateTime(text);
            if (Parse(start) is { } s && Parse(end) is { } e)
                events.Add(new CalendarEvent(item["subject"]?.GetValue<string>() ?? "Untitled", s, e, allDay));
        }
        return events;
    }

    private async Task<IReadOnlyList<CalendarEvent>> GoogleEvents(DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        var list = await Google.GetAsync("https://www.googleapis.com/calendar/v3/users/me/calendarList?minAccessRole=reader", cancellationToken: cancellationToken);
        var calendars = SelectedGoogleCalendars(list);
        var pages = await Task.WhenAll(calendars.Select(id => Google.GetAsync(
            $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(id)}/events" +
            $"?timeMin={Uri.EscapeDataString(Iso(start))}&timeMax={Uri.EscapeDataString(Iso(end))}&singleEvents=true&orderBy=startTime&maxResults=100",
            cancellationToken: cancellationToken)));
        return pages.SelectMany(ParseGoogle).ToList();
    }

    /// <summary>The calendars ticked in Google Calendar.</summary>
    public static IReadOnlyList<string> SelectedGoogleCalendars(string json) =>
        (JsonNode.Parse(json)?["items"]?.AsArray() ?? [])
            .Where(i => i?["selected"]?.GetValue<bool>() == true)
            .Select(i => i!["id"]!.GetValue<string>()).ToList();

    public static IReadOnlyList<CalendarEvent> ParseGoogle(string json)
    {
        var events = new List<CalendarEvent>();
        foreach (var item in JsonNode.Parse(json)?["items"]?.AsArray() ?? [])
        {
            if (item is null || item["status"]?.GetValue<string>() == "cancelled") continue;
            var title = item["summary"]?.GetValue<string>() ?? "Untitled";
            if (item["start"]?["dateTime"]?.GetValue<string>() is { } s && item["end"]?["dateTime"]?.GetValue<string>() is { } e
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                && DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                events.Add(new CalendarEvent(title, start.LocalDateTime, end.LocalDateTime, false));
            else if (item["start"]?["date"]?.GetValue<string>() is { } sd && item["end"]?["date"]?.GetValue<string>() is { } ed
                     && LocalDay(sd) is { } startDay && LocalDay(ed) is { } endDay)
                events.Add(new CalendarEvent(title, startDay, endDay, true));
        }
        return events;
    }

    // Reminders

    public string Name => Connected == Outlook ? "Microsoft To Do" : Connected == Google ? "Google Tasks" : "Alfred";

    public async Task AddAsync(string title, SpokenWhen? when, CancellationToken cancellationToken)
    {
        var session = Connected;
        if (session is null)
        {
            // In the app: rings like an alarm, with a toast as the backup.
            // A day with no time rings at 9 in the morning.
            var date = when is null ? (DateTime?)null : when.HasTime ? when.Date : when.Date.Date.AddHours(9);
            if (date is null) throw new ToolException("When should I remind you? Say “remind me at 6 to …”, or connect Outlook or Google for undated reminders");
            _clock.AddReminder(date.Value, title);
            return;
        }
        if (session == Outlook)
        {
            var lists = JsonNode.Parse(await Outlook.GetAsync("https://graph.microsoft.com/v1.0/me/todo/lists", cancellationToken: cancellationToken));
            var list = lists?["value"]?.AsArray().FirstOrDefault(l => l?["wellknownListName"]?.GetValue<string>() == "defaultList")
                       ?? lists?["value"]?.AsArray().FirstOrDefault()
                       ?? throw new ToolException("There's no Microsoft To Do list to add to");
            var task = new JsonObject { ["title"] = title };
            if (when is not null)
            {
                var zone = TimeZoneInfo.Local.Id;
                task["dueDateTime"] = new JsonObject { ["dateTime"] = when.Date.Date.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), ["timeZone"] = zone };
                if (when.HasTime)
                {
                    task["isReminderOn"] = true;
                    task["reminderDateTime"] = new JsonObject { ["dateTime"] = when.Date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), ["timeZone"] = zone };
                }
            }
            var (status, _) = await Outlook.SendAsync(HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(list["id"]!.GetValue<string>())}/tasks",
                task.ToJsonString(), cancellationToken: cancellationToken);
            if (status != HttpStatusCode.Created) throw new ToolException($"Microsoft To Do didn't take the reminder ({(int)status})");
            return;
        }
        // Google Tasks keeps a due date only, never a time: a timed reminder also rings here.
        var body = new JsonObject { ["title"] = title };
        if (when is not null) body["due"] = when.Date.Date.ToString("yyyy-MM-dd'T'00:00:00.000'Z'", CultureInfo.InvariantCulture);
        var (googleStatus, _) = await Google.SendAsync(HttpMethod.Post, "https://tasks.googleapis.com/tasks/v1/lists/@default/tasks",
            body.ToJsonString(), cancellationToken: cancellationToken);
        if (googleStatus != HttpStatusCode.OK) throw new ToolException($"Google Tasks didn't take the reminder ({(int)googleStatus})");
        if (when is { HasTime: true }) _clock.AddReminder(when.Date, title);
    }

    // Dates

    private static string Iso(DateTime local) => local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>"2026-09-26T09:00:00.0000000", in UTC, as local time.</summary>
    public static DateTime? UtcDateTime(string text) =>
        DateTime.TryParseExact(text[..Math.Min(19, text.Length)], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : null;

    /// <summary>"2026-09-26" as the start of that day here.</summary>
    public static DateTime? LocalDay(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day) ? day : null;
}

public sealed record CalendarArguments(string Action, string? When = null);

/// <summary>Reads the connected calendar aloud: a day's events, the next event, or whether a time is free.</summary>
public sealed class CalendarTool(ICalendarSource source) : AssistantTool<CalendarArguments>
{
    public override string Name => "calendar";
    public override string Title => "Calendar";
    public override bool RequiresNetwork => true;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        "calendar", "schedule", "agenda", "meeting", "meetings", "event", "events", "appointment", "appointments", "free", "busy",
    };

    public override string Description =>
        "Read the user's calendar. \"what's on my calendar tomorrow\" → agenda, when \"tomorrow\". " +
        "\"when is my next meeting\" → next. \"am I free at 3\" → free, when \"at 3\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("action", "What the user asked", Choices: ["agenda", "next", "free"]),
        new("when", "Which day or time, as the user said it, e.g. \"tomorrow\", \"on Friday\", \"at 3\"", Required: false),
    ];

    protected override string Target(CalendarArguments a) => a.When ?? (a.Action == "next" ? "Next event" : "Today");

    protected override async Task<ToolResult> ExecuteAsync(CalendarArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var when = (ClockPhrases.Grounded(arguments.When, context) is { } spoken ? SpokenWhen.Parse(spoken, now) : null)
                   ?? (context.Transcript is { } t ? SpokenWhen.Parse(t, now) : null);
        switch (arguments.Action)
        {
            case "next":
            {
                var events = await source.EventsAsync(now, now.AddDays(14), cancellationToken);
                var next = events.Where(e => !e.IsAllDay && e.Start >= now).MinBy(e => e.Start);
                return next is null
                    ? new ToolResult("Nothing on your calendar in the next two weeks", IsAnswer: true)
                    : new ToolResult($"Your next event is {next.Title}, {ClockFormat.When(next.Start, now)}", IsAnswer: true);
            }
            case "free":
            {
                if (when is not { HasTime: true }) throw new ToolException("Free when? Say “am I free at 3”");
                var slotEnd = when.Date.AddMinutes(30);
                var events = await source.EventsAsync(when.Date.AddDays(-1), slotEnd, cancellationToken);
                var clashes = events.Where(e => !e.IsAllDay && e.Start < slotEnd && e.End > when.Date).Select(e => e.Title).ToList();
                var time = At(ClockFormat.When(when.Date, now));
                return new ToolResult(clashes.Count == 0 ? $"You're free {time}" : $"You have {List(clashes)} {time}", IsAnswer: true);
            }
            default:
            {
                var day = (when?.Date ?? now).Date;
                var events = (await source.EventsAsync(day, day.AddDays(1), cancellationToken))
                    .OrderBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.Start).ToList();
                return new ToolResult(Agenda(events, day, now), IsAnswer: true);
            }
        }
    }

    /// <summary>"You have 3 events today: Standup at 9:30 AM, …". At most five named.</summary>
    public static string Agenda(IReadOnlyList<CalendarEvent> events, DateTime day, DateTime now)
    {
        var name = ClockFormat.When(day, now, hasTime: false);
        var dayName = name is "today" or "tomorrow" ? name : $"on {name}";
        if (events.Count == 0) return $"Nothing on your calendar {dayName}";
        var described = events.Take(5).Select(e => e.IsAllDay ? $"{e.Title}, all day" : $"{e.Title} at {ClockFormat.Time(e.Start)}");
        var more = events.Count > 5 ? $", and {events.Count - 5} more" : "";
        return $"You have {(events.Count == 1 ? "1 event" : $"{events.Count} events")} {dayName}: {string.Join(", ", described)}{more}";
    }

    private static string At(string time) => time.Length > 0 && char.IsDigit(time[0]) ? $"at {time}" : time;

    private static string List(List<string> titles) => titles.Count == 1 ? titles[0] : string.Join(", ", titles[..^1]) + " and " + titles[^1];

    private static readonly HashSet<string> Nouns =
        ["calendar", "schedule", "agenda", "meeting", "meetings", "event", "events", "appointment", "appointments", "plans"];

    /// <summary>"what's on my calendar tomorrow", "what's my next meeting", "am I free at 3".</summary>
    protected override CalendarArguments? Direct(DirectCommand command)
    {
        if (ClockPhrases.ForeignVerbs.Contains(command.Verb)) return null;
        var words = new SpokenWords(command.Original);
        var text = command.Text;
        var asksFree = new[] { "am i free", "am i busy", "do i have anything", "is my calendar free", "have i got anything" }
            .Any(p => text.StartsWith(p, StringComparison.Ordinal));
        if (!asksFree && !words.ContainsAny(Nouns)) return null;
        // Something to do, not a question about the calendar.
        if (!asksFree && (words.ContainsAny(["add", "create", "book", "delete", "cancel", "move", "remind", "reminder"])
                          || text.Contains("schedule a", StringComparison.Ordinal))) return null;
        var first = words.Count > 0 ? words.Lower[0] : "";
        if (!asksFree && first is not ("what" or "whats" or "when" or "whens" or "do" or "have" or "any" or "is" or "show" or "read" or "tell" or "how")
            && !text.StartsWith("my ", StringComparison.Ordinal)) return null;
        var when = SpokenWhen.Find(words, DateTime.Now) is { } found ? words.Text(found.Consumed) : null;
        if (asksFree) return new CalendarArguments("free", when);
        return words.ContainsAny(["next", "upcoming"]) ? new CalendarArguments("next") : new CalendarArguments("agenda", when);
    }
}

public sealed record ReminderArguments(string Task, string? When = null);

/// <summary>Adds a reminder: Microsoft To Do or Google Tasks when connected, otherwise Alfred rings it.</summary>
public sealed class ReminderTool(ClockStore clock, IReminderSink sink) : AssistantTool<ReminderArguments>
{
    public override string Name => "reminder";
    public override string Title => "Reminder";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "remind", "reminder", "reminders", "forget", "remember" };

    public override string Description =>
        "Add a reminder. \"remind me to call Mum at 6\" → task \"call Mum\", when \"at 6\". " +
        "\"remind me tomorrow to buy milk\" → task \"buy milk\", when \"tomorrow\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("task", "What to be reminded of, in the user's words, e.g. \"call Mum\""),
        new("when", "When, as the user said it, e.g. \"at 6\", \"tomorrow\" or \"in 20 minutes\"", Required: false),
    ];

    protected override string Target(ReminderArguments a) => string.Join(" · ", new[] { a.Task, a.When }.OfType<string>());

    protected override async Task<ToolResult> ExecuteAsync(ReminderArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var task = arguments.Task.Trim();
        // Every word must have been said; the time may sit in the middle.
        if (context.Transcript is { } transcript
            && (task.Length == 0 || !new SpokenWords(task).Lower.ToHashSet().IsSubsetOf(new SpokenWords(transcript).Lower)))
            throw new ToolException("What should I remind you about?");
        var now = clock.Now;
        var when = ClockPhrases.Grounded(arguments.When, context) is { } spoken ? SpokenWhen.Parse(spoken, now) : null;
        if (when is { HasTime: true } && when.Date <= now) throw new ToolException("That time has already passed");
        var title = ClockAlert.Capitalised(task);
        await sink.AddAsync(title, when, cancellationToken);
        if (when is null) return $"Added “{title}” to {sink.Name}";
        return $"I'll remind you to {task} {Phrase(when, now)}";
    }

    /// <summary>"at 6:00 PM", "tomorrow at 9:00 AM", "tomorrow".</summary>
    public static string Phrase(SpokenWhen when, DateTime now)
    {
        var text = ClockFormat.When(when.Date, now, when.HasTime);
        if (text.Length > 0 && char.IsDigit(text[0])) return $"at {text}";
        return when.HasTime || text is "today" or "tomorrow" ? text : $"on {text}";
    }

    private static readonly string[] Starts =
    [
        "remind me", "set a reminder", "set reminder", "add a reminder", "create a reminder", "make a reminder", "new reminder", "reminder",
        "don t let me forget", "do not let me forget", "remember to", "don t forget to",
    ];

    /// <summary>"remind me to call Mum at 6", "remind me in 20 minutes to check the oven", "set a reminder to …".</summary>
    protected override ReminderArguments? Direct(DirectCommand command)
    {
        var start = Starts.FirstOrDefault(s => command.Text == s || command.Text.StartsWith(s + " ", StringComparison.Ordinal));
        if (start is null) return null;
        var words = new SpokenWords(command.Original);
        // Skip the opening phrase's words in the original text, letter by
        // letter: "don't" is "don t" in the command but "dont" here.
        // It may start a few words in ("Please remind me…", "Hey, can you remind me…").
        var opening = start.Replace(" ", "", StringComparison.Ordinal);
        var index = -1;
        for (var first = 0; first < Math.Min(words.Count, 5) && index < 0; first++)
        {
            var consumed = "";
            var at = first;
            while (at < words.Count && consumed.Length < opening.Length)
            {
                var next = consumed + new string(words.Lower[at].Where(char.IsLetterOrDigit).ToArray());
                if (!opening.StartsWith(next, StringComparison.Ordinal)) break;
                consumed = next;
                at++;
            }
            if (consumed == opening) index = at;
        }
        if (index < 0) return null;
        var when = SpokenWhen.Find(words, clock.Now, index);
        var taken = when?.Consumed ?? new HashSet<int>();
        var rest = Enumerable.Range(index, Math.Max(0, words.Count - index)).Where(i => !taken.Contains(i)).ToList();
        // "to call Mum", "about the meeting", "that I need to call Mum".
        while (rest.Count > 0 && words.Lower[rest[0]] is "to" or "that" or "about" or "please") rest.RemoveAt(0);
        if (rest.Count >= 3 && words.Lower[rest[0]] == "i" && words.Lower[rest[1]] is "need" or "have" or "want" && words.Lower[rest[2]] == "to")
            rest.RemoveRange(0, 3);
        while (rest.Count > 0 && words.Lower[rest[^1]] is "to" or "at" or "on" or "in" or "by" or "please") rest.RemoveAt(rest.Count - 1);
        if (rest.Count == 0) return null;
        return new ReminderArguments(words.Text(rest), when is null ? null : words.Text(when.Consumed));
    }
}
