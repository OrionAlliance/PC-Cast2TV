using System.Runtime.InteropServices;

namespace PCCast2TV.Helpers;

public static class ScreenInfo
{
    const int CurrentSettings = -1, CdsFullscreen = 4;
    const int DmBitsPerPel = 0x40000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    static bool _switched;

    // The main screen's real pixel size, whatever the Windows scaling is set to.
    public static (int Width, int Height) Size => Current(out var dm) ? (dm.dmPelsWidth, dm.dmPelsHeight) : (1920, 1080);

    // What the cast sends: the screen's own size, or shrunk to fit 1920x1080 (1080p Rokus can't play more); even for video.
    public static (int Width, int Height) SentSize(int w, int h)
    {
        double fit = Math.Min(1, Math.Min(1920.0 / w, 1080.0 / h));
        return ((int)Math.Round(w * fit) & ~1, (int)Math.Round(h * fit) & ~1);
    }

    // How many times a second the main screen redraws.
    public static int Hz => Current(out var dm) ? dm.dmDisplayFrequency : 0;

    // TVs show 60 frames a second, so only a 60, 120 or 240 Hz screen lines up with them evenly.
    public static bool LinesUpWithTv(int hz) => hz > 1 && hz % 60 == 0;

    // Switches the main screen to 60 Hz for the cast. The switch is temporary, never saved as the user's setting:
    // PutRateBack undoes it, and Windows undoes it by itself if the app is killed.
    public static string SwitchTo60()
    {
        if (!Current(out var dm)) return "could not read the screen's settings, left alone";
        int hz = dm.dmDisplayFrequency;
        if (!Offers60(dm)) return $"{hz} Hz and 60 Hz isn't offered at {dm.dmPelsWidth}x{dm.dmPelsHeight}, left alone";
        dm.dmDisplayFrequency = 60;
        dm.dmFields = DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        int r = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CdsFullscreen, IntPtr.Zero);
        if (r != 0) return $"{hz} Hz, Windows refused the switch to 60 (code {r})";
        _switched = true;
        return $"switched from {hz} to 60 Hz for the cast";
    }

    // Back to the user's own rate; does nothing unless SwitchTo60 changed it.
    public static void PutRateBack()
    {
        if (!_switched) return;
        _switched = false;
        int r = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Write(r == 0 ? $"screen: back to {Hz} Hz" : $"screen: could not put the rate back (code {r})");
    }

    static bool Offers60(DEVMODE now)
    {
        for (int i = 0; ; i++)
        {
            var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(null, i, ref m)) return false;
            if (m.dmDisplayFrequency == 60 && m.dmPelsWidth == now.dmPelsWidth && m.dmPelsHeight == now.dmPelsHeight && m.dmBitsPerPel == now.dmBitsPerPel) return true;
        }
    }

    static bool Current(out DEVMODE dm)
    {
        dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(null, CurrentSettings, ref dm);
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
