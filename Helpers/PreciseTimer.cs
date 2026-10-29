using System.Runtime.InteropServices;

namespace PCCast2TV.Helpers;

// Windows' high-resolution wait (an ordinary one where that's missing), for the screen clock and the mirroring's pacing.
public sealed class PreciseTimer : IDisposable
{
    readonly IntPtr _timer = Create();

    static IntPtr Create()
    {
        var t = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* high resolution */, 0x1F0003);
        return t != IntPtr.Zero ? t : CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
    }

    // Sleeps this many seconds, never past limitMs.
    public void Wait(double seconds, uint limitMs)
    {
        long until = -(long)(seconds * 10_000_000);
        SetWaitableTimerEx(_timer, ref until, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
        WaitForSingleObject(_timer, limitMs);
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimerEx(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, IntPtr wake, uint delay);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
