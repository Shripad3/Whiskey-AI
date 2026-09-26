using System.Globalization;

namespace Alfred.Core;

public enum FileKind { Document, Pdf, Image, Screenshot, Video, Audio, Spreadsheet, Presentation, Folder }

public enum FilePeriod { Today, Yesterday, ThisWeek, LastWeek, ThisMonth, LastMonth, ThisYear, LastYear }

public static class FileKinds
{
    public static FileKind? FromSpoken(string word) => word switch
    {
        "pdf" or "pdfs" => FileKind.Pdf,
        "document" or "documents" or "doc" or "docs" => FileKind.Document,
        "photo" or "photos" or "picture" or "pictures" or "image" or "images" => FileKind.Image,
        "screenshot" or "screenshots" => FileKind.Screenshot,
        "video" or "videos" or "movie" or "movies" or "recording" => FileKind.Video,
        "audio" or "podcast" => FileKind.Audio,
        "spreadsheet" or "spreadsheets" or "excel" or "sheet" => FileKind.Spreadsheet,
        "presentation" or "presentations" or "slides" or "deck" or "powerpoint" => FileKind.Presentation,
        "folder" or "folders" => FileKind.Folder,
        _ => null,
    };

    public static FileKind? Parse(string? name) =>
        name is not null && Enum.TryParse<FileKind>(name, ignoreCase: true, out var kind) ? kind : null;

    /// <summary>File extensions of each kind, for the index query and the directory walk.</summary>
    public static IReadOnlySet<string> Extensions(FileKind kind) => kind switch
    {
        FileKind.Pdf => Set(".pdf"),
        FileKind.Document => Set(".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".pdf", ".pages"),
        FileKind.Image or FileKind.Screenshot => Set(".png", ".jpg", ".jpeg", ".gif", ".bmp", ".heic", ".webp", ".tif", ".tiff"),
        FileKind.Video => Set(".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm"),
        FileKind.Audio => Set(".mp3", ".m4a", ".wav", ".flac", ".aac", ".ogg", ".wma"),
        FileKind.Spreadsheet => Set(".xlsx", ".xls", ".csv", ".ods", ".numbers"),
        FileKind.Presentation => Set(".pptx", ".ppt", ".odp", ".key"),
        _ => Set(),
    };

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    /// <summary>Screenshots, however they were taken: Windows' Snipping Tool and Print Screen name them so.</summary>
    public static bool LooksLikeScreenshot(string path) =>
        Path.GetFileName(path).StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).StartsWith("Screen Shot", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}Screenshots{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}

public static class FilePeriods
{
    public static FilePeriod? Parse(string? name) =>
        name is not null && Enum.TryParse<FilePeriod>(name, ignoreCase: true, out var period) ? period : null;

    public static string[] Phrases(this FilePeriod period) => period switch
    {
        FilePeriod.Today => ["from today", "today"],
        FilePeriod.Yesterday => ["from yesterday", "yesterday"],
        FilePeriod.ThisWeek => ["from this week", "this week"],
        FilePeriod.LastWeek => ["from last week", "last week"],
        FilePeriod.ThisMonth => ["from this month", "this month"],
        FilePeriod.LastMonth => ["from last month", "last month"],
        FilePeriod.ThisYear => ["from this year", "this year"],
        _ => ["from last year", "last year"],
    };

    /// <summary>[start, end) in local time.</summary>
    public static (DateTime Start, DateTime End) Range(this FilePeriod period, DateTime now)
    {
        var today = now.Date;
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var week = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var year = new DateTime(today.Year, 1, 1);
        return period switch
        {
            FilePeriod.Today => (today, today.AddDays(1)),
            FilePeriod.Yesterday => (today.AddDays(-1), today),
            FilePeriod.ThisWeek => (week, week.AddDays(7)),
            FilePeriod.LastWeek => (week.AddDays(-7), week),
            FilePeriod.ThisMonth => (month, month.AddMonths(1)),
            FilePeriod.LastMonth => (month.AddMonths(-1), month),
            FilePeriod.ThisYear => (year, year.AddYears(1)),
            _ => (year.AddYears(-1), year),
        };
    }
}

/// <summary>
/// What a file request says: name words, an optional kind and time period.
/// Everything here is metadata; there is no way to express "a file that
/// mentions X", because the agent never reads file contents (spec §11).
/// </summary>
public sealed record FileQuery(IReadOnlyList<string> Words, FileKind? Kind = null, FilePeriod? Period = null, string? Folder = null)
{
    private static readonly HashSet<string> StopWords =
    [
        "my", "the", "a", "an", "file", "files", "from", "of", "that", "this", "latest", "recent", "last",
        "called", "named", "list", "all", "every", "in", "on", "at", "folder", "s", "show", "me", "any",
        // "Everything in Downloads" is every file there, not one named "everything".
        "everything", "anything", "stuff", "things",
    ];

