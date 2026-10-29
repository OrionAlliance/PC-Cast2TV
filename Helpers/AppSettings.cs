using System.IO;
using System.Text.Json;

namespace PCCast2TV.Helpers;

// DesignWidth and DesignHeight are the layout the size was saved for, so a size from an older layout isn't reused.
public sealed record WindowPlace(double Left, double Top, double Width, double Height, bool Maximized, double DesignWidth = 0, double DesignHeight = 0);

// Saved devices, the bookmark, hidden devices, the window's spot and the theme, in Settings\settings.json beside the app.
public static class AppSettings
{
    sealed class Saved
    {
        public Device Default { get; set; }
        public List<Device> Devices { get; set; } = new();
        public List<Device> Hidden { get; set; } = new();
        public WindowPlace Window { get; set; }
        public string Theme { get; set; } // a ThemeColors key (one of the 8, or a RANDOM roll); none = CYAN
        public string MyTheme { get; set; } // the one RANDOM roll kept with SAVE, as a ThemeColors key
    }

    static string FilePath => Path.Combine(Log.Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly Saved Data = Load();

    public static Device Default => Data.Default;
    public static IReadOnlyList<Device> Devices => Data.Devices;
    public static IReadOnlyList<Device> Hidden => Data.Hidden;
    public static WindowPlace Window => Data.Window;
    public static string Theme => Data.Theme;
    public static string MyTheme => Data.MyTheme;

    public static bool IsHidden(string id) => Data.Hidden.Any(d => d.Id == id);

    static Saved Load()
    {
        try
        {
            var saved = (File.Exists(FilePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) : null) ?? new Saved();
            saved.Devices ??= new();
            saved.Hidden ??= new();
            return saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("settings: could not read: " + ex.Message);
            KeepUnreadable();
            return new Saved();
        }
    }

    // The app goes on with fresh settings, and its next save would wipe the old file, so a copy is kept to recover from.
    static void KeepUnreadable()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var copy = Path.Combine(Log.Folder, $"settings.unreadable-{DateTime.Now:yyyy-MM-dd-HHmmss}.json");
            File.Copy(FilePath, copy);
            Log.Write("settings: kept a copy as " + Path.GetFileName(copy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("settings: could not keep a copy: " + ex.Message);
        }
    }

    // Detect adds new devices and updates moved or renamed ones; devices that were off stay saved, hidden ones stay hidden.
    public static void Remember(IEnumerable<Device> found)
    {
        bool changed = false;
        foreach (var d in found)
        {
            int i = Data.Devices.FindIndex(s => s.Id == d.Id);
            if (i < 0) Data.Devices.Add(d);
            else if (Data.Devices[i] != d) Data.Devices[i] = d;
            else continue;
            changed = true;
        }
        if (changed && Save()) Log.Write("settings: saved devices now " + string.Join(", ", Data.Devices.Select(d => d.LogName)));
    }

    public static void SetDefault(Device d)
    {
        Data.Default = d;
        if (Save()) Log.Write(d == null ? "settings: default cleared" : "settings: default is " + d.LogName);
    }

    // The checked devices become the hidden set; anything unchecked comes back.
    public static void SetHidden(IEnumerable<Device> devices)
    {
        Data.Hidden = devices.ToList();
        if (Save()) Log.Write("settings: hidden now " + (Data.Hidden.Count == 0 ? "none" : string.Join(", ", Data.Hidden.Select(d => d.LogName))));
    }

    // Hidden: off the list and out of Detect's but still saved (right-click Detect brings it back); its bookmark is cleared.
    public static void Hide(Device d)
    {
        if (!IsHidden(d.Id)) Data.Hidden.Add(d);
        bool wasDefault = Data.Default?.Id == d.Id;
        if (wasDefault) Data.Default = null;
        if (Save()) Log.Write("settings: hidden now " + string.Join(", ", Data.Hidden.Select(x => x.LogName)) + (wasDefault ? ", bookmark cleared" : ""));
    }

    public static void SetTheme(string name)
    {
        Data.Theme = name;
        if (Save()) Log.Write("settings: theme " + name);
    }

    // Only one: saving another roll replaces it.
    public static void SetMyTheme(string key)
    {
        Data.MyTheme = key;
        if (Save()) Log.Write("settings: my color is " + key);
    }

    public static void SetWindow(WindowPlace w)
    {
        Data.Window = w;
        Save();
    }

    static bool Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            // Written whole to a side file first, then swapped in, so a crash mid-save can't leave half a file.
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(Data, Json));
            File.Move(FilePath + ".tmp", FilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("settings: could not save: " + ex.Message);
            return false;
        }
    }
}
