namespace Alfred.Core;

public sealed record OpenUrlArguments(string Url, string? Browser = null);

public sealed class OpenUrlTool(IAppCatalog apps, ILauncher launcher) : AssistantTool<OpenUrlArguments>
{
    public override string Name => "openURL";
    public override string Title => "Open website";
    public override bool RequiresNetwork => true;
    public override IReadOnlySet<string> Keywords { get; } =
        new HashSet<string>(["open", "go", "website", "site", "com", "org", "www", "page", "visit", "browser", .. WebAddress.KnownSites.Keys.Where(k => k.Length > 1)]);

    public override string Description =>
        "Open a website in a browser. \"open YouTube\" or \"go to YouTube\" → url \"https://www.youtube.com\". " +
        "\"open YouTube in Chrome\" → url \"https://www.youtube.com\", browser \"Chrome\"; no openApp step. " +
        "Only use a site's home page or an address the user said. Never make up a path or video ID.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("url", "Full web address starting with https://, e.g. https://www.youtube.com"),
        new("browser", "Browser name, only if the user named one, e.g. \"Chrome\"", Required: false),
    ];

    protected override string Target(OpenUrlArguments arguments) =>
        WebAddress.Normalize(arguments.Url) is { } url ? new Uri(url).Host : arguments.Url;

    protected override async Task<ToolResult> ExecuteAsync(OpenUrlArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var address = WebAddress.Normalize(arguments.Url)
            ?? throw new ToolException($"\"{arguments.Url}\" isn't a web address I can open");
        var url = new Uri(address);
        // Act only on what the user said: an invented path falls back to the
        // site's home page, and an unspoken browser to the default one.
        if (context.Transcript is { } transcript && !Grounding.IsSpoken(url, transcript))
        {
            address = WebAddress.HomePage(url);
            url = new Uri(address);
        }
        var browserName = GroundBrowser(arguments.Browser, context.Transcript);
        // "go to YouTube" came back with browser "YouTube" on the Mac: the site, not a browser.
        if (browserName is not null && url.Host.Contains(SpokenText.Key(browserName), StringComparison.Ordinal))
            browserName = null;

        InstalledApp? browser = null;
        if (browserName is not null && !AppNameMatcher.IsGenericBrowser(browserName))
        {
            browser = apps.ResolveBrowser(browserName)
                ?? throw new ToolException($"I couldn't find a browser called \"{browserName}\"");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await launcher.OpenUrlAsync(address, browser, cancellationToken);
        return browser is null ? $"Opened {url.Host}" : $"Opened {url.Host} in {browser.Name}";
    }

    /// <summary>
    /// Drops a browser the user didn't say. With none given, uses an installed
    /// browser the user named anyway: "open Chrome and play the … video" plans
    /// openApp Chrome, then a step without a browser, which would open elsewhere.
    /// </summary>
    internal string? GroundBrowser(string? name, string? transcript)
    {
        if (transcript is null) return name;
        if (name is not null && Grounding.Mentions(name, transcript)) return name;
        return apps.Browsers().FirstOrDefault(b => Grounding.MentionsApp(b.Name, transcript))?.Name;
    }

    protected override OpenUrlArguments? Direct(DirectCommand command)
    {
        if (command.Verb is not ("open" or "go to")) return null;
        var (thing, spokenBrowser) = command.Target;
        if (spokenBrowser is not null && !AppNameMatcher.IsGenericBrowser(spokenBrowser) && apps.ResolveBrowser(spokenBrowser) is null)
            return null;
        if (WebAddress.Spoken(thing) is { } url) return new OpenUrlArguments(url, spokenBrowser);
        return WebAddress.KnownSites.TryGetValue(SpokenText.Key(thing), out var site) ? new OpenUrlArguments(site, spokenBrowser) : null;
    }
}
