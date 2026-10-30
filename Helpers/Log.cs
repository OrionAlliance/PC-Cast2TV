using System.Diagnostics;
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

    // OPEN LOG FOLDER: the folder in File Explorer with debug.log picked, ready to drag into a GitHub post.
    public static void ShowInFolder()
    {
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { Process.Start(explorer, File.Exists(FilePath) ? $"/select,\"{FilePath}\"" : $"\"{Folder}\"").Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { Write("app: couldn't open the log folder: " + ex.Message); }
    }

    // Text a device sent (its model, what its player says), cut to a short run of plain letters, digits and punctuation.
    public static string Outside(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var b = new StringBuilder();
        foreach (char c in s)
        {
            if (b.Length == 80) { b.Append("..."); break; }
            if (char.IsAsciiLetterOrDigit(c) || " .,-_:/()+#".Contains(c)) b.Append(c);
        }
        return b.ToString();
    }

    // Follow-on lines of an entry start with "|", other control characters become spaces, and no entry runs on for pages.
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
