# Alfred for Windows

A voice assistant for your PC that runs on the PC itself: no cloud AI. Say "Alfred, …" or hold **Ctrl+Alt+Space** and speak.
A local language model works out what you meant, and Alfred does it:
- opens apps and websites;
- controls Spotify;
- sets timers and alarms;
- finds your files;
- reads your calendar;
- and more.

It's the Windows edition of the Mac notch assistant. The design is in [Notch Assistant Windows Specification.md](Notch%20Assistant%20Windows%20Specification.md).

> **Status:** everything below is built and unit-tested. The Windows app has been compiled but **not yet run on a real Windows PC**. Your first run is its first run, so please send back what you see (see [Reporting problems](#reporting-problems)).

## What you need

- **Windows 11** (24H2 or later), 64-bit.
- **The .NET 10 SDK:** download the x64 installer from https://dotnet.microsoft.com/download/dotnet/10.0.
- **VS Code** with the **C# Dev Kit** extension. VS Code offers to install it when you open the folder.
- **For GPU speed** (strongly recommended on the Omen):
  - an up-to-date NVIDIA driver;
  - the **CUDA 12 toolkit**: open https://developer.nvidia.com/cuda-toolkit-archive, pick the newest 12.x release, then Windows › x86_64 › 11 › exe (local). The default install is fine.

  Without CUDA, Alfred still works on the CPU, just more slowly (plans take 2–4 s instead of well under 1 s).
- **About 4 GB of disk** for the models, which Alfred downloads for you.

## Run it

1. Clone the repo and open the folder in VS Code.
2. Press **F5** (or Run › Start Debugging › "Alfred (Windows)"). You can also use a terminal:
   ```
   dotnet run --project src\Alfred.Windows
   ```
3. The first time, **Settings › Model** opens. Click **Download everything needed** (about 3.2 GB) and wait until it says "All downloaded". Each file is checked against its SHA-256 before it's used.
4. Alfred's icon, a white "A" on black, is in the notification area at the bottom right of the taskbar. You may need the ^ arrow to see it. **Right-click it** for the menu.
5. Hold **Ctrl+Alt+Space**, say **"open Spotify"**, and let go.

To use the wake word, turn it on in **Settings › General**. It then listens for "Alfred, …" while plugged in, and on battery too if you allow it.

## What to say

| Area | Try |
| --- | --- |
| Apps and web | "open Spotify", "open YouTube in Edge", "search for cheap flights to Rome", "go to github.com" |
| YouTube | "play Mat Armstrong's latest video on YouTube" |
| Spotify | "play some music", "next", "pause", "play Bohemian Rhapsody", "play my chill playlist" |
| System | "volume 30", "turn it down", "mute", "brightness up", "lock the screen" |
| Weather | "how's the weather", "will it rain tomorrow in Paris" |
| Clock | "set a pasta timer for 10 minutes", "wake me up at 7 on weekdays", "start the stopwatch", "what time is it in Tokyo" |
| Reminders | "remind me to call Mum at 6", "don't let me forget to buy milk tomorrow" |
| Calendar | "what's on my calendar tomorrow", "when's my next meeting", "am I free at 3" |
| Files | "open my latest screenshot", "find my tax documents from last year", "rename test txt to notes", "move my screenshots to Receipts", "delete my old draft", "undo that" |
| Windows | "put Chrome on the left half", "maximise this", "move this to the other display" |
| Clipboard and notes | "what's on my clipboard", "paste as plain text", "note that the Wi-Fi password is on the router" |
| Routines | Your own phrases that run several steps; set them up in Settings › Routines |
| Chat | "hello", "what can you do" |

**Other ways to control it:**
- **Escape** cancels anything.
- A ringing alarm stops when you say "stop" or "snooze", or when you click the buttons.
- **Type a command…** in the tray menu works without a microphone.

**Safety rules:**
- Alfred never runs commands and never reads what's inside your files.
- Deleting only ever goes to the Recycle Bin, and "undo that" reverses the last file change.
- It won't act on anything you didn't say.

## Optional accounts

Each of these uses your own free developer credentials, entered in Settings. The repo contains none.

- **Spotify songs and playlists by name:** Settings › Spotify has the steps. Play, pause and skip work without any setup. Starting a particular song through Spotify's API needs Premium; without Premium, Alfred opens the song in the app and presses play.
- **Calendar and reminders:** Outlook or Google, in Settings › Calendar.
  - Alfred only reads the calendar. Reminders go into the same account's task list.
  - Without an account, reminders ring in Alfred.
  - Google asks you to sign in again every 7 days while your Google app is in "Testing".

## Where things are

| What | Where |
| --- | --- |
| Settings, alarms, file-change history | `%LOCALAPPDATA%\Alfred` |
| Models | `%LOCALAPPDATA%\Alfred\models` (or set `ALFRED_MODELS` to another folder) |
| Logs (a week's worth) | `%LOCALAPPDATA%\Alfred\logs`, or Settings › General › Open the log folder |
| Quick notes | `Documents\Alfred Notes` |
| Sign-in tokens | Windows Credential Manager, under "Alfred:…" |

## Reporting problems

Please send:
- the day's log file from `%LOCALAPPDATA%\Alfred\logs`;
- what you said;
- what happened.

The log records:
- whether the language model and Whisper loaded on the GPU. Look for "on the GPU" and "runtime Cuda"; "CPU" means CUDA isn't being used;
- every command heard and how long it took;
- any errors.

Things worth checking on the Omen:
- the pill appears at the top centre of the laptop screen, including with an external monitor plugged in;
- the pill stays hidden during a full-screen game, with results spoken instead;
- the first word isn't cut off when you hold the hotkey;
- "Alfred" wakes it without false wakes from videos or calls;
- a timer and an alarm ring, including with Alfred closed (a toast);
- rename a file, then "undo that".

## For developers

```sh
dotnet build Alfred.slnx
dotnet test Alfred.slnx
```

**Projects:**

| Path | What | Runs on |
| --- | --- | --- |
| `src/Alfred.Core` | Every tool's logic, the planner, the coordinator, the clock, files, calendar, OAuth | Windows, Mac, Linux |
| `src/Alfred.Speech` | Silero VAD, Whisper, wake-word listening, command endpointing | Windows, Mac, Linux |
| `src/Alfred.Llm` | llama.cpp through LLamaSharp: greedy, grammar-constrained planning | Windows, Mac, Linux |
| `src/Alfred.PlanCli` | Plans commands and shows what each would start (the intent corpus runner) | Windows, Mac, Linux |
| `src/Alfred.Windows` | The app: pill, tray, Settings, hotkey, microphone, voice, and the Windows side of every tool | Windows (compiles on the Mac) |
| `tests/` | Unit tests, many of them the Mac app's own tables in `shared/fixtures`, plus speech tests on real audio | |

**Speech tests:**
- They need the models in `models/`.
- They also need test clips: on a Mac, run `say -v Daniel -o x.aiff "Alfred, open Spotify."` and convert with `afconvert -f WAVE -d LEI16@16000 -c 1`. See `tests/Alfred.Speech.Tests`.
- Without the models or clips they pass trivially.

**Intent corpus:** this runs the model against `shared/fixtures/intents.txt`. Nothing is started.

```sh
dotnet run --project src/Alfred.PlanCli -c Release -- --model models/Qwen3-4B-Q4_K_M.gguf < shared/fixtures/intents.txt
```

Results from Sep 27, 2026, for Qwen3-4B Q4_K_M on an M4 Mac with Metal:
- Every command plans correctly, or reasonably declines.
- Direct matches take under 10 ms.
- Model calls take a median of 2.3 s. The Omen's RTX should be several times faster.

## Not in this version

- **Hand gestures:** the Mac has them; on Windows they're planned for later (spec §10).
- **Kokoro voice:** Alfred uses Windows' own voices. More natural ones are in Windows Settings › Speech. Kokoro's licensing question is still open (spec §13).
- **YouTube autoplay:** Alfred opens YouTube's results. Clicking the first video for you (the Mac's "Tier 2") isn't ported.
- **Notes:** they go to Markdown files in `Documents\Alfred Notes`, not to OneNote.
- **Full screen:** "full screen" maximises the window. Windows has no general full-screen mode for apps.
- **Echo cancellation:** not done yet. While music plays loudly the wake word hears it; Alfred lowers other apps while it listens.
