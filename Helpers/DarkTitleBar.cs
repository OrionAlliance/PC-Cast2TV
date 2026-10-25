using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PCCast2TV.Helpers;

// Paints the normal Windows title bar to match the app, keeping its own minimize, close and snap.
public static class DarkTitleBar
{
    // The app's black with the theme's main color for the title text.
    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        int on = 1, cap = ToBgr(Color.FromRgb(0x03, 0x07, 0x0B)), txt = ToBgr(ThemeColors.Current.Main);
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));   // dark mode (Windows 10 and 11)
        DwmSetWindowAttribute(hwnd, 35, ref cap, sizeof(int));  // caption color (Windows 11)
        DwmSetWindowAttribute(hwnd, 36, ref txt, sizeof(int));  // caption text color (Windows 11)
    }

    static int ToBgr(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
