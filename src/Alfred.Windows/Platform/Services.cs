using System.Runtime.InteropServices;
using System.Text;
using Alfred.Core;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace Alfred.Windows.Platform;

/// <summary>Tokens in the Windows Credential Manager (spec §11), under "Alfred:&lt;key&gt;".</summary>
internal sealed class CredentialStore : ISecretStore
{
    private static string Target(string key) => "Alfred:" + key;

    public string? Get(string key)
    {
        if (!Native.CredReadW(Target(key), Native.CRED_TYPE_GENERIC, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Native.CREDENTIAL>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Native.CredFree(pointer);
        }
    }

    public void Set(string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Native.CREDENTIAL
            {
                Type = Native.CRED_TYPE_GENERIC,
                TargetName = Target(key),
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = Native.CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (!Native.CredWriteW(ref credential, 0)) throw new ToolException($"Windows didn't save the {key} sign-in");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string key) => Native.CredDeleteW(Target(key), Native.CRED_TYPE_GENERIC, 0);
}

/// <summary>
/// Scheduled toasts: the backup that rings timers and alarms when Alfred isn't
/// running (spec §8). Alarms use the alarm scenario, which loops its sound and
/// is let through Do Not Disturb. Nothing fires while the laptop is asleep.
/// </summary>
internal sealed class ToastNotifier : IClockNotifier
{
    private const string Group = "clock";
    private readonly object _lock = new();

    public void Schedule(IReadOnlyList<ClockNotification> items)
    {
        lock (_lock)
        {
            try
            {
                var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
                foreach (var scheduled in notifier.GetScheduledToastNotifications().Where(t => t.Group == Group))
                    notifier.RemoveFromSchedule(scheduled);
                foreach (var item in items.Where(i => i.Date > DateTime.Now.AddSeconds(1)))
                {
                    var toast = new ScheduledToastNotification(Build(item).GetXml(), item.Date)
                    {
                        Tag = Tag(item.Id),
                        Group = Group,
                    };
                    notifier.AddToSchedule(toast);
                }
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
            {
                Log.Warn($"Couldn't schedule backup toasts: {e.Message}");
            }
        }
    }

    public void DeliverNow(ClockNotification item)
    {
        try
        {
            Build(item, alarm: false).Show(t =>
            {
                t.Tag = Tag(item.Id);
                t.Group = "missed";
            });
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            Log.Warn($"Couldn't show a toast: {e.Message}");
        }
    }

    /// <summary>Tags are limited to 64 characters.</summary>
    private static string Tag(string id) => id.Length <= 64 ? id : id[..64];

    private static ToastContentBuilder Build(ClockNotification item, bool alarm = true)
    {
        var builder = new ToastContentBuilder().AddText(item.Title).AddText(item.Body);
        if (!alarm) return builder;
        return builder
            .SetToastScenario(ToastScenario.Alarm)
            .AddAudio(new Uri("ms-winsoundevent:Notification.Looping.Alarm"), loop: true)
            .AddButton(new ToastButton().SetContent("Dismiss").AddArgument("action", "dismiss").SetBackgroundActivation());
    }

    public static void Clear()
    {
        try { ToastNotificationManagerCompat.Uninstall(); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
    }
}

/// <summary>Plugged in, on battery, or battery saver (spec §9), from GetSystemPowerStatus.</summary>
internal sealed class PowerMonitor : IDisposable
{
    private readonly Timer _poll;
    private PowerProfile _profile;

    public event Action<PowerProfile>? Changed;

    public PowerMonitor()
    {
        _profile = Current();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Battery saver doesn't always raise PowerModeChanged; check now and then.
        _poll = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public PowerProfile Profile => _profile;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var next = Current();
        if (next == _profile) return;
        _profile = next;
        Log.Info($"Power profile: {next}");
        Changed?.Invoke(next);
    }

    public static PowerProfile Current()
    {
        if (!Native.GetSystemPowerStatus(out var status)) return PowerProfile.PluggedIn;
        if ((status.SystemStatusFlag & 1) != 0) return PowerProfile.BatterySaver;
        // 255 = unknown (a desktop without a battery): treat as mains.
        return status.ACLineStatus is 1 or 255 ? PowerProfile.PluggedIn : PowerProfile.Battery;
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _poll.Dispose();
    }
}

/// <summary>
/// Where the pill goes (spec §5): the laptop's own panel, found by its output
/// technology, never "the primary monitor" (the Mac's NSScreen.main bug).
/// </summary>
internal static class Displays
{
    /// <summary>The internal panel's work area in physical pixels, or null with the lid closed.</summary>
    public static Native.RECT? InternalWorkArea()
    {
        var device = InternalDeviceName();
        if (device is null) return null;
        foreach (var (_, info) in Native.Monitors())
        {
            if (string.Equals(info.szDevice, device, StringComparison.OrdinalIgnoreCase)) return info.rcWork;
        }
        return null;
    }

    public static Native.RECT PrimaryWorkArea() =>
        Native.Monitors().FirstOrDefault(m => (m.Info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0).Info.rcWork;

    /// <summary>"\\.\DISPLAY1" for the path whose output is internal or embedded.</summary>
    private static string? InternalDeviceName()
    {
        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return null;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, 0) != 0) return null;
            foreach (var path in paths.Take((int)pathCount))
            {
                var technology = path.targetInfo.outputTechnology;
                if (technology is not (Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL or Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                    or Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED or Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS)) continue;
                var name = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                if (Native.DisplayConfigGetDeviceInfo(ref name) == 0) return name.viewGdiDeviceName;
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Warn($"Display query failed: {e.Message}");
        }
        return null;
    }

    /// <summary>
    /// A full-screen game, video or presentation: borderless as well as exclusive
    /// (spec §5). The pill stays hidden and results are spoken instead.
    /// </summary>
    public static bool FullScreenAppInFront() =>
        Native.SHQueryUserNotificationState(out var state) == 0
        && state is Native.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY or Native.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
            or Native.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
}

/// <summary>"Start with Windows": the per-user Run key, no admin needed.</summary>
internal static class StartWithWindows
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Alfred";

    public static bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(Key);
            return run?.GetValue(Name) is string;
        }
        set
        {
            using var run = Registry.CurrentUser.CreateSubKey(Key);
            if (value) run.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else run.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
