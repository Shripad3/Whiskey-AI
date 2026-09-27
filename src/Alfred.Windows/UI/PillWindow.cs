using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Alfred.Core;
using Alfred.Windows.Platform;

namespace Alfred.Windows.UI;

/// <summary>
/// The overlay pill (spec §5): borderless, transparent, top-most, never takes
/// focus, out of Alt+Tab, click-through unless it shows buttons. Sits at the
/// top centre of the laptop's own panel. Renders whatever the coordinator
/// says; decides nothing.
/// </summary>
internal sealed class PillWindow : Window
{
    private const double CanvasWidth = 560;
    private const double CanvasHeight = 420;

    private readonly Border _pill;
    private readonly StackPanel _content;
    private readonly List<Border> _bars = [];
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool? _clickThrough;
    private bool _expanded;

    /// <summary>What the user clicked, for the app to pass to the coordinator.</summary>
    public event Action<string>? Selected;
    public event Action<bool>? Confirmed;
    public event Action<bool>? AlertResolved;
    public event Action<string>? OpenSettings;
    public event Action<Guid>? CancelCountdown;

    public Func<ClockSnapshot>? Clock { get; set; }

    public PillWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = CanvasWidth;
        Height = CanvasHeight;
        Title = "Alfred";
        Focusable = false;

