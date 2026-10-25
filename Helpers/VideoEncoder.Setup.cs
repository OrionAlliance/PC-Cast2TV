using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace PCCast2TV.Helpers;

// Setting the encoder up: its quality, keyframes and picture format, through Windows' codec settings.
public sealed partial class VideoEncoder
{
    // Encoder settings (Windows' codecapi.h IDs).
    static readonly Guid RateControlMode = new(0x1c0608e9, 0x370c, 0x4710, 0x8a, 0x58, 0xcb, 0x61, 0x81, 0xc4, 0x24, 0x23);
    static readonly Guid GopSize = new(0x95f31b26, 0x95a4, 0x41aa, 0x93, 0x03, 0x24, 0x6a, 0x7f, 0xc6, 0xee, 0xf1);
    static readonly Guid BFrames = new(0x8d390aac, 0xdc5c, 0x4200, 0xb5, 0x7f, 0x81, 0x4d, 0x04, 0xba, 0xba, 0xb2);
    static readonly Guid LowLatency = new(0x9c27891a, 0xed7a, 0x40e1, 0x88, 0xe8, 0xb2, 0x27, 0x27, 0xa0, 0x24, 0xee);
    static readonly Guid Cabac = new(0xee6cad62, 0xd305, 0x4248, 0xa5, 0x0e, 0xe1, 0xb2, 0x55, 0xf7, 0xca, 0xf8);
    static readonly Guid ForceKeyFrame = new(0x398c1b98, 0x8353, 0x475a, 0x9e, 0xf2, 0x8f, 0x26, 0x5d, 0x26, 0x03, 0x45);
    static readonly Guid EncodeQp = new(0x2cb5696b, 0x23fb, 0x4ce1, 0xa0, 0xf9, 0xef, 0x5b, 0x90, 0xfd, 0x55, 0xca);
    static readonly Guid IidCodecApi = new(0x901db4c7, 0x31ce, 0x41a2, 0x85, 0xdc, 0x8f, 0xa0, 0xbf, 0x41, 0xb8, 0xda);
    // No bitrate cap (his call): every picture at the same quality, the bitrate follows the picture.
    const uint Qp = 19;
    const uint AvgBps = 20_000_000; // the output type must name one; quality mode ignores it

    void SetUp(bool hardware)
    {
        _hardware = hardware;
        if (hardware)
        {
            // Card encoders only work the asynchronous way: they ask for each picture and say when output is ready.
            using var attrs = _mft.Attributes; // handed back, or the encoder outlives the cast
            if (attrs.GetUInt32(TransformAttributeKeys.TransformAsync) == 0) throw new SharpGen.Runtime.SharpGenException("card encoder is not asynchronous");
            attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            _events = _mft.QueryInterface<IMFMediaEventGenerator>();
            _manager = MediaFactory.MFCreateDXGIDeviceManager();
            _manager.ResetDevice(_grab.Device).CheckError();
            _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_manager.NativePointer);
        }

        if (Marshal.QueryInterface(_mft.NativePointer, IidCodecApi, out _codecApi) != 0) _codecApi = IntPtr.Zero;
        // Some must be set before the output type; each card takes what it supports.
        var skipped = new List<string>();
        if (!Set(RateControlMode, 3) || !SetQp(Qp)) skipped.Add("quality mode"); // constant quality
        if (!Set(GopSize, (uint)_fps)) skipped.Add("keyframe spacing"); // a keyframe each second: every piece starts with one
        if (!Set(BFrames, 0)) skipped.Add("b-frames");
        if (!SetBool(LowLatency)) skipped.Add("low latency");
        if (!SetBool(Cabac)) skipped.Add("cabac");
        if (skipped.Count > 0) Log.Write("encoder: settings it didn't take: " + string.Join(", ", skipped));

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, AvgBps);
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u); // High
            Picture(output);
            _mft.SetOutputType(0, output, 0);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            Picture(input);
            _mft.SetInputType(0, input, 0);
        }
        var info = _mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & 0x300) != 0; // provides, or can provide, its own output samples
        _outSize = Math.Max(info.Size, _grab.Width * _grab.Height);
        ReadHeader();
    }

    // Size, rate and colors (BT.709, 16 to 235: what the screen grab makes and what TVs expect).
    void Picture(IMFMediaType t)
    {
        MediaFactory.MFSetAttributeSize(t, MediaTypeAttributeKeys.FrameSize, (uint)_grab.Width, (uint)_grab.Height).CheckError();
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        t.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);       // progressive
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);      // BT.709
        t.Set(MediaTypeAttributeKeys.TransferFunction, 5u);    // BT.709
        t.Set(MediaTypeAttributeKeys.YuvMatrix, 1u);           // BT.709
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);   // 16 to 235
    }

    void ReadHeader()
    {
        try
        {
            using var t = _mft.GetOutputCurrentType(0);
            if (t.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out uint size).Success && size > 0)
                _header = t.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
        }
        catch (SharpGen.Runtime.SharpGenException) { } // some only put it in front of the first keyframe
    }

    unsafe bool Set(Guid key, Variant24 v)
    {
        if (_codecApi == IntPtr.Zero) return false;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant24*, int>)(*(IntPtr**)_codecApi)[9];
        return setValue(_codecApi, &key, &v) >= 0;
    }

    bool Set(Guid key, uint value) => Set(key, new Variant24 { Type = 19 /* VT_UI4 */, UInt = value });
    bool SetBool(Guid key) => Set(key, new Variant24 { Type = 11 /* VT_BOOL */, Bool = -1 });
    // Windows' spec says 64-bit; some encoders only take it as 32-bit.
    bool SetQp(uint qp) => Set(EncodeQp, new Variant24 { Type = 21 /* VT_UI8 */, ULong = qp }) || Set(EncodeQp, qp);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant24
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public ulong ULong;
        [FieldOffset(8)] public short Bool;
    }
}
