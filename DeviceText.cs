using PCCast2TV.Helpers;

namespace PCCast2TV;

// How a device's connection and player read in both looks' lists.
static class DeviceText
{
    // "WI-FI" or "WIRED" as the device says; otherwise why it isn't known yet.
    public static string Link(Caster c, Device d)
    {
        if (c.LinkOf(d) is { } link) return link;
        if (c.LinkAsked(d)) return d.Kind == DeviceKind.Dlna ? "DOESN'T SAY" : "DIDN'T ANSWER";
        return c.IsOnline ? "CHECKING..." : "OFFLINE";
    }

    // " · PC2TV" once a Roku has answered that it has our app.
    public static string Player(Caster c, Device d) => c.LinkOf(d) != null && c.PlayerOf(d) == RokuApp.Ours ? " · PC2TV" : "";
}
