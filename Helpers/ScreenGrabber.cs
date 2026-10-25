using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PCCast2TV.Helpers;

// The app's own screen grab, timed like OBS: a steady clock ticks fps times a second, each tick takes the newest picture
// Windows has (the last one again when the screen didn't change) and turns it into video color on the graphics card.
// The encoder takes one picture per tick, so picture n is exactly n/fps seconds in.
public sealed partial class ScreenGrabber : IDisposable
{
    const int PoolSize = 16;

    // One video picture on the card; Refs counts the clock's hold, every wait in line, and the encoder's hold.
    public sealed class Picture
    {
        public ID3D11Texture2D Tex { get; init; }
        internal ID3D11VideoProcessorOutputView View;
        internal int Refs;
    }

    // A picture waiting for the encoder, and how many ticks in a row it fills.
    sealed class Waiting
    {
        public Picture Pic;
        public int Ticks;
    }

    readonly int _fps;
    readonly IDXGIOutput1 _output;
    readonly ID3D11DeviceContext _ctx;
    readonly ID3D11VideoDevice _video;
    readonly ID3D11VideoContext _vctx;
    readonly ID3D11Texture2D _staging;
    // The screen side, made again when Windows hands over another size or the screen is turned.
    ID3D11VideoProcessorEnumerator _vpEnum;
    ID3D11VideoProcessor _vp;
    ID3D11Texture2D _copy;
    ID3D11VideoProcessorInputView _input;
    uint _inW, _inH;
    ModeRotation _inTurn, _dupTurn;
    IDXGIOutputDuplication _dup;
    readonly Stack<Picture> _free = new();
    readonly List<Picture> _all = new();
    readonly Queue<Waiting> _line = new();
    Waiting _tail;
    readonly object _gate = new();
    readonly CancellationTokenSource _stop = new();
    readonly int _screenW, _screenH;
    Thread _clock;
    bool _dupBroken, _ended;

    // Counts for the 10-second timing line.
    readonly object _countGate = new();
    int _ticks, _new, _late, _screenUpdates, _heldBack, _mostWaiting;
    double _longestWake;

    public ID3D11Device Device { get; }
    public int Width { get; }
    public int Height { get; }
    public event Action FirstPicture; // the sound clock starts with it
    public event Action Died;         // the clock stopped on its own (no first picture, or the card failed)

