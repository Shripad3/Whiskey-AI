namespace Alfred.Core;

public sealed record SystemControlArguments(string Action, int? Value = null);

/// <summary>
/// Volume and mute (Core Audio), brightness (the internal panel), lock and
/// sleep. Do Not Disturb has no public API on Windows 11, so it opens the
/// notification settings and says so (spec §8). No shell escape hatch.
/// </summary>
public sealed class SystemControlTool(ISystemControl system) : AssistantTool<SystemControlArguments>
{
    private const float Step = 0.1f;

    public override string Name => "systemControl";
    public override string Title => "System";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        "volume", "sound", "mute", "unmute", "louder", "quieter", "loud", "quiet", "brightness", "brighter", "dim", "dimmer", "darker",
        "screen", "lock", "sleep", "disturb", "dnd", "focus",
    };

    public override string Description =>
        "Change this PC's volume or screen brightness, turn Do Not Disturb on or off, lock the screen, or sleep. " +
        "\"turn it down\" → action \"volumeDown\". \"set the volume to 30\" → action \"setVolume\", value 30. " +
        "\"make the screen brighter\" → action \"brightnessUp\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("action", "What to do", Choices:
        [
            "volumeUp", "volumeDown", "setVolume", "mute", "unmute", "brightnessUp", "brightnessDown", "setBrightness",
            "doNotDisturbOn", "doNotDisturbOff", "lock", "sleep",
        ]),
        new("value", "Percent from 0 to 100, only for setVolume or setBrightness", Required: false, Digits: true),
    ];

    protected override string Target(SystemControlArguments a) => a.Action switch
    {
        "setVolume" => $"Volume {(a.Value is { } v ? $"{v}%" : "")}",
        "volumeUp" => "Volume up",
        "volumeDown" => "Volume down",
        "mute" => "Mute",
        "unmute" => "Unmute",
        "sleep" => "Sleep",
        "brightnessUp" => "Brighter",
        "brightnessDown" => "Dimmer",
        "setBrightness" => $"Brightness {(a.Value is { } b ? $"{b}%" : "")}",
        "doNotDisturbOn" => "Do Not Disturb on",
        "doNotDisturbOff" => "Do Not Disturb off",
        "lock" => "Lock screen",
        _ => a.Action,
    };

    /// <summary>Words that must appear before anything changes: given "Hi how are you", the Mac's model turned the volume down.</summary>
    private static readonly HashSet<string> GroundingWords =
    [
        "volume", "sound", "audio", "mute", "unmute", "louder", "quieter", "loud", "quiet", "turn", "sleep", "speakers",
        "brightness", "bright", "brighter", "dim", "dimmer", "darker", "screen", "display", "disturb", "focus", "lock", "dnd",
    ];

    protected override Task<ToolResult> ExecuteAsync(SystemControlArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Transcript is { } transcript && !SpokenText.Words(transcript).Any(GroundingWords.Contains))
            throw new ToolException("I didn't catch what to change");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ToolResult>(arguments.Action switch
        {
            "volumeUp" => $"Volume {Percent(system.SetVolume(system.Volume() + Step))}%",
            "volumeDown" => $"Volume {Percent(system.SetVolume(system.Volume() - Step))}%",
            "setVolume" => arguments.Value is { } v and >= 0 and <= 100
                ? $"Volume {Percent(system.SetVolume(v / 100f))}%"
                : throw new ToolException("What volume should I set?"),
            "mute" => Do(() => system.SetMuted(true), "Muted"),
            "unmute" => Do(() => system.SetMuted(false), "Unmuted"),
            "brightnessUp" => Brightness(system.Brightness() + 20),
            "brightnessDown" => Brightness(system.Brightness() - 20),
            "setBrightness" => arguments.Value is { } b and >= 0 and <= 100 ? Brightness(b) : throw new ToolException("What brightness should I set?"),
            "doNotDisturbOn" or "doNotDisturbOff" => Do(system.OpenNotificationSettings,
                "Windows doesn't let apps change Do Not Disturb, so I opened the notification settings"),
            // Let the pill show the result before the screen goes.
            "lock" => Later(system.Lock, "Locking", 1000),
            "sleep" => Later(system.Sleep, "Going to sleep", 1500),
            _ => throw new ToolException($"I can't do \"{arguments.Action}\" yet"),
        });
    }

    private static int Percent(float volume) => (int)Math.Round(volume * 100);

    private string Brightness(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        system.SetBrightness(clamped);
        return $"Brightness {clamped}%";
    }

    private static string Do(Action action, string result)
    {
        action();
        return result;
    }

    private static string Later(Action action, string result, int milliseconds)
    {
        _ = Task.Delay(milliseconds).ContinueWith(_ => action(), TaskScheduler.Default);
        return result;
    }

    protected override SystemControlArguments? Direct(DirectCommand command)
    {
        // "DND" is how people say Do Not Disturb.
        var text = $" {command.Text} ".Replace(" dnd ", " do not disturb ", StringComparison.Ordinal).Trim();
        switch (text)
        {
            case "mute" or "mute the sound" or "mute sound" or "mute the volume" or "mute audio":
                return new("mute");
            case "unmute" or "unmute the sound" or "unmute sound" or "unmute the volume" or "unmute audio":
                return new("unmute");
            case "volume up" or "turn the volume up" or "turn up the volume" or "turn it up" or "louder":
                return new("volumeUp");
            case "volume down" or "turn the volume down" or "turn down the volume" or "turn it down" or "quieter":
                return new("volumeDown");
            case "go to sleep" or "put the pc to sleep" or "put my pc to sleep" or "put the computer to sleep" or "put my laptop to sleep"
                or "sleep the pc" or "sleep now":
                return new("sleep");
            case "brighter" or "brightness up" or "increase brightness" or "increase the brightness" or "turn up the brightness"
                or "turn the brightness up" or "make the screen brighter" or "make it brighter":
                return new("brightnessUp");
            case "dimmer" or "darker" or "brightness down" or "decrease brightness" or "decrease the brightness" or "turn down the brightness"
                or "turn the brightness down" or "dim the screen" or "make the screen darker" or "make it darker":
                return new("brightnessDown");
            case "max brightness" or "maximum brightness" or "full brightness":
                return new("setBrightness", 100);
            case "lock" or "lock the screen" or "lock screen" or "lock my pc" or "lock the pc" or "lock my screen" or "lock the computer"
                or "lock my laptop" or "lock my computer":
                return new("lock");
            case "turn on do not disturb" or "do not disturb on" or "enable do not disturb" or "do not disturb" or "don t disturb me"
                or "turn on focus" or "focus mode on":
                return new("doNotDisturbOn");
            case "turn off do not disturb" or "do not disturb off" or "disable do not disturb" or "turn off focus" or "focus mode off":
                return new("doNotDisturbOff");
        }
        // "set the volume to 40", "volume 40", "volume to 40 percent"
        var words = text.Split(' ').Where(w => w is not ("set" or "the" or "to" or "percent" or "at")).ToArray();
        if (words.Length == 2 && int.TryParse(words[1], out var value) && value is >= 0 and <= 100)
        {
            if (words[0] == "volume") return new("setVolume", value);
            if (words[0] == "brightness") return new("setBrightness", value);
        }
        return null;
    }
}
