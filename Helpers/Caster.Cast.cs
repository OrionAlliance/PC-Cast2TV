namespace PCCast2TV.Helpers;

// Casting: the checks that start a cast, ending it, the Roku remote's buttons and the once-a-second readout.
public sealed partial class Caster
{
    // ---- Cast ----

    // A VPN can stay on: the cast only crosses the house, so the VPN just has to let the house network through.
    const string AllowLan = "Open the VPN's settings and turn on \"Allow LAN connections\" (some VPNs call it \"Local network sharing\"), then try again.";

    // The center button: casts to the picked device, or ends the cast.
    public async Task ToggleCastAsync()
    {
        if (Phase == Phase.Casting)
        {
            await StopCastAsync(null);
            return;
        }
        if (Phase != Phase.Online || Picked is not { } d) return;
        await StartCastAsync(d);
    }

    // The checks run in order; the first one that fails says why on the status line and stops.
    async Task StartCastAsync(Device d)
    {
        var name = d.Name.ToUpperInvariant();
        Log.Write("cast: start to " + d.LogName);

        Set(Phase.Starting, $"CHECKING {name}...");
        bool answering = await AnsweringAsync(d);
        if (Phase != Phase.Starting) return;
        if (answering) _answering.Add(d.Id); else _answering.Remove(d.Id);
        if (!answering)
        {
            FailCast("NOT ANSWERING", d);
            // A Roku box answers even with the TV off, so for one of those the box itself is the thing to check.
            var check = d.Kind == DeviceKind.Roku ? "Make sure the Roku is plugged in and turned on." : "Make sure the TV is turned on.";
            Notice?.Invoke($"{name} ISN'T ANSWERING", check + "\n\n" +
                (NetUtil.AnyVpnUp ? "If it's on, your VPN may be blocking it. " + AllowLan : "If it's on, press Detect to find it again."));
            return;
        }
        _ = TvInfo.LogAsync(d); // maker, model and software for the log, asked alongside the start

        // A Roku plays it through our Roku app, or the free DisplayHUB channel when ours isn't on it. With neither, the
        // window shows how to add one.
        var rokuApp = RokuApp.None;
        if (d.IsRoku)
        {
            var on = await RokuPlayer.PlayerOnAsync(d);
            NotePlayer(d, on);
            if (Phase != Phase.Starting) return;
            if (on == null) { FailCast("STOPPED ANSWERING", d); return; }
            if (on == RokuApp.None)
            {
                FailCast("NEEDS DISPLAYHUB", d);
                NeedsRokuApp?.Invoke(d);
                return;
            }
            rokuApp = on.Value;
        }

        var localIp = NetUtil.LocalIpToward(d.Ip);
        if (localIp == null) { FailCast("NO NETWORK ROUTE TO THE TV"); return; }
        // A cast never goes through a VPN: the TV has to be on the home network the PC reaches it by.
        if (NetUtil.IsOnVpn(localIp) || !NetUtil.SameNetwork(localIp, d.Ip))
        {
            Log.Write($"route: the way to {d.Ip} leaves the home network (PC side {localIp})");
            if (NetUtil.IsOnVpn(localIp) || NetUtil.AnyVpnUp)
            {
                FailCast("VPN BLOCKS THE TV");
                Notice?.Invoke("YOUR VPN IS BLOCKING THE TV", AllowLan);
            }
            else
            {
                // No VPN: the TV sits on a different part of the home network (a mesh's second network, a guest network).
                FailCast("THE TV IS ON ANOTHER NETWORK");
                Notice?.Invoke("THE TV IS ON ANOTHER NETWORK", "This PC and the TV aren't on the same home network. Connect them both to " +
                    "the same one (not a guest network or a separate one for smart devices), then cast again.");
            }
            return;
        }
        if (!await Task.Run(FirewallRule.Exists)) { FailCast("FIREWALL BLOCKS TVS · PRESS CONNECT AGAIN"); return; }
        if (Phase != Phase.Starting) return;

        // A 75 or 144 Hz screen copied 60 times a second can't land evenly on a 60 Hz TV, so it runs at 60 while casting.
        int hz = ScreenInfo.Hz;
        if (ScreenInfo.LinesUpWithTv(hz)) Log.Write($"screen: {hz} Hz, lines up with the TV");
        else
        {
            Set(Phase.Starting, "SETTING THE SCREEN TO 60 HZ...");
            Log.Write("screen: " + await Task.Run(ScreenInfo.SwitchTo60));
            if (Phase != Phase.Starting) { ScreenInfo.PutRateBack(); return; }
        }

        Set(Phase.Starting, "STARTING SCREEN CAPTURE...");
        KeepAwake.On();
        // Set up off the window's thread so the window keeps drawing; it becomes the cast's recorder once it's up, so a
        // close in the meantime never tears it down halfway through.
        var rec = new ScreenRecorder();
        _server.BeginCast(d.Ip, rec);
        rec.Died += () => _ui.Post(_ => OnRecorderDied(), null);
        if (!d.TakesHls) rec.LiveChunk += _server.PushLive;
        try { await Task.Run(() => rec.Start(Encoder, d.TakesHls ? OutputKind.Hls : OutputKind.LiveTs)); }
        catch (InvalidOperationException ex)
        {
            rec.Dispose();
            Log.Write("cast: capture would not start: " + ex.Message);
            if (Phase == Phase.Starting) FailCast("SCREEN CAPTURE DID NOT START");
            return;
        }
        if (Phase != Phase.Starting) { rec.Dispose(); return; } // the app closed meanwhile; its cleanup ran without this one
        _rec = rec;
        Encoder = _rec.Running; // the window shows what really encodes
        bool videoReady = await _rec.WaitForVideoAsync(d.TakesHls, TimeSpan.FromSeconds(10));
        if (Phase != Phase.Starting) return;
        if (!videoReady) { FailCast("SCREEN CAPTURE DID NOT START · SEE LOG"); return; }
        Meter.Reset(_rec.EncodedBytes, _server.SentBytes);
        _rec.TakeGrabCounts(); // the timing readout starts here
        _rec.TakeCpuPercent();
        _ = Task.Run(GpuLoad.Sample); // its first reading only sets the starting point

        Set(Phase.Starting, $"STARTING {name}...");
        var hlsUrl = $"http://{localIp}:{_server.Port}/live/{ScreenRecorder.PlaylistName}?cast={DateTime.UtcNow.Ticks}";
        // Each kind of TV opens its own player; only a Chromecast's stays with the app for the cast.
        string error;
        CastPlayer player = null;
        if (d.IsRoku) error = await RokuPlayer.PlayAsync(d, rokuApp, hlsUrl, () => _server.PiecesServed);
        else if (d.Kind == DeviceKind.Cast) (player, error) = await CastPlayer.PlayAsync(d, hlsUrl, () => _server.PiecesServed);
        else error = await DlnaPlayer.PlayAsync(d, $"http://{localIp}:{_server.Port}/stream.ts");
        if (Phase != Phase.Starting) { if (player != null) await player.StopAsync(); return; } // closed mid-start: don't leave the TV's player open
        if (error != null) { PlayerFailed(error, d); return; }
        _castPlayer = player;
        _rokuApp = rokuApp;

        // The last cast's health numbers stay up until this one is really playing, so a start that fails keeps them.
        HealthFrom = d;
        DroppedFrames = 0;
        CpuLoad = EncoderLoad = null;
        CastingTo = d;
        _castStartUtc = DateTime.UtcNow;
        Log.Write("cast: playing" + (_rec.HasSound ? "" : ", no sound device"));
        Set(Phase.Casting, $"CASTING TO {name}" + (_rec.HasSound ? "" : " · NO SOUND"));
    }

