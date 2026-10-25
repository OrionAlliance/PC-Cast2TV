using System.Runtime.InteropServices;

namespace PCCast2TV.Helpers;

// Turns a Roku remote button into a key press on the PC. Play/pause, next and previous are media keys, which Windows
// hands to whatever is playing; forward and rewind are the arrow keys, which go to the window in front (the video).
public static class RemoteKeys
{
    const uint KeyboardInput = 1, ExtendedKey = 0x1, KeyUp = 0x2;

    static readonly Dictionary<string, ushort> Keys = new()
    {
        ["playpause"] = 0xB3, // media play/pause
        ["next"] = 0xB0,      // media next track
        ["previous"] = 0xB1,  // media previous track
        ["forward"] = 0x27,   // right arrow
        ["rewind"] = 0x25,    // left arrow
    };

    public static bool Knows(string name) => Keys.ContainsKey(name);

    public static void Press(string name)
    {
        if (!Keys.TryGetValue(name, out var vk)) return;
        var down = new INPUT { type = KeyboardInput, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = ExtendedKey } } };
        var up = down;
        up.u.ki.dwFlags |= KeyUp;
        uint sent = SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
        if (sent != 2) Log.Write($"remote: Windows took {sent} of 2 key events for {name} (error {Marshal.GetLastWin32Error()})");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    // Sized by its largest member (the mouse one), as Windows expects.
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