    // The card that drives the main screen (the one at 0,0) and the screen itself.
    public static (IDXGIAdapter1 Adapter, IDXGIOutput Output) MainScreen()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1 factory).CheckError();
        using (factory)
            for (uint a = 0; factory.EnumAdapters1(a, out var ad).Success; a++)
            {
                for (uint o = 0; ad.EnumOutputs(o, out var output).Success; o++)
                {
                    var r = output.Description.DesktopCoordinates;
                    if (output.Description.AttachedToDesktop && r.Left == 0 && r.Top == 0) return (ad, output);
                    output.Dispose();
                }
                ad.Dispose();
            }
        throw new InvalidOperationException("no main screen found");
    }

    // Sets everything up on the card that drives the main screen; throws InvalidOperationException with the reason.
    public ScreenGrabber(int fps)
    {
        _fps = fps;
        try
        {
            var (adapter, output) = MainScreen();
            using (adapter)
            using (output)
            {
                var r = output.Description.DesktopCoordinates;
                (_screenW, _screenH) = (r.Right - r.Left, r.Bottom - r.Top);
                _output = output.QueryInterface<IDXGIOutput1>();
                D3D11.D3D11CreateDevice(adapter, Vortice.Direct3D.DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    null, out ID3D11Device device, out _ctx).CheckError();
                Device = device;
                Log.Write($"grab: {adapter.Description1.Description}, screen {_screenW}x{_screenH}");
            }
            using (var mt = Device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true); // clock and encoder share the card
            _dup = _output.DuplicateOutput(Device);
            _dupTurn = _dup.Description.Rotation;

            (Width, Height) = ScreenInfo.SentSize(_screenW, _screenH);

            _staging = Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)Width, (uint)Height, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read));
            _video = Device.QueryInterface<ID3D11VideoDevice>();
            _vctx = _ctx.QueryInterface<ID3D11VideoContext>();
            for (int i = 0; i < PoolSize; i++)
            {
                var pic = new Picture { Tex = Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)Width, (uint)Height, 1, 1, BindFlags.RenderTarget)) };
                _all.Add(pic);
                _free.Push(pic);
            }
            var mode = _dup.Description.ModeDescription;
            BuildInput(mode.Width, mode.Height);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Dispose();
            throw new InvalidOperationException("the graphics card would not set up the screen grab: " + ex.Message, ex);
        }
        Log.Write($"grab: sending {Width}x{Height} at {fps} pictures a second");
    }

    // The first picture starts the clock (and the sound's clock).
    public void Start()
    {
        _clock = new Thread(ClockLoop) { IsBackground = true, Name = "screen clock", Priority = ThreadPriority.Highest };
        _clock.Start();
    }

    public (int Ticks, int NewPictures, int Late, double LongestWakeMs, int ScreenUpdates, int HeldBack, int MostWaiting) TakeCounts()
    {
        lock (_countGate)
        {
            var c = (_ticks, _new, _late, _longestWake, _screenUpdates, _heldBack, _mostWaiting);
            (_ticks, _new, _late, _longestWake, _screenUpdates, _heldBack, _mostWaiting) = (0, 0, 0, 0, 0, 0, 0);
            return c;
        }
    }

    // The encoder's side: the picture for the next tick, held for the encoder until it calls Release; null once stopped.
    public Picture NextTick()
    {
        lock (_gate)
        {
            while (_line.Count == 0)
            {
                if (_stop.IsCancellationRequested || _ended) return null;
                Monitor.Wait(_gate, 500);
            }
            var w = _line.Peek();
            w.Pic.Refs++;
            if (--w.Ticks == 0)
            {
                _line.Dequeue();
                if (w == _tail) _tail = null;
                w.Pic.Refs--; // the line's hold passes to the encoder's
            }
            return w.Pic;
        }
    }

    void ClockLoop()
    {
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* high resolution */, 0x1F0003);
        if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
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
            double rate = Stopwatch.Frequency, t0 = Stopwatch.GetTimestamp() / rate;
            Send(last, 1);
            long next = 1;
            while (!_stop.IsCancellationRequested)
            {
                double due = t0 + (double)next / _fps, wait = due - Stopwatch.GetTimestamp() / rate;
                if (wait > 0)
                {
                    long until = -(long)(wait * 10_000_000);
                    SetWaitableTimerEx(timer, ref until, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
                    WaitForSingleObject(timer, 1000);
                }
                double now = Stopwatch.GetTimestamp() / rate;
                long current = Math.Max(next, (long)((now - t0) * _fps)); // the tick whose time has come
                var fresh = TakeNewest(0);
                // Fell behind: the ticks it slept through keep the last picture, so the timeline stays whole.
                int missed = (int)(current - next);
                if (missed > 0) Send(last, missed);
                if (fresh != null) { Release(last); last = fresh; }
                Send(last, 1);
                lock (_countGate)
                {
                    _ticks += missed + 1;
                    _late += missed;
                    _longestWake = Math.Max(_longestWake, (now - due) * 1000);
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
            if (timer != IntPtr.Zero) CloseHandle(timer);
            Ended();
        }
    }

    // No more pictures are coming: the encoder finishes what's in line, and an unasked-for end is reported.
    void Ended()
    {
        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }
        if (!_stop.IsCancellationRequested) Died?.Invoke();
    }

    void Send(Picture p, int ticks)
    {
        lock (_gate)
        {
            if (_tail != null && _tail.Pic == p) _tail.Ticks += ticks; // the same picture again just fills more ticks
            else
            {
                p.Refs++;
                _tail = new Waiting { Pic = p, Ticks = ticks };
                _line.Enqueue(_tail);
                lock (_countGate) _mostWaiting = Math.Max(_mostWaiting, _line.Count);
            }
            Monitor.PulseAll(_gate);
        }
    }

    public void Release(Picture p)
    {
        if (p == null) return;
        lock (_gate) if (--p.Refs == 0) _free.Push(p);
    }

    // No more ticks; the encoder still gets what's already in line, then NextTick says stopped.
    public void Stop()
    {
        _stop.Cancel();
        _clock?.Join(1000);
        lock (_gate) Monitor.PulseAll(_gate);
    }

    public void Dispose()
    {
        Stop();
        foreach (var p in _all) { p.View?.Dispose(); p.Tex.Dispose(); }
        _input?.Dispose();
        _vp?.Dispose();
        _vpEnum?.Dispose();
        _vctx?.Dispose();
        _video?.Dispose();
        _copy?.Dispose();
        _staging?.Dispose();
        _dup?.Dispose();
        _ctx?.Dispose();
        Device?.Dispose();
        _output?.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimerEx(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, IntPtr wake, uint delay);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
