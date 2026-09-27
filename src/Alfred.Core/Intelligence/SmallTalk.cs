namespace Alfred.Core;

/// <summary>
/// Short replies to greetings and chit-chat, without the model. Given "Hi how
/// are you", the Mac's model picked tools anyway (turned the volume down and
/// searched Google). The pill is not a chat window, so these stay a handful
/// of fixed lines.
/// </summary>
public static class SmallTalk
{
    public static string? Reply(string transcript)
    {
        var text = SpokenText.Normalize(transcript);
        const string name = WakePhrase.Name;
        if (text.StartsWith(name + " ", StringComparison.Ordinal)) text = text[(name.Length + 1)..];
        if (text.EndsWith(" " + name, StringComparison.Ordinal)) text = text[..^(name.Length + 1)];
        if (text == name) return "Yes? What can I do for you?";
        foreach (var prefix in new[] { "hey ", "hi ", "hello ", "okay ", "ok " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length && Replies.ContainsKey(text[prefix.Length..]))
                text = text[prefix.Length..];
        }
        return Replies.GetValueOrDefault(text);
    }

    private static readonly Dictionary<string, string> Replies = Build();

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>();
        void Add(string reply, params string[] phrases)
        {
            foreach (var phrase in phrases) table[phrase] = reply;
        }
        Add("Hello. What can I do for you?", "hi", "hello", "hey", "hey there", "hello there", "yo");
        Add("Good morning. What can I do for you?", "good morning");
        Add("Good afternoon. What can I do for you?", "good afternoon");
        Add("Good evening. What can I do for you?", "good evening");
        Add("Good night.", "good night");
        Add("All running smoothly. What can I do for you?", "how are you", "how are you doing", "how s it going", "how are things", "you ok", "are you ok");
        Add("You're welcome.", "thanks", "thank you", "thanks a lot", "thank you so much", "cheers", "much appreciated");
        Add("I'm Alfred, the assistant on your PC.", "who are you", "what s your name", "what is your name");
        Add("I can open apps, websites and files, search the web and YouTube, control Spotify, change the volume and brightness, set timers, alarms and reminders, and check the weather and your calendar.", "what can you do", "help", "what can i ask you", "what do you do");
        return table;
    }
}

/// <summary>
/// Requests to read a file's contents, declined before the model sees them
/// (spec §11: no tool reads file contents). Given "read me what's in my notes
/// file", the Mac's model searched the web instead of saying no.
/// </summary>
public static class ContentRequests
{
    public const string RefusalText = "I can't read what's inside files. I can find, open, rename, move or recycle them.";

    private static readonly string[] Patterns =
    [
        " read me ", " read my ", " read the ", " what s in my ", " what s inside ", " what is in my ",
        " summarise my ", " summarize my ", " summarise the ", " summarize the ", " what does my ",
    ];

    public static string? Refusal(string transcript)
    {
        var text = " " + SpokenText.Normalize(transcript) + " ";
        return Patterns.Any(p => text.Contains(p, StringComparison.Ordinal)) ? RefusalText : null;
    }
}

/// <summary>
/// Chooses which tools the model sees for one command (spec §4, step 5). With
/// every tool's schema in the prompt the Mac overran its 4,096-token context;
/// showing only the few tools whose keywords were said keeps the prompt small
/// however many tools exist, and fewer choices means better choices for a
/// small model. Direct matching still sees every tool.
/// </summary>
public static class ToolRouter
{
    public const int Maximum = 4;
    /// <summary>When nothing matches: the general-purpose tools.</summary>
    private static readonly string[] Fallback = ["openApp", "openURL", "webSearch"];

    public static IReadOnlyList<IAssistantTool> Relevant(string transcript, IReadOnlyList<IAssistantTool> tools)
    {
        var words = SpokenText.Words(transcript).ToHashSet();
        var scored = tools
            .Select((tool, index) => (Tool: tool, Score: tool.Keywords.Count(words.Contains), Index: index))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score).ThenBy(s => s.Index)
            .Take(Maximum)
            .Select(s => s.Tool)
            .ToList();
        return scored.Count > 0 ? scored : tools.Where(t => Fallback.Contains(t.Name)).ToList();
    }
}
