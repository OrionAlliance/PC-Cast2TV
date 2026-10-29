using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// A Roku with no player: how to add DisplayHUB; ADD DISPLAYHUB opens its store page, the adding OK stays on the remote.
public partial class RokuAppWindow : Window
{
    Device _roku;

    RokuAppWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    // waitForApp asks the Roku until a player shows up, then closes the popup; OK, Enter, Esc and closing it all stop the asking.
    public static void Tell(Window owner, Device roku, Func<CancellationToken, Task> waitForApp)
    {
        using var stop = new CancellationTokenSource();
        var w = new RokuAppWindow { Owner = owner, _roku = roku };
        w.TitleText.Text = $"{roku.Name.ToUpperInvariant()} NEEDS DISPLAYHUB";
        w.Closed += (_, _) => stop.Cancel();
        w.Loaded += async (_, _) =>
        {
            try
            {
                await waitForApp(stop.Token);
                w.Close();
            }
            catch (OperationCanceledException) { } // closed before a player showed up
        };
        w.ShowDialog();
    }

    async void AddDisplayHub_Click(object sender, RoutedEventArgs e)
    {
        AddHubBtn.IsEnabled = false;
        HubResult.Text = await RokuPlayer.OpenDisplayHubPageAsync(_roku)
            ? $"DisplayHUB's page is open on {_roku.Name.ToUpperInvariant()}. Press OK on the Roku remote to add it, then cast again."
            : "The Roku didn't open the page (its \"Control by mobile apps\" setting may be off). On the Roku, open Search, type DisplayHUB and add it.";
        HubResult.Visibility = Visibility.Visible;
        AddHubBtn.IsEnabled = true;
    }
}
