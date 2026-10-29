using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;

namespace PCCast2TV.Helpers;

// Talks Google Cast to a Chromecast-style TV through its built-in mirroring or media player; one player per cast.
public sealed partial class CastPlayer : IDisposable
{
    const string MediaReceiver = "CC1AD845"; // Google's Default Media Receiver
    const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
    const string NsHeartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    const string NsReceiver = "urn:x-cast:com.google.cast.receiver";
    const string NsMedia = "urn:x-cast:com.google.cast.media";

    readonly Device _d;
    readonly SemaphoreSlim _sendGate = new(1, 1);
    // Only the newest are kept, so a chatty TV can't grow it.
    readonly Channel<(string Ns, JsonElement Msg)> _inbox = Channel.CreateBounded<(string, JsonElement)>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly CancellationTokenSource _cts = new();
    readonly CancellationTokenSource _watch = new(); // ends the watch so Stop can read the TV's answer itself
    TcpClient _tcp;
    SslStream _ssl;
    Timer _ping;
    int _requestId;
    string _sessionId, _transportId, _lastSaid;
    string _appId = MediaReceiver;
    Timer _ask;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double? _lastAt;    // the player's spot at its last report
    double _lastAtClock; // and when that report came, on _clock

    CastPlayer(Device d) => _d = d;

    // Answering = it takes a Cast connection and reports its status. Nothing changes on the TV.
    public static async Task<bool> AnsweringAsync(Device d)
    {
        using var c = new CastPlayer(d);
        try
        {
            await c.OpenAsync();
            await c.SendAsync(NsReceiver, "receiver-0", new { type = "GET_STATUS", requestId = c.NextId() });
            return await c.WaitAsync(NsReceiver, m => Type(m) == "RECEIVER_STATUS", 5) != null;
        }
        catch (Exception ex) when (IsNetError(ex))
        {
            Log.Write($"cast: {d.Ip} not answering: {ex.GetType().Name}");
            return false;
        }
    }

