namespace Alfred.Core;

/// <summary>
/// Checks that a model-supplied value actually came from what the user said.
/// Small models borrow names from tool-description examples and invent
/// details (a browser, a video ID); tools use this to drop what the user
/// never said instead of acting on it (spec §4, step 7).
/// </summary>
public static class Grounding
{
    /// <summary>
    /// True when every word of <paramref name="phrase"/> appears, in order and
    /// contiguous, in the transcript. Word-level, so "arc" does not match
    /// inside "search".
    /// </summary>
    public static bool Mentions(string phrase, string transcript)
    {
        var needle = SpokenText.Words(phrase);
        var haystack = SpokenText.Words(transcript);
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    /// <summary>
    /// <see cref="Mentions"/>, or the app's last word on its own: people say
    /// "chrome" and "edge", not "Google Chrome" and "Microsoft Edge".
    /// </summary>
    public static bool MentionsApp(string appName, string transcript)
    {
        if (Mentions(appName, transcript)) return true;
        var words = SpokenText.Words(appName);
        return words.Length > 1 && words[^1].Length >= 4 && Mentions(words[^1], transcript);
    }

    /// <summary>True when the user said "my browser", "the browser" and so on.</summary>
    public static bool MentionsGenericBrowser(string transcript) => SpokenText.Words(transcript).Contains("browser");

    /// <summary>
    /// When the model expanded an alias ("vs code" → "Visual Studio Code"),
    /// returns the alias the user actually said. Matched against runs of
    /// whole spoken words, so the alias "code" is not found inside "xcode".
    /// </summary>
    public static string? SpokenAlias(string appName, string transcript, IReadOnlyDictionary<string, string>? aliases = null)
    {
        aliases ??= AppNameMatcher.DefaultAliases;
        var spoken = SpokenText.Words(transcript);
        var runs = new HashSet<string>();
        for (var start = 0; start < spoken.Length; start++)
        {
            for (var end = start; end < Math.Min(start + 3, spoken.Length); end++)
                runs.Add(string.Concat(spoken[start..(end + 1)]));
        }
        var target = SpokenText.Key(appName);
        return aliases
            .Where(a => SpokenText.Key(a.Value) == target && runs.Contains(a.Key))
            .Select(a => a.Key)
            .MaxBy(a => a.Length);
    }

    /// <summary>
    /// True when the URL's path and query were spoken ("github.com/apple").
    /// A bare host is always acceptable: it is just the site's home page.
    /// </summary>
    public static bool IsSpoken(Uri url, string transcript)
    {
        var path = url.AbsolutePath;
        if ((path.Length == 0 || path == "/") && url.Query.Length == 0) return true;
        var host = url.Host.Replace("www.", "", StringComparison.Ordinal);
        return Compact(transcript).Contains(Compact(host + path), StringComparison.Ordinal);
    }

    private static string Compact(string text) => text.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal).Trim('/');
}
