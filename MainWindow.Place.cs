using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The window's spot and size, as WindowSpot decides: back where it was last closed, or at the design size.
public partial class MainWindow
{
    bool RestorePlace()
    {
        var p = WindowSpot.Saved(Design.Width, Design.Height);
        if (p == null) return false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized) WindowState = WindowState.Maximized;
        return true;
    }

    // Opens at the design size (smaller on a small screen), then resizes freely with everything scaled.
    void OpenAtDesignSize()
    {
        double fit = WindowSpot.DesignScale(Design.Width, Design.Height);
        Root.Width = Design.Width * fit;
        Root.Height = Design.Height * fit;
        ContentRendered += (_, _) =>
        {
            SizeToContent = SizeToContent.Manual;
            Root.Width = Root.Height = double.NaN;
        };
    }

    void SavePlace()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        WindowSpot.Keep(r, WindowState == WindowState.Maximized, Design.Width, Design.Height);
    }
}
