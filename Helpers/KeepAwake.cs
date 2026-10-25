using System.Runtime.InteropServices;

namespace PCCast2TV.Helpers;

// Keeps the PC and its screen awake for the length of a cast, as OBS does while it streams. Called on the window's
// thread: Windows ties the request to the thread that made it.
public static class KeepAwake
{
    const uint Continuous = 0x80000000, SystemOn = 0x1, DisplayOn = 0x2;

    public static void On()
    {
        if (SetThreadExecutionState(Continuous | SystemOn | DisplayOn) == 0) Log.Write("power: Windows would not keep the PC awake");
    }

    public static void Off() => SetThreadExecutionState(Continuous);

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
}
