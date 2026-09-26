using System.Text.RegularExpressions;

namespace Alfred.Core;

/// <param name="Latest">"true" for the newest videos first.</param>
public sealed record PlayYouTubeArguments(string Query, string? Latest = null, string? Browser = null);

/// <summary>
/// Finds videos on YouTube (spec §8, Tier 1): opens YouTube's search results,
/// optionally sorted by upload date. Deterministic, and needs no permission.
/// </summary>
public sealed partial class PlayYouTubeTool(IAppCatalog apps, ILauncher launcher) : AssistantTool<PlayYouTubeArguments>
{
    public override string Name => "playYouTube";
    public override string Title => "YouTube";
    public override bool RequiresNetwork => true;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "youtube", "video", "videos", "watch", "channel", "clip" };

    public override string Description =>
        "Find videos on YouTube. \"play the Mat Armstrong video on YouTube\" → query \"Mat Armstrong\". " +
        "\"Mat Armstrong's latest video on YouTube in Chrome\" → query \"Mat Armstrong\", latest \"true\", browser \"Chrome\"; no openApp step. " +
        "Never use openURL with a made-up video link.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("query", "What to find on YouTube, e.g. \"Mat Armstrong\" or \"lofi hip hop\""),
        new("latest", "\"true\" if the user asked for the latest, newest or most recent video", Required: false, Choices: ["true", "false"]),
        new("browser", "Browser name, only if the user named one, e.g. \"Chrome\"", Required: false),
    ];

    private static readonly string[] LatestWords = ["latest", "newest", "most recent", "new", "recent", "last"];

    protected override string Target(PlayYouTubeArguments a) => a.Query + (a.Latest == "true" ? " · latest" : "");

    protected override async Task<ToolResult> ExecuteAsync(PlayYouTubeArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var latest = arguments.Latest == "true" && (context.Transcript is null || LatestWords.Any(w => Grounding.Mentions(w, context.Transcript)));
        var url = SearchUrl(arguments.Query, latest) ?? throw new ToolException("What should I look for on YouTube?");
        var browserName = new OpenUrlTool(apps, launcher).GroundBrowser(arguments.Browser, context.Transcript);
        InstalledApp? browser = null;
        if (browserName is not null && !AppNameMatcher.IsGenericBrowser(browserName))
            browser = apps.ResolveBrowser(browserName) ?? throw new ToolException($"I couldn't find a browser called \"{browserName}\"");
        cancellationToken.ThrowIfCancellationRequested();
        await launcher.OpenUrlAsync(url, browser, cancellationToken);
        var what = latest ? $"latest “{arguments.Query}” videos" : $"“{arguments.Query}”";
        return $"YouTube results for {what}{(browser is null ? "" : $" in {browser.Name}")}";
    }

    /// <summary>youtube.com/results, optionally sorted by upload date (sp=CAI%3D).</summary>
    public static string? SearchUrl(string query, bool latest)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return null;
        return $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(trimmed)}" + (latest ? "&sp=CAI%3D" : "");
    }

    private static readonly HashSet<string> FillerWords = ["on", "youtube", "video", "videos", "the", "a", "s", "channel", "clip", "from", "by"];

    /// <summary>"play Matt Armstrong's latest video on YouTube in Chrome".</summary>
    protected override PlayYouTubeArguments? Direct(DirectCommand command)
    {
        if (command.Verb is not ("play" or "watch" or "open" or "search" or "look up") && !command.Text.StartsWith("search youtube", StringComparison.Ordinal)) return null;
        var words = command.Rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!words.Contains("youtube")) return null;

        string? browser = null;
        var index = words.LastIndexOf("in");
        if (index >= 0 && index + 1 < words.Count)
        {
            var name = words[(index + 1)..];
            if (name[^1] == "browser" && name.Count > 1) name.RemoveAt(name.Count - 1);
            var spoken = string.Join(' ', name);
            if (spoken == "browser" || AppNameMatcher.IsGenericBrowser(spoken) || apps.ResolveBrowser(spoken) is not null)
            {
                browser = spoken == "browser" ? null : spoken;
                words.RemoveRange(index, words.Count - index);
            }
            else if (index > words.IndexOf("youtube"))
            {
                return null; // "in <something that isn't a browser>": not ours to guess.
            }
        }
        var latest = false;
        var latestIndex = words.FindIndex(w => w is "latest" or "newest" or "recent" or "new");
        if (latestIndex >= 0)
        {
            latest = true;
            words.RemoveAt(latestIndex);
            if (latestIndex > 0 && words[latestIndex - 1] == "most") words.RemoveAt(latestIndex - 1);
        }
        var query = string.Join(' ', words.Where(w => !FillerWords.Contains(w)));
        return query.Length == 0 ? null : new PlayYouTubeArguments(query, latest ? "true" : null, browser);
    }
}

