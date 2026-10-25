using System.Net.NetworkInformation;

namespace PCCast2TV.Helpers;

public enum Phase { Offline, Connecting, Online, Detecting, Starting, Casting, Stopping }

// The center button's next step: a first run walks Connect, Detect, Select, Cast.
public enum CastStep { ConnectFirst, DetectFirst, SelectDevice, Cast, Casting, Busy }

// The one owner of the app's state. Every button lands here, the checks run in order, and the window
// only listens to Changed and draws what it reads.
public sealed partial class Caster
{
    readonly SynchronizationContext _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Caster must be made on the window thread");
    readonly CastServer _server = new();
    ScreenRecorder _rec;
    CastPlayer _castPlayer;
    HashSet<string> _answering = new(); // devices that answered the last check (Connect, Detect or a cast start)
    Timer _tick;
    DateTime _castStartUtc;
    int _ticks;

    public event Action Changed;
    public event Action<string, string> Notice; // a popup for the window to show: title, then what to do

    public Phase Phase { get; private set; } = Phase.Offline;
    public List<Device> Devices { get; private set; } = new();
    public Device Picked { get; private set; }
    public bool PickClicked { get; private set; } // picked by a click (shown lit), not just the bookmark standing in
    public Device Default => AppSettings.Default;
    public Device CastingTo { get; private set; }
    public bool Detected { get; private set; }
    public string Status { get; private set; } = "OFFLINE · PRESS CONNECT";
    public EncoderChoice Encoder { get; private set; }
    public RateMeter Meter { get; } = new();
    // The health readout: the same 10-second readings the log takes, null until this cast's first one. After a cast
    // ends they stay as they were until the next cast plays.
    public Device HealthFrom { get; private set; } // the cast they belong to (the one playing, or the last one); null before any
    public int DroppedFrames { get; private set; } // frames the screen clock missed this cast (the TV repeats the last one)
    public double? CpuLoad { get; private set; }   // this app's share of the processor
    public double? EncoderLoad { get; private set; } // the graphics card's video encoder, every program on it
    public double? BehindLive { get; private set; }  // seconds; only our Roku app reports it
    public string NetworkLabel { get; private set; } // this PC's side for the log: "WIRED · NETWORK 4"
    public string PcLink { get; private set; }       // how this PC reaches the device: "WIRED" or "WI-FI"
    readonly Dictionary<string, string> _links = new(); // each device's own connection by id; null = asked, it doesn't say

    public bool IsOnline => Phase is not (Phase.Offline or Phase.Connecting);
    int SentHeight
    {
        get
        {
            var (w, h) = ScreenInfo.Size;
            return ScreenInfo.SentSize(w, h).Height;
        }
    }
    public string VideoLabel => Encoder == null ? $"{SentHeight}P" : $"{SentHeight}P · {Encoder.Fps}";

    public Caster()
    {
        // Opening the app touches nothing on the network: it shows the devices earlier Detects found.
        Devices = Row();
        Picked = Devices.FirstOrDefault(d => d.Id == Default?.Id);
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.Post(_ => RefreshNetwork(), null);
        _server.RemoteButton = name => _ui.Post(_ => OnRemoteButton(name), null);
        _server.RokuBehind = seconds => _ui.Post(_ => OnRokuBehind(seconds), null);
    }

