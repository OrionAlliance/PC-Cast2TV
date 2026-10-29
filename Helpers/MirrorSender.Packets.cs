using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PCCast2TV.Helpers;

// Mirroring's way out: each frame locked and cut into packets, then sent at a pace a TV's wired port can take.
public sealed partial class MirrorSender
{
    // AES-128 in counter mode into the track's buffer: the counter starts at the frame's id mixed with the track's mask.
    static void Lock(Track s, uint id, byte[] data)
    {
        int size = (data.Length + 15) / 16 * 16;
        if (s.Counters.Length < size) (s.Counters, s.Locked) = (new byte[size + size / 4], new byte[size + size / 4]);
        Span<byte> c = stackalloc byte[16];
        c.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(c[8..], id);
        for (int i = 0; i < 16; i++) c[i] ^= s.Mask[i];
        for (int b = 0; b < size; b += 16)
        {
            c.CopyTo(s.Counters.AsSpan(b));
            for (int i = 15; i >= 0 && ++c[i] == 0; i--) { }
        }
        s.Aes.EncryptEcb(s.Counters.AsSpan(0, size), s.Locked.AsSpan(0, size), PaddingMode.None);
        var locked = s.Locked;
        for (int i = 0; i < data.Length; i++) locked[i] ^= data[i];
    }

    // One packet: the usual 12-byte RTP header, then Cast's 7 (keyframe, frame id, packet id, last packet id, the frame it needs).
    static byte[] Packet(Track s, uint id, uint needs, bool key, uint rtp, int length, int i, int count)
    {
        int at = i * Room, n = i == count - 1 ? length - at : Room;
        var p = new byte[Header + n];
        p[0] = 0x80;
        p[1] = (byte)((i == count - 1 ? 0x80 : 0) | (s.Video ? VideoType : AudioType));
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), s.Seq++);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), rtp);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), s.Ssrc);
        p[12] = (byte)((key ? 0x80 : 0) | 0x40);
        p[13] = (byte)id;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(14), (ushort)i);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(16), (ushort)(count - 1));
        p[18] = (byte)needs;
        s.Locked.AsSpan(at, n).CopyTo(p.AsSpan(Header));
        return p;
    }

    // Sends the line out, paced, waiting 1 ms at a time when it's ahead.
    void SendLoop()
    {
        using var timer = new PreciseTimer();
        try
        {
            double room = BurstBytes;
            long last = Stopwatch.GetTimestamp();
            foreach (var p in _out.GetConsumingEnumerable(_stop.Token))
            {
                while (true)
                {
                    long now = Stopwatch.GetTimestamp();
                    room = Math.Min(BurstBytes, room + (now - last) * BytesPerSecond / Stopwatch.Frequency);
                    last = now;
                    if (room >= p.Length) break;
                    timer.Wait(0.001, 100);
                }
                room -= p.Length;
                Interlocked.Add(ref _queued, -p.Length);
                Send(p);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    // A packet Windows couldn't send (no room, or the network gone for a moment) is counted, and the TV asks again.
    void Send(byte[] p)
    {
        try
        {
            _sock.Send(p);
            Interlocked.Add(ref _sent, p.Length);
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref _sendMisses);
            var code = ex.SocketErrorCode;
            if (code is not (SocketError.NoBufferSpaceAvailable or SocketError.WouldBlock) && Interlocked.Exchange(ref _lastSendError, (int)code) != (int)code)
                Log.Write("cast: mirroring could not send (" + code + "), still trying");
        }
    }
}
