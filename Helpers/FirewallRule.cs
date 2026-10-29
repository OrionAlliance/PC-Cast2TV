using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace PCCast2TV.Helpers;

// The app's own firewall rule, asked for once with an admin prompt: private networks, the app's ports, same-network devices only.
public static class FirewallRule
{
    const string Name = "PC Cast2TV";

    static string Exe => Environment.ProcessPath;
    // Windows' own netsh, by its full path.
    static string NetshExe => Path.Combine(Environment.SystemDirectory, "netsh.exe");

    // Reading rules needs no admin rights. A rule counts only if it points at this copy of the app.
    public static bool Exists()
    {
        var text = Netsh($"advfirewall firewall show rule name=\"{Name}\" verbose");
        return text != null && text.Contains(Exe, StringComparison.OrdinalIgnoreCase);
    }

    // One Windows admin prompt. False when the person says no or Windows refuses.
    public static bool Add()
    {
        var args = $"advfirewall firewall add rule name=\"{Name}\" dir=in action=allow program=\"{Exe}\" enable=yes " +
                   $"protocol=TCP localport={CastServer.FirstPort}-{CastServer.LastPort} remoteip=localsubnet profile=private";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(NetshExe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) { Log.Write("firewall: Windows started nothing for the admin prompt"); return false; }
            if (!p.WaitForExit(20000)) { Log.Write("firewall: adding the rule timed out"); return false; }
            Log.Write($"firewall: add rule finished, exit {p.ExitCode}");
            return p.ExitCode == 0 && Exists();
        }
        catch (Win32Exception ex)
        {
            // 1223 = the person said no; anything else (a policy that blocks admin prompts, say) is Windows refusing.
            Log.Write(ex.NativeErrorCode == 1223 ? "firewall: the admin prompt was declined" : $"firewall: the admin prompt failed ({ex.NativeErrorCode}): {ex.Message}");
            return false;
        }
    }

    static string Netsh(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(NetshExe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch (Win32Exception ex)
        {
            Log.Write("firewall: netsh would not run: " + ex.Message);
            return null;
        }
    }
}