    /// <summary>Spoken folder names → allowed root folder names.</summary>
    public static readonly IReadOnlyDictionary<string, string> Folders = new Dictionary<string, string>
    {
        ["desktop"] = "Desktop", ["documents"] = "Documents", ["document folder"] = "Documents",
        ["downloads"] = "Downloads", ["download folder"] = "Downloads",
    };

    /// <summary>
    /// From the model's free-text name, e.g. "tax invoice". With a kind, words
    /// naming that kind are dropped: "screenshots" would not match files called "Screenshot …".
    /// </summary>
    public static FileQuery FromText(string text, FileKind? kind, FilePeriod? period, string? folder = null) =>
        new(SpokenText.Words(text).Where(w => w.Length >= 2 && !StopWords.Contains(w))
            .Where(w => kind is null || FileKinds.FromSpoken(w) != kind).ToList(), kind, period, folder);

    /// <summary>True when there is anything to search by.</summary>
    public bool IsSpecific => Words.Count > 0 || Kind is not null || Period is not null || Folder is not null;

    /// <summary>
    /// Parses a spoken description directly: "my invoice pdf from last month".
    /// Null unless it clearly describes a file (a leading "my", a file kind, a
    /// time period or a folder), so "open spotify" is never mistaken for one.
    /// </summary>
    public static FileQuery? FromSpoken(string spoken)
    {
        var text = " " + SpokenText.Normalize(spoken) + " ";
        string? folder = null;
        foreach (var (phrase, root) in Folders)
        {
            foreach (var preposition in new[] { "in my", "on my", "in the", "on the", "in", "on", "from my", "from the" })
            {
                if (!text.Contains($" {preposition} {phrase} ", StringComparison.Ordinal)) continue;
                folder = root;
                text = text.Replace($" {preposition} {phrase} ", " ", StringComparison.Ordinal);
            }
        }
        FilePeriod? period = null;
        foreach (var candidate in Enum.GetValues<FilePeriod>())
        {
            foreach (var phrase in candidate.Phrases())
            {
                if (!text.Contains($" {phrase} ", StringComparison.Ordinal)) continue;
                period = candidate;
                text = text.Replace($" {phrase} ", " ", StringComparison.Ordinal);
            }
        }
        FileKind? kind = null;
        var words = new List<string>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (kind is null && FileKinds.FromSpoken(word) is { } match) kind = match;
            else words.Add(word);
        }
        var describesFile = words.FirstOrDefault() == "my" || words.Contains("file") || words.Contains("files")
                            || kind is not null || period is not null || folder is not null;
        if (!describesFile) return null;
        var query = FromText(string.Join(' ', words), kind, period, folder);
        return query.IsSpecific ? query : null;
    }

    public string Summary => string.Join(" · ", new[]
    {
        Words.Count == 0 ? null : string.Join(' ', Words),
        Kind?.ToString().ToLowerInvariant(),
        Period?.Phrases()[^1],
        Folder is null ? null : $"in {Folder}",
    }.OfType<string>());
}

/// <summary>One search result. The path never leaves the executor: the model and the UI see only the name and date.</summary>
public sealed record FoundFile(string Path, string Name, DateTime Date);

public static class FileRanking
{
    /// <summary>Best first: how well the name matches the words, then most recent.</summary>
    public static List<FoundFile> Rank(IEnumerable<FoundFile> files, FileQuery query) =>
        files.OrderByDescending(f => NameScore(f.Name, query.Words)).ThenByDescending(f => f.Date).ToList();

    /// <summary>
    /// 3: the name is exactly the words. 2: every word is a whole word of the
    /// name. 1: the words appear inside the name. 0: no words to match. A
    /// spoken extension counts: "test txt" is exactly "test.txt".
    /// </summary>
    public static int NameScore(string name, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return 0;
        var stemWords = SpokenText.Words(Path.GetFileNameWithoutExtension(name));
        var fullWords = SpokenText.Words(name.Replace('.', ' '));
        if (stemWords.SequenceEqual(words) || fullWords.SequenceEqual(words)) return 3;
        return words.All(fullWords.Contains) ? 2 : 1;
    }
}
