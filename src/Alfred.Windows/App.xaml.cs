using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Alfred.Core;
using Alfred.Speech;
using Alfred.Windows.Audio;
using Alfred.Windows.Model;
using Alfred.Windows.Platform;
using Alfred.Windows.UI;
using Microsoft.Win32;

namespace Alfred.Windows;

/// <summary>
/// Alfred for Windows: a tray app with the overlay pill. This is the
/// composition root: it builds every service, wires the hotkey, the wake word
/// and the clock to the coordinator, and reacts to power and display changes.
/// </summary>
public partial class App : Application
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alfred");

    private Mutex? _single;
    private readonly List<IDisposable> _owned = [];
    private SettingsStore _settings = null!;
    private ClockStore _clock = null!;
    private AssistantCoordinator _coordinator = null!;
    private PillWindow _pill = null!;
    private Presenter _presenter = null!;
    private Tray _tray = null!;
    private ModelHost _model = null!;
    private SpeechModels _speechModels = null!;
    private WakeHost _wake = null!;
    private PowerMonitor _power = null!;
    private SettingsContext _settingsContext = null!;
    private SettingsWindow? _settingsWindow;
    private HoldToTalkHotkey? _hotkey;
    private DispatcherTimer? _displayDebounce;
    private string _modelsFolder = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\Alfred.Windows.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Alfred is already running. Look for its icon in the taskbar's notification area.", "Alfred");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI error", args.Exception);
            args.Handled = true; // Keep running: one broken screen shouldn't end the assistant.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task error", args.Exception);
            args.SetObserved();
        };

        try
        {
            Build(background: e.Args.Contains("--background"));
        }
        catch (Exception error)
        {
            Log.Error("Start-up failed", error);
            MessageBox.Show($"Alfred couldn't start:\n\n{error.Message}\n\nThe log is in {Log.Folder}.", "Alfred", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void Build(bool background)
    {
        Directory.CreateDirectory(DataFolder);
        Log.Prune();
        Log.Info($"Alfred starting ({Environment.OSVersion}, .NET {Environment.Version})");
        _modelsFolder = ModelsFolder();

        _settings = new SettingsStore(Path.Combine(DataFolder, "settings.json"));
        var secrets = new CredentialStore();
        _clock = Own(new ClockStore(Path.Combine(DataFolder, "clock.json")));
        var apps = new WindowsAppCatalog(_settings);
        var launcher = new WindowsLauncher();
        IReadOnlyList<string> Roots() => FileRoots(_settings.Current);
        var organizer = new FileOrganizer(Roots, new FileJournal(Path.Combine(DataFolder, "file-journal.json")), new WindowsRecycleBin());
        var files = new FileServices(Roots, new WindowsFileSearch(), organizer, WindowsLauncher.OpenFileAsync);
        var accounts = new Accounts(_settings, secrets, launcher, _clock);
        var spotify = new SpotifyWebApi(_settings, secrets, launcher);
        var services = new AlfredServices
        {
            Settings = _settings, Clock = _clock, Apps = apps, Launcher = launcher, System = new WindowsSystemControl(),
            Media = new WindowsMediaControl(apps, launcher), Spotify = spotify, Clipboard = new WindowsClipboard(),
            Windows = new WindowsWindowManager(), Files = files, Places = new OpenMeteo(), CalendarSource = accounts, Reminders = accounts,
            Notes = new NoteBook(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Alfred Notes"), () => DateTime.Now),
        };
        var registry = ToolRegistry.Standard(services);

        _model = Own(new ModelHost(_modelsFolder, () => _settings.Current));
        var engine = new LocalModelEngine(_model);
        var capture = Own(new AudioCapture());
        var ducker = new AudioDucker(Path.Combine(DataFolder, "ducked.json"), () => _settings.Current.DuckAudio);
        ducker.RecoverAfterCrash();
        _speechModels = Own(new SpeechModels(_modelsFolder, () => _settings.Current));
        var vadPath = Path.Combine(_modelsFolder, "silero_vad.onnx");
        SileroVad? commandVad = null;
        var speech = new SpeechInput(capture, _speechModels, () => commandVad ??= File.Exists(vadPath) ? Own(new SileroVad(vadPath)) : null, ducker);

        _pill = new PillWindow { Clock = () => _clock.Snapshot };
        var speaker = Own(new Speaker());
        var chime = Own(new Chime());
        _presenter = new Presenter(Dispatcher, _pill, speaker, chime, _settings);
        _coordinator = new AssistantCoordinator(speech, engine, registry, _presenter, _clock, files, () => _settings.Current.Routines);
        _coordinator.StateChanged += state => Log.Info($"→ {Describe(state)}");
        _pill.Selected += id => _ = _coordinator.SelectAsync(id);
        _pill.Confirmed += yes => _coordinator.ResolveConfirmation(yes);
        _pill.AlertResolved += snooze => _coordinator.ResolveAlert(snooze);
        _pill.OpenSettings += page => ShowSettings(page);
        _pill.CancelCountdown += id => _clock.Remove([id]);
        // Show it once so its window handle exists for placement; it hides itself when idle.
        _pill.Show();
        _pill.HideNow();

        _power = Own(new PowerMonitor());
        ApplyPower(_power.Profile);
        _power.Changed += profile => Dispatcher.BeginInvoke(() => ApplyPower(profile));
        _wake = Own(new WakeHost(capture, _modelsFolder, speech, _coordinator, _settings, _power));
        _model.Notice += message => Dispatcher.BeginInvoke(() => _tray.Notify("Alfred", message));

        StartHotkey();
        _clock.Start(new ToastNotifier(), _ => _presenter.ClockChanged(), alert => _coordinator.Ring(alert));

        _tray = Own(new Tray(TrayStatusNow, _clock));
        _tray.TypeCommand += () => new CommandBox(text => _coordinator.RunTypedAsync(text)).Show();
        _tray.OpenSettings += () => ShowSettings(null);
        _tray.PauseListening += paused => _ = Pause(paused);
        _tray.Quit += Quit;
        _wake.StatusChanged += UpdateTooltip;
        _coordinator.StateChanged += _ => Dispatcher.BeginInvoke(UpdateTooltip);

        _settingsContext = new SettingsContext(_settings, _clock, accounts, spotify, files, registry, _model, _wake, speaker, _modelsFolder,
            alert => _coordinator.Ring(alert));
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(DisplaysChanged);
        _settings.Changed += _ => Dispatcher.BeginInvoke(UpdateTooltip);

        _wake.Update();
        UpdateTooltip();
        FirstRun(background);
        Log.Info("Alfred ready");
    }

    private T Own<T>(T item) where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    /// <summary>%LOCALAPPDATA%\Alfred\models, or ALFRED_MODELS for a shared copy.</summary>
    private static string ModelsFolder()
    {
        var custom = Environment.GetEnvironmentVariable("ALFRED_MODELS");
        var folder = !string.IsNullOrWhiteSpace(custom) ? custom : Path.Combine(DataFolder, "models");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The allowed folders, wherever Windows keeps them (OneDrive may hold Documents and Desktop).</summary>
    private static List<string> FileRoots(Settings settings)
    {
        var roots = new List<string>();
        foreach (var name in settings.FileRoots)
        {
            var path = name switch
            {
                "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "Downloads" => KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"))
                               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                _ => null,
            };
            if (path is null || !Directory.Exists(path)) continue;
            try { roots.Add(FileScope.Canonical(path)); }
            catch (IOException) { }
        }
        return roots;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out string path);

    private static string? KnownFolder(Guid id) => SHGetKnownFolderPath(id, 0, 0, out var path) == 0 ? path : null;

    private void StartHotkey()
    {
        try
        {
            _hotkey = Own(new HoldToTalkHotkey(Chord.Parse(_settings.Current.Hotkey)));
            _hotkey.Pressed += () => _ = _coordinator.ActivateAsync();
            _hotkey.Released += () => _coordinator.Release();
            _hotkey.Toggled += () => _ = _coordinator.ActivateAsync(new WakeContext(Confirmed: true, Command: null, FromWakeWord: false));
            _hotkey.Escape += () =>
            {
                if (_coordinator.State is not AssistantState.Idle) _ = _coordinator.CancelAsync();
            };
            if (!_hotkey.Registered) Log.Warn($"{_settings.Current.Hotkey} is registered by another app; hold-to-talk still works, but not over admin windows");
        }
        catch (InvalidOperationException error)
        {
            Log.Error("Hotkey failed", error);
        }
    }

    private void ApplyPower(PowerProfile profile)
    {
        _model.Profile = profile;
        _speechModels.Profile = profile;
        if (profile != PowerProfile.PluggedIn) return;
        _ = Task.Run(_model.PreloadAsync);
        _ = Task.Run(() =>
        {
            try
            {
                if (_speechModels.UnavailableReason() is null) _speechModels.Transcriber();
            }
            catch (Exception e)
            {
                Log.Error("Preloading Whisper failed", e);
            }
        });
    }

    /// <summary>Monitors plugged in or out, lid opened or closed: re-place the pill, debounced by 300 ms (spec §5).</summary>
    private void DisplaysChanged()
    {
        _displayDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _displayDebounce.Stop();
        _displayDebounce.Tick -= OnDisplayTick;
        _displayDebounce.Tick += OnDisplayTick;
        _displayDebounce.Start();
    }

    private void OnDisplayTick(object? sender, EventArgs e)
    {
        _displayDebounce?.Stop();
        var lidClosed = Displays.InternalWorkArea() is null;
        var disable = lidClosed && _settings.Current.LidClosed == LidClosedBehaviour.Disable;
        Log.Info($"Displays changed; internal panel {(lidClosed ? "off" : "on")}");
        if (disable != _coordinator.IsSuspended && !_manuallyPaused) _ = _coordinator.SetSuspendedAsync(disable);
        _presenter.Render(_coordinator.State);
    }

    private bool _manuallyPaused;

    private async Task Pause(bool paused)
    {
        _manuallyPaused = paused;
        await _coordinator.SetSuspendedAsync(paused);
        _wake.Update();
        UpdateTooltip();
    }

    private TrayStatus TrayStatusNow()
    {
        var chord = Chord.Parse(_settings.Current.Hotkey);
        var headline = _coordinator.IsSuspended ? "Alfred is paused" : $"Alfred: hold {chord} to talk";
        var missing = ModelFile.Essential.Where(m => !File.Exists(m.PathIn(_modelsFolder))).Select(m => m.Name).ToList();
        var detail = missing.Count > 0 ? $"Missing: {string.Join(", ", missing)} (Settings › Model)" : $"{_model.Status} · wake word: {_wake.Status}";
        return new TrayStatus(headline, detail, _coordinator.IsSuspended);
    }

    private void UpdateTooltip()
    {
        var status = TrayStatusNow();
        var text = $"{status.Headline}\n{status.Detail}";
        _tray?.SetTooltip(text.Length > 120 ? text[..120] : text);
    }

    /// <summary>First run: without the models there's nothing to understand speech with, so go straight to downloading them.</summary>
    private void FirstRun(bool background)
    {
        var missing = ModelFile.Essential.Where(m => !File.Exists(m.PathIn(_modelsFolder))).ToList();
        if (missing.Count > 0)
        {
            ShowSettings("Model");
            _tray.Notify("Welcome to Alfred", "Download the models in Settings › Model, then hold Ctrl+Alt+Space and speak.");
        }
        else if (!background)
        {
            _tray.Notify("Alfred is ready", $"Hold {Chord.Parse(_settings.Current.Hotkey)} and speak, or right-click this icon.");
        }
    }

    private void ShowSettings(string? page)
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settingsContext);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show(page);
    }

    private static string Describe(AssistantState state) => state switch
    {
        AssistantState.Thinking t => $"Thinking \"{t.Transcript}\"",
        AssistantState.Acting a => $"Acting {a.Tool}: {a.Target}",
        AssistantState.Result r => $"Result: {r.Text}",
        AssistantState.Reply r => $"Reply: {r.Text}",
        AssistantState.Error e => $"Error: {e.Message}",
        AssistantState.List l => $"List: {l.Text} ({l.Items.Count})",
        AssistantState.Confirm c => $"Confirm: {c.Text}",
        AssistantState.Alert a => $"Alert: {a.Clock.Title}",
        _ => state.GetType().Name,
    };

    private void Quit()
    {
        Log.Info("Alfred quitting");
        foreach (var item in Enumerable.Reverse(_owned))
        {
            try { item.Dispose(); }
            catch (Exception e) { Log.Warn($"Shutting down {item.GetType().Name}: {e.Message}"); }
        }
        _pill.Close();
        _single?.ReleaseMutex();
        Shutdown();
    }
}
