namespace PCCast2TV.Helpers;

// WebRTC's congestion control (goog_cc) for mirroring: watches whether the TV's confirmations fall behind, sets the bitrate.
public sealed class RateController(double maxBps, double minBps)
{
    enum Usage { Normal, Overusing, Underusing }
    enum Step { Hold, Increase, Decrease }

    // Delay trend (trendline_estimator.cc): 20 samples, smoothing 0.9, gain 4, a 12.5 ms limit that adapts between 6 and 600.
    const int Window = 20, MinDeltas = 60, MaxDeltas = 1000;
    const double Smoothing = 0.9, Gain = 4, KUp = 0.0087, KDown = 0.039, MaxAdaptOffset = 15, OverusingTime = 10;
    readonly Queue<(double X, double Y)> _history = new();
    double _prevSent = double.NaN, _prevArrived, _firstArrival = -1, _accumulated, _smoothed, _threshold = 12.5, _prevTrend, _timeOverUsing = -1, _lastThreshold = -1;
    int _deltas, _overuseCount;
    Usage _usage;

    // What the TV took, Chrome's cast sender's way (bandwidth_estimator.cc): confirmed bytes at arrival less a round trip,
    // summed over the last 2.56 s in 256 slices of 10 ms.
    const int Slices = 256;
    const double SliceMs = 10;
    readonly long[] _slice = new long[Slices];
    long _newest = long.MinValue, _first, _inWindow;
    double _ackedKbps = -1;

    // Where it last overloaded (link_capacity_estimator.cc).
    double? _capacityKbps;
    double _deviation = 0.4;

    // The rate (aimd_rate_control.cc): cut to 85% of what got through, hold, then climb 8% a second, slower near the last overload.
    readonly double _max = maxBps, _min = minBps;
    double _bitrate = maxBps, _lastChange = double.NegativeInfinity;
    Step _step = Step.Hold;

    // Back up after a calm-picture drop (probe_controller.cc): under 66% within 5 s, calm or just calm: 85% once the TV recovers.
    // WebRTC probes first; Cast TVs can't report a probe, so the TV recovering stands in for it.
    const double DropRatio = 0.66, DropTimeoutMs = 5000, CalmEndedMs = 3000, BackFraction = 0.85, BackUncertainty = 0.05, MinBetweenMs = 5000;
    readonly AlrDetector _alr = new(maxBps);
    double _dropAtMs = double.NegativeInfinity, _beforeDrop, _wentBackAtMs = double.NegativeInfinity;

    public double Bitrate => _bitrate;
    public double DelayLimitMs => _threshold;
    public int Cuts { get; private set; }
    // For the log: falling behind or not, the delay trend against its limit, what the TV took, and what it's doing.
    public bool FallingBehind => _usage == Usage.Overusing;
    public double DelayTrendMs { get; private set; }
    public string Took => _ackedKbps < 0 ? "not measured yet" : $"{_ackedKbps / 1000:0.0} Mbps";
    public string Doing => _step switch { Step.Increase => "rising", Step.Decrease => "cutting", _ => "holding" };
    // How many times it let go of where the TV last fell behind (the TV took more than that point allows), and that point.
    public int LetGo { get; private set; }
    public double LetGoOfMbps { get; private set; }
    // Whether the picture is calm (using under 65% of the rate), and how many times it went back up after a calm-picture cut.
    public bool Calm => _alr.StartedMs != null;
    public int WentBack { get; private set; }

