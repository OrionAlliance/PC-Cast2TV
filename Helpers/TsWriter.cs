using System.IO;
using System.Text;

namespace PCCast2TV.Helpers;

// Puts the H.264 frames and AAC frames into an MPEG transport stream, the format TVs and Rokus play: for a Roku,
// 1-second pieces that each start with a keyframe plus the live list of the newest 4; for a DLNA TV, one endless stream.
// Pieces stay in memory; the server hands them out from here.
public sealed class TsWriter
{
    const int PmtPid = 0x1000, VideoPid = 0x100, AudioPid = 0x101, ListSize = 4, KeptBehind = 3;
    const long Start = 126_000;        // timestamps start 1.4 s in
    const long PcrLead = 9_000;        // the clock runs 0.1 s ahead of the pictures it carries

    // What the server hands out, swapped whole at each finished piece so readers never wait.
    sealed record Served(string Playlist, Dictionary<int, byte[]> Pieces, int Newest, int Listed);

    readonly OutputKind _kind;
    readonly bool _sound;
    readonly object _gate = new();
    readonly Dictionary<int, int> _cc = new();
    readonly MemoryStream _out = new(), _piece = new();
    readonly Queue<(byte[] Frame, long Pts)> _audioAhead = new();
    readonly List<(int Index, double Seconds)> _list = new();
    volatile Served _served = new(null, new(), -1, 0);
    int _index = -1;
    long _pieceStart = -1, _lastVideo = long.MinValue, _bytes;

    public event Action<byte[], int> Live; // the endless stream's bytes as they're made
    public long Bytes => Interlocked.Read(ref _bytes); // live bytes sent out, or finished pieces' bytes

    public string Playlist => _served.Playlist;
    public byte[] Piece(int index) => _served.Pieces.GetValueOrDefault(index);
    public int? Newest => _served.Newest < 0 ? null : _served.Newest;
    public int Listed => _served.Listed;

    public TsWriter(OutputKind kind, bool sound) => (_kind, _sound) = (kind, sound);

    public void AddVideo(byte[] frame, long pts, bool key)
    {
        lock (_gate)
        {
            if (key)
            {
                if (_kind == OutputKind.Hls && (_pieceStart < 0 || pts - _pieceStart >= 81_000)) NextPiece(pts); // 0.9 s or more in
                else if (_kind == OutputKind.LiveTs) Tables();
            }
            if (_kind == OutputKind.Hls && _index < 0) return; // waiting for the first keyframe
            while (_audioAhead.Count > 0 && _audioAhead.Peek().Pts <= pts) Audio(_audioAhead.Dequeue());
            Pes(VideoPid, 0xE0, frame, Start + pts, key, Start + pts - PcrLead);
            _lastVideo = pts;
            Flush();
        }
    }

    // Sound comes a little after the picture it goes with; sound that's ahead waits for its picture.
    public void AddAudio(byte[] frame, long pts)
    {
        lock (_gate)
        {
            if (_lastVideo == long.MinValue) return; // nothing to go with yet
            if (pts <= _lastVideo) { Audio((frame, pts)); Flush(); }
            else
            {
                _audioAhead.Enqueue((frame, pts));
                while (_audioAhead.Count > 200) Audio(_audioAhead.Dequeue()); // the picture stalled: don't hold sound forever
            }
        }
    }

    void Audio((byte[] Frame, long Pts) a) => Pes(AudioPid, 0xC0, a.Frame, Start + a.Pts, false, null);

    // Finishes the piece that ends here, lists it, and starts the next one.
    void NextPiece(long pts)
    {
        if (_index >= 0)
        {
            Flush();
            var bytes = _piece.ToArray();
            _piece.SetLength(0);
            Interlocked.Add(ref _bytes, bytes.Length); // a piece counts as made once it's finished
            _list.Add((_index, (pts - _pieceStart) / 90_000.0));
            if (_list.Count > ListSize) _list.RemoveAt(0);
            // A player may still be reading a piece just off the list, so the last few stay too.
            var pieces = _served.Pieces.Where(p => p.Key >= _list[0].Index - KeptBehind).ToDictionary();
            pieces[_index] = bytes;
            _served = new Served(ListText(), pieces, _index, _list.Count);
        }
        _index++;
        _pieceStart = pts;
        Tables();
    }

    static string Name(int i) => $"seg_{i:00000}.ts";

    string ListText()
    {
        var s = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n");
        int target = Math.Max(1, (int)Math.Round(_list.Max(p => p.Seconds), MidpointRounding.AwayFromZero));
        s.Append($"#EXT-X-TARGETDURATION:{target}\n#EXT-X-MEDIA-SEQUENCE:{_list[0].Index}\n");
        foreach (var (i, sec) in _list) s.Append($"#EXTINF:{sec.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)},\n{Name(i)}\n");
        return s.ToString();
    }

