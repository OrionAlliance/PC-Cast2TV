using System.Net.NetworkInformation;

namespace PCCast2TV.Helpers;

public enum Phase { Offline, Connecting, Online, Detecting, Starting, Casting, Stopping }

// The center button's next step: a first run walks Detect, Select, Cast (Offline only if going online failed).
public enum CastStep { Offline, DetectFirst, SelectDevice, Cast, Casting, Busy }

// The one owner of the app's state: every button lands here, the checks run in order, and the window just draws what it reads.
public sealed partial class Caster
{
    readonly SynchronizationContext _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Caster must be made on the window thread");
    readonly CastServer _server = new();
    ScreenRecorder _rec;
    CastPlayer _castPlayer;
    HashSet<string> _answering = new(); // devices that answered the last check (going online, Detect or a cast start)
    Timer _tick;
    DateTime _castStartUtc;
    int _ticks;
    Task _castWork = Task.CompletedTask; // the cast start or end under way, so closing the app can let it tell the TV

    public event Action Changed;
    public event Action<string, string> Notice; // a popup for the window to show: title, then what to do

    public Phase Phase { get; private set; } = Phase.Offline;
    public List<Device> Devices { get; private set; } = new();
    public Device Picked { get; private set; }
    public bool PickClicked { get; private set; } // picked by a click (shown lit), not just the bookmark standing in
    public Device Default => AppSettings.Default;
    public Device CastingTo { get; private set; }
    public bool Detected { get; private set; }
    public string Status { get; private set; } = "CONNECTING...";
    public EncoderChoice Encoder { get; private set; }
    public RateMeter Meter { get; } = new();
    // The health readout: the log's 10-second readings, null until this cast's first, kept after a cast until the next one plays.
    public Device HealthFrom { get; private set; } // the cast they belong to (the one playing, or the last one); null before any
    public int DroppedFrames { get; private set; } // frames the screen clock missed this cast (the TV repeats the last one)
    public double? CpuLoad { get; private set; }   // this app's share of the processor
    public double? EncoderLoad { get; private set; } // the graphics card's video encoder, every program on it
    public string PcLink { get; private set; }       // how this PC reaches the device: "WIRED" or "WI-FI"
    readonly Dictionary<string, string> _links = new(); // each device's own connection by id; null = asked, it doesn't say

    public bool IsOnline => Phase is not (Phase.Offline or Phase.Connecting);
    public bool Idle => Phase is Phase.Offline or Phase.Online; // nothing connecting, detecting or casting
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
        // The row starts with the devices earlier Detects found; the window has the app go online once it's open.
        Devices = Row();
        Picked = Devices.FirstOrDefault(d => d.Id == Default?.Id);
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.Post(_ => RefreshNetwork(), null);
        _server.RemoteButton = name => _ui.Post(_ => OnRemoteButton(name), null);
    }

    // The row: every device Detect has found (the bookmark is always one of them), minus the hidden ones.
    List<Device> Row() => AppSettings.Devices.Where(d => !AppSettings.IsHidden(d.Id)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsAnswering(Device d) => d != null && _answering.Contains(d.Id);

    // The center button casts to the picked device (the bookmarked one is picked when the app opens).
    public CastStep Step => Phase switch
    {
        Phase.Offline => CastStep.Offline,
        Phase.Online when Devices.Count == 0 => CastStep.DetectFirst,
        Phase.Online when Picked == null => CastStep.SelectDevice,
        Phase.Online => CastStep.Cast,
        Phase.Casting => CastStep.Casting,
        _ => CastStep.Busy,
    };

    // Shown only while connected. It follows the device in use and updates by itself when Windows changes networks.
    void RefreshNetwork()
    {
        var before = PcLink;
        SetNetwork(IsOnline);
        if (PcLink == before) return;
        Log.Write("network: now " + (PcLink ?? "NO NETWORK"));
        Raise();
    }

    // This PC's side of the link to the device in use; nothing while offline.
    void SetNetwork(bool online) => PcLink = online ? HomeNetwork.Link((CastingTo ?? Picked)?.Ip) : null;

    // A device's connection in its own words (WI-FI or WIRED); LinkAsked is false until it's asked, and one that doesn't say is null.
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
            if (d.IsRoku && link != null) NotePlayer(d, await RokuPlayer.PlayerOnAsync(d)); // asked quietly, no popup
        }));
    }

    // ---- Online when the app opens, offline when it closes ----

    public async Task ConnectAsync()
    {
        if (Phase != Phase.Offline) return;
        Set(Phase.Connecting, "CONNECTING...");
        Encoder = await EncoderPicker.PickAsync(); // the card driving the main screen when the app opens
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
        Log.Write("connect: online on " + (PcLink ?? "NO NETWORK"));
        Set(Phase.Online, !firewallOk ? "FIREWALL BLOCKS TVS · CAST TO ASK WINDOWS AGAIN"
                        : Devices.Count > 0 ? Ready : "CONNECTED · CLICK DETECT DEVICE(S)");
        _ = LoadLinksAsync();
        await CheckDevicesAsync();
    }

    // Once when the app goes online: asks each device on the row if it's there, for the log (the rows' CHECKING... is LoadLinksAsync's).
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
        Set(Phase.Offline, "OFFLINE");
    }

    // Closing takes the app offline in 8 seconds at most; a cast starting or ending gets up to 6 of them to tell the TV to stop.
    public Task ShutdownAsync() => Task.WhenAny(CloseDownAsync(), Task.Delay(TimeSpan.FromSeconds(8)));

    async Task CloseDownAsync()
    {
        if (Phase is Phase.Starting or Phase.Stopping)
        {
            if (Phase == Phase.Starting) Phase = Phase.Offline;
            await Task.WhenAny(_castWork, Task.Delay(TimeSpan.FromSeconds(6))); // a TV slower than that is left as it is
        }
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
        var found = await DeviceFinder.DetectAsync(AppSettings.Devices);
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

    // A click picks a device (lit) and a click on your pick lets it go; the bookmark's own pick isn't lit, so a first click lights it.
    public void Pick(Device d)
    {
        if (!Idle) return;
        bool letGo = Picked?.Id == d.Id && PickClicked;
        Picked = letGo ? null : d;
        PickClicked = !letGo;
        if (IsOnline) Status = Ready; // offline, the line keeps saying why
        SetNetwork(IsOnline);
        Raise();
        if (!letGo && d.IsRoku && IsOnline) _ = CheckRokuAppAsync(d);
    }

    // The status before a cast; the center button shows what to press.
    const string Ready = "READY";

    // A line from outside the casting (like "UPDATED TO V0.3.83"), shown only while nothing is going on.
    public void Note(string status)
    {
        if (!Idle) return;
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
