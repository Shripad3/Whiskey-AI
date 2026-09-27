using Alfred.Core;
using Alfred.Speech;
using NAudio.Wave;

namespace Alfred.Windows.Audio;

/// <summary>
/// The microphone at 16 kHz mono, shared: the wake word listens all the time
/// (when on), and a command recording listens while the user speaks. Opened
/// only while someone needs it, so the privacy light means something. Keeps
/// the last second as a pre-roll, so a hotkey press doesn't clip the first word.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<object> _users = [];
    private readonly RingBuffer _preRoll = new(SileroVad.SampleRate);
    private WaveIn? _input;

    /// <summary>Raised on the capture thread with each ~30 ms buffer.</summary>
    public event Action<float[]>? Samples;

    public bool IsRunning
    {
        get { lock (_lock) return _input is not null; }
    }

    public static bool HasMicrophone => WaveIn.DeviceCount > 0;

    /// <summary>Starts capture for <paramref name="user"/>; a no-op if it's already running.</summary>
    public void Start(object user)
    {
        lock (_lock)
        {
            _users.Add(user);
            if (_input is not null) return;
            if (!HasMicrophone) throw new ToolException("There's no microphone. Check Settings › System › Sound");
            _preRoll.Clear();
            var input = new WaveIn { WaveFormat = new WaveFormat(SileroVad.SampleRate, 16, 1), BufferMilliseconds = 30, NumberOfBuffers = 4 };
            input.DataAvailable += OnData;
            input.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Error("Microphone stopped", e.Exception);
            };
            try
            {
                input.StartRecording();
            }
            catch (NAudio.MmException e)
            {
                input.Dispose();
                throw new ToolException(e.Result == NAudio.MmResult.AlreadyAllocated
                    ? "Another app is using the microphone"
                    : "Windows didn't let me use the microphone. Check Settings › Privacy › Microphone");
            }
            _input = input;
        }
    }

    /// <summary>Stops capture once nobody needs it.</summary>
    public void Stop(object user)
    {
        WaveIn? input = null;
        lock (_lock)
        {
            _users.Remove(user);
            if (_users.Count == 0)
            {
                input = _input;
                _input = null;
            }
        }
        if (input is null) return;
        input.DataAvailable -= OnData;
        try
        {
            input.StopRecording();
        }
        catch (NAudio.MmException)
        {
            // Already gone (unplugged).
        }
        input.Dispose();
    }

    /// <summary>The last <paramref name="seconds"/> of audio, if capture was already running.</summary>
    public float[] Recent(double seconds)
    {
        lock (_lock) return _preRoll.Last((int)(seconds * SileroVad.SampleRate));
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var samples = new float[e.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        lock (_lock) _preRoll.Push(samples);
        try
        {
            Samples?.Invoke(samples);
        }
        catch (Exception error)
        {
            Log.Error("Audio consumer failed", error);
        }
    }

    public void Dispose()
    {
        lock (_lock) _users.Clear();
        Stop(this);
    }
}

/// <summary>
/// The Whisper models, per power profile (spec §9): <c>small.en</c> on the GPU
/// plugged in, <c>base.en</c> on the CPU on battery. Loaded on first use and
/// swapped when the profile changes.
/// </summary>
internal sealed class SpeechModels(string folder, Func<Settings> settings) : IDisposable
{
    private readonly object _lock = new();
    private WhisperTranscriber? _loaded;
    private string? _loadedKey;
    private PowerProfile _profile = PowerProfile.PluggedIn;

    public PowerProfile Profile
    {
        set
        {
            lock (_lock)
            {
                _profile = value;
                if (_loadedKey is not null && _loadedKey != Choose().Key) Unload();
            }
        }
    }

    private (string Key, string Path, bool Gpu) Choose()
    {
        var s = settings();
        var small = Path.Combine(folder, s.WhisperFile);
        var @base = Path.Combine(folder, s.WhisperCpuFile);
        if (_profile == PowerProfile.PluggedIn && File.Exists(small)) return ("gpu:" + small, small, true);
        if (File.Exists(@base)) return ("cpu:" + @base, @base, false);
        return ("cpu:" + small, small, false);
    }

