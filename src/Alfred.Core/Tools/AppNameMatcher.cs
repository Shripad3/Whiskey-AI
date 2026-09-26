namespace Alfred.Core;

/// <summary>
/// Resolves a spoken app name against installed app names. Pure, so the
/// spoken-name table in the tests covers it without touching the system.
/// When no match is confident, returns null rather than guessing.
/// </summary>
public static class AppNameMatcher
{
    /// <summary>Keys are compact keys (see <see cref="SpokenText.Key"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultAliases = new Dictionary<string, string>
    {
        ["vscode"] = "Visual Studio Code",
        ["code"] = "Visual Studio Code",
        ["explorer"] = "File Explorer",
        ["files"] = "File Explorer",
        ["store"] = "Microsoft Store",
        ["appstore"] = "Microsoft Store",
        ["taskmanager"] = "Task Manager",
        ["omen"] = "OMEN Gaming Hub",
    };

    private static readonly HashSet<string> GenericBrowserKeys = ["browser", "mybrowser", "webbrowser", "defaultbrowser", "thebrowser"];

    public static bool IsGenericBrowser(string spoken) => GenericBrowserKeys.Contains(SpokenText.Key(spoken));

    public static string? Match(string spoken, IReadOnlyList<string> candidates, IReadOnlyDictionary<string, string>? aliases = null)
    {
        aliases ??= DefaultAliases;
        var query = SpokenText.Normalize(spoken);
        if (query.Length == 0) return null;
        if (aliases.TryGetValue(query.Replace(" ", ""), out var alias))
            query = SpokenText.Normalize(alias);
        var queryKey = query.Replace(" ", "");

        // 1. Exact, ignoring case, punctuation and spacing.
        var exact = candidates.FirstOrDefault(c => SpokenText.Key(c) == queryKey);
        if (exact is not null) return exact;

        // 2. Every spoken word appears as a whole word in the app name
        //    ("chrome" → "Google Chrome"). Among several, the one with the
        //    fewest extra words wins, but only if it is strictly shortest.
        var queryWords = query.Split(' ');
        var wordMatches = candidates
            .Where(c => queryWords.All(new HashSet<string>(SpokenText.Words(c)).Contains))
            .ToList();
        if (UniqueShortest(wordMatches) is { } best) return best;

        // 3. Close spelling, for mis-transcriptions ("spotfy"). Requires a
        //    clear winner so near-ties fail instead of launching the wrong app.
        if (queryKey.Length < 4) return null;
        var scored = candidates
            .Select(c => (Name: c, Score: Similarity(queryKey, SpokenText.Key(c))))
            .OrderByDescending(s => s.Score)
            .ToList();
        if (scored.Count == 0 || scored[0].Score < 0.8) return null;
        if (scored.Count > 1 && scored[0].Score - scored[1].Score < 0.1) return null;
        return scored[0].Name;
    }

    private static string? UniqueShortest(List<string> names)
    {
        var counted = names.Select(n => (Name: n, Words: SpokenText.Words(n).Length)).OrderBy(n => n.Words).ToList();
        if (counted.Count == 0) return null;
        if (counted.Count > 1 && counted[1].Words == counted[0].Words) return null;
        return counted[0].Name;
    }

    internal static double Similarity(string a, string b)
    {
        var longest = Math.Max(a.Length, b.Length);
        return longest == 0 ? 1 : 1 - (double)Levenshtein(a, b) / longest;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            previous = current;
        }
        return previous[b.Length];
    }
}
