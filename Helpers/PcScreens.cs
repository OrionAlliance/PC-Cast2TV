using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace PCCast2TV.Helpers;

// One monitor on the desktop: Windows' name for it (\\.\DISPLAY2), a lasting key for the saved default, its own name, size and card.
public sealed record PcScreen(string Device, string Key, string Name, int Width, int Height, bool Main, string Card)
{
    public string LogName => $"{Name} {Width}x{Height}" + (Main ? " (main)" : "");
}

// The monitors on the desktop, left to right, each named the way it names itself (the names Windows Settings shows).
public static class PcScreens
{
    public static List<PcScreen> All()
    {
        var names = MonitorNames();
        var found = new List<(int X, PcScreen Screen)>();
        try
        {
            DXGI.CreateDXGIFactory1(out IDXGIFactory1 factory).CheckError();
            using (factory)
                for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
                    using (adapter)
                        for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                            using (output)
                            {
                                var d = output.Description;
                                if (!d.AttachedToDesktop) continue;
                                var r = d.DesktopCoordinates;
                                names.TryGetValue(d.DeviceName, out var n);
                                found.Add((r.Left, new PcScreen(d.DeviceName, n.Key ?? d.DeviceName, n.Name, r.Right - r.Left, r.Bottom - r.Top,
                                    r.Left == 0 && r.Top == 0, adapter.Description1.Description)));
                            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) { Log.Write("screens: could not list them: " + ex.Message); }
        var list = found.OrderBy(f => f.X).Select(f => f.Screen).ToList();
        // A monitor that gives no name (some laptop panels and virtual ones) is numbered; twins get a number too.
        var raw = list.Select(s => s.Name?.Trim() ?? "").ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string name = raw[i].Length == 0 ? $"MONITOR {i + 1}" : raw[i].ToUpperInvariant();
            if (raw[i].Length > 0 && raw.Count(n => string.Equals(n, raw[i], StringComparison.OrdinalIgnoreCase)) > 1) name += $" {i + 1}";
            list[i] = list[i] with { Name = name };
        }
        return list;
    }

    // Windows' display paths: each monitor's own name and its lasting device path, by Windows' name for it.
    static Dictionary<string, (string Name, string Key)> MonitorNames()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(OnlyActive, out uint pathCount, out uint modeCount) != 0) return map;
        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(OnlyActive, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return map; // changed mid-ask: numbered instead
        for (int i = 0; i < pathCount; i++)
        {
            var source = new SourceName { Header = new Header { Type = GetSourceName, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = paths[i].SourceAdapter, Id = paths[i].SourceId } };
            var target = new TargetName { Header = new Header { Type = GetTargetName, Size = (uint)Marshal.SizeOf<TargetName>(), Adapter = paths[i].TargetAdapter, Id = paths[i].TargetId } };
            if (DisplayConfigGetDeviceInfo(ref source) != 0 || DisplayConfigGetDeviceInfo(ref target) != 0) continue;
            map.TryAdd(source.GdiName, (target.FriendlyName, string.IsNullOrEmpty(target.DevicePath) ? null : target.DevicePath));
        }
        return map;
    }

    const uint OnlyActive = 2, GetSourceName = 1, GetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo
    {
        public Luid SourceAdapter;
        public uint SourceId, SourceMode, SourceFlags;
        public Luid TargetAdapter;
        public uint TargetId, TargetMode, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLines;
        public int TargetAvailable;
        public uint TargetFlags, Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct ModeInfo { public uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TargetName
    {
        public Header Header;
        public uint Flags, Technology;
        public ushort EdidMaker, EdidProduct;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref SourceName info);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref TargetName info);
}
