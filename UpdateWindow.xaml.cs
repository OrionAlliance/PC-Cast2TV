using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The update offer: UPDATE downloads the new exe and swaps it in; NOT NOW (or Esc, or the X) leaves this copy as it is.
// Display only: AppUpdater does the work.
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
        Log.Write($"update: UPDATE pressed (v{_update.Installed} to v{_update.Latest})");
        _busy = true;
        ButtonRow.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Progress.Visibility = Visibility.Visible;
        StatusText.Text = "DOWNLOADING...";
        try
        {
            string downloaded = await AppUpdater.DownloadAsync(_update, new Progress<string>(s => StatusText.Text = s));
            StatusText.Text = "INSTALLING...";
            AppUpdater.Swap(downloaded);
            SwappedIn = true;
            _busy = false;
            Close();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException or UnauthorizedAccessException)
        {
            _busy = false;
            Log.Write($"update: didn't finish: {ex.GetType().Name}: {ex.Message}");
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Text = "COULDN'T UPDATE: " + ex.Message + "\nNothing changed. This copy still works.";
            UpdateLabel.Text = "TRY AGAIN";
            ButtonRow.Visibility = Visibility.Visible;
        }
    }
}
