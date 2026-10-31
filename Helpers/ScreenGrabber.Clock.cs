using System.Diagnostics;

namespace PCCast2TV.Helpers;

// The grab's clock, Sunshine's way (display_base.cpp): it starts from a real screen update and ticks on a steady beat,
// and when a tick misses its time or finds nothing new it waits for the next real update and starts again from there.
// The TV's timeline stays fps a second: picture n is n/fps seconds in, the last picture filling any tick it missed.
public sealed partial class ScreenGrabber
{
    static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    void ClockLoop()
    {
        var timer = new PreciseTimer();
        try
        {
            Picture last = null;
            for (int i = 0; i < 50 && last == null && !_stop.IsCancellationRequested; i++) last = TakeNewest(100, first: true);
            if (last == null)
            {
                if (!_stop.IsCancellationRequested) Log.Write("grab: Windows gave no first picture"); // not when the cast just ended first
                return;
            }
            FirstPicture?.Invoke();
            double t0 = Now(), pace = PaceFps(), interval = 1 / pace;
            if (pace < _fps) Log.Write($"grab: ticking at {pace:0.000} a second to match the screen");
            Send(last, 1);
            long next = 1, inGroup = 0;
            double? group = null; // when the screen showed the update this run of ticks started from
            while (!_stop.IsCancellationRequested)
            {
                double due = t0 + (double)next / _fps; // the TV's next tick
                Picture fresh = null;
                if (group is { } start)
                {
                    double target = start + inGroup * interval;
                    if (target > Now())
                    {
                        timer.Wait(target - Now(), 1000);
                        lock (_countGate) _longestWake = Math.Max(_longestWake, (Now() - target) * 1000);
                        fresh = TakeNewest(0);
                    }
                    if (fresh != null) inGroup++;
                    else group = null; // missed its time, or nothing new: start again from the next real update
                }
                if (group == null && (fresh = WaitForUpdate(due, timer)) != null)
                {
                    (group, inGroup) = (_shownAt, 1);
                    lock (_countGate) _resyncs++;
                }
                long current = Math.Max(next, (long)((Now() - t0) * _fps)); // the tick whose time has come
                // Fell behind: the ticks it slept through keep the last picture, so the timeline stays whole.
                int missed = (int)(current - next);
                if (missed > 0) Send(last, missed);
                if (fresh != null) { Release(last); last = fresh; }
                Send(last, 1);
                lock (_countGate)
                {
                    _ticks += missed + 1;
                    _late += missed;
                    if (fresh != null) _new++;
                }
                next = current + 1;
            }
            Release(last);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) Log.Write("grab: stopped: " + ex.Message);
        }
        finally
        {
            timer.Dispose();
            Ended();
        }
    }

    // The screen's next real update, waited for until the TV's next tick is due; null if none came by then.
    // Sunshine waits up to 200 ms here; the TV needs a picture every tick, so this waits one tick at most.
    Picture WaitForUpdate(double due, PreciseTimer timer)
    {
        while (!_stop.IsCancellationRequested)
        {
            double left = due - Now();
            var p = TakeNewest(left > 0 ? (uint)Math.Ceiling(left * 1000) : 0);
            if (p != null || left <= 0) return p;
            if (_dupBroken) // the screen copy is away: wait out the tick instead of asking again at once
            {
                timer.Wait(due - Now(), 1000);
                return null;
            }
        }
        return null;
    }

    // Sunshine's rule: a screen whose rate isn't a whole number but is within 1% under the cast's sets the beat; never faster.
    double PaceFps()
    {
        var r = _dup.Description.ModeDescription.RefreshRate;
        if (r.Denominator <= 1 || r.Numerator == 0) return _fps;
        double screen = (double)r.Numerator / r.Denominator;
        int whole = (int)Math.Round(screen);
        if (whole > 0 && _fps % whole == 0) screen *= _fps / whole;
        else if (whole > 0 && whole % _fps == 0) screen /= whole / _fps;
        return _fps > screen && screen / _fps > 0.99 ? screen : _fps;
    }
}
