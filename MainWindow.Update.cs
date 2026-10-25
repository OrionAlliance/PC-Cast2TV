using System.ComponentModel;
using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// Updates: once the window is up, tidy up after a past update and ask GitHub for a newer version. An offer waits
// until nothing is casting or starting, so it never interrupts a cast.
public partial class MainWindow
{
    AppUpdater.Update _pendingUpdate;

    async Task CheckForUpdateAsync()
    {
        if (AppUpdater.JustUpdated())
        {
            Log.Write($"update: now running v{AppUpdater.Installed}");
            _caster.Note($"UPDATED TO V{AppUpdater.Installed}");
        }
        await AppUpdater.CleanUpAsync();
        _pendingUpdate = await AppUpdater.CheckAsync();
        OfferUpdate();
    }

    // Shown once per run; NOT NOW leaves it for the next start.
    void OfferUpdate()
    {
        if (_pendingUpdate == null || _caster.Phase is not (Phase.Offline or Phase.Online) || OwnedWindows.Count > 0) return;
        var update = _pendingUpdate;
        _pendingUpdate = null;
        if (!UpdateWindow.Offer(this, update)) return;
        try
        {
            AppUpdater.StartNewCopy();
            Close(); // closing disconnects as usual
        }
        catch (Win32Exception ex)
        {
            Log.Write($"update: couldn't start the new version: {ex.Message}");
            NoticeWindow.Tell(this, "UPDATED", "The new version is in place. Close PC Cast2TV and open it again to use it.");
        }
    }
}
