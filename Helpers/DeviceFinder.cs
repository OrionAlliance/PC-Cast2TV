using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace PCCast2TV.Helpers;

public enum DeviceKind { Roku, RokuTv, Dlna, Cast }

// A device the app can cast to: Id is its own serial or UDN (it survives a new address), Model what Detect heard, for the log.
public sealed record Device(string Id, string Name, DeviceKind Kind, string Ip, string ControlUrl = null, string ServiceType = null, int Port = 0,
                            string Model = null)
{
    // Worked out from Kind, so they stay out of settings.json.
    [JsonIgnore] public bool IsRoku => Kind is DeviceKind.Roku or DeviceKind.RokuTv;
    [JsonIgnore] public string KindLabel => Kind switch { DeviceKind.Roku => "ROKU", DeviceKind.RokuTv => "ROKU TV", DeviceKind.Cast => "CHROMECAST", _ => "DLNA" };
    // The log calls it by kind and address, never the name people gave it: logs get posted in public Issues.
    [JsonIgnore] public string LogName => $"{KindLabel} at {Ip}";

    // Rokus take the 1-second HLS pieces (Chromecast-style TVs too, as mirroring's backup); DLNA TVs take one endless stream.
    [JsonIgnore] public bool TakesHls => Kind != DeviceKind.Dlna;
}

