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
var tools = ToolRegistry.Standard(new SampleCatalog(), launcher).EnabledTools();
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
        var outcome = await PlanRunner.RunAsync(plan, CancellationToken.None);
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