public sealed record WindowArguments(string Action, string? App = null);

/// <summary>
/// Arranges the front window, or a named app's: halves, maximise, centre,
/// full screen, minimise, or the other display. Nothing is closed or quit.
/// </summary>
public sealed class WindowTool(IWindowManager windows) : AssistantTool<WindowArguments>
{
    public override string Name => "arrangeWindow";
    public override string Title => "Window";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        "window", "left", "right", "half", "side", "maximize", "maximise", "fullscreen", "full", "center", "centre", "minimize", "minimise",
        "display", "monitor", "tile", "snap",
    };

    public override string Description =>
        "Move or resize a window. \"put Chrome on the left half\" → leftHalf, app \"Chrome\". " +
        "\"make this full screen\" → fullScreen. \"move this window to the other display\" → nextDisplay.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("action", "Where the window goes", Choices: Enum.GetNames<WindowAction>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]).ToList()),
        new("app", "App name, only if the user named one, e.g. \"Chrome\"; otherwise the front window", Required: false),
    ];

    protected override string Target(WindowArguments a) => string.Join(" · ", new[] { a.App, Describe(a.Action) }.OfType<string>());

    protected override Task<ToolResult> ExecuteAsync(WindowArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<WindowAction>(arguments.Action, ignoreCase: true, out var action))
            throw new ToolException("I don't know how to do that with a window");
        string? app = null;
        if (ClockPhrases.Grounded(arguments.App, context) is { } spoken)
            app = AppNameMatcher.Match(spoken, windows.RunningApps()) ?? throw new ToolException($"{spoken} isn't open");
        var name = windows.Arrange(app, action);
        return Task.FromResult<ToolResult>($"{name}: {Describe(arguments.Action)}");
    }

    public static string Describe(string action) => action switch
    {
        "leftHalf" => "left half",
        "rightHalf" => "right half",
        "topHalf" => "top half",
        "bottomHalf" => "bottom half",
        "maximize" => "maximised",
        "center" => "centred",
        "fullScreen" => "full screen",
        "exitFullScreen" => "out of full screen",
        "minimize" => "minimised",
        "nextDisplay" => "moved to the other display",
        _ => action,
    };

    /// <summary>The frame for a placement within a monitor's work area (taskbar excluded; top-left origin).</summary>
    public static Rect? Frame(WindowAction action, Rect work, Rect current) => action switch
    {
        WindowAction.LeftHalf => work with { Width = work.Width / 2 },
        WindowAction.RightHalf => work with { X = work.MidX, Width = work.Width / 2 },
        WindowAction.TopHalf => work with { Height = work.Height / 2 },
        WindowAction.BottomHalf => work with { Y = work.MidY, Height = work.Height / 2 },
        WindowAction.Maximize => work,
        WindowAction.Center => Centered(work, current),
        _ => null,
    };

    private static Rect Centered(Rect work, Rect current)
    {
        double width = Math.Min(current.Width, work.Width), height = Math.Min(current.Height, work.Height);
        return new Rect(work.MidX - width / 2, work.MidY - height / 2, width, height);
    }

    /// <summary>The same relative place on another monitor.</summary>
    public static Rect Moved(Rect frame, Rect source, Rect destination)
    {
        double sx = destination.Width / source.Width, sy = destination.Height / source.Height;
        return new Rect(destination.X + (frame.X - source.X) * sx, destination.Y + (frame.Y - source.Y) * sy, frame.Width * sx, frame.Height * sy);
    }

    private static readonly (string Words, string Action)[] Positions =
    [
        ("left half", "leftHalf"), ("left side", "leftHalf"), ("to the left", "leftHalf"), ("on the left", "leftHalf"),
        ("right half", "rightHalf"), ("right side", "rightHalf"), ("to the right", "rightHalf"), ("on the right", "rightHalf"),
        ("top half", "topHalf"), ("bottom half", "bottomHalf"),
        ("exit full screen", "exitFullScreen"), ("leave full screen", "exitFullScreen"), ("exit fullscreen", "exitFullScreen"),
        ("full screen", "fullScreen"), ("fullscreen", "fullScreen"),
        ("maximize", "maximize"), ("maximise", "maximize"), ("fill the screen", "maximize"),
        ("minimize", "minimize"), ("minimise", "minimize"),
        ("other screen", "nextDisplay"), ("other display", "nextDisplay"), ("other monitor", "nextDisplay"),
        ("next screen", "nextDisplay"), ("next display", "nextDisplay"), ("external display", "nextDisplay"), ("external monitor", "nextDisplay"),
        ("center", "center"), ("centre", "center"),
    ];

    private static readonly HashSet<string> Verbs = ["put", "move", "snap", "tile", "send", "make", "set", "throw", "push", "place", "drag"];

    private static readonly HashSet<string> Filler =
    [
        "put", "move", "snap", "tile", "send", "make", "set", "throw", "push", "place", "drag", "the", "this", "that", "my", "window",
        "windows", "to", "on", "of", "in", "a", "it", "screen", "display", "monitor", "half", "side", "left", "right", "top", "bottom",
        "full", "fullscreen", "maximize", "maximise", "minimize", "minimise", "center", "centre", "other", "next", "external", "exit",
        "leave", "fill", "app", "please", "and",
    ];

    /// <summary>"put Chrome on the left half", "maximize this window", "full screen", "move this to the other display".</summary>
    protected override WindowArguments? Direct(DirectCommand command)
    {
        if (command.Verb is "open" or "launch" or "play" or "watch" or "search" or "search for" or "google" or "look up" or "go to") return null;
        var text = $" {command.Text} ";
        var match = Positions.FirstOrDefault(p => text.Contains($" {p.Words} ", StringComparison.Ordinal));
        if (match.Action is null) return null;
        var words = command.Text.Split(' ');
        // Directions and "center" need a window verb or the word "window":
        // "what's on the left" is not a window command.
        var needsVerb = match.Action is "leftHalf" or "rightHalf" or "center" && !text.Contains(" half ", StringComparison.Ordinal);
        if (needsVerb && !(Verbs.Contains(words[0]) || words.Contains("window"))) return null;
        var rest = words.Where(w => !Filler.Contains(w)).ToList();
        if (rest.Count > 3) return null;
        return new WindowArguments(match.Action, rest.Count == 0 ? null : string.Join(' ', rest));
    }
}

