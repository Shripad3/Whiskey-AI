# Alfred for Windows — Technical Specification (draft)

Sep 26, 2026 · draft for review · companion to the Mac spec, [Notch Assistant Technical Specification.md](../AI_Assistant_app/Notch%20Assistant%20Technical%20Specification.md)

## 1. Overview and goals

A Windows edition of Alfred, the voice assistant built for the MacBook notch. The user says "Alfred, …" (or holds a hotkey). A **local** language model turns the words into a plan, and the plan runs as tool calls on the PC. The first target machine is an **HP Omen 16**.

This is a new application, not a port. The Mac app is Swift and uses macOS frameworks throughout, none of which exist on Windows. What carries over:

- **The architecture:** activation → speech → planning → tools → presentation, with one coordinator.
- **The safety rules:** §11 lists them unchanged.
- **The deterministic layers:**
  - the command phrases matched without the model;
  - the time and duration parsers;
  - grounding (arguments must have been said);
  - the file token and undo-journal design.

  The Mac test tables are the acceptance tests for these (§12).
- **The integrations that are already cross-platform:** the Spotify Web API, Google Calendar, Microsoft Graph, Open-Meteo weather and Kokoro TTS.

### The offline premise, unchanged

No cloud AI. Understanding happens on the PC; only actions that need the internet use it (weather, web search, Spotify search, calendars).

### Goals

1. **Free.** Every dependency free and open source. No API keys that cost money.
2. **Fast on this laptop.** Less than 1.5 s from the moment the user stops speaking to the start of an action for common commands, with the model already loaded. This includes the 0.8 s end-of-speech silence (§6).
3. **Light on battery.** On battery the wake word is the only always-on cost, and the model and the voice load on demand and unload when idle. Plugged in, the model stays loaded for speed (§9).
4. **Same assistant.** The same commands, name, confirmation and undo behaviour, and the same refusals as the Mac app.
5. **Easy to hand over.** A single installer the friend can run without developer tools.

### Non-goals (first release)

- A notch. Windows laptops have none; §5 describes the replacement.
- Apple-only features: WeatherKit, Apple Calendar, Reminders, Shortcuts and AppleScript are replaced or dropped (§8).
- A Microsoft Store listing, and gestures. Both are possible later (§10).
- Lights and smart-home control. The Mac's lights came through Apple Shortcuts/HomeKit, and there is no Windows replacement in scope.

## 2. Target machine

An HP Omen 16 with **8 GB of dedicated VRAM** (confirmed). The table gives the model-year range; the design assumes the 8 GB GPU.

| Part | Range | Consequence |
| --- | --- | --- |
| CPU | Intel Core i7-13700HX to i9-14900HX, or AMD Ryzen 7 7840HS / 8040-series | Fine for the CPU fallback. The AMD 8040 NPU (about 16 TOPS) is below Copilot+ (40 TOPS), so **Windows AI APIs / Phi Silica are not available**. |
| GPU | NVIDIA RTX with **8 GB** (RTX 4060/4070 or 5060/5070 class) | **The main accelerator.** CUDA runs the language model, Whisper and Kokoro far faster than the Mac's CPU/ANE paths. |
| RAM | 16–32 GB | Budget ≤ 3 GB of system RAM for the app; models live in VRAM. |
| Display | 16.1" internal; often an external monitor | The overlay is pinned to the internal display, as on the Mac (§5). |
| Power | Gaming laptop with fans; weak battery life on the dGPU | Plugged in: GPU path. Battery: CPU path, smaller models, wake word off by default (§9). |
| OS | Windows 11 24H2 or later (23H2 Home/Pro left support in Nov 2025) | Required for the toast, audio and UI Automation APIs used. |

Unlike the fanless M4 Air, heat is not the ceiling; **battery and GPU switching are**. With Optimus the NVIDIA GPU sleeps when idle and wakes in about 0.5 s. That is acceptable, but the model should stay resident while plugged in (§9).

## 3. Technology choices

