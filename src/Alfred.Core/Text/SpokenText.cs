using System.Globalization;
using System.Text;

namespace Alfred.Core;

/// <summary>
/// The normal form every matcher compares in: lowercased, diacritics folded,
/// file extensions and filler words dropped, punctuation turned into spaces,
/// single-spaced. Port of the Mac's <c>AppNameMatcher.normalize</c>.
/// </summary>
public static class SpokenText
{
    private static readonly string[] Extensions = [".app", ".exe", ".lnk"];

    public static string Normalize(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToLowerInvariant(c));
        }
        var lowered = folded.ToString();
        foreach (var extension in Extensions)
            lowered = lowered.Replace(extension, "", StringComparison.Ordinal);

        var spaced = new string(lowered.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 0 && words[0] == "the") words.RemoveAt(0);
        if (words.Count > 1 && words[^1] is "app" or "application") words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>The normal form without spaces: "VS Code" → "vscode".</summary>
    public static string Key(string text) => Normalize(text).Replace(" ", "", StringComparison.Ordinal);

    public static string[] Words(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Punctuation around the command changes the model's choice ("Open
    /// YouTube." chose openApp where "Open YouTube" chose openURL on the Mac).
    /// Speech recognition adds it inconsistently, so strip it.
    /// </summary>
    public static string Clean(string transcript)
    {
        static bool Strip(char c) => char.IsPunctuation(c) || char.IsWhiteSpace(c);
        var start = 0;
        var end = transcript.Length;
        while (start < end && Strip(transcript[start])) start++;
        while (end > start && Strip(transcript[end - 1])) end--;
        return transcript[start..end];
    }
}