    public string? UnavailableReason()
    {
        var (_, path, _) = Choose();
        return File.Exists(path) ? null : "The speech model isn't downloaded yet. Get it in Settings › Model";
    }

    public WhisperTranscriber Transcriber()
    {
        lock (_lock)
        {
            var (key, path, gpu) = Choose();
            if (_loaded is not null && _loadedKey == key) return _loaded;
            Unload();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _loaded = new WhisperTranscriber(path, gpu);
            }
            catch (Exception e) when (gpu && e is not FileNotFoundException)
            {
                Log.Warn($"Whisper on the GPU failed ({e.Message}); using the CPU");
                _loaded = new WhisperTranscriber(path, useGpu: false);
                key = "cpu:" + path;
            }
            _loadedKey = key;
            Log.Info($"Whisper {Path.GetFileName(path)} loaded in {clock.ElapsedMilliseconds} ms ({key.Split(':')[0]}, runtime {WhisperTranscriber.Runtime})");
            return _loaded;
        }
    }

    private void Unload()
    {
        _loaded?.Dispose();
        _loaded = null;
        _loadedKey = null;
    }

    public void Dispose()
    {
        lock (_lock) Unload();
    }
}

/// <summary>
/// The coordinator's microphone: records a command and transcribes it. Held
/// down, the key release ends it; hands-free, the VAD does (spec §6).
/// </summary>
internal sealed class SpeechInput(AudioCapture capture, SpeechModels models, Func<SileroVad?> vad, AudioDucker ducker) : ISpeechInput
{
    private readonly object _lock = new();
    private CommandRecorder? _recorder;
    private float[]? _wakeAudio;
    private double _wakeSilence;

    /// <summary>Set by the wake word just before it activates, so the recording starts with what was already said.</summary>
    public void PrimeWithWake(float[] audio, double trailingSilence)
    {
        lock (_lock)
        {
            _wakeAudio = audio;
            _wakeSilence = trailingSilence;
        }
    }

    public string? UnavailableReason()
    {
        if (!AudioCapture.HasMicrophone) return "There's no microphone. Check Settings › System › Sound";
        return models.UnavailableReason();
    }

    public Task StartAsync(WakeContext? wake, Action<SpeechUpdate> onUpdate)
    {
        float[] initial;
        double silence;
        bool heard;
        lock (_lock)
        {
            var fromWake = wake is { FromWakeWord: true } && _wakeAudio is not null;
            initial = fromWake ? _wakeAudio! : capture.IsRunning ? capture.Recent(0.3) : [];
            silence = fromWake ? _wakeSilence : 0;
            heard = fromWake;
            _wakeAudio = null;
            _recorder = new CommandRecorder(wake is null ? null : vad(), endpointed: wake is not null, initial, heard, silence, onUpdate);
        }
        capture.Samples += Push;
        try
        {
            capture.Start(this);
        }
        catch
        {
            capture.Samples -= Push;
            throw;
        }
        ducker.Duck();
        return Task.CompletedTask;
    }

    private void Push(float[] samples)
    {
        CommandRecorder? recorder;
        lock (_lock) recorder = _recorder;
        recorder?.Push(samples);
    }

    public async Task<string> FinishAsync(CancellationToken cancellationToken)
    {
        var samples = Stop();
        if (samples is null || samples.Length < SileroVad.SampleRate / 4) return "";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var transcriber = await Task.Run(models.Transcriber, cancellationToken);
        var text = await transcriber.TranscribeAsync(samples, cancellationToken);
        Log.Info($"Heard \"{text}\" ({samples.Length / (double)SileroVad.SampleRate:0.0} s, {clock.ElapsedMilliseconds} ms)");
        return text;
    }

    public Task CancelAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    private float[]? Stop()
    {
        CommandRecorder? recorder;
        lock (_lock)
        {
            recorder = _recorder;
            _recorder = null;
        }
        capture.Samples -= Push;
        capture.Stop(this);
        ducker.Restore();
        return recorder?.Finish();
    }
}
