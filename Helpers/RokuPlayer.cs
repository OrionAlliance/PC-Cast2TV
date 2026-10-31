using System.Net.Http;
using System.Xml.Linq;

namespace PCCast2TV.Helpers;

public enum RokuApp { None, Ours, DisplayHub }

// Plays and stops the cast on a Roku over ECP (port 8060), through our app in its developer slot or DisplayHUB when ours isn't on it.
public static class RokuPlayer
{
    const string OurApp = "dev";
    const string OurName = "PC Cast2TV"; // the developer slot holds one app; this checks it's ours
    const string DisplayHub = "835615";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 1_000_000 };

    // Which player is on the Roku, from its app list: ours first, DisplayHUB as the backup. Null when the list can't be read.
    public static async Task<RokuApp?> PlayerOnAsync(Device d, bool logFailure = true)
    {
        try
        {
            var apps = XDocument.Parse(await Http.GetStringAsync($"http://{d.Ip}:8060/query/apps")).Root.Elements("app").ToList();
            if (apps.FirstOrDefault(a => (string)a.Attribute("id") == OurApp)?.Value == OurName) return RokuApp.Ours;
            return apps.Any(a => (string)a.Attribute("id") == DisplayHub) ? RokuApp.DisplayHub : RokuApp.None;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            if (logFailure) Log.Write($"roku: could not read the app list: {ex.GetType().Name}");
            return null;
        }
    }

    // Opens DisplayHUB's store page on the TV (adding it takes one OK on the remote); false when the Roku refuses or doesn't answer.
    public static async Task<bool> OpenDisplayHubPageAsync(Device d)
    {
        try
        {
            using var r = await Http.PostAsync($"http://{d.Ip}:8060/install/{DisplayHub}", null);
            Log.Write($"roku: opened DisplayHUB's store page on {d.LogName}, answer {(int)r.StatusCode}");
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"roku: DisplayHUB's store page request failed: {ex.GetType().Name}");
            return false;
        }
    }

    static string IdOf(RokuApp app) => app == RokuApp.Ours ? OurApp : DisplayHub;
    static string LabelOf(RokuApp app) => app == RokuApp.Ours ? "our app" : "DisplayHUB";

    // Answering at its last address, and still the same Roku (serial matches).
    public static async Task<bool> AnsweringAsync(Device d)
    {
        try
        {
            var (serial, _, _) = await DeviceFinder.RokuInfoAsync(d.Ip, Http);
            bool same = "roku:" + serial == d.Id;
            if (!same) Log.Write($"roku: a different Roku answers at {d.Ip}");
            return same;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Log.Write($"roku: {d.Ip} not answering: {ex.GetType().Name}");
            return false;
        }
    }

    // Our app takes the link in url and sends buttons back, DisplayHUB takes it in contentID; null once playing, else what went wrong.
    public static async Task<string> PlayAsync(Device d, RokuApp app, string url, Func<int> piecesServed)
    {
        const string failed = "DID NOT START PLAYING";
        string label = LabelOf(app), link = Uri.EscapeDataString(url);
        try
        {
            using var r = await Http.PostAsync($"http://{d.Ip}:8060/launch/{IdOf(app)}?{(app == RokuApp.Ours ? "url" : "contentID")}={link}", null);
            Log.Write($"roku: {label} asked to play, answer {(int)r.StatusCode}");
            if (!r.IsSuccessStatusCode) return failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"roku: play request failed: {ex.GetType().Name}");
            return failed;
        }
        // Playing once the Roku says so, or once it is pulling video pieces from us.
        int startPieces = piecesServed();
        string unread = null; // why the Roku's player state last couldn't be read, for the log
        for (int i = 0; i < 24; i++)
        {
            await Task.Delay(500);
            var (state, error, why) = await MediaStateAsync(d);
            unread = why ?? unread;
            if (error) { Log.Write($"roku: {label} reported a player error"); return failed; }
            if (state == "play" || piecesServed() - startPieces >= 3)
            {
                Log.Write($"roku: {label} playing (state {Log.Outside(state)}, pieces {piecesServed() - startPieces})");
                return null;
            }
        }
        Log.Write($"roku: {label} never started (pieces {piecesServed() - startPieces}" + (unread == null ? ")" : $", its player state couldn't be read: {unread})"));
        return failed;
    }

    // The app on the Roku's screen now (null: home screen); a Roku that doesn't answer throws, so it isn't taken for home.
    static async Task<string> ActiveAppAsync(Device d)
    {
        var x = XDocument.Parse(await Http.GetStringAsync($"http://{d.Ip}:8060/query/active-app"));
        var id = x.Root?.Element("app")?.Attribute("id")?.Value;
        Log.Write($"roku: on screen now: {Log.Outside(id) ?? "home"}");
        return string.IsNullOrEmpty(id) ? null : id;
    }

    static async Task<(string State, bool Error, string Failed)> MediaStateAsync(Device d)
    {
        try
        {
            var x = XDocument.Parse(await Http.GetStringAsync($"http://{d.Ip}:8060/query/media-player"));
            return (x.Root?.Attribute("state")?.Value ?? "", x.Root?.Attribute("error")?.Value == "true", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { return ("", false, ex.GetType().Name); }
    }

    // Back to the Roku home screen, but only if the cast's player is still the one on screen.
    public static async Task StopAsync(Device d, RokuApp app)
    {
        try
        {
            var onScreen = await ActiveAppAsync(d);
            if (onScreen != IdOf(app))
            {
                Log.Write($"roku: player no longer on screen (shows {Log.Outside(onScreen) ?? "home"}), left alone");
                return;
            }
            using var r = await Http.PostAsync($"http://{d.Ip}:8060/keypress/Home", null);
            Log.Write($"roku: sent Home, answer {(int)r.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Log.Write($"roku: stop failed: {ex.GetType().Name}");
        }
    }
}
