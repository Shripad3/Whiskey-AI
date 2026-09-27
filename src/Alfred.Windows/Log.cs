using System.Text;

namespace Alfred.Windows;

/// <summary>
/// A plain text log per day in %LOCALAPPDATA%\Alfred\logs, kept for a week.
/// Commands are logged so problems can be reported; nothing leaves the PC.
/// </summary>
internal static class Log
{
    private static readonly object Lock = new();
    private static string? _folder;

    public static string Folder => _folder ??= Path.Combine(App.DataFolder, "logs");

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("warn", message);

    public static void Error(string message, Exception? error = null) =>
        Write("error", error is null ? message : $"{message}: {error}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, $"alfred-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }

    public static void Prune()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "alfred-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7)) File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
