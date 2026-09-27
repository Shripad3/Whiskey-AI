using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Alfred.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace Alfred.Windows.Audio;

/// <summary>
/// Speaks results with a Windows OneCore voice (spec §3), only when the
/// result isn't self-evident: an app opening needs no narration; an answer,
/// a failure, or anything while the pill is hidden does.
/// </summary>
internal sealed class Speaker : IDisposable
{
    private readonly object _lock = new();
    private WaveOut? _output;
    private CancellationTokenSource? _speaking;

    public static IReadOnlyList<string> Voices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Select(v => v.DisplayName).ToList();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or TypeLoadException)
        {
            return [];
        }
    }

    /// <summary>Speaks what this state needs spoken under the user's setting.</summary>
    /// <param name="visible">False when there is no pill to show it in (a full-screen game, or the lid closed).</param>
    public void Speak(AssistantState state, Settings settings, bool visible)
    {
        var mode = settings.Speak;
        string? text = state switch
        {
            AssistantState.Listening => null,
            AssistantState.Result r when mode == SpokenResponses.Always || (mode == SpokenResponses.ErrorsOnly && !visible) => r.Text,
            AssistantState.List l when !visible && mode != SpokenResponses.Never => l.Text,
            AssistantState.Confirm c when mode != SpokenResponses.Never => c.Text,
            AssistantState.Error e when mode != SpokenResponses.Never => e.Message,
            AssistantState.Reply r when mode != SpokenResponses.Never => r.Text,
            AssistantState.Alert a => a.Clock.Message,
            _ => null,
        };
        if (state is AssistantState.Listening) Stop();
        if (text is not null) Say(text, settings.Voice);
    }

    /// <summary>Speaks regardless of the setting (the voice preview in Settings).</summary>
    public void Say(string text, string? voice)
    {
        Stop();
        var cancel = new CancellationTokenSource();
        lock (_lock) _speaking = cancel;
        _ = Task.Run(async () =>
        {
            try
            {
                using var synthesizer = new SpeechSynthesizer();
                if (voice is not null && SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.DisplayName == voice) is { } chosen) synthesizer.Voice = chosen;
                using var stream = await synthesizer.SynthesizeTextToStreamAsync(Readable(text));
                var bytes = new byte[stream.Size];
                await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, global::Windows.Storage.Streams.InputStreamOptions.None);
                if (cancel.IsCancellationRequested) return;
                var reader = new WaveFileReader(new MemoryStream(bytes));
                var output = new WaveOut();
                output.Init(reader);
                lock (_lock)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        output.Dispose();
                        return;
                    }
                    _output = output;
                }
                output.PlaybackStopped += (_, _) => reader.Dispose();
                output.Play();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Warn($"Couldn't speak: {e.Message}");
            }
        });
    }

    /// <summary>"·" separates steps on screen; spoken, it's a pause.</summary>
    private static string Readable(string text) => text.Replace(" · ", ". ", StringComparison.Ordinal).Replace("“", "", StringComparison.Ordinal).Replace("”", "", StringComparison.Ordinal);

    public void Stop()
    {
        WaveOut? output;
        lock (_lock)
        {
            _speaking?.Cancel();
            _speaking = null;
            output = _output;
            _output = null;
        }
        output?.Stop();
        output?.Dispose();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Lowers other apps' audio while Alfred listens (spec §6): their own mixer
/// sessions, not the master volume. The volumes are saved to disk before they
/// change, so a crash mid-command can't leave everything quiet: they are put
/// back at the next start.
/// </summary>
internal sealed class AudioDucker(string stateFile, Func<bool> enabled)
{
    private const float DuckTo = 0.3f;
    private readonly object _lock = new();
    private Dictionary<string, float>? _saved;

    public void Duck()
    {
        if (!enabled()) return;
        lock (_lock)
        {
            if (_saved is not null) return;
            try
            {
                var saved = new Dictionary<string, float>();
                var own = (uint)Environment.ProcessId;
                ForEachSession((id, session) =>
                {
                    if (session.GetProcessID == own || session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) return;
                    saved[id] = session.SimpleAudioVolume.Volume;
                });
                File.WriteAllText(stateFile, JsonSerializer.Serialize(saved));
                ForEachSession((id, session) =>
                {
                    if (saved.TryGetValue(id, out var volume)) session.SimpleAudioVolume.Volume = volume * DuckTo;
                });
                _saved = saved;
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException)
            {
                Log.Warn($"Couldn't lower other audio: {e.Message}");
            }
        }
    }

    public void Restore()
    {
        lock (_lock)
        {
            if (_saved is null) return;
            Apply(_saved);
            _saved = null;
        }
    }

    /// <summary>At start-up: volumes a crash left lowered.</summary>
    public void RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(stateFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(stateFile));
            if (saved is not null) Apply(saved);
            Log.Info("Restored audio volumes left lowered by the last run");
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            File.Delete(stateFile);
        }
    }

    private void Apply(Dictionary<string, float> saved)
    {
        try
        {
            ForEachSession((id, session) =>
            {
                if (saved.TryGetValue(id, out var volume)) session.SimpleAudioVolume.Volume = volume;
            });
            File.Delete(stateFile);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException)
        {
            Log.Warn($"Couldn't restore other audio: {e.Message}");
        }
    }

    private static void ForEachSession(Action<string, AudioSessionControl> action)
    {
        using var devices = new MMDeviceEnumerator();
        var sessions = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            action(session.GetSessionInstanceIdentifier, session);
        }
    }
}

/// <summary>The alarm sound: a soft chime, repeated until the alert is stopped or times out.</summary>
internal sealed class Chime : IDisposable
{
    private readonly string _file = Path.Combine(AppContext.BaseDirectory, "Assets", "chime.wav");
    private readonly object _lock = new();
    private WaveOut? _output;
    private CancellationTokenSource? _loop;

    public void Start()
    {
        Stop();
        if (!File.Exists(_file)) return;
        var loop = new CancellationTokenSource();
        lock (_lock) _loop = loop;
        _ = Task.Run(async () =>
        {
            while (!loop.IsCancellationRequested)
            {
                PlayOnce(loop.Token);
                try { await Task.Delay(2200, loop.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    private void PlayOnce(CancellationToken cancel)
    {
        try
        {
            var reader = new WaveFileReader(_file);
            var output = new WaveOut();
            output.Init(reader);
            output.PlaybackStopped += (_, _) =>
            {
                reader.Dispose();
                output.Dispose();
            };
            lock (_lock)
            {
                if (cancel.IsCancellationRequested) return;
                _output = output;
            }
            output.Play();
        }
        catch (Exception e) when (e is NAudio.MmException or IOException)
        {
            Log.Warn($"Couldn't play the chime: {e.Message}");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _loop?.Cancel();
            _loop = null;
            _output?.Stop();
            _output = null;
        }
    }

    public void Dispose() => Stop();
}
