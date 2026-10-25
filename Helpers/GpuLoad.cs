using System.Diagnostics;

namespace PCCast2TV.Helpers;

// How busy each engine on the graphics card is, across every program (Task Manager's "GPU Engine" counters), logged
// every 10 seconds during a cast. Engine names differ by brand, so every busy one is listed by name.
public static class GpuLoad
{
    static readonly object Gate = new();
    static Dictionary<string, CounterSample> _before = new();

    // Line: "gpu 3D 12%, VideoDecode 30%, ..." (busiest engine of each kind since the last call, idle kinds left out).
    // Encoder: the video encoder's load for the window, null when there's no reading. The first call only takes a starting reading.
    public static (string Line, double? Encoder) Sample()
    {
        lock (Gate)
        {
            try
            {
                var counter = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
                if (counter == null) return ("gpu load not offered by this Windows", null);
                var now = new Dictionary<string, CounterSample>();
                var engines = new Dictionary<string, double>(); // one physical engine, summed over every program using it
                foreach (InstanceData d in counter.Values)
                {
                    now[d.InstanceName] = d.Sample;
                    int at = d.InstanceName.IndexOf("_luid_", StringComparison.Ordinal); // pid_<n>_luid_<card>_phys_<p>_eng_<e>_engtype_<kind>
                    if (at < 0 || !_before.TryGetValue(d.InstanceName, out var before)) continue;
                    var engine = d.InstanceName[at..];
                    engines[engine] = engines.GetValueOrDefault(engine) + CounterSample.Calculate(before, d.Sample);
                }
                bool first = _before.Count == 0;
                _before = now;
                if (first) return ("gpu load: first reading", null);
                var kinds = engines
                    .GroupBy(e => Kind(e.Key))
                    .Select(g => (Kind: g.Key, Pct: Math.Min(100, g.Max(e => e.Value))))
                    .OrderByDescending(k => k.Pct)
                    .ToList();
                var list = string.Join(", ", kinds.Where(k => k.Pct >= 1).Select(k => $"{k.Kind} {k.Pct:0}%"));
                return ("gpu " + (list.Length == 0 ? "idle" : list), EncoderOf(kinds));
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return ("gpu load not readable: " + ex.Message, null);
            }
        }
    }

    // NVIDIA has its own "VideoEncode" engine; AMD encodes on its "Video Codec" engine. Null when the card has neither.
    static double? EncoderOf(List<(string Kind, double Pct)> kinds)
    {
        foreach (var name in new[] { "Encode", "Codec" })
            foreach (var k in kinds)
                if (k.Kind.Contains(name, StringComparison.OrdinalIgnoreCase)) return k.Pct;
        return null;
    }

    static string Kind(string engine)
    {
        int at = engine.IndexOf("_engtype_", StringComparison.Ordinal);
        var kind = at < 0 ? "" : engine[(at + 9)..];
        return kind.Length == 0 ? "other" : kind;
    }
}
