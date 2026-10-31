using System.Diagnostics;

namespace PCCast2TV.Helpers;

// The stream controller's wiring: the Settings switch, each confirmation in, the encoder's bitrate out, and its log lines.
public sealed partial class MirrorSender
{
    RateController _controller;
    readonly Action<int> _setBitRate;
    readonly Func<bool> _controllerOn;
    double _sentRate = MaxBitRate, _lowestRate = MaxBitRate, _peakTrend;
    int _cutsAtReport, _behindTimes;
    long _behindSince, _longestBehind, _sentAtControl;
    readonly List<string> _notes = new(); // written once the lock is let go, so the disk never holds up the stream

    void Note(string line) => _notes.Add(line);

    // Follows the Settings switch live and hears each confirmation; the encoder gets any cut at once, rises in 5% steps.
    void Control(long firstSent, long now, int bytes)
    {
        bool on = _controllerOn();
        if (on != (_controller != null))
        {
            _controller = on ? NewController() : null;
            Note("cast: stream controller turned " + (on ? "on" : "off"));
            (_lowestRate, _cutsAtReport) = (MaxBitRate, 0);
            if (!on && _sentRate < MaxBitRate) _setBitRate(MaxBitRate); // off: straight back to the cap
            _sentRate = MaxBitRate;
        }
        if (_controller == null) return;
        double ms = 1000.0 / Stopwatch.Frequency, before = _controller.Bitrate;
        int letGo = _controller.LetGo, wentBack = _controller.WentBack;
        long sent = SentBytes;
        bool wasBehind = _controller.FallingBehind, changed = _controller.OnFeedback(firstSent * ms, now * ms, bytes, _rtt * ms, sent - _sentAtControl);
        _sentAtControl = sent;
        _peakTrend = Math.Max(_peakTrend, _controller.DelayTrendMs);
        if (_controller.LetGo != letGo)
            Note($"cast: controller let go of where the TV fell behind ({_controller.LetGoOfMbps:0.0} Mbps), the TV took {_controller.Took}, so it climbs fast again");
        if (_controller.WentBack != wentBack)
            Note($"cast: controller went back up to {_controller.Bitrate / 1e6:0.00} Mbps: it had cut while the picture was calm, and the TV recovered");
        if (!wasBehind && _controller.FallingBehind)
        {
            (_behindSince, _behindTimes) = (now, _behindTimes + 1);
            Note($"cast: controller saw the TV falling behind (delay trend {_controller.DelayTrendMs:0} ms over its {_controller.DelayLimitMs:0} ms limit), the TV took {_controller.Took}" +
                      (_controller.Calm ? ", the picture was calm" : ""));
        }
        else if (wasBehind && !_controller.FallingBehind)
        {
            _longestBehind = Math.Max(_longestBehind, now - _behindSince);
            Note($"cast: controller saw the TV catch up after {(now - _behindSince) * ms:0} ms, bitrate now {_controller.Bitrate / 1e6:0.0} Mbps");
        }
        if (!changed) return;
        double rate = _controller.Bitrate;
        if (rate < before) Note($"cast: controller cut the bitrate from {before / 1e6:0.00} to {rate / 1e6:0.00} Mbps");
        _lowestRate = Math.Min(_lowestRate, rate);
        if (rate >= _sentRate && rate < _sentRate * 1.05) return;
        _sentRate = rate;
        _setBitRate((int)rate);
    }

    // The controller starts at the cap; 300 kbps is the mirroring spec's floor.
    static RateController NewController() => new(MaxBitRate, 300_000);

    // The controller's part of the 10-second line, then a fresh count for the next one.
    string ControllerReport()
    {
        if (_controller == null) return ", controller off";
        string s = $", controller {_controller.Bitrate / 1e6:0.0} Mbps (lowest {_lowestRate / 1e6:0.0}, " +
                   $"cut {_controller.Cuts - _cutsAtReport} times, delay limit {_controller.DelayLimitMs:0} ms)" +
                   $"\ncontroller: saw the TV fall behind {_behindTimes} time{(_behindTimes == 1 ? "" : "s")}" +
                   (_behindTimes > 0 ? $" (longest {_longestBehind * 1000 / Stopwatch.Frequency} ms)" : "") +
                   $", biggest delay trend {_peakTrend:0} ms against a {_controller.DelayLimitMs:0} ms limit, the TV took {_controller.Took}, now {_controller.Doing}";
        (_lowestRate, _cutsAtReport, _behindTimes, _longestBehind, _peakTrend) = (_controller.Bitrate, _controller.Cuts, 0, 0, 0);
        return s;
    }
}
