using Microsoft.Win32;

namespace PCCast2TV.Helpers;

// Which monitor a cast copies: this session's pick, else the saved default when it's connected, else the main screen.
public sealed partial class Caster
{
    public IReadOnlyList<PcScreen> Screens { get; private set; } = Array.Empty<PcScreen>();
    PcScreen _screenPick; // picked this session, not saved

    // The monitor the next cast copies (null only if Windows lists none).
    public PcScreen CastScreen => Find(_screenPick?.Key) ?? DefaultScreen ?? Screens.FirstOrDefault(s => s.Main) ?? Screens.FirstOrDefault();
    public PcScreen DefaultScreen => Find(AppSettings.Screen);
    // Two or more monitors and nothing chosen yet (no pick, no connected default): MONITOR SELECTION opens by itself.
    public bool ScreenChoiceNeeded => Screens.Count > 1 && Find(_screenPick?.Key) == null && DefaultScreen == null;

    PcScreen Find(string key) => key == null ? null : Screens.FirstOrDefault(s => s.Key == key);

    // From the constructor: the list follows monitors being plugged in, unplugged or changed.
    void WatchScreens()
    {
        RefreshScreens();
        SystemEvents.DisplaySettingsChanged += (_, _) => _ui.Post(_ => RefreshScreens(), null);
    }

    void RefreshScreens()
    {
        var now = PcScreens.All();
        bool changed = !now.Select(s => s.Key + s.LogName).SequenceEqual(Screens.Select(s => s.Key + s.LogName));
        Screens = now;
        if (!changed) return;
        Log.Write($"screens: {now.Count}: " + string.Join(", ", now.Select(s => $"{s.LogName} on {s.Card}")));
        if (AppSettings.Screen != null && DefaultScreen == null) Log.Write("screens: the default monitor isn't connected, the main screen stands in");
        Raise();
    }

    public void PickScreen(PcScreen s)
    {
        if (!Idle) return;
        _screenPick = s;
        Log.Write("screens: casting " + s.LogName + " this session");
        Raise();
    }

    // Ticking makes a monitor the default (and the pick); unticking the default clears it.
    public void ToggleDefaultScreen(PcScreen s)
    {
        if (!Idle) return;
        bool clear = DefaultScreen?.Key == s.Key;
        AppSettings.SetScreen(clear ? null : s.Key, s.LogName);
        if (!clear) _screenPick = s;
        Raise();
    }

    // For a cast: the monitor to copy, with the encoder on that monitor's own card; null (the cast failed or the app closed) if it can't.
    async Task<PcScreen> ScreenForCastAsync()
    {
        var screen = CastScreen;
        if (screen == null) { FailCast("NO SCREEN FOUND"); return null; }
        Log.Write("cast: copying " + screen.LogName);
        var encoder = await EncoderPicker.PickAsync(screen.Device);
        if (Phase != Phase.Starting) return null;
        if (encoder == null) { FailCast("NO VIDEO ENCODER · SEE LOG"); return null; }
        Encoder = encoder;
        return screen;
    }
}
