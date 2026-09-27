namespace Alfred.Core;

public enum FileOperation { Rename, Move, Copy, Trash, CreateFolder }

/// <summary>
/// A checked, not-yet-applied change. Everything that could be refused has
/// been refused by the time one of these exists: paths are canonical and
/// inside the roots, no target exists (names are auto-suffixed instead),
/// moves stay on one drive, and there are at most 20 items.
/// </summary>
public sealed record FileChangePlan(FileOperation Operation, string Summary, IReadOnlyList<FileChangePlan.Item> Items, string? CreateFolder)
{
    /// <param name="Target">Null for the Recycle Bin.</param>
    public sealed record Item(string Source, string? Target);

    /// <summary>One item happens at once; a batch is shown for one confirmation.</summary>
    public bool NeedsConfirmation => Items.Count > 1;
}

/// <summary>
/// Plans, applies and undoes file changes. Never reads or edits a file's
/// contents and never deletes permanently: the only removal is the Recycle
/// Bin, and nothing here can empty it (spec §11).
/// </summary>
public sealed class FileOrganizer(Func<IReadOnlyList<string>> roots, FileJournal journal, IRecycleBin bin)
{
    public const int MaxBatch = 20;

    public FileJournal Journal => journal;

    // Planning

    public FileChangePlan PlanRename(string file, string spokenName)
    {
        var source = CheckedSource(file);
        var name = Renamed(Path.GetFileName(source), spokenName);
        if (name == Path.GetFileName(source)) throw new ToolException($"It's already called {name}");
        var target = UniqueTarget(Path.GetDirectoryName(source)!, name, new HashSet<string>());
        return new FileChangePlan(FileOperation.Rename, $"Renamed {Path.GetFileName(source)} to {Path.GetFileName(target)}", [new(source, target)], null);
    }

