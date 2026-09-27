using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alfred.Core;

/// <summary>
/// Where file tools may look, and the checks every path must pass before it
/// is acted on. Enforced here in the executor, after canonicalisation, never
/// by prompt. Failing any check refuses the operation; there is no override.
/// </summary>
public static class FileScope
{
    /// <summary>Build output, dependencies and app data: never the user's own files, and changing them breaks things.</summary>
    public static readonly IReadOnlySet<string> DeniedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__", "node_modules", "DerivedData", "site-packages", "venv", "Pods", "Carthage", "build", "target", "bin", "obj",
        "AppData", "Library",
    };

    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The path with ".." removed and every junction or symlink along it
    /// followed, so a link pointing outside a root fails the prefix test.
    /// </summary>
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null) continue;
            current = Canonical(target.FullName);
        }
        return current;
    }

    /// <summary>The canonical path if it may be acted on, otherwise null.</summary>
    public static string? Validated(string path, IReadOnlyList<string> roots)
    {
        string resolved;
        try
        {
            resolved = Canonical(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        var root = roots.FirstOrDefault(r => IsInside(resolved, r));
        if (root is null) return null;
        var relative = resolved[root.TrimEnd(Path.DirectorySeparatorChar).Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var denied = relative.Any(c => c.StartsWith('.') || c.StartsWith('$') || c.EndsWith(".app", PathComparison) || DeniedFolders.Contains(c));
        return denied ? null : resolved;
    }

    /// <summary>Strictly inside the folder, not the folder itself.</summary>
    public static bool IsInside(string path, string folder)
    {
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison) && path.Length > prefix.Length;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), PathComparison);

    /// <summary>"Desktop", or "BDM in Projects": enough to tell same-named files apart.</summary>
    public static string Location(string path, IReadOnlyList<string> roots)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var folderName = Path.GetFileName(folder);
        var parent = Path.GetFileName(Path.GetDirectoryName(folder) ?? "");
        return roots.Any(r => SamePath(r, folder)) || parent.Length == 0 ? folderName : $"{folderName} in {parent}";
    }
}

/// <summary>Searches file metadata. The Windows Search index in the app; a bounded directory walk as the fallback.</summary>
public interface IFileSearch
{
    Task<IReadOnlyList<FoundFile>> SearchAsync(FileQuery query, IReadOnlyList<string> roots, CancellationToken cancellationToken);
}

/// <summary>
/// The Recycle Bin: the only way anything is removed (spec §11). Nothing here
/// can empty it or delete permanently.
/// </summary>
public interface IRecycleBin
{
    /// <summary>Moves an item to the Recycle Bin and returns where it went, for undo.</summary>
    string Recycle(string path);
    /// <summary>Puts a recycled item back where it was.</summary>
    void Restore(string inBin, string original);
    bool Contains(string path);
}

/// <summary>
/// Metadata search by walking the allowed folders, as a fallback when the
/// Windows Search index is off or unreachable. Bounded in depth, count and
/// time; skips everything <see cref="FileScope"/> would refuse.
/// </summary>
public sealed class DirectoryFileSearch : IFileSearch
{
    private const int MaxDepth = 6;
    private const int MaxVisited = 50_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    public Task<IReadOnlyList<FoundFile>> SearchAsync(FileQuery query, IReadOnlyList<string> roots, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<FoundFile>>(() =>
        {
            var found = new List<FoundFile>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var visited = 0;
            var range = query.Period?.Range(DateTime.Now);
            var pending = new Stack<(string Folder, int Depth)>(roots.Where(Directory.Exists).Select(r => (r, 0)));
            while (pending.Count > 0 && visited < MaxVisited && clock.Elapsed < Budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (folder, depth) = pending.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(folder).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var entry in entries)
                {
                    visited++;
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith('.') || name.StartsWith('$') || FileScope.DeniedFolders.Contains(name)) continue;
                    var isFolder = Directory.Exists(entry);
                    if (isFolder && depth < MaxDepth) pending.Push((entry, depth + 1));
                    if (Matches(entry, name, isFolder, query, range)) found.Add(new FoundFile(entry, name, Changed(entry)));
                }
            }
            return found;
        }, cancellationToken);

    private static bool Matches(string path, string name, bool isFolder, FileQuery query, (DateTime Start, DateTime End)? range)
    {
        if (query.Kind == FileKind.Folder ? !isFolder : isFolder) return false;
        if (query.Kind is { } kind and not FileKind.Folder)
        {
            if (!FileKinds.Extensions(kind).Contains(Path.GetExtension(name))) return false;
            if (kind == FileKind.Screenshot && !FileKinds.LooksLikeScreenshot(path)) return false;
        }
        var lower = name.ToLowerInvariant();
        if (!query.Words.All(w => lower.Contains(w, StringComparison.Ordinal))) return false;
        if (range is { } r)
        {
            var date = Changed(path);
            if (date < r.Start || date >= r.End) return false;
        }
        return true;
    }

    private static DateTime Changed(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}

