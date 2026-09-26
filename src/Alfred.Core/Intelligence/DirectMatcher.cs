using System.Text.RegularExpressions;

namespace Alfred.Core;

/// <summary>
/// A normalised command for tools' direct matching: "Please open YouTube in
/// Chrome" → text "open youtube in chrome", verb "open", rest "youtube in
/// chrome". Commands without a known leading verb ("mute") have an empty verb
/// and the whole text as rest.
/// </summary>
public sealed record DirectCommand
{
    internal static readonly string[] Verbs = ["go to", "look up", "search for", "open", "launch", "start", "search", "google", "play", "watch"];
    private static readonly string[] Fillers = ["please ", "can you ", "could you ", "hey "];

    public string Text { get; }
    public string Verb { get; }
    public string Rest { get; }
    /// <summary>The transcript as said, for tools that parse "7:30", which <see cref="Text"/> splits apart.</summary>
    public string Original { get; }

    private DirectCommand(string text, string verb, string rest, string original) =>
        (Text, Verb, Rest, Original) = (text, verb, rest, original);

    public static DirectCommand? Parse(string transcript)
    {
        var text = SpokenText.Normalize(transcript);
        foreach (var filler in Fillers)
        {
            if (text.StartsWith(filler, StringComparison.Ordinal)) text = text[filler.Length..];
        }
        if (text.EndsWith(" please", StringComparison.Ordinal)) text = text[..^" please".Length];
        if (text.Length == 0) return null;
        var verb = Verbs.FirstOrDefault(v => text.StartsWith(v + " ", StringComparison.Ordinal));
        return verb is null
            ? new DirectCommand(text, "", text, transcript)
            : new DirectCommand(text, verb, text[(verb.Length + 1)..].Trim(), transcript);
    }

    /// <summary>Splits "youtube in chrome" into ("youtube", "chrome").</summary>
    public (string Thing, string? Browser) Target
    {
        get
        {
            var index = Rest.LastIndexOf(" in ", StringComparison.Ordinal);
            return index < 0 ? (Rest, null) : (Rest[..index], Rest[(index + 4)..]);
        }
    }
}

/// <summary>
/// Recognises simple single-action commands without the model. A small model
/// is not deterministic: on the Mac, "Open YouTube" gave openURL once and
/// webSearch twice in a row. The common cases have to be reliable, so they
/// never reach it. Anything not matched goes to the model.
/// </summary>
public static partial class DirectMatcher
{
    public static Plan? Match(string transcript, IReadOnlyList<IAssistantTool> tools)
    {
        if (DirectCommand.Parse(transcript) is not { } command) return null;
        // Compound commands are several steps; only the model plans those.
        if (IsCompound(command.Text)) return null;
        // Registry order decides ties: an installed app beats a website of the
        // same name, as with "open Spotify".
        foreach (var tool in tools)
        {
            if (tool.DirectArguments(command) is { } arguments)
                return new Plan([new PlannedStep(tool, arguments, transcript)]) { IsDirect = true };
        }
        return null;
    }

    /// <summary>
    /// "and" that joins two commands, not "an hour and a half", "1 hour and 30
    /// minutes" or a reminder's "bread and milk".
    /// </summary>
    public static bool IsCompound(string text)
    {
        if (text.StartsWith("remind me ", StringComparison.Ordinal) || text.StartsWith("set a reminder ", StringComparison.Ordinal)
            || text.StartsWith("add a reminder ", StringComparison.Ordinal)) return false;
        var padded = $" {text} ".Replace(" and a half ", " ", StringComparison.Ordinal).Replace(" and half ", " ", StringComparison.Ordinal);
        padded = HoursAndMinutes().Replace(padded, " $1 $3 ");
        padded = ListedWeekdays().Replace(padded, " $1 ");
        return padded.Contains(" and ", StringComparison.Ordinal) || padded.Contains(" then ", StringComparison.Ordinal);
    }

    // "1 hour and 30 minutes".
    [GeneratedRegex(@" (hours?|minutes?) and (\w+ ){1,2}(minutes?|seconds?) ")]
    private static partial Regex HoursAndMinutes();

    // "every Monday and Wednesday".
    [GeneratedRegex(@" ((mon|tues|wednes|thurs|fri|satur|sun)days?) and (?=(mon|tues|wednes|thurs|fri|satur|sun)days?\b)")]
    private static partial Regex ListedWeekdays();
}
