using System.Windows;
using System.Windows.Media;

namespace PCCast2TV.Helpers;

public static class WaveShape
{
    // A smooth curve through the rate samples (Catmull-Rom), scaled into the chart box.
    public static Geometry Build(double[] values, double width, double height, double max)
    {
        var p = values.Select((v, i) => new Point(width * i / (values.Length - 1), height - height * Math.Min(v, max) / max)).ToList();
        var f = new PathFigure { StartPoint = p[0] };
        for (int i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(i - 1, 0)]; var p1 = p[i]; var p2 = p[i + 1]; var p3 = p[Math.Min(i + 2, p.Count - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Clamp(p1.Y + (p2.Y - p0.Y) / 6, height));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Clamp(p2.Y - (p3.Y - p1.Y) / 6, height));
            f.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
        var g = new PathGeometry(new[] { f });
        g.Freeze();
        return g;
    }

    // Keeps the curve's overshoot inside the chart so it never dips below zero.
    static double Clamp(double y, double height) => Math.Clamp(y, 0, height);

    // Chart top in Mbps: 15, or the next step of 15 that fits the peak.
    public static double Scale(double peak) => Math.Max(15, Math.Ceiling(peak / 15) * 15);
}
