using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alfred.Core;

public enum RoutineStepKind
{
    OpenApp, OpenWebsite, PlayMusic, PlayPlaylist, PlaySong, PauseMusic,
    SetVolume, SetBrightness, Mute, Unmute, LockScreen,
}

/// <summary>
/// A named set of steps started by a phrase: "I'm home", "good night".
/// Written by the user in Settings, so it runs without the model and without
/// grounding checks. Steps go through the ordinary tools, with their safety
/// checks; file changes are deliberately not a step type.
/// </summary>
public sealed record Routine
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Phrases that start it, e.g. "I'm home", "I am home".</summary>
    public List<string> Triggers { get; set; } = [];
    public List<RoutineStep> Steps { get; set; } = [];
    /// <summary>Spoken when it finishes, e.g. "Welcome home". Empty: a short summary.</summary>
    public string Response { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed record RoutineStep(RoutineStepKind Kind, string Text = "", int Percent = 50)
{
    [JsonIgnore]
    public string Title => Kind switch
    {
        RoutineStepKind.OpenApp => "Open app",
        RoutineStepKind.OpenWebsite => "Open website",
        RoutineStepKind.PlayMusic => "Play music",
        RoutineStepKind.PlayPlaylist => "Play playlist",
        RoutineStepKind.PlaySong => "Play song",
        RoutineStepKind.PauseMusic => "Pause music",
        RoutineStepKind.SetVolume => "Set volume",
        RoutineStepKind.SetBrightness => "Set brightness",
        RoutineStepKind.Mute => "Mute",
        RoutineStepKind.Unmute => "Unmute",
        _ => "Lock screen",
    };

    /// <summary>The label of the text the step needs, if any.</summary>
    public static string? TextLabel(RoutineStepKind kind) => kind switch
    {
        RoutineStepKind.OpenApp => "App",
        RoutineStepKind.OpenWebsite => "Address",
        RoutineStepKind.PlayPlaylist => "Playlist",
        RoutineStepKind.PlaySong => "Song",
        _ => null,
    };

    public static bool TakesPercent(RoutineStepKind kind) => kind is RoutineStepKind.SetVolume or RoutineStepKind.SetBrightness;

    [JsonIgnore]
    public string Summary => TextLabel(Kind) is not null ? $"{Title}: {Text}" : TakesPercent(Kind) ? $"{Title} to {Percent}%" : Title;

    /// <summary>The tool and its arguments, as if the user had asked for this step.</summary>
    public (string Tool, JsonObject Arguments)? Invocation()
    {
        var text = Text.Trim();
        if (TextLabel(Kind) is not null && text.Length == 0) return null;
        var percent = Math.Clamp(Percent, 0, 100);
        return Kind switch
        {
            RoutineStepKind.OpenApp => ("openApp", new JsonObject { ["appName"] = text }),
            RoutineStepKind.OpenWebsite => ("openURL", new JsonObject { ["url"] = text }),
            RoutineStepKind.PlayMusic => ("controlSpotify", new JsonObject { ["action"] = "play" }),
            RoutineStepKind.PlayPlaylist => ("controlSpotify", new JsonObject { ["action"] = "playPlaylist", ["query"] = text }),
            RoutineStepKind.PlaySong => ("controlSpotify", new JsonObject { ["action"] = "playSong", ["query"] = text }),
            RoutineStepKind.PauseMusic => ("controlSpotify", new JsonObject { ["action"] = "pause" }),
            RoutineStepKind.SetVolume => ("systemControl", new JsonObject { ["action"] = "setVolume", ["value"] = percent }),
            RoutineStepKind.SetBrightness => ("systemControl", new JsonObject { ["action"] = "setBrightness", ["value"] = percent }),
            RoutineStepKind.Mute => ("systemControl", new JsonObject { ["action"] = "mute" }),
            RoutineStepKind.Unmute => ("systemControl", new JsonObject { ["action"] = "unmute" }),
            _ => ("systemControl", new JsonObject { ["action"] = "lock" }),
        };
    }
}

/// <summary>A routine being run: every step runs even if one fails, and its closing line (or a summary) is spoken at the end.</summary>
public sealed record RoutineRun(string Name, string? Closing, IReadOnlyList<string> Skipped);

public static class Routines
{
    /// <summary>
    /// The routine a short command asks for: one of its phrases, said on its own
    /// or with a couple of extra words ("hey, I'm home now"). Long sentences
    /// that merely contain a phrase don't trigger it.
    /// </summary>
    public static Routine? Match(string transcript, IEnumerable<Routine> routines)
    {
        var said = SpokenText.Words(transcript).Length;
        foreach (var routine in routines.Where(r => r.Enabled))
        {
            foreach (var trigger in routine.Triggers)
            {
                var length = SpokenText.Words(trigger).Length;
                if (length > 0 && said <= length + 2 && Grounding.Mentions(trigger, transcript)) return routine;
            }
        }
        return null;
    }

    /// <summary>Steps whose tool is turned off are skipped and reported.</summary>
    public static Plan Plan(Routine routine, IReadOnlyList<IAssistantTool> tools)
    {
        var steps = new List<PlannedStep>();
        var skipped = new List<string>();
        foreach (var step in routine.Steps)
        {
            if (step.Invocation() is { } call && tools.FirstOrDefault(t => t.Name == call.Tool) is { } tool)
                steps.Add(new PlannedStep(tool, call.Arguments, null));
            else
                skipped.Add(step.Summary);
        }
        var closing = routine.Response.Trim();
        return new Plan(steps) { IsDirect = true, Routine = new RoutineRun(routine.Name, closing.Length == 0 ? null : closing, skipped) };
    }

    /// <summary>Ready-made examples offered in Settings.</summary>
    public static IReadOnlyList<Routine> Examples =>
    [
        new()
        {
            Name = "I'm home", Triggers = ["I'm home", "I am home"], Response = "Welcome home.",
            Steps = [new(RoutineStepKind.PlayPlaylist, "Liked Songs"), new(RoutineStepKind.OpenApp, "Visual Studio Code")],
        },
        new()
        {
            Name = "Good night", Triggers = ["good night", "goodnight", "going to bed"], Response = "Good night.",
            Steps = [new(RoutineStepKind.PauseMusic), new(RoutineStepKind.LockScreen)],
        },
        new()
        {
            Name = "Focus", Triggers = ["focus mode", "time to focus", "work mode"], Response = "Focus mode on.",
            Steps = [new(RoutineStepKind.SetVolume, Percent: 30), new(RoutineStepKind.PlayPlaylist, "Deep Focus")],
        },
    ];
}
