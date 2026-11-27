using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PCCast2TV.Helpers;

// Sends a bug report or feedback to the developer's Discord channel. The address is built into the exe from Private\report-address.txt, which stays out of the code on GitHub.
public static class ReportSender
{
    public enum Outcome { Sent, NoConnection, TooSlow, Busy, Refused, PictureUnreadable, LogUnreadable }

    static readonly string Address = ReadAddress();
    // 5 minutes: a full report (2 MB log + 8 MB screenshot) still gets through a slow upload, about 300 kbps.
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };

    public static bool Ready => Address != null;

    static string ReadAddress()
    {
        using var s = typeof(ReportSender).Assembly.GetManifestResourceStream("report-address");
        if (s == null) return null;
        string text = new StreamReader(s).ReadToEnd().Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? text : null;
    }

    // One message: the words up top, then report.txt, debug.log and the screenshot as files. The email is never written to the log.
    public static async Task<Outcome> SendAsync(bool bug, string email, string words, bool withLog, string picture)
    {
        byte[] log = null, shot = null;
        if (withLog && File.Exists(Log.FilePath) && (log = Read(Log.FilePath, "debug.log")) == null) return Outcome.LogUnreadable;
        if (picture != null && (shot = Read(picture, "the screenshot")) == null) return Outcome.PictureUnreadable;
        string kind = bug ? "BUG REPORT" : "FEEDBACK", windows = RuntimeInformation.OSDescription, version = AppUpdater.Installed.ToString();
        var payload = new
        {
            username = "PC Cast2TV",
            embeds = new[]
            {
                new
                {
                    title = kind + " - PC Cast2TV", description = Cut(words, 3500), color = bug ? 0xF2665E : 0x3CCBF4,
                    fields = new[]
                    {
                        new { name = "Email", value = Cut(email, 1024), inline = true },
                        new { name = "Windows", value = Cut(windows, 1024), inline = true },
                        new { name = "Version", value = version, inline = true },
                    },
                },
            },
        };
        using var form = new MultipartFormDataContent { { new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json" } };
        int n = 0;
        Attach(form, n++, "report.txt", Encoding.UTF8.GetBytes($"{kind}\nEmail: {email}\nWindows: {windows}\nVersion: {version}\n\n{words}\n"));
        if (log != null) Attach(form, n++, "debug.log", log);
        if (shot != null) Attach(form, n++, "screenshot" + Path.GetExtension(picture).ToLowerInvariant(), shot); // never the picture's own name
        try
        {
            using var answer = await Http.PostAsync(Address, form);
            if (answer.IsSuccessStatusCode)
            {
                Log.Write($"report: sent {(bug ? "a bug report" : "feedback")}, {(log != null ? "with" : "without")} the log, {(shot != null ? "with a" : "no")} screenshot");
                return Outcome.Sent;
            }
            Log.Write($"report: the report service answered {(int)answer.StatusCode}");
            return (int)answer.StatusCode == 429 ? Outcome.Busy : Outcome.Refused;
        }
        catch (TaskCanceledException)
        {
            Log.Write($"report: sending took longer than {Http.Timeout.TotalMinutes:0} minutes, gave up");
            return Outcome.TooSlow; // nothing else cancels it, so this is the time limit
        }
        catch (HttpRequestException ex)
        {
            Log.Write($"report: could not send: {ex.GetType().Name} {Log.Outside(ex.Message)}");
            return Outcome.NoConnection;
        }
    }

    // Read without locking (the app may be writing the log right now); a file moved, deleted or locked since gives null, and the log says which.
    static byte[] Read(string path, string what)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"report: couldn't read {what}: {ex.GetType().Name}");
            return null;
        }
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    static void Attach(MultipartFormDataContent form, int index, string name, byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, $"files[{index}]", name);
    }
}
