namespace Alfred.Core;

public sealed record FileRequestArguments(string Name, string? Kind = null, string? Period = null, string? Folder = null)
{
    /// <summary>
    /// The query, keeping only what the user actually said: the Mac's model
    /// added periods and kinds nobody mentioned, which would silently narrow the search.
    /// </summary>
    public FileQuery Query(ToolContext context)
    {
        var kind = FileKinds.Parse(Kind);
        var period = FilePeriods.Parse(Period);
        var folder = Folder is null ? null : FileQuery.Folders.GetValueOrDefault(Folder.ToLowerInvariant());
        if (context.Transcript is { } transcript)
        {
            var spoken = SpokenText.Words(transcript);
            if (kind is { } k && !spoken.Any(w => FileKinds.FromSpoken(w) == k)) kind = null;
            if (period is { } p && !p.Phrases().Any(phrase => Grounding.Mentions(phrase, transcript))) period = null;
            if (folder is not null && !Grounding.Mentions(folder, transcript)) folder = null;
        }
        return FileQuery.FromText(Name, kind, period, folder);
    }

    public static FileRequestArguments From(FileQuery query) =>
        new(string.Join(' ', query.Words), query.Kind is { } k ? Camel(k.ToString()) : null, query.Period is { } p ? Camel(p.ToString()) : null, query.Folder);

    public static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => FileQuery.FromText(Name, FileKinds.Parse(Kind), FilePeriods.Parse(Period), Folder).Summary;

    public static IReadOnlyList<ArgumentSpec> Specs { get; } =
    [
        new("name", "Words from the file's name, e.g. \"invoice\" or \"tax return\". Empty if only a type or date was given"),
        new("kind", "File type if said", Required: false, Choices: Enum.GetNames<FileKind>().Select(Camel).ToList()),
        new("period", "When it's from, if said", Required: false, Choices: Enum.GetNames<FilePeriod>().Select(Camel).ToList()),
        new("folder", "Folder if said", Required: false, Choices: ["Desktop", "Documents", "Downloads"]),
    ];
}

internal static class FileTexts
{
    private static readonly HashSet<string> ContentWords = ["mentions", "mentioning", "contains", "containing", "inside", "says", "saying", "about", "read"];

    /// <summary>The agent never reads file contents. A request that depends on them is declined plainly.</summary>
    public static void RefuseContentRequests(ToolContext context)
    {
        if (context.Transcript is { } transcript && SpokenText.Words(transcript).Any(ContentWords.Contains))
            throw new ToolException("I can't look inside files, only at their names, types and dates");
    }

    public static ToolException NotFound(string summary, IReadOnlyList<string> roots) =>
        new($"No file matching “{summary}” in {string.Join(", ", roots.Select(Path.GetFileName))}. I search names and dates, not what's inside files");
}

/// <summary>Opens the best match for a description: the name match first, then the most recent.</summary>
public sealed class OpenFileTool(FileServices files) : AssistantTool<FileRequestArguments>
{
    public override string Name => "openFile";
    public override string Title => "Open file";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "open", "file", "files", "document", "pdf", "my", "show", "screenshot", "photo", "presentation" };

    public override string Description =>
        "Open a file on this PC, found by its name, type and date, never by what is inside it. " +
        "\"open my invoice from last month\" → name \"invoice\", period \"lastMonth\". " +
        "\"open the latest screenshot\" → name \"\", kind \"screenshot\".";

    public override IReadOnlyList<ArgumentSpec> Arguments => FileRequestArguments.Specs;

    protected override string Target(FileRequestArguments a) => a.Summary;

    protected override async Task<ToolResult> ExecuteAsync(FileRequestArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        FileTexts.RefuseContentRequests(context);
        var query = arguments.Query(context);
        var found = await files.SearchAsync(query, 25, cancellationToken);
        if (found.Count == 0) throw FileTexts.NotFound(query.Summary, files.Roots);
        var opened = await files.OpenAsync(found[0], cancellationToken);
        var others = found.Count - 1;
        return others > 0 ? $"{opened} · {others} other match{(others == 1 ? "" : "es")}" : opened;
    }

    private static readonly HashSet<string> Extensions =
        ["pdf", "doc", "docx", "pptx", "ppt", "xls", "xlsx", "csv", "txt", "md", "rtf", "png", "jpg", "jpeg", "heic", "mov", "mp4", "mp3", "m4a", "zip"];

    private static readonly HashSet<string> CommandStarts = WakePhrase.CommandStarts;

    protected override FileRequestArguments? Direct(DirectCommand command)
    {
        if (command.Verb == "open") return FileQuery.FromSpoken(command.Rest) is { } q ? FileRequestArguments.From(q) : null;
        // A bare name with a file type ("Foundations of process mining introduction.PDF") means open it.
        var words = command.Text.Split(' ');
        if (command.Verb.Length > 0 || CommandStarts.Contains(words[0]) || !Extensions.Contains(words[^1]) || words.Length > 10) return null;
        if (FindFilesTool.Prefixes.Any(p => command.Text.StartsWith(p + " ", StringComparison.Ordinal))) return null;
        return FileQuery.FromSpoken(command.Text) is { Kind: not null, Words.Count: > 0 } query ? FileRequestArguments.From(query) : null;
    }
}

