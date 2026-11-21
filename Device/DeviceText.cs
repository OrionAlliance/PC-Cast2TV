using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// How a device's connection and player read in both looks' lists.
static class DeviceText
{
    // The bookmark beside a device in both looks: filled for the default; a click sets or clears it.
    public static Button Bookmark(Caster c, Device d, bool isDefault, Brush on, Brush off, double size)
    {
        var icon = new Path
        {
            Data = (Geometry)Application.Current.FindResource("IcoBookmark"), Stroke = isDefault ? on : off, Fill = isDefault ? on : null, StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var mark = new Button
        {
            Style = (Style)Application.Current.FindResource("Bare"), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = isDefault ? "Default device (click to clear)" : "Make this the default",
            Content = new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { icon } } },
        };
        mark.Click += (_, _) => c.ToggleDefault(d);
        return mark;
    }

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