/// <summary>
/// The Windows Search query (SystemIndex over OLE DB) for a file request.
/// Metadata columns only: System.Search.Contents is never queried, so there
/// is no way to match what's inside a file (spec §8, §11). Pure, so the
/// quoting is tested here.
/// </summary>
public static class WindowsSearchSql
{
    public static string Build(FileQuery query, IReadOnlyList<string> roots, int limit = 200)
    {
        var clauses = new List<string>
        {
            "(" + string.Join(" OR ", roots.Select(r => $"SCOPE='file:{Quote(r)}'")) + ")",
        };
        clauses.AddRange(query.Words.Select(w => $"System.FileName LIKE '%{QuoteLike(w)}%'"));
        switch (query.Kind)
        {
            case FileKind.Folder:
                clauses.Add("System.ItemType = 'Directory'");
                break;
            case { } kind:
                clauses.Add("(" + string.Join(" OR ", FileKinds.Extensions(kind).Select(e => $"System.FileExtension = '{Quote(e)}'")) + ")");
                if (kind == FileKind.Screenshot) clauses.Add("(System.FileName LIKE 'Screenshot%' OR System.ItemFolderPathDisplay LIKE '%\\Screenshots%')");
                break;
            default:
                clauses.Add("System.ItemType <> 'Directory'");
                break;
        }
        if (query.Period is { } period)
        {
            var (start, end) = period.Range(DateTime.Now);
            clauses.Add($"System.DateModified >= '{Utc(start)}' AND System.DateModified < '{Utc(end)}'");
        }
        return $"SELECT TOP {limit} System.ItemPathDisplay, System.FileName, System.DateModified FROM SystemIndex WHERE {string.Join(" AND ", clauses)}";
    }

    private static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Words are letters and digits by the time they get here; LIKE's own wildcards are escaped anyway.</summary>
    private static string QuoteLike(string text) =>
        Quote(text).Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);

    private static string Utc(DateTime local) => local.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// The undo journal. Every change is written here, with how to reverse it,
/// before it is made, and the file is on disk rather than in memory, so undo
/// survives a crash.
/// </summary>
public sealed class FileJournal
{
    public enum Status { Pending, Done, Undone }

    public enum StepKind { Move, Copy, Recycle, CreateFolder }

    /// <summary>One change and everything needed to reverse it.</summary>
    /// <param name="From">Move: the original path. Recycle: the original path. Copy/CreateFolder: unused.</param>
    /// <param name="To">Move: the new path. Copy: the created copy. Recycle: where it went in the bin. CreateFolder: the folder.</param>
    public sealed record Step(StepKind Kind, string? From, string To);

    public sealed record Entry
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public DateTime Date { get; init; } = DateTime.Now;
        public string Summary { get; init; } = "";
        public List<Step> Steps { get; init; } = [];
        public Status State { get; set; } = Status.Pending;
    }

    public const int Keep = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<Entry> _entries;

    public FileJournal(string file)
    {
        _file = file;
        try
        {
            _entries = File.Exists(file) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), Json) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _entries = [];
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Entry> History
    {
        get
        {
            lock (_lock) return _entries.AsEnumerable().Reverse().Select(e => e with { Steps = [.. e.Steps] }).ToList();
        }
    }

    public Guid Begin(string summary)
    {
        var entry = new Entry { Summary = summary };
        Update(entries => entries.Add(entry));
        return entry.Id;
    }

    /// <summary>Records one step before performing it.</summary>
    public void Record(Step step, Guid id) => Update(entries => entries.Find(e => e.Id == id)?.Steps.Add(step));

    /// <summary>Replaces the last step (a recycled item's bin location is only known after).</summary>
    public void AmendLast(Step step, Guid id) => Update(entries =>
    {
        if (entries.Find(e => e.Id == id) is { Steps.Count: > 0 } entry) entry.Steps[^1] = step;
    });

    public void SetStatus(Status status, Guid id) => Update(entries =>
    {
        if (entries.Find(e => e.Id == id) is { } entry) entry.State = status;
    });

    /// <summary>The most recent entry that can still be undone.</summary>
    public Entry? LastUndoable
    {
        get
        {
            lock (_lock) return _entries.LastOrDefault(e => e.State != Status.Undone && e.Steps.Count > 0);
        }
    }

    public Entry? Find(Guid id)
    {
        lock (_lock) return _entries.Find(e => e.Id == id);
    }

    private void Update(Action<List<Entry>> change)
    {
        string json;
        lock (_lock)
        {
            change(_entries);
            if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
            json = JsonSerializer.Serialize(_entries, Json);
        }
        // Atomic: a crash mid-write can't leave half a journal. If the journal
        // can't be written, the change must not go ahead: this throws.
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, json, Encoding.UTF8);
        File.Move(temporary, _file, overwrite: true);
    }
}
