using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The cog's window, display only: it draws the theme pills and hands each click to ThemeColors, which applies and saves it.
// Under the 8 themes: RANDOM rolls a new color, SAVE keeps the roll on screen as MY COLOR.
public partial class SettingsWindow : Window
{
    SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        ThemeColors.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeColors.Changed -= OnThemeChanged;
        BuildSwatches();
        VersionText.Text = $"VERSION {AppUpdater.Installed}";
    }

    public static void Open(Window owner) => new SettingsWindow { Owner = owner }.ShowDialog();

    void GitHub_Click(object sender, RoutedEventArgs e) => AppUpdater.OpenPage();

    void OnThemeChanged()
    {
        DarkTitleBar.Apply(this);
        BuildSwatches(); // the ring and tick move to the new pick
    }

    // Each pill is painted in its own color, whatever theme is on now; the one on now has a ring and a tick.
    void BuildSwatches()
    {
        var now = ThemeColors.Current;
        Swatches.Children.Clear();
        foreach (var t in ThemeColors.All)
        {
            bool on = now.Roll == null && now.Name == t.Name;
            Swatches.Children.Add(Swatch(t.Name, new SolidColorBrush(t.Main), on ? t.Soft : null, () => ThemeColors.Pick(t.Name)));
        }

        // RANDOM is filled while its roll is on (it's a color then), MY COLOR once something's saved; the rest are actions.
        string saved = ThemeColors.MyColor;
        bool roll = ThemeColors.UnsavedRoll != null;
        Own.Children.Clear();
        Own.Children.Add(Swatch("RANDOM", roll ? new SolidColorBrush(now.Main) : null, roll ? now.Soft : null, () => ThemeColors.Pick(ThemeColors.Roll()), "IcoDice"));
        if (saved != null)
        {
            var mine = ThemeColors.FromKey(saved);
            Own.Children.Add(Swatch("MY COLOR", new SolidColorBrush(mine.Main), now.Roll == saved ? mine.Soft : null, () => ThemeColors.Pick(saved)));
        }
        else Own.Children.Add(Swatch("MY COLOR", null, null, null)); // nothing saved yet
        Own.Children.Add(Swatch("SAVE", null, null, roll ? () => { ThemeColors.KeepRoll(); BuildSwatches(); } : null));
        Own.Children.Add(Swatch("CLOSE", null, null, Close, cancel: true));
    }

    // A pill in a ring slot. Filled = a color; no fill = an action, outlined. No click = dim and can't be pressed.
    Border Swatch(string name, Brush fill, Color? ring, Action click, string icon = null, bool cancel = false)
    {
        var text = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
            text.Children.Add(new Viewbox
            {
                Width = 13, Height = 13, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new Canvas { Width = 24, Height = 24, Children = { new Path { Style = (Style)FindResource("PillIcon"), Data = (Geometry)FindResource(icon) } } },
            });
        text.Children.Add(new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (ring != null)
            text.Children.Add(new Path
            {
                Data = Geometry.Parse("M4,11 L9,16 L18,6"), Width = 10, Height = 8, Stretch = Stretch.Uniform, Margin = new Thickness(5, 0, 0, 0),
                Stroke = (Brush)FindResource("Ink"), StrokeThickness = 2.4, VerticalAlignment = VerticalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            });
        var pill = new Button { Style = (Style)FindResource(fill == null ? "OutlinePill" : "Pill"), Height = 30, Content = text, IsEnabled = click != null, IsCancel = cancel };
        if (fill != null) pill.Background = fill;
        if (click != null) pill.Click += (_, _) => click();
        // The ring: the picked one sits inside a soft outline with a dark gap, like a selected tile.
        return new Border
        {
            Margin = new Thickness(2, 0, 2, 6), Padding = new Thickness(2), CornerRadius = new CornerRadius(19), BorderThickness = new Thickness(2),
            BorderBrush = ring is Color c ? new SolidColorBrush(c) : Brushes.Transparent, Child = pill,
        };
    }
}
