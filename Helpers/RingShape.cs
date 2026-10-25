using System.Windows;
using System.Windows.Media;

namespace PCCast2TV.Helpers;

// The main window's dial: 270 degrees open at the bottom, 0 at the lower left, full at the lower right.
public static class RingShape
{
    const double Start = -135, Sweep = 270;

    // The point a fraction (0 to 1) of the way round the dial, at radius r.
    public static Point At(Point center, double r, double fraction)
    {
        double a = (Start + Sweep * Math.Clamp(fraction, 0, 1)) * Math.PI / 180;
        return new Point(center.X + r * Math.Sin(a), center.Y - r * Math.Cos(a));
    }

    // The arc from one fraction of the dial to another; null when there's nothing to draw.
    public static Geometry Arc(Point center, double r, double from, double to)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        if (to - from < 0.002) return null;
        var f = new PathFigure { StartPoint = At(center, r, from) };
        f.Segments.Add(new ArcSegment(At(center, r, to), new Size(r, r), 0, (to - from) * Sweep > 180, SweepDirection.Clockwise, true));
        return Frozen(new PathGeometry(new[] { f }));
    }

    // A short line across the dial at a fraction, from radius r1 out to r2.
    public static Geometry Tick(Point center, double r1, double r2, double fraction) =>
        Frozen(new LineGeometry(At(center, r1, fraction), At(center, r2, fraction)));

    static Geometry Frozen(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
