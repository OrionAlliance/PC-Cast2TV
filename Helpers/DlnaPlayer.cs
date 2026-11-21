using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace PCCast2TV.Helpers;

// Tells a DLNA TV to play the live stream and to stop, through its AVTransport service (the order and labels follow Jellyfin's DLNA casting).
public static class DlnaPlayer
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) // a device's answer can't send the app anywhere else
        { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 1_000_000 };
    const string Zero = "<InstanceID>0</InstanceID>";

    // A live stream, said honestly: converted on the fly (CI=1), no seeking (no OP), streamed (FLAGS: streaming, interactive, background, DLNA 1.5).
    public const string Features = "DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01D00000000000000000000000000000";

    public static async Task<bool> AnsweringAsync(Device d) => await TransportAsync(d) != null;

    // Returns null when the TV is playing, otherwise what went wrong (the caller puts the TV's name in front).
    public static async Task<string> PlayAsync(Device d, string url)
    {
        await LogWhatItPlaysAsync(d);
        await SoapAsync(d, "Stop", Zero); // a TV still playing something answers the stream with error 705 (transport locked)
        for (int i = 0; i < 30 && (await TransportAsync(d))?.State is "PLAYING" or "TRANSITIONING"; i++) await Task.Delay(100);
        if (await SoapAsync(d, "SetAVTransportURI", Handover(url, Didl(url))) != null) Log.Write("dlna: the TV took the stream");
        else if (await SoapAsync(d, "SetAVTransportURI", Handover(url, "")) != null) Log.Write("dlna: the TV took the stream on the second try, without its description");
        else return "REFUSED THE STREAM";
        await Task.Delay(50);
        if (await SoapAsync(d, "Play", Zero + "<Speed>1</Speed>") == null) Log.Write("dlna: the TV answered Play with an error, watching whether it plays anyway (some do)");
        var until = DateTime.UtcNow.AddSeconds(12); // a firm limit: a TV that goes quiet can't hold the start for minutes
        for (int quiet = 0; DateTime.UtcNow < until;)
        {
            await Task.Delay(500);
            var t = await TransportAsync(d);
            if (t?.State == "PLAYING") { Log.Write("dlna: playing"); return null; }
            quiet = t == null ? quiet + 1 : 0;
            if (quiet == 2) { Log.Write("dlna: the TV stopped answering while starting"); return "STOPPED ANSWERING"; }
        }
        Log.Write("dlna: never said playing, " + await StateLineAsync(d));
        return "DID NOT START PLAYING";
    }

    public static async Task StopAsync(Device d) => await SoapAsync(d, "Stop", Zero);

    // The TV closed the stream mid-cast on its own: what it says now, for the log ("STOPPED, status ERROR_OCCURRED").
    public static async Task LogWhyItStoppedAsync(Device d) => Log.Write("dlna: the TV closed the stream on its own, " + await StateLineAsync(d));

    static async Task<string> StateLineAsync(Device d) =>
        await TransportAsync(d) is { } t ? $"it says {Said(t.State)}, status {Said(t.Status)}" : "it didn't answer";

    // A word the TV sent, through the log's filter for device text (logs get posted in public).
    static string Said(string s) => string.IsNullOrEmpty(s = Log.Outside(s)) ? "not given" : s;

    // What the TV says it plays (its ConnectionManager's list), the video types and their DLNA profiles, so a report shows what it claims.
    static async Task LogWhatItPlaysAsync(Device d)
    {
        if (d.ConnectionUrl == null) { Log.Write("dlna: the TV's list of what it plays isn't known (DETECT DEVICE(S) reads it)"); return; }
        var body = await SoapAsync(d.ConnectionUrl, d.ConnectionType, "GetProtocolInfo", "");
        if (body == null) return; // SoapAsync logged why
        var entries = WebUtility.HtmlDecode(Regex.Match(body, "<Sink>([^<]*)</Sink>").Groups[1].Value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var video = entries.Select(e => e.Split(':', 4)).Where(p => p.Length == 4 && p[2].StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p[2].ToLowerInvariant())
            .Select(g =>
            {
                var kinds = g.Select(p => Log.Outside(Regex.Match(p[3], "DLNA.ORG_PN=([^;]+)").Groups[1].Value)).Where(k => k.Length > 0).Distinct().ToList();
                return Said(g.Key) + (kinds.Count == 0 ? "" : $" ({string.Join(", ", kinds.Take(8))}{(kinds.Count > 8 ? $", {kinds.Count - 8} more" : "")})");
            }).ToList();
        Log.Write($"dlna: the TV lists {entries.Length} types it plays" + (video.Count == 0 ? ", none of them video" : ": " + string.Join("; ", video)));
    }

    static string Handover(string url, string didl) =>
        $"{Zero}<CurrentURI>{WebUtility.HtmlEncode(url)}</CurrentURI><CurrentURIMetaData>{WebUtility.HtmlEncode(didl)}</CurrentURIMetaData>";

    // The stream's description; the sec namespace is the one Samsung TVs look for.
    static string Didl(string url) =>
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:dlna=\"urn:schemas-dlna-org:metadata-1-0/\" xmlns:sec=\"http://www.sec.co.kr/\">" +
        "<item id=\"0\" parentID=\"-1\" restricted=\"1\"><dc:title>PC Cast2TV</dc:title><upnp:class>object.item.videoItem</upnp:class>" +
        $"<res protocolInfo=\"http-get:*:video/mpeg:{Features}\">{WebUtility.HtmlEncode(url)}</res></item></DIDL-Lite>";

    // The TV's transport state (PLAYING, STOPPED...) and status (OK, ERROR_OCCURRED); null when it doesn't answer.
    static async Task<(string State, string Status)?> TransportAsync(Device d)
    {
        var body = await SoapAsync(d, "GetTransportInfo", Zero);
        if (body == null) return null;
        return (Regex.Match(body, "<CurrentTransportState>([^<]*)<").Groups[1].Value, Regex.Match(body, "<CurrentTransportStatus>([^<]*)<").Groups[1].Value);
    }

    static Task<string> SoapAsync(Device d, string action, string args) => SoapAsync(d.ControlUrl, d.ServiceType, action, args);

    static async Task<string> SoapAsync(string url, string serviceType, string action, string args)
    {
        var envelope = "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                       "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                       $"<u:{action} xmlns:u=\"{serviceType}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");
        try
        {
            using var r = await Http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
            {
                Log.Write($"dlna: {action} refused, answer {(int)r.StatusCode}{TvError(text)}");
                return null;
            }
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"dlna: {action} failed: {ex.GetType().Name}");
            return null;
        }
    }

    // The TV's own reason from its error answer: ", TV error 714 (Illegal MIME-type)".
    static string TvError(string text)
    {
        var code = Regex.Match(text, @"<errorCode>\s*(\d+)").Groups[1].Value;
        var why = Log.Outside(Regex.Match(text, "<errorDescription>([^<]*)<").Groups[1].Value.Trim()) ?? "";
        return code.Length == 0 ? "" : $", TV error {code}" + (why.Length == 0 ? "" : $" ({why})");
    }
}
