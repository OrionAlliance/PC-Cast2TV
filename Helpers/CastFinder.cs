using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;

namespace PCCast2TV.Helpers;

// Finds Chromecast-style screens with one mDNS question per home adapter; Cast speakers (no screen flag) stay off the list.
public static class CastFinder
{
    sealed record Rec(string Name, int Type, List<string> Texts, int Port);

    static readonly byte[] Query = BuildQuery();

    public static async Task<List<Device>> SearchAsync(IEnumerable<IPAddress> adapters)
    {
        var found = new ConcurrentDictionary<string, Device>();
        await Task.WhenAll(adapters.Select(a => SearchOnAsync(a, found)));
        return found.Values.ToList();
    }

    static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);

    static Task SearchOnAsync(IPAddress local, ConcurrentDictionary<string, Device> found) =>
        NetUtil.AskAsync(local, MdnsGroup, new[] { Query }, "ask for Cast devices", r =>
        {
            var from = r.RemoteEndPoint.Address.ToString();
            if (!NetUtil.SameNetwork(local.ToString(), from)) return; // only devices on the network of the adapter that asked
            var d = Read(r.Buffer, from);
            if (d != null) found.TryAdd($"{d.Id} at {d.Ip}", d); // two addresses with one ID both go on, for Detect to sort out
        });

    // A PTR question for _googlecast._tcp.local, with the "answer me directly" bit so replies come to this socket.
    static byte[] BuildQuery()
    {
        var q = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in "_googlecast._tcp.local".Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(Encoding.ASCII.GetBytes(label));
        }
        q.AddRange(new byte[] { 0, 0, 12, 0x80, 1 });
        return q.ToArray();
    }

    // One answer packet: TXT carries the name (fn), model (md), id and flags (ca), SRV the port, the sender the address.
    static Device Read(byte[] packet, string fromIp)
    {
        List<Rec> recs;
        try { recs = Parse(packet); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            Log.Write($"detect: a Cast answer from {fromIp} could not be read");
            return null;
        }
        var txt = recs.FirstOrDefault(r => r.Type == 16 && r.Name.EndsWith("._googlecast._tcp.local", StringComparison.OrdinalIgnoreCase));
        if (txt == null) return null;
        var kv = txt.Texts.Select(t => t.Split('=', 2)).Where(p => p.Length == 2)
                          .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        var model = kv.GetValueOrDefault("md") is { Length: > 0 } md ? md : null;
        if (!int.TryParse(kv.GetValueOrDefault("ca"), out var caps) || (caps & 1) == 0)
        {
            Log.Write($"detect: {Log.Outside(model) ?? "a Cast device"} at {fromIp} has no screen, left off the list"); // its model, never its given name
            return null;
        }
        var name = kv.GetValueOrDefault("fn") is { Length: > 0 } fn ? fn : model ?? "Chromecast";
        var srv = recs.FirstOrDefault(r => r.Type == 33 && r.Name == txt.Name);
        var id = kv.GetValueOrDefault("id") is { Length: > 0 } i ? i : fromIp;
        return new Device("cast:" + id, name, DeviceKind.Cast, fromIp, Port: srv?.Port ?? 8009, Model: model);
    }

    static List<Rec> Parse(byte[] p)
    {
        int qd = p[4] << 8 | p[5], records = (p[6] << 8 | p[7]) + (p[8] << 8 | p[9]) + (p[10] << 8 | p[11]), i = 12;
        for (int k = 0; k < qd; k++) { Name(p, ref i); i += 4; }
        var list = new List<Rec>();
        for (int k = 0; k < records; k++)
        {
            var name = Name(p, ref i);
            int type = p[i] << 8 | p[i + 1], len = p[i + 8] << 8 | p[i + 9];
            i += 10;
            int end = i + len;
            if (end > p.Length) throw new InvalidDataException("record runs past the packet");
            var texts = new List<string>();
            int port = 0;
            if (type == 16)
                for (int t = i; t < end; t += p[t] + 1) texts.Add(Encoding.UTF8.GetString(p, t + 1, p[t]));
            else if (type == 33) port = p[i + 4] << 8 | p[i + 5];
            list.Add(new Rec(name, type, texts, port));
            i = end;
        }
        return list;
    }

    // DNS names, following compression pointers (capped so a bad packet can't loop forever).
    static string Name(byte[] p, ref int i)
    {
        var parts = new List<string>();
        int? resume = null;
        for (int hops = 0; ; )
        {
            int l = p[i];
            if (l == 0) { i++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (++hops > 16) throw new InvalidDataException("name pointer loop");
                resume ??= i + 2;
                i = (l & 0x3F) << 8 | p[i + 1];
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(p, i + 1, l));
            i += l + 1;
        }
        if (resume != null) i = resume.Value;
        return string.Join(".", parts);
    }
}
