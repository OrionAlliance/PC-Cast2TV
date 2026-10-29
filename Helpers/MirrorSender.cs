using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCCast2TV.Helpers;

// Screen mirroring the way Chrome casts a screen: frames go to the TV live, locked and packed, and missed packets go again.
public sealed partial class MirrorSender : IDisposable
{
    // Google's sizes and numbers (Open Screen), paced under a TV's 100 Mbps wired port; the TV holds each frame 400 ms.
    public const int DelayMs = 400;
    public const int MaxBitRate = 20_000_000; // the most the TV is told to expect, and what the encoder holds to
    const int MaxPacket = 1500 - 20 - 8, Header = 19, Room = MaxPacket - Header - 4;
    const byte VideoType = 96, AudioType = 127;
    const int BurstBytes = 64 * 1024;
    const double BytesPerSecond = 70e6 / 8;
    const long MaxQueued = 8 << 20;
    const uint Cast = 0x43415354;
    static readonly long ResendGap = Stopwatch.Frequency / 20, KeyGrace = Stopwatch.Frequency * 3 / 10, KeptFor = Stopwatch.Frequency;
    static readonly long CatchUp = Stopwatch.Frequency * DelayMs / 3000; // a third of the TV's hold, Chrome's in-flight limit

    // One track (picture or sound): its key, ids, working buffers and the frames kept for resending.
    sealed class Track
    {
        public readonly bool Video;
        public readonly int Rate;
        public readonly uint Ssrc = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        public readonly byte[] Key = RandomNumberGenerator.GetBytes(16), Mask = RandomNumberGenerator.GetBytes(16);
        public readonly uint Offset = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        public readonly Aes Aes = Aes.Create();
        public ushort Seq = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue);
        public uint Next;
        public long Acked = -1;
        public long LastPts = long.MinValue;
        public bool Flowing;
        public uint Packets, Octets;
        public readonly LinkedList<Sent> Kept = new();
        public byte[] Counters = [], Locked = [];

