using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Alfred.Speech;

/// <summary>
/// Silero VAD v5 (MIT, ONNX) on 16 kHz audio: the probability that a 32 ms
/// frame is speech (spec §3). Keeps the model's recurrent state and the 64
/// samples of context it expects before each frame. Runs on the CPU at
/// about 1% of one core. Not thread-safe: one instance per audio stream.
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16_000;
    public const int FrameSamples = 512;
    private const int ContextSamples = 64;

    private readonly InferenceSession _session;
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _input = new float[ContextSamples + FrameSamples];
    private readonly DenseTensor<long> _rate = new(new[] { (long)SampleRate }, [1]);

    public SileroVad(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"No voice activity model at {modelPath}", modelPath);
        var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
    }

    /// <summary>Speech probability for exactly <see cref="FrameSamples"/> samples.</summary>
    public float Probability(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples) throw new ArgumentException($"A frame is {FrameSamples} samples", nameof(frame));
        // The previous frame's last 64 samples come first.
        Array.Copy(_input, FrameSamples, _input, 0, ContextSamples);
        frame.CopyTo(_input.AsSpan(ContextSamples));
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, [1, ContextSamples + FrameSamples])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", _rate),
        };
        using var results = _session.Run(inputs);
        var probability = results.First(r => r.Name == "output").AsEnumerable<float>().First();
        var next = results.First(r => r.Name == "stateN").AsEnumerable<float>().ToArray();
        next.CopyTo(_state, 0);
        return probability;
    }

    /// <summary>Forgets everything heard, for a new stream.</summary>
    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_input);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Cuts an audio stream of any buffer size into whole VAD frames.</summary>
public sealed class FrameSplitter
{
    private readonly float[] _pending = new float[SileroVad.FrameSamples];
    private int _count;

    /// <summary>Calls <paramref name="frame"/> for each complete frame; keeps the remainder for next time.</summary>
    public void Push(ReadOnlySpan<float> samples, Action<float[]> frame)
    {
        while (samples.Length > 0)
        {
            var take = Math.Min(samples.Length, _pending.Length - _count);
            samples[..take].CopyTo(_pending.AsSpan(_count));
            _count += take;
            samples = samples[take..];
            if (_count < _pending.Length) continue;
            frame((float[])_pending.Clone());
            _count = 0;
        }
    }

    public void Reset() => _count = 0;
}

/// <summary>The last few seconds of audio, for the pre-roll before speech was detected.</summary>
public sealed class RingBuffer(int capacity)
{
    private readonly float[] _buffer = new float[capacity];
    private int _start;
    private int _count;

    public int Count => _count;

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _buffer[(_start + _count) % capacity] = sample;
            if (_count < capacity) _count++;
            else _start = (_start + 1) % capacity;
        }
    }

    /// <summary>The most recent <paramref name="samples"/> samples, oldest first.</summary>
    public float[] Last(int samples)
    {
        var n = Math.Min(samples, _count);
        var result = new float[n];
        for (var i = 0; i < n; i++) result[i] = _buffer[(_start + _count - n + i) % capacity];
        return result;
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }
}
