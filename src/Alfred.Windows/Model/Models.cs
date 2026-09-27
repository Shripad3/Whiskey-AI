using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Alfred.Core;
using Alfred.Llm;

namespace Alfred.Windows.Model;

/// <summary>A model file Alfred can download, with the SHA-256 it must have (spec §11).</summary>
internal sealed record ModelFile(string Name, string File, string Url, string Sha256, long Size, string Purpose)
{
    public static readonly IReadOnlyList<ModelFile> All =
    [
        new("Qwen3 4B (Q4_K_M)", "Qwen3-4B-Q4_K_M.gguf", "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
            "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5", 2_497_280_256, "Understands commands (default)"),
        new("Qwen3 8B (Q4_K_M)", "Qwen3-8B-Q4_K_M.gguf", "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf",
            "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785", 5_027_783_488, "Stronger, for the 8 GB GPU (optional)"),
        new("Whisper small.en", "ggml-small.en.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
            "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d", 487_614_201, "Speech to text on the GPU"),
        new("Whisper base.en", "ggml-base.en.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",
            "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", 147_964_211, "Speech to text on battery"),
        new("Whisper tiny.en", "ggml-tiny.en.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
            "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f", 77_704_715, $"Listens for “{WakePhrase.DisplayName}”"),
        new("Silero VAD v5.1.2", "silero_vad.onnx", "https://github.com/snakers4/silero-vad/raw/v5.1.2/src/silero_vad/data/silero_vad.onnx",
            "2623a2953f6ff3d2c1e61740c6cdb7168133479b267dfef114a4a3cc5bdd788f", 2_327_524, "Hears when you start and stop talking"),
    ];

    /// <summary>What a working setup needs: the default model, both Whispers for the power profiles, the wake word and the VAD.</summary>
    public static IEnumerable<ModelFile> Essential => All.Where(m => !m.File.StartsWith("Qwen3-8B", StringComparison.Ordinal));

    public string PathIn(string folder) => Path.Combine(folder, File);

    public string SizeText => Size >= 1_000_000_000
        ? (Size / 1e9).ToString("0.0 'GB'", CultureInfo.InvariantCulture)
        : (Size / 1e6).ToString("0 'MB'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Downloads a model file to "&lt;name&gt;.part", checks its SHA-256, and only then
/// moves it into place. Nothing downloads without the user clicking (spec §7).
/// </summary>
internal static class ModelDownloader
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task DownloadAsync(ModelFile model, string folder, IProgress<double> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var target = model.PathIn(folder);
        var part = target + ".part";
        using (var response = await Client.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? model.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(part);
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                progress.Report((double)done / total);
            }
        }
        progress.Report(1);
        if (!await VerifyAsync(part, model.Sha256, cancellationToken))
        {
            File.Delete(part);
            throw new InvalidDataException($"{model.Name} didn't match its checksum; the download was deleted. Try again.");
        }
        File.Move(part, target, overwrite: true);
    }

    public static async Task<bool> VerifyAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The language model, loaded on demand (spec §7): on the GPU when there's
/// room (a game can hold most of the 8 GB), otherwise the CPU. Plugged in it
/// stays loaded; on battery it unloads after 2 idle minutes; in battery saver,
/// after each use.
/// </summary>
internal sealed class ModelHost(string folder, Func<Settings> settings) : ILanguageModel, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LlamaLanguageModel? _model;
    private string? _loadedFile;
    private PowerProfile _profile = PowerProfile.PluggedIn;
    private Timer? _idle;

    public string Status { get; private set; } = "Not loaded";

    /// <summary>Raised when the model falls back to the CPU because the GPU is busy, so the pill can say so once.</summary>
    public event Action<string>? Notice;

    private string ModelPath => Path.Combine(folder, settings().ModelFile);

    public PowerProfile Profile
    {
        set
        {
            _profile = value;
            if (value.UnloadAfter() is { } after) ScheduleUnload(after);
        }
    }

