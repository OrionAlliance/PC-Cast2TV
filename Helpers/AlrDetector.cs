namespace PCCast2TV.Helpers;

// WebRTC's calm-picture detector (alr_detector.cc): a 500 ms budget at 65% of the bitrate; calm over 80% unspent, not under 50%.
public sealed class AlrDetector
{
    const double WindowMs = 500, UsageRatio = 0.65, StartRatio = 0.80, StopRatio = 0.50;
    double _bytesPerMs, _max, _remaining, _lastMs = -1;

    public AlrDetector(double bps) => SetBitrate(bps);

    public double? StartedMs { get; private set; }
    public double? EndedMs { get; private set; }

    public void SetBitrate(double bps)
    {
        _bytesPerMs = bps * UsageRatio / 8000;
        _max = WindowMs * _bytesPerMs;
        _remaining = Math.Clamp(_remaining, -_max, _max);
    }

    // Bytes sent since the last call; unspent budget carries over, as WebRTC's does.
    public void OnSent(long bytes, double nowMs)
    {
        if (_lastMs < 0) { _lastMs = nowMs; return; }
        _remaining = Math.Max(_remaining - bytes, -_max);
        _remaining = Math.Min(_remaining + _bytesPerMs * (nowMs - _lastMs), _max);
        _lastMs = nowMs;
        double ratio = _max == 0 ? 0 : _remaining / _max;
        if (ratio > StartRatio && StartedMs == null) StartedMs = nowMs;
        else if (ratio < StopRatio && StartedMs != null) (StartedMs, EndedMs) = (null, nowMs);
    }
}