public sealed record ClipboardArguments(string Action);

/// <summary>
/// The clipboard: say what's on it, clear it, or strip its formatting (and
/// paste that). Never reads out anything a password manager marked private.
/// </summary>
public sealed class ClipboardTool(IClipboard clipboard) : AssistantTool<ClipboardArguments>
{
    public override string Name => "clipboard";
    public override string Title => "Clipboard";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "clipboard", "copied", "copy", "paste", "formatting", "plain" };

    public override string Description =>
        "The clipboard. \"what's on my clipboard\" → read. \"clear the clipboard\" → clear. \"paste as plain text\" → pastePlainText.";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
        [new("action", "What to do", Choices: ["read", "clear", "plainText", "pastePlainText"])];

    protected override string Target(ClipboardArguments a) => a.Action switch
    {
        "clear" => "Clear",
        "plainText" or "pastePlainText" => "Plain text",
        _ => "Read",
    };

    protected override Task<ToolResult> ExecuteAsync(ClipboardArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        switch (arguments.Action)
        {
            case "clear":
                clipboard.Clear();
                return Task.FromResult<ToolResult>("Clipboard cleared");
            case "plainText" or "pastePlainText":
                if (!clipboard.MakePlainText()) throw new ToolException("There's no text on the clipboard");
                if (arguments.Action == "plainText") return Task.FromResult<ToolResult>("The clipboard is now plain text");
                clipboard.Paste();
                return Task.FromResult<ToolResult>("Pasted as plain text");
            default:
                return Task.FromResult(new ToolResult(Describe(clipboard.Read()), IsAnswer: true));
        }
    }

    public static string Describe(ClipboardContent content)
    {
        if (content.IsPrivate) return "That's marked private by a password manager, so I won't read it out";
        if (content.Files.Count > 0)
        {
            var names = string.Join(", ", content.Files.Take(3).Select(Path.GetFileName));
            return content.Files.Count == 1 ? $"A file: {names}" : $"{content.Files.Count} files: {names}{(content.Files.Count > 3 ? "…" : "")}";
        }
        if (content.Text?.Trim() is { Length: > 0 } text) return $"Your clipboard says: {(text.Length > 200 ? text[..200] + "…" : text)}";
        return content.HasImage ? "An image" : "The clipboard is empty";
    }

    protected override ClipboardArguments? Direct(DirectCommand command)
    {
        var text = command.Text;
        if (text is "paste as plain text" or "paste without formatting" or "paste plain text" or "paste it as plain text" or "paste and match style")
            return new("pastePlainText");
        if (text.Contains("clipboard", StringComparison.Ordinal))
        {
            if (new[] { "clear", "empty", "wipe", "delete" }.Any(text.Contains)) return new("clear");
            if (new[] { "plain text", "formatting" }.Any(text.Contains)) return new("plainText");
            if (new[] { "what", "read", "show", "tell" }.Any(w => text.StartsWith(w, StringComparison.Ordinal))) return new("read");
            return null;
        }
        return text is "what did i copy" or "what have i copied" or "what did i just copy" or "what s copied" ? new("read") : null;
    }
}

