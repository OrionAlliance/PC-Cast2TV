using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace PCCast2TV.Helpers;

// Tells a DLNA TV to play the live stream and to stop, through its AVTransport service.
public static class DlnaPlayer
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<bool> AnsweringAsync(Device d) => await TransportStateAsync(d) != null;

    // Returns null when the TV is playing, otherwise what went wrong (the caller puts the TV's name in front).
    public static async Task<string> PlayAsync(Device d, string url)
    {
        var didl = "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                   "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"><item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
                   "<dc:title>PC Cast2TV</dc:title><upnp:class>object.item.videoItem</upnp:class>" +
                   "<res protocolInfo=\"http-get:*:video/mpeg:DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\">" +
                   WebUtility.HtmlEncode(url) + "</res></item></DIDL-Lite>";
        if (await SoapAsync(d, "SetAVTransportURI",
                $"<InstanceID>0</InstanceID><CurrentURI>{WebUtility.HtmlEncode(url)}</CurrentURI><CurrentURIMetaData>{WebUtility.HtmlEncode(didl)}</CurrentURIMetaData>") == null)
            return "REFUSED THE STREAM";
        if (await SoapAsync(d, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>") == null)
            return "WOULD NOT PLAY";
        for (int i = 0; i < 24; i++)
        {
            await Task.Delay(500);
            var state = await TransportStateAsync(d);
            if (state == "PLAYING") { Log.Write("dlna: playing"); return null; }
        }
        return "DID NOT START PLAYING";
    }

    public static async Task StopAsync(Device d) => await SoapAsync(d, "Stop", "<InstanceID>0</InstanceID>");

    static async Task<string> TransportStateAsync(Device d)
    {
        var body = await SoapAsync(d, "GetTransportInfo", "<InstanceID>0</InstanceID>");
        return body == null ? null : Regex.Match(body, "<CurrentTransportState>([^<]*)<").Groups[1].Value;
    }

    static async Task<string> SoapAsync(Device d, string action, string args)
    {
        var envelope = "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                       "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                       $"<u:{action} xmlns:u=\"{d.ServiceType}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, d.ControlUrl) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{d.ServiceType}#{action}\"");
        try
        {
            using var r = await Http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
            {
                Log.Write($"dlna: {action} refused, answer {(int)r.StatusCode}");
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
}
