using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Alfred.Core;
using Alfred.Windows.Audio;
using Alfred.Windows.Model;
using Alfred.Windows.Platform;

namespace Alfred.Windows.UI;

internal sealed record SettingsContext(
    SettingsStore Settings, ClockStore Clock, Accounts Accounts, SpotifyWebApi Spotify, FileServices Files, ToolRegistry Tools,
    ModelHost Model, WakeHost Wake, Speaker Speaker, string ModelsFolder, Action<ClockAlert> TestAlert);

/// <summary>Settings (spec §5), one tab per Mac pane. Every change is saved at once.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly SettingsContext _c;
    private readonly TabControl _tabs = new() { Margin = new Thickness(8) };
    private SettingsStore Store => _c.Settings;
    private Settings S => _c.Settings.Current;

    public SettingsWindow(SettingsContext context)
    {
        _c = context;
        Title = "Alfred Settings";
        Width = 720;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Theme.Text;
        FontSize = 13;
        Content = _tabs;
        Add("General", General);
        Add("Model", ModelPage);
        Add("Capabilities", Capabilities);
        Add("Routines", RoutinesPage);
        Add("Clock", ClockPage);
        Add("Calendar", CalendarPage);
        Add("Files", FilesPage);
        Add("Spotify", SpotifyPage);
        Add("Search & Weather", SearchPage);
    }

    public void Show(string? page)
    {
        if (page is not null && _tabs.Items.OfType<TabItem>().FirstOrDefault(t => (string)t.Header == page) is { } tab) tab.IsSelected = true;
        Show();
        Activate();
    }

    /// <summary>Each tab is built when first shown, and again when its data changes.</summary>
    private void Add(string header, Func<UIElement> build)
    {
        var tab = new TabItem { Header = header, Tag = build };
        _tabs.Items.Add(tab);
        if (_tabs.Items.Count == 1)
        {
            tab.Content = Page(build());
            _tabs.SelectionChanged += (_, e) =>
            {
                if (e.Source == _tabs && _tabs.SelectedItem is TabItem { Content: null, Tag: Func<UIElement> make } selected) selected.Content = Page(make());
            };
        }
    }

    private void Rebuild(Func<UIElement> build)
    {
        if (_tabs.SelectedItem is TabItem tab) tab.Content = Page(build());
    }

    private static ScrollViewer Page(UIElement content) => new() { Content = content, Padding = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    // Building blocks

    private static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 8) };

    private static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var child in children)
        {
            if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(child);
        }
        return row;
    }

    private static TextBlock Label(string text, double width = 150) => new() { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center };

    private static TextBox Field(string? value, Action<string> changed, double width = 320)
    {
        var box = new TextBox { Text = value ?? "", Width = width, Padding = new Thickness(4) };
        box.LostFocus += (_, _) => changed(box.Text.Trim());
        return box;
    }

    private static ComboBox Choice<T>(IEnumerable<(T Value, string Title)> options, T current, Action<T> changed, double width = 220)
    {
        var box = new ComboBox { Width = width };
        foreach (var (value, title) in options) box.Items.Add(new ComboBoxItem { Content = title, Tag = value, IsSelected = Equals(value, current) });
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is T value) changed(value);
            else if (item.Tag is null && default(T) is null) changed(default!); // the "none" option
        };
        return box;
    }

    private static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4) };
        button.Click += (_, _) => click();
        return button;
    }

    private static Button AsyncButton(string text, Func<Button, Task> click)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await click(button); }
            catch (Exception e) { MessageBox.Show(e.Message, "Alfred", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    // Pages

    private UIElement General()
    {
        var hotkey = Field(S.Hotkey, text =>
        {
            var chord = HotkeyText(text);
            if (chord is null) return;
            Store.Update(s => s.Hotkey = chord);
        }, 200);
        return Stack(
            Heading("Activation"),
            Row(Label("Hold to talk"), hotkey),
            Note($"Hold the keys, speak, let go. Now: {Chord.Parse(S.Hotkey)}. Needs Ctrl, Alt or Win plus a key; takes effect after restarting Alfred."),
            Check($"Wake word: say “{WakePhrase.DisplayName}, …”", S.WakeWordEnabled, v => Store.Update(s => s.WakeWordEnabled = v)),
            Check("Also listen for the wake word on battery", S.WakeWordOnBattery, v => Store.Update(s => s.WakeWordOnBattery = v)),
            Note($"Wake word: {_c.Wake.Status}. The microphone stays open while it listens; nothing leaves this PC."),
            Heading("Voice"),
            Row(Label("Speak responses"), Choice([(SpokenResponses.Always, "Always"), (SpokenResponses.ErrorsOnly, "Errors and answers"), (SpokenResponses.Never, "Never")],
                S.Speak, v => Store.Update(s => s.Speak = v))),
            Row(Label("Voice"), Choice(new[] { ((string?)null, "System default") }.Concat(Speaker.Voices().Select(v => ((string?)v, v))), S.Voice,
                v => Store.Update(s => s.Voice = v), 300), Button("Preview", () => _c.Speaker.Say("Good evening. What can I do for you?", S.Voice))),
            Note("More natural voices: Windows Settings › Time & language › Speech › Manage voices."),
            Check("Lower other apps' audio while I listen", S.DuckAudio, v => Store.Update(s => s.DuckAudio = v)),
            Heading("Display"),
            Row(Label("Lid closed"), Choice([(LidClosedBehaviour.Floating, "Show on the main monitor"), (LidClosedBehaviour.Hide, "Hide, and speak results"),
                (LidClosedBehaviour.Disable, "Stop listening")], S.LidClosed, v => Store.Update(s => s.LidClosed = v), 260)),
            Heading("Start-up"),
            Check("Start Alfred when I sign in to Windows", StartWithWindows.Enabled, v => StartWithWindows.Enabled = v),
            Row(Button("Open the log folder", () => Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true })?.Dispose())));
    }

    private static string? HotkeyText(string text)
    {
        var chord = Chord.Parse(text);
        return chord == Chord.Default && !text.Replace(" ", "").Equals("Ctrl+Alt+Space", StringComparison.OrdinalIgnoreCase) ? null : chord.ToString();
    }

    private UIElement ModelPage()
    {
        var stack = Stack(
            Heading("Models"),
            Note($"Everything runs on this PC. Files live in {_c.ModelsFolder}. Each download is checked against its SHA-256 before it's used."),
            Note($"Language model: {_c.Model.Status}. Free GPU memory: {(ModelHost.FreeVramMegabytes() is { } mb ? $"{mb:N0} MB" : "unknown")}."));
        foreach (var model in ModelFile.All)
        {
            var path = model.PathIn(_c.ModelsFolder);
            var have = File.Exists(path);
            var progress = new ProgressBar { Width = 140, Height = 10, Visibility = Visibility.Collapsed };
            var status = new TextBlock { Text = have ? "Downloaded" : model.SizeText, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            var download = AsyncButton(have ? "Verify" : "Download", async button =>
            {
                if (File.Exists(path))
                {
                    status.Text = "Checking…";
                    var ok = await ModelDownloader.VerifyAsync(path, model.Sha256, CancellationToken.None);
                    status.Text = ok ? "Verified" : "Damaged";
                    if (!ok) File.Delete(path);
                    return;
                }
                progress.Visibility = Visibility.Visible;
                status.Text = "0%";
                var report = new Progress<double>(p =>
                {
                    progress.Value = p * 100;
                    status.Text = $"{p:P0}";
                });
                await ModelDownloader.DownloadAsync(model, _c.ModelsFolder, report, CancellationToken.None);
                status.Text = "Downloaded";
                progress.Visibility = Visibility.Collapsed;
                _c.Wake.Update();
            });
            stack.Children.Add(Row(new TextBlock { Text = model.Name, Width = 170, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = model.Purpose, Width = 230, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                status, download, progress));
        }
        stack.Children.Add(Heading("Which language model"));
        stack.Children.Add(Row(Label("Use"), Choice(ModelFile.All.Where(m => m.File.EndsWith(".gguf", StringComparison.Ordinal)).Select(m => (m.File, m.Name)),
            S.ModelFile, v => Store.Update(s => s.ModelFile = v), 260)));
        stack.Children.Add(Note("The 8B model is stronger but needs about 6.5 GB of free GPU memory; it falls back to the CPU (slower) when a game is running."));
        var overall = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Row(AsyncButton("Download everything needed", async _ =>
        {
            var missing = ModelFile.Essential.Where(m => !File.Exists(m.PathIn(_c.ModelsFolder))).ToList();
            for (var i = 0; i < missing.Count; i++)
            {
                var model = missing[i];
                var step = $"{i + 1} of {missing.Count}: {model.Name}";
                await ModelDownloader.DownloadAsync(model, _c.ModelsFolder, new Progress<double>(p => overall.Text = $"{step} ({p:P0})"), CancellationToken.None);
            }
            overall.Text = "All downloaded";
            _c.Wake.Update();
            Rebuild(ModelPage);
        }), overall));
        return stack;
    }

    private UIElement Capabilities()
    {
        var stack = Stack(Heading("What Alfred may do"), Note("A capability that's off is never offered to the model, so it can't be used by mistake."));
        foreach (var tool in _c.Tools.Tools)
        {
            var name = tool.Name;
            stack.Children.Add(Check($"{tool.Title}", S.IsEnabled(name), on => Store.Update(s =>
            {
                if (on) s.DisabledTools.Remove(name);
                else s.DisabledTools.Add(name);
            })));
        }
        stack.Children.Add(Heading("App names"));
        stack.Children.Add(Note("Your own names for apps, one per line: spoken = app name. For example: music = Spotify"));
        var aliases = new TextBox { AcceptsReturn = true, Height = 90, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = string.Join(Environment.NewLine, S.AppAliases.Select(a => $"{a.Key} = {a.Value}")) };
        aliases.LostFocus += (_, _) => Store.Update(s => s.AppAliases = aliases.Text.Split('\n')
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0)
            .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.First()[1].Trim()));
        stack.Children.Add(aliases);
        return stack;
    }

    private UIElement RoutinesPage()
    {
        var stack = Stack(Heading("Routines"), Note("Your own phrases that run several steps. “I'm home” → a playlist, VS Code. They run without the model, as written."));
        foreach (var routine in S.Routines) stack.Children.Add(RoutineEditor(routine));
        stack.Children.Add(Row(
            Button("Add a routine", () => Structural(s => s.Routines.Add(new Routine { Name = "New routine", Triggers = ["my phrase"] }))),
            Button("Add the examples", () => Structural(s => s.Routines.AddRange(Routines.Examples.Where(e => s.Routines.All(r => r.Name != e.Name)))))));
        return stack;
    }

    /// <summary>Changes that add or remove parts of the page redraw it; edits to a field don't, so focus stays put.</summary>
    private void Structural(Action<Settings> change)
    {
        Store.Update(change);
        Rebuild(RoutinesPage);
    }

    private UIElement RoutineEditor(Routine routine)
    {
        var id = routine.Id;
        void Change(Action<Routine> change) => Store.Update(s =>
        {
            if (s.Routines.Find(r => r.Id == id) is { } r) change(r);
        });
        void Reshape(Action<Routine> change) => Structural(s =>
        {
            if (s.Routines.Find(r => r.Id == id) is { } r) change(r);
        });
        var box = new GroupBox { Header = routine.Name, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(8) };
        var stack = Stack(
            Row(Label("Name", 90), Field(routine.Name, v => Change(r => r.Name = v), 240), Check("On", routine.Enabled, v => Change(r => r.Enabled = v)),
                Button("Delete", () => Structural(s => s.Routines.RemoveAll(r => r.Id == id)))),
            Row(Label("Phrases", 90), Field(string.Join(", ", routine.Triggers), v => Change(r => r.Triggers = [.. v.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)]), 420)),
            Row(Label("Then say", 90), Field(routine.Response, v => Change(r => r.Response = v), 420)));
        for (var i = 0; i < routine.Steps.Count; i++)
        {
            var index = i;
            var step = routine.Steps[i];
            var kinds = Choice(Enum.GetValues<RoutineStepKind>().Select(k => (k, new RoutineStep(k).Title)), step.Kind,
                k => Reshape(r => r.Steps[index] = r.Steps[index] with { Kind = k }), 160);
            var row = Row(new TextBlock { Text = $"{i + 1}.", Width = 20, VerticalAlignment = VerticalAlignment.Center }, kinds);
            if (RoutineStep.TextLabel(step.Kind) is { } label)
                row.Children.Add(Field(step.Text, v => Change(r => r.Steps[index] = r.Steps[index] with { Text = v }), 220));
            if (RoutineStep.TakesPercent(step.Kind))
                row.Children.Add(Field(step.Percent.ToString(), v => Change(r => r.Steps[index] = r.Steps[index] with { Percent = int.TryParse(v, out var p) ? Math.Clamp(p, 0, 100) : 50 }), 60));
            row.Children.Add(Button("Remove", () => Reshape(r => r.Steps.RemoveAt(index))));
            stack.Children.Add(row);
        }
        stack.Children.Add(Button("Add a step", () => Reshape(r => r.Steps.Add(new RoutineStep(RoutineStepKind.OpenApp)))));
        box.Content = stack;
        return box;
    }

    private UIElement ClockPage()
    {
        var stack = Stack(Heading("Alarms"));
        var alarms = _c.Clock.Snapshot.Alarms.OrderBy(a => a.FireDate).ToList();
        if (alarms.Count == 0) stack.Children.Add(Note("No alarms. Say “wake me up at 7 on weekdays”, or add one here."));
        foreach (var alarm in alarms)
        {
            var id = alarm.Id;
            var when = alarm.RepeatDays is { } days ? $"{ClockFormat.Time(alarm.FireDate)} {AlarmRepeat.Describe(days)}" : ClockFormat.When(alarm.FireDate, DateTime.Now);
            stack.Children.Add(Row(Check(when + (alarm.Label is { } l ? $" · {l}" : ""), alarm.IsEnabled, on => _c.Clock.SetEnabled(id, on)),
                Button("Delete", () =>
                {
                    _c.Clock.Remove([id]);
                    Rebuild(ClockPage);
                })));
        }
        var hour = new TextBox { Width = 40, Text = "7" };
        var minute = new TextBox { Width = 40, Text = "00" };
        var name = new TextBox { Width = 140 };
        var weekdays = new CheckBox { Content = "Weekdays", VerticalAlignment = VerticalAlignment.Center };
        var weekends = new CheckBox { Content = "Weekends", VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Heading("Add an alarm"));
        stack.Children.Add(Row(Label("Time (24 h)", 90), hour, new TextBlock { Text = ":" }, minute, Label("Name", 40), name, weekdays, weekends));
        stack.Children.Add(Row(Button("Add", () =>
        {
            if (!int.TryParse(hour.Text, out var h) || !int.TryParse(minute.Text, out var m) || h is < 0 or > 23 || m is < 0 or > 59) return;
            var days = new HashSet<int>();
            if (weekdays.IsChecked == true) days.UnionWith(AlarmRepeat.Weekdays);
            if (weekends.IsChecked == true) days.UnionWith([1, 7]);
            _c.Clock.AddAlarm(h, m, name.Text, days);
            Rebuild(ClockPage);
        }), Button("Test an alarm", () => _c.TestAlert(ClockAlert.Test(CountdownKind.Alarm))), Button("Test a timer", () => _c.TestAlert(ClockAlert.Test(CountdownKind.Timer)))));
        stack.Children.Add(Note("Alarms ring here with a chime and the voice. As a backup, Windows shows a toast when Alfred isn't running, but not while the laptop is asleep."));
        return stack;
    }

    private UIElement CalendarPage()
    {
        var provider = S.Calendar;
        var status = provider == CalendarProvider.None ? "Not connected" :
            _c.Accounts.Session(provider)?.IsSignedIn == true ? $"Connected to {provider}" : $"{provider} chosen, not signed in";
        return Stack(
            Heading("Calendar and reminders"),
            Note($"{status}. Alfred reads your calendar (never changes it) and adds reminders to the same account's task list. " +
                 "With nothing connected, reminders ring in Alfred."),
            Heading("Outlook (Microsoft 365, Outlook.com)"),
            Note("1. Go to entra.microsoft.com › App registrations › New registration. Accounts: “any organizational directory and personal Microsoft accounts”.\n" +
                 "2. Redirect URI: platform “Public client/native”, http://localhost\n" +
                 "3. API permissions: Microsoft Graph › Delegated › Calendars.Read and Tasks.ReadWrite.\n" +
                 "4. Copy the Application (client) ID here."),
            Row(Label("Client ID", 90), Field(S.OutlookClientId, v => Store.Update(s => s.OutlookClientId = v)),
                AsyncButton("Connect", async _ =>
                {
                    await _c.Accounts.ConnectAsync(CalendarProvider.Outlook, CancellationToken.None);
                    Rebuild(CalendarPage);
                })),
            Heading("Google"),
            Note("1. console.cloud.google.com › new project › enable the Google Calendar API and Google Tasks API.\n" +
                 "2. OAuth consent screen: External, add yourself as a test user.\n3. Credentials › OAuth client ID › Desktop app. Copy its ID and secret here.\n" +
                 "While the app is in Testing, Google asks you to sign in again every 7 days."),
            Row(Label("Client ID", 90), Field(S.GoogleClientId, v => Store.Update(s => s.GoogleClientId = v))),
            Row(Label("Client secret", 90), SecretField(_c.Accounts.HasGoogleSecret, _c.Accounts.SetGoogleSecret),
                AsyncButton("Connect", async _ =>
                {
                    await _c.Accounts.ConnectAsync(CalendarProvider.Google, CancellationToken.None);
                    Rebuild(CalendarPage);
                })),
            Row(Button("Disconnect", () =>
            {
                _c.Accounts.Disconnect();
                Rebuild(CalendarPage);
            })));
    }

    private static PasswordBox SecretField(bool saved, Action<string> changed)
    {
        var box = new PasswordBox { Width = 320, Padding = new Thickness(4), Password = saved ? "••••••••" : "" };
        box.LostFocus += (_, _) =>
        {
            if (box.Password != "••••••••") changed(box.Password);
        };
        return box;
    }

    private UIElement FilesPage()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stack = Stack(Heading("Folders Alfred may use"),
            Note("File tools look only here, by name, type and date, never inside files. Deleting means the Recycle Bin, and every change can be undone."));
        foreach (var folder in new[] { "Documents", "Downloads", "Desktop", "Pictures", "Videos", "Music" })
        {
            var name = folder;
            stack.Children.Add(Check(Path.Combine(home, folder), S.FileRoots.Contains(name), on => Store.Update(s =>
            {
                s.FileRoots.Remove(name);
                if (on) s.FileRoots.Add(name);
            })));
        }
        stack.Children.Add(Heading("Recent changes"));
        var history = _c.Files.Organizer.Journal.History.Take(20).ToList();
        if (history.Count == 0) stack.Children.Add(Note("Nothing changed yet."));
        foreach (var entry in history)
        {
            var id = entry.Id;
            var row = Row(new TextBlock { Text = $"{entry.Date:g}  {entry.Summary}", Width = 480, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            if (entry.State != FileJournal.Status.Undone && entry.Steps.Count > 0)
            {
                row.Children.Add(Button("Undo", () =>
                {
                    try { MessageBox.Show(_c.Files.Organizer.Undo(id), "Alfred"); }
                    catch (ToolException e) { MessageBox.Show(e.Message, "Alfred"); }
                    Rebuild(FilesPage);
                }));
            }
            else if (entry.State == FileJournal.Status.Undone)
            {
                row.Children.Add(new TextBlock { Text = "undone", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            }
            stack.Children.Add(row);
        }
        return stack;
    }

    private UIElement SpotifyPage() => Stack(
        Heading("Spotify"),
        Note("Play, pause, next and previous work without any setup. To play songs and playlists by name, connect your own Spotify app (free):"),
        Note("1. developer.spotify.com › Dashboard › Create app. Tick “Web API”.\n" +
             $"2. Redirect URI, exactly: {SpotifyWebApi.RegisteredRedirectUri}\n" +
             "3. Settings › User Management: add your Spotify account's email.\n4. Copy the Client ID here, then Connect."),
        Row(Label("Client ID", 90), Field(S.SpotifyClientId, v => Store.Update(s => s.SpotifyClientId = v)),
            Button("Copy redirect URI", () => Clipboard.SetText(SpotifyWebApi.RegisteredRedirectUri))),
        Row(AsyncButton(_c.Spotify.IsSignedIn ? "Reconnect" : "Connect", async _ =>
        {
            await _c.Spotify.Session.SignInAsync(CancellationToken.None);
            Rebuild(SpotifyPage);
        }), Button("Disconnect", () =>
        {
            _c.Spotify.Session.SignOut();
            Rebuild(SpotifyPage);
        })),
        Note(_c.Spotify.IsSignedIn ? "Connected." : "Not connected."),
        Note("Starting a song through the Web API needs Spotify Premium. Without it, Alfred opens the song in the Spotify app and presses play."));

    private UIElement SearchPage() => Stack(
        Heading("Web search"),
        Row(Label("Search with"), Choice([(SearchEngine.Google, "Google"), (SearchEngine.DuckDuckGo, "DuckDuckGo"), (SearchEngine.Bing, "Bing")],
            S.SearchEngine, v => Store.Update(s => s.SearchEngine = v))),
        Heading("Weather"),
        Row(Label("Home city"), Field(S.WeatherCity, v => Store.Update(s => s.WeatherCity = v.Length == 0 ? null : v), 220)),
        Note($"Empty uses this PC's time zone: {WeatherTool.City(new Settings())}. Forecasts come from Open-Meteo (free, no account); only the city name is sent."),
        Row(Label("Units"), Choice([((bool?)null, "From Windows' region"), (false, "Celsius"), (true, "Fahrenheit")], S.Fahrenheit,
            v => Store.Update(s => s.Fahrenheit = v))));
}
