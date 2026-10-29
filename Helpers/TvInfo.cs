using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace PCCast2TV.Helpers;

// One log line per cast naming the TV (maker, model, software) so a one-kind problem shows; no serials, they identify it.
public static class TvInfo
{
    const string ModelUnknown = "model unknown until the next Detect";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 1_000_000 };

    public static async Task LogAsync(Device d)
    {
        try { Log.Write("tv: " + await DescribeAsync(d)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or JsonException)
        {
            Log.Write($"tv: {Log.Outside(d.Model) ?? ModelUnknown}, software not reported ({ex.GetType().Name})");
        }
    }

    // The device's own connection to the network: "WI-FI", "WIRED", or null when it doesn't say (DLNA TVs) or doesn't answer.
    public static async Task<string> LinkAsync(Device d)
    {
        try
        {
            if (d.IsRoku) return RokuLink(await RokuInfoAsync(d));
            if (d.Kind == DeviceKind.Cast) return CastLink(await CastInfoAsync(d));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or JsonException)
        {
            Log.Write($"tv: {d.LogName} didn't say how it's connected ({ex.GetType().Name})");
        }
        return null;
    }

    // Rokus are asked each time (their software updates); Chromecasts report their software, DLNA TVs only their model from Detect.
    static async Task<string> DescribeAsync(Device d)
    {
        if (d.IsRoku)
        {
            var x = await RokuInfoAsync(d);
            string g(string n) => Log.Outside(x.Root?.Element(n)?.Value?.Trim() ?? "");
            return $"{ModelText(g("vendor-name"), g("model-name"), g("model-number"))}, Roku OS {g("software-version")} build {g("software-build")}" +
                   $", {RokuLink(x) ?? "connection not reported"}";
        }
        if (d.Kind == DeviceKind.Cast)
        {
            var e = await CastInfoAsync(d);
            var rev = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("cast_build_revision", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return $"{Log.Outside(d.Model) ?? ModelUnknown}, Cast software {Log.Outside(rev) ?? "not reported"}, {CastLink(e) ?? "connection not reported"}";
        }
        return $"{Log.Outside(d.Model) ?? ModelUnknown}, software and connection not reported (DLNA TVs don't say)";
    }

    static async Task<XDocument> RokuInfoAsync(Device d) => XDocument.Parse(await Http.GetStringAsync($"http://{d.Ip}:8060/query/device-info"));

    static async Task<JsonElement> CastInfoAsync(Device d)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://{d.Ip}:8008/setup/eureka_info?params=build_info"));
        return doc.RootElement.Clone();
    }

    static string RokuLink(XDocument x) => x.Root?.Element("network-type")?.Value?.Trim() switch { "wifi" => "WI-FI", "ethernet" => "WIRED", _ => null };

    static string CastLink(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("ethernet_connected", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False ? (w.GetBoolean() ? "WIRED" : "WI-FI") : null;

    // Maker, model name and number in one line, the maker left off when the name already starts with it.
    public static string ModelText(string maker, string model, string number)
    {
        maker = maker?.Trim() ?? "";
        model = model?.Trim() ?? "";
        if (maker.Length > 0 && model.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) maker = "";
        var text = string.Join(" ", new[] { maker, model, number?.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
        return text.Length > 0 ? text : null;
    }
}
