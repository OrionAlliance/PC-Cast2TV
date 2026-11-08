using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// MONITOR SELECTION, over both looks: shown only with 2 or more monitors; display only, the Caster owns the pick and the default.
public partial class MonitorSelection : UserControl
{
    Caster _caster;
    bool _open;
    string _drawn;   // what the cards show, so they're rebuilt only when it changes
    string _autoFor; // the monitor set it last opened by itself for, so it opens once per set, not on every redraw

    public MonitorSelection()
    {
        InitializeComponent();
        // Closed, the slider sits just above the window's top edge and only the tab shows.
        Slider.SizeChanged += (_, _) =>
        {
            if (_open) return;
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Slide.Y = Hidden;
        };
        Tab.MouseEnter += (_, _) => TabColor("Main");
        Tab.MouseLeave += (_, _) => TabColor("Second");
    }

    public void Attach(Caster caster) => _caster = caster;

    // Theme change: the cards are drawn again in the new colors.
    public void Repaint()
    {
        _drawn = null;
        Draw();
    }

    public void Draw()
    {
        var c = _caster;
        if (c == null) return;
        bool show = c.Screens.Count > 1;
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            _open = false;
            return;
        }
        // A cast in progress keeps its monitor: the tab looks the same but only answers once the cast is over.
        Tab.IsEnabled = c.Idle;
        Tab.Cursor = c.Idle ? Cursors.Hand : Cursors.Arrow;
        Tab.ToolTip = c.Idle ? "Pick which monitor to cast" : "Change monitors once the cast is over";
        if (!c.Idle && _open) Move(false);
        string drawn = string.Join("|", c.Screens.Select(s => s.Key + s.LogName)) + "|" + c.CastScreen?.Key + "|" + c.DefaultScreen?.Key;
        if (drawn != _drawn)
        {
            _drawn = drawn;
            Cards.Children.Clear();
            foreach (var s in c.Screens) Cards.Children.Add(Column(s, s.Key == c.CastScreen?.Key, s.Key == c.DefaultScreen?.Key));
        }
        // First run (or the default was unplugged): it opens by itself so the tab explains itself.
        string set = string.Join("|", c.Screens.Select(s => s.Key));
        if (c.ScreenChoiceNeeded && c.Idle && !_open && _autoFor != set)
        {
            _autoFor = set;
            Dispatcher.BeginInvoke(() => Move(true), DispatcherPriority.Loaded);
        }
    }

    void Tab_Click(object sender, MouseButtonEventArgs e) => Move(!_open);

    void Move(bool open)
    {
        _open = open;
        Arrow.Data = Geometry.Parse(open ? "M0,5 L8,5 L4,0 Z" : "M0,0 L8,0 L4,5 Z");
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(open ? 0 : Hidden, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // Closed: the slider's bottom edge tucked just past the window's top, so no border line peeks through.
    double Hidden => -(Slider.ActualHeight + 2);

    void TabColor(string key)
    {
        TabText.SetResourceReference(TextBlock.ForegroundProperty, key);
        Arrow.SetResourceReference(Shape.FillProperty, key);
    }

    // A monitor's card, and its SET AS DEFAULT box under it.
    FrameworkElement Column(PcScreen s, bool lit, bool isDefault)
    {
        var col = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        col.Children.Add(Card(s, lit));
        col.Children.Add(DefaultBox(s, isDefault));
        return col;
    }

    // Click casts this monitor (this session) and the slider goes back up; the lit one is the monitor the next cast copies.
    Border Card(PcScreen s, bool lit)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(s.Name, 20, lit ? Res("Soft") : Res("Second")));
        text.Children.Add(Text($"{s.Width} × {s.Height}" + (s.Main ? " · MAIN" : ""), 12, Res("DimText")));
        var icon = new Viewbox
        {
            Width = 26, Height = 26, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { Line("IcoPc", lit ? Res("Main") : Res("DimText")) } },
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(icon);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        Brush rest = Tint(lit ? 0.08 : 0.03), hover = Tint(lit ? 0.14 : 0.09);
        var card = new Border
        {
            Width = 210, Height = 64, CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 0, 10, 0),
            BorderThickness = new Thickness(lit ? 2 : 1.5), BorderBrush = lit ? Res("Main") : Res("Dim"), Background = rest, Cursor = Cursors.Hand, Child = g,
        };
        if (lit) card.Effect = new DropShadowEffect { Color = ThemeColors.Current.Main, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.3 };
        card.MouseEnter += (_, _) => card.Background = hover;
        card.MouseLeave += (_, _) => card.Background = rest;
        card.MouseLeftButtonUp += (_, _) =>
        {
            _caster.PickScreen(s);
            Move(false);
        };
        return card;
    }

    // Ticked, this monitor is cast every time and the slider stays down when the app opens; unticking clears it.
    FrameworkElement DefaultBox(PcScreen s, bool on)
    {
        var box = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = on ? Res("Main") : Res("Second"), Background = on ? Res("Main") : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
        };
        if (on) box.Child = new Path
        {
            Data = Geometry.Parse("M4,11 L9,16 L18,6"), Stretch = Stretch.Uniform, Margin = new Thickness(2.5), Stroke = Res("Ink"), StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 2),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = on ? "Default monitor (click to clear)" : "Cast this monitor every time",
        };
        row.Children.Add(box);
        row.Children.Add(Text("SET AS DEFAULT", 12, on ? Res("Main") : Res("Second")));
        row.MouseLeftButtonUp += (_, _) => _caster.ToggleDefaultScreen(s);
        return row;
    }

    Brush Res(string key) => (Brush)FindResource(key);

    // The theme's main color, see-through.
    static Brush Tint(double opacity) => (Brush)new SolidColorBrush(ThemeColors.Current.Main) { Opacity = opacity }.GetAsFrozen();

    TextBlock Text(string text, double size, Brush fg) =>
        new() { Text = text, FontSize = size, Foreground = fg, Style = (Style)FindResource("Label"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    Path Line(string key, Brush stroke) => new()
    {
        Data = (Geometry)FindResource(key), Stroke = stroke, StrokeThickness = 1.8,
        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };
}