/// <summary>Lists the best matches in the pill without opening anything.</summary>
public sealed class FindFilesTool(FileServices files) : AssistantTool<FileRequestArguments>
{
    public const int Limit = 6;
    public static readonly string[] Prefixes = ["show me the list of", "show me a list of", "show me", "list", "find", "where is", "where s", "what s on", "what s in"];

    public override string Name => "findFiles";
    public override string Title => "Find files";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "find", "where", "list", "show", "files", "documents", "screenshots", "photos" };

    public override string Description =>
        "Find files on this PC by name, type and date, never by contents, and list them. " +
        "\"find my tax documents from last year\" → name \"tax\", kind \"document\", period \"lastYear\".";

    public override IReadOnlyList<ArgumentSpec> Arguments => FileRequestArguments.Specs;

    protected override string Target(FileRequestArguments a) => a.Summary;

    protected override async Task<ToolResult> ExecuteAsync(FileRequestArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        FileTexts.RefuseContentRequests(context);
        var query = arguments.Query(context);
        var found = await files.SearchAsync(query, Limit + 1, cancellationToken);
        if (found.Count == 0) throw FileTexts.NotFound(query.Summary, files.Roots);
        // One match: nothing to choose, so open it. Lists are for choosing between several.
        if (found.Count == 1 && context.IsFinalStep) return await files.OpenAsync(found[0], cancellationToken);
        var shown = found.Take(Limit).ToList();
        var count = found.Count > Limit ? $"{Limit}+" : $"{found.Count}";
        return new ToolResult($"Found {count} — click one to open") { Items = files.Register(shown) };
    }

    protected override FileRequestArguments? Direct(DirectCommand command)
    {
        var prefix = Prefixes.FirstOrDefault(p => command.Text.StartsWith(p + " ", StringComparison.Ordinal));
        if (prefix is null) return null;
        var rest = command.Text[(prefix.Length + 1)..];
        // A question ("find out who won the race yesterday") is for the web.
        if (rest.StartsWith("out ", StringComparison.Ordinal) || rest.Split(' ').Any(w => w is "who" or "why" or "how" or "whether" or "if")) return null;
        // Only when the rest itself sounds like files, so "show me the weather" isn't.
        return FileQuery.FromSpoken(rest) is { } query ? FileRequestArguments.From(query) : null;
    }
}

public sealed record OrganiseFilesArguments(string Operation, string Name, string? Kind = null, string? Period = null, string? Folder = null,
    string? NewName = null, string? Destination = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public FileRequestArguments Files => new(Name, Kind, Period, Folder);
}

