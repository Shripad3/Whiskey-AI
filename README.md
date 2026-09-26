# Alfred for Windows

The Windows edition of Alfred, the offline voice assistant. The design is in
[Notch Assistant Windows Specification.md](Notch%20Assistant%20Windows%20Specification.md).
This is **phase W0: prove the loop**. Hold a hotkey, speak, a local model plans it, and Alfred opens an app, a website or a search. Output goes to a console.

## Layout

| Path | What | Builds on |
| --- | --- | --- |
| `src/Alfred.Core` | Matchers, grounding, direct matching, tool router, plan grammar, engine, the W0 tools | Mac and Windows |
| `src/Alfred.Llm` | llama.cpp through LLamaSharp: greedy, grammar-constrained completion | Mac and Windows |
| `src/Alfred.PlanCli` | Plans transcripts and shows what each would start (the intent corpus runner) | Mac and Windows |
| `src/Alfred.Windows` | The W0 app: hold-to-talk hook, microphone, Whisper, Start menu catalog, launcher | Windows (compile-checks on the Mac) |
| `tests/Alfred.Core.Tests` | Unit tests, including the tables in `shared/fixtures` | Mac and Windows |
| `shared/fixtures` | Test tables ported from the Mac app, and the intent corpus | — |

Requires the .NET 10 SDK.

```sh
dotnet build Alfred.slnx
dotnet test
```

## Running W0 on the Omen

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
2. Download the two models into `%LOCALAPPDATA%\Alfred\models`:
   - [Qwen3-4B-Q4_K_M.gguf](https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf) (2.5 GB, Apache 2.0).
     SHA-256 `7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5`.
   - [ggml-small.en.bin](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin) (466 MB, MIT).
3. Plug in the laptop, then run:
   ```
   dotnet run --project src\Alfred.Windows -c Release
   ```
4. Hold **Ctrl+Alt+Space**, say "open Spotify", and let go. You can also type a command and press Enter.

The console shows each stage and how long it took. The numbers below only illustrate the format; nothing has been measured on the Omen yet:

```
● listening…
  heard    "Open Spotify." (1.4 s of audio, transcribed in 310 ms)
  plan     direct, 2 ms
  step     openApp {"appName":"spotify"}
  » Opened Spotify
  time     act 40 ms, end of speech → action started 352 ms
```

**GPU check.** At startup the `model` line should say that all layers were offloaded to the GPU, and the `whisper` line should name the CUDA runtime.
If either reports the CPU, install the NVIDIA driver and the CUDA 12 runtime, then run it again. `--cpu` forces the CPU path, which is what battery mode will use.

Options: `--model <gguf>`, `--whisper <bin>`, `--cpu`.

## The intent corpus

This runs on the Mac or on Windows. It plans each line and prints what would be launched after grounding. Nothing is actually started.

```sh
dotnet run --project src/Alfred.PlanCli -c Release -- --model models/Qwen3-4B-Q4_K_M.gguf < shared/fixtures/intents.txt
```

Run it against each candidate model to choose the default (spec §3, §13). Re-run it whenever the prompt, the tools or the model change.

Results from Sep 27, 2026, for Qwen3-4B Q4_K_M on the M4 Mac with Metal:
- All 17 lines planned correctly, and all 17 are correct after grounding.
- The 9 direct matches took under 20 ms.
- The 8 model calls took a median of 1.9 s (slowest 2.9 s).

## What W0 leaves out

These come in later phases (spec §10):
- the overlay pill, tray and Settings, including downloading models with SHA-256 checks (W1);
- the wake word, VAD, echo cancellation and ducking (W2);
- the fallback hotkey for when an elevated window has focus (W1).
