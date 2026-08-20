namespace BladeFanCurve.Control;

public readonly record struct PowerSample(double AgeSeconds, double? CpuWatts, double? GpuWatts);

/// <summary>
/// A fixed 30-minute rolling window of package power, sampled once a second.
///
/// This lives on the control loop rather than the window so that history keeps
/// accumulating while the app is closed to the tray — a graph that resets every time
/// you open the window would be useless for spotting what happened during a game.
///
/// Gaps are preserved rather than interpolated. A missing reading is stored as null
/// and drawn as a break in the line, because joining across a gap would invent power
/// draw that was never measured.
/// </summary>
public sealed class PowerHistory
{
    public const int WindowSeconds = 30 * 60;

    private readonly object _gate = new();
    private readonly double?[] _cpu = new double?[WindowSeconds];
    private readonly double?[] _gpu = new double?[WindowSeconds];
    private readonly double[] _at = new double[WindowSeconds];

    private int _next;
    private int _count;

    public int Count { get { lock (_gate) return _count; } }

    /// <summary>Highest value seen in the window, for scaling the axis.</summary>
    public double PeakWatts { get; private set; }

    public void Add(double nowSeconds, double? cpuWatts, double? gpuWatts)
    {
        lock (_gate)
        {
            _at[_next] = nowSeconds;
            _cpu[_next] = Sensors.SensorSnapshot.PlausibleWatts(cpuWatts) ? cpuWatts : null;
            _gpu[_next] = Sensors.SensorSnapshot.PlausibleWatts(gpuWatts) ? gpuWatts : null;

            _next = (_next + 1) % WindowSeconds;
            if (_count < WindowSeconds) _count++;
        }
    }

    /// <summary>
    /// The window in chronological order, each sample tagged with how long ago it was
    /// taken. Samples older than the window are dropped.
    /// </summary>
    public IReadOnlyList<PowerSample> Snapshot(double nowSeconds)
    {
        lock (_gate)
        {
            var result = new List<PowerSample>(_count);
            var peak = 0.0;

            for (var i = 0; i < _count; i++)
            {
                // Walk from oldest to newest.
                var index = (_next - _count + i + WindowSeconds) % WindowSeconds;
                var age = nowSeconds - _at[index];
                if (age is < 0 or > WindowSeconds) continue;

                var cpu = _cpu[index];
                var gpu = _gpu[index];
                if (cpu is { } c && c > peak) peak = c;
                if (gpu is { } g && g > peak) peak = g;

                result.Add(new PowerSample(age, cpu, gpu));
            }

            PeakWatts = peak;
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
            PeakWatts = 0;
            Array.Clear(_cpu);
            Array.Clear(_gpu);
            Array.Clear(_at);
        }
    }

    /// <summary>
    /// Mean over the last <paramref name="seconds"/>, ignoring gaps. Returns null when
    /// nothing in that span was measured.
    /// </summary>
    public static double? Average(IReadOnlyList<PowerSample> samples, Func<PowerSample, double?> pick,
        double seconds)
    {
        double sum = 0;
        var n = 0;

        foreach (var s in samples)
        {
            if (s.AgeSeconds > seconds) continue;
            if (pick(s) is not { } v) continue;
            sum += v;
            n++;
        }

        return n == 0 ? null : sum / n;
    }
}
