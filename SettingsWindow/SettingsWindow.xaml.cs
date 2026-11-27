using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The cog's window, display only: theme pills go to ThemeColors (RANDOM rolls, SAVE keeps MY COLOR), WINDOW LOOK to WindowLook, the controller and SHOW CURSOR to AppSettings, REPORT A PROBLEM to its own window.
public partial class SettingsWindow : Window
{
    SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        ThemeColors.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeColors.Changed -= OnThemeChanged;
        BuildSwatches();
        BuildLook();
        BuildController();
        BuildCursor();
        VersionText.Text = $"VERSION {AppUpdater.Installed}";
    }

    public static void Open(Window owner) => new SettingsWindow { Owner = owner }.ShowDialog();

    void Donate_Click(object sender, RoutedEventArgs e) => WebPage.Open(WebPage.Donate, "the Ko-fi page");

    void PatchNotes_Click(object sender, RoutedEventArgs e) => WebPage.Open(WebPage.PatchNotes, "the patch notes");

    void Report_Click(object sender, RoutedEventArgs e) => ReportWindow.Open(this);

    void OnThemeChanged()
    {
        DarkTitleBar.Apply(this);
        BuildSwatches(); // the ring and tick move to the new pick
        BuildLook();
        BuildController();
        BuildCursor();
    }

    // DIAL and SIMPLE for the main window (it swaps at once); OFF and ON for the stream controller and SHOW CURSOR (the cast picks both up right away).
    void BuildLook() => BuildPair(LookPills, "DIAL", "SIMPLE", WindowLook.Simple, WindowLook.Pick, BuildLook);
    void BuildController() => BuildPair(ControllerPills, "OFF", "ON", AppSettings.StreamController, AppSettings.SetStreamController, BuildController);
    void BuildCursor() => BuildPair(CursorPills, "OFF", "ON", AppSettings.ShowCursor, AppSettings.SetShowCursor, BuildCursor,
        "Keeps the mouse pointer off your TV, so it shows only your screen. Works right away, even during a cast.",
        "Shows your PC's mouse pointer on your TV while you cast. Works right away, even during a cast.");

    // Two pills for a yes/no choice: the one in use is filled, with a ring and a tick; a click saves a change and redraws the pair.
    void BuildPair(Panel pills, string no, string yes, bool on, Action<bool> set, Action redraw, string noTip = null, string yesTip = null)
    {
        void Pick(bool v) { if (v != on) set(v); redraw(); }
        var now = ThemeColors.Current;
        var off = Swatch(no, on ? null : new SolidColorBrush(now.Main), on ? null : now.Soft, () => Pick(false));
        var lit = Swatch(yes, on ? new SolidColorBrush(now.Main) : null, on ? now.Soft : null, () => Pick(true));
        (off.ToolTip, lit.ToolTip) = (noTip, yesTip);
        pills.Children.Clear();
        pills.Children.Add(off);
        pills.Children.Add(lit);
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
        else Own.Children.Add(Swatch("MY COLOR", null, null, null));
        Own.Children.Add(Swatch("SAVE", null, null, roll ? () => { ThemeColors.KeepRoll(); BuildSwatches(); } : null));
        Own.Children.Add(Swatch("CLOSE", null, null, Close, cancel: true));
    }

    // A pill in a ring slot: filled for a color or the choice in use, outlined for an action, dim when it can't be clicked.
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
        return new Border
        {
            Margin = new Thickness(2, 0, 2, 6), Padding = new Thickness(2), CornerRadius = new CornerRadius(19), BorderThickness = new Thickness(2),
            BorderBrush = ring is Color c ? new SolidColorBrush(c) : Brushes.Transparent, Child = pill,
        };
    }
}