    // An ended cast says so for 5 seconds, then the line goes back to ready; an error stays up.
    async Task StopCastAsync(string message, bool error = false, Device about = null)
    {
        var d = CastingTo;
        Set(Phase.Stopping, "ENDING CAST...");
        if (d.IsRoku) await RokuPlayer.StopAsync(d, _rokuApp);
        else if (d.Kind == DeviceKind.Cast) await _castPlayer.StopAsync();
        else await DlnaPlayer.StopAsync(d);
        CleanupCast();
        Log.Write("cast: ended" + (message == null ? "" : " (" + ForLog(about, message) + ")"));
        Set(Phase.Online, message == null ? "CAST ENDED" : Shown(about, message));
        if (!error) _ = BackToReadyAsync();
    }

    // A message about a device: the status line puts its name in front, the log its kind and address (logs get posted
    // in public Issues).
    static string Shown(Device about, string message) => about == null ? message : $"{about.Name.ToUpperInvariant()} {message}";
    static string ForLog(Device about, string message) => about == null ? message : $"{about.LogName} {message}";

    // Left alone if anything else is on the line by then, or another cast has started and ended since.
    async Task BackToReadyAsync()
    {
        var shown = Status;
        var cast = _castStartUtc;
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (Phase != Phase.Online || Status != shown || _castStartUtc != cast) return;
        Status = Ready;
        Raise();
    }

