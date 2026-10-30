using System.Net.Sockets;
using System.Text;

namespace PCCast2TV.Helpers;

// Reading a player's request: its method, path, query and whether it keeps the connection open.
public sealed partial class CastServer
{
    sealed record Request(string Method, string Path, string Query, bool KeepAlive);

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
}
