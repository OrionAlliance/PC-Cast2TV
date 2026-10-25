namespace PCCast2TV.Helpers;

// One sound device's own format to 16-bit stereo at the cast's rate (what AAC takes): every channel folds into left
// and right by the speaker it feeds, mono goes to both.
public sealed class SoundConverter
{
    readonly int _inRate, _inChannels, _inBits, _inBlock, _rate;
    readonly bool _inFloat, _folded; // folded: more than 2 channels add into each side, so it can pass full scale
    readonly (float L, float R)[] _mix;
    double _resampleAt;   // where the next output sample falls in the source, when the rates differ
    float _lastL, _lastR; // the source's last sample, for the step across chunks when resampling

    // mask: the device's speaker map (0 when it doesn't say, then Windows' usual layout for that many channels).
    public SoundConverter(int inRate, int channels, int bits, bool isFloat, int mask, int rate)
    {
        (_inRate, _inChannels, _inBits, _inFloat, _rate) = (inRate, channels, bits, isFloat, rate);
        _inBlock = channels * bits / 8;
        _folded = channels > 2;
        _mix = new (float, float)[channels];
        if (channels == 1)
        {
            _mix[0] = (1, 1);
            return;
        }
        if (mask == 0) mask = channels switch { 2 => 0x3, 3 => 0x7, 4 => 0x33, 5 => 0x37, 6 => 0x3F, 7 => 0x13F, 8 => 0x63F, _ => 0 };
        int ch = 0;
        for (int speaker = 1; speaker != 0 && ch < channels; speaker <<= 1)
            if ((mask & speaker) != 0) _mix[ch++] = Share(speaker);
        for (; ch < channels; ch++) _mix[ch] = (0.5f, 0.5f); // more channels than the map names
    }

    // Each speaker's share of left and right: fronts straight across, center and surrounds at 0.707, the bass
    // channel left out (as standard downmixes do), height speakers to both.
    static (float, float) Share(int speaker) => speaker switch
    {
        0x1 or 0x40 => (1, 0),            // front left, front left of center
        0x2 or 0x80 => (0, 1),            // front right, front right of center
        0x4 or 0x100 => (0.707f, 0.707f), // front center, back center
        0x8 => (0, 0),                    // bass
        0x10 or 0x200 => (0.707f, 0),     // back left, side left
        0x20 or 0x400 => (0, 0.707f),     // back right, side right
        _ => (0.5f, 0.5f),
    };

    public byte[] ToStereo16(ReadOnlySpan<byte> data)
    {
        int frames = data.Length / _inBlock;
        var l = new float[frames];
        var r = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int at = i * _inBlock;
            for (int ch = 0; ch < _inChannels; ch++)
            {
                float v = Read(data, at, ch);
                l[i] += v * _mix[ch].L;
                r[i] += v * _mix[ch].R;
            }
        }
        if (_rate != _inRate) (l, r) = Resample(l, r);
        var o = new byte[l.Length * 4];
        for (int i = 0; i < l.Length; i++)
        {
            BitConverter.TryWriteBytes(o.AsSpan(i * 4), Clip(_folded ? Soft(l[i]) : l[i]));
            BitConverter.TryWriteBytes(o.AsSpan(i * 4 + 2), Clip(_folded ? Soft(r[i]) : r[i]));
        }
        return o;
    }

    float Read(ReadOnlySpan<byte> d, int at, int ch)
    {
        var p = d[(at + ch * _inBits / 8)..];
        if (_inFloat) return BitConverter.ToSingle(p);
        return _inBits switch
        {
            16 => BitConverter.ToInt16(p) / 32768f,
            24 => ((p[0] << 8) | (p[1] << 16) | (p[2] << 24)) / 2147483648f,
            _ => BitConverter.ToInt32(p) / 2147483648f,
        };
    }

    // The same scale reading uses, so 16-bit sound comes out exactly as it went in.
    static short Clip(float v) => (short)Math.Clamp((int)Math.Round(v * 32768), short.MinValue, short.MaxValue);

    // Above 0.8 a folded mix bends smoothly toward full scale instead of clipping hard.
    static float Soft(float v)
    {
        float a = Math.Abs(v);
        return a <= 0.8f ? v : Math.Sign(v) * (0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f));
    }

    // Straight-line steps from the source rate to the cast's; only for a device not at the cast's rate.
    (float[], float[]) Resample(float[] l, float[] r)
    {
        double step = (double)_inRate / _rate;
        var ol = new List<float>();
        var or = new List<float>();
        for (; _resampleAt < l.Length; _resampleAt += step)
        {
            int i = (int)Math.Floor(_resampleAt);
            double f = _resampleAt - i;
            float l0 = i < 0 ? _lastL : l[i], r0 = i < 0 ? _lastR : r[i];
            float l1 = i + 1 < l.Length ? l[i + 1] : l0, r1 = i + 1 < r.Length ? r[i + 1] : r0;
            ol.Add((float)(l0 + (l1 - l0) * f));
            or.Add((float)(r0 + (r1 - r0) * f));
        }
        _resampleAt -= l.Length;
        if (l.Length > 0) (_lastL, _lastR) = (l[^1], r[^1]);
        return (ol.ToArray(), or.ToArray());
    }
}
