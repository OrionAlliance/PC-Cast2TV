using System.Buffers.Binary;
using System.Text;

namespace PCCast2TV.Helpers;

public sealed partial class CastPlayer
{
    // ---- the Cast message: a tiny protobuf, sent with a 4-byte length in front ----

    static byte[] Encode(string ns, string destination, string json)
    {
        var m = new List<byte> { 0x08, 0x00 };                    // protocol_version = CASTV2_1_0
        void Str(int field, string v)
        {
            var b = Encoding.UTF8.GetBytes(v);
            m.Add((byte)(field << 3 | 2));
            for (int n = b.Length; ; n >>= 7) { if (n < 0x80) { m.Add((byte)n); break; } m.Add((byte)(n | 0x80)); }
            m.AddRange(b);
        }
        Str(2, "sender-0");
        Str(3, destination);
        Str(4, ns);
        m.Add(0x28); m.Add(0x00);                                 // payload_type = STRING
        Str(6, json);
        var framed = new byte[4 + m.Count];
        BinaryPrimitives.WriteInt32BigEndian(framed, m.Count);
        m.CopyTo(framed, 4);
        return framed;
    }

    static (string Ns, string Payload) Decode(byte[] b)
    {
        int i = 0;
        string ns = null, payload = null;
        while (i < b.Length)
        {
            int key = ReadVarint(b, ref i), field = key >> 3, wire = key & 7;
            if (wire == 0) ReadVarint(b, ref i);
            else if (wire == 2)
            {
                int len = ReadVarint(b, ref i);
                if (len < 0 || i + len > b.Length) return (null, null);
                if (field == 4) ns = Encoding.UTF8.GetString(b, i, len);
                if (field == 6) payload = Encoding.UTF8.GetString(b, i, len);
                i += len;
            }
            else return (null, null);
        }
        return (ns, payload);
    }

    static int ReadVarint(byte[] b, ref int i)
    {
        int v = 0;
        for (int shift = 0; i < b.Length && shift < 32; shift += 7)
        {
            byte x = b[i++];
            v |= (x & 0x7F) << shift;
            if (x < 0x80) return v;
        }
        return -1;
    }
}