    // Returns the player (kept so End Cast can close it), or what went wrong when it didn't play (the caller puts the TV's name in front).
    public static async Task<(CastPlayer Player, string Error)> PlayAsync(Device d, string url, Func<int> piecesServed)
    {
        var c = new CastPlayer(d);
        try
        {
            await c.OpenAsync();
            await c.SendAsync(NsReceiver, "receiver-0", new { type = "LAUNCH", appId = MediaReceiver, requestId = c.NextId() });
            var status = await c.WaitAsync(NsReceiver, m => Type(m) == "RECEIVER_STATUS" && c.App(m) != null, 20);
            if (status == null)
            {
                c.Dispose();
                return (null, "DID NOT OPEN ITS PLAYER");
            }
            (c._sessionId, c._transportId) = c.App(status.Value).Value;
            Log.Write("cast: Google's player is open on the TV");
            await c.SendAsync(NsConnection, c._transportId, new { type = "CONNECT" });
            await c.SendAsync(NsMedia, c._transportId, new
            {
                // No currentTime: on a live stream that makes the player start at the live edge (0 = the oldest piece listed).
                type = "LOAD", requestId = c.NextId(), autoplay = true,
                media = new
                {
                    contentId = url, contentType = "application/x-mpegurl", streamType = "LIVE", hlsSegmentFormat = "ts",
                    metadata = new { metadataType = 0, title = "PC Cast2TV" },
                },
            });
            // Asked once a second where its player is, so the log shows a stuck or jumping player between its own reports.
            c._ask = new Timer(_ => _ = c.SendQuietlyAsync(NsMedia, c._transportId, new { type = "GET_STATUS", requestId = c.NextId() }), null, 1000, 1000);

            // Playing once the TV says so, or once it is pulling video pieces from us.
            int start = piecesServed();
            for (int i = 0; i < 40 && !c._inbox.Reader.Completion.IsCompleted; i++)
            {
                var m = await c.WaitAsync(NsMedia, _ => true, 0.5);
                var t = m == null ? null : Type(m.Value);
                if (m != null) c.Heard(m.Value);
                if (t is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST")
                {
                    Log.Write("cast: the TV refused the stream: " + t);
                    await c.StopAsync();
                    return (null, "REFUSED THE STREAM");
                }
                if ((t == "MEDIA_STATUS" && State(m.Value) == "PLAYING") || piecesServed() - start >= 3)
                {
                    Log.Write($"cast: playing (state {(m == null ? "none" : Log.Outside(State(m.Value)))}, pieces {piecesServed() - start})");
                    _ = c.WatchAsync();
                    return (c, null);
                }
            }
            Log.Write($"cast: never started (pieces {piecesServed() - start})");
            await c.StopAsync();
            return (null, "DID NOT START PLAYING");
        }
        catch (Exception ex) when (IsNetError(ex))
        {
            Log.Write("cast: connection failed: " + ex.GetType().Name + " " + ex.Message);
            c.Dispose();
            return (null, "STOPPED ANSWERING");
        }
    }

    // While the cast plays, the TV's reports go in the log (see Heard) so a stop or a loop says why; mirroring's first 5 kinds.
    async Task WatchAsync()
    {
        bool saidClosed = false;
        var mirrorSaid = new HashSet<string>();
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(_watch.Token))
                while (!_watch.IsCancellationRequested && _inbox.Reader.TryRead(out var m))
                {
                    if (m.Ns == NsMedia) Heard(m.Msg);
                    else if (m.Ns == NsWebRtc && mirrorSaid.Count < 5 && mirrorSaid.Add(Type(m.Msg) ?? "")) Log.Write("cast: the TV's mirroring says " + MirrorSender.Flat(m.Msg));
                    else if (m.Ns == NsReceiver && Type(m.Msg) == "RECEIVER_STATUS" && App(m.Msg) == null && !saidClosed)
                    {
                        saidClosed = true;
                        Log.Write(_mirror != null ? "cast: the TV closed the mirroring" : "cast: the TV closed Google's player");
                    }
                }
        }
        catch (OperationCanceledException) { }
    }

    // Logs each change in the TV player's state, and any report where its spot didn't keep pace with the clock.
    void Heard(JsonElement m)
    {
        var said = $"{Type(m)} {State(m)} {Detail(m)}".Trim();
        double? at = At(m);
        double now = _clock.Elapsed.TotalSeconds;
        if (said != _lastSaid) Log.Write("cast: TV player says " + Log.Outside(_lastSaid = said) + Where(m));
        else if (at != null && _lastAt != null)
        {
            double took = now - _lastAtClock, moved = at.Value - _lastAt.Value;
            if (State(m) != "PLAYING" || took >= 0.5 && (moved < took * 0.5 || moved > took * 1.5))
                Log.Write($"cast: TV player {(Log.Outside(State(m)) ?? "with no state")}, moved {moved:0.00} s in {took:0.00} s" + Where(m));
        }
        if (at != null) (_lastAt, _lastAtClock) = (at, now);
    }

    // Closes the TV app this player opened (the mirroring or Google's player), then the connection.
    public async Task StopAsync()
    {
        _ask?.Dispose();
        _watch.Cancel();
        _mirror?.Dispose();
        try
        {
            if (_sessionId != null && _ssl != null)
            {
                await SendAsync(NsReceiver, "receiver-0", new { type = "STOP", sessionId = _sessionId, requestId = NextId() });
                await WaitAsync(NsReceiver, m => Type(m) == "RECEIVER_STATUS", 2);
                Log.Write(_appId == Mirroring ? "cast: mirroring closed on the TV" : "cast: player closed on the TV");
            }
        }
        catch (Exception ex) when (IsNetError(ex)) { Log.Write("cast: stop failed: " + ex.GetType().Name); }
        Dispose();
    }

    async Task OpenAsync()
    {
        _tcp = new TcpClient();
        using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await _tcp.ConnectAsync(_d.Ip, _d.Port > 0 ? _d.Port : 8009, t.Token);
        _ssl = new SslStream(_tcp.GetStream(), false);
        // Cast devices use Google's own certificates, which the usual check can't pass; the link stays on the home network.
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _d.Ip, RemoteCertificateValidationCallback = (_, _, _, _) => true,
        });
        _ = ReadLoopAsync();
        await SendAsync(NsConnection, "receiver-0", new { type = "CONNECT" });
        _ping = new Timer(_ => _ = SendQuietlyAsync(NsHeartbeat, "receiver-0", new { type = "PING" }), null, 5000, 5000); // the TV drops a sender that goes quiet
    }

    // A send nobody waits on; the read loop notices a dead connection.
    async Task SendQuietlyAsync(string ns, string destination, object payload)
    {
        try { await SendAsync(ns, destination, payload); }
        catch (Exception ex) when (IsNetError(ex)) { }
    }

    async Task ReadLoopAsync()
    {
        var head = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _ssl.ReadExactlyAsync(head, _cts.Token);
                int n = BinaryPrimitives.ReadInt32BigEndian(head);
                if (n <= 0 || n > 1_000_000)
                {
                    Log.Write($"cast: the TV sent a message the app can't read ({n} bytes), link closed");
                    break;
                }
                var body = new byte[n];
                await _ssl.ReadExactlyAsync(body, _cts.Token);
                var (ns, payload) = Decode(body);
                if (payload == null) continue;
                JsonElement msg;
                using (var doc = JsonDocument.Parse(payload)) msg = doc.RootElement.Clone();
                if (ns == NsHeartbeat)
                {
                    if (Type(msg) == "PING") _ = SendQuietlyAsync(NsHeartbeat, "receiver-0", new { type = "PONG" });
                    continue;
                }
                _inbox.Writer.TryWrite((ns, msg));
            }
        }
        catch (Exception ex) when (IsNetError(ex) || ex is JsonException)
        {
            if (!_cts.IsCancellationRequested) Log.Write($"cast: the TV's link ended: {ex.GetType().Name} {ex.Message}");
        }
        _inbox.Writer.TryComplete();
    }

    async Task<JsonElement?> WaitAsync(string ns, Func<JsonElement, bool> match, double seconds)
    {
        using var t = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(t.Token))
                while (_inbox.Reader.TryRead(out var m))
                    if (m.Ns == ns && match(m.Msg)) return m.Msg;
        }
        catch (OperationCanceledException) { }
        return null;
    }

    async Task SendAsync(string ns, string destination, object payload)
    {
        var bytes = Encode(ns, destination, JsonSerializer.Serialize(payload));
        await _sendGate.WaitAsync();
        try
        {
            await _ssl.WriteAsync(bytes);
            await _ssl.FlushAsync();
        }
        finally { _sendGate.Release(); }
    }

    int NextId() => Interlocked.Increment(ref _requestId);

    // ---- reading the TV's answers ----

    static string Type(JsonElement m) => Text(m, "type");

    // A text field of a TV message; null when the message or the field isn't the shape it should be.
    static string Text(JsonElement m, string name) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The first entry of a message's status list, when it has one that is an object.
    static JsonElement? FirstStatus(JsonElement m) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0
        && s[0].ValueKind == JsonValueKind.Object ? s[0] : null;

    // The session of the TV app this player opened, when the TV's status lists it.
    (string SessionId, string TransportId)? App(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("applications", out var apps) || apps.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in apps.EnumerateArray())
            if (Text(a, "appId") == _appId && Text(a, "sessionId") is { } sid && Text(a, "transportId") is { } tid)
                return (sid, tid);
        return null;
    }

    static string State(JsonElement m) => FirstStatus(m) is { } st ? Text(st, "playerState") : null;

    // Why the TV's player stopped or refused, when it says (idle reason or error code).
    static string Detail(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return "";
        if (m.TryGetProperty("detailedErrorCode", out var code)) return "error " + code;
        if (m.TryGetProperty("reason", out var reason)) return reason.ToString();
        return FirstStatus(m) is { } st && Text(st, "idleReason") is { } idle ? "idle " + idle : "";
    }

    // The player's spot and the live stretch it says it can play, in the cast's seconds, for the log.
    static string Where(JsonElement m)
    {
        if (FirstStatus(m) is not { } st) return "";
        var parts = new List<string>();
        if (At(m) is { } at) parts.Add($"at {at:0.00} s");
        if (st.TryGetProperty("liveSeekableRange", out var r) && r.ValueKind == JsonValueKind.Object
            && r.TryGetProperty("start", out var a) && a.ValueKind == JsonValueKind.Number
            && r.TryGetProperty("end", out var b) && b.ValueKind == JsonValueKind.Number)
            parts.Add($"can play {a.GetDouble():0.00} to {b.GetDouble():0.00} s");
        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
    }

    // The player's spot in the cast, in seconds; null when it doesn't say.
    static double? At(JsonElement m) =>
        FirstStatus(m) is { } st && st.TryGetProperty("currentTime", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : null;

    static bool IsNetError(Exception ex) =>
        ex is IOException or SocketException or AuthenticationException or ObjectDisposedException or OperationCanceledException;

    public void Dispose()
    {
        _mirror?.Dispose();
        _ask?.Dispose();
        _ping?.Dispose();
        _watch.Cancel();
        _cts.Cancel();
        _ssl?.Dispose();
        _tcp?.Dispose();
    }
}
