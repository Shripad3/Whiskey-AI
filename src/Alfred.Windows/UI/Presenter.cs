using System.Windows.Threading;
using Alfred.Core;
using Alfred.Windows.Audio;
using Alfred.Windows.Platform;

namespace Alfred.Windows.UI;

/// <summary>
/// The coordinator's view: posts every state to the UI thread, where the pill
/// shows it, the voice speaks it and the chime rings. Decides where the pill
/// goes (the laptop's panel) and when it can't be seen (a full-screen game,
/// the lid closed), in which case results are spoken instead (spec §5).
/// </summary>
internal sealed class Presenter(Dispatcher dispatcher, PillWindow pill, Speaker speaker, Chime chime, SettingsStore settings) : IPresenter
{
    private bool _visible = true;

    public void Render(AssistantState state) => dispatcher.BeginInvoke(() =>
    {
        try
        {
            _visible = Place();
            if (_visible) pill.Render(state);
            else if (state is AssistantState.Idle) pill.HideNow();
            if (state is AssistantState.Alert) chime.Start();
            else chime.Stop();
            speaker.Speak(state, settings.Current, _visible);
        }
        catch (Exception e)
        {
            Log.Error("Rendering failed", e);
        }
    });

    public void AudioLevel(float level) => dispatcher.BeginInvoke(DispatcherPriority.Background, () => pill.AudioLevel(level));

    /// <summary>A clock change while idle: the countdown pill appears or goes.</summary>
    public void ClockChanged() => dispatcher.BeginInvoke(() =>
    {
        if (pill.Current is AssistantState.Idle && Place()) pill.Render(AssistantState.IdleState);
    });

    /// <summary>Positions the pill; false when it shouldn't be shown at all.</summary>
    private bool Place()
    {
        if (Displays.FullScreenAppInFront()) return false;
        if (Displays.InternalWorkArea() is { } panel)
        {
            pill.Place(panel);
            return true;
        }
        // Lid closed with an external monitor (spec §5).
        switch (settings.Current.LidClosed)
        {
            case LidClosedBehaviour.Floating:
                pill.Place(Displays.PrimaryWorkArea());
                return true;
            default:
                return false;
        }
    }
}
