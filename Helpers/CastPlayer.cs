using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;

namespace PCCast2TV.Helpers;

// Talks Google Cast to a Chromecast-style TV: opens Google's own media player that's built into every Cast
// device and has it play the cast's HLS link. Nothing gets installed on the TV. One player lives for one cast.
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
    string _sessionId;

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

    // Returns the player (kept so End Cast can close it) or the status line message when it didn't play.
    public static async Task<(CastPlayer Player, string Error)> PlayAsync(Device d, string url, Func<int> piecesServed)
    {
        var name = d.Name.ToUpperInvariant();
        var c = new CastPlayer(d);
        try
        {
            await c.OpenAsync();
            await c.SendAsync(NsReceiver, "receiver-0", new { type = "LAUNCH", appId = MediaReceiver, requestId = c.NextId() });
            var status = await c.WaitAsync(NsReceiver, m => Type(m) == "RECEIVER_STATUS" && PlayerApp(m) != null, 20);
            if (status == null)
            {
                c.Dispose();
                return (null, $"{name} DID NOT OPEN ITS PLAYER");
            }
            var (sessionId, transportId) = PlayerApp(status.Value).Value;
            c._sessionId = sessionId;
            Log.Write("cast: Google's player is open on the TV");
            await c.SendAsync(NsConnection, transportId, new { type = "CONNECT" });
            await c.SendAsync(NsMedia, transportId, new
            {
                // No currentTime: on a live stream that makes the player start at the live edge (0 = the oldest piece listed).
                type = "LOAD", requestId = c.NextId(), autoplay = true,
                media = new
                {
                    contentId = url, contentType = "application/x-mpegurl", streamType = "LIVE", hlsSegmentFormat = "ts",
                    metadata = new { metadataType = 0, title = "PC Cast2TV" },
                },
            });

            // Playing once the TV says so, or once it is pulling video pieces from us.
            int start = piecesServed();
            string lastSaid = null;
            for (int i = 0; i < 40 && !c._inbox.Reader.Completion.IsCompleted; i++)
            {
                var m = await c.WaitAsync(NsMedia, _ => true, 0.5);
                var t = m == null ? null : Type(m.Value);
                var said = m == null ? null : $"{t} {State(m.Value)} {Detail(m.Value)}".Trim();
                if (said != null && said != lastSaid) Log.Write("cast: TV player says " + (lastSaid = said));
                if (t is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST")
                {
                    Log.Write("cast: the TV refused the stream: " + t);
                    await c.StopAsync();
                    return (null, $"{name} REFUSED THE STREAM");
                }
                if ((t == "MEDIA_STATUS" && State(m.Value) == "PLAYING") || piecesServed() - start >= 3)
                {
                    Log.Write($"cast: playing (state {(m == null ? "none" : State(m.Value))}, pieces {piecesServed() - start})");
                    _ = c.WatchAsync(lastSaid);
                    return (c, null);
                }
            }
            Log.Write($"cast: never started (pieces {piecesServed() - start})");
            await c.StopAsync();
            return (null, $"{name} DID NOT START PLAYING");
        }
        catch (Exception ex) when (IsNetError(ex))
        {
            Log.Write("cast: connection failed: " + ex.GetType().Name + " " + ex.Message);
            c.Dispose();
            return (null, $"{name} STOPPED ANSWERING");
        }
    }

    // While the cast plays, every change the TV's player reports goes in the log, so a stop says why.
    async Task WatchAsync(string lastSaid)
    {
        bool saidClosed = false;
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(_watch.Token))
                while (!_watch.IsCancellationRequested && _inbox.Reader.TryRead(out var m))
                {
                    if (m.Ns == NsMedia)
                    {
                        var said = $"{Type(m.Msg)} {State(m.Msg)} {Detail(m.Msg)}".Trim();
                        if (said != lastSaid) Log.Write("cast: TV player says " + (lastSaid = said));
                    }
                    else if (m.Ns == NsReceiver && Type(m.Msg) == "RECEIVER_STATUS" && PlayerApp(m.Msg) == null && !saidClosed)
                    {
                        saidClosed = true;
                        Log.Write("cast: the TV closed Google's player");
                    }
                }
        }
        catch (OperationCanceledException) { }
    }

    // Closes Google's player on the TV, then the connection.
    public async Task StopAsync()
    {
        _watch.Cancel();
        try
        {
            if (_sessionId != null && _ssl != null)
            {
                await SendAsync(NsReceiver, "receiver-0", new { type = "STOP", sessionId = _sessionId, requestId = NextId() });
                await WaitAsync(NsReceiver, m => Type(m) == "RECEIVER_STATUS", 2);
                Log.Write("cast: player closed on the TV");
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
        // Cast devices identify with Google's own device certificates, not a website certificate, so the usual
        // check can't pass. The link only carries the cast's local address, and the server answers only this TV.
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _d.Ip, RemoteCertificateValidationCallback = (_, _, _, _) => true,
        });
        _ = ReadLoopAsync();
        await SendAsync(NsConnection, "receiver-0", new { type = "CONNECT" });
        _ping = new Timer(_ => _ = BeatAsync("PING"), null, 5000, 5000); // the TV drops a sender that goes quiet
    }

    async Task BeatAsync(string type)
    {
        try { await SendAsync(NsHeartbeat, "receiver-0", new { type }); }
        catch (Exception ex) when (IsNetError(ex)) { } // the read loop notices a dead connection
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
                    if (Type(msg) == "PING") _ = BeatAsync("PONG");
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

    static string Type(JsonElement m) => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("type", out var t) ? t.GetString() : null;

    static (string SessionId, string TransportId)? PlayerApp(JsonElement m)
    {
        if (!m.TryGetProperty("status", out var s) || !s.TryGetProperty("applications", out var apps) || apps.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in apps.EnumerateArray())
            if (a.TryGetProperty("appId", out var id) && id.GetString() == MediaReceiver
                && a.TryGetProperty("sessionId", out var sid) && a.TryGetProperty("transportId", out var tid))
                return (sid.GetString(), tid.GetString());
        return null;
    }

    static string State(JsonElement m) =>
        m.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0
        && s[0].TryGetProperty("playerState", out var p) ? p.GetString() : null;

    // Why the TV's player stopped or refused, when it says (idle reason or error code).
    static string Detail(JsonElement m)
    {
        if (m.TryGetProperty("detailedErrorCode", out var code)) return "error " + code;
        if (m.TryGetProperty("reason", out var reason)) return reason.ToString();
        return m.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0
               && s[0].TryGetProperty("idleReason", out var idle) ? "idle " + idle.GetString() : "";
    }

    static bool IsNetError(Exception ex) =>
        ex is IOException or SocketException or AuthenticationException or ObjectDisposedException or OperationCanceledException;

    public void Dispose()
    {
        _ping?.Dispose();
        _watch.Cancel();
        _cts.Cancel();
        _ssl?.Dispose();
        _tcp?.Dispose();
    }
}
