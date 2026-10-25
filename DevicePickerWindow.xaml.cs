using System.Windows;
using System.Windows.Controls;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The hidden and forgotten devices, ticked (right-click Detect). Display only: it hands back the ones still ticked.
public partial class DevicePickerWindow : Window
{
    readonly List<(Device Device, CheckBox Box)> _rows = new();

    DevicePickerWindow(string heading, string action, IReadOnlyList<Device> devices, Func<Device, bool> startChecked)
    {
        InitializeComponent();
        Heading.Text = heading;
        OkLabel.Text = action;
        Empty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var d in devices)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Name.ToUpperInvariant(), FontSize = 18, Style = (Style)FindResource("Label"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = d.KindLabel, FontSize = 12, Style = (Style)FindResource("Label") });
            var box = new CheckBox { Style = (Style)FindResource("PickRow"), Content = text, IsChecked = startChecked(d) };
            Rows.Children.Add(box);
            _rows.Add((d, box));
        }
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    // The checked devices, or null when Cancel or the X closed it.
    public static List<Device> Ask(Window owner, string heading, string action, IReadOnlyList<Device> devices, Func<Device, bool> startChecked)
    {
        var w = new DevicePickerWindow(heading, action, devices, startChecked) { Owner = owner };
        return w.ShowDialog() == true ? w._rows.Where(r => r.Box.IsChecked == true).Select(r => r.Device).ToList() : null;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
