namespace PCCast2TV.Helpers;

// The H.264 frame as a TV wants it in a stream: an access unit delimiter first, and the SPS/PPS in front of every keyframe.
static class Annex
{
    static readonly byte[] Aud = { 0, 0, 0, 1, 0x09, 0xF0 };

    // The finished frame in one new array, whatever had to go in front of it.
    public static byte[] Finish(ReadOnlySpan<byte> frame, bool key, ref byte[] header)
    {
        bool hasAud = false, hasSps = false;
        int spsLength = 0;
        var units = Units(frame);
        foreach (var (start, end, type) in units)
        {
            if (type == 9) hasAud = true;
            if (type == 7) hasSps = true;
            if (type is 7 or 8) spsLength += end - start;
            if (type == 5) key = true;
        }
        if (hasSps) // keep the newest SPS/PPS for keyframes that come without them
        {
            header = new byte[spsLength];
            int at = 0;
            foreach (var (start, end, type) in units)
                if (type is 7 or 8) { frame[start..end].CopyTo(header.AsSpan(at)); at += end - start; }
        }
        int aud = hasAud ? 0 : Aud.Length, head = key && !hasSps ? header.Length : 0;
        var o = new byte[aud + head + frame.Length];
        if (aud > 0) Aud.CopyTo(o, 0);
        if (head > 0) header.CopyTo(o, aud);
        frame.CopyTo(o.AsSpan(aud + head));
        return o;
    }

    // The format name a player sets itself up with ("avc1.64002A" = High 4.2), read from the frame's SPS; null without one.
    public static string CodecOf(ReadOnlySpan<byte> frame)
    {
        foreach (var (start, end, type) in Units(frame))
        {
            if (type != 7) continue;
            int header = start + (frame[start + 2] == 1 ? 3 : 4); // the unit's own first byte, after its start code
            if (header + 3 < end) return $"avc1.{frame[header + 1]:X2}{frame[header + 2]:X2}{frame[header + 3]:X2}";
        }
        return null;
    }

    // Each unit from its start code to the next: (start of its start code, end, unit type).
    static List<(int Start, int End, int Type)> Units(ReadOnlySpan<byte> b)
    {
        var units = new List<(int, int, int)>();
        int prev = -1, prevType = 0;
        for (int i = 0; i + 3 < b.Length; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0) continue;
            int sc = b[i + 2] == 1 ? 3 : (b[i + 2] == 0 && b[i + 3] == 1 ? 4 : 0);
            if (sc == 0) continue;
            if (prev >= 0) units.Add((prev, i, prevType));
            prev = i;
            prevType = i + sc < b.Length ? b[i + sc] & 0x1F : 0;
            i += sc - 1;
        }
        if (prev >= 0) units.Add((prev, b.Length, prevType));
        return units;
    }
}
