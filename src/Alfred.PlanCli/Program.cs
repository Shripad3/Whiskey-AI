// Plans transcripts and shows what each would start, without starting anything, to choose and check the default
// model against the intent corpus (spec §12). Runs on Windows and the Mac.
//
//   dotnet run --project src/Alfred.PlanCli -- --model models/qwen3-4b-q4_k_m.gguf < shared/fixtures/intents.txt
//   dotnet run --project src/Alfred.PlanCli -- --model <gguf> "open chrome and go to youtube"

using System.Diagnostics;
using Alfred.Core;
using Alfred.Llm;

string? modelPath = null;
var gpuLayers = int.MaxValue;
var transcripts = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model": modelPath = args[++i]; break;
        case "--cpu": gpuLayers = 0; break;
        default: transcripts.Add(args[i]); break;
    }
}
if (transcripts.Count == 0)
{
    while (Console.ReadLine() is { } line) transcripts.Add(line);
}

ILanguageModel model = modelPath is null ? new NoModel() : await LoadAsync(modelPath, gpuLayers);
var launcher = new RecordingLauncher();
var tools = ToolRegistry.Standard(DryRun.Services(launcher)).EnabledTools();
var engine = new LocalModelEngine(model);
var modelTimes = new List<double>();

foreach (var raw in transcripts)
{
    var line = raw.Trim();
    if (line.Length == 0) continue;
    if (line.StartsWith('#'))
    {
        Console.WriteLine($"\n{line}");
        continue;
    }
    var clock = Stopwatch.StartNew();
    try
    {
        var plan = await engine.PlanAsync(line, tools, CancellationToken.None);
        var ms = clock.Elapsed.TotalMilliseconds;
        var source = plan.Reply is not null ? "reply" : plan.IsDirect ? "direct" : "model";
        if (source == "model") modelTimes.Add(ms);
        var steps = plan.Reply ?? (plan.Steps.Count == 0 ? "(no steps)" :
            string.Join(" → ", plan.Steps.Select(s => $"{s.Tool.Name} {s.Arguments.ToJsonString()}")));
        var routed = plan.Routed is null ? "" : $"  [saw {string.Join(", ", plan.Routed)}]";
        Console.WriteLine($"{line}\n    {source,-6} {ms,6:0} ms  {steps}{routed}");
        // What would happen after each tool's grounding, with nothing started.
        launcher.Launched.Clear();
        var outcome = plan.Steps.Count == 0 ? new PlanOutcome([], null) : await PlanRunner.RunAsync(plan, CancellationToken.None);
        var did = string.Join("; ", launcher.Launched.Concat(outcome.Failure is null ? [] : [$"✗ {outcome.Failure}"]));
        if (did.Length > 0) Console.WriteLine($"    would  {did}");
    }
    catch (AssistantFailure failure)
    {
        Console.WriteLine($"{line}\n    FAILED {clock.Elapsed.TotalMilliseconds,6:0} ms  {failure.Message}");
    }
}

if (modelTimes.Count > 0)
{
    modelTimes.Sort();
    Console.WriteLine($"\nmodel calls: {modelTimes.Count}, median {modelTimes[modelTimes.Count / 2]:0} ms, slowest {modelTimes[^1]:0} ms");
}
(model as IDisposable)?.Dispose();

static async Task<ILanguageModel> LoadAsync(string path, int gpuLayers)
{
    var clock = Stopwatch.StartNew();
    var model = await LlamaLanguageModel.LoadAsync(path, gpuLayers);
    Console.WriteLine($"loaded {model.ModelName} in {clock.Elapsed.TotalSeconds:0.0} s");
    return model;
}

/// <summary>Without --model, only the stages before the model run.</summary>
sealed class NoModel : ILanguageModel
{
    public AssistantFailure? UnavailableReason() => new("no --model given; only direct matches and replies are planned");
    public Task<string> CompleteAsync(string instructions, string command, string grammar, CancellationToken cancellationToken) =>
        throw new InvalidOperationException();
}

/// <summary>A typical Windows Start menu, so results don't depend on the machine running the corpus.</summary>
sealed class SampleCatalog : IAppCatalog
{
    private static readonly InstalledApp[] Installed =
    [
        .. new[]
        {
            "Spotify", "Google Chrome", "Microsoft Edge", "Firefox", "Visual Studio Code", "Settings", "Notepad",
            "File Explorer", "Microsoft Store", "Word", "Excel", "Calculator", "Discord", "Steam", "OMEN Gaming Hub",
        }.Select(n => new InstalledApp(n, n)),
    ];

