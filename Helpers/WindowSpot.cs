using System.Windows;

namespace PCCast2TV.Helpers;

// Where the main window opens: its last spot if that still fits, else the design size, each spot kept with its layout's size.
public static class WindowSpot
{
    // The spot it was last closed at; null when there's none, it was for another layout, or it's off the screens attached now.
    public static WindowPlace Saved(double designWidth, double designHeight)
    {
        var p = AppSettings.Window;
        if (p == null) return null;
        if (p.DesignWidth != designWidth || p.DesignHeight != designHeight)
        {
            Log.Write("window: saved size was for the old layout, opening at the new size");
            return null;
        }
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        screens.Inflate(16, 16); // a window against a screen edge sits a few pixels past it
        if (p.Width < 400 || p.Height < 260 || !screens.Contains(new Rect(p.Left, p.Top, p.Width, p.Height)))
        {
            Log.Write("window: saved spot doesn't fit these screens, opening at the design size");
            return null;
        }
        return p;
    }

    // How much the design size shrinks to fit this screen (1 = full size).
    public static double DesignScale(double designWidth, double designHeight)
    {
        var area = SystemParameters.WorkArea;
        return Math.Min(1, Math.Min((area.Width - 40) / designWidth, (area.Height - 80) / designHeight));
    }

    public static void Keep(Rect r, bool maximized, double designWidth, double designHeight) =>
        AppSettings.SetWindow(new WindowPlace(r.Left, r.Top, r.Width, r.Height, maximized, designWidth, designHeight));
}