    // The row: every device Detect has found (the bookmark is always one of them), minus the hidden ones.
    List<Device> Row() => AppSettings.Devices.Where(d => !AppSettings.IsHidden(d.Id)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsAnswering(Device d) => d != null && _answering.Contains(d.Id);

    // The center button casts to the picked device (the bookmarked one is picked when the app opens).
    public CastStep Step => Phase switch
    {
        Phase.Offline => CastStep.ConnectFirst,
        Phase.Online when Devices.Count == 0 => CastStep.DetectFirst,
        Phase.Online when Picked == null => CastStep.SelectDevice,
        Phase.Online => CastStep.Cast,
        Phase.Casting => CastStep.Casting,
        _ => CastStep.Busy,
    };

    // Shown only while connected. It follows the device in use and updates by itself when Windows changes networks.
    void RefreshNetwork()
    {
        var before = NetworkLabel;
        SetNetwork(IsOnline);
        if (NetworkLabel == before) return;
        Log.Write("network: now " + NetworkLabel);
        Raise();
    }

    // This PC's side of the link to the device in use; nothing while offline.
    void SetNetwork(bool online) =>
        (PcLink, NetworkLabel) = online ? NetworkName.Describe((CastingTo ?? Picked)?.Ip) : (null, null);

    // How a device on the list is connected, in its own words ("WI-FI" or "WIRED"). Asked returns false until it has
    // been asked (on Connect, Detect, or when it comes back); a device asked that doesn't say has a null link.
    public string LinkOf(Device d) => d != null && _links.TryGetValue(d.Id, out var l) ? l : null;
    public bool LinkAsked(Device d) => d != null && _links.ContainsKey(d.Id);

    async Task LoadLinksAsync()
    {
        await Task.WhenAll(Devices.Select(async d =>
        {
            var link = await TvInfo.LinkAsync(d);
            if (!IsOnline) return;
            _links[d.Id] = link;
            Raise();
        }));
    }

    // ---- Connect / Disconnect ----

    public async Task ToggleConnectAsync()
    {
        if (Phase == Phase.Offline) await ConnectAsync();
        else if (Phase is Phase.Online or Phase.Casting) await DisconnectAsync();
    }

    async Task ConnectAsync()
    {
        Set(Phase.Connecting, "CONNECTING...");
        Encoder = await EncoderPicker.PickAsync(); // each Connect, so a main screen moved to another card is followed
        if (Phase != Phase.Connecting) return; // the app closed meanwhile
        if (Encoder == null)
        {
            Set(Phase.Offline, "NO VIDEO ENCODER ON THIS PC · SEE LOG");
            return;
        }

        // TVs can only fetch the video if Windows lets them in; ask once per PC for the app's own rule.
        bool firewallOk = await Task.Run(FirewallRule.Exists);
        if (!firewallOk)
        {
            Set(Phase.Connecting, "ASKING WINDOWS TO LET TVS IN...");
            firewallOk = await Task.Run(FirewallRule.Add);
            if (Phase != Phase.Connecting) return;
        }
        Log.Write("connect: firewall rule " + (firewallOk ? "in place" : "missing"));

        try { _server.Start(); }
        catch (InvalidOperationException ex)
        {
            Log.Write("connect: " + ex.Message);
            Set(Phase.Offline, "NO FREE NETWORK PORT");
            return;
        }
        _tick = new Timer(_ => _ui.Post(_ => Tick(), null), null, 1000, 1000);
        SetNetwork(true);
        Log.Write("connect: online on " + NetworkLabel);
        Set(Phase.Online, !firewallOk ? "FIREWALL BLOCKS TVS · CASTS WILL FAIL"
                        : Devices.Count > 0 ? Ready : "CONNECTED · PRESS DETECT");
        _ = LoadLinksAsync();
        await CheckDevicesAsync();
    }

    // Once per Connect: asks each device on the row if it's there. Each button lights up as its device answers;
    // nothing is asked again until the next Connect, Detect or cast.
    async Task CheckDevicesAsync()
    {
        _answering.Clear();
        var row = Devices;
        await Task.WhenAll(row.Select(async d =>
        {
            bool ok = await AnsweringAsync(d);
            if (!IsOnline) return;
            if (ok) _answering.Add(d.Id);
            Raise();
        }));
        if (IsOnline) Log.Write($"connect: {row.Count(IsAnswering)} of {row.Count} saved devices answering");
    }

    static Task<bool> AnsweringAsync(Device d) => d.Kind switch
    {
        DeviceKind.Dlna => DlnaPlayer.AnsweringAsync(d),
        DeviceKind.Cast => CastPlayer.AnsweringAsync(d),
        _ => RokuPlayer.AnsweringAsync(d),
    };

    async Task DisconnectAsync()
    {
        if (Phase == Phase.Casting) await StopCastAsync(null);
        _tick?.Dispose();
        _tick = null;
        _server.Stop();
        SetNetwork(false);
        _answering.Clear();
        Detected = false; // offline the row is just what's saved
        Log.Write("connect: offline");
        Set(Phase.Offline, "OFFLINE · PRESS CONNECT");
    }

    // Closing the app is the same as Disconnect.
    public async Task ShutdownAsync()
    {
        if (Phase is Phase.Casting or Phase.Online) await DisconnectAsync();
        else
        {
            // Mid-check: drop everything; the steps still waiting see the phase change and stop.
            Phase = Phase.Offline;
            _tick?.Dispose();
            CleanupCast();
            _server.Stop();
        }
    }

    // ---- Detect / pick / bookmark ----

    public async Task DetectAsync()
    {
        if (Phase != Phase.Online) return;
        Set(Phase.Detecting, "DETECTING DEVICES...");
        var found = await DeviceFinder.DetectAsync();
        if (Phase != Phase.Detecting) return;
        var moved = found.FirstOrDefault(d => d.Id == Default?.Id && d != Default);
        if (moved != null) AppSettings.SetDefault(moved); // same device, new address or name
        AppSettings.Remember(found);
        Devices = Row();
        Detected = true;
        _answering = found.Select(d => d.Id).ToHashSet(); // answering the search counts as answering
        int shown = Devices.Count(IsAnswering);
        if (shown < found.Count) Log.Write($"detect: {found.Count - shown} hidden from the row");
        // Keep the pick (with its new address if it moved), otherwise fall back to the bookmarked one.
        var kept = Devices.FirstOrDefault(d => d.Id == Picked?.Id);
        if (kept == null) PickClicked = false;
        Picked = kept ?? Devices.FirstOrDefault(d => d.Id == Default?.Id);
        SetNetwork(true);
        Set(Phase.Online, shown == 0 ? (found.Count == 0 ? (NetUtil.AnyVpnUp ? "NOTHING FOUND · VPN ON? TURN ON 'ALLOW LAN'" : "NOTHING FOUND · IS THE TV ON?") : "ONLY HIDDEN DEVICES FOUND")
                        : $"{shown} FOUND · {Ready}");
        _ = LoadLinksAsync();
    }

    // A click on a device picks it (lit); a click on the one you picked lets it go. The bookmarked device the app picked
    // on its own isn't lit, so a first click on it lights it rather than letting it go.
    public void Pick(Device d)
    {
        if (Phase is not (Phase.Offline or Phase.Online)) return;
        bool letGo = Picked?.Id == d.Id && PickClicked;
        Picked = letGo ? null : d;
        PickClicked = !letGo;
        Status = IsOnline ? Ready : "OFFLINE · PRESS CONNECT";
        SetNetwork(IsOnline);
        Raise();
    }

    // ---- Hide / Forget (right-click a device) and bring back (right-click Detect) ----

    public bool CanManageDevices => Phase is Phase.Offline or Phase.Online;

    // Hidden and forgotten devices: off the list, still saved, so they can come back.
    public List<Device> SetAside => AppSettings.Hidden.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void Hide(Device d)
    {
        if (!CanManageDevices) return;
        AppSettings.SetHidden(AppSettings.Hidden.Append(d));
        Refresh($"{d.Name.ToUpperInvariant()} HIDDEN");
    }

    public void Forget(Device d)
    {
        if (!CanManageDevices) return;
        AppSettings.Forget(new[] { d }); // off the list and its bookmark wiped
        Refresh($"{d.Name.ToUpperInvariant()} FORGOTTEN");
    }

    // Everything unticked in the set-aside list goes straight back on the list.
    public void BringBack(List<Device> stillAside)
    {
        if (!CanManageDevices) return;
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

    // The bookmark stands in for a click: clearing it lets go of a device only it picked, and setting it picks the
    // device when nothing is picked. A device you clicked stays picked either way.
    public void ToggleDefault(Device d)
    {
        if (Phase is Phase.Starting or Phase.Stopping) return;
        bool clearing = Default?.Id == d.Id;
        AppSettings.SetDefault(clearing ? null : d);
        if (Phase is Phase.Offline or Phase.Online)
        {
            if (clearing && Picked?.Id == d.Id && !PickClicked) Picked = null;
            else if (!clearing && Picked == null) Picked = d;
            SetNetwork(IsOnline);
        }
        Raise();
    }

    // The status before a cast; the center button shows what to press.
    const string Ready = "READY";

    // A line from outside the casting (like "UPDATED TO V0.3.83"), shown only while nothing is going on.
    public void Note(string status)
    {
        if (Phase is not (Phase.Offline or Phase.Online)) return;
        Status = status;
        Raise();
    }

    void Set(Phase phase, string status)
    {
        Phase = phase;
        Status = status;
        Raise();
    }

    void Raise() => Changed?.Invoke();
}
