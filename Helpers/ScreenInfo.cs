using System.Runtime.InteropServices;

namespace PCCast2TV.Helpers;

// The cast monitor's refresh rate (by Windows' name for it, like \\.\DISPLAY2), and the size a cast sends.
public static class ScreenInfo
{
    const int CurrentSettings = -1, CdsFullscreen = 4;
    const int DmBitsPerPel = 0x40000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    static string _switched; // the monitor SwitchTo60 changed, so PutRateBack undoes that one

    // What the cast sends: the screen's own size, or shrunk to fit 1920x1080 (1080p Rokus can't play more); even for video.
    public static (int Width, int Height) SentSize(int w, int h)
    {
        double fit = Math.Min(1, Math.Min(1920.0 / w, 1080.0 / h));
        return ((int)Math.Round(w * fit) & ~1, (int)Math.Round(h * fit) & ~1);
    }

    // How many times a second the monitor redraws.
    public static int Hz(string device) => Current(device, out var dm) ? dm.dmDisplayFrequency : 0;

    // TVs show 60 frames a second, so only a 60, 120 or 240 Hz screen lines up with them evenly.
    public static bool LinesUpWithTv(int hz) => hz > 1 && hz % 60 == 0;

    // Switches the monitor to 60 Hz for the cast, never saved: PutRateBack undoes it, and Windows does if the app is killed.
    public static string SwitchTo60(string device)
    {
        if (!Current(device, out var dm)) return "could not read the screen's settings, left alone";
        int hz = dm.dmDisplayFrequency;
        if (!Offers60(device, dm)) return $"{hz} Hz and 60 Hz isn't offered at {dm.dmPelsWidth}x{dm.dmPelsHeight}, left alone";
        dm.dmDisplayFrequency = 60;
        dm.dmFields = DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CdsFullscreen, IntPtr.Zero);
        if (r != 0) return $"{hz} Hz, Windows refused the switch to 60 (code {r})";
        _switched = device;
        return $"switched from {hz} to 60 Hz for the cast";
    }

    // Back to the user's own rate; does nothing unless SwitchTo60 changed it.
    public static void PutRateBack()
    {
        if (_switched is not { } device) return;
        _switched = null;
        int r = ChangeDisplaySettingsEx(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Write(r == 0 ? $"screen: back to {Hz(device)} Hz" : $"screen: could not put the rate back (code {r})");
    }

    static bool Offers60(string device, DEVMODE now)
    {
        for (int i = 0; ; i++)
        {
            var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device, i, ref m)) return false;
            if (m.dmDisplayFrequency == 60 && m.dmPelsWidth == now.dmPelsWidth && m.dmPelsHeight == now.dmPelsHeight && m.dmBitsPerPel == now.dmBitsPerPel) return true;
        }
    }

    static bool Current(string device, out DEVMODE dm)
    {
        dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(device, CurrentSettings, ref dm);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr devMode, IntPtr hwnd, int flags, IntPtr param);
}
