using System.Text;
using Alfred.Core;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;

namespace Alfred.Llm;

/// <summary>
/// A GGUF model run in-process by llama.cpp (spec §3). CUDA when the backend
/// and a GPU are present, otherwise the CPU; the backend packages referenced
/// by the executable decide which libraries exist.
/// </summary>
public sealed class LlamaLanguageModel : ILanguageModel, IDisposable
{
    /// <summary>Spec §4: a context of 4,096 tokens.</summary>
    public const int ContextSize = 4096;
    /// <summary>A five-step plan is well under this; it stops a runaway string.</summary>
    private const int MaxOutputTokens = 384;

    private readonly LLamaWeights _weights;
    private readonly ModelParams _parameters;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ModelName { get; }

    /// <summary>llama.cpp's own line saying how many layers went to the GPU, if it said.</summary>
    public static string? OffloadReport { get; private set; }

    private LlamaLanguageModel(LLamaWeights weights, ModelParams parameters, string modelName)
    {
        _weights = weights;
        _parameters = parameters;
        ModelName = modelName;
    }

    /// <param name="gpuLayers">Layers to put in VRAM: all of them by default, 0 for the CPU only.</param>
    public static async Task<LlamaLanguageModel> LoadAsync(string path, int gpuLayers = int.MaxValue, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new AssistantFailure($"No model file at {path}", settingsPage: "Model");
        if (!NativeLibraryConfig.LLama.LibraryHasLoaded)
        {
            // llama.cpp logs every tensor; keep errors and the GPU offload line.
            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (message.Contains("offloaded", StringComparison.Ordinal)) OffloadReport = message.Trim();
                else if (level == LLamaLogLevel.Error) Console.Error.Write(message);
            });
        }
        var parameters = new ModelParams(path)
        {
            ContextSize = ContextSize,
            GpuLayerCount = gpuLayers == int.MaxValue ? 999 : gpuLayers,
            BatchSize = 512,
        };
        var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);
        return new LlamaLanguageModel(weights, parameters, Path.GetFileNameWithoutExtension(path));
    }

    public AssistantFailure? UnavailableReason() => null;

    public async Task<string> CompleteAsync(string instructions, string command, string grammar, CancellationToken cancellationToken)
    {
        var template = new LLamaTemplate(_weights) { AddAssistant = true };
        template.Add("system", instructions);
        template.Add("user", command);
        var prompt = PromptTemplateTransformer.ToModelPrompt(template);

        var inference = new InferenceParams
        {
            MaxTokens = MaxOutputTokens,
            // Greedy, as on the Mac: the same command gives the same plan.
            SamplingPipeline = new GreedySamplingPipeline { Grammar = new Grammar(grammar, "root") },
        };

        // One command at a time; each gets a fresh context (nothing carries over).
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var executor = new StatelessExecutor(_weights, _parameters);
            var output = new StringBuilder();
            await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken))
                output.Append(piece);
            return output.ToString().Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _weights.Dispose();
        _gate.Dispose();
    }
}