/// <summary>
/// Rename, move, copy, recycle and create folders. One file changes at once;
/// a batch waits for one yes. Every change goes through the journal, so
/// "undo" reverses it. "Delete" means the Recycle Bin.
/// </summary>
public sealed class OrganiseFilesTool(FileServices files) : AssistantTool<OrganiseFilesArguments>
{
    public override string Name => "organiseFiles";
    public override string Title => "Files";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        "rename", "move", "copy", "trash", "delete", "remove", "bin", "folder", "tidy", "organise", "organize", "name", "recycle",
    };

    public override string Description =>
        "Rename, move, copy or delete files, or create a folder. Deleting means moving to the Recycle Bin (operation \"trash\"). " +
        "\"rename my invoice from last month to Invoice August\" → operation \"rename\", name \"invoice\", period \"lastMonth\", newName \"Invoice August\". " +
        "\"move my screenshots from today to Receipts\" → operation \"move\", kind \"screenshot\", period \"today\", destination \"Receipts\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("operation", "What to do", Choices: ["rename", "move", "copy", "trash", "createFolder"]),
        .. FileRequestArguments.Specs.Select(s => s.Name == "name" ? s with { Description = "Words from the name of the file(s) to change. Empty for createFolder" } : s),
        new("newName", "New name for rename, or the new folder's name for createFolder", Required: false),
        new("destination", "Folder to move or copy into: Desktop, Documents, Downloads, or a folder name", Required: false),
    ];

    /// <summary>
    /// The operation must have been said, as must a new name or destination.
    /// Given "put on something to listen to", the Mac's model chose rename.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> OperationWords = new()
    {
        ["rename"] = ["rename", "name", "call", "called", "retitle"],
        ["move"] = ["move", "put", "tidy", "organise", "organize", "file", "into", "sort"],
        ["copy"] = ["copy", "duplicate"],
        ["trash"] = ["trash", "delete", "remove", "bin", "throw", "rid", "erase", "recycle"],
        ["createFolder"] = ["folder"],
    };

    public static bool IsGrounded(OrganiseFilesArguments arguments, string transcript)
    {
        var words = SpokenText.Words(transcript).ToHashSet();
        if (!OperationWords.TryGetValue(arguments.Operation, out var needed) || !needed.Overlaps(words)) return false;
        foreach (var value in new[] { arguments.NewName, arguments.Destination }.OfType<string>().Where(v => v.Length > 0))
        {
            if (!Grounding.Mentions(value, transcript) && !words.Contains(SpokenText.Normalize(value))) return false;
        }
        return true;
    }

    protected override string Target(OrganiseFilesArguments a) =>
        string.Join(' ', new[] { a.Files.Summary, a.NewName is null ? null : $"→ {a.NewName}", a.Destination is null ? null : $"→ {a.Destination}" }
            .Where(s => !string.IsNullOrEmpty(s)));

    protected override async Task<ToolResult> ExecuteAsync(OrganiseFilesArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        FileTexts.RefuseContentRequests(context);
        if (!Enum.TryParse<FileOperation>(arguments.Operation, ignoreCase: true, out var operation))
            throw new ToolException($"I can't do “{arguments.Operation}” to files");
        if (context.Transcript is { } transcript && !IsGrounded(arguments, transcript))
            throw new ToolException("I didn't catch what to do with which files");
        var organizer = files.Organizer;

        if (operation == FileOperation.CreateFolder)
        {
            var folderName = arguments.NewName ?? arguments.Destination ?? throw new ToolException("What should the folder be called?");
            var parent = Destinations.Root(arguments.Folder, files.Roots) ?? Destinations.DefaultParent(files.Roots);
            return Result(organizer.PlanCreateFolder(folderName, parent));
        }

        var query = arguments.Files.Query(context);
        var found = await files.SearchAsync(query, FileOrganizer.MaxBatch + 1, cancellationToken);
        if (found.Count == 0) throw FileTexts.NotFound(query.Summary, files.Roots);

        switch (operation)
        {
            case FileOperation.Rename:
            {
                var newName = arguments.NewName ?? throw new ToolException("What should it be renamed to?");
                // Rename acts on exactly one file: the only match, or an exact name.
                var exact = found.Where(f => FileRanking.NameScore(f.Name, query.Words) == 3).ToList();
                if (found.Count == 1 || exact.Count == 1) return Result(organizer.PlanRename((found.Count == 1 ? found[0] : exact[0]).Path, newName));
                // Several: let the user pick, with each one's folder shown, rather than guess.
                var choices = (exact.Count > 0 ? exact : found).Take(6).ToList();
                var rows = choices.Select(f => new ResultItem(files.ParkRename(organizer.PlanRename(f.Path, newName)), f.Name,
                    FileScope.Location(f.Path, files.Roots), "doc")).ToList();
                return new ToolResult($"Which {choices[0].Name}? Click the one to rename") { Items = rows };
            }
            case FileOperation.Move or FileOperation.Copy:
            {
                var destination = arguments.Destination ?? throw new ToolException("Where to?");
                var sources = found.Select(f => f.Path).ToList();
                var (folder, create) = await Destinations.ResolveAsync(destination, sources, files, cancellationToken);
                return Result(organizer.PlanTransfer(sources, operation == FileOperation.Copy, folder, create));
            }
            default:
                return Result(organizer.PlanTrash(found.Select(f => f.Path).ToList()));
        }
    }

    /// <summary>One change happens now; a batch is parked for the user's yes.</summary>
    private ToolResult Result(FileChangePlan plan)
    {
        if (!plan.NeedsConfirmation) return new ToolResult(files.Organizer.Apply(plan) + " · say “undo” to reverse") { Undoable = true };
        var rows = plan.Items.Select(i => new ResultItem("row_" + Guid.NewGuid().ToString("N"), Path.GetFileName(i.Source),
            i.Target is { } t ? $"→ {Path.GetFileName(Path.GetDirectoryName(t))}" : "→ Recycle Bin", "doc")).ToList();
        var question = plan.Summary.Replace("Moved", "Move", StringComparison.Ordinal).Replace("Copied", "Copy", StringComparison.Ordinal) + "?";
        return new ToolResult(question) { Items = rows, Confirmation = files.ParkBatch(plan) };
    }

    protected override OrganiseFilesArguments? Direct(DirectCommand command)
    {
        var text = command.Text;
        static OrganiseFilesArguments Make(string operation, FileQuery q, string? newName = null, string? destination = null)
        {
            var f = FileRequestArguments.From(q);
            return new OrganiseFilesArguments(operation, f.Name, f.Kind, f.Period, f.Folder, newName, destination);
        }

        // "create a folder called Taxes (on my desktop)"
        foreach (var prefix in new[]
                 {
                     "create a new folder called ", "create a folder called ", "make a new folder called ", "make a folder called ",
                     "create a folder named ", "make a folder named ", "new folder called ", "new folder named ",
                 })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = text[prefix.Length..];
            string? parent = null;
            foreach (var (phrase, root) in FileQuery.Folders)
            {
                foreach (var preposition in new[] { " on my ", " in my ", " on the ", " in the ", " in ", " on " })
                {
                    if (!rest.EndsWith(preposition + phrase, StringComparison.Ordinal)) continue;
                    rest = rest[..^(preposition + phrase).Length];
                    parent = root;
                }
            }
            return rest.Length == 0 ? null : new OrganiseFilesArguments("createFolder", "", Folder: parent, NewName: rest);
        }

        // "rename my invoice from last month to invoice august"
        if (text.StartsWith("rename ", StringComparison.Ordinal) && text.LastIndexOf(" to ", StringComparison.Ordinal) is var split and > 7)
        {
            var description = text[7..split];
            var newName = text[(split + 4)..];
            var query = FileQuery.FromSpoken(description) ?? FileQuery.FromText(description, null, null);
            return (query.Words.Count > 0 || query.Kind is not null) && newName.Length > 0 ? Make("rename", query, newName: newName) : null;
        }

        // "delete / remove / recycle my …", "move my … to the recycle bin"
        foreach (var prefix in new[] { "move to the recycle bin ", "move to the trash ", "throw away ", "get rid of ", "trash ", "delete ", "remove ", "bin ", "recycle " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return FileQuery.FromSpoken(text[prefix.Length..]) is { } q ? Make("trash", q) : null;
        }
        foreach (var suffix in new[] { " to the recycle bin", " in the recycle bin", " to the trash", " to trash", " to the bin", " in the trash", " in the bin" })
        {
            if (!text.EndsWith(suffix, StringComparison.Ordinal)) continue;
            foreach (var prefix in new[] { "move ", "put ", "send " })
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    return FileQuery.FromSpoken(text[prefix.Length..^suffix.Length]) is { } q ? Make("trash", q) : null;
            }
        }

        // "move / put / copy my … to|into <folder>"
        foreach (var (prefix, operation) in new[] { ("move ", "move"), ("put ", "move"), ("copy ", "copy") })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = text[prefix.Length..];
            var cut = new[] { " into ", " to ", " in " }.Select(s => (Index: rest.LastIndexOf(s, StringComparison.Ordinal), s.Length)).MaxBy(p => p.Index);
            if (cut.Index < 0) return null;
            var destination = Destinations.Clean(rest[(cut.Index + cut.Length)..]);
            return FileQuery.FromSpoken(rest[..cut.Index]) is { } q && destination.Length > 0 ? Make(operation, q, destination: destination) : null;
        }
        return null;
    }
}

