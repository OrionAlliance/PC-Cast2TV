using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The simple look, display only: page 1 finds, picks and casts, page 2 shows each step of the cast; every click goes to the Caster.
public partial class SimpleLook : UserControl
{
    const double CardSpace = 236; // a 224-wide card and its 12 gap
    Caster _caster;
    string _cardsKey;

    public SimpleLook()
    {
        InitializeComponent();
        BuildSteps();
    }

    public void Attach(Caster caster) => _caster = caster;

    Window Owner => Window.GetWindow(this);
    Brush Res(string key) => (Brush)FindResource(key);

    // The cards and glows are drawn here, so a new theme redraws them.
    public void Repaint()
    {
        _cardsKey = null;
        _warn = null;
        _warnGlow = null;
        Draw();
    }

    public void Draw()
    {
        if (_caster == null) return;
        DrawCards();
        DrawCastButton();
        DrawStatus();
        DrawSteps();
    }

    void Cog_Click(object sender, RoutedEventArgs e) => SettingsWindow.Open(Owner);
    async void Cast_Click(object sender, RoutedEventArgs e) => await _caster.ToggleCastAsync();

    // Rebuilt only when a card would look different, not on every readout tick.
    void DrawCards()
    {
        var c = _caster;
        var lit = (c.CastingTo ?? c.Picked)?.Id;
        var key = string.Join("|", c.Devices.Select(d => d.Id + d.Name + DeviceLine(d))) + "#" + lit + "#" + c.Default?.Id + "#" + c.Phase + "#" + c.CastingTo?.Id;
        if (key == _cardsKey) return;
        _cardsKey = key;
        Cards.Children.Clear();
        Cards.Children.Add(FindCard());
        foreach (var d in c.Devices) Cards.Children.Add(DeviceCard(d, d.Id == lit, d.Id == c.Default?.Id));
        Cards.Width = Math.Clamp(Cards.Children.Count, 2, 3) * CardSpace; // never under 2 across, so the button and status line fit
    }

    string DeviceLine(Device d) => $"{d.KindLabel} · {DeviceText.Link(_caster, d)}{DeviceText.Player(_caster, d)}";

