using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// Display only: buttons hand off to the Caster, and Draw() shows whatever the Caster reports.
public partial class MainWindow : Window
{
    readonly Caster _caster = new();
    string _rowKey;
    Task _shutdown; // the cast ending as the window closes; a second X waits for it too

    // The theme's colors right now (Settings can change them while the app runs).
    Brush Main => (Brush)FindResource("Main");
    Brush Soft => (Brush)FindResource("Soft");
    Brush Second => (Brush)FindResource("Second");
    Brush Dim => (Brush)FindResource("Dim");
    Brush DimText => (Brush)FindResource("DimText");
    Brush Ink => (Brush)FindResource("Ink");

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PC Cast2TV  v{AppUpdater.Installed}"; // title bar and taskbar, like Fan Control
        BuildSignalRing();
        _caster.Changed += Draw;
        _caster.Changed += () => { if (_pendingUpdate != null) Dispatcher.BeginInvoke(OfferUpdate); }; // a waiting update shows once the cast is over
        _caster.Notice += (title, body) => NoticeWindow.Tell(this, title, body);
        _caster.NeedsRokuApp += roku => RokuAppWindow.Tell(this, roku, stop => _caster.WaitForRokuAppAsync(roku, stop));
        ContentRendered += async (_, _) => await CheckForUpdateAsync();
        ThemeColors.Changed += OnThemeChanged;
        Glow(ThemeColors.Current.Main);
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        if (!RestorePlace()) OpenAtDesignSize();
        Closing += OnClosing;
        Draw();
    }

    // Everything drawn from XAML follows the theme by itself; these are the parts drawn here.
    void OnThemeChanged()
    {
        DarkTitleBar.Apply(this);
        Glow(ThemeColors.Current.Main);
        _rowKey = null; // the device list is rebuilt in the new colors
        Draw();
    }

    // Glows are effects, not brushes, so they're handed the theme's color.
    void Glow(Color c) => SentGlow.Color = RingGlow.Color = CastGlow.Color = c;

    void Cog_Click(object sender, RoutedEventArgs e) => SettingsWindow.Open(this);

    async void Connect_Click(object sender, RoutedEventArgs e) => await _caster.ToggleConnectAsync();
    async void Detect_Click(object sender, RoutedEventArgs e) => await _caster.DetectAsync();
    async void Cast_Click(object sender, RoutedEventArgs e) => await _caster.ToggleCastAsync();

    // Right-click Detect shows the hidden devices, caught here because a greyed-out button gets no clicks.
    void Window_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (!new Rect(DetectBtn.RenderSize).Contains(e.GetPosition(DetectBtn)) || !_caster.Idle) return;
        e.Handled = true;
        var aside = _caster.SetAside;
        if (aside.Count == 0)
        {
            NoticeWindow.Tell(this, "NOTHING HIDDEN", "No devices are hidden. Right-click a device in the list to hide it.");
            return;
        }
        var keep = DevicePickerWindow.Ask(this, aside);
        if (keep != null) _caster.BringBack(keep);
    }

    // Closing = Disconnect: the cast ends and the TV goes back to its home screen before the window goes.
    async void OnClosing(object sender, CancelEventArgs e)
    {
        if (_shutdown?.IsCompleted == true) return; // shut down: the close queued below goes straight through
        e.Cancel = true;
        if (_shutdown != null) return; // clicked again while closing: the first close finishes the job
        SavePlace();
        _shutdown = _caster.ShutdownAsync();
        await _shutdown;
        Log.Write("app: closed");
        // Queued: closing again from inside this closing event is refused.
        _ = Dispatcher.InvokeAsync(Close);
    }

    void Draw()
    {
        var c = _caster;
        var ph = c.Phase;
        StatusText.Text = c.Status;
        Block1.Text = c.IsOnline ? "ONLINE" : "OFFLINE";
        Block2.Text = c.Detected ? $"{c.Devices.Count(c.IsAnswering)} FOUND" : $"{c.Devices.Count} SAVED";
        Block3.Text = c.Encoder?.Label ?? "ENCODER";
        Block4.Text = c.VideoLabel;

        ConnectLabel.Text = c.IsOnline ? "DISCONNECT" : "CONNECT";
        ConnectBtn.Background = c.IsOnline ? Main : Second;
        ConnectBtn.IsEnabled = ph is Phase.Offline or Phase.Online or Phase.Casting;
        DetectBtn.IsEnabled = ph == Phase.Online;
        DetectLabel.Text = ph == Phase.Detecting ? "DETECTING" : "DETECT";

        EmptyHint.Visibility = c.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = c.AllHidden ? "ALL YOUR DEVICES ARE HIDDEN" : "NO DEVICES YET";
        EmptyText.Text = c.AllHidden ? "RIGHT-CLICK DETECT TO BRING THEM BACK." : "CLICK CONNECT, THEN DETECT TO FIND YOUR TVS AND ROKUS.";
        ListNote.Text = c.Devices.Count > 4 ? $"+{c.Devices.Count - 4} MORE · SCROLL" : ""; // the list shows 4 at a time

        // This PC's side; each device's own side is on its row.
        NetworkText.Text = !c.IsOnline ? "OFFLINE" : c.PcLink == null ? "NONE" : $"PC {c.PcLink}";

        DrawCastButton();
        DrawList();
        DrawReadout();
        DrawHealth();
    }

    // The health lines: this cast's numbers (kept until the next cast plays), dimmed while waiting (...) or before any cast (IDLE).
    void DrawHealth()
    {
        var c = _caster;
        var from = c.HealthFrom;
        string missing = from != null && c.Phase == Phase.Casting ? "..." : "IDLE";
        static string Pct(double? p) => p is double v ? v.ToString("0", CultureInfo.InvariantCulture) + "%" : null;
        HealthLine(DroppedText, from == null ? null : c.DroppedFrames.ToString("N0", CultureInfo.InvariantCulture), missing);
        HealthLine(CpuText, Pct(c.CpuLoad), missing);
        HealthLine(GpuText, Pct(c.EncoderLoad), missing);
    }

    void HealthLine(TextBlock t, string value, string missing)
    {
        t.Text = value ?? missing;
        t.Foreground = value != null ? Soft : DimText;
    }

    // "WI-FI" or "WIRED" as the device says; otherwise why it isn't known yet.
    string LinkText(Device d)
    {
        var c = _caster;
        if (c.LinkOf(d) is { } link) return link;
        if (c.LinkAsked(d)) return d.Kind == DeviceKind.Dlna ? "DOESN'T SAY" : "DIDN'T ANSWER";
        return c.IsOnline ? "CHECKING..." : "CONNECT TO CHECK";
    }

    // A Roku's player after its address once it has answered (" · PC2TV" or " · DISPLAYHUB"); nothing with neither.
    string PlayerText(Device d) => _caster.LinkOf(d) == null ? "" : _caster.PlayerOf(d) switch
    {
        RokuApp.Ours => " · PC2TV",
        RokuApp.DisplayHub => " · DISPLAYHUB",
        _ => "",
    };

    // The big button names the next step (Connect, Detect, Select, Cast); only Cast and Casting can be pressed.
    void DrawCastButton()
    {
        var c = _caster;
        var step = c.Step;
        bool casting = step == CastStep.Casting, ready = step == CastStep.Cast;
        CastBtn.IsEnabled = casting || ready;
        CastBtn.Cursor = CastBtn.IsEnabled ? Cursors.Hand : Cursors.Arrow;
        CastFill.Opacity = casting ? 1 : ready ? 0.08 : 0;
        CastGlow.Opacity = casting ? 0.45 : 0;
        CastRing.Stroke = casting || ready ? Main : Dim;
        var (title, sub, size) = step switch
        {
            CastStep.Casting => ("CASTING", "CLICK TO END", 34.0),
            CastStep.Cast => ("CAST", "TO " + c.Picked.Name.ToUpperInvariant(), 46.0),
            CastStep.ConnectFirst => ("CONNECT", "FIRST", 30.0),
            CastStep.DetectFirst => ("DETECT", "FIRST", 30.0),
            CastStep.SelectDevice => ("SELECT", "A DEVICE", 30.0),
            _ => (c.Phase switch { Phase.Connecting => "CONNECTING", Phase.Detecting => "DETECTING", Phase.Starting => "STARTING", _ => "ENDING" }, "", 26.0),
        };
        CastTitle.Text = title;
        CastTitle.FontSize = size;
        CastTitle.Foreground = casting ? Ink : ready ? Main : Second;
        CastSub.Text = sub;
        CastSub.FontSize = ready ? 18 : 13; // the device's name reads bigger than the other small lines
        CastSub.Visibility = sub.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CastSub.Foreground = casting ? Ink : Second;
    }

    // Rebuilt only when the list, a connection, the pick or the bookmark changes, not on every readout tick.
    void DrawList()
    {
        var c = _caster;
        var lit = c.PickClicked ? c.Picked?.Id : null; // only a device you clicked is lit
        var key = string.Join("|", c.Devices.Select(d => d.Id + d.Name + d.Ip + LinkText(d) + PlayerText(d))) + "#" + lit + "#" + c.Default?.Id;
        if (key == _rowKey) return;
        _rowKey = key;
        DeviceList.Children.Clear();
        foreach (var d in c.Devices) DeviceList.Children.Add(Row(d, d.Id == lit, d.Id == c.Default?.Id));
    }

    // Plain text rows (name and kind, then link, IP and a Roku's player); hover or a click tints the row, the default's bookmark is filled.
    FrameworkElement Row(Device d, bool picked, bool isDefault)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label($"{d.Name.ToUpperInvariant()} · {d.KindLabel}", 20, Soft));
        var link = Label("", 15, Second);
        link.Inlines.Add(new Run(LinkText(d)) { Foreground = _caster.LinkOf(d) != null ? Second : DimText });
        link.Inlines.Add(new Run(" · " + d.Ip + PlayerText(d)));
        text.Children.Add(link);
        g.Children.Add(text);

        var icon = new Path
        {
            Data = (Geometry)FindResource("IcoBookmark"), Stroke = isDefault ? Main : Second, Fill = isDefault ? Main : null, StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var mark = new Button
        {
            Style = (Style)FindResource("Bare"), Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Content = new Viewbox { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Child = new Canvas { Width = 24, Height = 24, Children = { icon } } },
            ToolTip = isDefault ? "Default device (click to clear)" : "Make this the default",
        };
        mark.Click += (_, _) => _caster.ToggleDefault(d);
        Grid.SetColumn(mark, 1);
        g.Children.Add(mark);

        Brush rest = picked ? Tint(0.09) : Brushes.Transparent, hover = Tint(0.18);
        var row = new Border
        {
            Width = 246, CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 4, 0, 5), Margin = new Thickness(0, 0, 0, 6),
            Background = rest, Cursor = Cursors.Hand, Child = g,
        };
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = rest;
        row.MouseLeftButtonUp += (_, _) => _caster.Pick(d);
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; if (_caster.Idle && DeviceActionWindow.Ask(this, d)) _caster.Hide(d); }; // hide it from the list
        row.ToolTip = "Right-click to hide this device";
        return row;
    }

    // The theme's main color, see-through.
    static Brush Tint(double opacity) => (Brush)new SolidColorBrush(ThemeColors.Current.Main) { Opacity = opacity }.GetAsFrozen();

    TextBlock Label(string text, double size, Brush fg) =>
        new() { Text = text, FontSize = size, Foreground = fg, Style = (Style)FindResource("Label"), TextTrimming = TextTrimming.CharacterEllipsis };
}
