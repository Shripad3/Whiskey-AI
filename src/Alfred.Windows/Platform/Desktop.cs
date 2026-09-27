using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Alfred.Core;
using Clipboard = System.Windows.Clipboard;

namespace Alfred.Windows.Platform;

/// <summary>
/// The clipboard, on the UI thread (it's an STA-only API). Items a password
/// manager marks private are never returned (spec §8, clipboard).
/// </summary>
internal sealed class WindowsClipboard : IClipboard
{
    /// <summary>The Windows conventions password managers use to keep secrets out of clipboard history and monitors.</summary>
    private static readonly string[] PrivateFormats = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    private static T OnUi<T>(Func<T> work) => Application.Current.Dispatcher.Invoke(work);

    public ClipboardContent Read() => OnUi(() =>
    {
        var data = Retry(Clipboard.GetDataObject);
        if (data is null) return new ClipboardContent(null, [], false, false);
        var formats = data.GetFormats();
        var isPrivate = formats.Any(PrivateFormats.Contains) || IsExcludedFromHistory(data);
        if (isPrivate) return new ClipboardContent(null, [], false, true);
        var files = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped ? dropped : [];
        var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
        return new ClipboardContent(text, files, data.GetDataPresent(DataFormats.Bitmap), false);
    });

    /// <summary>"CanIncludeInClipboardHistory" set to 0.</summary>
    private static bool IsExcludedFromHistory(IDataObject data)
    {
        if (!data.GetDataPresent("CanIncludeInClipboardHistory")) return false;
        return data.GetData("CanIncludeInClipboardHistory") is MemoryStream stream && stream.Length >= 4 && BitConverter.ToInt32(stream.ToArray(), 0) == 0;
    }

    public void Clear() => OnUi(() =>
    {
        Retry(() =>
        {
            Clipboard.Clear();
            return true;
        });
        return true;
    });

    public bool MakePlainText() => OnUi(() =>
    {
        var current = Retry(Clipboard.GetDataObject);
        if (current is null || !current.GetDataPresent(DataFormats.UnicodeText) || current.GetData(DataFormats.UnicodeText) is not string text) return false;
        var isPrivate = current.GetFormats().Any(PrivateFormats.Contains) || IsExcludedFromHistory(current);
        var data = new DataObject(DataFormats.UnicodeText, text);
        // Keep it out of clipboard history if it was private to start with.
        if (isPrivate) data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        Retry(() =>
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        });
        return true;
    });

    /// <summary>
    /// Ctrl+V into the app in front: the pill never takes focus, so that's the
    /// user's app. Blocked by Windows for elevated apps (spec §6).
    /// </summary>
    public void Paste()
    {
        Thread.Sleep(150);
        Native.PressKeys(Native.VK_CONTROL, Native.VK_V);
    }

    /// <summary>The clipboard is briefly locked while other apps use it.</summary>
    private static T Retry<T>(Func<T> work)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return work();
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (COMException)
            {
                throw new ToolException("Another app is holding the clipboard; try again");
            }
        }
    }
}

/// <summary>
/// Moves and sizes top-level windows with SetWindowPos, inside each monitor's
/// work area (taskbar excluded), allowing for Windows 11's invisible borders.
/// </summary>
internal sealed class WindowsWindowManager : IWindowManager
{
    private sealed record AppWindow(nint Handle, string Name);

    public IReadOnlyList<string> RunningApps() => Windows().Select(w => w.Name).Distinct().ToList();

    public string Arrange(string? app, WindowAction action)
    {
        var window = app is null
            ? Windows().FirstOrDefault(w => w.Handle == Native.GetForegroundWindow()) ?? Windows().FirstOrDefault()
            : Windows().FirstOrDefault(w => w.Name.Equals(app, StringComparison.OrdinalIgnoreCase));
        if (window is null) throw new ToolException(app is null ? "There's no window in front" : $"{app} isn't open");
        var hwnd = window.Handle;

        switch (action)
        {
            case WindowAction.Minimize:
                Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
                return window.Name;
            case WindowAction.Maximize or WindowAction.FullScreen:
                Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
                return window.Name;
            case WindowAction.ExitFullScreen:
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                return window.Name;
        }

        if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.GetWindowRect(hwnd, out var outer);
        var visible = Visible(hwnd, outer);
        var monitors = Native.Monitors();
        var current = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var index = Math.Max(0, monitors.FindIndex(m => m.Handle == current));
        var work = ToRect(monitors[index].Info.rcWork);
        Alfred.Core.Rect target;
        if (action == WindowAction.NextDisplay)
        {
            if (monitors.Count < 2) throw new ToolException("There's only one display");
            target = WindowTool.Moved(ToRect(visible), work, ToRect(monitors[(index + 1) % monitors.Count].Info.rcWork));
        }
        else
        {
            target = WindowTool.Frame(action, work, ToRect(visible)) ?? throw new ToolException("I don't know how to do that with a window");
        }
        // SetWindowPos takes the outer frame, invisible borders included.
        int left = outer.Left - visible.Left, top = outer.Top - visible.Top, right = outer.Right - visible.Right, bottom = outer.Bottom - visible.Bottom;
        var x = (int)Math.Round(target.X) + left;
        var y = (int)Math.Round(target.Y) + top;
        var width = (int)Math.Round(target.Width) - left + right;
        var height = (int)Math.Round(target.Height) - top + bottom;
        if (!Native.SetWindowPos(hwnd, 0, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
            throw new ToolException($"Windows didn't let me move {window.Name}; it may be running as administrator");
        // Once more: some apps clamp their size to the monitor they're on before the move lands.
        if (action == WindowAction.NextDisplay) Native.SetWindowPos(hwnd, 0, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        return window.Name;
    }

    private static Native.RECT Visible(nint hwnd, Native.RECT outer) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<Native.RECT>()) == 0 ? frame : outer;

    private static Alfred.Core.Rect ToRect(Native.RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    /// <summary>Visible, titled, uncloaked top-level windows, front to back, named after their app.</summary>
    private static List<AppWindow> Windows()
    {
        var windows = new List<AppWindow>();
        var names = new Dictionary<uint, string?>();
        var own = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindowTextLengthW(hwnd) == 0) return true;
            if (Native.GetWindow(hwnd, 4 /* GW_OWNER */) != 0) return true;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            if ((style & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttributeInt(hwnd, Native.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own) return true;
            if (!names.TryGetValue(pid, out var name)) names[pid] = name = AppName(pid);
            if (name is not null) windows.Add(new AppWindow(hwnd, name));
            return true;
        }, 0);
        return windows;
    }

    /// <summary>"Google Chrome" for chrome.exe: the file description, as the taskbar shows.</summary>
    private static string? AppName(uint pid)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            var size = 1024;
            var path = new StringBuilder(size);
            if (!Native.QueryFullProcessImageNameW(process, 0, path, ref size)) return null;
            var file = path.ToString();
            if (file.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
            var description = FileVersionInfo.GetVersionInfo(file).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(file) : description.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}