public sealed record UndoArguments(string Which);

/// <summary>"Undo that": reverses the most recent file change from the journal.</summary>
public sealed class UndoFileChangeTool(FileServices files) : AssistantTool<UndoArguments>
{
    public override string Name => "undoFileChange";
    public override string Title => "Undo";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "undo", "revert", "back" };
    public override string Description => "Undo the last file change (a rename, move, copy, delete or new folder). \"undo that\" → which \"last\".";
    public override IReadOnlyList<ArgumentSpec> Arguments { get; } = [new("which", "Always \"last\"", Choices: ["last"])];

    protected override string Target(UndoArguments a) => files.Organizer.Journal.LastUndoable?.Summary ?? "Last change";

    protected override Task<ToolResult> ExecuteAsync(UndoArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var last = files.Organizer.Journal.LastUndoable ?? throw new ToolException("There's nothing to undo");
        return Task.FromResult<ToolResult>(files.Organizer.Undo(last.Id));
    }

    protected override UndoArguments? Direct(DirectCommand command) =>
        command.Text is "undo" or "undo that" or "undo it" or "undo the last change" or "undo last change" or "undo the last one"
            or "revert that" or "put it back" ? new UndoArguments("last") : null;
}

/// <summary>Where "…to Receipts" means: a root by name, an existing folder with that name, or a new folder beside the files.</summary>
public static class Destinations
{
    public static string DefaultParent(IReadOnlyList<string> roots) =>
        roots.FirstOrDefault(r => Path.GetFileName(r).Equals("Documents", StringComparison.OrdinalIgnoreCase)) ?? roots[0];

