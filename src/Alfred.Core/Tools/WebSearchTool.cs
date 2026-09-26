namespace Alfred.Core;

public sealed record WebSearchArguments(string Query);

/// <summary>
/// Builds a search URL and opens it. Deliberately not an API call: no key, no
/// cost, no rate limit. The engine comes from Settings, not from the model:
/// given an optional engine argument, the Mac's model filled it in unasked.
/// </summary>
public sealed class WebSearchTool(ILauncher launcher, Func<SearchEngine> engine) : AssistantTool<WebSearchArguments>
{
    public override string Name => "webSearch";
    public override string Title => "Search the web";
    public override bool RequiresNetwork => true;
    public override IReadOnlySet<string> Keywords { get; } =
        new HashSet<string> { "search", "google", "look", "what", "who", "how", "when", "why", "news" };

    public override string Description =>
        "Search the web for information or a question. \"search for banana bread recipes\" → query \"banana bread recipes\". " +
        "Not for opening a website by name.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } = [new("query", "What to search for, in the user's words")];

    protected override string Target(WebSearchArguments arguments) => arguments.Query;

    protected override async Task<ToolResult> ExecuteAsync(WebSearchArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var searchEngine = engine();
        var url = searchEngine.Url(arguments.Query) ?? throw new ToolException("There's nothing to search for");
        cancellationToken.ThrowIfCancellationRequested();
        await launcher.OpenUrlAsync(url, null, cancellationToken);
        return $"Searched {searchEngine.Title()} for “{arguments.Query.Trim()}”";
    }

    protected override WebSearchArguments? Direct(DirectCommand command) =>
        command.Verb is "search for" or "search" or "google" or "look up" ? new WebSearchArguments(command.Rest) : null;
}