    // The table of contents every piece (and the live stream at each keyframe) starts with: program list, then streams.
    void Tables()
    {
        Section(0, new byte[] { 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, (byte)(0xE0 | PmtPid >> 8), PmtPid & 0xFF }, 0x00);
        var pmt = new List<byte> { 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE0 | VideoPid >> 8, VideoPid & 0xFF, 0xF0, 0x00,
                                   0x1B, 0xE0 | VideoPid >> 8, VideoPid & 0xFF, 0xF0, 0x00 };
        if (_sound) pmt.AddRange(new byte[] { 0x0F, 0xE0 | AudioPid >> 8, AudioPid & 0xFF, 0xF0, 0x00 });
        Section(PmtPid, pmt.ToArray(), 0x02);
    }

    void Section(int pid, byte[] body, byte table)
    {
        int len = body.Length + 4; // plus the CRC
        var sec = new byte[3 + len];
        sec[0] = table;
        sec[1] = (byte)(0xB0 | len >> 8);
        sec[2] = (byte)len;
        body.CopyTo(sec, 3);
        uint crc = Crc32(sec.AsSpan(0, 3 + body.Length));
        sec[^4] = (byte)(crc >> 24); sec[^3] = (byte)(crc >> 16); sec[^2] = (byte)(crc >> 8); sec[^1] = (byte)crc;
        var p = new byte[188];
        p[0] = 0x47;
        p[1] = (byte)(0x40 | pid >> 8);
        p[2] = (byte)pid;
        p[3] = (byte)(0x10 | NextCc(pid));
        p[4] = 0; // pointer field
        sec.CopyTo(p, 5);
        p.AsSpan(5 + sec.Length).Fill(0xFF);
        _out.Write(p);
    }

    // One frame as a PES packet cut into 188-byte transport packets.
    void Pes(int pid, byte streamId, byte[] payload, long pts, bool key, long? pcr)
    {
        var h = new byte[14];
        h[2] = 1;
        h[3] = streamId;
        int pesLen = pid == AudioPid && payload.Length + 8 <= 0xFFFF ? payload.Length + 8 : 0; // video: unbounded
        h[4] = (byte)(pesLen >> 8);
        h[5] = (byte)pesLen;
        h[6] = 0x80;
        h[7] = 0x80; // PTS only (no reordering, so it's also the decode time)
        h[8] = 5;
        h[9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        h[10] = (byte)(pts >> 22);
        h[11] = (byte)(((pts >> 14) & 0xFE) | 1);
        h[12] = (byte)(pts >> 7);
        h[13] = (byte)(((pts << 1) & 0xFE) | 1);

        // The header, then the frame, cut into packets straight from both (no joined copy).
        int total = h.Length + payload.Length, pos = 0;
        bool first = true;
        var p = new byte[188];
        while (pos < total)
        {
            bool flagged = first && (key || pcr.HasValue);
            int af = flagged ? 2 + (pcr.HasValue ? 6 : 0) : 0; // adaptation field: length byte + flags (+ clock)
            int left = total - pos;
            if (left < 184 - af) af = 184 - left;              // the last packet: the rest is padding
            int n = 184 - af;
            p[0] = 0x47;
            p[1] = (byte)((first ? 0x40 : 0) | pid >> 8);
            p[2] = (byte)pid;
            p[3] = (byte)((af > 0 ? 0x30 : 0x10) | NextCc(pid));
            int i = 4;
            if (af > 0)
            {
                p[i++] = (byte)(af - 1);
                if (af > 1)
                {
                    p[i++] = (byte)((flagged && key ? 0x40 : 0) | (flagged && pcr.HasValue ? 0x10 : 0));
                    if (flagged && pcr.HasValue)
                    {
                        long b = pcr.Value;
                        p[i++] = (byte)(b >> 25); p[i++] = (byte)(b >> 17); p[i++] = (byte)(b >> 9); p[i++] = (byte)(b >> 1);
                        p[i++] = (byte)(((b & 1) << 7) | 0x7E); p[i++] = 0;
                    }
                    while (i < 4 + af) p[i++] = 0xFF;
                }
            }
            int fromHead = Math.Clamp(h.Length - pos, 0, n);
            if (fromHead > 0) Buffer.BlockCopy(h, pos, p, 4 + af, fromHead);
            if (n > fromHead) Buffer.BlockCopy(payload, pos + fromHead - h.Length, p, 4 + af + fromHead, n - fromHead);
            _out.Write(p);
            pos += n;
            first = false;
        }
    }

    int NextCc(int pid)
    {
        _cc.TryGetValue(pid, out int c);
        _cc[pid] = (c + 1) & 0x0F;
        return c;
    }

    void Flush()
    {
        if (_out.Length == 0) return;
        var buf = _out.GetBuffer();
        int n = (int)_out.Length;
        if (_kind == OutputKind.Hls) _piece.Write(buf, 0, n);
        else
        {
            Live?.Invoke(buf, n);
            Interlocked.Add(ref _bytes, n);
        }
        _out.SetLength(0);
    }

    // MPEG-2 CRC-32 (polynomial 0x04C11DB7, no reflection).
    static uint Crc32(ReadOnlySpan<byte> d)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in d)
        {
            crc ^= (uint)x << 24;
            for (int k = 0; k < 8; k++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    public void Close()
    {
        lock (_gate) Flush();
    }
}
