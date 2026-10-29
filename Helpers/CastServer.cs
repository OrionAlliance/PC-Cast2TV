using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace PCCast2TV.Helpers;

// Tiny web server for the cast. It listens only while Connected, and only answers the one device being cast to.
public sealed class CastServer
{
    public const int FirstPort = 8765, LastPort = 8784;
    const string DlnaFeatures = "DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
    // A Chromecast's player is a web page, so it only takes video from a server that says any page may read it.
    const string Cors = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, HEAD, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\n";

    const long LiveSlack = 8_000_000; // bytes a DLNA TV may fall behind before it loses video

    sealed record Request(string Method, string Path, string Query, bool KeepAlive);

    // One TV's live stream: the bytes waiting for it, counted so a stalled TV can't hold the whole cast.
    sealed class LiveTv
    {
        public readonly Channel<byte[]> Line = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        public long Waiting;
    }

    TcpListener _listener;
    CancellationTokenSource _cts;
    readonly object _liveGate = new();
    readonly List<LiveTv> _live = new();
    long _sent, _lastAskTicks;
    int _piecesServed, _drops, _saidPlaylist, _saidMaster, _saidPiece;
    int _playlistAsks, _pieceAsks, _piecesGone, _lastPieceAsked; // what the TV asked for since the last TakeAsks
    readonly ConcurrentDictionary<string, bool> _turnedAway = new();
    volatile string _allowedIp;
    volatile ScreenRecorder _source; // the cast's pieces and live list

    public int Port { get; private set; }
    public long SentBytes => Interlocked.Read(ref _sent);
    public int PiecesServed => Volatile.Read(ref _piecesServed);
    public DateTime LastAskUtc => new(Interlocked.Read(ref _lastAskTicks), DateTimeKind.Utc);

    // Playlist checks and pieces asked for since the last call, how many were already gone, and the last piece asked for.
    public (int Playlists, int Pieces, int Gone, int LastPiece) TakeAsks() =>
        (Interlocked.Exchange(ref _playlistAsks, 0), Interlocked.Exchange(ref _pieceAsks, 0), Interlocked.Exchange(ref _piecesGone, 0),
         Volatile.Read(ref _lastPieceAsked));
    // The PC Cast2TV Roku app sends /key/<button>; called off the window's thread.
    public Action<string> RemoteButton { get; set; }

