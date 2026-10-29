using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

public partial class App : Application
{
    // Anything that slips through gets written down; the window opens here so a second copy can close before it has one.
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!OneCopy.Take())
        {
            Log.Write("app: already open, brought that one to the front");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, a) => Log.Write("crash: " + a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("crash: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => Log.Write("unobserved task error: " + a.Exception);
        Log.Write($"app: start, version {typeof(App).Assembly.GetName().Version}");
        Log.Write("pc: " + PcInfo.Describe());
        ThemeColors.Apply(AppSettings.Theme); // before the window opens, so it never flashes the default colors
        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        OneCopy.Release();
        base.OnExit(e);
    }
}