    // The TV's player didn't start. If it never got a byte and Windows calls the network Public, the firewall is why.
    void PlayerFailed(string message, Device d)
    {
        bool nothingReached = _server.SentBytes == 0;
        FailCast(message, d);
        if (!nothingReached || !HomeNetwork.IsPublic(d.Ip)) return;
        Log.Write("cast: nothing reached the TV and Windows calls this network Public");
        Notice?.Invoke("WINDOWS CALLS THIS NETWORK PUBLIC", "Windows keeps TVs out on a network it calls Public. Open Windows Settings > " +
            "Network & internet, open your network's properties and set \"Network profile type\" to Private, then cast again.");
    }

    void FailCast(string message, Device about = null)
    {
        Log.Write("cast: failed: " + ForLog(about, message));
        CleanupCast();
        Set(Phase.Online, Shown(about, message));
    }

    void CleanupCast()
    {
        _server.EndCast();
        _rec?.Dispose();
        _rec = null;
        KeepAwake.Off();
        ScreenInfo.PutRateBack();
        _castPlayer?.Dispose();
        _castPlayer = null;
        CastingTo = null;
    }

    // A button on the Roku remote, sent by our Roku app: Back ends the cast, the rest become key presses on the PC
    // (the Roku app shows the press itself).
    void OnRemoteButton(string name)
    {
        if (Phase != Phase.Casting) return;
        if (name == "back")
        {
            Log.Write("remote: back, ending the cast");
            _ = StopCastAsync(null);
            return;
        }
        if (!RemoteKeys.Knows(name))
        {
            Log.Write("remote: unknown button");
            return;
        }
        Log.Write("remote: " + name);
        RemoteKeys.Press(name);
    }

    // The encoder's load, read off the window's thread; dropped if that cast has ended since.
    void OnEncoderLoad(double? load, DateTime cast)
    {
        if (Phase != Phase.Casting || _castStartUtc != cast) return;
        EncoderLoad = load;
        Raise();
    }

    void OnRecorderDied()
    {
        if (Phase == Phase.Casting) _ = StopCastAsync("SCREEN CAPTURE STOPPED · SEE LOG", error: true);
    }

    // Once a second while online: feed the readout, and end the cast if the device stopped asking for video.
    void Tick()
    {
        if (Phase != Phase.Casting) return;
        Meter.Tick(_rec.EncodedBytes, _server.SentBytes);
        var now = DateTime.UtcNow;
        if (++_ticks % 10 == 0) // the readout, written down every 10 seconds so a cast can be judged from the log
        {
            Log.Write($"cast: made {Meter.EncodedLine[^1]:0.0} Mbps, sent {Meter.Now:0.0}, peak {Meter.Peak:0.0}, signal {Meter.SignalLabel}");
            if (CastingTo.Kind == DeviceKind.Cast) // the Roku reports its own side; this is the Chromecast's
            {
                var a = _server.TakeAsks();
                Log.Write($"chromecast: asked for the playlist {a.Playlists} times and {a.Pieces} pieces (last {a.LastPiece}, newest made {_rec.NewestPiece})" +
                          (a.Gone > 0 ? $", {a.Gone} already gone" : ""));
            }
            // Where a hitch starts: the screen clock's ticks, how often the screen really changed, whether the encoder kept
            // up, and what's using the card. Late = ticks the clock slept through (they repeat the last picture).
            var g = _rec.TakeGrabCounts();
            double cpu = _rec.TakeCpuPercent();
            DroppedFrames += g.Late;
            CpuLoad = cpu;
            var cast = _castStartUtc;
            _ = Task.Run(() =>
            {
                var gpu = GpuLoad.Sample(); // read off the window's thread: Windows' counters can take a moment
                Log.Write($"cast: timing: {g.Ticks} ticks ({g.NewPictures} new pictures, {g.Late} late, longest wake {g.LongestWakeMs:0.0} ms), " +
                          $"screen changed {g.ScreenUpdates} times, encoder: most waiting {g.MostWaiting}, held back {g.HeldBack}, " +
                          $"app cpu {cpu:0}%, {gpu.Line}");
                _ui.Post(_ => OnEncoderLoad(gpu.Encoder, cast), null);
            });
        }
        var last = _server.LastAskUtc > _castStartUtc ? _server.LastAskUtc : _castStartUtc;
        if (now - last > TimeSpan.FromSeconds(20))
        {
            _ = StopCastAsync("STOPPED PLAYING · CAST ENDED", about: CastingTo);
            return;
        }
        Raise();
    }
}
