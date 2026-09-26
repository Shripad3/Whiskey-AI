using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alfred.Core;

/// <summary>
/// One argument as the model sees it. Every argument is a JSON string; an
/// optional one may be null. <paramref name="Choices"/> limits it to fixed
/// values, and <paramref name="Digits"/> to a whole number of up to three digits.
/// </summary>
public sealed record ArgumentSpec(string Name, string Description, bool Required = true, IReadOnlyList<string>? Choices = null, bool Digits = false);

/// <summary>What the running tool knows about the command it serves.</summary>
/// <param name="Transcript">
/// What the user said, so the tool can ground its arguments in it. Null for a
/// routine's steps, which the user wrote themselves.
/// </param>
/// <param name="IsFinalStep">False for steps followed by others.</param>
public sealed record ToolContext(string? Transcript, bool IsFinalStep = true);

/// <summary>
/// A row in a result list, such as a found file. <paramref name="Id"/> is an
/// opaque token: the UI never holds a path, and selecting a row goes back
/// through the coordinator, which validates again before acting.
/// </summary>
public sealed record ResultItem(string Id, string Title, string Detail, string Symbol);

/// <summary>What a tool reports: a one-line outcome, and optionally items to choose from.</summary>
/// <param name="IsAnswer">An answer to a question (the weather): shown and spoken as a reply.</param>
public sealed record ToolResult(string Text, bool IsAnswer = false)
{
    public IReadOnlyList<ResultItem> Items { get; init; } = [];
    /// <summary>Set when the change waits for the user's yes (a batch of files).</summary>
    public string? Confirmation { get; init; }
    /// <summary>A file change that "undo" can reverse; its result stays up longer.</summary>
    public bool Undoable { get; init; }

    public static implicit operator ToolResult(string text) => new(text);
}

/// <summary>A failure to show the user, in their terms.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// One capability. Adding a capability means writing one tool and listing it
/// in <see cref="ToolRegistry"/>; the engine never names concrete tools.
/// </summary>
public interface IAssistantTool
{
    string Name { get; }
    /// <summary>Shown to the user in the Acting state and the Capabilities pane.</summary>
    string Title { get; }
    /// <summary>Written for the model, not a human: concrete, with an example phrasing.</summary>
    string Description { get; }
    /// <summary>Words that suggest this tool (<see cref="ToolRouter"/>).</summary>
    IReadOnlySet<string> Keywords { get; }
    IReadOnlyList<ArgumentSpec> Arguments { get; }
    bool RequiresNetwork { get; }

    /// <summary>
    /// Arguments for a command simple enough to recognise without the model
    /// ("open Spotify"), or null to leave it to the model. Must be
    /// conservative: a wrong match skips the model entirely.
    /// </summary>
    JsonObject? DirectArguments(DirectCommand command);

    /// <summary>Short description of what the call acts on, for the Acting state.</summary>
    string Target(JsonObject arguments);

    Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>A tool with typed arguments, serialised as camelCase JSON.</summary>
public abstract class AssistantTool<TArguments> : IAssistantTool where TArguments : class
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Numbers reach tools as quoted digits from the model's grammar.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public abstract string Name { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract IReadOnlySet<string> Keywords { get; }
    public abstract IReadOnlyList<ArgumentSpec> Arguments { get; }
    public abstract bool RequiresNetwork { get; }

    protected virtual TArguments? Direct(DirectCommand command) => null;
    protected abstract string Target(TArguments arguments);
    protected abstract Task<ToolResult> ExecuteAsync(TArguments arguments, ToolContext context, CancellationToken cancellationToken);

    public JsonObject? DirectArguments(DirectCommand command) =>
        Direct(command) is { } arguments ? (JsonObject)JsonSerializer.SerializeToNode(arguments, Json)! : null;

    public string Target(JsonObject arguments) => Target(Parse(arguments));

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken) =>
        ExecuteAsync(Parse(arguments), context, cancellationToken);

    private TArguments Parse(JsonObject arguments)
    {
        try
        {
            return arguments.Deserialize<TArguments>(Json) ?? throw new ToolException($"{Title}: missing arguments");
        }
        catch (JsonException)
        {
            throw new ToolException($"{Title}: the arguments didn't make sense");
        }
    }
}

/// <summary>
/// The tools the model may see. Read for every command: a disabled capability
/// is never registered, so the model cannot call it (spec §11).
/// </summary>
public sealed class ToolRegistry(IReadOnlyList<IAssistantTool> tools, Func<string, bool>? isEnabled = null)
{
    public IReadOnlyList<IAssistantTool> Tools { get; } = tools;

    public IReadOnlyList<IAssistantTool> EnabledTools() =>
        Tools.Where(t => isEnabled?.Invoke(t.Name) ?? true).ToList();

    /// <summary>Every tool, in tie-breaking order for direct matching.</summary>
    public static ToolRegistry Standard(AlfredServices services, Func<string, bool>? isEnabled = null) =>
        new([
            // First: their phrasings are specific, and "stop the timer" or
            // "start a stopwatch" must not reach Spotify or app launching.
            new TimerTool(services.Clock),
            new AlarmTool(services.Clock),
            new StopwatchTool(services.Clock),
            new ReminderTool(services.Clock, services.Reminders),
            new CurrentTimeTool(services.Places),
            new CalendarTool(services.CalendarSource),
            new OpenAppTool(services.Apps, services.Launcher),
            new OpenUrlTool(services.Apps, services.Launcher),
            // Before Spotify and files: "play … on YouTube" and "… video" are its.
            new PlayYouTubeTool(services.Apps, services.Launcher),
            // Before webSearch: "how's the weather" is answered, not searched.
            new WeatherTool(services.Places, services.Settings),
            new WebSearchTool(services.Launcher, () => services.Settings.Current.SearchEngine),
            new ControlSpotifyTool(services.Media, services.Spotify),
            new SystemControlTool(services.System),
            new WindowTool(services.Windows),
            new ClipboardTool(services.Clipboard),
            new NoteTool(services.Notes),
            // After openApp and openURL: "open spotify" must stay an app.
            new OpenFileTool(services.Files),
            new FindFilesTool(services.Files),
            new OrganiseFilesTool(services.Files),
            new UndoFileChangeTool(services.Files),
        ], isEnabled ?? (name => services.Settings.Current.IsEnabled(name)));
}
