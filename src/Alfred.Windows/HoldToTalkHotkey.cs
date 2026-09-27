using System.Runtime.InteropServices;

namespace Alfred.Windows;

/// <summary>A chord like "Ctrl+Alt+Space": modifiers plus one key.</summary>
internal sealed record Chord(bool Ctrl, bool Alt, bool Shift, bool Win, int Key)
{
    public static readonly Chord Default = new(true, true, false, false, 0x20);

    public static Chord Parse(string text)
    {
        bool ctrl = false, alt = false, shift = false, win = false;
        int? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                case "space": key = 0x20; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
                    else if (part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) key = 0x70 + f - 1;
                    break;
            }
        }
        // A chord needs a key and at least one modifier, or it would fire while typing.
        return key is { } k && (ctrl || alt || win) ? new Chord(ctrl, alt, shift, win, k) : Default;
    }

    public uint Modifiers =>
        (Ctrl ? Native.MOD_CONTROL : 0) | (Alt ? Native.MOD_ALT : 0) | (Shift ? Native.MOD_SHIFT : 0) | (Win ? Native.MOD_WIN : 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key switch
        {
            0x20 => "Space",
            >= 0x70 and <= 0x87 => $"F{Key - 0x70 + 1}",
            _ => ((char)Key).ToString(),
        });
        return string.Join('+', parts);
    }
}

/// <summary>
/// Hold the chord to talk (spec §6): listening starts when its key goes down
/// with the modifiers held, and ends when the key comes up. A low-level
/// keyboard hook sees key-up, which RegisterHotKey cannot; the chord's own
/// key presses are swallowed so they don't type.
///
/// Windows doesn't send an elevated window's keys to the hook. The same
/// chord is also registered with RegisterHotKey, which does fire then: that
/// becomes press-to-start, ended by silence.
/// </summary>
internal sealed class HoldToTalkHotkey : IDisposable
{
    private const int HotkeyId = 0xA1F;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly Chord _chord;
    // Kept in a field: the hook holds only a native pointer to it.
    private Native.LowLevelKeyboardProc? _callback;
    private nint _hook;
    private uint _threadId;
    private bool _ctrl, _alt, _shift, _win, _held;

    public event Action? Pressed;
    public event Action? Released;
    /// <summary>The chord pressed over an elevated window: start, and let silence end it.</summary>
    public event Action? Toggled;
    /// <summary>Escape, pressed anywhere.</summary>
    public event Action? Escape;

    public bool Registered { get; private set; }

    public HoldToTalkHotkey(Chord chord)
    {
        _chord = chord;
        _thread = new Thread(Run) { IsBackground = true, Name = "Hotkey hook" };
        _thread.Start();
        _started.Wait();
        if (_hook == 0) throw new InvalidOperationException($"Couldn't install the keyboard hook ({Marshal.GetLastWin32Error()})");
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _callback = OnKey;
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _callback, Native.GetModuleHandleW(null), 0);
        // Another app may own the chord already; hold-to-talk still works through the hook.
        Registered = Native.RegisterHotKey(0, HotkeyId, _chord.Modifiers | Native.MOD_NOREPEAT, (uint)_chord.Key);
        _started.Set();
        // Low-level hooks and WM_HOTKEY both arrive on this thread's message loop.
        while (Native.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            if (message.message == Native.WM_HOTKEY && (int)message.wParam == HotkeyId) Raise(Toggled);
        }
        if (Registered) Native.UnregisterHotKey(0, HotkeyId);
        Native.UnhookWindowsHookEx(_hook);
    }

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);
        var key = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        var message = (int)wParam;
        var down = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        var up = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;
        var injected = (key.flags & Native.LLKHF_INJECTED) != 0;
        var vk = (int)key.vkCode;

        switch (vk)
        {
            case 0xA2 or 0xA3 or 0x11: _ctrl = down || (!up && _ctrl); break;
            case 0xA4 or 0xA5 or 0x12: _alt = down || (!up && _alt); break;
            case 0xA0 or 0xA1 or 0x10: _shift = down || (!up && _shift); break;
            case 0x5B or 0x5C: _win = down || (!up && _win); break;
            case 0x1B when down:
                if (_held)
                {
                    _held = false;
                    Raise(Escape);
                    return 1;
                }
                Raise(Escape);
                break;
        }

        if (vk == _chord.Key && !injected)
        {
            var chordHeld = _ctrl == _chord.Ctrl && _alt == _chord.Alt && _win == _chord.Win && (!_chord.Shift || _shift);
            if (down && (_held || chordHeld))
            {
                if (!_held)
                {
                    _held = true;
                    Raise(Pressed);
                }
                return 1; // swallow the press and its auto-repeats
            }
            if (up && _held)
            {
                _held = false;
                Raise(Released);
                return 1;
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Off the hook thread: Windows drops hooks that take too long.</summary>
    private static void Raise(Action? handler)
    {
        if (handler is not null) ThreadPool.QueueUserWorkItem(_ => handler());
    }

    public void Dispose()
    {
        Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
        _started.Dispose();
    }
}
