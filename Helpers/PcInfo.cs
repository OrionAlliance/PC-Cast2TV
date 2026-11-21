using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCCast2TV.Helpers;

// Lines about the PC at start, so logs from different PCs compare: processor, graphics cards and drivers, battery, security software.
public static class PcInfo
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static string Describe()
    {
        var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string;
        var cards = new List<string>();
        var named = new HashSet<string>();
        using (var cls = Registry.LocalMachine.OpenSubKey(DisplayClass))
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? Enumerable.Empty<string>())
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string name && named.Add(name)) cards.Add(name + Driver(k));
            }
        return $"{cpu?.Trim() ?? "unknown processor"} ({Environment.ProcessorCount} threads), Windows {Environment.OSVersion.Version}, " +
               $"graphics: {(cards.Count == 0 ? "unknown" : string.Join(" + ", cards))}";
    }

    // " (driver 32.0.15.6094 from 2024-08-14)": an old driver is a usual suspect when the video encoder misbehaves.
    static string Driver(RegistryKey k)
    {
        var version = (k.GetValue("DriverVersion") as string)?.Trim();
        var date = (k.GetValue("DriverDate") as string)?.Trim(); // Windows keeps it as month-day-year
        if (DateTime.TryParseExact(date, "M-d-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            date = d.ToString("yyyy-MM-dd");
        if (string.IsNullOrEmpty(version)) return "";
        return $" (driver {version}{(string.IsNullOrEmpty(date) ? "" : " from " + date)})";
    }

    // "laptop, on battery at 64%": on battery, Windows may slow the graphics card down to save power.
    public static string Power()
    {
        if (!GetSystemPowerStatus(out var p)) return "power: unknown";
        if (p.BatteryFlag == 255) return "battery status unknown (Windows couldn't read it)"; // 255 has the 128 bit too
        if ((p.BatteryFlag & 128) != 0) return "desktop, no battery";
        string level = p.BatteryLifePercent <= 100 ? $" at {p.BatteryLifePercent}%" : "";
        return p.ACLineStatus switch
        {
            0 => "laptop, on battery" + level,
            1 => "laptop, plugged in, battery" + level,
            _ => "has a battery" + level + ", power cord unknown",
        };
    }

    // "Windows Defender (antivirus, on)": security software can block casting; Security Center lists antivirus and other companies' firewalls.
    public static string Security()
    {
        try
        {
            var found = Products("AntiVirusProduct", "antivirus").Concat(Products("FirewallProduct", "firewall")).ToList();
            return found.Count == 0 ? "none listed" : string.Join(", ", found);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "couldn't read it (" + ex.GetType().Name + ")";
        }
    }

    static IEnumerable<string> Products(string kind, string label)
    {
        using var search = new ManagementObjectSearcher(@"root\SecurityCenter2", $"SELECT displayName, productState FROM {kind}");
        using var results = search.Get();
        var list = new List<string>();
        foreach (ManagementObject o in results)
            using (o)
            {
                var name = (o["displayName"] as string)?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var state = Convert.ToUInt32(o["productState"] ?? 0u);
                bool on = ((state >> 12) & 0xF) == 1; // the state's second byte is 0x1_ when it's switched on
                list.Add($"{name} ({label}, {(on ? "on" : "off")})");
            }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out PowerStatus status);
}