    // FIND: TV / ROKU finds devices; right-click shows the hidden ones.
    FrameworkElement FindCard()
    {
        var c = _caster;
        bool can = c.Phase == Phase.Online;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(c.Phase == Phase.Detecting ? "FINDING..." : "FIND:", 13, Res("Second")));
        text.Children.Add(Text("TV / ROKU", 20, can ? Res("Soft") : Res("DimText")));
        var card = Card(Icon("IcoSearch", can ? Res("Second") : Res("DimText"), 26), text, null, lit: false, find: true);
        card.Cursor = can ? Cursors.Hand : Cursors.Arrow;
        card.ToolTip = "Click to find your TVs and Rokus. Right-click to show hidden devices";
        card.MouseLeftButtonUp += async (_, _) => await c.DetectAsync();
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (c.Idle) DevicePickerWindow.ShowHidden(Owner, c);
        };
        return card;
    }

    // A device: click picks it, the bookmark makes it the default, right-click hides it.
    FrameworkElement DeviceCard(Device d, bool lit, bool isDefault)
    {
        var c = _caster;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(d.Name.ToUpperInvariant(), 20, lit ? Res("Soft") : Res("Second")));
        if (c.Phase == Phase.Casting && c.CastingTo?.Id == d.Id)
        {
            var now = new StackPanel { Orientation = Orientation.Horizontal };
            now.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Res("Main"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            now.Children.Add(Text("CASTING NOW", 12, Res("Main")));
            text.Children.Add(now);
        }
        else text.Children.Add(Text(DeviceLine(d), 12, Res("DimText")));
        var card = Card(Icon("IcoTv", lit ? Res("Main") : Res("DimText"), 26), text, Bookmark(d, isDefault), lit);
        card.ToolTip = "Right-click to hide this device";
        card.MouseLeftButtonUp += (_, _) => c.Pick(d);
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (c.Idle && DeviceActionWindow.Ask(Owner, d)) c.Hide(d);
        };
        return card;
    }

    Button Bookmark(Device d, bool isDefault)
    {
        var icon = new Path
        {
            Data = (Geometry)FindResource("IcoBookmark"), Stroke = isDefault ? Res("Main") : Res("DimText"), Fill = isDefault ? Res("Main") : null, StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var mark = new Button
        {
            Style = (Style)FindResource("Bare"), VerticalAlignment = VerticalAlignment.Center, ToolTip = isDefault ? "Default device (click to clear)" : "Make this the default",
            Content = new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 24, Height = 24, Children = { icon } } },
        };
        mark.Click += (_, _) => _caster.ToggleDefault(d);
        return mark;
    }

    // A card: icon, two lines of text, maybe a bookmark; the lit one has a ring in the theme color and a soft glow.
    Border Card(FrameworkElement icon, FrameworkElement text, FrameworkElement mark, bool lit, bool find = false)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.Margin = new Thickness(0, 0, 14, 0);
        g.Children.Add(icon);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (mark != null)
        {
            Grid.SetColumn(mark, 2);
            g.Children.Add(mark);
        }
        Brush rest = lit ? Tint(0.08) : find ? Brushes.Transparent : Tint(0.03), hover = Tint(lit ? 0.14 : 0.09);
        var card = new Border
        {
            Width = 224, Height = 64, CornerRadius = new CornerRadius(14), Margin = new Thickness(6), Padding = new Thickness(16, 0, 6, 0),
            BorderThickness = new Thickness(lit ? 2 : 1.5), BorderBrush = lit ? Res("Main") : Res("Dim"), Background = rest, Cursor = Cursors.Hand, Child = g,
        };
        if (lit) card.Effect = new DropShadowEffect { Color = ThemeColors.Current.Main, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.35 };
        card.MouseEnter += (_, _) => card.Background = hover;
        card.MouseLeave += (_, _) => card.Background = rest;
        return card;
    }

    // The big button says the next step; only CAST and STOP can be clicked.
    void DrawCastButton()
    {
        var c = _caster;
        var step = c.Step;
        bool casting = step == CastStep.Casting, ready = step == CastStep.Cast;
        CastBtn.IsEnabled = casting || ready;
        CastBtn.Style = (Style)FindResource(casting ? "OutlinePill" : "Pill");
        if (ready) CastBtn.Background = Res("Main");
        else CastBtn.ClearValue(BackgroundProperty);
        CastGlow.Color = ThemeColors.Current.Main;
        CastGlow.Opacity = casting || ready ? 0.35 : 0;
        CastIcon.Data = (Geometry)FindResource(casting ? "IcoStop" : "IcoPlay");
        CastIcon.Visibility = casting || ready ? Visibility.Visible : Visibility.Collapsed;
        string name = (c.CastingTo ?? c.Picked)?.Name.ToUpperInvariant();
        CastLabel.Text = step switch
        {
            CastStep.Casting => "STOP CASTING TO " + name,
            CastStep.Cast => "CAST TO " + name,
            CastStep.Offline => "OFFLINE",
            CastStep.DetectFirst => "FIND YOUR TV OR ROKU FIRST",
            CastStep.SelectDevice => "PICK A TV OR ROKU",
            _ => c.Phase switch { Phase.Connecting => "CONNECTING...", Phase.Detecting => "FINDING...", Phase.Starting => "STARTING...", _ => "ENDING..." },
        };
    }

    // One line: what's going on, and while casting the time, the rate and the signal.
    void DrawStatus()
    {
        var c = _caster;
        StatusText.Text = c.Phase == Phase.Casting
            ? string.Create(CultureInfo.InvariantCulture, $"{c.Status} · {Minutes(c.CastTime)} · {c.Meter.Now:0.0} MBPS · SIGNAL {c.Meter.SignalLabel}")
            : WindowLook.Words(c.Status);
        StatusDot.Fill = c.IsOnline ? Res("Main") : Res("DimText");
    }

    static string Minutes(TimeSpan t) => $"{(int)t.TotalMinutes} MIN";

    // The theme's main color, see-through.
    static Brush Tint(double opacity) => (Brush)new SolidColorBrush(ThemeColors.Current.Main) { Opacity = opacity }.GetAsFrozen();

    TextBlock Text(string text, double size, Brush fg) =>
        new() { Text = text, FontSize = size, Foreground = fg, Style = (Style)FindResource("Label"), TextTrimming = TextTrimming.CharacterEllipsis };

    FrameworkElement Icon(string key, Brush stroke, double size) => new Viewbox
    {
        Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
        Child = new Canvas { Width = 24, Height = 24, Children = { Line(key, stroke) } },
    };

    Path Line(string key, Brush stroke) => new()
    {
        Data = (Geometry)FindResource(key), Stroke = stroke, StrokeThickness = 1.9,
        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };
}
