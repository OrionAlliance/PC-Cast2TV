using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace PCCast2TV.Helpers;

// Windows' own AAC encoder: 16-bit stereo at 48 or 44.1 kHz in, timed AAC frames with ADTS headers out.
public sealed class SoundEncoder : IDisposable
{
    public delegate void FrameHandler(byte[] adts, long pts90k);

    const uint EnumSync = 0x1, EnumSortAndFilter = 0x40;
    const int NeedMoreInput = unchecked((int)0xC00D6D72);
    const uint BytesPerSecond = 20_000; // 160 kbps

    readonly IMFTransform _mft;
    readonly FrameHandler _onFrame;
    readonly int _rate;
    readonly int _outSize;
    long _fed;            // samples handed to the encoder, for its input times
    IMFSample _out;       // the one sample it writes its frames into, used again for each

    // Throws InvalidOperationException when Windows' AAC encoder won't start (no sound in the cast then).
    public SoundEncoder(int rate, FrameHandler onFrame)
    {
        (_rate, _onFrame) = (rate, onFrame);
        try
        {
            VideoEncoder.EnsureStarted();
            using var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder, EnumSync | EnumSortAndFilter,
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm },
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac });
            foreach (var a in found)
                using (a)
                    if (_mft == null) _mft = a.ActivateObject<IMFTransform>();
            if (_mft == null) throw new InvalidOperationException("Windows has no AAC encoder");
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_rate);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, BytesPerSecond);
                output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u); // raw frames; the ADTS header is added here
                output.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                _mft.SetOutputType(0, output, 0);
            }
            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_rate);
                input.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)_rate * 4);
                _mft.SetInputType(0, input, 0);
            }
            _outSize = Math.Max(_mft.GetOutputStreamInfo(0).Size, 8192);
            _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            _mft?.Dispose();
            throw new InvalidOperationException("Windows' AAC encoder would not start: " + ex.Message, ex);
        }
        Log.Write($"sound: AAC {_rate} Hz stereo");
    }

    // 16-bit stereo at the encoder's rate; called from the sound writer's thread only.
    public void Add(byte[] pcm, int length)
    {
        if (length == 0) return;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out var at, out _, out _);
        Marshal.Copy(pcm, 0, at, length);
        buffer.Unlock();
        buffer.CurrentLength = length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        int samples = length / 4;
        sample.SampleTime = _fed * 10_000_000 / _rate;
        sample.SampleDuration = (long)samples * 10_000_000 / _rate;
        _fed += samples;
        _mft.ProcessInput(0, sample, 0);
        while (TakeOutput()) { }
    }

    bool TakeOutput()
    {
        if (_out == null)
        {
            _out = MediaFactory.MFCreateSample();
            using var made = MediaFactory.MFCreateMemoryBuffer(_outSize);
            _out.AddBuffer(made);
        }
        using (var b = _out.GetBufferByIndex(0)) b.CurrentLength = 0;
        var odb = new OutputDataBuffer { StreamID = 0, Sample = _out };
        var r = _mft.ProcessOutput(0, 1, ref odb, out _);
        odb.Events?.Dispose();
        if (r.Code == NeedMoreInput) return false;
        r.CheckError();
        using var buf = _out.ConvertToContiguousBuffer();
        buf.Lock(out var at, out _, out int length);
        var frame = new byte[length + 7];
        try { Marshal.Copy(at, frame, 7, length); }
        finally { buf.Unlock(); }
        WriteAdts(frame, length);
        _onFrame(frame, (_out.SampleTime * 9 + 500) / 1000); // 100 ns to 90 kHz, rounded
        return true;
    }

    // 7-byte ADTS header: AAC LC, the sample rate, stereo, and the frame's length.
    void WriteAdts(byte[] f, int raw)
    {
        int len = raw + 7, rateIndex = _rate == 48000 ? 3 : 4;
        f[0] = 0xFF;
        f[1] = 0xF1;
        f[2] = (byte)((1 << 6) | (rateIndex << 2));
        f[3] = (byte)((2 << 6) | (len >> 11));
        f[4] = (byte)(len >> 3);
        f[5] = (byte)(((len & 7) << 5) | 0x1F);
        f[6] = 0xFC;
    }

    public void Dispose()
    {
        _out?.Dispose();
        _mft?.Dispose();
    }
}
