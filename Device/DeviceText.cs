using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// How a device's connection and player read in both looks' lists, and the label and line-icon builders the windows share.
static class DeviceText
{
    // A label in the condensed theme font, cut short with an ellipsis when it doesn't fit.
    public static TextBlock Label(string text, double size, Brush fg) =>
        new() { Text = text, FontSize = size, Foreground = fg, Style = (Style)Application.Current.FindResource("Label"), TextTrimming = TextTrimming.CharacterEllipsis };

    // One of Theme.xaml's 24 x 24 line icons, with round ends and joins.
    public static Path Line(string key, Brush stroke, double thickness) => new()
    {
        Data = (Geometry)Application.Current.FindResource(key), Stroke = stroke, StrokeThickness = thickness,
        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };

    // The bookmark beside a device in both looks: filled for the default; a click sets or clears it.
    public static Button Bookmark(Caster c, Device d, bool isDefault, Brush on, Brush off, double size)
    {
        var icon = Line("IcoBookmark", isDefault ? on : off, 1.8);
        icon.Fill = isDefault ? on : null;
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
