using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PCCast2TV.Helpers;

// Self-update from the app's GitHub releases (the same way the Time Sheet apps do it). The portable exe swaps itself
// in place; the Settings folder next to it is never touched. All GitHub sees is a plain request for the newest release.
public static class AppUpdater
{
    public const string Repo = "OrionAlliance/PC-Cast2TV", Page = "https://github.com/" + Repo;

    // The app's GitHub page in the default browser (Settings > ABOUT).
    public static void OpenPage()
    {
        try { Process.Start(new ProcessStartInfo(Page) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Write("update: couldn't open the GitHub page: " + ex.Message); }
    }

    public sealed record Update(Version Installed, Version Latest, string DownloadUrl, long Size);

    static readonly Assembly App = Assembly.GetExecutingAssembly();
    public static Version Installed => Short(App.GetName().Version ?? new Version(0, 0, 0));
    static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PC Cast2TV.exe");

    static Version Short(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // Null when up to date, offline, or there's nothing usable on GitHub; it never nags.
    public static async Task<Update> CheckAsync(string repo = Repo, Version installed = null)
    {
        installed ??= Installed;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(15));
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest"));
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
            {
                Log.Write($"update: the newest release's tag '{tag}' isn't a version, skipped");
                return null;
            }
            var latest = Short(parsed);
            // The release also carries the Roku zip; the exe is the one this app replaces itself with.
            var exe = doc.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            if (exe.ValueKind == JsonValueKind.Undefined)
            {
                Log.Write($"update: release {tag} has no exe, skipped");
                return null;
            }
            Log.Write($"update: this is v{installed}, GitHub has v{latest}");
            return latest > installed
                ? new Update(installed, latest, exe.GetProperty("browser_download_url").GetString(), exe.GetProperty("size").GetInt64())
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write($"update: check didn't finish (offline?): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Downloads next to the exe as "<name>.exe.new", so the swap is a rename in the same folder.
    public static async Task<string> DownloadAsync(Update u, IProgress<string> progress, string exePath = null)
    {
        string target = (exePath ?? ExePath) + ".new";
        long totalMb = Math.Max(1, u.Size / 1_048_576);
        try
        {
            using var http = Client(TimeSpan.FromMinutes(10));
            using var resp = await http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(target))
            {
                var buffer = new byte[81920];
                long done = 0, shownMb = -1;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    if (done / 1_048_576 == shownMb) continue;
                    shownMb = done / 1_048_576;
                    progress.Report($"DOWNLOADING V{u.Latest}... {shownMb} OF {totalMb} MB");
                }
            }
            long got = new FileInfo(target).Length;
            if (got != u.Size) throw new IOException($"The download came up short ({got:N0} of {u.Size:N0} bytes).");
            using (var f = File.OpenRead(target))
                if (f.ReadByte() != 'M' || f.ReadByte() != 'Z') throw new IOException("The download isn't a Windows program.");
            Log.Write($"update: downloaded v{u.Latest} ({got:N0} bytes)");
            return target;
        }
        catch
        {
            if (File.Exists(target)) File.Delete(target); // never leave half a download behind
            throw;
        }
    }

    // A running exe can be renamed but not overwritten: this one becomes .old, the download takes its name. The next
    // start deletes .old.
    public static void Swap(string downloaded, string exePath = null)
    {
        string exe = exePath ?? ExePath, old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(downloaded, exe); }
        catch
        {
            File.Move(old, exe); // put the working copy back
            throw;
        }
        Log.Write("update: the new exe is in place");
    }

    public static void StartNewCopy()
    {
        Log.Write("update: starting the new version and closing this one");
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath) });
    }

    // True right after an update, while the previous version is still left behind.
    public static bool JustUpdated(string exePath = null) => File.Exists((exePath ?? ExePath) + ".old");

    // Deletes the previous version and any half download; the old copy may still be closing, so it retries for 10 seconds.
    public static async Task CleanUpAsync(string exePath = null)
    {
        string exe = exePath ?? ExePath;
        foreach (var leftover in new[] { exe + ".old", exe + ".new" })
        {
            for (int i = 0; i < 20 && File.Exists(leftover); i++)
            {
                try
                {
                    File.Delete(leftover);
                    Log.Write($"update: removed the {Path.GetExtension(leftover)} copy");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (i == 19) Log.Write($"update: couldn't remove the {Path.GetExtension(leftover)} copy yet ({ex.GetType().Name}), the next start tries again");
                    else await Task.Delay(500);
                }
            }
        }
    }

    static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PC-Cast2TV"); // GitHub requires a name; this is all it gets
        return http;
    }
}
