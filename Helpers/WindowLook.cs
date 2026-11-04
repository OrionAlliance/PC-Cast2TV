namespace PCCast2TV.Helpers;

// The main window's look: the dial (the first one) or the simple picker; picked in Settings and kept for next time.
public static class WindowLook
{
    const string SimpleKey = "SIMPLE";

    public static bool Simple => AppSettings.Look == SimpleKey;

    // After a pick in Settings; the main window swaps looks at once, a cast keeps going.
    public static event Action Changed;

    // A line that names the find button, in the words of the look on screen.
    public static string Words(string line) => Simple ? line.Replace("DETECT DEVICE(S)", "FIND: TV / ROKU", StringComparison.Ordinal) : line;

    public static void Pick(bool simple)
    {
        if (simple == Simple) return;
        AppSettings.SetLook(simple ? SimpleKey : null);
        Changed?.Invoke();
    }
}
