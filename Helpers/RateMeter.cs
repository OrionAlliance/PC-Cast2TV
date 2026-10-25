namespace PCCast2TV.Helpers;

// Turns byte counts into the readout: Mbps now, the peak, the two wave lines and the signal bars.
// The device grabs each piece in a quick burst, so rates are averaged over 4 seconds to roll instead of spike.
public sealed class RateMeter
{
    public const int History = 90;
    const int Smooth = 4, SignalWindow = 6;

    readonly Queue<long> _encRaw = new(), _sentRaw = new();
    long _lastEnc, _lastSent;

    public double[] EncodedLine { get; private set; } = new double[History];
    public double[] SentLine { get; private set; } = new double[History];
    public double Now { get; private set; }
    public double Peak { get; private set; }
    public int Bars { get; private set; }

    public void Reset(long encTotal, long sentTotal)
    {
        _encRaw.Clear();
        _sentRaw.Clear();
        _lastEnc = encTotal;
        _lastSent = sentTotal;
        EncodedLine = new double[History];
        SentLine = new double[History];
        Now = Peak = 0;
        Bars = 0;
        Measuring = true;
    }

    // Called once a second with the running totals.
    public void Tick(long encTotal, long sentTotal)
    {
        Push(_encRaw, encTotal - _lastEnc);
        Push(_sentRaw, sentTotal - _lastSent);
        _lastEnc = encTotal;
        _lastSent = sentTotal;

        double enc = Mbps(_encRaw.TakeLast(Smooth)), sent = Mbps(_sentRaw.TakeLast(Smooth));
        EncodedLine = EncodedLine.Skip(1).Append(enc).ToArray();
        SentLine = SentLine.Skip(1).Append(sent).ToArray();
        Now = sent;
        Peak = Math.Max(Peak, sent);

        // Signal = how much of what was made the device actually took. A piece counts as made the moment it's
        // written and gets fetched about a second later, so "taken" looks one second further ahead than "made".
        long made = _encRaw.Take(_encRaw.Count - 1).Sum(), taken = _sentRaw.Skip(1).Sum(), lately = _sentRaw.TakeLast(4).Sum();
        double ratio = made == 0 ? 0 : (double)taken / made;
        Measuring = _encRaw.Count < 2;
        Bars = Measuring || made == 0 || lately == 0 ? 0 : ratio >= 0.9 ? 5 : ratio >= 0.75 ? 4 : ratio >= 0.6 ? 3 : ratio >= 0.4 ? 2 : 1;
    }

    // True for the first second of a cast, before there is anything to compare.
    public bool Measuring { get; private set; }

    public string SignalLabel => Measuring ? "CHECKING" : Bars switch { 5 => "STRONG", 4 => "GOOD", 3 => "FAIR", 2 => "WEAK", 1 => "POOR", _ => "NO SIGNAL" };

    static void Push(Queue<long> q, long v)
    {
        q.Enqueue(Math.Max(0, v));
        while (q.Count > SignalWindow) q.Dequeue();
    }

    static double Mbps(IEnumerable<long> perSecond)
    {
        var list = perSecond.ToList();
        return list.Count == 0 ? 0 : list.Average() * 8 / 1_000_000;
    }
}
