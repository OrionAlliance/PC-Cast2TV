using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PCCast2TV.Helpers;

// The home network's name as Windows shows it: the Wi-Fi name on Wi-Fi, "Network 4" style on a cable.
// A VPN never counts. Reading it is local only; nothing goes out on the network.
public static class NetworkName
{
    static readonly Guid ListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    // This PC's side: how it reaches the device ("WI-FI" or "WIRED", null with no network) and the label for the log.
    // towardIp is the device being cast to, when known, so a PC on both Wi-Fi and a cable names the one actually used.
    public static (string Kind, string Label) Describe(string towardIp)
    {
        var nic = PickAdapter(towardIp);
        if (nic == null) return (null, "NO NETWORK");
        var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "WI-FI" : "WIRED";
        return (kind, $"{kind} · {(NameOf(nic) ?? nic.Name).ToUpperInvariant()}");
    }

    static NetworkInterface PickAdapter(string towardIp)
    {
        var home = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && !NetUtil.IsVpn(n))
            .Where(n => IPv4(n).Any()).ToList();
        if (towardIp != null)
        {
            var local = NetUtil.LocalIpToward(towardIp);
            var used = home.FirstOrDefault(n => IPv4(n).Contains(local));
            if (used != null) return used;
        }
        return home.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && g.Address.ToString() != "0.0.0.0"))
               ?? home.FirstOrDefault();
    }

    static IEnumerable<string> IPv4(NetworkInterface n) => n.GetIPProperties().UnicastAddresses
        .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(u => u.Address.ToString());

    // True when Windows calls the network that reaches the device Public: its firewall then keeps TVs out, because the
    // app's rule lets them in on home (Private) networks only.
    public static bool IsPublic(string towardIp)
    {
        var nic = PickAdapter(towardIp);
        try { return nic != null && NetworkOf(nic)?.GetCategory() == 0; }
        catch (COMException ex) { Log.Write("network type: Windows' network list failed: " + ex.Message); }
        return false;
    }

    static string NameOf(NetworkInterface nic)
    {
        try { return NetworkOf(nic)?.GetName(); }
        catch (COMException ex) { Log.Write("network name: Windows' network list failed: " + ex.Message); }
        return null;
    }

    // Windows' own network list (Network List Manager), matched to the adapter by its ID.
    static INetwork NetworkOf(NetworkInterface nic)
    {
        if (!Guid.TryParse(nic.Id, out var id)) return null;
        var nlm = (INetworkListManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ListManager));
        var list = nlm.GetNetworkConnections();
        while (list.Next(1, out var conn, out var got) == 0 && got == 1)
            if (conn.GetAdapterId() == id) return conn.GetNetwork();
        return null;
    }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object NewEnum();
        [PreserveSig] int Next(uint count, out INetworkConnection conn, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    // Listed in Windows' own order up to GetCategory, since COM finds each one by its place in that order.
    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        int GetCategory(); // 0 Public, 1 Private, 2 work domain
    }
}