        public Track(bool video, int rate)
        {
            (Video, Rate) = (video, rate);
            Aes.Key = Key;
        }
    }

    // A sent frame's packets, kept until the TV has it.
    sealed class Sent(uint id, long at, byte[][] packets)
    {
        public readonly uint Id = id;
        public readonly long At = at;
        public readonly byte[][] Packets = packets;
        public readonly long[] Resent = Enumerable.Repeat(at, packets.Length).ToArray();
    }

    readonly object _gate = new();
    readonly Action _askKey;
    readonly int _fps, _width, _height;
    Track _video, _audio;
    readonly BlockingCollection<byte[]> _out = new();
    readonly CancellationTokenSource _stop = new();
    readonly TaskCompletionSource _heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly DateTime _ntpBase = DateTime.UtcNow;
    readonly long _ntpBaseTicks = Stopwatch.GetTimestamp();
    Socket _sock;
    Thread _sender, _listener;
    (long At, long Pts)? _origin;
    long _lastKey, _queued, _sent, _lastHeardTicks;
    int _port, _frames, _resent, _keysAsked, _tvDelay, _sendMisses, _dropped, _held, _lastSendError, _lastHearError;
    volatile bool _closed;

    public long SentBytes => Interlocked.Read(ref _sent);
    public bool HasSound => _audio != null;
    public DateTime LastHeardUtc => new(Interlocked.Read(ref _lastHeardTicks), DateTimeKind.Utc);

    // The cast's picture size and rate, and the sound's rate (0: no sound).
    public MirrorSender(int fps, int width, int height, int soundRate, Action askKey)
    {
        (_fps, _width, _height, _askKey) = (fps, width, height, askKey);
        _video = new Track(true, 90_000);
        if (soundRate > 0) _audio = new Track(false, soundRate);
    }

    // What this PC offers: H.264 pictures and AAC sound, each with its own key.
    public object Offer(int seq) => new
    {
        type = "OFFER", seqNum = seq,
        offer = new { castMode = "mirroring", receiverGetStatus = false, supportedStreams = _audio == null ? new[] { Describe(_video, 0) } : new[] { Describe(_video, 0), Describe(_audio, 1) } },
    };

    object Describe(Track s, int index) => s.Video
        ? new
        {
            index, type = "video_source", codecName = "h264", rtpProfile = "cast", rtpPayloadType = VideoType, ssrc = s.Ssrc,
            targetDelay = DelayMs, aesKey = Convert.ToHexStringLower(s.Key), aesIvMask = Convert.ToHexStringLower(s.Mask),
            timeBase = "1/90000", receiverRtcpEventLog = false, rtpExtensions = new[] { "adaptive_playout_delay" },
            maxFrameRate = $"{_fps * 1000}/1000", maxBitRate = MaxBitRate, resolutions = new[] { new { width = _width, height = _height } },
        }
        : new
        {
            index, type = "audio_source", codecName = "aac", rtpProfile = "cast", rtpPayloadType = AudioType, ssrc = s.Ssrc,
            targetDelay = DelayMs, aesKey = Convert.ToHexStringLower(s.Key), aesIvMask = Convert.ToHexStringLower(s.Mask),
            timeBase = $"1/{s.Rate}", receiverRtcpEventLog = false, rtpExtensions = new[] { "adaptive_playout_delay" },
            bitRate = 160_000, sampleRate = s.Rate, channels = 2,
        };

    // Reads the TV's ANSWER: null when it took the picture (and says so in the log), otherwise why it didn't.
    public string Accept(JsonElement answer)
    {
        if (!answer.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.String || r.GetString() != "ok")
            return "the TV said " + (answer.TryGetProperty("error", out var e) ? Flat(e) : "no result");
        if (!answer.TryGetProperty("answer", out var a) || a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("udpPort", out var p)
            || p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out _port) || _port is < 1 or > 65535)
            return "the answer named no port";
        var taken = new List<int>();
        if (a.TryGetProperty("sendIndexes", out var ix) && ix.ValueKind == JsonValueKind.Array)
            foreach (var x in ix.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int i)) taken.Add(i);
        if (!taken.Contains(0)) return "it took no picture";
        if (!taken.Contains(1) && _audio != null)
        {
            Log.Write("cast: the TV took the picture but not the sound");
            _audio = null;
        }
        var said = new List<string>();
        foreach (var name in new[] { "constraints", "display" })
            if (a.TryGetProperty(name, out var v)) said.Add(Flat(v, name));
        Log.Write("cast: the TV took the mirroring" + (said.Count > 0 ? ": " + string.Join(" ", said) : ""));
        return null;
    }

    // A TV message flattened for the log as plain name=value pairs, 300 characters at most.
    public static string Flat(JsonElement e, string at = "")
    {
        var parts = new List<string>();
        void Walk(JsonElement x, string path)
        {
            if (parts.Count >= 40) return;
            if (x.ValueKind == JsonValueKind.Object)
                foreach (var p in x.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
            else if (x.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var v in x.EnumerateArray()) Walk(v, $"{path}.{i++}");
            }
            else parts.Add(Log.Outside(path) + "=" + Log.Outside(x.ToString()));
        }
        Walk(e, at);
        var line = string.Join(" ", parts);
        return line.Length <= 300 ? line : line[..300] + "...";
    }

    // Starts sending to the port the TV named, and asks for a keyframe to start on.
    public void Start(IPAddress tv)
    {
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 50, SendBufferSize = 1 << 20 };
        _sock.IOControl(unchecked((int)0x9800000C), new byte[4], null); // SIO_UDP_CONNRESET off, so a closed port can't end the listening
        _sock.Connect(tv, _port);
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "mirror sender", Priority = ThreadPriority.AboveNormal };
        _listener = new Thread(Listen) { IsBackground = true, Name = "mirror listener" };
        _sender.Start();
        _listener.Start();
        _askKey();
    }

    // True once the TV reports having the first picture.
    public async Task<bool> HeardAsync(double seconds) => await Task.WhenAny(_heard.Task, Task.Delay(TimeSpan.FromSeconds(seconds))) == _heard.Task;

    // Pictures start (and start over) at a keyframe; sound waits for the first picture, which sets the clock.
    public void AddVideo(byte[] frame, long pts90k, bool key)
    {
        lock (_gate)
        {
            if (_closed) return;
            var s = _video;
            if (!s.Flowing && !key) { if (Interlocked.Read(ref _queued) <= MaxQueued) _askKey(); return; }
            _origin ??= (Stopwatch.GetTimestamp(), pts90k);
            if (Queue(s, frame, pts90k, (uint)pts90k, key) && key) _lastKey = Stopwatch.GetTimestamp();
        }
    }

    public void AddAudio(byte[] adts, long pts90k)
    {
        lock (_gate)
        {
            if (_closed || _audio is not { } s || _origin == null) return;
            Queue(s, adts, pts90k, (uint)(pts90k * s.Rate / 90_000), true);
        }
    }

    // Chrome's catch-up rule: true while the TV sits on a frame past a third of its hold (the screen clock then repeats the last picture).
    public bool Behind()
    {
        lock (_gate)
        {
            if (_closed || _video.Acked < 0 || _video.Kept.First is not { } oldest || Stopwatch.GetTimestamp() - oldest.Value.At <= CatchUp) return false;
            _held++;
            return true;
        }
    }

    // Locks a frame, packs it and lines it up (kept for resends); while the TV is stalled, frames drop until a keyframe.
    bool Queue(Track s, byte[] data, long pts, uint ticks, bool key)
    {
        if (pts <= s.LastPts) return false;
        if (Interlocked.Read(ref _queued) > MaxQueued)
        {
            if (s.Video) { s.Flowing = false; _dropped++; }
            return false;
        }
        s.LastPts = pts;
        s.Flowing = true;
        uint id = s.Next++;
        bool alone = key || !s.Video;
        Lock(s, id, data);
        int count = Math.Max(1, (data.Length + Room - 1) / Room);
        var packets = new byte[count][];
        for (int i = 0; i < count; i++) packets[i] = Packet(s, id, alone ? id : id - 1, alone, s.Offset + ticks, data.Length, i, count);
        long now = Stopwatch.GetTimestamp();
        s.Kept.AddLast(new Sent(id, now, packets));
        while (s.Kept.Count > 240 || now - s.Kept.First.Value.At > KeptFor) s.Kept.RemoveFirst();
        s.Packets += (uint)count;
        s.Octets += (uint)data.Length;
        if (s.Video) _frames++;
        foreach (var p in packets) Put(p);
        return true;
    }

    void Put(byte[] p)
    {
        if (_closed) return;
        Interlocked.Add(ref _queued, p.Length);
        _out.Add(p);
    }

    // Closed under the frames' lock, so no frame is half way into the line when it closes.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }
        _stop.Cancel();
        _sender?.Join(500);
        _listener?.Join(500);
        _sock?.Dispose();
        lock (_gate)
        {
            _video.Aes.Dispose();
            _audio?.Aes.Dispose();
        }
    }
}
