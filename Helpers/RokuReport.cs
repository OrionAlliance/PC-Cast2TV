using System.Globalization;

namespace PCCast2TV.Helpers;

// Turns the PC Cast2TV Roku app's 10-second player report into one log line. Only numbers are read from it; the words are ours.
public static class RokuReport
{
    // newestOnPc: the newest piece the PC has finished, to see how far behind live the piece on the TV is.
    // BehindSec is that number for the window; null when the report doesn't have what it takes.
    public static (string Line, double? BehindSec) Describe(string query, int? newestOnPc)
    {
        var v = new Dictionary<string, double>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && double.TryParse(Uri.UnescapeDataString(kv[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && double.IsFinite(n))
                v[kv[0]] = n;
        }
        string F(string key, string format) => v.TryGetValue(key, out var n) ? n.ToString(format, CultureInfo.InvariantCulture) : "?";
        double Get(string key) => v.TryGetValue(key, out var n) ? n : 0;
        static string N(double x, string format) => x.ToString(format, CultureInfo.InvariantCulture);

        // The live moment is inside the piece the PC is still recording, one after its newest finished one.
        double? behindSec = null;
        string held = "?";
        if (v.TryGetValue("playing", out var playing))
        {
            if (newestOnPc is int pc && v.TryGetValue("piecems", out var pieceMs)) behindSec = (pc + 1 - playing) * pieceMs / 1000;
            if (v.TryGetValue("newest", out var newest)) held = N(newest - playing, "0");
        }
        string behind = behindSec is double b ? N(b, "0.0") : "?";
        double ms = Get("downloadms");
        string mbps = ms > 0 ? N(Get("bytes") * 8 / ms / 1000, "0.0") : "?";
        return ($"about {behind} s behind live (piece {F("playing", "0")} on screen, PC finished {(newestOnPc?.ToString() ?? "?")}, " +
                $"Roku holds {held} more), speed {F("speed", "0.000")}x (whole cast {F("castspeed", "0.0000")}x), " +
                $"{F("pieces", "0")} pieces in (slowest {F("slowest", "0")} ms, each plays {F("piecems", "0")} ms), " +
                $"{F("late", "0")} slower than they play, {F("failed", "0")} failed, arriving at {mbps} Mbps, " +
                $"{F("stalls", "0")} stalls" + (Get("stallms") > 0 ? $" ({F("stallms", "0")} ms)" : ""), behindSec);
    }
}
