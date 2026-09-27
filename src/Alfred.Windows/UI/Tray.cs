using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Alfred.Core;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace Alfred.Windows.UI;

/// <summary>
/// The tray icon, Windows' counterpart of the Mac's menu bar item: status,
/// running timers, alarms and the stopwatch with their controls, Pause
/// Listening (the kill switch), Settings and Quit (spec §5).
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly ContextMenu _menu = new();
    private readonly Func<TrayStatus> _status;
    private readonly ClockStore _clock;

    public event Action? TypeCommand;
    public event Action? OpenSettings;
    public event Action<bool>? PauseListening;
    public event Action? Quit;

    public Tray(Func<TrayStatus> status, ClockStore clock)
    {
        _status = status;
        _clock = clock;
        _menu.Opened += (_, _) => Build();
        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/alfred.ico")),
            ToolTipText = "Alfred",
            ContextMenu = _menu,
            MenuActivation = PopupActivationMode.LeftOrRightClick,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
        Build();
    }

    public void SetTooltip(string text) => _icon.ToolTipText = text;

    /// <summary>A balloon for things worth knowing once ("models missing").</summary>
    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    private void Build()
    {
        var status = _status();
        _menu.Items.Clear();
        Add(status.Headline, enabled: false);
        if (status.Detail is { Length: > 0 } detail) Add(detail, enabled: false);
        _menu.Items.Add(new Separator());
        Add($"Type a command…", () => TypeCommand?.Invoke());

        var clock = _clock.Snapshot;
        var now = DateTime.Now;
        if (!clock.IsEmpty) _menu.Items.Add(new Separator());
        foreach (var timer in clock.Timers)
        {
            var item = Submenu($"{ClockAlert.Capitalised(timer.Name)}: {ClockFormat.Clock(timer.Remaining(now))}{(timer.IsPaused ? " (paused)" : "")}");
            Add(item, timer.IsPaused ? "Resume" : "Pause", () =>
            {
                if (timer.IsPaused) _clock.Resume(timer.Id);
                else _clock.Pause(timer.Id);
            });
            Add(item, "Cancel", () => _clock.Remove([timer.Id]));
        }
        foreach (var alarm in clock.Alarms.OrderBy(a => a.FireDate))
        {
            var when = alarm.RepeatDays is { } days ? $"{ClockFormat.Time(alarm.FireDate)} {AlarmRepeat.Describe(days)}" : ClockFormat.When(alarm.FireDate, now);
            var item = Submenu($"Alarm {when}{(alarm.Label is { } l ? $" ({l})" : "")}{(alarm.IsEnabled ? "" : " (off)")}");
            Add(item, alarm.IsEnabled ? "Turn off" : "Turn on", () => _clock.SetEnabled(alarm.Id, !alarm.IsEnabled));
            Add(item, "Delete", () => _clock.Remove([alarm.Id]));
        }
        foreach (var reminder in clock.Reminders.OrderBy(r => r.FireDate))
        {
            var item = Submenu($"Reminder {ClockFormat.When(reminder.FireDate, now)}: {reminder.Label}");
            Add(item, "Delete", () => _clock.Remove([reminder.Id]));
        }
        if (!clock.Stopwatch.IsIdle)
        {
            var item = Submenu($"Stopwatch: {ClockFormat.Stopwatch(clock.Stopwatch.Elapsed(now))}");
            Add(item, clock.Stopwatch.IsRunning ? "Stop" : "Resume", _clock.ToggleStopwatch);
            Add(item, "Reset", _clock.ResetStopwatch);
        }

        _menu.Items.Add(new Separator());
        var pause = new MenuItem { Header = "Pause Listening", IsCheckable = true, IsChecked = status.Paused };
        pause.Click += (_, _) => PauseListening?.Invoke(pause.IsChecked);
        _menu.Items.Add(pause);
        Add("Settings…", () => OpenSettings?.Invoke());
        Add("Quit Alfred", () => Quit?.Invoke());
    }

    private MenuItem Submenu(string header)
    {
        var item = new MenuItem { Header = header };
        _menu.Items.Add(item);
        return item;
    }

    private void Add(string header, Action? click = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (click is not null) item.Click += (_, _) => click();
        _menu.Items.Add(item);
    }

    private static void Add(MenuItem parent, string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        parent.Items.Add(item);
    }

    public void Dispose() => _icon.Dispose();
}

internal sealed record TrayStatus(string Headline, string? Detail, bool Paused);

/// <summary>A small box to type a command, as if spoken: handy in a quiet office, and for testing.</summary>
internal sealed class CommandBox : Window
{
    public CommandBox(Func<string, Task> run)
    {
        Title = "Alfred";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        var box = new TextBox { FontSize = 16, Padding = new Thickness(8), Margin = new Thickness(12) };
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
            if (e.Key != System.Windows.Input.Key.Enter || box.Text.Trim().Length == 0) return;
            var text = box.Text.Trim();
            Close();
            await run(text);
        };
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Type a command, then press Enter", Margin = new Thickness(12, 12, 12, 0), Foreground = System.Windows.Media.Brushes.Gray },
                box,
            },
        };
        Loaded += (_, _) => box.Focus();
    }
}
