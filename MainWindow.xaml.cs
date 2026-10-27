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

    // Right-click Detect: the hidden and forgotten devices, ticked; untick one to put it back on the list. Caught here
    // because a greyed-out button gets no clicks of its own.
    void Window_RightClick(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DetectBtn);
        if (p.X < 0 || p.Y < 0 || p.X > DetectBtn.ActualWidth || p.Y > DetectBtn.ActualHeight || !_caster.CanManageDevices) return;
        e.Handled = true;
        var aside = _caster.SetAside;
        if (aside.Count == 0)
        {
            NoticeWindow.Tell(this, "NOTHING HIDDEN", "No devices are hidden or forgotten. Right-click a device in the list to hide or forget it.");
            return;
        }
        var keep = DevicePickerWindow.Ask(this, "HIDDEN AND FORGOTTEN DEVICES · UNTICK ONE TO PUT IT BACK ON THE LIST", "DONE", aside, _ => true);
        if (keep != null) _caster.BringBack(keep);
    }

    // Right-click a device: hide it from the list or forget it from detection.
    void Row_RightClick(Device d)
    {
        if (!_caster.CanManageDevices) return;
        switch (DeviceActionWindow.Ask(this, d))
        {
            case DeviceAction.Hide: _caster.Hide(d); break;
            case DeviceAction.Forget: _caster.Forget(d); break;
        }
    }

    // Closing = Disconnect: the cast ends and the TV goes back to its home screen before the window goes.
    async void OnClosing(object sender, CancelEventArgs e)
    {
        Closing -= OnClosing; // runs once; the close queued below then goes straight through
        e.Cancel = true;
        SavePlace();
        await Task.WhenAny(_caster.ShutdownAsync(), Task.Delay(TimeSpan.FromSeconds(8)));
        Log.Write("app: closed");
        // Queued, not called here: with nothing to shut down this line runs while Windows is still
        // inside this closing event, and closing again from in there is refused.
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
        ListNote.Text = c.Devices.Count > 4 ? $"+{c.Devices.Count - 4} MORE · SCROLL" : ""; // the list shows 4 at a time

        // This PC's side; each device's own side is on its row.
        NetworkText.Text = !c.IsOnline ? "OFFLINE" : c.PcLink == null ? "NONE" : $"PC {c.PcLink}";

        DrawCastButton();
        DrawList();
        DrawReadout();
        DrawHealth();
    }

    // The health lines: the cast's numbers while it plays, and after it ends they stay up until the next cast plays.
    // Dimmed while waiting on a first reading ("...") or before any cast (IDLE).
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

    // Plain text rows, all alike: name and kind, and under it how it's connected, the IP and a Roku's player. Hover or a click lays
    // a faint tint behind the row; the default's bookmark is filled.
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
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; Row_RightClick(d); };
        row.ToolTip = "Right-click to hide or forget this device";
        return row;
    }

    // The theme's main color, see-through.
    static Brush Tint(double opacity)
    {
        var b = new SolidColorBrush(ThemeColors.Current.Main) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    TextBlock Label(string text, double size, Brush fg) =>
        new() { Text = text, FontSize = size, Foreground = fg, Style = (Style)FindResource("Label"), TextTrimming = TextTrimming.CharacterEllipsis };
}
