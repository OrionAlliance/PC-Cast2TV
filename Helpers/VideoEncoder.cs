using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace PCCast2TV.Helpers;

// The card's H.264 encoder fed straight from the screen grab, picture n stamped n/fps; no card encoder: Windows' own, from memory.
public sealed partial class VideoEncoder : IDisposable
{
    public delegate void FrameHandler(byte[] annexB, long pts90k, bool key);

    const uint EnumSync = 0x1, EnumHardware = 0x4, EnumSortAndFilter = 0x40;
    const int NeedMoreInput = unchecked((int)0xC00D6D72), StreamChange = unchecked((int)0xC00D6D61);
    readonly ScreenGrabber _grab;
    readonly int _fps, _maxBitRate;
    readonly FrameHandler _onFrame;
    IMFTransform _mft;
    IMFMediaEventGenerator _events;
    IMFDXGIDeviceManager _manager;
    IntPtr _codecApi;
    bool _hardware, _providesSamples, _shutDown;
    int _outSize;
    byte[] _header = Array.Empty<byte>(); // SPS + PPS, put in front of every keyframe that lacks them
    Thread _thread;
    volatile bool _stop, _keyWanted;
    readonly Queue<ScreenGrabber.Picture> _inFlight = new();
    long _frames, _framesOut;
    // Windows' own encoder: the picture buffers it was fed, by frame, each used again once its frame has come out.
    readonly Queue<(long Frame, IMFMediaBuffer Buffer)> _fed = new();
    IMFSample _outSample; // and the one sample it writes its frames into, when it doesn't bring its own

    public event Action Died;

    static readonly Lazy<bool> Started = new(() => { MediaFactory.MFStartup(false).CheckError(); return true; });

    // Windows' media library has to be started once before anything else in it.
    public static void EnsureStarted() => _ = Started.Value;

    // maxBitRate 0: no cap.
    public VideoEncoder(ScreenGrabber grab, int fps, int maxBitRate, FrameHandler onFrame)
    {
        _grab = grab;
        _fps = fps;
        _maxBitRate = maxBitRate;
        _onFrame = onFrame;
    }

    // The names of the card encoders Windows has, best first, for the card that drives the main screen.
    public static List<string> CardEncoders(string vendor)
    {
        using var found = Find(EnumHardware);
        return found.Where(a => IsFor(a, vendor)).Select(a => Text(a, TransformAttributeKeys.MftFriendlyNameAttribute)).ToList();
    }

    // Every NV12-to-H.264 encoder of one kind; closing the list lets them all go, so it stays open while one starts.
    static IMFActivateCollection Find(uint kind) => MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, kind | EnumSortAndFilter,
        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

    // "VEN_10DE" style vendor ID: the encoder on the same card as the screen grab's pictures.
    static bool IsFor(IMFActivate a, string vendor) => Text(a, TransformAttributeKeys.MftEnumHardwareVendorIdAttribute).Equals(vendor, StringComparison.OrdinalIgnoreCase);

