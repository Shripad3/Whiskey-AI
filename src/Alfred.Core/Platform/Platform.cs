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

/// <summary>Starts things. The only way tools reach the OS in W0.</summary>
public interface ILauncher
{
    Task LaunchAsync(InstalledApp app, CancellationToken cancellationToken);

    /// <summary>Opens an http(s) URL in <paramref name="browser"/>, or the default browser when null.</summary>
    Task OpenUrlAsync(string url, InstalledApp? browser, CancellationToken cancellationToken);
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