// One Detect: an SSDP search (Rokus, DLNA TVs) and an mDNS search (Chromecasts) together; only devices that can play are kept.
public static class DeviceFinder
{
    const string RokuSt = "roku:ecp";
    const string RendererSt = "urn:schemas-upnp-org:device:MediaRenderer:1";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 1_000_000 };

    public static async Task<List<Device>> DetectAsync(IReadOnlyList<Device> saved)
    {
        var adapters = LocalAddresses();
        var ssdp = SearchAsync(adapters);
        var cast = CastFinder.SearchAsync(adapters);
        await Task.WhenAll(ssdp, cast);
        var answers = ssdp.Result;
        var found = (await Task.WhenAll(answers.Select(a => IdentifyAsync(a.Location, a.St)))).Where(d => d != null).Concat(cast.Result);
        // A device answering more than one way keeps its best way to play: Roku, then Chromecast, then DLNA.
        var best = found.GroupBy(d => d.Ip).Select(g => g.OrderBy(d => d.IsRoku ? 0 : d.Kind == DeviceKind.Cast ? 1 : 2).First());
        var list = OnePerId(best, saved).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Log.Write($"detect: searched {adapters.Count} adapter(s), {answers.Count + cast.Result.Count} answered, {list.Count} can play: " +
                  string.Join(", ", list.Select(d => d.LogName)));
        return list;
    }

    // Two devices answering with one ID: a saved TV keeps its saved address, and a new ID claimed twice is left off.
    static IEnumerable<Device> OnePerId(IEnumerable<Device> found, IReadOnlyList<Device> saved) =>
        found.GroupBy(d => d.Id).SelectMany(g =>
        {
            if (g.Count() == 1) return g.ToList();
            var keep = g.Where(d => saved.Any(s => s.Id == d.Id && s.Ip == d.Ip)).Take(1).ToList();
            Log.Write($"detect: {string.Join(", ", g.Select(d => d.LogName))} answer with one ID, " +
                      (keep.Count > 0 ? $"kept the saved {keep[0].LogName}" : "left off the list"));
            return keep;
        });

    // The search goes out on each home adapter by name, never a VPN's, so a VPN can't carry it away from the home network.
    static async Task<List<(string Location, string St)>> SearchAsync(List<IPAddress> adapters)
    {
        var answers = new ConcurrentDictionary<string, string>();
        await Task.WhenAll(adapters.Select(a => SearchOnAsync(a, answers)));
        return answers.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    static List<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast && !NetUtil.IsVpn(n))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Distinct().ToList();

    static readonly IPEndPoint SsdpGroup = new(IPAddress.Parse("239.255.255.250"), 1900);

    // Each search sent twice in a row, Roku first.
    static readonly byte[][] Searches = new[] { RokuSt, RokuSt, RendererSt, RendererSt }
        .Select(st => Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n")).ToArray();

    static Task SearchOnAsync(IPAddress local, ConcurrentDictionary<string, string> answers) =>
        NetUtil.AskAsync(local, SsdpGroup, Searches, "search", res =>
        {
            var headers = Encoding.ASCII.GetString(res.Buffer).Split("\r\n")
                .Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
            if (!headers.TryGetValue("LOCATION", out var loc) || !headers.TryGetValue("ST", out var st)) return;
            if (!st.Equals(RokuSt, StringComparison.OrdinalIgnoreCase) && !st.Equals(RendererSt, StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(loc, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return;
            // An answer counts only for the device that sent it, on the network of the adapter that asked.
            if (uri.Host != res.RemoteEndPoint.Address.ToString() || !NetUtil.SameNetwork(local.ToString(), uri.Host))
            {
                Log.Write($"detect: an answer from {res.RemoteEndPoint.Address} named another address, left off the list");
                return;
            }
            answers.TryAdd(loc, st);
        });

    static async Task<Device> IdentifyAsync(string location, string st)
    {
        var host = new Uri(location).Host;
        try
        {
            return st.Equals(RokuSt, StringComparison.OrdinalIgnoreCase) ? await IdentifyRokuAsync(host) : await IdentifyRendererAsync(location, host);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or UriFormatException)
        {
            Log.Write($"detect: could not identify {host}: {ex.GetType().Name} {Log.Outside(ex.Message)}");
            return null;
        }
    }

    public static async Task<(string Serial, string Name, bool IsTv)> RokuInfoAsync(string ip, HttpClient http = null)
    {
        var x = XDocument.Parse(await (http ?? Http).GetStringAsync($"http://{ip}:8060/query/device-info"));
        string g(string n) => x.Root?.Element(n)?.Value?.Trim() ?? "";
        var name = new[] { g("user-device-name"), g("friendly-device-name"), g("friendly-model-name"), g("model-name") }
            .FirstOrDefault(s => s.Length > 0) ?? "Roku";
        var serial = g("serial-number").Length > 0 ? g("serial-number") : g("device-id");
        return (serial, name, g("is-tv") == "true");
    }

    static async Task<Device> IdentifyRokuAsync(string ip)
    {
        var (serial, name, isTv) = await RokuInfoAsync(ip);
        if (serial.Length == 0) serial = ip;
        return new Device("roku:" + serial, name, isTv ? DeviceKind.RokuTv : DeviceKind.Roku, ip);
    }

    // A renderer is only listed if it has an AVTransport service, the part that plays a link.
    static async Task<Device> IdentifyRendererAsync(string location, string ip)
    {
        var x = XDocument.Parse(await Http.GetStringAsync(location));
        var service = x.Descendants().Where(e => e.Name.LocalName == "service")
            .FirstOrDefault(s => (Child(s, "serviceType") ?? "").Contains(":AVTransport:", StringComparison.OrdinalIgnoreCase));
        if (service == null) return null;
        var device = service.Ancestors().FirstOrDefault(e => e.Name.LocalName == "device");
        if (device == null)
        {
            Log.Write($"detect: the DLNA device at {ip} lists its player outside any device, left off the list");
            return null;
        }
        var baseText = x.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value?.Trim();
        var baseUri = Uri.TryCreate(baseText, UriKind.Absolute, out var b) ? b : new Uri(location);
        var control = new Uri(baseUri, Child(service, "controlURL") ?? "");
        if (control.Host != ip || control.Scheme != Uri.UriSchemeHttp) // the TV's own play link
        {
            Log.Write($"detect: the DLNA device at {ip} names a control link elsewhere, left off the list");
            return null;
        }
        var name = Child(device, "friendlyName") ?? Child(device, "modelName") ?? "TV";
        var udn = Child(device, "UDN") ?? ip;
        return new Device("dlna:" + udn, name, DeviceKind.Dlna, ip, control.ToString(), Child(service, "serviceType"),
                          Model: TvInfo.ModelText(Child(device, "manufacturer"), Child(device, "modelName"), Child(device, "modelNumber")));
    }

    static string Child(XElement e, string name) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value?.Trim() is { Length: > 0 } v ? v : null;
}
