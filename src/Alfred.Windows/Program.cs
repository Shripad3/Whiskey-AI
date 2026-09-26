// Alfred for Windows, W0 (spec §10): prove the loop.
// Hold Ctrl+Alt+Space and speak, or type a command and press Enter.
//
//   Alfred.exe [--model <gguf>] [--whisper <ggml .bin>] [--cpu]
//
// Models default to %LOCALAPPDATA%\Alfred\models (see README.md).

using System.Diagnostics;
using Alfred.Core;
using Alfred.Llm;
using Alfred.Windows;

var modelsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alfred", "models");
var modelPath = Path.Combine(modelsFolder, "Qwen3-4B-Q4_K_M.gguf");
var whisperPath = Path.Combine(modelsFolder, "ggml-small.en.bin");
var cpuOnly = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model": modelPath = args[++i]; break;
        case "--whisper": whisperPath = args[++i]; break;
        case "--cpu": cpuOnly = true; break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}. Usage: Alfred.exe [--model <gguf>] [--whisper <bin>] [--cpu]");
            return 2;
    }
}

Console.WriteLine("Alfred W0");
var clock = Stopwatch.StartNew();
using var model = await LlamaLanguageModel.LoadAsync(modelPath, cpuOnly ? 0 : int.MaxValue);
Console.WriteLine($"  model    {model.ModelName}, loaded in {clock.Ms()}. {LlamaLanguageModel.OffloadReport ?? "(no GPU offload reported)"}");

WhisperTranscriber? whisper = null;
try
{
    clock.Restart();
    whisper = new WhisperTranscriber(whisperPath);
    Console.WriteLine($"  whisper  {Path.GetFileName(whisperPath)}, loaded in {clock.Ms()} on {whisper.Runtime}");
}
catch (FileNotFoundException error)
{
    Console.WriteLine($"  whisper  {error.Message}; voice is off, typed commands still work");
}

clock.Restart();
var catalog = new WindowsAppCatalog();
Console.WriteLine($"  apps     {catalog.Apps().Count} in the Start menu, {catalog.Browsers().Count} browsers, default {catalog.DefaultBrowser()?.Name ?? "unknown"} ({clock.Ms()})");

var registry = ToolRegistry.Standard(catalog, new WindowsLauncher());
var engine = new LocalModelEngine(model);
// The coordinator's single lane (spec §4): one command at a time.
var lane = new SemaphoreSlim(1, 1);

async Task HandleAsync(string transcript, Stopwatch sinceEndOfSpeech)
{
    await lane.WaitAsync();
    try
    {
        var planning = Stopwatch.StartNew();
        var plan = await engine.PlanAsync(transcript, registry.EnabledTools(), CancellationToken.None);
        var how = plan.Reply is not null ? "reply" : plan.IsDirect ? "direct" : "model";
        Console.WriteLine($"  plan     {how}, {planning.Ms()}{(plan.Routed is null ? "" : $", saw {string.Join(", ", plan.Routed)}")}");
        if (plan.Reply is not null)
        {
            Console.WriteLine($"  » {plan.Reply}");
            return;
        }
        if (plan.Steps.Count == 0)
        {
            Console.WriteLine("  » I can't do that yet.");
            return;
        }
        foreach (var step in plan.Steps) Console.WriteLine($"  step     {step.Tool.Name} {step.Arguments.ToJsonString()}");
        var acting = Stopwatch.StartNew();
        var outcome = await PlanRunner.RunAsync(plan, CancellationToken.None);
        foreach (var result in outcome.Results) Console.WriteLine($"  » {result.Text}");
        if (outcome.Failure is not null) Console.WriteLine($"  ✗ {outcome.Failure}");
        Console.WriteLine($"  time     act {acting.Ms()}, end of speech → action started {sinceEndOfSpeech.Elapsed.TotalMilliseconds - acting.Elapsed.TotalMilliseconds:0} ms");
    }
    catch (AssistantFailure failure)
    {
        Console.WriteLine($"  ✗ {failure.Message}");
    }
    finally
    {
        lane.Release();
    }
}

using var microphone = new Microphone();
HoldToTalkHotkey? hotkey = null;
if (whisper is not null)
{
    hotkey = new HoldToTalkHotkey();
    hotkey.Pressed += () =>
    {
        microphone.Start();
        Console.WriteLine("\n● listening…");
    };
    hotkey.Cancelled += () =>
    {
        _ = microphone.StopAsync();
        Console.WriteLine("  cancelled");
    };
    hotkey.Released += async () =>
    {
        var sinceRelease = Stopwatch.StartNew();
        var samples = await microphone.StopAsync();
        if (samples.Length < Microphone.SampleRate / 4)
        {
            Console.WriteLine("  (too short)");
            return;
        }
        var transcribing = Stopwatch.StartNew();
        var transcript = await whisper.TranscribeAsync(samples, CancellationToken.None);
        Console.WriteLine($"  heard    \"{transcript}\" ({samples.Length / (double)Microphone.SampleRate:0.0} s of audio, transcribed in {transcribing.Ms()})");
        if (transcript.Length == 0) return;
        await HandleAsync(transcript, sinceRelease);
    };
}

Console.WriteLine(whisper is null
    ? "\nType a command and press Enter. Ctrl+C quits."
    : "\nHold Ctrl+Alt+Space and speak, or type a command and press Enter. Ctrl+C quits.");
while (Console.ReadLine() is { } line)
{
    if (line.Trim().Length == 0) continue;
    await HandleAsync(line, Stopwatch.StartNew());
    Console.WriteLine();
}

hotkey?.Dispose();
whisper?.Dispose();
return 0;
