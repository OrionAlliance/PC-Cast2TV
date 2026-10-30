namespace PCCast2TV.Helpers;

// Casting: the checks that start a cast, ending it, and the Roku remote's buttons.
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
        await (_castWork = StartCastAsync(d));
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
                (NetUtil.AnyVpnUp ? "If it's on, your VPN may be blocking it. " + AllowLan : "If it's on, click Detect to find it again."));
            return;
        }
        _ = TvInfo.LogAsync(d); // maker, model and software for the log, asked alongside the start

        // A Roku plays through our Roku app, or DisplayHUB when ours isn't on it; with neither, the window shows how to add one.
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
        if (!await Task.Run(FirewallRule.Exists)) { FailCast("FIREWALL BLOCKS TVS · CLICK CONNECT AGAIN"); return; }
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
        // Set up off the window's thread, and made the cast's recorder only once it's up, so a close meanwhile can't tear it half down.
        var rec = new ScreenRecorder();
        _server.BeginCast(d.Ip, rec);
        rec.Died += () => _ui.Post(_ => OnRecorderDied(), null);
        if (!d.TakesHls) rec.LiveChunk += _server.PushLive;
        // A Chromecast's mirroring is capped at what it tells the TV; Roku and DLNA casts aren't.
        try { await Task.Run(() => rec.Start(Encoder, d.TakesHls ? OutputKind.Hls : OutputKind.LiveTs, d.Kind == DeviceKind.Cast ? MirrorSender.MaxBitRate : 0)); }
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
        // Mirroring sends live pictures, so a Chromecast doesn't wait for pieces here, only if it falls back to them.
        bool videoReady = d.Kind == DeviceKind.Cast || await _rec.WaitForVideoAsync(d.TakesHls, TimeSpan.FromSeconds(10));
        if (Phase != Phase.Starting) return;
        if (!videoReady) { FailCast("SCREEN CAPTURE DID NOT START · SEE LOG"); return; }
        _rec.TakeGrabCounts(); // the timing readout starts here
        _rec.TakeCpuPercent();
        _ = Task.Run(GpuLoad.Sample); // its first reading only sets the starting point

        Set(Phase.Starting, $"STARTING {name}...");
        // A Chromecast starts from the master list, which names the video's real format; the Roku takes the live list.
        var list = d.Kind == DeviceKind.Cast ? ScreenRecorder.MasterName : ScreenRecorder.PlaylistName;
        var hlsUrl = $"http://{localIp}:{_server.Port}/live/{list}?cast={DateTime.UtcNow.Ticks}";
        // Each kind of TV opens its own player; a Chromecast mirrors, or plays the pieces if it turns mirroring down.
        string error;
        CastPlayer player = null;
        bool viaPieces = true;
        if (d.IsRoku) error = await RokuPlayer.PlayAsync(d, rokuApp, hlsUrl, () => _server.PiecesServed);
        else if (d.Kind == DeviceKind.Cast)
        {
            (player, error) = await CastPlayer.MirrorAsync(d, _rec);
            viaPieces = error == CastPlayer.UsePieces;
            if (viaPieces && Phase == Phase.Starting)
            {
                Log.Write("cast: playing the pieces through Google's player instead");
                if (!await _rec.WaitForVideoAsync(true, TimeSpan.FromSeconds(10)))
                {
                    if (Phase == Phase.Starting) FailCast("SCREEN CAPTURE DID NOT START · SEE LOG");
                    return;
                }
                (player, error) = await CastPlayer.PlayAsync(d, hlsUrl, () => _server.PiecesServed);
            }
        }
        else error = await DlnaPlayer.PlayAsync(d, $"http://{localIp}:{_server.Port}/stream.ts");
        if (Phase != Phase.Starting) { await StopPlayerAsync(d, rokuApp, player); return; } // closed mid-start: don't leave the TV's player open
        if (error != null) { PlayerFailed(error, d, viaPieces); return; }
        _castPlayer = player;
        _rokuApp = rokuApp;

        // The last cast's health numbers stay up until this one is really playing, so a start that fails keeps them.
        HealthFrom = d;
        DroppedFrames = 0;
        CpuLoad = EncoderLoad = null;
        CastingTo = d;
        _castStartUtc = DateTime.UtcNow;
        Meter.Reset(_rec.EncodedBytes, SentBytes);
        bool sound = _rec.HasSound && player?.TvTookSound != false;
        Log.Write("cast: playing" + (!_rec.HasSound ? ", no sound device" : sound ? "" : ", the TV took no sound"));
        Set(Phase.Casting, $"CASTING TO {name}" + (sound ? "" : " · NO SOUND"));
    }

    // What has gone to the TV: a mirrored cast counts its own, the rest go through the server.
    long SentBytes => _castPlayer?.Mirrored == true ? _castPlayer.SentBytes : _server.SentBytes;

    // An ended cast says so for 5 seconds, an error stays up; kept so closing the app can let it finish.
    Task StopCastAsync(string message, bool error = false, Device about = null) => _castWork = EndCastAsync(message, error, about);

    async Task EndCastAsync(string message, bool error, Device about)
    {
        var d = CastingTo;
        Set(Phase.Stopping, "ENDING CAST...");
        await StopPlayerAsync(d, _rokuApp, _castPlayer);
        CleanupCast();
        Log.Write("cast: ended" + (message == null ? "" : " (" + ForLog(about, message) + ")"));
        Set(Phase.Online, message == null ? "CAST ENDED" : Shown(about, message));
        if (!error) _ = BackToReadyAsync();
    }

    // The TV's player goes back off: a Roku goes home (if ours is still on screen), a Chromecast closes, DLNA stops.
    static async Task StopPlayerAsync(Device d, RokuApp rokuApp, CastPlayer player)
    {
        if (d.IsRoku) await RokuPlayer.StopAsync(d, rokuApp);
        else if (d.Kind == DeviceKind.Cast) { if (player != null) await player.StopAsync(); }
        else await DlnaPlayer.StopAsync(d);
    }

    // A message about a device: its name on the status line, its kind and address in the log (logs get posted in public Issues).
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

    // The TV's player didn't start; pieces that never got a byte out on a network Windows calls Public mean the firewall.
    void PlayerFailed(string message, Device d, bool viaPieces)
    {
        bool nothingReached = viaPieces && _server.SentBytes == 0;
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

    // A Roku remote button from our Roku app: Back ends the cast, the rest become PC key presses (the Roku shows play/pause).
    void OnRemoteButton(string name)
    {
        if (Phase != Phase.Casting) return;
        if (_rokuApp != RokuApp.Ours)
        {
            Log.Write("remote: a button from a cast that isn't through our Roku app, ignored");
            return;
        }
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

    void OnRecorderDied()
    {
        if (Phase == Phase.Casting) _ = StopCastAsync("SCREEN CAPTURE STOPPED · SEE LOG", error: true);
    }
}
