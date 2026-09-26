using System.Text.Json.Nodes;
using Alfred.Core;

namespace Alfred.Core.Tests;

/// <summary>A typical Omen's Start menu.</summary>
internal sealed class FakeCatalog : IAppCatalog
{
    public static readonly string[] InstalledNames =
    [
        "Spotify", "Google Chrome", "Microsoft Edge", "Firefox", "Visual Studio Code", "Settings", "Notepad",
        "File Explorer", "Microsoft Store", "Word", "Excel", "Calculator", "Discord", "Steam", "OMEN Gaming Hub",
        "Remote Desktop Connection", "Chrome Remote Desktop",
    ];

    public List<InstalledApp> Installed { get; } = InstalledNames.Select(n => new InstalledApp(n, "id:" + n)).ToList();

    public IReadOnlyList<InstalledApp> Apps(bool refresh = false) => Installed;

    public IReadOnlyList<InstalledApp> Browsers() =>
    [
        new("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
        new("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
        new("Mozilla Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
    ];

    public InstalledApp? DefaultBrowser() => Browsers()[1];
}

/// <summary>Records what would have been started.</summary>
internal sealed class FakeLauncher : ILauncher
{
    public List<string> Launched { get; } = [];

    public Task LaunchAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        Launched.Add("app " + app.Name);
        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url, InstalledApp? browser, CancellationToken cancellationToken)
    {
        Launched.Add(browser is null ? "url " + url : $"url {url} in {browser.Name}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeSystem : ISystemControl
{
    public float Level = 0.5f;
    public bool Muted;
    public int Bright = 50;
    public List<string> Calls { get; } = [];
    public float Volume() => Level;
    public float SetVolume(float volume) { Level = Math.Clamp(volume, 0, 1); if (Level > 0) Muted = false; return Level; }
    public void SetMuted(bool muted) => Muted = muted;
    public int Brightness() => Bright;
    public void SetBrightness(int percent) => Bright = percent;
    public void Lock() => Calls.Add("lock");
    public void Sleep() => Calls.Add("sleep");
    public void OpenNotificationSettings() => Calls.Add("notifications");
}

internal sealed class FakeMedia : IMediaControl
{
    public bool Installed = true;
    public bool Running = true;
    public PlaybackState Playing = PlaybackState.Paused;
    public List<string> Calls { get; } = [];
    public bool IsSpotifyInstalled => Installed;
    public bool IsSpotifyRunning => Running;
    public Task LaunchSpotifyAsync(CancellationToken cancellationToken) { Running = true; Calls.Add("launch"); return Task.CompletedTask; }
    public Task<PlaybackState> StateAsync() => Task.FromResult(Playing);
    public Task PlayAsync() { Calls.Add("play"); Playing = PlaybackState.Playing; return Task.CompletedTask; }
    public Task PauseAsync() { Calls.Add("pause"); Playing = PlaybackState.Paused; return Task.CompletedTask; }
    public Task NextAsync() { Calls.Add("next"); return Task.CompletedTask; }
    public Task PreviousAsync() { Calls.Add("previous"); return Task.CompletedTask; }
    public void SetSpotifyVolume(int percent) => Calls.Add($"volume {percent}");
    public Task OpenUriAsync(string uri, CancellationToken cancellationToken) { Calls.Add("open " + uri); return Task.CompletedTask; }
}

internal sealed class FakeClipboard : IClipboard
{
    public ClipboardContent Content = new("hello", [], false, false);
    public List<string> Calls { get; } = [];
    public ClipboardContent Read() => Content;
    public void Clear() { Calls.Add("clear"); Content = new(null, [], false, false); }
    public bool MakePlainText() { Calls.Add("plain"); return Content.Text is not null; }
    public void Paste() => Calls.Add("paste");
}

internal sealed class FakeWindows : IWindowManager
{
    public List<string> Calls { get; } = [];
    public IReadOnlyList<string> RunningApps() => ["Google Chrome", "Visual Studio Code", "Notepad"];
    public string Arrange(string? app, WindowAction action) { Calls.Add($"{app ?? "front"} {action}"); return app ?? "Notepad"; }
}

internal sealed class FakePlaces : IPlaceFinder
{
    public Task<Place> LocateAsync(string name, CancellationToken cancellationToken) =>
        name.ToLowerInvariant() switch
        {
            "nowhere" => throw new ToolException($"I couldn't find a place called {name}"),
            _ => Task.FromResult(new Place(System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name), 52.37, 4.89, "Europe/Amsterdam")),
        };

    public Task<Forecast> ForecastAsync(Place place, bool fahrenheit, CancellationToken cancellationToken) =>
        Task.FromResult(new Forecast(21.5, "clear", [21.5, 19.4], [11.4, 15.5], ["overcast", "overcast"], [0, 45]));
}

internal sealed class FakeCalendar(params CalendarEvent[] all) : ICalendarSource
{
    public Task<IReadOnlyList<CalendarEvent>> EventsAsync(DateTime start, DateTime end, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CalendarEvent>>(all.Where(e => e.Start < end && e.End > start).ToList());
}

internal sealed class FakeReminders : IReminderSink
{
    public List<(string Title, SpokenWhen? When)> Added { get; } = [];
    public string Name => "Microsoft To Do";
    public Task AddAsync(string title, SpokenWhen? when, CancellationToken cancellationToken)
    {
        Added.Add((title, when));
        return Task.CompletedTask;
    }
}

/// <summary>A Recycle Bin inside the test folder: the real one is never touched.</summary>
internal sealed class FakeRecycleBin(string folder) : IRecycleBin
{
    public string Folder => folder;

    public string Recycle(string path)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return target;
    }

    public void Restore(string inBin, string original)
    {
        if (Directory.Exists(inBin)) Directory.Move(inBin, original);
        else File.Move(inBin, original);
    }

    public bool Contains(string path) => FileAccess.IsInside(Path.GetFullPath(path), folder);
}

/// <summary>A throwaway tree with Documents, Downloads and Desktop, deleted afterwards.</summary>
internal sealed class TempTree : IDisposable
{
    public string Base { get; } = FileAccess.Canonical(Path.Combine(Path.GetTempPath(), "alfred-tests-" + Guid.NewGuid().ToString("N")));
    public string Documents => Path.Combine(Base, "Documents");
    public string Downloads => Path.Combine(Base, "Downloads");
    public string Desktop => Path.Combine(Base, "Desktop");
    public string Bin => Path.Combine(Base, "Bin");
    public IReadOnlyList<string> Roots => [Documents, Downloads, Desktop];

    public TempTree()
    {
        foreach (var folder in Roots) Directory.CreateDirectory(folder);
    }

    public string File(string name, string? folder = null, string contents = "x", DateTime? modified = null)
    {
        var path = Path.Combine(folder ?? Documents, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
        if (modified is { } date) System.IO.File.SetLastWriteTime(path, date);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Base, recursive: true); }
        catch (IOException) { }
    }
}

internal sealed class World : IDisposable
{
    public TempTree Tree { get; } = new();
    public FakeCatalog Catalog { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public FakeSystem System { get; } = new();
    public FakeMedia Media { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeWindows Windows { get; } = new();
    public FakeReminders Reminders { get; } = new();
    public FakeCalendar Calendar { get; init; } = new();
    public List<string> Opened { get; } = [];
    public SettingsStore Settings { get; } = new(null);
    public ClockStore Clock { get; } = new(null);
    public MemorySecretStore Secrets { get; } = new();
    public FileServices Files { get; }
    public AlfredServices Services { get; }
    public IReadOnlyList<IAssistantTool> Tools { get; }

    public World(FakeCalendar? calendar = null)
    {
        if (calendar is not null) Calendar = calendar;
        var organizer = new FileOrganizer(() => Tree.Roots, new FileJournal(Path.Combine(Tree.Base, "journal.json")), new FakeRecycleBin(Tree.Bin));
        Files = new FileServices(() => Tree.Roots, new DirectoryFileSearch(), organizer, path =>
        {
            Opened.Add(path);
            return Task.CompletedTask;
        });
        Services = new AlfredServices
        {
            Settings = Settings, Clock = Clock, Apps = Catalog, Launcher = Launcher, System = System, Media = Media,
            Spotify = new SpotifyWebApi(Settings, Secrets, Launcher), Clipboard = Clipboard, Windows = Windows, Files = Files,
            Places = new FakePlaces(), CalendarSource = Calendar, Reminders = Reminders,
            Notes = new NoteBook(Path.Combine(Tree.Documents, "Alfred Notes"), () => new DateTime(2026, 9, 27, 14, 5, 0)),
        };
        Tools = ToolRegistry.Standard(Services).EnabledTools();
    }

    public IAssistantTool Tool(string name) => Tools.Single(t => t.Name == name);

    /// <summary>The direct match's tool and arguments for a command, or null.</summary>
    public (string Tool, JsonObject Args)? Direct(string transcript) =>
        DirectMatcher.Match(transcript, Tools) is { } plan ? (plan.Steps[0].Tool.Name, plan.Steps[0].Arguments) : null;

    public async Task<ToolResult> Run(string tool, string? transcript, params (string Key, object? Value)[] args)
    {
        var json = new JsonObject();
        foreach (var (key, value) in args) json[key] = value is null ? null : JsonValue.Create(value);
        return await Tool(tool).ExecuteAsync(json, new ToolContext(transcript), CancellationToken.None);
    }

    /// <summary>Runs what the direct matcher planned for this command, as the coordinator would.</summary>
    public async Task<ToolResult> Say(string transcript)
    {
        var plan = DirectMatcher.Match(transcript, Tools) ?? throw new InvalidOperationException($"No direct match for \"{transcript}\"");
        return await plan.Steps[0].ExecuteAsync(true, CancellationToken.None);
    }

    public void Dispose()
    {
        Clock.Dispose();
        Tree.Dispose();
    }
}

internal sealed class FakeModel(string answer) : ILanguageModel
{
    public int Calls { get; private set; }
    public string? LastGrammar { get; private set; }
    public string? LastInstructions { get; private set; }
    public TimeSpan Delay { get; init; }

    public AssistantFailure? UnavailableReason() => null;

    public async Task<string> CompleteAsync(string instructions, string command, string grammar, CancellationToken cancellationToken)
    {
        Calls++;
        LastGrammar = grammar;
        LastInstructions = instructions;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        return answer;
    }
}

internal static class Setup
{
    /// <summary>The first three tools of the W0 tests: openApp, openURL and webSearch, over a fake catalog.</summary>
    public static (IReadOnlyList<IAssistantTool> Tools, FakeLauncher Launcher, FakeCatalog Catalog) Standard()
    {
        var catalog = new FakeCatalog();
        var launcher = new FakeLauncher();
        IReadOnlyList<IAssistantTool> tools =
        [
            new OpenAppTool(catalog, launcher), new OpenUrlTool(catalog, launcher),
            new WebSearchTool(launcher, () => SearchEngine.Google),
        ];
        return (tools, launcher, catalog);
    }

    public static IAssistantTool Tool(this IReadOnlyList<IAssistantTool> tools, string name) => tools.Single(t => t.Name == name);

    public static JsonObject Args(params (string Key, string? Value)[] pairs)
    {
        var json = new JsonObject();
        foreach (var (key, value) in pairs) json[key] = value;
        return json;
    }

    public static string? Str(this JsonObject json, string key) => json[key]?.GetValueKind() == System.Text.Json.JsonValueKind.Null ? null : json[key]?.ToString();
}
