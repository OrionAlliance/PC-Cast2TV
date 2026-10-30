using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

namespace PCCast2TV.Helpers;

// Mirroring's way back: the TV's frame reports, picture-lost notes and round trip, our sender reports, the 10-second line.
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
                    lock (_gate)
                    {
                        long t = Stopwatch.GetTimestamp();
                        if (_heardAt > 0) _quiet = Math.Max(_quiet, t - _heardAt);
                        _heardAt = t;
                        Heard(buf.AsSpan(0, n));
                    }
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

    // The TV's packets come several to a datagram; its frame reports, picture-lost notes and receiver reports matter here.
    void Heard(ReadOnlySpan<byte> d)
    {
        for (int at = 0; at + 4 <= d.Length;)
        {
            if (d[at] >> 6 != 2) return;
            int len = 4 + BinaryPrimitives.ReadUInt16BigEndian(d[(at + 2)..]) * 4;
            if (at + len > d.Length) return;
            var p = d.Slice(at, len);
            at += len;
            if (p[1] == 201) RoundTrip(p);
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
        long now = Stopwatch.GetTimestamp();
        uint newest = s.Next - 1;
        long checkpoint = newest - (long)(byte)(newest - f[0]); // the TV sends only the id's low 8 bits
        int losses = f[1];
        if (s.Video) _tvDelay = BinaryPrimitives.ReadUInt16BigEndian(f[2..]);
        if (checkpoint > s.Acked)
        {
            s.Acked = checkpoint;
            // One missing piece holds back every later picture, so a slow batch counts as waiting on re-sends if any picture in it needed one.
            long slow = Stopwatch.Frequency * (_tvDelay > 0 ? _tvDelay : DelayMs) / 2000;
            int slowHere = 0, bytes = 0;
            bool resent = false;
            long firstSent = 0;
            while (s.Kept.First is { } k && k.Value.Id <= checkpoint)
            {
                if (s.Video)
                {
                    bytes += k.Value.Bytes;
                    if (k.Value.FirstSent > 0) firstSent = k.Value.FirstSent;
                    _confirms.Add(now - k.Value.At);
                    if (now - k.Value.At > slow && slowHere++ == 0) Stall(k.Value.At);
                    int most = k.Value.Tries.Max();
                    resent |= most > 1;
                    _mostTries = Math.Max(_mostTries, most);
                }
                DropFirst(s);
            }
            _slow += slowHere;
            if (resent) _slowOnResend += slowHere;
            if (s.Video && firstSent > 0) Control(firstSent, now, bytes);
            if (s.Video && checkpoint >= 0) _heard.TrySetResult();
        }
        long tooRecent = now - _rtt; // sent after this, it may still be on its way: Chrome doesn't send it again
        for (int i = 0; i < losses && 4 + i * 4 + 4 <= f.Length; i++)
        {
            var l = f.Slice(4 + i * 4, 4);
            byte id = l[0];
            var sent = s.Kept.FirstOrDefault(k => (byte)k.Id == id);
            if (sent == null) continue;
            ushort first = BinaryPrimitives.ReadUInt16BigEndian(l[1..]);
            if (first == 0xFFFF) { for (int j = 0; j < sent.Packets.Length; j++) Resend(sent, j, tooRecent); continue; }
            Resend(sent, first, tooRecent);
            for (int b = 0; b < 8; b++) if ((l[3] >> b & 1) != 0) Resend(sent, first + b + 1, tooRecent);
        }
    }

    // A stall (pictures confirmed past half the TV's cushion): any keyframe in the quarter second before, and the biggest picture then.
    void Stall(long at)
    {
        _stalls++;
        bool key = false;
        foreach (var r in _recent)
            if (r.At <= at && at - r.At <= Stopwatch.Frequency / 4)
            {
                key |= r.Key;
                _bigBeforeStall = Math.Max(_bigBeforeStall, r.Bytes);
            }
        if (key) _stallsNearKey++;
    }

    // Marks a missing packet to send again, unless it's already waiting or went out too recently to have arrived.
    void Resend(Sent sent, int i, long tooRecent)
    {
        if (i >= sent.Packets.Length || sent.Need[i] || sent.SentAt[i] > tooRecent) return;
        sent.Need[i] = true;
        sent.Pending++;
        _queued += sent.Packets[i].Length;
        _resent++;
        Monitor.PulseAll(_gate);
    }

    // Round trip from the TV's receiver report (time since our report, less its wait), smoothed fast up, slow down like Chrome.
    void RoundTrip(ReadOnlySpan<byte> p)
    {
        for (int b = 8, n = p[0] & 0x1F; n > 0 && b + 24 <= p.Length; n--, b += 24)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(p[b..]) != _video.Ssrc) continue;
            uint lsr = BinaryPrimitives.ReadUInt32BigEndian(p[(b + 16)..]), dlsr = BinaryPrimitives.ReadUInt32BigEndian(p[(b + 20)..]);
            if (lsr == 0) return;
            int units = (int)((uint)(Ntp(Stopwatch.GetTimestamp()) >> 16) - lsr - dlsr); // 1/65536 s
            long rtt = Math.Clamp(units * Stopwatch.Frequency / 65536, Stopwatch.Frequency / 13_000, Stopwatch.Frequency * DelayMs / 1000);
            _rtt = _rtt == 0 ? rtt : rtt > _rtt ? (_rtt + rtt) / 2 : (7 * _rtt + rtt) / 8;
            return;
        }
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
                       (_rtt > 0 ? $", round trip {_rtt * 1000.0 / Stopwatch.Frequency:0.0} ms" : ", no round trip reported");
            if (_confirms.Count > 0)
            {
                _confirms.Sort();
                line += $", the TV confirmed pictures in {_confirms[_confirms.Count / 2] * 1000 / Stopwatch.Frequency} ms typical, {_confirms[^1] * 1000 / Stopwatch.Frequency} ms slowest";
                _confirms.Clear();
            }
            long at = Stopwatch.GetTimestamp();
            double seconds = Math.Max(0.001, (at - _reportAt) / (double)Stopwatch.Frequency);
            line += $"\nslow confirms (over {(_tvDelay > 0 ? _tvDelay : DelayMs) / 2} ms): {_slow} in {_stalls} stall{(_stalls == 1 ? "" : "s")}" +
                    (_stalls > 0 ? $" ({_stallsNearKey} within a quarter second of a keyframe, biggest picture just before {_bigBeforeStall / 1024} KB)" : "") +
                    $", {_slowOnResend} of them waiting on re-sent pieces, " +
                    $"a piece took up to {_mostTries} tries, the TV's longest quiet {_quiet * 1000 / Stopwatch.Frequency} ms" +
                    $"\npictures: biggest {_biggest / 1024} KB, {_keys} keyframe{(_keys == 1 ? "" : "s")}" + (_keys > 0 ? $" at {_keyBytes / _keys / 1024} KB on average" : "") +
                    $", video {_videoBytes * 8 / seconds / 1e6:0.0} Mbps" + ControllerReport();
            _reportAt = at;
            _frames = _resent = _keysAsked = _dropped = _slow = _slowOnResend = _mostTries = _biggest = _keys = _stalls = _stallsNearKey = _bigBeforeStall = 0;
            _quiet = _videoBytes = _keyBytes = 0;
            return line;
        }
    }
}
