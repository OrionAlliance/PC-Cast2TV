using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// A Roku without our app: how to add it; ADD PC CAST2TV opens its store page, the adding OK stays on the remote.
public partial class RokuAppWindow : Window
{
    Device _roku;

    RokuAppWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    // waitForApp asks the Roku until our app shows up, then closes the popup; OK, Enter, Esc and closing it all stop the asking.
    public static void Tell(Window owner, Device roku, Func<CancellationToken, Task> waitForApp)
    {
        using var stop = new CancellationTokenSource();
        var w = new RokuAppWindow { Owner = owner, _roku = roku };
        w.TitleText.Text = $"{roku.Name.ToUpperInvariant()} NEEDS THE PC CAST2TV APP";
        w.Closed += (_, _) => stop.Cancel();
        w.Loaded += async (_, _) =>
        {
            try
            {
                await waitForApp(stop.Token);
                w.Close();
            }
            catch (OperationCanceledException) { } // closed before our app showed up
        };
        w.ShowDialog();
    }

    async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        AddAppBtn.IsEnabled = false;
        AppResult.Text = await RokuPlayer.OpenStorePageAsync(_roku)
            ? $"PC Cast2TV's page is open on {_roku.Name.ToUpperInvariant()}. Press OK on the Roku remote to add it, then cast again."
            : "The Roku didn't open the page (its \"Control by mobile apps\" setting may be off). On the Roku, open Search, type PC Cast2TV and add it.";
        AppResult.Visibility = Visibility.Visible;
        AddAppBtn.IsEnabled = true;
    }
}
