using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace Alfred.Speech;

/// <summary>
/// whisper.cpp through Whisper.net (MIT): <c>small.en</c> on the GPU, <c>base.en</c>
/// on the CPU, <c>tiny.en</c> on the CPU for the wake word (spec §3). One
/// transcription at a time per instance.
/// </summary>
public sealed partial class WhisperTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ModelName { get; }

    /// <param name="useGpu">False keeps it on the CPU, so the dGPU can sleep (the wake word).</param>
    /// <param name="prompt">Words to expect, which biases recognition towards them.</param>
    public WhisperTranscriber(string modelPath, bool useGpu, string? prompt = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"No Whisper model at {modelPath}", modelPath);
        ModelName = Path.GetFileNameWithoutExtension(modelPath);
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        var builder = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()
            .WithSingleSegment()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
        if (prompt is not null) builder = builder.WithPrompt(prompt);
        _processor = builder.Build();
    }

    /// <summary>Which native library whisper.cpp loaded ("Cuda", "Cpu", …).</summary>
    public static string? Runtime => WhisperFactory.GetRuntimeInfo();

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        // whisper.cpp wants at least a second; pad short commands with silence.
        if (samples.Length < SileroVad.SampleRate + SileroVad.SampleRate / 10)
            Array.Resize(ref samples, SileroVad.SampleRate + SileroVad.SampleRate / 10);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var text = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(samples, cancellationToken)) text.Append(segment.Text);
            return Clean(text.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whisper writes non-speech as "[BLANK_AUDIO]" or "(wind blowing)", and
    /// invents a "Thank you." for silence; none of those is a command.
    /// </summary>
    public static string Clean(string text)
    {
        var cleaned = Bracketed().Replace(text, " ").Trim();
        cleaned = Spaces().Replace(cleaned, " ");
        return cleaned.TrimEnd('.').Trim() is "Thank you" or "Thanks for watching" or "you" or "Bye" ? "" : cleaned;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
        _gate.Dispose();
    }
}