    /// <summary><paramref name="destination"/> is a folder that exists, or will be created when <paramref name="create"/> is true.</summary>
    public FileChangePlan PlanTransfer(IReadOnlyList<string> files, bool copying, string destination, bool create)
    {
        if (files.Count == 0) throw new ToolException($"There are no files to {(copying ? "copy" : "move")}");
        if (files.Count > MaxBatch) throw TooMany(files.Count);
        var folder = CheckedFolder(destination, mustExist: !create);
        var items = new List<FileChangePlan.Item>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var source = CheckedSource(file);
            if (!copying)
            {
                if (FileScope.SamePath(Path.GetDirectoryName(source)!, folder))
                    throw new ToolException($"{Path.GetFileName(source)} is already in {Path.GetFileName(folder)}");
                if (FileScope.IsInside(folder, source)) throw new ToolException($"Can't move {Path.GetFileName(source)} into itself");
                // Moves between drives are copies and deletes: not reversible.
                if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase))
                    throw new ToolException($"{Path.GetFileName(source)} is on a different drive; I only move files within one drive");
            }
            var target = UniqueTarget(folder, Path.GetFileName(source), reserved);
            reserved.Add(Path.GetFileName(target));
            items.Add(new(source, target));
        }
        var what = items.Count == 1 ? Path.GetFileName(items[0].Source) : $"{items.Count} files";
        return new FileChangePlan(copying ? FileOperation.Copy : FileOperation.Move,
            $"{(copying ? "Copied" : "Moved")} {what} to {Path.GetFileName(folder)}", items, create ? folder : null);
    }

    public FileChangePlan PlanTrash(IReadOnlyList<string> files)
    {
        if (files.Count == 0) throw new ToolException("There are no files to move to the Recycle Bin");
        if (files.Count > MaxBatch) throw TooMany(files.Count);
        var items = files.Select(f => new FileChangePlan.Item(CheckedSource(f), null)).ToList();
        var what = items.Count == 1 ? Path.GetFileName(items[0].Source) : $"{items.Count} files";
        return new FileChangePlan(FileOperation.Trash, $"Moved {what} to the Recycle Bin", items, null);
    }

    public FileChangePlan PlanCreateFolder(string spokenName, string parent)
    {
        var folder = CheckedFolder(parent, mustExist: true);
        var target = UniqueTarget(folder, CleanName(spokenName), new HashSet<string>());
        return new FileChangePlan(FileOperation.CreateFolder, $"Created the folder {Path.GetFileName(target)} in {Path.GetFileName(folder)}", [], target);
    }

    // Applying

    /// <summary>Journals each step before making it. On failure, reverses the steps already made: all or nothing.</summary>
    public string Apply(FileChangePlan plan)
    {
        var id = journal.Begin(plan.Summary);
        try
        {
            if (plan.CreateFolder is { } created)
            {
                journal.Record(new(FileJournal.StepKind.CreateFolder, null, created), id);
                Directory.CreateDirectory(created);
            }
            foreach (var item in plan.Items)
            {
                switch (plan.Operation)
                {
                    case FileOperation.Rename or FileOperation.Move when item.Target is { } target:
                        journal.Record(new(FileJournal.StepKind.Move, item.Source, target), id);
                        MoveItem(item.Source, target);
                        break;
                    case FileOperation.Copy when item.Target is { } copy:
                        journal.Record(new(FileJournal.StepKind.Copy, item.Source, copy), id);
                        CopyItem(item.Source, copy);
                        break;
                    case FileOperation.Trash:
                        // Recorded first with a placeholder, then with where it went.
                        journal.Record(new(FileJournal.StepKind.Recycle, item.Source, item.Source), id);
                        var recycled = bin.Recycle(item.Source);
                        journal.AmendLast(new(FileJournal.StepKind.Recycle, item.Source, recycled), id);
                        break;
                }
            }
            journal.SetStatus(FileJournal.Status.Done, id);
            return plan.Summary;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ToolException)
        {
            try { Undo(id); }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException or ToolException) { }
            throw new ToolException($"Couldn't finish ({e.Message}). Nothing was changed");
        }
    }

    /// <summary>Reverses an entry, newest step first. A step whose files have changed since is left alone rather than forced.</summary>
    public string Undo(Guid id)
    {
        var entry = journal.Find(id) ?? throw new ToolException("There's nothing to undo");
        if (entry.State == FileJournal.Status.Undone) throw new ToolException("That was already undone");
        var skipped = 0;
        foreach (var step in entry.Steps.AsEnumerable().Reverse())
        {
            switch (step.Kind)
            {
                case FileJournal.StepKind.Move when step.From is { } from:
                    if (!Allowed(from) || !Allowed(step.To) || !Exists(step.To) || Exists(from)) { skipped++; continue; }
                    MoveItem(step.To, from);
                    break;
                case FileJournal.StepKind.Copy:
                    if (!Allowed(step.To) || !Exists(step.To)) { skipped++; continue; }
                    bin.Recycle(step.To);
                    break;
                case FileJournal.StepKind.Recycle when step.From is { } original:
                    if (!Allowed(original) || FileScope.SamePath(step.To, original) || !bin.Contains(step.To) || !Exists(step.To) || Exists(original))
                    {
                        skipped++;
                        continue;
                    }
                    bin.Restore(step.To, original);
                    break;
                case FileJournal.StepKind.CreateFolder:
                    if (!Allowed(step.To) || !Directory.Exists(step.To)
                        || Directory.EnumerateFileSystemEntries(step.To).Any(e => Path.GetFileName(e) is not ("desktop.ini" or "Thumbs.db")))
                    {
                        skipped++;
                        continue;
                    }
                    bin.Recycle(step.To);
                    break;
            }
        }
        journal.SetStatus(FileJournal.Status.Undone, id);
        var note = skipped == 0 ? "" : $" ({skipped} item{(skipped == 1 ? " had" : "s had")} changed since and {(skipped == 1 ? "was" : "were")} left alone)";
        return $"Undid: {entry.Summary}{note}";
    }

    // Checks

    private string CheckedSource(string file)
    {
        var source = FileScope.Validated(file, roots()) ?? throw new ToolException($"{Path.GetFileName(file)} is outside the folders I may change");
        if (!Exists(source)) throw new ToolException($"{Path.GetFileName(source)} no longer exists");
        // A OneDrive file that isn't on this PC can't be moved back reliably.
        if ((File.GetAttributes(source) & (FileAttributes)0x00400000) != 0) // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
            throw new ToolException($"{Path.GetFileName(source)} is in OneDrive but not downloaded; open it once first");
        return source;
    }

    /// <summary>A folder inside the roots, or a root itself.</summary>
    private string CheckedFolder(string path, bool mustExist)
    {
        var folder = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var isRoot = roots().Any(r => FileScope.SamePath(r, folder));
        if (!isRoot)
        {
            var parent = Path.GetDirectoryName(folder);
            // A folder that doesn't exist yet is checked by its parent and name.
            var ok = Directory.Exists(folder)
                ? FileScope.Validated(folder, roots()) is not null
                : parent is not null && (roots().Any(r => FileScope.SamePath(r, parent)) || FileScope.Validated(parent, roots()) is not null)
                  && !Path.GetFileName(folder).StartsWith('.');
            if (!ok) throw new ToolException($"{Path.GetFileName(folder)} is outside the folders I may change");
            if (Directory.Exists(folder)) folder = FileScope.Canonical(folder);
        }
        var exists = Directory.Exists(folder);
        if (mustExist && !exists) throw new ToolException($"There's no folder called {Path.GetFileName(folder)}");
        if (!mustExist && (exists || File.Exists(folder))) throw new ToolException($"{Path.GetFileName(folder)} already exists");
        return folder;
    }

    /// <summary>
    /// Whether undo may touch this path. It often doesn't exist yet (it is where
    /// a file is being put back), so the check is on its parent plus the name.
    /// </summary>
    private bool Allowed(string path)
    {
        if (bin.Contains(path)) return true;
        var parent = Path.GetDirectoryName(path);
        if (parent is null) return false;
        var parentAllowed = roots().Any(r => FileScope.SamePath(r, parent)) || FileScope.Validated(parent, roots()) is not null;
        var name = Path.GetFileName(path);
        return parentAllowed && !name.StartsWith('.') && !name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MoveItem(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: false);
    }

    private static void CopyItem(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Copy(from, to, overwrite: false);
            return;
        }
        Directory.CreateDirectory(to);
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            CopyItem(entry, Path.Combine(to, Path.GetFileName(entry)));
    }

    /// <summary>Never overwrites: a taken name gets " 2", " 3", … before its extension.</summary>
    public static string UniqueTarget(string folder, string name, IReadOnlySet<string> reserved)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = name;
        for (var number = 2; reserved.Contains(candidate) || Exists(Path.Combine(folder, candidate)); number++)
            candidate = $"{stem} {number}{extension}";
        return Path.Combine(folder, candidate);
    }

    private static ToolException TooMany(int count) => new($"That's {count} files; I change at most {MaxBatch} at a time. Narrow it down");

    // Names (pure)

    /// <summary>The extension is kept unless the new name gives one ("report.txt").</summary>
    public static string Renamed(string current, string spoken)
    {
        var name = CleanName(spoken);
        var currentExtension = Path.GetExtension(current);
        var spokenExtension = Path.GetExtension(name).TrimStart('.');
        var explicitExtension = spokenExtension.Length is > 0 and <= 5 && spokenExtension.All(char.IsLetterOrDigit);
        return explicitExtension || currentExtension.Length == 0 ? name : name + currentExtension;
    }

    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string CleanName(string spoken)
    {
        var name = spoken.Trim().Trim('"', '“', '”', '\'').Trim().TrimEnd('.');
        if (name.Length == 0 || name.Length > 200 || name.StartsWith('.') || name.IndexOfAny(Forbidden) >= 0
            || name.Any(char.IsControl) || Reserved.Contains(Path.GetFileNameWithoutExtension(name)))
            throw new ToolException($"“{spoken}” isn't a name I can use");
        return name;
    }
}