    static string Text(IMFAttributes a, Guid key)
    {
        try { return a.GetString(key) ?? ""; }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Log.Write($"encoder: an encoder's {(key == TransformAttributeKeys.MftFriendlyNameAttribute ? "name" : "card maker")} couldn't be read: {ex.Message}");
            return "";
        }
    }

    // Opens the card's encoder, or Windows' own on the processor, and sets it up; false when none would start.
    public bool Open(string vendor, bool onCard) => onCard ? TryStart(EnumHardware, vendor) : TryStart(EnumSync, null);

    bool TryStart(uint kind, string vendor)
    {
        using var found = Find(kind);
        foreach (var a in found)
        {
            if (vendor != null && !IsFor(a, vendor)) continue;
            string name = Text(a, TransformAttributeKeys.MftFriendlyNameAttribute);
            try
            {
                _mft = a.ActivateObject<IMFTransform>();
                SetUp(hardware: vendor != null);
                Log.Write($"encoder: {name} " + (vendor != null ? "on the card" : "on the processor"));
                return true;
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                Log.Write($"encoder: {name} would not start: {ex.Message}");
                CloseMft();
            }
        }
        return false;
    }

    // The next picture fed comes out as a keyframe.
    public void ForceKey() => _keyWanted = true;

    public void Start()
    {
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _thread = new Thread(_hardware ? CardLoop : ProcessorLoop) { IsBackground = true, Name = "video encoder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // The card encoder asks for a picture (need input) and says when a frame is ready (have output).
    void CardLoop()
    {
        try
        {
            while (!_stop)
            {
                using var ev = _events.GetEvent(0);
                if (ev.EventType == MediaEventTypes.TransformNeedInput)
                {
                    var pic = _grab.NextTick();
                    if (pic == null) return; // stopped (the grab reports its own failures)
                    using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID, pic.Tex, 0, false);
                    Feed(buffer, pic);
                }
                else if (ev.EventType == MediaEventTypes.TransformHaveOutput) TakeOutput();
                else if (ev.EventType == MediaEventTypes.Error)
                {
                    Fail($"the card encoder reported an error ({ev.Status.Code:X8})");
                    return;
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) { Fail(ex.Message); }
    }

    // Windows' own encoder works in memory: each picture is read back from the card, then encoded at once.
    void ProcessorLoop()
    {
        try
        {
            int size = _grab.Width * _grab.Height * 3 / 2;
            while (!_stop)
            {
                var pic = _grab.NextTick();
                if (pic == null) return;
                // A buffer is reused only once its frame has come out, so the encoder never reads a half-new picture.
                var buffer = _fed.Count > 0 && _fed.Peek().Frame < _framesOut ? _fed.Dequeue().Buffer : MediaFactory.MFCreateMemoryBuffer(size);
                buffer.Lock(out var at, out _, out _);
                try { _grab.ReadBack(pic, at); }
                finally { buffer.Unlock(); }
                _grab.Release(pic); // read back: the card's picture is free again
                buffer.CurrentLength = size;
                _fed.Enqueue((_frames, buffer));
                Feed(buffer, null);
                while (TakeOutput()) { }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) { Fail(ex.Message); }
    }

    void Feed(IMFMediaBuffer buffer, ScreenGrabber.Picture pic)
    {
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        long n = _frames++;
        sample.SampleTime = n * 10_000_000 / _fps;
        sample.SampleDuration = 10_000_000 / _fps;
        // Every piece starts with a keyframe, whatever the card's own spacing; a mirrored TV can also ask for one.
        if (n % _fps == 0 || _keyWanted)
        {
            _keyWanted = false;
            Set(ForceKeyFrame, 1);
        }
        if (pic != null) _inFlight.Enqueue(pic);
        _mft.ProcessInput(0, sample, 0);
        // A picture is free once its frame comes out (in order, no reordering); past 8 behind, the oldest goes back either way.
        while (_inFlight.Count > 8) _grab.Release(_inFlight.Dequeue());
    }

    // One finished frame out of the encoder; false when it has none ready.
    bool TakeOutput()
    {
        var odb = new OutputDataBuffer { StreamID = 0 };
        if (!_providesSamples)
        {
            if (_outSample == null)
            {
                _outSample = MediaFactory.MFCreateSample();
                using var made = MediaFactory.MFCreateMemoryBuffer(_outSize);
                _outSample.AddBuffer(made);
            }
            _outSample.DeleteAllItems(); // the last frame's marks (a keyframe's, say) must not carry over
            using (var b = _outSample.GetBufferByIndex(0)) b.CurrentLength = 0;
            odb.Sample = _outSample;
        }
        var r = _mft.ProcessOutput(0, 1, ref odb, out _);
        odb.Events?.Dispose();
        if (r.Code == NeedMoreInput) return false;
        if (r.Code == StreamChange)
        {
            using (var t = _mft.GetOutputAvailableType(0, 0)) _mft.SetOutputType(0, t, 0);
            ReadHeader();
            return true;
        }
        r.CheckError();
        _framesOut++;
        using var provided = _providesSamples ? odb.Sample : null; // its own sample goes back; ours is kept for the next frame
        var sample = odb.Sample;
        if (_inFlight.Count > 0) _grab.Release(_inFlight.Dequeue());
        bool key = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint clean).Success && clean != 0;
        using var buf = sample.ConvertToContiguousBuffer();
        buf.Lock(out var at, out _, out int length);
        byte[] frame;
        try { unsafe { frame = Annex.Finish(new ReadOnlySpan<byte>((void*)at, length), key, ref _header); } } // read in place, copied once
        finally { buf.Unlock(); }
        _onFrame(frame, (sample.SampleTime * 9 + 500) / 1000, key); // 100 ns to 90 kHz, rounded
        return true;
    }

    void Fail(string why)
    {
        if (_stop) return;
        Log.Write("encoder: stopped: " + why);
        Died?.Invoke();
    }

    // A card encoder must be shut down before it's let go, or Windows keeps its handles; that also frees a thread waiting on it.
    void ShutDownCard()
    {
        if (!_hardware || _mft == null || _shutDown) return;
        _shutDown = true;
        try { MediaFactory.MFShutdownObject(_mft); }
        catch (SharpGen.Runtime.SharpGenException ex) { Log.Write("encoder: shutting the card encoder down failed: " + ex.Message); }
    }

    void CloseMft()
    {
        ShutDownCard();
        _shutDown = false; // the next encoder tried starts fresh
        if (_codecApi != IntPtr.Zero) { Marshal.Release(_codecApi); _codecApi = IntPtr.Zero; }
        _events?.Dispose();
        _events = null;
        _mft?.Dispose();
        _mft = null;
        _manager?.Dispose();
        _manager = null;
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        ShutDownCard();
        _thread?.Join(1000);
        while (_inFlight.Count > 0) _grab.Release(_inFlight.Dequeue());
        while (_fed.Count > 0) _fed.Dequeue().Buffer.Dispose();
        _outSample?.Dispose();
        CloseMft();
    }
}
