using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The update offer, display only: UPDATE hands the work to AppUpdater; NOT NOW, Esc or the X leave this copy as it is.
public partial class UpdateWindow : Window
{
    readonly AppUpdater.Update _update;
    bool _busy;

    // True once the new exe is in place; the main window then restarts on it.
    public bool SwappedIn { get; private set; }

    UpdateWindow(AppUpdater.Update update)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _update = update;
        BodyText.Text = $"A newer version of PC Cast2TV is ready.\n\nThis copy: v{update.Installed}\nNew version: v{update.Latest}\n\n" +
                        "It downloads from the app's GitHub page, then the app restarts on the new version. Your devices and settings stay as they are.";
        Closing += (_, e) => e.Cancel = _busy; // nothing closes it mid-download
    }

    // True when the new version is in place and this copy should restart on it.
    public static bool Offer(Window owner, AppUpdater.Update update)
    {
        var w = new UpdateWindow(update) { Owner = owner };
        w.ShowDialog();
        return w.SwappedIn;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        ButtonRow.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Progress.Visibility = Visibility.Visible;
        StatusText.Text = "DOWNLOADING...";
        string why = await AppUpdater.InstallAsync(_update, new Progress<string>(s => StatusText.Text = s));
        _busy = false;
        if (why == null)
        {
            SwappedIn = true;
            Close();
            return;
        }
        Progress.Visibility = Visibility.Collapsed;
        StatusText.Text = "COULDN'T UPDATE: " + why + "\nNothing changed. This copy still works.";
        UpdateLabel.Text = "TRY AGAIN";
        ButtonRow.Visibility = Visibility.Visible;
    }
}