        _content = new StackPanel();
        _pill = new Border
        {
            Background = Theme.Background, CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 10, 18, 12),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0),
            MinWidth = 180, MaxWidth = CanvasWidth - 40, Effect = Theme.Shadow, Child = _content, Opacity = 0,
        };
        _pill.MouseEnter += (_, _) => SetExpanded(true);
        _pill.MouseLeave += (_, _) => SetExpanded(false);
        Content = new Grid { Children = { _pill } };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.25) };
        _tick.Tick += (_, _) => RefreshIdle();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
            SetClickThrough(true);
        };
    }

    public AssistantState Current { get; private set; } = AssistantState.IdleState;

    /// <summary>Where to put the pill, in physical pixels: the internal panel, or the primary monitor.</summary>
    public void Place(Native.RECT work)
    {
        if (_hwnd == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)(CanvasWidth * dpi.DpiScaleX);
        var height = (int)(CanvasHeight * dpi.DpiScaleY);
        var x = work.Left + (work.Width - width) / 2;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, work.Top, width, height, Native.SWP_NOACTIVATE);
    }

    public void Render(AssistantState state)
    {
        Current = state;
        _expanded = false;
        _content.Children.Clear();
        _bars.Clear();
        switch (state)
        {
            case AssistantState.Idle:
                RefreshIdle();
                return;
            case AssistantState.Listening listening:
                ShowListening(listening.Partial);
                break;
            case AssistantState.Thinking thinking:
                Line(Theme.Icon("", Theme.Accent), Theme.Label(thinking.Transcript.Length == 0 ? "Thinking…" : $"“{thinking.Transcript}”", 14, Theme.Secondary));
                break;
            case AssistantState.Acting acting:
                Line(Theme.Icon(Theme.ToolGlyph(acting.Tool), Theme.Accent), Theme.Label(Join(acting.Title, acting.Target)));
                break;
            case AssistantState.Result result:
                Line(Theme.Icon(Theme.Glyph.Check, Theme.Success), Theme.Label(result.Text));
                break;
            case AssistantState.Reply reply:
                Line(Theme.Icon(Theme.Glyph.Chat, Theme.Accent), Theme.Label(reply.Text));
                break;
            case AssistantState.List list:
                Line(Theme.Icon(Theme.Glyph.Folder, Theme.Accent), Theme.Label(list.Text, 13, Theme.Secondary));
                foreach (var item in list.Items)
                    _content.Children.Add(Theme.Row(Theme.ItemGlyph(item.Symbol), item.Title, item.Detail, () => Selected?.Invoke(item.Id)));
                break;
            case AssistantState.Confirm confirm:
                Line(Theme.Icon(Theme.Glyph.Warning, Theme.Warning), Theme.Label(confirm.Text));
                foreach (var item in confirm.Items.Take(8))
                    _content.Children.Add(Theme.Row(Theme.ItemGlyph(item.Symbol), item.Title, item.Detail, null));
                if (confirm.Items.Count > 8) _content.Children.Add(Theme.Label($"and {confirm.Items.Count - 8} more", 12, Theme.Secondary));
                Buttons(Theme.Button("Confirm", () => Confirmed?.Invoke(true), primary: true), Theme.Button("Cancel", () => Confirmed?.Invoke(false)));
                break;
            case AssistantState.Alert alert:
                Line(Theme.Icon(alert.Clock.Kind == CountdownKind.Timer ? Theme.Glyph.Timer : Theme.Glyph.Ringer, Theme.Warning, 18),
                    Theme.Label(alert.Clock.Title, 15, weight: FontWeights.SemiBold));
                _content.Children.Add(Theme.Label(alert.Clock.Message, 13, Theme.Secondary));
                var stop = Theme.Button("Stop", () => AlertResolved?.Invoke(false), primary: true);
                if (alert.Clock.CanSnooze) Buttons(stop, Theme.Button($"Snooze {ClockStore.SnoozeMinutes} min", () => AlertResolved?.Invoke(true)));
                else Buttons(stop);
                break;
            case AssistantState.Error error:
                Line(Theme.Icon(Theme.Glyph.Error, Theme.Danger), Theme.Label(error.Message));
                if (error.SettingsPage is { } page) Buttons(Theme.Button("Open Settings", () => OpenSettings?.Invoke(page)));
                break;
        }
        SetClickThrough(state is not (AssistantState.List or AssistantState.Confirm or AssistantState.Alert or AssistantState.Error { SettingsPage: not null }));
        Appear(true);
    }

    public void AudioLevel(float level)
    {
        if (_bars.Count == 0) return;
        var shape = new[] { 0.55, 0.8, 1.0, 0.8, 0.55 };
        for (var i = 0; i < _bars.Count; i++)
            _bars[i].Height = 4 + 18 * Math.Clamp(level * shape[i] * (0.8 + Random.Shared.NextDouble() * 0.4), 0, 1);
    }

    private void ShowListening(string partial)
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Height = 22 };
        for (var i = 0; i < 5; i++)
        {
            var bar = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Accent, Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
            _bars.Add(bar);
            bars.Children.Add(bar);
        }
        Line(Theme.Icon(Theme.Glyph.Microphone, Theme.Accent), bars, Theme.Label(partial.Length == 0 ? "Listening…" : partial, 14, Theme.Secondary));
    }

    /// <summary>Idle: hidden, unless a timer or the stopwatch is running (a small countdown), which expands on hover.</summary>
    private void RefreshIdle()
    {
        if (Current is not AssistantState.Idle) return;
        // Rebuilding under a press would swallow the click.
        if (_expanded && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
        var clock = Clock?.Invoke();
        var ticking = clock is { IsTicking: true };
        if (!ticking)
        {
            _tick.Stop();
            _content.Children.Clear();
            Appear(false);
            return;
        }
        _tick.Start();
        _content.Children.Clear();
        var now = DateTime.Now;
        var timers = clock!.Timers.Where(t => !t.IsPaused).ToList();
        var next = timers.MinBy(t => t.Remaining(now));
        var summary = next is not null ? ClockFormat.Clock(next.Remaining(now)) : ClockFormat.Stopwatch(clock.Stopwatch.Elapsed(now));
        Line(Theme.Icon(next is not null ? Theme.Glyph.Timer : Theme.Glyph.Stopwatch, Theme.Accent, 14), Theme.Label(summary, 14, wrap: false, weight: FontWeights.SemiBold));
        if (_expanded)
        {
            foreach (var timer in clock.Timers)
            {
                var row = Theme.Row(Theme.Glyph.Timer, ClockAlert.Capitalised(timer.Name), ClockFormat.Clock(timer.Remaining(now)) + (timer.IsPaused ? " (paused)" : ""), null);
                _content.Children.Add(row);
                _content.Children.Add(Theme.Button("Cancel", () => CancelCountdown?.Invoke(timer.Id)));
            }
            if (!clock.Stopwatch.IsIdle)
                _content.Children.Add(Theme.Row(Theme.Glyph.Stopwatch, "Stopwatch", ClockFormat.Stopwatch(clock.Stopwatch.Elapsed(now)), null));
        }
        _pill.MinWidth = _expanded ? 260 : 96;
        SetClickThrough(false);
        Appear(true);
    }

    private void SetExpanded(bool expanded)
    {
        if (Current is not AssistantState.Idle || _expanded == expanded) return;
        _expanded = expanded;
        RefreshIdle();
    }

    private void Line(params UIElement[] parts)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        foreach (var part in parts)
        {
            if (part is TextBlock { TextWrapping: TextWrapping.Wrap } text) text.MaxWidth = CanvasWidth - 110;
            row.Children.Add(part);
        }
        _content.Children.Add(row);
        _pill.MinWidth = 180;
    }

    private void Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons) row.Children.Add(button);
        _content.Children.Add(row);
    }

    private static string Join(string title, string target) => target.Length == 0 ? title : $"{title}: {target}";

    private void Appear(bool visible)
    {
        if (visible && !IsVisible) Show();
        var animation = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 140 : 220)) { EasingFunction = new QuadraticEase() };
        if (!visible) animation.Completed += (_, _) =>
        {
            if (_pill.Opacity == 0 && Current is AssistantState.Idle && _content.Children.Count == 0) Hide();
        };
        _pill.BeginAnimation(OpacityProperty, animation);
    }

    public void HideNow()
    {
        _pill.BeginAnimation(OpacityProperty, null);
        _pill.Opacity = 0;
        Hide();
    }

    private void SetClickThrough(bool through)
    {
        if (_hwnd == 0 || _clickThrough == through) return;
        _clickThrough = through;
        var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        style = through ? style | Native.WS_EX_TRANSPARENT : style & ~(nint)Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, style);
    }
}
