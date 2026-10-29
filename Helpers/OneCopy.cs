using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCCast2TV.Helpers;

// One copy at a time (two would fight over settings): a second brings the first forward, an updater's start waits.
public static class OneCopy
{
    static Mutex _lock;
    static bool _held;

    // True when this copy is the one to run.
    public static bool Take()
    {
        _lock = new Mutex(false, @"Local\PC Cast2TV");
        if (Environment.GetCommandLineArgs().Contains(AppUpdater.AfterUpdate))
        {
            if (OldCopyClosed(TimeSpan.FromSeconds(15))) Wait(TimeSpan.Zero);
            else Log.Write("app: the old version was still open after 15 seconds, opening anyway");
            return true;
        }
        if (Wait(TimeSpan.Zero)) return true;
        ShowTheOpenOne();
        return false;
    }

    public static void Release()
    {
        if (_held) _lock.ReleaseMutex();
        _held = false;
    }

    static bool Wait(TimeSpan time)
    {
        try { _held = _lock.WaitOne(time); }
        catch (AbandonedMutexException) { _held = true; } // the last copy went without letting go; it's this one's now
        return _held;
    }

    // Waits for the copy being replaced to close all the way, so its exe is free to delete; false if it's still open.
    static bool OldCopyClosed(TimeSpan limit)
    {
        using var me = Process.GetCurrentProcess();
        var until = DateTime.UtcNow + limit;
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
            using (p)
            {
                if (p.Id == me.Id) continue;
                try
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !p.WaitForExit(left)) return false;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } // gone already, or not ours to wait on
            }
        return true;
    }

    static void ShowTheOpenOne()
    {
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // 9 = restore from the taskbar
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
    }

    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int how);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