    public IReadOnlyList<InstalledApp> Apps(bool refresh = false) => Installed;
    public IReadOnlyList<InstalledApp> Browsers() => [new("Google Chrome", "chrome.exe"), new("Microsoft Edge", "msedge.exe"), new("Mozilla Firefox", "firefox.exe")];
    public InstalledApp? DefaultBrowser() => Browsers()[1];
}

/// <summary>Records what would have been started.</summary>
sealed class RecordingLauncher : ILauncher
{
    public List<string> Launched { get; } = [];

    public Task LaunchAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        Launched.Add($"launch {app.Name}");
        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url, InstalledApp? browser, CancellationToken cancellationToken)
    {
        Launched.Add(browser is null ? $"open {url}" : $"open {url} in {browser.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>Stand-ins for everything a tool touches: nothing starts, nothing goes online.</summary>
static class DryRun
{
    public static AlfredServices Services(RecordingLauncher launcher)
    {
        var settings = new SettingsStore(null);
        var clock = new ClockStore(null);
        var roots = new[] { Path.Combine(Path.GetTempPath(), "alfred-dry-run") };
        var organizer = new FileOrganizer(() => roots, new FileJournal(Path.Combine(roots[0], "journal.json")), new NoBin());
        return new AlfredServices
        {
            Settings = settings, Clock = clock, Apps = new SampleCatalog(), Launcher = launcher, System = new NoSystem(),
            Media = new NoMedia(), Spotify = new SpotifyWebApi(settings, new MemorySecretStore(), launcher), Clipboard = new NoClipboard(),
            Windows = new NoWindows(), Files = new FileServices(() => roots, new DirectoryFileSearch(), organizer, _ => Task.CompletedTask),
            Places = new NoPlaces(), CalendarSource = new NoCalendar(), Reminders = new NoReminders(),
            Notes = new NoteBook(Path.Combine(roots[0], "notes"), () => DateTime.Now),
        };
    }

    sealed class NoSystem : ISystemControl
    {
        public float Volume() => 0.5f;
        public float SetVolume(float volume) => Math.Clamp(volume, 0, 1);
        public void SetMuted(bool muted) { }
        public int Brightness() => 50;
        public void SetBrightness(int percent) { }
        public void Lock() { }
        public void Sleep() { }
        public void OpenNotificationSettings() { }
    }

    sealed class NoMedia : IMediaControl
    {
        public bool IsSpotifyInstalled => true;
        public bool IsSpotifyRunning => true;
        public Task LaunchSpotifyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PlaybackState> StateAsync() => Task.FromResult(PlaybackState.Playing);
        public Task PlayAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task NextAsync() => Task.CompletedTask;
        public Task PreviousAsync() => Task.CompletedTask;
        public void SetSpotifyVolume(int percent) { }
        public Task OpenUriAsync(string uri, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    sealed class NoClipboard : IClipboard
    {
        public ClipboardContent Read() => new("(clipboard)", [], false, false);
        public void Clear() { }
        public bool MakePlainText() => true;
        public void Paste() { }
    }

    sealed class NoWindows : IWindowManager
    {
        public IReadOnlyList<string> RunningApps() => ["Google Chrome", "Visual Studio Code", "Spotify"];
        public string Arrange(string? app, WindowAction action) => app ?? "Window";
    }

    sealed class NoBin : IRecycleBin
    {
        public string Recycle(string path) => path;
        public void Restore(string inBin, string original) { }
        public bool Contains(string path) => false;
    }

    sealed class NoPlaces : IPlaceFinder
    {
        public Task<Place> LocateAsync(string name, CancellationToken cancellationToken) => Task.FromResult(new Place(name, 0, 0, "UTC"));
        public Task<Forecast> ForecastAsync(Place place, bool fahrenheit, CancellationToken cancellationToken) =>
            Task.FromResult(new Forecast(20, "clear", [22, 21], [12, 11], ["clear", "clear"], [0, 0]));
    }

    sealed class NoCalendar : ICalendarSource
    {
        public Task<IReadOnlyList<CalendarEvent>> EventsAsync(DateTime start, DateTime end, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);
    }

    sealed class NoReminders : IReminderSink
    {
        public string Name => "Alfred";
        public Task AddAsync(string title, SpokenWhen? when, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
