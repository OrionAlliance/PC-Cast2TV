using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The dial in the middle and the readouts beside it. The dial glides to each new reading, the way Fan Control's
// needles sweep, instead of jumping once a second.
public partial class MainWindow
{
    // On the 910 x 428 design: rate ring outside, signal ring inside.
    static readonly Point RingCenter = new(455, 262);
    const double RingRadius = 150, SignalRadius = 118;
    // Most of the second between readings, eased out, so the dial is always moving and settles into each reading.
    static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(900);

    // What the dial shows: the number, how full the ring is, and where the peak mark sits.
    readonly record struct DialReading(double Rate, double Fill, double PeakFill);

    // One 0 to 1 glide carries all three together from where they were to the new reading.
    static readonly DependencyProperty GlideProperty = DependencyProperty.Register("Glide", typeof(double), typeof(MainWindow),
        new PropertyMetadata(1.0, (o, _) => ((MainWindow)o).DrawDial()));
    DialReading _dialFrom, _dialTo;

    readonly Path[] _signalLit = new Path[5];
    readonly bool[] _signalOn = new bool[5];

    void DrawReadout()
    {
        var c = _caster;
        var m = c.Meter;
        bool live = c.Phase == Phase.Casting;
        double scale = WaveShape.Scale(m.Peak);
        ScaleMid.Text = (scale / 2).ToString("0.#", CultureInfo.InvariantCulture);
        ScaleTop.Text = scale.ToString("0", CultureInfo.InvariantCulture);
        EncLine.Data = WaveShape.Build(m.EncodedLine, Wave.Width, Wave.Height, scale);
        SentLine.Data = WaveShape.Build(m.SentLine, Wave.Width, Wave.Height, scale);

        double now = live ? m.Now : 0;
        NowText.Foreground = live ? Main : DimText;
        GlideTo(new DialReading(now, now / scale, m.Peak / scale));
        PeakText.Text = m.Peak.ToString("0.0", CultureInfo.InvariantCulture);
        for (int i = 0; i < _signalLit.Length; i++) FadeSignal(i, live && i < m.Bars);
        SignalText.Text = live ? m.SignalLabel : "IDLE";
        SignalText.Foreground = live ? Soft : DimText;
    }

    // A new reading starts a glide from wherever the dial is now; the same reading again leaves a glide in flight alone.
    void GlideTo(DialReading to)
    {
        if (to == _dialTo)
        {
            if (!IsGliding) DrawDial();
            return;
        }
        _dialFrom = DialNow;
        _dialTo = to;
        BeginAnimation(GlideProperty, new DoubleAnimation(0, 1, GlideTime) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    bool IsGliding => (double)GetValue(GlideProperty) < 1;

    DialReading DialNow
    {
        get
        {
            double t = (double)GetValue(GlideProperty);
            double Mix(double a, double b) => a + (b - a) * t;
            return new DialReading(Mix(_dialFrom.Rate, _dialTo.Rate), Mix(_dialFrom.Fill, _dialTo.Fill), Mix(_dialFrom.PeakFill, _dialTo.PeakFill));
        }
    }

    void DrawDial()
    {
        var d = DialNow;
        NowText.Text = d.Rate.ToString("0.0", CultureInfo.InvariantCulture);
        RingFill.Data = RingShape.Arc(RingCenter, RingRadius, 0, d.Fill);
        PeakTick.Data = d.PeakFill > 0.002 ? RingShape.Tick(RingCenter, 137, 165, d.PeakFill) : null;
    }

    // A segment fades on or off instead of flipping.
    void FadeSignal(int i, bool on)
    {
        if (_signalOn[i] == on) return;
        _signalOn[i] = on;
        _signalLit[i].BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(400)));
    }

    // Five segments round the inside of the dial, one lit per bar of signal: a dim track, and a lit copy over it that fades.
    void BuildSignalRing()
    {
        const double gap = 3.0 / 270;
        for (int i = 0; i < _signalLit.Length; i++)
        {
            var arc = RingShape.Arc(RingCenter, SignalRadius, i / 5.0 + gap, (i + 1) / 5.0 - gap);
            SignalRing.Children.Add(Segment(arc, "Dim", 1));
            _signalLit[i] = Segment(arc, "Soft", 0);
            SignalRing.Children.Add(_signalLit[i]);
        }
    }

    static Path Segment(Geometry arc, string color, double opacity)
    {
        var p = new Path { Data = arc, StrokeThickness = 8, Opacity = opacity, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        p.SetResourceReference(Shape.StrokeProperty, color); // follows the theme by itself
        return p;
    }
}
