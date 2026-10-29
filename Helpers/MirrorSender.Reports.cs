using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

namespace PCCast2TV.Helpers;

// Mirroring's way back: the TV's frame reports and picture-lost notes, our sender reports, and the log's 10-second line.
public sealed partial class MirrorSender
{
    // Hears the TV's reports, and sends ours every half second.
    void Listen()
    {
        var buf = new byte[2048];
        long nextReport = 0;
        try
        {
            while (!_closed)
            {
                try
                {
                    int n = _sock.Receive(buf);
                    Interlocked.Exchange(ref _lastHeardTicks, DateTime.UtcNow.Ticks);
                    lock (_gate) Heard(buf.AsSpan(0, n));
                }
                catch (SocketException ex) when (ex.SocketErrorCode != SocketError.TimedOut)
                {
                    if (Interlocked.Exchange(ref _lastHearError, (int)ex.SocketErrorCode) != (int)ex.SocketErrorCode && !_closed)
                        Log.Write("cast: mirroring could not hear the TV (" + ex.SocketErrorCode + "), still listening");
                    _stop.Token.WaitHandle.WaitOne(50);
                }
                catch (SocketException) { }
                long now = Stopwatch.GetTimestamp();
                if (now >= nextReport)
                {
                    Report(now);
                    nextReport = now + Stopwatch.Frequency / 2;
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    // A sender report per track ties its time to the clock, so the TV can line up pictures and sound.
    void Report(long now)
    {
        var reports = new List<byte[]>();
        lock (_gate)
        {
            if (_origin is not { } o) return;
            ulong ntp = Ntp(now);
            long media = o.Pts + (now - o.At) * 90_000 / Stopwatch.Frequency;
            foreach (var s in new[] { _video, _audio })
            {
                if (s == null || s.Next == 0) continue;
                var r = new byte[28];
                r[0] = 0x80;
                r[1] = 200;
                BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), 6);
                BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), s.Ssrc);
                BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(8), ntp);
                BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(16), s.Offset + (uint)(media * s.Rate / 90_000));
                BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(20), s.Packets);
                BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(24), s.Octets);
                reports.Add(r);
            }
        }
        foreach (var r in reports) Send(r);
    }

    // NTP time (seconds since 1900, then the fraction) on a steady clock, so a clock change can't jump it.
    ulong Ntp(long now)
    {
        var t = _ntpBase + TimeSpan.FromSeconds((now - _ntpBaseTicks) / (double)Stopwatch.Frequency);
        double s = (t - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        ulong whole = (ulong)s;
        return whole << 32 | (ulong)((s - whole) * 4294967296.0);
    }

    // The TV's packets come several to a datagram; only its frame reports and picture-lost notes matter here.
    void Heard(ReadOnlySpan<byte> d)
    {
        for (int at = 0; at + 4 <= d.Length;)
        {
            if (d[at] >> 6 != 2) return;
            int len = 4 + BinaryPrimitives.ReadUInt16BigEndian(d[(at + 2)..]) * 4;
            if (at + len > d.Length) return;
            var p = d.Slice(at, len);
            at += len;
            if (p[1] != 206 || p.Length < 12) continue;
            uint media = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
            var s = media == _video.Ssrc ? _video : media == _audio?.Ssrc ? _audio : null;
            if (s == null) continue;
            int kind = p[0] & 0x1F;
            if (kind == 1 && s.Video) PictureLost();
            else if (kind == 15 && p.Length >= 20 && BinaryPrimitives.ReadUInt32BigEndian(p[12..]) == Cast) Frames(s, p[16..]);
        }
    }

    // A frame report: every frame up to the checkpoint arrived (those are let go), then the packets it's missing.
    void Frames(Track s, ReadOnlySpan<byte> f)
    {
        if (s.Next == 0) return;
        uint newest = s.Next - 1;
        long checkpoint = newest - (long)(byte)(newest - f[0]); // the TV sends only the id's low 8 bits
        int losses = f[1];
        if (s.Video) _tvDelay = BinaryPrimitives.ReadUInt16BigEndian(f[2..]);
        if (checkpoint > s.Acked)
        {
            s.Acked = checkpoint;
            while (s.Kept.First is { } k && k.Value.Id <= checkpoint) s.Kept.RemoveFirst();
            if (s.Video && checkpoint >= 0) _heard.TrySetResult();
        }
        long now = Stopwatch.GetTimestamp();
        for (int i = 0; i < losses && 4 + i * 4 + 4 <= f.Length; i++)
        {
            var l = f.Slice(4 + i * 4, 4);
            byte id = l[0];
            var sent = s.Kept.FirstOrDefault(k => (byte)k.Id == id);
            if (sent == null) continue;
            ushort first = BinaryPrimitives.ReadUInt16BigEndian(l[1..]);
            if (first == 0xFFFF) { for (int j = 0; j < sent.Packets.Length; j++) Resend(sent, j, now); continue; }
            Resend(sent, first, now);
            for (int b = 0; b < 8; b++) if ((l[3] >> b & 1) != 0) Resend(sent, first + b + 1, now);
        }
    }

    void Resend(Sent sent, int i, long now)
    {
        if (i >= sent.Packets.Length || now - sent.Resent[i] < ResendGap) return;
        sent.Resent[i] = now;
        _resent++;
        Put(sent.Packets[i]);
    }

    // The TV lost its picture: a fresh keyframe, unless one just went.
    void PictureLost()
    {
        if (Stopwatch.GetTimestamp() - _lastKey < KeyGrace) return;
        _keysAsked++;
        _askKey();
    }

    // The 10-second line for the log; the counts start over.
    public string TakeReport()
    {
        lock (_gate)
        {
            var line = $"mirror: sent {_frames} pictures, the TV has up to {_video.Acked}" + (_audio is { } a ? $" (sound up to {a.Acked} of {a.Next})" : "") + $", resent {_resent} packets, " +
                       $"{_keysAsked} fresh pictures asked, TV delay {_tvDelay} ms, waiting {Interlocked.Read(ref _queued) / 1024} KB" +
                       (_sendMisses > 0 ? $", {Interlocked.Exchange(ref _sendMisses, 0)} packets Windows couldn't send" : "") +
                       (_dropped > 0 ? $", {_dropped} pictures dropped while the TV caught up" : "") +
                       (_held > 0 ? $", held the picture {_held} ticks while the TV caught up" : "");
            _frames = _resent = _keysAsked = _dropped = _held = 0;
            return line;
        }
    }
}
