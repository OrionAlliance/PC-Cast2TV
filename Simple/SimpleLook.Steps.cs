using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// Page 2: the picture's trip (the video, this app, the network, the TV), each lit in the theme color, or the watch color where a stutter would start.
public partial class SimpleLook
{
    enum Health { Idle, Ok, Watch }

    sealed record Step(Border Ring, Path Icon, TextBlock Name, TextBlock Value, TextBlock Sub);

    readonly Step[] _steps = new Step[4];
    bool _onSteps;
    Brush _warn;
    DropShadowEffect _warnGlow;
    static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(320);

    void Details_Click(object sender, RoutedEventArgs e) => Slide(true);
    void Back_Click(object sender, RoutedEventArgs e) => Slide(false);

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_caster.Phase == Phase.Casting) await _caster.ToggleCastAsync();
    }

    // Both pages move together, one out and one in.
    void Slide(bool toSteps)
    {
        if (_onSteps == toSteps) return;
        _onSteps = toSteps;
        double w = Pages.ActualWidth;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        PickerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(toSteps ? 0 : -w, toSteps ? -w : 0, SlideTime) { EasingFunction = ease });
        StepsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(toSteps ? w : 0, toSteps ? 0 : w, SlideTime) { EasingFunction = ease });
    }

    // The window grows with the cards; the page out of view moves off past the new edge.
    void Pages_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var hidden = _onSteps ? PickerShift : StepsShift;
        hidden.BeginAnimation(TranslateTransform.XProperty, null);
        hidden.X = _onSteps ? -4000 : 4000;
    }

    void BuildSteps()
    {
        string[] icons = { "IcoVideo", "IcoLaptop", "IcoWifi", "IcoTv" };
        for (int i = 0; i < _steps.Length; i++)
        {
            var icon = new Path { Data = (Geometry)FindResource(icons[i]), StrokeThickness = 1.9, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var ring = new Border
            {
                Width = 72, Height = 72, CornerRadius = new CornerRadius(36), BorderThickness = new Thickness(3), Background = Res("Ink"), HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Viewbox { Width = 30, Height = 30, Child = new Canvas { Width = 24, Height = 24, Children = { icon } } },
            };
            var name = StepText(15);
            name.Margin = new Thickness(0, 10, 0, 0);
            var value = StepText(24);
            var sub = StepText(13);
            sub.TextWrapping = TextWrapping.Wrap;
            var panel = new StackPanel { Children = { ring, name, value, sub } };
            Grid.SetColumn(panel, i * 2);
            Grid.SetColumnSpan(panel, 2);
            StepRow.Children.Add(panel);
            _steps[i] = new Step(ring, icon, name, value, sub);
        }
    }

    TextBlock StepText(double size) => new()
    {
        FontSize = size, Style = (Style)FindResource("Label"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 0, 4, 0),
    };

    // While casting: this cast's numbers; after one: the last cast's, dimmed; before any: nothing yet.
    void DrawSteps()
    {
        var c = _caster;
        bool live = c.Phase == Phase.Casting;
        var tv = live ? c.CastingTo : c.HealthFrom;
        var inv = CultureInfo.InvariantCulture;
        StepsTitle.Text = live ? "CASTING TO " + tv.Name.ToUpperInvariant() : tv != null ? "LAST CAST: " + tv.Name.ToUpperInvariant() : "NOT CASTING YET";
        StepsSub.Text = live ? string.Create(inv, $"{Minutes(c.CastTime)} · {c.Meter.Now:0.0} MBPS") : tv != null ? "THE NUMBERS FROM THAT CAST" : "START A CAST AND EACH STEP LIGHTS UP";
        StopBtn.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        Health Now(bool watch) => !live ? Health.Idle : watch ? Health.Watch : Health.Ok;

        // The video: new pictures a second from your screen (a still screen makes few, and that's fine).
        Show(_steps[0], Now(false), "THE VIDEO", c.ScreenFps is double fps ? fps.ToString("0", inv) + " FPS" : live ? "..." : "IDLE", "new pictures a second");

        // This app: pictures the screen clock missed; a few in 10 seconds is normal, more is this PC falling behind.
        bool appWatch = c.RecentMissed >= 6;
        Show(_steps[1], Now(appWatch), "THIS APP", tv == null ? "IDLE" : c.DroppedFrames.ToString("N0", inv) + " MISSED",
             c.RecentMissed == 0 ? "every picture sent" : $"{c.RecentMissed} late in 10 s");

        // The network: how much of what was made the TV took; a Roku's burst fetching wobbles that, so for a Roku it counts only with a stall it reported.
        var m = c.Meter;
        bool rokuKeepsUp = tv?.IsRoku == true && c.RecentStalls == 0;
        bool netWatch = live && !m.Measuring && m.Bars <= 3 && !rokuKeepsUp;
        bool wifi = c.PcLink == "WI-FI" || c.LinkOf(tv) == "WI-FI";
        Show(_steps[2], Now(netWatch), wifi ? "WI-FI" : "NETWORK", live ? m.SignalLabel : "IDLE",
             !live || m.Measuring ? "how much the TV gets" : rokuKeepsUp ? "the Roku keeps up"
             : m.Bars >= 4 ? "the TV gets it all" : m.Bars == 3 ? "the TV gets most of it" : "the TV is missing some");

        // The TV: a Roku reports its stalls and how far behind live it plays; other TVs just play.
        bool tvWatch = c.RecentStalls > 0;
        string tvValue = c.TvStalls is int stalls ? $"{stalls} STALLS" : live ? "PLAYING" : "IDLE";
        string tvSub = c.TvBehindSec is double b ? b.ToString("0", inv) + " s behind live" : tv?.IsRoku == true ? "reports every 10 s" : "plays it live";
        Show(_steps[3], Now(tvWatch), "THE TV", tvValue, tvSub);

        StepsLine.Text = !live ? (tv != null ? "These are the last cast's numbers." : "Start a cast and each step lights up here.")
            : appWatch ? "A stutter right now would be this PC falling behind, not the network or the TV."
            : netWatch ? "A stutter right now would be the network, not the video and not this app."
            : tvWatch ? "A stutter right now would be the TV, not the network and not this app."
            : "Every step is keeping up, so the picture should be smooth.";
        StepsDot.Fill = !live ? Res("DimText") : appWatch || netWatch || tvWatch ? Warn : Res("Main");
    }

    void Show(Step s, Health h, string name, string value, string sub)
    {
        var color = h switch { Health.Ok => Res("Main"), Health.Watch => Warn, _ => Res("Dim") };
        s.Ring.BorderBrush = color;
        s.Ring.Effect = h == Health.Watch ? WarnGlow : null;
        s.Icon.Stroke = h == Health.Idle ? Res("DimText") : color;
        s.Name.Text = name;
        s.Name.Foreground = Res("Second");
        s.Value.Text = value;
        s.Value.Foreground = h == Health.Watch ? Warn : h == Health.Idle ? Res("DimText") : Res("Soft");
        s.Sub.Text = sub;
        s.Sub.Foreground = Res("DimText");
    }

    // The watch color follows the theme (amber, or red on an amber-like theme); made once per theme.
    Brush Warn => _warn ??= (Brush)new SolidColorBrush(ThemeColors.Warning).GetAsFrozen();

    DropShadowEffect WarnGlow => _warnGlow ??= (DropShadowEffect)new DropShadowEffect { Color = ThemeColors.Warning, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.45 }.GetAsFrozen();
}
