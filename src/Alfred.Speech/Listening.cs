using Alfred.Core;

namespace Alfred.Speech;

/// <summary>A wake word heard: the segment's audio and what Whisper made of it.</summary>
/// <param name="Audio">The whole segment, pre-roll included; the command recording starts with it.</param>
/// <param name="TrailingSilence">Seconds of silence that ended the segment.</param>
public sealed record WakeDetection(string Transcript, float[] Audio, double TrailingSilence);

/// <summary>
/// The wake word (spec §6): Silero VAD finds speech, and each speech segment
/// goes to Whisper <c>tiny.en</c>, which must hear "Alfred". Whisper runs only
/// while someone is talking. Device-agnostic: the app pushes microphone
/// audio in, from any thread, one stream at a time.
/// </summary>
public sealed class WakeListener : IDisposable
{
    private const float StartThreshold = 0.5f;
    private const float EndThreshold = 0.35f;
    private const double EndSilence = 0.4;
    private const double MinimumSpeech = 0.25;
    private const double MaximumSegment = 6;
    private const double PreRoll = 1.0;
    private const double FrameSeconds = (double)SileroVad.FrameSamples / SileroVad.SampleRate;

    private readonly SileroVad _vad;
    private readonly Func<float[], CancellationToken, Task<string>> _transcribe;
    private readonly FrameSplitter _frames = new();
    private readonly RingBuffer _history = new((int)(PreRoll * SileroVad.SampleRate));
    private readonly object _lock = new();
    private List<float>? _segment;
    private double _speech;
    private double _silence;
    private bool _busy;
    private bool _enabled;
    private long _heardThrough;
    private long _position;

    /// <summary>Raised off the audio thread when Whisper confirms "Alfred" in a segment.</summary>
    public event Action<WakeDetection>? Detected;

    /// <summary>Raised for every transcribed segment, for the log.</summary>
    public event Action<string>? Heard;

    public WakeListener(SileroVad vad, Func<float[], CancellationToken, Task<string>> transcribe)
    {
        _vad = vad;
        _transcribe = transcribe;
    }

    /// <summary>Off while a command is being handled, so it isn't heard twice.</summary>
    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set
        {
            lock (_lock)
            {
                if (_enabled == value) return;
                _enabled = value;
                _segment = null;
                _frames.Reset();
                _history.Clear();
                _vad.Reset();
            }
        }
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (!_enabled) return;
            _frames.Push(samples, Frame);
        }
    }

    /// <summary>Called under the lock, per 32 ms frame.</summary>
    private void Frame(float[] frame)
    {
        _position += frame.Length;
        var probability = _vad.Probability(frame);
        if (_segment is null)
        {
            _history.Push(frame);
            if (probability < StartThreshold) return;
            _segment = [.. _history.Last(_history.Count)];
            _speech = FrameSeconds;
            _silence = 0;
            return;
        }
        _segment.AddRange(frame);
        if (probability >= EndThreshold)
        {
            _speech += FrameSeconds;
            _silence = 0;
        }
        else
        {
            _silence += FrameSeconds;
        }
        var length = (double)_segment.Count / SileroVad.SampleRate;
        if (_silence < EndSilence && length < MaximumSegment) return;
        var audio = _segment.ToArray();
        var speech = _speech;
        var silence = _silence;
        _segment = null;
        _history.Clear();
        // Short noises aren't worth transcribing; one transcription at a time,
        // and a segment already handled (overlapping audio) is not heard again.
        if (speech < MinimumSpeech || _busy || _position <= _heardThrough) return;
        _busy = true;
        var end = _position;
        _ = Task.Run(() => Check(audio, silence, end));
    }

    private async Task Check(float[] audio, double silence, long end)
    {
        try
        {
            var text = await _transcribe(audio, CancellationToken.None);
            Heard?.Invoke(text);
            if (!WakePhrase.Contains(text)) return;
            lock (_lock) _heardThrough = end;
            Detected?.Invoke(new WakeDetection(text, audio, silence));
        }
        finally
        {
            lock (_lock) _busy = false;
        }
    }

    public void Dispose() => _vad.Dispose();
}

/// <summary>
/// One command's audio: everything between activation and release, or, when
/// hands-free, until the VAD hears the speaker stop (spec §6: 0.8 s of silence,
/// a 10 s cap, 2 s with no speech at all). Device-agnostic, like <see cref="WakeListener"/>.
/// </summary>
public sealed class CommandRecorder
{
    private const float SpeechThreshold = 0.5f;
    private readonly List<float> _samples = [];
    private readonly object _lock = new();
    private readonly SileroVad? _vad;
    private readonly EnergyGate _energy = new();
    private readonly Endpointer? _endpointer;
    private readonly FrameSplitter _frames = new();
    private readonly Action<SpeechUpdate> _onUpdate;
    private bool _ended;

    /// <param name="vad">Null falls back to loudness (<see cref="EnergyGate"/>).</param>
    /// <param name="endpointed">Hands-free: decide the end by silence. Otherwise the key release ends it.</param>
    /// <param name="initial">Audio already heard: the wake segment, or a short pre-roll.</param>
    public CommandRecorder(SileroVad? vad, bool endpointed, float[] initial, bool speechAlreadyHeard, double silenceSoFar, Action<SpeechUpdate> onUpdate)
    {
        _vad = vad;
        _vad?.Reset();
        _samples.AddRange(initial);
        _onUpdate = onUpdate;
        if (endpointed) _endpointer = new Endpointer(speechAlreadyHeard, silenceSoFar);
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        float level;
        lock (_lock)
        {
            if (_ended) return;
            _samples.AddRange(samples);
            _frames.Push(samples, Frame);
            double sum = 0;
            foreach (var s in samples) sum += s * s;
            level = samples.Length == 0 ? 0 : (float)Math.Clamp(Math.Sqrt(sum / samples.Length) * 8, 0, 1);
        }
        _onUpdate(new SpeechUpdate.Level(level));
    }

    private void Frame(float[] frame)
    {
        if (_endpointer is null || _ended) return;
        var duration = (double)frame.Length / SileroVad.SampleRate;
        var speech = _vad is not null ? _vad.Probability(frame) >= SpeechThreshold : _energy.IsSpeech(frame, duration);
        switch (_endpointer.Feed(speech, duration))
        {
            case Endpointer.Decision.EndOfSpeech:
                _ended = true;
                ThreadPool.QueueUserWorkItem(_ => _onUpdate(new SpeechUpdate.EndOfSpeech()));
                break;
            case Endpointer.Decision.NoSpeech:
                _ended = true;
                ThreadPool.QueueUserWorkItem(_ => _onUpdate(new SpeechUpdate.NoSpeech()));
                break;
        }
    }

    /// <summary>Everything recorded, and stops taking more.</summary>
    public float[] Finish()
    {
        lock (_lock)
        {
            _ended = true;
            return [.. _samples];
        }
    }
}