    public static string? Root(string? spoken, IReadOnlyList<string> roots)
    {
        if (spoken is null) return null;
        var key = FileQuery.Folders.GetValueOrDefault(SpokenText.Normalize(spoken)) ?? spoken;
        return roots.FirstOrDefault(r => Path.GetFileName(r).Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"a folder called receipts" → "receipts".</summary>
    public static string Clean(string spoken)
    {
        var text = SpokenText.Normalize(spoken);
        foreach (var filler in new[]
                 {
                     "a new folder called ", "a folder called ", "a new folder named ", "a folder named ", "the folder called ",
                     "the folder named ", "the folder ", "folder called ", "folder named ", "my ", "the ",
                 })
        {
            if (text.StartsWith(filler, StringComparison.Ordinal)) text = text[filler.Length..];
        }
        return text.EndsWith(" folder", StringComparison.Ordinal) ? text[..^" folder".Length] : text;
    }

    /// <summary>(folder, needs creating).</summary>
    public static async Task<(string Folder, bool Create)> ResolveAsync(string spoken, IReadOnlyList<string> sources, FileServices files, CancellationToken cancellationToken)
    {
        var name = Clean(spoken);
        if (Root(name, files.Roots) is { } root) return (root, false);
        var existing = (await files.SearchAsync(FileQuery.FromText(name, FileKind.Folder, null), 10, cancellationToken))
            .FirstOrDefault(f => SpokenText.Key(f.Name) == SpokenText.Key(name));
        if (existing is not null) return (existing.Path, false);
        // New: beside the files if they share a folder, else in Documents.
        var parents = sources.Select(s => Path.GetDirectoryName(s)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parent = parents.Count == 1 ? parents[0] : DefaultParent(files.Roots);
        var title = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
        return (Path.Combine(parent, FileOrganizer.CleanName(title)), true);
    }
}
