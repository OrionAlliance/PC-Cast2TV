namespace PCCast2TV.Helpers;

// Which player each Roku has (ours, DisplayHUB or neither): asked for the rows, on a click, and each second the popup is open.
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

    // A clicked Roku is asked which player it has (neither: the window shows how to add one), and a cast asks again.
    async Task CheckRokuAppAsync(Device d)
    {
        var on = await RokuPlayer.PlayerOnAsync(d);
        NotePlayer(d, on);
        if (on == RokuApp.None && Phase == Phase.Online && Picked?.Id == d.Id) NeedsRokuApp?.Invoke(d);
    }

    // While the popup is open, asks the Roku each second until a player shows up, then says so and closes it; closing it first stops this.
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