/// <summary>Quick notes, appended to a Markdown file per day in Documents\Alfred Notes (spec §8).</summary>
public sealed class NoteBook(string folder, Func<DateTime> now)
{
    public string Folder => folder;

    /// <summary>Appends a note and returns the file it went into. Never edits or deletes existing notes.</summary>
    public string Add(string text)
    {
        Directory.CreateDirectory(folder);
        var time = now();
        var file = Path.Combine(folder, $"{time:yyyy-MM-dd}.md");
        var heading = File.Exists(file) ? "" : $"# Notes, {time:dddd d MMMM yyyy}\n\n";
        File.AppendAllText(file, $"{heading}- {time:HH:mm} {text}\n");
        return file;
    }
}

public sealed record NoteArguments(string Text);

/// <summary>"note that the Wi-Fi password is on the router".</summary>
public sealed partial class NoteTool(NoteBook notes) : AssistantTool<NoteArguments>
{
    public override string Name => "takeNote";
    public override string Title => "Note";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "note", "notes", "jot", "write" };

    public override string Description =>
        "Save a quick note. \"note that the Wi-Fi password is on the router\" → text \"the Wi-Fi password is on the router\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
        [new("text", "The note's text, in the user's words, e.g. \"the Wi-Fi password is on the router\"")];

    protected override string Target(NoteArguments a) => a.Text;

    protected override Task<ToolResult> ExecuteAsync(NoteArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var text = arguments.Text.Trim();
        if (context.Transcript is { } transcript
            && (text.Length == 0 || !new SpokenWords(text).Lower.ToHashSet().IsSubsetOf(new SpokenWords(transcript).Lower)))
            throw new ToolException("What should the note say?");
        try
        {
            notes.Add(ClockAlert.Capitalised(text));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolException($"I couldn't write to {notes.Folder}");
        }
        return Task.FromResult<ToolResult>($"Noted: {text}");
    }

    [GeneratedRegex(@"^\s*(?:please\s+)?(?:(?:take|make|add|create|write|start)\s+(?:a\s+)?(?:new\s+|quick\s+)?note|note\s+down|note|jot\s+down|write\s+down|note\s+to\s+self)(?:\s+(?:that|saying|to\s+self))?\s*[:,.-]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Opening();

    /// <summary>"note that …", "take a note: …", "jot down …", "write down …".</summary>
    protected override NoteArguments? Direct(DirectCommand command)
    {
        var original = command.Original;
        var match = Opening().Match(original);
        if (!match.Success || match.Length == 0) return null;
        // "note" alone must be followed by "that" etc., or "notes" questions would match.
        var lead = original[..match.Length].ToLowerInvariant();
        if (lead.Trim().StartsWith("note", StringComparison.Ordinal) && !lead.Contains("that") && !lead.Contains("down")
            && !lead.Contains("self") && !lead.Contains(':')) return null;
        var text = original[match.Length..].Trim().Trim('.').Trim();
        return text.Length == 0 ? null : new NoteArguments(text);
    }
}
