using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCCast2TV.Helpers;

public static class NetUtil
{
    // Names VPN apps give their adapters. Hyper-V and other virtual LAN bridges are left alone, since they can be the real home network.
    static readonly string[] VpnWords =
        { "vpn", "wireguard", "wintun", "tap-windows", "openvpn", "tailscale", "zerotier", "nordlynx", "hamachi", "anyconnect", "globalprotect", "fortinet" };

    // True for a VPN adapter: a tunnel or dial-up type, Windows' "virtual" type 53 (WireGuard uses it), or a VPN app's name.
    public static bool IsVpn(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp || (int)n.NetworkInterfaceType == 53
        || VpnWords.Any(w => (n.Name + " " + n.Description).Contains(w, StringComparison.OrdinalIgnoreCase));

    // Sends the packets out one home adapter to a multicast group, then hands every answer in the next 3 seconds to onAnswer.
    public static async Task AskAsync(IPAddress local, IPEndPoint group, IEnumerable<byte[]> packets, string what, Action<UdpReceiveResult> onAnswer)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            foreach (var p in packets) await udp.SendAsync(p, group);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { while (true) onAnswer(await udp.ReceiveAsync(cts.Token)); }
            catch (OperationCanceledException) { } // the 3 second listening window closed
        }
        catch (SocketException ex) { Log.Write($"detect: one adapter could not {what}: " + ex.Message); }
    }

    // The PC's own address toward the device, asked with a UDP connect that sends nothing (right even with several adapters).
    public static string LocalIpToward(string deviceIp)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(IPAddress.Parse(deviceIp), 9);
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception ex) when (ex is SocketException or FormatException)
        {
            Log.Write($"route: none toward {deviceIp}: {ex.Message}");
            return null;
        }
    }

    // True when that local address belongs to a VPN adapter, so the cast would travel through the VPN.
    public static bool IsOnVpn(string localIp) => NetworkInterface.GetAllNetworkInterfaces()
        .Where(IsVpn)
        .Any(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString() == localIp));

    // True when the device is on the same home network as the PC address reaching it, which catches any VPN that takes the route.
    public static bool SameNetwork(string localIp, string deviceIp)
    {
        if (!IPAddress.TryParse(localIp, out var local) || !IPAddress.TryParse(deviceIp, out var device)) return false;
        var mine = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(u => u.Address.Equals(local));
        if (mine == null || mine.PrefixLength is <= 0 or > 32) return true; // can't tell, so don't stand in the way
        uint mask = uint.MaxValue << (32 - mine.PrefixLength);
        return (ToUInt(local) & mask) == (ToUInt(device) & mask);
    }

    static uint ToUInt(IPAddress a) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes());

    // True when any VPN is connected, for the hints shown only when nothing answers.
    public static bool AnyVpnUp => NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && IsVpn(n));
}
