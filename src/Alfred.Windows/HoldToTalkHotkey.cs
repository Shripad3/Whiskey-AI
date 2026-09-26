namespace Alfred.Windows;

/// <summary>
/// Hold Ctrl+Alt+Space to talk (spec §6): listening starts when Space goes
/// down with Ctrl and Alt held, and ends when Space comes up. Escape while
/// held cancels. A low-level keyboard hook sees key-up, which RegisterHotKey
/// cannot; the chord's own Space presses are swallowed so they don't type.
///
/// The hook doesn't see keys while an elevated window has focus (spec §6,
/// "Admin windows"); the W1 fallback toggle is not in W0.
/// </summary>
internal sealed class HoldToTalkHotkey : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    // Kept in a field: the hook holds only a native pointer to it.
    private Native.LowLevelKeyboardProc? _callback;
    private nint _hook;
    private uint _threadId;
    private bool _ctrl, _alt, _held;

    public event Action? Pressed;
    public event Action? Released;
    public event Action? Cancelled;

    public HoldToTalkHotkey()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Hotkey hook" };
        _thread.Start();
        _started.Wait();
        if (_hook == 0) throw new InvalidOperationException("Couldn't install the keyboard hook");
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _callback = OnKey;
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _callback, Native.GetModuleHandleW(null), 0);
        _started.Set();
        // Low-level hooks are called on the installing thread's message loop.
        while (Native.GetMessageW(out _, 0, 0, 0) > 0) { }
        Native.UnhookWindowsHookEx(_hook);
    }

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);
        var key = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        var message = (int)wParam;
        var down = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        var up = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;
        var injected = (key.flags & Native.LLKHF_INJECTED) != 0;

        switch ((int)key.vkCode)
        {
            case Native.VK_LCONTROL or Native.VK_RCONTROL:
                _ctrl = down || (!up && _ctrl);
                break;
            case Native.VK_LMENU or Native.VK_RMENU:
                _alt = down || (!up && _alt);
                break;
            case Native.VK_SPACE when !injected:
                if (down && (_held || (_ctrl && _alt)))
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
                break;
            case Native.VK_ESCAPE when down && _held:
                _held = false;
                Raise(Cancelled);
                return 1;
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
