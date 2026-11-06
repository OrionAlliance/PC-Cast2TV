using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// Right-click a device to hide it from the list; display only, it hands back the choice.
public partial class DeviceActionWindow : Window
{
    DeviceActionWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    // False when Cancel, Esc or the X closed it (Cancel is the popup's cancel button, so it closes it by itself).
    public static bool Ask(Window owner, Device d)
    {
        var w = new DeviceActionWindow { Owner = owner };
        w.Heading.Text = d.Name.ToUpperInvariant();
        return w.ShowDialog() == true;
    }

    void Hide_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
