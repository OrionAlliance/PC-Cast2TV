using System.Diagnostics;

namespace PCCast2TV.Helpers;

// A web page in the default browser (Settings > GITHUB PAGE, PATCH NOTES and DONATE).
public static class WebPage
{
    public const string Donate = "https://ko-fi.com/orionailliance";
    public const string PatchNotes = "https://github.com/OrionAlliance/PC-Cast2TV/releases"; // every version and what changed

    public static void Open(string url, string what)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } // the browser may already be running, then there's no new process
        catch (System.ComponentModel.Win32Exception ex) { Log.Write($"app: couldn't open {what}: {ex.Message}"); }
    }
}
