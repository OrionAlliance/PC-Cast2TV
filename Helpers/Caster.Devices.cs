namespace PCCast2TV.Helpers;

// The device list's own parts: hiding (right-click a device), bringing back (right-click Detect), and the bookmark.
public sealed partial class Caster
{
    // ---- Hide (right-click a device) and bring back (right-click Detect) ----

    // Hidden devices: off the list, still saved, so they can come back.
    public List<Device> SetAside => AppSettings.Hidden.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // Every saved device is hidden, so the empty list says how to bring them back (Detect won't).
    public bool AllHidden => Devices.Count == 0 && AppSettings.Hidden.Count > 0;

    public void Hide(Device d)
    {
        if (!Idle) return;
        AppSettings.Hide(d);
        Refresh($"{d.Name.ToUpperInvariant()} HIDDEN");
    }

    // Everything unticked in the set-aside list goes straight back on the list.
    public void BringBack(List<Device> stillAside)
    {
        if (!Idle) return;
        int back = AppSettings.Hidden.Count - stillAside.Count;
        if (back == 0) return;
        AppSettings.SetHidden(stillAside);
        Refresh(back == 1 ? "1 DEVICE BACK ON THE LIST" : $"{back} DEVICES BACK ON THE LIST");
        if (IsOnline) _ = LoadLinksAsync(); // a device back on the list says how it's connected
    }

    void Refresh(string status)
    {
        Devices = Row();
        if (Picked != null && AppSettings.IsHidden(Picked.Id))
        {
            Picked = null;
            PickClicked = false;
            SetNetwork(IsOnline);
        }
        Status = status;
        Raise();
    }

    // The bookmark stands in for a click: it picks when nothing is picked, and lets go only of what it picked.
    public void ToggleDefault(Device d)
    {
        if (Phase is Phase.Starting or Phase.Stopping) return;
        bool clearing = Default?.Id == d.Id;
        AppSettings.SetDefault(clearing ? null : d);
        if (Idle)
        {
            if (clearing && Picked?.Id == d.Id && !PickClicked) Picked = null;
            else if (!clearing && Picked == null) Picked = d;
            SetNetwork(IsOnline);
        }
        Raise();
    }
}
