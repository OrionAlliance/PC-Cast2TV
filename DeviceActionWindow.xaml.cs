using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

public enum DeviceAction { Hide, Forget }

// Right-click a device: hide it from the list or forget it from detection. Display only: it hands back the choice.
public partial class DeviceActionWindow : Window
{
    DeviceAction? _choice;

    DeviceActionWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    // Null when Cancel, Esc or the X closed it (Cancel is the popup's cancel button, so it closes it by itself).
    public static DeviceAction? Ask(Window owner, Device d)
    {
        var w = new DeviceActionWindow { Owner = owner };
        w.Heading.Text = d.Name.ToUpperInvariant();
        w.ShowDialog();
        return w._choice;
    }

    void Hide_Click(object sender, RoutedEventArgs e) => Pick(DeviceAction.Hide);
    void Forget_Click(object sender, RoutedEventArgs e) => Pick(DeviceAction.Forget);

    void Pick(DeviceAction choice)
    {
        _choice = choice;
        DialogResult = true;
    }
}
