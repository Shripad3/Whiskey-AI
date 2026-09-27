using Alfred.Core;
using Alfred.Speech;
using Alfred.Windows.Platform;

namespace Alfred.Windows.Audio;

/// <summary>
/// Runs the wake word when the user has it on and the power profile allows
/// (spec §9): microphone open, Silero VAD on every frame, Whisper tiny.en on
/// the CPU for each speech segment. Listens only while the pill is idle,
/// showing a confirmation, or ringing, where "Whiskey, …" is an answer.
/// </summary>
internal sealed class WakeHost : IDisposable
{
    private readonly AudioCapture _capture;
    private readonly string _models;
    private readonly SpeechInput _speech;
    private readonly AssistantCoordinator _coordinator;
    private readonly SettingsStore _settings;
    private readonly PowerMonitor _power;
    private readonly object _lock = new();
    private WakeListener? _listener;
    private WhisperTranscriber? _tiny;

    public string Status { get; private set; } = "Off";

    public event Action? StatusChanged;

    public WakeHost(AudioCapture capture, string models, SpeechInput speech, AssistantCoordinator coordinator, SettingsStore settings, PowerMonitor power)
    {
        _capture = capture;
        _models = models;
        _speech = speech;
        _coordinator = coordinator;
        _settings = settings;
        _power = power;
        coordinator.StateChanged += _ => UpdateListening();
        settings.Changed += _ => Update();
        power.Changed += _ => Update();
    }

    /// <summary>Starts or stops the wake word to match settings, power and the models present.</summary>
    public void Update()
    {
        lock (_lock)
        {
            var settings = _settings.Current;
            var vad = Path.Combine(_models, "silero_vad.onnx");
            var tiny = Path.Combine(_models, settings.WakeWhisperFile);
            string status;
            if (!settings.WakeWordEnabled) status = "Off";
            else if (_coordinator.IsSuspended) status = "Paused";
            else if (!_power.Profile.AllowsWakeWord(settings.WakeWordOnBattery)) status = $"Paused: {_power.Profile.PauseReason()}";
            else if (!File.Exists(vad) || !File.Exists(tiny)) status = "Needs the wake word models (Settings › Model)";
            else status = $"Listening for “{WakePhrase.DisplayName}”";

            var shouldRun = status == $"Listening for “{WakePhrase.DisplayName}”";
            if (shouldRun && _listener is null) Start(vad, tiny, ref status);
            else if (!shouldRun && _listener is not null) StopListening();
            if (Status != status)
            {
                Status = status;
                Log.Info($"Wake word: {status}");
                StatusChanged?.Invoke();
            }
        }
        UpdateListening();
    }

    private void Start(string vadPath, string tinyPath, ref string status)
    {
        try
        {
            _tiny = new WhisperTranscriber(tinyPath, useGpu: false);
            _listener = new WakeListener(new SileroVad(vadPath), _tiny.TranscribeAsync);
            _listener.Detected += OnDetected;
            _listener.Heard += text => Log.Info($"Wake word heard \"{text}\"");
            _capture.Samples += Push;
            _capture.Start(this);
        }
        catch (Exception e)
        {
            Log.Error("Wake word failed to start", e);
            StopListening();
            status = $"Couldn't start: {e.Message}";
        }
    }

    private void StopListening()
    {
        _capture.Samples -= Push;
        _capture.Stop(this);
        _listener?.Dispose();
        _listener = null;
        _tiny?.Dispose();
        _tiny = null;
    }

    private void Push(float[] samples) => _listener?.Push(samples);

    private void UpdateListening()
    {
        var listener = _listener;
        if (listener is null) return;
        listener.Enabled = _coordinator.State is AssistantState.Idle or AssistantState.Confirm or AssistantState.Alert && !_coordinator.IsSuspended;
    }

    private void OnDetected(WakeDetection detection)
    {
        Log.Info($"Wake word: \"{detection.Transcript}\"");
        _speech.PrimeWithWake(detection.Audio, detection.TrailingSilence);
        var command = WakePhrase.Command(detection.Transcript);
        _ = _coordinator.ActivateAsync(new WakeContext(Confirmed: true, command == detection.Transcript ? null : command));
    }

    public void Dispose()
    {
        lock (_lock) StopListening();
    }
}
