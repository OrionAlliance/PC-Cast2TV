using System.Windows;
using System.Windows.Threading;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// The window's spot and size, as WindowSpot decides: back where it was last closed, or at the design size; each look keeps its own spot.
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
            if (WindowLook.Simple) return; // switched before it finished opening; the simple look sizes itself
            SizeToContent = SizeToContent.Manual;
            Root.Width = Root.Height = double.NaN;
        };
    }

    // The simple look opens where it was last closed (if that's still on a screen), sized to its cards.
    void OpenSimple()
    {
        ShowSimple();
        var p = AppSettings.SimpleWindow;
        if (p == null || !WindowSpot.OnScreens(new Rect(p.Left, p.Top, p.Width, p.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
    }

    // A pick in Settings: the look being left keeps its spot, the other takes over in the same place, and a cast keeps going.
    void ApplyLook()
    {
        SaveSpot(!WindowLook.Simple);
        if (WindowLook.Simple) ShowSimple();
        else ShowDial();
        Draw();
    }

    // Sized to its cards: it grows as devices are found and can be moved, not stretched.
    void ShowSimple()
    {
        Root.Visibility = Visibility.Collapsed;
        Simple.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.CanMinimize;
        SizeToContent = SizeToContent.WidthAndHeight;
    }

    // Back at the dial's last size (or the design size); it stretches again with everything scaled.
    void ShowDial()
    {
        Simple.Visibility = Visibility.Collapsed;
        Root.Visibility = Visibility.Visible;
        ResizeMode = ResizeMode.CanResize;
        if (WindowSpot.Saved(Design.Width, Design.Height) is { } p)
        {
            SizeToContent = SizeToContent.Manual;
            Width = p.Width;
            Height = p.Height;
            Root.Width = Root.Height = double.NaN;
            return;
        }
        double fit = WindowSpot.DesignScale(Design.Width, Design.Height);
        Root.Width = Design.Width * fit;
        Root.Height = Design.Height * fit;
        SizeToContent = SizeToContent.WidthAndHeight;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (WindowLook.Simple) return;
            SizeToContent = SizeToContent.Manual;
            Root.Width = Root.Height = double.NaN;
        }, DispatcherPriority.Background);
    }

    void SavePlace() => SaveSpot(WindowLook.Simple);

    void SaveSpot(bool simple)
    {
        if (simple)
        {
            AppSettings.SetSimpleWindow(new WindowPlace(Left, Top, ActualWidth, ActualHeight, false));
            return;
        }
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        WindowSpot.Keep(r, WindowState == WindowState.Maximized, Design.Width, Design.Height);
    }
}