    public AssistantFailure? UnavailableReason() =>
        File.Exists(ModelPath) ? null : new AssistantFailure("The language model isn't downloaded yet. Get it in Settings › Model", "Model");

    /// <summary>Loads ahead of the first command, plugged in (spec §9).</summary>
    public async Task PreloadAsync()
    {
        if (_profile != PowerProfile.PluggedIn || UnavailableReason() is not null) return;
        try
        {
            await _gate.WaitAsync();
            try { await EnsureLoaded(); }
            finally { _gate.Release(); }
        }
        catch (Exception e)
        {
            Log.Error("Preloading the model failed", e);
        }
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureLoaded(); }
        finally { _gate.Release(); }
    }

    public async Task<string> CompleteAsync(string instructions, string command, string grammar, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var model = await EnsureLoaded();
            var clock = Stopwatch.StartNew();
            var output = await model.CompleteAsync(instructions, command, grammar, cancellationToken);
            Log.Info($"Model planned \"{command}\" in {clock.ElapsedMilliseconds} ms: {output}");
            return output;
        }
        finally
        {
            _gate.Release();
            if (_profile.UnloadAfter() is { } after) ScheduleUnload(after);
        }
    }

    private async Task<LlamaLanguageModel> EnsureLoaded()
    {
        if (_model is not null && _loadedFile == ModelPath) return _model;
        Unload();
        var path = ModelPath;
        var gpu = UseGpu(path, out var reason);
        var clock = Stopwatch.StartNew();
        try
        {
            _model = await LlamaLanguageModel.LoadAsync(path, gpu ? int.MaxValue : 0);
        }
        catch (Exception e) when (gpu)
        {
            Log.Warn($"Loading on the GPU failed ({e.Message}); using the CPU");
            _model = await LlamaLanguageModel.LoadAsync(path, 0);
            gpu = false;
            reason = "the GPU couldn't take it";
        }
        _loadedFile = path;
        Status = $"{Path.GetFileNameWithoutExtension(path)} on the {(gpu ? "GPU" : "CPU")}";
        Log.Info($"Model {Status}, loaded in {clock.ElapsedMilliseconds} ms. {LlamaLanguageModel.OffloadReport}");
        if (!gpu && reason is not null) Notice?.Invoke($"Using the CPU while {reason}");
        return _model;
    }

    /// <summary>
    /// The GPU when plugged in and it has room: about 4 GB free for the 4B model,
    /// 6.5 GB for the 8B (spec §7). Free memory comes from nvidia-smi, which
    /// ships with the driver.
    /// </summary>
    private bool UseGpu(string path, out string? reason)
    {
        reason = null;
        if (_profile == PowerProfile.BatterySaver) return false;
        var needed = path.Contains("8B", StringComparison.OrdinalIgnoreCase) ? 6500 : 4000;
        var free = FreeVramMegabytes();
        if (free is { } megabytes && megabytes < needed)
        {
            reason = "a game or another app is using the GPU";
            return false;
        }
        return true;
    }

    public static int? FreeVramMegabytes()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return int.TryParse(output.Split('\n')[0].Trim(), out var free) ? free : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // No NVIDIA driver tools: let llama.cpp find out.
        }
    }

    private void ScheduleUnload(TimeSpan after)
    {
        _idle?.Dispose();
        _idle = new Timer(_ =>
        {
            if (!_gate.Wait(0)) return; // In use: the next command reschedules.
            try { Unload(); }
            finally { _gate.Release(); }
        }, null, after, Timeout.InfiniteTimeSpan);
    }

    private void Unload()
    {
        if (_model is null) return;
        _model.Dispose();
        _model = null;
        _loadedFile = null;
        Status = "Not loaded (unloads when idle on battery)";
        Log.Info("Model unloaded");
    }

    public void Dispose()
    {
        _idle?.Dispose();
        Unload();
    }
}
