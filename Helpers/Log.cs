using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PCCast2TV.Helpers;

// One debug log next to the app (Settings\debug.log). Folder paths are swapped for short tags so no user name lands in it.
public static class Log
{
    const int MaxLine = 8000;
    static readonly object Gate = new();
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Settings");
    static string FilePath => Path.Combine(Folder, "debug.log");

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) fi.MoveTo(Path.Combine(Folder, "debug.old.log"), true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Clean(Scrub(line))}\n");
            }
            catch (IOException) { } // a log that can't be written must never take the cast down with it
            catch (UnauthorizedAccessException) { }
        }
    }

    // Device names and other outside text go in as sent: a line break inside one can't pass for a line of the log
    // (follow-on lines start with "|"), other control characters become spaces, and no entry runs on for pages.
    static string Clean(string s)
    {
        if (s.Length > MaxLine) s = s[..MaxLine] + " (cut)";
        var b = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\n') b.Append("\n  | ");
            else if (c != '\r') b.Append(char.IsControl(c) ? ' ' : c);
        }
        return b.ToString();
    }

    // Any folder path left over (an error's source file, for one) keeps only the file name.
    static readonly Regex AnyFolder = new(@"[A-Za-z]:\\(?:[^\\\r\n:*?""<>|]+\\)+");

    public static string Scrub(string s) => AnyFolder.Replace(s
        .Replace(AppContext.BaseDirectory.TrimEnd('\\'), "<app>", StringComparison.OrdinalIgnoreCase)
        .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>", StringComparison.OrdinalIgnoreCase), @"<path>\");
}
