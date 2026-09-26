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
    public static (IReadOnlyList<IAssistantTool> Tools, FakeLauncher Launcher, FakeCatalog Catalog) Standard()
    {
        var catalog = new FakeCatalog();
        var launcher = new FakeLauncher();
        return (ToolRegistry.Standard(catalog, launcher).EnabledTools(), launcher, catalog);
    }

    public static IAssistantTool Tool(this IReadOnlyList<IAssistantTool> tools, string name) => tools.Single(t => t.Name == name);

    public static JsonObject Args(params (string Key, string? Value)[] pairs)
    {
        var json = new JsonObject();
        foreach (var (key, value) in pairs) json[key] = value;
        return json;
    }
}
