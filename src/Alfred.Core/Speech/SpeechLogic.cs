namespace Alfred.Core;

/// <summary>
/// Second-stage check on a wake-word detection: false positives are the main
/// risk. Whisper hears the audio from before the detection, so a real
/// "Alfred" shows up in the transcript; one without it is dropped silently.
/// </summary>
public static class WakePhrase
{
    public const string Name = "alfred";
    /// <summary>How far into the transcript the wake word may appear.</summary>
    private const int SearchWords = 10;

    /// <summary>
    /// The command with the wake word removed, or null when the transcript
    /// doesn't contain it (a false detection). "Hey Alfred, open Spotify" →
    /// "open Spotify". "Good morning Alfred" stays as it is (small talk answers it).
    /// </summary>
    public static string? Command(string transcript)
    {
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keys = words.Select(SpokenText.Key).ToArray();
        for (var index = 0; index < Math.Min(keys.Length, SearchWords); index++)
        {
            var length = IsWakeWord(keys[index]) ? 1 : index + 1 < keys.Length && IsWakeWord(keys[index] + keys[index + 1]) ? 2 : 0;
            if (length == 0) continue;
            var command = SpokenText.Clean(string.Join(' ', words[(index + length)..]));
            return command.Length == 0 ? transcript : command;
        }
        return null;
    }

    /// <summary>Words a command starts with; used to drop a misheard wake word from the front.</summary>
    public static readonly HashSet<string> CommandStarts =
    [
        "open", "play", "search", "go", "look", "launch", "start", "google", "watch", "show", "find", "where", "list",
        "turn", "set", "mute", "unmute", "lock", "volume", "brightness", "brighter", "dimmer", "pause", "stop", "skip",
        "next", "previous", "resume", "put", "make", "enable", "disable", "do",
        "rename", "move", "copy", "trash", "delete", "remove", "create", "undo", "throw", "get", "revert",
    ];

    /// <summary>
    /// For a detection already confirmed: the command without the wake word, or
    /// without a few misheard words before the first command word.
    /// </summary>
    public static string CommandAfterConfirmedWake(string transcript)
    {
        if (Command(transcript) is { } command) return command;
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = Array.FindIndex(words.Take(4).Select(SpokenText.Key).ToArray(), CommandStarts.Contains);
        return start > 0 ? string.Join(' ', words[start..]) : transcript;
    }

    /// <summary>True when the wake word appears anywhere in the text.</summary>
    public static bool Contains(string text)
    {
        var keys = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(SpokenText.Key).ToArray();
        return keys.Where((key, i) => IsWakeWord(key) || (i + 1 < keys.Length && IsWakeWord(key + keys[i + 1]))).Any();
    }

    /// <summary>Exact, a known mis-hearing, or close in spelling.</summary>
    public static bool IsWakeWord(string key) =>
        key == Name || key is "alfreds" or "alfredo" or "alfie" or "alford" or "alfrid" or "alfret"
        || (key.Length >= 5 && AppNameMatcher.Similarity(key, Name) >= 0.7);
}

/// <summary>
/// Decides when a hands-free utterance has ended (spec §6). Hold-to-talk needs
/// none of this: releasing the key is the endpoint. Works on per-frame speech
/// decisions from the VAD, or on levels as the fallback (<see cref="EnergyGate"/>).
/// </summary>
public sealed class Endpointer
{
    public enum Decision { Listening, EndOfSpeech, NoSpeech }

    public static readonly double TrailingSilence = 0.8;
    public static readonly double NoSpeechTimeout = 2.0;
    public static readonly double HardCap = 10.0;

    private double _elapsed;
    private double _silence;
    private bool _heardSpeech;

    /// <param name="speechAlreadyHeard">True when the wake word's own segment was speech.</param>
    /// <param name="silenceSoFar">Trailing silence already heard at the end of that segment, in seconds.</param>
    public Endpointer(bool speechAlreadyHeard = false, double silenceSoFar = 0)
    {
        _heardSpeech = speechAlreadyHeard;
        _silence = silenceSoFar;
    }

    /// <summary>Feed each frame's speech decision and its duration in seconds.</summary>
    public Decision Feed(bool isSpeech, double duration)
    {
        _elapsed += duration;
        if (_elapsed >= HardCap) return _heardSpeech ? Decision.EndOfSpeech : Decision.NoSpeech;
        if (isSpeech)
        {
            _heardSpeech = true;
            _silence = 0;
        }
        else
        {
            _silence += duration;
        }
        if (_heardSpeech && _silence >= TrailingSilence) return Decision.EndOfSpeech;
        if (!_heardSpeech && _elapsed >= NoSpeechTimeout) return Decision.NoSpeech;
        return Decision.Listening;
    }
}

/// <summary>
/// Speech by loudness, the fallback when the VAD model is missing (the Mac's
/// energy threshold): speech is 10 dB above a noise floor sampled over the
/// first 200 ms, never below -60 dBFS.
/// </summary>
public sealed class EnergyGate
{
    private const float Margin = 10;
    private const float MinimumFloor = -60;
    private readonly List<float> _calibration = [];
    private double _elapsed;
    private float? _floor;

    public bool IsSpeech(ReadOnlySpan<float> samples, double duration)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        var level = samples.Length == 0 ? -100f : (float)(10 * Math.Log10(sum / samples.Length + 1e-12));
        _elapsed += duration;
        if (_floor is null)
        {
            _calibration.Add(level);
            if (_elapsed >= 0.2) _floor = Math.Max(_calibration.Average(), MinimumFloor);
            return false;
        }
        return level >= _floor + Margin;
    }
}

public enum PowerProfile { PluggedIn, Battery, BatterySaver }

public static class PowerProfiles
{
    /// <summary>What each profile allows (spec §9): the wake word only plugged in, unless the user allows it on battery.</summary>
    public static bool AllowsWakeWord(this PowerProfile profile, bool onBatteryAllowed) =>
        profile == PowerProfile.PluggedIn || (profile == PowerProfile.Battery && onBatteryAllowed);

    /// <summary>Plugged in: the model stays loaded. On battery: unloaded after 2 idle minutes. Battery saver: after use.</summary>
    public static TimeSpan? UnloadAfter(this PowerProfile profile) => profile switch
    {
        PowerProfile.PluggedIn => null,
        PowerProfile.Battery => TimeSpan.FromMinutes(2),
        _ => TimeSpan.Zero,
    };

    public static string? PauseReason(this PowerProfile profile) => profile switch
    {
        PowerProfile.Battery => "on battery",
        PowerProfile.BatterySaver => "battery saver is on",
        _ => null,
    };
}