    // A confirmation that moved the TV's checkpoint: the newest picture's first send, when the confirmation came, the bytes it covered,
    // and all bytes sent since the last one; true when the rate changed.
    public bool OnFeedback(double sentMs, double arrivedMs, int bytes, double rttMs, long sentBytes)
    {
        _alr.OnSent(sentBytes, arrivedMs);
        Acked(arrivedMs - rttMs, bytes);
        var was = _usage;
        if (!double.IsNaN(_prevSent) && sentMs > _prevSent) Trend(arrivedMs - _prevArrived, sentMs - _prevSent, arrivedMs);
        if (double.IsNaN(_prevSent) || sentMs > _prevSent) (_prevSent, _prevArrived) = (sentMs, arrivedMs);
        double before = _bitrate;
        if (_usage != Usage.Overusing) Change(arrivedMs, rttMs);
        else if (_ackedKbps > 0 && TimeToReduceFurther(arrivedMs, rttMs)) Change(arrivedMs, rttMs);
        if (_bitrate < DropRatio * before) (_dropAtMs, _beforeDrop) = (arrivedMs, before);
        if (was == Usage.Underusing && _usage == Usage.Normal) GoBack(arrivedMs); // WebRTC's "recovered from overuse"
        if (_bitrate != before) _alr.SetBitrate(_bitrate);
        return _bitrate != before;
    }

    void GoBack(double nowMs)
    {
        bool calm = Calm || _alr.EndedMs is { } ended && nowMs - ended < CalmEndedMs;
        double back = BackFraction * _beforeDrop;
        if (!calm || (1 - BackUncertainty) * back <= _bitrate || nowMs - _dropAtMs >= DropTimeoutMs || nowMs - _wentBackAtMs <= MinBetweenMs) return;
        (_bitrate, _lastChange, _wentBackAtMs) = (Math.Min(_max, back), nowMs, nowMs);
        WentBack++;
    }

    void Trend(double recvDelta, double sendDelta, double arrivedMs)
    {
        _deltas = Math.Min(_deltas + 1, MaxDeltas);
        if (_firstArrival < 0) _firstArrival = arrivedMs;
        _accumulated += recvDelta - sendDelta;
        _smoothed = Smoothing * _smoothed + (1 - Smoothing) * _accumulated;
        _history.Enqueue((arrivedMs - _firstArrival, _smoothed));
        if (_history.Count > Window) _history.Dequeue();
        double trend = _history.Count == Window ? Slope() ?? _prevTrend : _prevTrend;
        Detect(trend, sendDelta, arrivedMs);
    }

    double? Slope()
    {
        double xAvg = _history.Average(p => p.X), yAvg = _history.Average(p => p.Y), num = 0, den = 0;
        foreach (var (x, y) in _history)
        {
            num += (x - xAvg) * (y - yAvg);
            den += (x - xAvg) * (x - xAvg);
        }
        return den == 0 ? null : num / den;
    }

    // Overuse only when the trend stays over the limit past 10 ms and is still rising; the limit itself follows what's normal for this TV.
    void Detect(double trend, double sendDelta, double nowMs)
    {
        if (_deltas < 2) { _usage = Usage.Normal; return; }
        double modified = Math.Min(_deltas, MinDeltas) * trend * Gain;
        DelayTrendMs = modified;
        if (modified > _threshold)
        {
            _timeOverUsing = _timeOverUsing < 0 ? sendDelta / 2 : _timeOverUsing + sendDelta;
            _overuseCount++;
            if (_timeOverUsing > OverusingTime && _overuseCount > 1 && trend >= _prevTrend) (_timeOverUsing, _overuseCount, _usage) = (0, 0, Usage.Overusing);
        }
        else if (modified < -_threshold) (_timeOverUsing, _overuseCount, _usage) = (-1, 0, Usage.Underusing);
        else (_timeOverUsing, _overuseCount, _usage) = (-1, 0, Usage.Normal);
        _prevTrend = trend;
        if (_lastThreshold < 0) _lastThreshold = nowMs;
        if (Math.Abs(modified) > _threshold + MaxAdaptOffset) { _lastThreshold = nowMs; return; }
        double k = Math.Abs(modified) < _threshold ? KDown : KUp;
        _threshold = Math.Clamp(_threshold + k * (Math.Abs(modified) - _threshold) * Math.Min(nowMs - _lastThreshold, 100), 6, 600);
        _lastThreshold = nowMs;
    }