/// <summary>
/// Everything the file tools share: the allowed roots, search, the organiser,
/// and the tokens that stand in for paths. Tokens are cleared at the start of
/// every activation, so a token from an earlier command can't be reused.
/// </summary>
public sealed class FileServices(Func<IReadOnlyList<string>> roots, IFileSearch search, FileOrganizer organizer, Func<string, Task> open)
{
    private readonly object _lock = new();
    private Dictionary<string, string> _tokens = [];
    private readonly Dictionary<string, FileChangePlan> _renames = [];
    private (string Token, FileChangePlan Plan)? _pending;

    public IReadOnlyList<string> Roots => roots();
    public FileOrganizer Organizer => organizer;

    public static readonly string[] YesWords = ["yes", "yeah", "yep", "sure", "confirm", "do it", "go ahead", "ok", "okay", "yes please"];
    public static readonly string[] NoWords = ["no", "nope", "cancel", "don t", "stop", "never mind", "no thanks"];

    /// <summary>Where the tools may not look any more: forget every token and pending change.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _tokens = [];
            _renames.Clear();
        }
    }

    public async Task<IReadOnlyList<FoundFile>> SearchAsync(FileQuery query, int limit, CancellationToken cancellationToken)
    {
        if (!query.IsSpecific) throw new ToolException("Which file? Say part of its name, its type, where it is or when it's from");
        var scoped = query.Folder is { } folder ? Roots.Where(r => Path.GetFileName(r).Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList() : Roots.ToList();
        IReadOnlyList<FoundFile> found;
        using (var limitTime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limitTime.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                found = await search.SearchAsync(query, scoped, limitTime.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ToolException("The file search took too long");
            }
        }
        var valid = found.Select(f => FileScope.Validated(f.Path, scoped) is { } path ? f with { Path = path } : null).OfType<FoundFile>()
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase);
        return FileRanking.Rank(valid, query).Take(limit).ToList();
    }

    /// <summary>Opens a found file, validating its path again at the moment of use.</summary>
    public async Task<string> OpenAsync(FoundFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FileScope.Validated(file.Path, Roots) ?? throw new ToolException("That file is outside the folders I may open");
        await open(path);
        return $"Opened {file.Name}";
    }

    /// <summary>Replaces the token table with these files and returns their rows.</summary>
    public IReadOnlyList<ResultItem> Register(IReadOnlyList<FoundFile> files)
    {
        var entries = new Dictionary<string, string>();
        var items = files.Select(file =>
        {
            var token = "file_" + Guid.NewGuid().ToString("N")[..8];
            entries[token] = file.Path;
            return new ResultItem(token, file.Name, Ago(file.Date), Symbol(file.Name));
        }).ToList();
        lock (_lock) _tokens = entries;
        return items;
    }

    public string ParkRename(FileChangePlan plan)
    {
        var token = "rename_" + Guid.NewGuid().ToString("N")[..8];
        lock (_lock) _renames[token] = plan;
        return token;
    }

    public string ParkBatch(FileChangePlan plan)
    {
        var token = "change_" + Guid.NewGuid().ToString("N")[..8];
        lock (_lock) _pending = (token, plan);
        return token;
    }

    /// <summary>Acting on a selected result row, by token kind.</summary>
    public async Task<string> SelectAsync(string id)
    {
        if (id.StartsWith("file_", StringComparison.Ordinal))
        {
            string? path;
            lock (_lock) path = _tokens.GetValueOrDefault(id);
            if (path is null) throw new ToolException("That list has expired; ask again");
            return await OpenAsync(new FoundFile(path, Path.GetFileName(path), DateTime.MinValue), CancellationToken.None);
        }
        if (id.StartsWith("rename_", StringComparison.Ordinal))
        {
            FileChangePlan? plan;
            lock (_lock)
            {
                plan = _renames.GetValueOrDefault(id);
                _renames.Clear();
            }
            if (plan is null) throw new ToolException("That list has expired; ask again");
            return organizer.Apply(plan) + " · say “undo” to reverse";
        }
        throw new ToolException("That item can't be opened");
    }

    /// <summary>True for yes, false for no, null when it's neither (a new command).</summary>
    public static bool? Answer(string transcript)
    {
        var text = SpokenText.Normalize(transcript);
        if (YesWords.Contains(text)) return true;
        if (NoWords.Contains(text)) return false;
        return null;
    }

    public string Confirm(string token)
    {
        FileChangePlan? plan = null;
        lock (_lock)
        {
            if (_pending is { } parked && parked.Token == token) plan = parked.Plan;
            _pending = null;
        }
        if (plan is null) throw new ToolException("That change has expired; ask again");
        return organizer.Apply(plan) + " · say “undo” to reverse";
    }

    public void Discard()
    {
        lock (_lock) _pending = null;
    }

    private static string Ago(DateTime date)
    {
        if (date == DateTime.MinValue) return "";
        var span = DateTime.Now - date;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        return span.TotalDays < 30 ? $"{(int)span.TotalDays} days ago" : date.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Symbol(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".heic" or ".gif" or ".webp" or ".bmp" => "photo",
        ".pdf" => "pdf",
        ".mov" or ".mp4" or ".mkv" or ".avi" => "video",
        ".mp3" or ".m4a" or ".wav" or ".flac" => "audio",
        ".pptx" or ".ppt" or ".key" => "slides",
        ".xlsx" or ".xls" or ".csv" => "table",
        "" => "folder",
        _ => "doc",
    };
}
