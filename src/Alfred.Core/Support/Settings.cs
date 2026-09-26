using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alfred.Core;

public enum SpokenResponses { Always, ErrorsOnly, Never }

public enum LidClosedBehaviour { Hide, Floating, Disable }

public enum CalendarProvider { None, Outlook, Google }

/// <summary>
/// Everything the user chooses in Settings. One JSON file in
/// %LOCALAPPDATA%\Alfred; secrets are never here (see <see cref="ISecretStore"/>).
/// </summary>
public sealed class Settings
{
    // Activation
    public bool WakeWordEnabled { get; set; }
    public bool WakeWordOnBattery { get; set; }
    /// <summary>Hold-to-talk chord: Ctrl+Alt+Space by default (spec §6).</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    // Model and voice
    public string ModelFile { get; set; } = "Qwen3-4B-Q4_K_M.gguf";
    public string WhisperFile { get; set; } = "ggml-small.en.bin";
    public string WhisperCpuFile { get; set; } = "ggml-base.en.bin";
    public string WakeWhisperFile { get; set; } = "ggml-tiny.en.bin";
    public SpokenResponses Speak { get; set; } = SpokenResponses.ErrorsOnly;
    /// <summary>A OneCore voice's display name; null for the system default.</summary>
    public string? Voice { get; set; }
    public bool DuckAudio { get; set; } = true;

    // Capabilities: tools default to on (spec §11 gates registration, not just the UI).
    public HashSet<string> DisabledTools { get; set; } = [];

    // Tools
    public SearchEngine SearchEngine { get; set; } = SearchEngine.Google;
    public string? WeatherCity { get; set; }
    public bool? Fahrenheit { get; set; }
    public Dictionary<string, string> AppAliases { get; set; } = [];
    /// <summary>Folders the file tools may look in, under the user's profile.</summary>
    public List<string> FileRoots { get; set; } = ["Documents", "Downloads", "Desktop"];
    public string? SpotifyClientId { get; set; }
    public CalendarProvider Calendar { get; set; } = CalendarProvider.None;
    public string? OutlookClientId { get; set; }
    public string? GoogleClientId { get; set; }
    public List<Routine> Routines { get; set; } = [];

    // Display
    public LidClosedBehaviour LidClosed { get; set; } = LidClosedBehaviour.Floating;

    public bool IsEnabled(string tool) => !DisabledTools.Contains(tool);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Loads and saves <see cref="Settings"/>, and tells listeners when they change.</summary>
public sealed class SettingsStore(string? file)
{
    private readonly object _lock = new();
    private Settings _current = Load(file);

    public event Action<Settings>? Changed;

    public Settings Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    /// <summary>Changes settings and saves them. The change runs on a copy, so readers never see half of it.</summary>
    public void Update(Action<Settings> change)
    {
        Settings next;
        lock (_lock)
        {
            next = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(_current, Settings.Json), Settings.Json)!;
            change(next);
            _current = next;
            Save(next);
        }
        Changed?.Invoke(next);
    }

    private static Settings Load(string? file)
    {
        try
        {
            if (file is not null && File.Exists(file))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Settings.Json) ?? new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file must not stop the app: start from defaults, keep the old file.
            try { File.Copy(file!, file + ".bad", overwrite: true); } catch (IOException) { }
        }
        return new Settings();
    }

    private void Save(Settings settings)
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Settings.Json));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next change tries again.
        }
    }
}

/// <summary>
/// Tokens and secrets: the Windows Credential Manager in the app (spec §11),
/// never the settings file.
/// </summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}

public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    public string? Get(string key) { lock (_values) return _values.GetValueOrDefault(key); }
    public void Set(string key, string value) { lock (_values) _values[key] = value; }
    public void Delete(string key) { lock (_values) _values.Remove(key); }
}

/// <summary>One HttpClient for the app, with a short timeout: a voice command can't wait long.</summary>
public static class Http
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    static Http() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("Alfred/1.0 (+https://github.com/Shripad3/Whiskey-AI)");

    /// <summary>Runs a request, turning "offline" and "too slow" into sentences for the user.</summary>
    public static async Task<T> Guard<T>(string service, Func<Task<T>> request)
    {
        try
        {
            return await request();
        }
        catch (HttpRequestException)
        {
            throw new ToolException($"{service} needs the internet, and I couldn't reach it");
        }
        catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
        {
            throw new ToolException($"{service} didn't respond in time");
        }
    }
}
