using Alfred.Core;
using Alfred.Speech;

namespace Alfred.Speech.Tests;

/// <summary>
/// The speech stack on real audio: Silero VAD, Whisper and the wake listener.
/// Needs models/silero_vad.onnx, models/ggml-tiny.en.bin and models/ggml-base.en.bin,
/// and the clips in models/test-audio (made with macOS `say`; see README).
/// Each test passes trivially when its files are missing.
/// </summary>
public class SpeechTests
{
    private static readonly string Models = FindModels();

    private static string FindModels()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "models");
            if (Directory.Exists(candidate)) return candidate;
        }
        return "models";
    }

    private static string Model(string name) => Path.Combine(Models, name);
    private static string Clip(string name) => Path.Combine(Models, "test-audio", name + ".wav");
    private static bool Have(params string[] files) => files.All(File.Exists);

    /// <summary>16-bit mono PCM WAV → samples in -1…1.</summary>
    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "data")
            {
                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, offset + 8 + i * 2) / 32768f;
                return samples;
            }
            offset += 8 + size + (size & 1);
        }
        throw new InvalidDataException("No data chunk");
    }

    /// <summary>Silence around a clip, as a microphone would hear it.</summary>
    private static float[] Padded(float[] clip, double before = 1.0, double after = 1.5)
    {
        var result = new float[(int)(before * 16000) + clip.Length + (int)(after * 16000)];
        var noise = new Random(7);
        for (var i = 0; i < result.Length; i++) result[i] = (float)((noise.NextDouble() - 0.5) * 0.002);
        clip.CopyTo(result, (int)(before * 16000));
        return result;
    }

    /// <summary>Feeds audio in 30 ms buffers, as the microphone does.</summary>
    private static void Stream(float[] audio, Action<float[]> push)
    {
        for (var i = 0; i < audio.Length; i += 480) push(audio[i..Math.Min(audio.Length, i + 480)]);
    }

    [Fact]
    public void VadFindsSpeechAndSilence()
    {
        if (!Have(Model("silero_vad.onnx"), Clip("set_timer"))) return;
        using var vad = new SileroVad(Model("silero_vad.onnx"));
        var audio = Padded(ReadWav(Clip("set_timer")));
        var probabilities = new List<float>();
        new FrameSplitter().Push(audio, frame => probabilities.Add(vad.Probability(frame)));
        var firstSecond = probabilities.Take(25).ToList();
        Assert.True(firstSecond.Max() < 0.3, $"silence scored {firstSecond.Max()}");
        Assert.True(probabilities.Count(p => p > 0.5) > 20, "speech not detected");
    }

    [Theory]
    [InlineData("alfred_open_spotify", "open Spotify")]
    [InlineData("hey_alfred_weather", "what's the weather like tomorrow")]
    public async Task WakeWordIsHeardWithItsCommand(string clip, string command)
    {
        if (!Have(Model("silero_vad.onnx"), Model("ggml-tiny.en.bin"), Clip(clip))) return;
        using var tiny = new WhisperTranscriber(Model("ggml-tiny.en.bin"), useGpu: false);
        using var listener = new WakeListener(new SileroVad(Model("silero_vad.onnx")), tiny.TranscribeAsync) { Enabled = true };
        var detected = new TaskCompletionSource<WakeDetection>();
        listener.Detected += d => detected.TrySetResult(d);
        Stream(Padded(ReadWav(Clip(clip))), samples => listener.Push(samples));
        var detection = await detected.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(command, SpokenText.Clean(WakePhrase.Command(detection.Transcript)!), ignoreCase: true);
        Assert.True(detection.Audio.Length > 16000, "the segment should include the whole phrase");
    }

    [Theory]
    [InlineData("offered_coffee")]
    [InlineData("all_friends")]
    [InlineData("set_timer")]
    public async Task OtherSpeechDoesNotWake(string clip)
    {
        if (!Have(Model("silero_vad.onnx"), Model("ggml-tiny.en.bin"), Clip(clip))) return;
        using var tiny = new WhisperTranscriber(Model("ggml-tiny.en.bin"), useGpu: false);
        using var listener = new WakeListener(new SileroVad(Model("silero_vad.onnx")), tiny.TranscribeAsync) { Enabled = true };
        var heard = new TaskCompletionSource<string>();
        var woke = false;
        listener.Heard += text => heard.TrySetResult(text);
        listener.Detected += _ => woke = true;
        Stream(Padded(ReadWav(Clip(clip))), samples => listener.Push(samples));
        var text = await heard.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(100);
        Assert.False(woke, $"woke on \"{text}\"");
        Assert.NotEmpty(text);
    }

    [Fact]
    public async Task DisabledListenerHearsNothing()
    {
        if (!Have(Model("silero_vad.onnx"), Model("ggml-tiny.en.bin"), Clip("alfred_open_spotify"))) return;
        using var tiny = new WhisperTranscriber(Model("ggml-tiny.en.bin"), useGpu: false);
        using var listener = new WakeListener(new SileroVad(Model("silero_vad.onnx")), tiny.TranscribeAsync);
        var woke = false;
        listener.Detected += _ => woke = true;
        Stream(Padded(ReadWav(Clip("alfred_open_spotify"))), samples => listener.Push(samples));
        await Task.Delay(3000);
        Assert.False(woke);
    }

    [Fact]
    public async Task CommandRecorderEndsAfterTheSpeakerStops()
    {
        if (!Have(Model("silero_vad.onnx"), Model("ggml-base.en.bin"), Clip("set_timer"))) return;
        var ended = new TaskCompletionSource<SpeechUpdate>();
        var recorder = new CommandRecorder(new SileroVad(Model("silero_vad.onnx")), endpointed: true, [], false, 0, update =>
        {
            if (update is SpeechUpdate.EndOfSpeech or SpeechUpdate.NoSpeech) ended.TrySetResult(update);
        });
        var audio = Padded(ReadWav(Clip("set_timer")), before: 0.3, after: 3);
        Stream(audio, samples => recorder.Push(samples));
        Assert.IsType<SpeechUpdate.EndOfSpeech>(await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // The recorder stopped taking audio about 0.8 s into the trailing
        // silence, not at the end of the 3 s.
        var kept = recorder.Finish();
        var clipEnd = (0.3 * 16000) + ReadWav(Clip("set_timer")).Length;
        Assert.InRange((kept.Length - clipEnd) / 16000.0, 0.4, 1.6);

        using var whisper = new WhisperTranscriber(Model("ggml-base.en.bin"), useGpu: false);
        var text = await whisper.TranscribeAsync(kept, CancellationToken.None);
        Assert.Equal("Set a timer for 10 minutes", text.TrimEnd('.'), ignoreCase: true);
    }

    [Fact]
    public void RecorderWithoutSpeechGivesUp()
    {
        var updates = new List<SpeechUpdate>();
        var recorder = new CommandRecorder(null, endpointed: true, [], false, 0, u => { lock (updates) updates.Add(u); });
        Stream(Padded([], before: 0, after: 3), samples => recorder.Push(samples));
        Thread.Sleep(200);
        lock (updates) Assert.Contains(updates, u => u is SpeechUpdate.NoSpeech);
    }

    [Fact]
    public void RingBufferKeepsTheLatest()
    {
        var ring = new RingBuffer(4);
        ring.Push([1, 2, 3]);
        ring.Push([4, 5, 6]);
        Assert.Equal([3f, 4, 5, 6], ring.Last(10));
        Assert.Equal([5f, 6], ring.Last(2));
    }

    [Fact]
    public void SplitterMakesWholeFrames()
    {
        var splitter = new FrameSplitter();
        var frames = 0;
        splitter.Push(new float[700], _ => frames++);
        splitter.Push(new float[400], _ => frames++);
        Assert.Equal(2, frames);
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" Thank you.", "")]
    [InlineData("(wind blowing) Open Spotify.", "Open Spotify.")]
    [InlineData(" Set a  timer", "Set a timer")]
    public void CleansWhisperOutput(string raw, string clean) => Assert.Equal(clean, WhisperTranscriber.Clean(raw));
}
