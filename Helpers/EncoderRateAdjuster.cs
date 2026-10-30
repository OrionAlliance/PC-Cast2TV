namespace PCCast2TV.Helpers;

// WebRTC's encoder adjuster (encoder_bitrate_adjuster.cc): measures how far the encoder runs over what it's told,
// then tells it that much less, so what it makes matches the bitrate wanted.
public sealed class EncoderRateAdjuster(int fps)
{
    // 3 s of pictures; the factor stays between 1 and 2 (never below half the wanted rate); 1.2 until 30 pictures are in.
    const double WindowMs = 3000, Default = 1.2, MaxFactor = 2;
    const int FirstPictures = 30;
    readonly int _fps = fps;
    readonly Queue<(double Factor, double AtMs)> _factors = new();
    double _sum, _toldBps, _bufferBits, _lastMs = -1;
    int _pictures;

    // How far over it ran, from the last 3 s (1: on target or under).
    public double Factor { get; private set; } = Default;

    // What to tell the encoder for the bitrate wanted; later pictures are measured against that.
    public int Tell(int wantedBps, double nowMs)
    {
        Leak(nowMs);
        while (_factors.Count > 0 && _factors.Peek().AtMs < nowMs - WindowMs) _sum = Math.Max(0, _sum - _factors.Dequeue().Factor);
        Factor = _pictures < FirstPictures || _factors.Count == 0 ? Default : Math.Clamp(_sum / _factors.Count, 1, MaxFactor);
        _toldBps = Math.Round(wantedBps / Factor);
        return (int)_toldBps;
    }

    // Each finished picture against a virtual sender draining at the told rate: only bits it couldn't drain in time count as over.
    public void OnPicture(int bytes, double nowMs)
    {
        if (bytes == 0 || _toldBps <= 0) return;
        Leak(nowMs);
        double bits = bytes * 8.0, ideal = _toldBps / _fps, over = 0;
        if (bits + _bufferBits > ideal) over = Math.Min(_bufferBits, bits + _bufferBits - ideal);
        double factor = _factors.Count == 0 ? Math.Max(1, bits / ideal) : 1 + over / ideal;
        _bufferBits += bits - over;
        _factors.Enqueue((factor, nowMs));
        _sum += factor;
        _pictures++;
    }

    void Leak(double nowMs)
    {
        if (_lastMs >= 0) _bufferBits = Math.Max(0, _bufferBits - _toldBps * (nowMs - _lastMs) / 1000);
        _lastMs = nowMs;
    }
}
