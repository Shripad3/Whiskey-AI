namespace Alfred.Core;

/// <summary>
/// An app the user can start. <paramref name="LaunchId"/> is opaque to the
/// core: a Start menu parsing name on Windows, a path for a browser's exe.
/// </summary>
public sealed record InstalledApp(string Name, string LaunchId);

/// <summary>What is installed. Implemented per platform; faked in tests.</summary>
public interface IAppCatalog
{
    /// <summary>Everything in the Start menu, Store apps included.</summary>
    IReadOnlyList<InstalledApp> Apps(bool refresh = false);

    /// <summary>Browsers that can be handed a URL, by display name ("Google Chrome").</summary>
    IReadOnlyList<InstalledApp> Browsers();

    InstalledApp? DefaultBrowser();

    /// <summary>Built-in aliases plus the user's own (spoken key → app name).</summary>
    IReadOnlyDictionary<string, string> Aliases => AppNameMatcher.DefaultAliases;
}

/// <summary>Starts things. Never a shell or script host (spec §11).</summary>
public interface ILauncher
{
    Task LaunchAsync(InstalledApp app, CancellationToken cancellationToken);

    /// <summary>Opens an http(s) URL in <paramref name="browser"/>, or the default browser when null.</summary>
    Task OpenUrlAsync(string url, InstalledApp? browser, CancellationToken cancellationToken);
}

/// <summary>Volume, brightness, lock and sleep (spec §8, systemControl).</summary>
public interface ISystemControl
{
    /// <summary>The default output's volume, 0…1.</summary>
    float Volume();
    /// <summary>Returns the volume actually set. A non-zero volume unmutes, as the volume keys do.</summary>
    float SetVolume(float volume);
    void SetMuted(bool muted);
    /// <summary>The internal panel's brightness, 0…100.</summary>
    int Brightness();
    void SetBrightness(int percent);
    void Lock();
    void Sleep();
    /// <summary>Windows 11 has no public Do Not Disturb API: opens Settings › Notifications instead.</summary>
    void OpenNotificationSettings();
}

public enum PlaybackState { Unknown, Playing, Paused, Stopped }

/// <summary>
/// Spotify on this PC: transport through the system media controls, which
/// need no account, plus launching and handing it a <c>spotify:</c> URI.
/// </summary>
public interface IMediaControl
{
    bool IsSpotifyInstalled { get; }
    bool IsSpotifyRunning { get; }
    /// <summary>Starts Spotify in the background and waits until its media session appears.</summary>
    Task LaunchSpotifyAsync(CancellationToken cancellationToken);
    Task<PlaybackState> StateAsync();
    Task PlayAsync();
    Task PauseAsync();
    Task NextAsync();
    Task PreviousAsync();
    /// <summary>Spotify's own volume in the Windows mixer, 0…100.</summary>
    void SetSpotifyVolume(int percent);
    /// <summary>Opens a spotify: URI in the app ("spotify:track:…").</summary>
    Task OpenUriAsync(string uri, CancellationToken cancellationToken);
}

/// <summary>The clipboard, as text; nothing marked private is ever returned.</summary>
public interface IClipboard
{
    /// <summary>What's on it, described: text, files or an image. Null when a password manager marked it private.</summary>
    ClipboardContent Read();
    void Clear();
    /// <summary>Replaces the clipboard with its own plain text. False when there's no text.</summary>
    bool MakePlainText();
    /// <summary>Presses Ctrl+V in the app in front.</summary>
    void Paste();
}

public sealed record ClipboardContent(string? Text, IReadOnlyList<string> Files, bool HasImage, bool IsPrivate);

public sealed record Rect(double X, double Y, double Width, double Height)
{
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
}

/// <summary>Moves and sizes windows (spec §8, arrangeWindow).</summary>
public interface IWindowManager
{
    /// <summary>Running apps with a window, by name.</summary>
    IReadOnlyList<string> RunningApps();
    /// <summary>Arranges the named app's main window, or the one in front when null. Returns the app's name.</summary>
    string Arrange(string? app, WindowAction action);
}

public enum WindowAction { LeftHalf, RightHalf, TopHalf, BottomHalf, Maximize, Center, FullScreen, ExitFullScreen, Minimize, NextDisplay }

/// <summary>Everything the tools need from the platform and the app.</summary>
public sealed record AlfredServices
{
    public required SettingsStore Settings { get; init; }
    public required ClockStore Clock { get; init; }
    public required IAppCatalog Apps { get; init; }
    public required ILauncher Launcher { get; init; }
    public required ISystemControl System { get; init; }
    public required IMediaControl Media { get; init; }
    public required SpotifyWebApi Spotify { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required IWindowManager Windows { get; init; }
    public required FileServices Files { get; init; }
    public required IPlaceFinder Places { get; init; }
    public required ICalendarSource CalendarSource { get; init; }
    public required IReminderSink Reminders { get; init; }
    public required NoteBook Notes { get; init; }
}

public static class AppCatalogExtensions
{
    /// <summary>
    /// The installed app the user means, or null. Rescans once on a miss, so
    /// an app installed since the last scan is found.
    /// </summary>
    public static InstalledApp? Resolve(this IAppCatalog catalog, string spoken)
    {
        return Find(catalog.Apps()) ?? Find(catalog.Apps(refresh: true));

        InstalledApp? Find(IReadOnlyList<InstalledApp> apps)
        {
            var name = AppNameMatcher.Match(spoken, apps.Select(a => a.Name).ToList(), catalog.Aliases);
            return name is null ? null : apps.First(a => a.Name == name);
        }
    }

    public static InstalledApp? ResolveBrowser(this IAppCatalog catalog, string spoken)
    {
        var browsers = catalog.Browsers();
        var name = AppNameMatcher.Match(spoken, browsers.Select(a => a.Name).ToList(), catalog.Aliases);
        return name is null ? null : browsers.First(a => a.Name == name);
    }
}