    // Old slices empty as the window slides, too-old bytes are dropped; like Open Screen, it reads once half the window is in.
    void Acked(double leftMs, int bytes)
    {
        long n = (long)Math.Floor(leftMs / SliceMs);
        if (_newest == long.MinValue) (_newest, _first) = (n, n);
        for (long i = _newest + 1; i <= Math.Min(n, _newest + Slices); i++)
        {
            _inWindow -= _slice[i % Slices];
            _slice[i % Slices] = 0;
        }
        _newest = Math.Max(_newest, n);
        if (n > _newest - Slices)
        {
            _slice[n % Slices] += bytes;
            _inWindow += bytes;
        }
        long covered = Math.Min(_newest - _first + 1, Slices);
        if (covered >= Slices / 2) _ackedKbps = _inWindow * 8 / (covered * SliceMs);
    }

    bool TimeToReduceFurther(double nowMs, double rttMs) => nowMs - _lastChange >= Math.Clamp(rttMs, 10, 200) || _ackedKbps * 1000 < 0.5 * _bitrate;

    void Change(double nowMs, double rttMs)
    {
        switch (_usage)
        {
            case Usage.Normal when _step == Step.Hold: (_lastChange, _step) = (nowMs, Step.Increase); break;
            case Usage.Overusing: _step = Step.Decrease; break;
            case Usage.Underusing: _step = Step.Hold; break;
        }
        double acked = _ackedKbps * 1000;
        if (_step == Step.Increase)
        {
            if (acked > Upper()) (LetGoOfMbps, LetGo, _capacityKbps) = (_capacityKbps!.Value / 1000, LetGo + 1, null);
            // WebRTC's cap on a rise (1.5x what got through) is left out: it needs probes, which Cast TVs can't answer.
            double rise = _capacityKbps != null
                ? NearMaxRiseBps(rttMs) * (nowMs - _lastChange) / 1000
                : Math.Max(_bitrate * (Math.Pow(1.08, Math.Min((nowMs - _lastChange) / 1000, 1.0)) - 1), 1000);
            _bitrate = Math.Min(_max, _bitrate + rise);
            _lastChange = nowMs;
        }
        else if (_step == Step.Decrease)
        {
            double lower = acked * 0.85;
            if (lower > 5000) lower -= 5000;
            if (lower > _bitrate && _capacityKbps is { } c) lower = 0.85 * c * 1000;
            if (lower < _bitrate) { _bitrate = Math.Max(_min, lower); Cuts++; }
            if (acked < Lower()) _capacityKbps = null;
            Capacity(acked / 1000);
            (_step, _lastChange) = (Step.Hold, nowMs); // hold until things clear
        }
    }

    // Near the last overload it climbs about one packet per round trip, as WebRTC does.
    double NearMaxRiseBps(double rttMs)
    {
        double frameBytes = _bitrate / 30 / 8, perPacket = frameBytes / Math.Ceiling(frameBytes / 1200);
        return Math.Max(4000, perPacket * 8 / ((rttMs + 100) * 2 / 1000));
    }

    void Capacity(double sampleKbps)
    {
        const double alpha = 0.05;
        _capacityKbps = _capacityKbps is { } e ? (1 - alpha) * e + alpha * sampleKbps : sampleKbps;
        double err = _capacityKbps.Value - sampleKbps;
        _deviation = Math.Clamp((1 - alpha) * _deviation + alpha * err * err / Math.Max(_capacityKbps.Value, 1.0), 0.4, 2.5);
    }

    double Spread() => 3 * Math.Sqrt(_deviation * _capacityKbps!.Value);
    double Upper() => _capacityKbps is { } c ? (c + Spread()) * 1000 : double.PositiveInfinity;
    double Lower() => _capacityKbps is { } c ? Math.Max(0, c - Spread()) * 1000 : 0;
}
