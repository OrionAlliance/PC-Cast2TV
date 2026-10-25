namespace PCCast2TV.Helpers;

// Which player each Roku has (our app, DisplayHUB or neither): asked quietly for the rows, on a click (a Roku with
// neither gets the popup), and once a second while the popup is open.
public sealed partial class Caster
{
    public event Action<Device> NeedsRokuApp;   // a Roku with neither player: the window shows how to add one
    readonly Dictionary<string, RokuApp> _players = new(); // each Roku's player by id, once asked

    // What a Roku plays through (our app, DisplayHUB or neither), for its row; null until it has been asked.
    public RokuApp? PlayerOf(Device d) => d != null && _players.TryGetValue(d.Id, out var p) ? p : null;

    // Remembers a Roku's player; the log and the window hear about it only when it changes.
    void NotePlayer(Device d, RokuApp? on)
    {
        if (on is not { } now || PlayerOf(d) == now) return;
        _players[d.Id] = now;
        Log.Write($"roku: {d.LogName} has " + now switch { RokuApp.Ours => "our app", RokuApp.DisplayHub => "DisplayHUB", _ => "neither player" });
        Raise();
    }

    // A Roku you click is asked right away which player it has; with neither, the window shows how to add one. A cast
    // asks again, so a player removed later is still caught.
    async Task CheckRokuAppAsync(Device d)
    {
        var on = await RokuPlayer.PlayerOnAsync(d);
        NotePlayer(d, on);
        if (on == RokuApp.None && Phase == Phase.Online && Picked?.Id == d.Id) NeedsRokuApp?.Invoke(d);
    }

    // While the popup is open: asks the Roku once a second until our app or DisplayHUB shows up, then says so on the
    // status line and returns, which closes the popup. Closing the popup first cancels it.
    public async Task WaitForRokuAppAsync(Device d, CancellationToken stop)
    {
        while (true)
        {
            await Task.Delay(1000, stop);
            var on = await RokuPlayer.PlayerOnAsync(d, logFailure: false); // a Roku that's off would fill the log
            stop.ThrowIfCancellationRequested();
            if (on is not (RokuApp.Ours or RokuApp.DisplayHub)) continue;
            NotePlayer(d, on);
            if (Phase == Phase.Online) Note($"{(on == RokuApp.Ours ? "PC2TV" : "DISPLAYHUB")} ADDED · {Ready}");
            return;
        }
    }
}