    // Listens on every adapter.
    public void Start()
    {
        for (int port = FirstPort; port <= LastPort && _listener == null; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                _listener = l;
                Port = port;
            }
            catch (SocketException) { } // taken, try the next one
        }
        if (_listener == null) throw new InvalidOperationException($"no free port from {FirstPort} to {LastPort}");
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
        Log.Write($"server: listening on port {Port}");
    }

    public void Stop()
    {
        EndCast();
        if (_listener == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _listener.Stop();
        _listener = null;
        Log.Write("server: stopped");
    }

    // Opens the door for one device for the length of a cast.
    public void BeginCast(string deviceIp, ScreenRecorder source)
    {
        _source = source;
        Interlocked.Exchange(ref _sent, 0);
        Interlocked.Exchange(ref _lastAskTicks, 0);
        Volatile.Write(ref _piecesServed, 0);
        Volatile.Write(ref _drops, 0);
        Volatile.Write(ref _saidPlaylist, 0);
        Volatile.Write(ref _saidMaster, 0);
        Volatile.Write(ref _saidPiece, 0);
        TakeAsks();
        Volatile.Write(ref _lastPieceAsked, -1);
        _turnedAway.Clear();
        _allowedIp = deviceIp;
    }

    public void EndCast()
    {
        _allowedIp = null;
        _source = null;
        lock (_liveGate)
        {
            foreach (var tv in _live) tv.Line.Writer.TryComplete();
            _live.Clear();
        }
    }

    // Hands live-stream bytes to the TV. A TV that falls 8 MB behind loses chunks instead of stalling the recorder.
    public void PushLive(byte[] buf, int n)
    {
        lock (_liveGate)
        {
            if (_live.Count == 0) return;
            var copy = buf.AsSpan(0, n).ToArray();
            foreach (var tv in _live)
            {
                if (Interlocked.Read(ref tv.Waiting) + n > LiveSlack)
                {
                    if (Interlocked.Increment(ref _drops) == 1) Log.Write("server: TV is not keeping up, dropping live chunks");
                    continue;
                }
                Interlocked.Add(ref tv.Waiting, n);
                tv.Line.Writer.TryWrite(copy);
            }
        }
    }

    async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (!ct.IsCancellationRequested) Log.Write("server: accept failed: " + ex.Message);
                return;
            }
            _ = Task.Run(() => ServeAsync(c, ct));
        }
    }

    async Task ServeAsync(TcpClient c, CancellationToken ct)
    {
        using (c)
        {
            var ip = (c.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString();
            if (ip == null || ip != _allowedIp)
            {
                // not the device we're casting to; written once per address so the log shows who knocked
                if (ip != null && _turnedAway.TryAdd(ip, true)) Log.Write($"server: turned away {ip} (not the device being cast to)");
                return;
            }
            c.NoDelay = true;
            var s = c.GetStream();
            var incoming = new Incoming();
            try
            {
                while (!ct.IsCancellationRequested && ip == _allowedIp)
                {
                    var req = await ReadRequestAsync(s, incoming, ct);
                    if (req == null || ip != _allowedIp) return; // checked again: the cast may have moved on while this connection sat open
                    Interlocked.Exchange(ref _lastAskTicks, DateTime.UtcNow.Ticks);
                    if (!await AnswerAsync(s, req, ip, ct)) return;
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }

    // One connection's incoming bytes: a request can arrive in pieces, or two can arrive in one read.
    sealed class Incoming
    {
        public readonly byte[] Buf = new byte[8192];
        public int Len;
    }

    static async Task<Request> ReadRequestAsync(NetworkStream s, Incoming inc, CancellationToken ct)
    {
        int end;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(15));
        while ((end = Encoding.ASCII.GetString(inc.Buf, 0, inc.Len).IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
        {
            if (inc.Len == inc.Buf.Length) return null; // far bigger than any player request
            int n;
            try { n = await s.ReadAsync(inc.Buf.AsMemory(inc.Len), idle.Token); }
            catch (OperationCanceledException) { return null; }
            if (n == 0) return null;
            inc.Len += n;
        }
        var lines = Encoding.ASCII.GetString(inc.Buf, 0, end).Split("\r\n");
        int used = end + 4; // what came after this header is the start of the next request
        Buffer.BlockCopy(inc.Buf, used, inc.Buf, 0, inc.Len - used);
        inc.Len -= used;
        var parts = lines[0].Split(' ');
        if (parts.Length < 3) return null;
        bool close = parts[2] == "HTTP/1.0" || lines.Any(l =>
            l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) && l.Contains("close", StringComparison.OrdinalIgnoreCase));
        string path = parts[1], query = "";
        int q = path.IndexOf('?');
        if (q >= 0) (path, query) = (path[..q], path[(q + 1)..]);
        return new Request(parts[0].ToUpperInvariant(), path, query, !close);
    }

    async Task<bool> AnswerAsync(NetworkStream s, Request r, string ip, CancellationToken ct)
    {
        bool head = r.Method == "HEAD";
        if (r.Method == "OPTIONS") // a web player asking first whether it may read the video
            return await SendAsync(s, "204 No Content", "text/plain", null, r.KeepAlive, ct);
        if (r.Method != "GET" && !head) return await SendAsync(s, "405 Method Not Allowed", "text/plain", null, false, ct);
        if (r.Path == "/live/" + ScreenRecorder.MasterName)
        {
            var text = _source?.Master;
            if (Interlocked.Exchange(ref _saidMaster, 1) == 0) Log.Write($"server: {ip} asked for the master list ({(text == null ? "not ready" : "sent")})");
            return await SendAsync(s, text == null ? "404 Not Found" : "200 OK", "application/vnd.apple.mpegurl",
                text == null ? null : Encoding.UTF8.GetBytes(text), r.KeepAlive, ct, head);
        }
        if (r.Path == "/live/" + ScreenRecorder.PlaylistName)
        {
            var text = _source?.Playlist;
            if (!head) Interlocked.Increment(ref _playlistAsks);
            if (Interlocked.Exchange(ref _saidPlaylist, 1) == 0) Log.Write($"server: {ip} asked for the playlist ({(text == null ? "not ready" : "sent")})");
            return await SendAsync(s, text == null ? "404 Not Found" : "200 OK", "application/vnd.apple.mpegurl",
                text == null ? null : Encoding.UTF8.GetBytes(text), r.KeepAlive, ct, head);
        }
        if (r.Path.StartsWith("/live/", StringComparison.Ordinal) && ScreenRecorder.PieceName.Match(r.Path[6..]) is { Success: true } piece)
        {
            int number = int.Parse(piece.Groups[1].Value);
            var body = _source?.Piece(number);
            if (!head)
            {
                Interlocked.Increment(ref _pieceAsks);
                Volatile.Write(ref _lastPieceAsked, number);
                if (body == null) Interlocked.Increment(ref _piecesGone);
            }
            await SendAsync(s, body == null ? "404 Not Found" : "200 OK", "video/mp2t", body, r.KeepAlive, ct, head);
            if (body != null && !head && Interlocked.Increment(ref _piecesServed) == 1) Log.Write($"server: first video piece sent to {ip}");
            if (body == null && Interlocked.Exchange(ref _saidPiece, 1) == 0) Log.Write($"server: {ip} asked for a piece that is gone ({Log.Outside(r.Path[6..])})");
            return r.KeepAlive;
        }
        if (r.Path.StartsWith("/key/", StringComparison.Ordinal))
        {
            if (!head) RemoteButton?.Invoke(r.Path[5..]);
            return await SendAsync(s, "204 No Content", "text/plain", null, r.KeepAlive, ct);
        }
        if (r.Path == "/report") // the PC Cast2TV Roku app's player numbers, every 10 seconds
        {
            if (!head) Log.Write("roku: " + RokuReport.Describe(r.Query, _source?.NewestPiece));
            return await SendAsync(s, "204 No Content", "text/plain", null, r.KeepAlive, ct);
        }
        if (r.Path == "/stream.ts") return await StreamLiveAsync(s, head, ct);
        return await SendAsync(s, "404 Not Found", "text/plain", null, r.KeepAlive, ct);
    }

    // Writes the answer; hands back keep (whether the connection stays open) so a branch can return it.
    async Task<bool> SendAsync(NetworkStream s, string status, string type, byte[] body, bool keep, CancellationToken ct, bool headOnly = false)
    {
        var h = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body?.Length ?? 0}\r\nCache-Control: no-cache\r\n" +
                Cors + $"Connection: {(keep ? "keep-alive" : "close")}\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(h), ct);
        if (headOnly || body == null || body.Length == 0) return keep;
        await s.WriteAsync(body, ct);
        Interlocked.Add(ref _sent, body.Length);
        return keep;
    }

    // One endless MPEG-TS stream for DLNA TVs, with the DLNA headers TVs look for.
    async Task<bool> StreamLiveAsync(NetworkStream s, bool head, CancellationToken ct)
    {
        var h = "HTTP/1.1 200 OK\r\nContent-Type: video/mpeg\r\ntransferMode.dlna.org: Streaming\r\n" +
                $"contentFeatures.dlna.org: {DlnaFeatures}\r\nCache-Control: no-cache\r\n" + Cors + "Connection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(h), ct);
        if (head) return false;
        var tv = new LiveTv();
        lock (_liveGate) _live.Add(tv);
        Log.Write("server: TV opened the live stream");
        try
        {
            await foreach (var chunk in tv.Line.Reader.ReadAllAsync(ct))
            {
                await s.WriteAsync(chunk, ct);
                Interlocked.Add(ref tv.Waiting, -chunk.Length);
                Interlocked.Add(ref _sent, chunk.Length);
                Interlocked.Exchange(ref _lastAskTicks, DateTime.UtcNow.Ticks);
            }
        }
        finally
        {
            lock (_liveGate) _live.Remove(tv);
            Log.Write("server: TV closed the live stream");
        }
        return false;
    }
}
