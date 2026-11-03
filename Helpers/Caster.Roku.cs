namespace PCCast2TV.Helpers;

// Whether each Roku has our Roku app: asked for the rows, on a click, and each second the popup is open.
public sealed partial class Caster
{
    public event Action<Device> NeedsRokuApp;   // a Roku without our app: the window shows how to add it
    readonly Dictionary<string, RokuApp> _players = new(); // each Roku's answer by id, once asked

    // Whether a Roku has our app, for its row; null until it has been asked.
    public RokuApp? PlayerOf(Device d) => d != null && _players.TryGetValue(d.Id, out var p) ? p : null;

    // Remembers a Roku's answer; the log and the window hear about it only when it changes.
    void NotePlayer(Device d, RokuApp? on)
    {
        if (on is not { } now || PlayerOf(d) == now) return;
        _players[d.Id] = now;
        Log.Write($"roku: {d.LogName} " + (now == RokuApp.Ours ? "has our app" : "doesn't have our app"));
        Raise();
    }

    // A clicked Roku is asked whether it has our app (no: the window shows how to add it), and a cast asks again.
    async Task CheckRokuAppAsync(Device d)
    {
        var on = await RokuPlayer.PlayerOnAsync(d);
        NotePlayer(d, on);
        if (on == RokuApp.None && Phase == Phase.Online && Picked?.Id == d.Id) NeedsRokuApp?.Invoke(d);
    }

    // While the popup is open, asks the Roku each second until our app shows up, then says so and closes it; closing it first stops this.
    public async Task WaitForRokuAppAsync(Device d, CancellationToken stop)
    {
        while (true)
        {
            await Task.Delay(1000, stop);
            var on = await RokuPlayer.PlayerOnAsync(d, logFailure: false); // a Roku that's off would fill the log
            stop.ThrowIfCancellationRequested();
            if (on != RokuApp.Ours) continue;
            NotePlayer(d, on);
            if (Phase == Phase.Online) Note($"PC CAST2TV ADDED · {Ready}");
            return;
        }
    }
}
