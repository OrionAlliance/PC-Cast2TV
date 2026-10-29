using System.Windows;
using System.Windows.Controls;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The hidden devices, ticked (right-click Detect); display only, it hands back the ones still ticked.
public partial class DevicePickerWindow : Window
{
    readonly List<(Device Device, CheckBox Box)> _rows = new();

    DevicePickerWindow(IReadOnlyList<Device> devices)
    {
        InitializeComponent();
        foreach (var d in devices)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Name.ToUpperInvariant(), FontSize = 18, Style = (Style)FindResource("Label"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = d.KindLabel, FontSize = 12, Style = (Style)FindResource("Label") });
            var box = new CheckBox { Style = (Style)FindResource("PickRow"), Content = text, IsChecked = true };
            Rows.Children.Add(box);
            _rows.Add((d, box));
        }
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    // The checked devices, or null when Cancel or the X closed it.
    public static List<Device> Ask(Window owner, IReadOnlyList<Device> devices)
    {
        var w = new DevicePickerWindow(devices) { Owner = owner };
        return w.ShowDialog() == true ? w._rows.Where(r => r.Box.IsChecked == true).Select(r => r.Device).ToList() : null;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
