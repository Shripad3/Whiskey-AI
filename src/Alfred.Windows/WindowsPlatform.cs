using System.Diagnostics;
using System.Text;
using Alfred.Core;
using Microsoft.Win32;

namespace Alfred.Windows;

/// <summary>
/// The Start menu as the user sees it: the <c>shell:AppsFolder</c> virtual
/// folder lists Start menu shortcuts and Store apps together, without admin
/// rights (spec §8, openApp). Scanned at startup and again when a name isn't
/// found, so a newly installed app turns up.
/// </summary>
internal sealed class WindowsAppCatalog : IAppCatalog
{
    private static readonly string[] Junk = ["uninstall", "readme", "read me", "help", "documentation", "release notes", "website"];
    private IReadOnlyList<InstalledApp>? _apps;
    private IReadOnlyList<InstalledApp>? _browsers;

    public IReadOnlyList<InstalledApp> Apps(bool refresh = false)
    {
        if (refresh || _apps is null) _apps = OnStaThread(ScanAppsFolder);
        return _apps;
    }

    private static List<InstalledApp> ScanAppsFolder()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("No Shell.Application");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        foreach (var item in folder.Items())
        {
            string name = item.Name;
            string parsingName = item.Path;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsingName)) continue;
            // Web links and documents that installers drop into the Start menu.
            if (parsingName.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetExtension(parsingName).ToLowerInvariant() is ".url" or ".txt" or ".chm" or ".pdf" or ".html" or ".htm") continue;
            var lower = name.ToLowerInvariant();
            if (Junk.Any(lower.Contains)) continue;
            if (seen.Add(name)) apps.Add(new InstalledApp(name, parsingName));
        }
        return apps;
    }

    /// <summary>
    /// Browsers register under Clients\StartMenuInternet with their exe, which
    /// is what's needed to hand one a URL.
    /// </summary>
    public IReadOnlyList<InstalledApp> Browsers() => _browsers ??= ScanBrowsers();

    private static List<InstalledApp> ScanBrowsers()
    {
        var browsers = new List<InstalledApp>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
            if (clients is null) continue;
            foreach (var id in clients.GetSubKeyNames())
            {
                using var client = clients.OpenSubKey(id);
                using var command = clients.OpenSubKey($@"{id}\shell\open\command");
                var name = client?.GetValue(null) as string ?? id;
                if (command?.GetValue(null) is not string line || ExecutableOf(line) is not { } exe) continue;
                if (browsers.All(b => !b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    browsers.Add(new InstalledApp(name, exe));
            }
        }
        return browsers;
    }

    public InstalledApp? DefaultBrowser()
    {
        uint length = 1024;
        var buffer = new StringBuilder((int)length);
        if (Native.AssocQueryStringW(Native.ASSOCF_IS_PROTOCOL, Native.ASSOCSTR_EXECUTABLE, "https", "open", buffer, ref length) != 0)
            return null;
        var exe = buffer.ToString();
        return Browsers().FirstOrDefault(b => string.Equals(b.LaunchId, exe, StringComparison.OrdinalIgnoreCase))
            ?? new InstalledApp(FileVersionInfo.GetVersionInfo(exe).ProductName ?? Path.GetFileNameWithoutExtension(exe), exe);
    }

    private static string? ExecutableOf(string commandLine)
    {
        var line = commandLine.Trim();
        var exe = line.StartsWith('"') ? line[1..line.IndexOf('"', 1)] : line.Split(' ')[0];
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Shell.Application is an apartment-threaded COM object.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw new InvalidOperationException("Couldn't read the Start menu", error);
    }
}

/// <summary>
/// Starts apps and opens web pages. Never a shell or script host (spec §11):
/// apps go through their Start menu entry, and URLs are checked again here as
/// the last line of defence.
/// </summary>
internal sealed class WindowsLauncher : ILauncher
{
    public Task LaunchAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        // Explorer resolves any AppsFolder item, desktop or Store app alike.
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(@"shell:AppsFolder\" + app.LaunchId);
        Process.Start(start)?.Dispose();
        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url, InstalledApp? browser, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            throw new ToolException($"\"{url}\" isn't a web address I can open");
        ProcessStartInfo start;
        if (browser is null)
        {
            start = new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true };
        }
        else
        {
            start = new ProcessStartInfo(browser.LaunchId) { UseShellExecute = false };
            start.ArgumentList.Add(parsed.AbsoluteUri);
        }
        Process.Start(start)?.Dispose();
        return Task.CompletedTask;
    }
}
