using System.Diagnostics;
using System.Management;
using Alfred.Core;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace Alfred.Windows.Platform;

/// <summary>
/// Volume and mute through Core Audio, brightness through WMI (the internal
/// panel only), lock and sleep through Win32 (spec §8, systemControl).
/// </summary>
internal sealed class WindowsSystemControl : ISystemControl
{
    private static AudioEndpointVolume Endpoint()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            throw new ToolException("There's no sound output device");
        }
    }

    public float Volume() => Endpoint().MasterVolumeLevelScalar;

    public float SetVolume(float volume)
    {
        var endpoint = Endpoint();
        var clamped = Math.Clamp(volume, 0f, 1f);
        endpoint.MasterVolumeLevelScalar = clamped;
        if (clamped > 0) endpoint.Mute = false;
        return endpoint.MasterVolumeLevelScalar;
    }

    public void SetMuted(bool muted) => Endpoint().Mute = muted;

    public int Brightness()
    {
        foreach (var monitor in Query("SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            return Convert.ToInt32(monitor["CurrentBrightness"]);
        throw NoBrightness();
    }

    public void SetBrightness(int percent)
    {
        var done = false;
        foreach (ManagementObject monitor in Query("SELECT * FROM WmiMonitorBrightnessMethods"))
        {
            monitor.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)Math.Clamp(percent, 0, 100)]);
            done = true;
        }
        if (!done) throw NoBrightness();
    }

    private static ManagementObjectCollection Query(string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", query);
            return searcher.Get();
        }
        catch (ManagementException)
        {
            throw NoBrightness();
        }
    }

    private static ToolException NoBrightness() => new("This screen's brightness can't be changed from Windows (external monitors usually can't)");

    public void Lock()
    {
        if (!Native.LockWorkStation()) throw new ToolException("Windows didn't lock the screen");
    }

    /// <summary>On Modern Standby laptops this enters standby rather than S3; that's fine.</summary>
    public void Sleep() => Native.SetSuspendState(false, false, false);

    public void OpenNotificationSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true })?.Dispose();
}

/// <summary>
/// Spotify through the System Media Transport Controls: play, pause, next and
/// previous for any account, no Web API needed (spec §8). Falls back to the
/// media keys when Spotify hasn't registered a media session yet.
/// </summary>
internal sealed class WindowsMediaControl(IAppCatalog apps, ILauncher launcher) : IMediaControl
{
    private InstalledApp? Spotify => apps.Apps().FirstOrDefault(a => a.Name.Equals("Spotify", StringComparison.OrdinalIgnoreCase));

    public bool IsSpotifyInstalled => Spotify is not null;

    public bool IsSpotifyRunning => Process.GetProcessesByName("Spotify").Length > 0;

    public async Task LaunchSpotifyAsync(CancellationToken cancellationToken)
    {
        var spotify = Spotify ?? throw new ToolException("Spotify isn't installed");
        await launcher.LaunchAsync(spotify, cancellationToken);
        // Wait until it answers, up to 20 s: a just-launched Spotify ignores "play".
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSpotifyRunning && await Session() is not null) break;
            if (IsSpotifyRunning && clock.Elapsed > TimeSpan.FromSeconds(6)) break; // no session until something plays
            await Task.Delay(500, cancellationToken);
        }
        await Task.Delay(800, cancellationToken);
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> Session()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PlaybackState> StateAsync()
    {
        var session = await Session();
        return session?.GetPlaybackInfo().PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackState.Stopped,
            _ => PlaybackState.Unknown,
        };
    }

    public async Task PlayAsync()
    {
        if (await Session() is { } session) await session.TryPlayAsync();
        else Native.PressKeys(Native.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task PauseAsync()
    {
        if (await Session() is { } session) await session.TryPauseAsync();
        else throw new ToolException("Spotify isn't playing anything");
    }

    public async Task NextAsync()
    {
        if (await Session() is { } session) await session.TrySkipNextAsync();
        else Native.PressKeys(Native.VK_MEDIA_NEXT_TRACK);
    }

    public async Task PreviousAsync()
    {
        if (await Session() is { } session) await session.TrySkipPreviousAsync();
        else Native.PressKeys(Native.VK_MEDIA_PREV_TRACK);
    }

    /// <summary>Spotify's own slider in the Windows volume mixer.</summary>
    public void SetSpotifyVolume(int percent)
    {
        using var devices = new MMDeviceEnumerator();
        var sessions = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioSessionManager.Sessions;
        var ids = Process.GetProcessesByName("Spotify").Select(p => (uint)p.Id).ToHashSet();
        var found = false;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (!ids.Contains(sessions[i].GetProcessID)) continue;
            sessions[i].SimpleAudioVolume.Volume = Math.Clamp(percent, 0, 100) / 100f;
            found = true;
        }
        if (!found) throw new ToolException("Spotify isn't playing through this output");
    }

    public Task OpenUriAsync(string uri, CancellationToken cancellationToken)
    {
        if (!uri.StartsWith("spotify:", StringComparison.Ordinal)) throw new ToolException("That isn't a Spotify link");
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}
