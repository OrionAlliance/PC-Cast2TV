using Microsoft.Win32;

namespace PCCast2TV.Helpers;

// One line naming the processor and graphics cards, written at start so logs from different PCs can be compared.
public static class PcInfo
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static string Describe()
    {
        var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string;
        var cards = new List<string>();
        using (var cls = Registry.LocalMachine.OpenSubKey(DisplayClass))
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? Enumerable.Empty<string>())
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string name && !cards.Contains(name)) cards.Add(name);
            }
        return $"{cpu?.Trim() ?? "unknown processor"} ({Environment.ProcessorCount} threads), Windows {Environment.OSVersion.Version}, " +
               $"graphics: {(cards.Count == 0 ? "unknown" : string.Join(" + ", cards))}";
    }
}
