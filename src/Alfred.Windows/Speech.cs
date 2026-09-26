using System.Diagnostics;
using NAudio.Wave;
using Whisper.net;

namespace Alfred.Windows;

/// <summary>
/// Records the default microphone at 16 kHz mono between Start and Stop.
/// Opened per command in W0, so the microphone light is only on while the
/// hotkey is held; the always-on capture with VAD and echo cancellation
/// arrives with the wake word (W2).
/// </summary>
internal sealed class Microphone : IDisposable
{
    public const int SampleRate = 16_000;

    private readonly object _lock = new();
    private readonly List<float> _samples = [];
    private WaveIn? _input;
    private TaskCompletionSource? _stopped;

    public void Start()
    {
        lock (_lock)
        {
            if (_input is not null) return;
            _samples.Clear();
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _input = new WaveIn { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 30 };
            _input.DataAvailable += OnData;
            _input.RecordingStopped += (_, _) => _stopped.TrySetResult();
            _input.StartRecording();
        }
    }

    /// <summary>Stops and returns everything recorded since Start.</summary>
    public async Task<float[]> StopAsync()
    {
        WaveIn? input;
        Task? stopped;
        lock (_lock)
        {
            input = _input;
            stopped = _stopped?.Task;
            _input = null;
        }
        if (input is null) return [];
        input.StopRecording();
        if (stopped is not null) await stopped.WaitAsync(TimeSpan.FromSeconds(1));
        input.Dispose();
        lock (_lock) return [.. _samples];
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                _samples.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
        }
    }

    public void Dispose() => _ = StopAsync();
}

/// <summary>
/// whisper.cpp through Whisper.net (spec §3): <c>small.en</c> on the GPU,
/// <c>base.en</c> on the CPU. The runtime tries CUDA first and falls back.
/// </summary>
internal sealed class WhisperTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    public string Runtime { get; }

    public WhisperTranscriber(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"No Whisper model at {modelPath}");
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()
            .WithSingleSegment()
            .Build();
        Runtime = WhisperFactory.GetRuntimeInfo() ?? "unknown";
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        // whisper.cpp wants at least a second; pad short commands with silence.
        if (samples.Length < Microphone.SampleRate)
            Array.Resize(ref samples, Microphone.SampleRate + Microphone.SampleRate / 10);
        var text = new System.Text.StringBuilder();
        await foreach (var segment in _processor.ProcessAsync(samples, cancellationToken))
            text.Append(segment.Text);
        return Clean(text.ToString());
    }

    /// <summary>
    /// Whisper writes non-speech as "[BLANK_AUDIO]" or "(wind blowing)", and
    /// invents a "Thank you." for silence; none of those is a command.
    /// </summary>
    internal static string Clean(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ").Trim();
        return cleaned.TrimEnd('.').Trim() is "Thank you" or "Thanks for watching" or "you" ? "" : cleaned;
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
    }
}

internal static class Timing
{
    public static string Ms(this Stopwatch clock) => $"{clock.Elapsed.TotalMilliseconds:0} ms";
}