| Concern | Choice | Why | Alternatives considered |
| --- | --- | --- | --- |
| Language / runtime | **C# on .NET 10 (LTS)** | First-class Windows APIs (Win32, WinRT, UI Automation, Core Audio); one language for everything; good native interop for llama.cpp and Whisper | Rust + Tauri (smaller, but weaker UI Automation and COM interop); Python (packaging and startup cost) |
| UI | **WPF** | Borderless, transparent, topmost overlay windows are simple and reliable; mature tray-icon libraries | WinUI 3 (modern look, but custom transparent overlay windows are awkward) |
| Tray icon | H.NotifyIcon (MIT) | The equivalent of the Mac menu bar item | — |
| Language model runtime | **llama.cpp via LLamaSharp (MIT)**, CUDA backend, CPU fallback | In-process (no local server); **grammar-constrained JSON output**, the equivalent of the Mac's `@Generable` structured output | Ollama (a separate service to install); ONNX Runtime GenAI with DirectML (vendor-neutral, weaker constrained decoding) |
| Model | **A 4B-class instruct model, Q4_K_M GGUF** (Qwen3-4B or Phi-4-mini, chosen by the intent test corpus, §12); Qwen3-8B offered as an option. Gemma is left out: its licence is Google's own terms, not open source (Goal 1) | A 4B model at Q4 is about 2.5–3 GB of VRAM, leaving ample room for Whisper, Kokoro and a running game within 8 GB, and is stronger than the Mac's 3B | 7–8B as default (about 5 GB at Q4: fits in 8 GB alongside Whisper, but leaves little headroom while a game is running, and is slower on the CPU fallback) |
| Speech to text | **whisper.cpp via Whisper.net (MIT)**, `small.en` on GPU, `base.en` on CPU | Accurate on accents; on GPU a 3 s command transcribes in about 0.3 s | Windows.Media.SpeechRecognition (on-device and streaming, but noticeably less accurate) |
| End of speech | **Silero VAD (MIT, ONNX)** | Reliable end-of-speech detection in noisy rooms; also gates the wake word | Energy threshold (the Mac's `Endpointer`), kept as the fallback |
| Wake word | **VAD-gated keyword check**: Silero VAD finds speech, then Whisper `tiny.en` **on the CPU** (so the dGPU can sleep) checks the segment for "Alfred" | The same idea as the Mac's speech-recogniser wake word, which beat a trained openWakeWord model on the owner's voice; no training needed; it runs only while someone is talking | openWakeWord with a custom "alfred" model (training failed on the Mac); Porcupine (proprietary) |
| Text to speech | **Windows OneCore voices** by default; **Kokoro-82M** optional (ONNX Runtime + DirectML/CUDA) | OneCore voices need nothing extra. Kokoro is the natural voice from the Mac app | Piper (lighter, less natural) |
| Audio | NAudio (MIT): capture at 16 kHz mono through the **communications stream with Windows' built-in echo cancellation**, so music or video playing on the speakers is not heard as speech; ducking via Core Audio session volumes | Ducking lowers *other apps' sessions*, not the master volume. That is better than the Mac, which had to lower the master volume | — |
| Packaging | **Inno Setup installer** (unsigned at first), later signed with **Azure Trusted Signing** | One `.exe` for the friend; SmartScreen warns until it is signed | MSIX (self-signed certificates are awkward to trust on another PC) |

**Kokoro phonemisation needs care.** Kokoro needs text converted to phonemes. The usual converter, espeak-ng, is **GPL-3.0**. Shipping it inside a closed installer has licence implications, while running it as a separate, unmodified process with its source offered is the common approach. The alternative is a non-GPL grapheme-to-phoneme model, as FluidAudio uses on the Mac. This is decided before W4 (§13). Either way, reuse the Mac's `SpeechText.forNeuralVoice` rewriting, which turns "07:00" into "7 A M" and similar, because Kokoro misreads clock times on any platform.

## 4. Architecture

The same five layers and single coordinator as the Mac spec, as C# interfaces:

| Layer | Interface | Windows implementation |
| --- | --- | --- |
| Activation | `IActivationSource` | `HotkeyActivation` (RegisterHotKey, hold-to-talk via a low-level keyboard hook), `WakeWordListener` |
| Speech | `ITranscriptionService` | `WhisperTranscriber` + `SileroEndpointer` |
| Intelligence | `IAssistantEngine` | `LocalModelEngine`: routines → small talk → `DirectMatcher` → `ToolRouter` → constrained model call |
| Action | `IAssistantTool` | One class per tool (§8) |
| Presentation | `IPresenter` | `OverlayWindow` (WPF) + `Speaker` |

The **coordinator** is a single-threaded async state machine (one `SemaphoreSlim`-guarded loop). It has the same ten states as the Mac: Idle, Listening, Thinking, Acting, Result, Reply, List, Confirm, Alert and Error, with the same dismissal timings.

**The model is never given a shell.** The rules of §11 apply to every tool.

### The planning pipeline

Unchanged from the Mac, because it is what made the small model reliable:

1. **Clean** the transcript.
2. **Routines**, matched on the user's phrases.
3. **Small talk and refusals** (for example, requests to read file contents).
4. **`DirectMatcher`:** each tool's fixed phrasings, with no model call. "and"/"then" commands go to the model, except durations such as "an hour and a half", listed weekdays, and reminders.
5. **`ToolRouter`:** show the model at most 4 tools, chosen by keyword.
6. **Constrained generation:**
   - A JSON schema, `{"steps": [ {tool, arguments} … ]}`, where each step is one of the routed tools' argument schemas, compiled to a llama.cpp grammar.
   - A context of 4,096 tokens and a prompt under about 2,500 tokens.
   - A 10 s timeout.
7. **Grounding in each tool:** an argument must have been said, or must mean the same as something said (the Mac's multi-step rule). Values that don't check out are refused, not guessed.

A plan is executed step by step. A failing step stops the rest, except in routines, which run every step and report failures at the end.

## 5. Presentation: the overlay pill

Windows has no notch, so Alfred appears as a **pill at the top centre of the internal display**, just below the top edge. It keeps the Mac design's states and sizes.

- **Window:**
  - Borderless and transparent.
  - `Topmost`, `ShowActivated=false` and `WS_EX_NOACTIVATE`, so it never takes focus from the user's app.
  - `WS_EX_TOOLWINDOW`, so it stays out of Alt+Tab.
  - Click-through (`WS_EX_TRANSPARENT`) except while it shows buttons.
- **Placement:** the internal panel is found with `QueryDisplayConfig`, where the output technology is internal/embedded. Never "the primary monitor", which is the Windows version of the Mac's `NSScreen.main` bug.
- **Display changes:** re-resolve on `WM_DISPLAYCHANGE`, debounced by 300 ms.
- **Lid closed with an external monitor:** Hide, Floating (top of the primary monitor) or Disable, as on the Mac.
- **Full-screen games and video:** most games run borderless, not exclusive fullscreen, and a topmost window over them can cause stutter. `SHQueryUserNotificationState` detects both (`QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_BUSY`); in either case the pill is not shown and results are spoken, as in the Mac's Hide mode.
- **Idle:** hidden. The one exception is a running timer or stopwatch, which shows a small countdown pill (as on the Mac), expanding on hover to list the timers.
- **Escape** cancels from any non-idle state. A global key registration is active only while the pill is visible.

**Tray menu:** status, running timers, alarms and the stopwatch with their controls, Pause Listening (kill switch), Settings and Quit.

**Settings window (WPF):** Activation, Model & Voice, Capabilities (a generated toggle per tool), Routines, Clock, Calendar, Files, Spotify, Permissions & Privacy, and Display. It mirrors the Mac panes.

## 6. Activation and speech

- **Hotkey:** hold-to-talk. The Mac uses ⌥Space, but Alt+Space opens the window menu on Windows, so the default is **Ctrl+Alt+Space**, and it can be rebound.
- **Admin windows:** the app never runs elevated (§11), so Windows stops its keyboard hook, pasting and window moves from reaching an elevated window (Task Manager, installers). While one has focus, hold-to-talk falls back to a press-to-start `RegisterHotKey` toggle, and paste and window arranging say they can't act on that window.
- **Wake word:**
  - Silero VAD runs continuously on 30 ms frames, at about 1% of one CPU core.
  - When VAD detects speech, the segment (including a 2.5 s pre-roll ring buffer) goes to Whisper `tiny.en`. An initial prompt of "Alfred" helps recognition but makes Whisper more likely to hear the name in unrelated speech, so it is used only if it passes the false-wake target (§10, W2) against recorded YouTube, game and call audio.
  - It triggers if the text contains "Alfred" (any prefix: "hey", "okay", "good morning").
  - A command in the same breath ("Alfred, open Spotify") is taken from that segment directly.
  - Detections are de-duplicated by audio time, as on the Mac.
- **End of speech:** 0.8 s of VAD silence, a 10 s cap, and 2 s with no speech at all returns to Idle.
- **Ducking:** while listening, lower other apps' audio sessions to 30% through `IAudioSessionManager2`, and restore them afterwards. Restoring is crash-safe: the previous volumes are saved to disk first.
- **Speaking:** the same Always / Errors only / Never setting. Answers (weather, clock, calendar) are always spoken unless set to Never.

## 7. Intelligence: model management

- **First run:**
  - Settings offers to download the model (about 2.5 GB for 4B Q4) and Whisper `small.en` (about 470 MB) from Hugging Face, with progress and a checksum check.
  - Nothing downloads without the user clicking.
- **Placement:** a GPU is used if CUDA is available with ≥ 4 GB free VRAM (≥ 6.5 GB for the 8B); otherwise the CPU (4B Q4 runs at about 10–20 tokens/s there, so plans take 2–4 s). A running game can hold most of the 8 GB, so free VRAM is checked on each load, and the pill says "Using the CPU while a game is running" the first time it falls back.
- **Staying loaded:**
  - Plugged in: the model stays loaded.
  - On battery: it unloads after 2 idle minutes.
  - Whisper `tiny` (the wake word) always stays loaded while the wake word is on.
- **Model choice:** Settings lists 4B (default) and 8B (the Omen's 8 GB card fits it). The final default is whichever scores best on the intent corpus (§12). Model swaps need no code change.
- **Unavailable model:** a clear error with a link to Settings → Model, never a crash.

## 8. Tool catalog

Mac tools and their Windows implementations. "Same" means the same arguments, direct phrasings and grounding rules as the Mac tool.

| Tool | Windows implementation | Notes |
| --- | --- | --- |
| `openApp` | The `shell:AppsFolder` virtual folder, which lists Start menu shortcuts and packaged (Store) apps together without admin rights; matched with the Mac's `AppNameMatcher` rules; launched via `ShellExecute` on the item's parsing name | Same aliases ("vs code" → Visual Studio Code) |
| `openURL` | `ShellExecute` (default browser) or the named browser's exe (Chrome, Edge, Firefox, Brave) | Same URL grounding |
| `webSearch` | Search URL, as on the Mac | Same |
| `playYouTube` | Tier 1: YouTube results URL. Tier 2 (opt-in): UI Automation in Chrome/Edge to open the first real video | Chrome and Edge expose their page to UI Automation, but this is brittle, as on the Mac; it always falls back to Tier 1 |
| `controlSpotify` | Play/pause/next/previous: **System Media Transport Controls** (`GlobalSystemMediaTransportControlsSessionManager`), which works for Spotify and any media app with no account. Songs and playlists: **Spotify Web API** (the user's own client ID, PKCE, loopback), then start playback through the Web API | Web API playback control needs **Spotify Premium**; without it, fall back to opening `spotify:track:…` in the app |
| `systemControl` | Volume/mute: Core Audio endpoint volume. Brightness: WMI `WmiMonitorBrightnessMethods` (internal panel). Lock: `LockWorkStation`. Sleep: `SetSuspendState` (on Modern Standby laptops this enters standby rather than S3; that is acceptable) | **Do Not Disturb has no public API on Windows 11**: open Settings › Notifications instead and say so |
| `getWeather` | Open-Meteo only | No WeatherKit |
| `findFiles` / `openFile` | **Windows Search index** via the `SystemIndex` OLE DB provider, **metadata columns only** (name, kind, dates); results as tokens | Content columns (`System.Search.Contents`) are never queried, matching the Mac's no-contents rule |
| `organiseFiles` / `undoFileChange` | `IFileOperation`: rename, move, copy and **Recycle Bin only** (`FOFX_RECYCLEONDELETE`), with the same write-ahead journal and undo | Same scoped roots (Documents, Downloads, Desktop), deny list and batch ≤ 20 with confirmation |
| `timer`, `alarm`, `stopwatch` | In-app scheduler, as on the Mac, plus **scheduled toast notifications** (`ScheduledToastNotification`) as the backup when the app isn't running. Alarms use the `alarm` toast scenario, which loops the sound and is let through Do Not Disturb | Windows can deliver scheduled toasts with the app closed, which is better than the Mac. The app has no package identity, so the installer's Start Menu shortcut must carry the app's AUMID and toast activator CLSID. Nothing fires while the laptop is asleep |
| `reminder` | **Microsoft To Do** (Graph `Tasks.ReadWrite`) or **Google Tasks**, whichever calendar account is connected; otherwise an in-app reminder with a scheduled toast | No Apple Reminders |
| `currentTime` | Same | Time zones via `TimeZoneInfo` plus Open-Meteo geocoding |
| `calendar` | **Outlook** (Microsoft Graph, the primary option) or **Google Calendar**, one at a time, **read-only** | No local calendar store with a public API on Windows. A Google OAuth app left in "Testing" status gets refresh tokens that expire after 7 days, so Google needs a sign-in every week until the app is verified |
| `runShortcut` | **Dropped.** Replaced by a routine step "run program or script the user chose" only if §11 allows it (it does not by default) | Lights on the Mac came through Apple Shortcuts/HomeKit; lights and smart home are out of scope (§1) |
| `arrangeWindow` | `SetWindowPos` on the foreground window (or the named app's main window) within the monitor's work area; `ShowWindow` for minimise/maximise | The frame maths from the Mac `WindowLayout` carries over |
| `clipboard` | Win32 clipboard; skip items marked `ExcludeClipboardContentFromMonitorProcessing` or `CanIncludeInClipboardHistory = 0` (the password-manager convention on Windows); paste via `SendInput` Ctrl+V | Same "never read secrets aloud" rule |
| `takeNote` | Append to **OneNote** (Graph) if connected, else a Markdown file in Documents\Alfred Notes | No Apple Notes |
| Routines | Same model and editor; the steps are the tools above | No "lights" steps (§1) |

## 9. Power profiles

| Feature | Plugged in | On battery | Battery saver |
| --- | --- | --- | --- |
| Hotkey | On | On | On |
| Wake word | On if enabled | Off by default (setting) | Off |
| Model | GPU, kept loaded | CPU or GPU per setting, unloaded after 2 min idle | CPU, unloaded after use |
| Whisper | `small.en` on GPU | `base.en` on CPU | `base.en` on CPU |
| Kokoro | GPU | CPU (slower) or OneCore voice | OneCore voice |

Signals: `PowerManager.PowerSupplyStatus`, `PowerManager.EnergySaverStatus`, and the NVIDIA GPU's availability, which is re-checked on each load.

## 10. Phases

Each phase is usable on its own. The friend tests every phase on the Omen.

| Phase | Contents | Done when |
| --- | --- | --- |
| **W0: prove the loop** | Hotkey → Whisper → `DirectMatcher` + local model (constrained JSON) → `openApp`, `openURL`; console output | "open Spotify" and "open YouTube" work in < 2 s from releasing the hotkey, with the model loaded on the GPU |
| **W1: the pill** | Overlay window, state machine, tray, Settings (Capabilities, Permissions, Model with downloads), `webSearch`, `getWeather`, `currentTime` | All states render on the internal display, and an external monitor doesn't move the pill |
| **W2: hands-free** | Wake word, VAD endpointing, ducking, OneCore TTS, `controlSpotify`, `systemControl`, file search/open, power profiles | "Alfred, play some music" works across a day with < 2 false wakes |
| **W3: daily use** | Clock tools with toasts, calendar (Outlook/Google), reminders, routines, `arrangeWindow`, `clipboard`, `takeNote` | The Mac clock and calendar test tables pass; an alarm rings with the app closed (toast) while the laptop is awake |
| **W4: the rest** | File organising with undo, YouTube Tier 2, Kokoro voice, installer signing | Rename then "undo that" restores; the installer runs on a clean Windows install |
| Later | Gestures (camera + MediaPipe hand landmarks via ONNX), Microsoft Store | — |

### Build status (Sep 27, 2026)

**Built together:** W0–W3 and the file work of W4 were built in one pass, at the owner's request, so the friend gets the whole feature set at once. The phase "done when" checks above still apply, and all of them need the Omen.

**Tested on the Mac:**
- The shared core: 534 unit tests, most of them the Mac app's own tables.
- The speech stack on real audio: 15 tests. The wake word fires on "Alfred" and not on three sentences the Mac found tricky.
- The intent corpus through Qwen3-4B.

**Compiled only:** the Windows app has been compiled but not yet run.

**Not built yet:**
- YouTube Tier 2, the Kokoro voice (§13 is still open) and installer signing.
- Echo cancellation: the §3 audio row, which ducking only partly covers.

**Differs from this spec:**
- Notes go to Markdown files in `Documents\Alfred Notes`; OneNote isn't connected.
- "Full screen" maximises the window.

## 11. Invariants (unchanged from the Mac spec, plus Windows specifics)

- No `runCommand` tool; the model never reaches `cmd`, PowerShell, `wscript`, `rundll32` or any script host.
- No tool reads or edits file contents. Search uses metadata columns only.
- No permanent deletion: the Recycle Bin only, and the agent cannot empty it.
- The model receives file tokens, never paths. Every file change is journalled before acting, and existing files are never overwritten.
- Arguments must have been said (grounding).
- Capability toggles gate registration with the model, not just the UI.
- The calendar is read-only.
- **Windows specifics:**
  - The app never requests elevation (no admin, no UIAccess) and never writes outside its own folders or the file roots.
  - Model files are verified by SHA-256 before loading.
  - OAuth tokens go in the Windows Credential Manager (`CredWrite`), never in settings files.
  - The loopback sign-in listener binds 127.0.0.1/::1 only.

## 12. Development and testing

- **Where it's built:**
  - Code can be written from the Mac.
  - WPF, CUDA and the Windows APIs need Windows to build and run. Plain .NET class libraries (the parsers and matchers) build and test on the Mac with `dotnet test`.
  - Windows 11 on Arm in a VM on the M4 Mac can test the UI and the CPU paths, but **not CUDA**.
  - GPU testing happens on the Omen.
- **Shared tests:** export the Mac's test tables (direct phrasings, time and duration parsing, grounding, routine matching, clock multi-step cases, `SpeechText`) to `shared/fixtures/*.json`. Both apps run them. This keeps the Windows behaviour identical and catches drift.
- **Intent corpus:** the Mac's `Tests/Fixtures/intents.txt` is run against each candidate model to choose the default (§3), and re-run whenever the prompt, the tools or the model change.
- **Manual checklist:**
  - external monitor attached and removed;
  - lid closed;
  - full-screen game;
  - unplug mid-command;
  - no internet;
  - no GPU (driver disabled);
  - a password manager item on the clipboard;
  - a rename then "undo that";
  - an alarm with the app closed.

**Repository.** Its own repository, `Whiskey-AI` (`Alfred.slnx`), next to the Mac app's `AI_Assistant_app`. The Mac test tables are copied into `shared/fixtures/*.json` here; a change to one app's tables is copied to the other. It is released separately from the Mac app.

## 13. Open decisions

1. **Default model**, decided by the intent corpus between the 4B candidates.
2. **Kokoro phonemisation** (§3): espeak-ng as a separate GPL process, or a non-GPL G2P model.
3. **Signing:** Azure Trusted Signing (about US$10/month) before sharing more widely; unsigned is acceptable for one friend, who clicks through SmartScreen once.

**Decided (Sep 27, 2026):**

- **Target GPU:** Omen 16 with 8 GB VRAM, so the 8B model is offered in Settings.
- **Lights and smart home:** left out (§1).
- **Spotify without Premium:** play/pause/next/previous work through Windows media controls; "play <song>" opens the `spotify:track:…` link in the app instead of Web API playback.

## 14. Sources

- HP Omen 16 configurations: [HP Omen 16 2025 (Intel)](https://www.hp.com/us-en/gaming-pc/laptops/2025-omen-16-intel.html), [Omen 16z-xf000 (Ryzen 7840HS, RTX 4060)](https://www.hp.com/us-en/shop/pdp/omen-gaming-laptop-16z-xf000-161-77a94av-1), [Notebookcheck: Omen Transcend 16 (2024)](https://www.notebookcheck.net/HP-Omen-Transcend-16-2024-laptop-review-An-RTX-4070-gaming-machine-with-an-OLED-display.802778.0.html), [nanoreview: Omen 16 (2025)](https://nanoreview.net/en/laptop/hp-omen-16-2025)
- The Mac specification: `AI_Assistant_app/Notch Assistant Technical Specification.md` (the design this edition follows)
