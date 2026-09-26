namespace Alfred.Core;

public sealed record OpenAppArguments(string AppName);

public sealed class OpenAppTool(IAppCatalog apps, ILauncher launcher) : AssistantTool<OpenAppArguments>
{
    public override string Name => "openApp";
    public override string Title => "Open app";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "open", "launch", "start", "app", "application", "browser" };

    public override string Description =>
        "Launch an installed Windows app by name. \"open Spotify\" → appName \"Spotify\". " +
        "\"open my browser\" → appName \"my browser\", exactly as said. " +
        "Not for websites: YouTube and Gmail are websites, use openURL.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
        [new("appName", "The app's name as the user said it, e.g. \"Spotify\" or \"Discord\"")];

    protected override string Target(OpenAppArguments arguments) => arguments.AppName;

    protected override async Task<ToolResult> ExecuteAsync(OpenAppArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        InstalledApp? app;
        switch (Ground(arguments.AppName, context.Transcript))
        {
            case Grounded.DefaultBrowser:
                app = apps.DefaultBrowser();
                break;
            case Grounded.Named(var name):
                app = apps.Resolve(name);
                break;
            default:
                // "open again" became openApp "Finder" on the Mac: the model
                // filled in an app the user never named. Refuse rather than open it.
                throw new ToolException("I didn't catch which app to open");
        }
        if (app is null) throw new ToolException($"I couldn't find an app called \"{arguments.AppName}\"");
        cancellationToken.ThrowIfCancellationRequested();
        await launcher.LaunchAsync(app, cancellationToken);
        return $"Opened {app.Name}";
    }

    protected override OpenAppArguments? Direct(DirectCommand command)
    {
        if (command.Verb is not ("open" or "launch" or "start")) return null;
        var name = command.Rest;
        return AppNameMatcher.IsGenericBrowser(name) || apps.Resolve(name) is not null ? new OpenAppArguments(name) : null;
    }

    private abstract record Grounded
    {
        public sealed record Named(string Name) : Grounded;
        public sealed record DefaultBrowser : Grounded;
        public sealed record Unspoken : Grounded;
    }

    /// <summary>
    /// The app must be one the user named: said outright, said through an
    /// alias ("vs code"), or "my browser" for the default browser.
    /// </summary>
    private Grounded Ground(string appName, string? transcript)
    {
        if (transcript is null) return new Grounded.Named(appName);
        if (AppNameMatcher.IsGenericBrowser(appName) || Grounding.MentionsGenericBrowser(transcript))
        {
            // The model turns "my browser" into a browser's name; trust what was said.
            if (!Grounding.Mentions(appName, transcript)) return new Grounded.DefaultBrowser();
        }
        if (Grounding.Mentions(appName, transcript)) return new Grounded.Named(appName);
        if (Grounding.SpokenAlias(appName, transcript, apps.Aliases) is { } alias) return new Grounded.Named(alias);
        return new Grounded.Unspoken();
    }
}
