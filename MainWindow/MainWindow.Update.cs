using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// Updates: checked once the window is up, offered only when the Caster is idle and no other popup is open.
public partial class MainWindow
{
    AppUpdater.Update _pendingUpdate;
    bool _updateDownloaded; // the new exe waits next to this one until closing is done

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
        _updateDownloaded = true;
        Close(); // closing disconnects as usual, then FinishUpdate
    }

    // The very end of closing after UPDATE: this copy is done with its own exe, so the new one takes its place and starts.
    void FinishUpdate()
    {
        string why = AppUpdater.SwapIn();
        if (why != null) NoticeWindow.Tell(this, "COULDN'T UPDATE", why + "\nNothing changed. Open PC Cast2TV again to keep using this version.");
        else if (!AppUpdater.StartNewCopy()) NoticeWindow.Tell(this, "UPDATED", "The new version is in place. Open PC Cast2TV again to use it.");
    }
}
