using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// Updates: checked once the window is up, offered only when the Caster is idle and no other popup is open.
public partial class MainWindow
{
    AppUpdater.Update _pendingUpdate;

    // The "updated" line goes up the moment the window does, before the cleanup and GitHub's answer.
    async Task CheckForUpdateAsync()
    {
        if (AppUpdater.JustUpdated()) _caster.Note($"UPDATED TO V{AppUpdater.Installed}");
        _pendingUpdate = await AppUpdater.StartUpAsync();
        OfferUpdate();
    }

    // Shown once per run; NOT NOW leaves it for the next start.
    void OfferUpdate()
    {
        if (_pendingUpdate == null || !_caster.Idle || OwnedWindows.Count > 0) return;
        var update = _pendingUpdate;
        _pendingUpdate = null;
        if (!UpdateWindow.Offer(this, update)) return;
        if (AppUpdater.StartNewCopy()) Close(); // closing disconnects as usual
        else NoticeWindow.Tell(this, "UPDATED", "The new version is in place. Close PC Cast2TV and open it again to use it.");
    }
}
