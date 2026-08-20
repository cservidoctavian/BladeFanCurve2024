using BladeFanCurve.Config;

namespace BladeFanCurve.Control;

public static class FanCurveEvaluator
{
    /// <summary>
    /// Linear interpolation between curve points. Below the first point the first
    /// RPM is held; above the last point the last RPM is held. The result is always
    /// clamped into [minRpm, maxRpm] and quantised to the 100 RPM wire granularity.
    /// </summary>
    public static int Evaluate(FanCurveConfig curve, double tempC, int minRpm, int maxRpm)
    {
        var points = curve.Points;
        if (points.Count == 0) return Quantise(minRpm, minRpm, maxRpm);

        var sorted = points.Count > 1 && !IsSorted(points)
            ? points.OrderBy(p => p.TempC).ToList()
            : points;

        if (tempC <= sorted[0].TempC) return Quantise(sorted[0].Rpm, minRpm, maxRpm);

        var last = sorted[^1];
        if (tempC >= last.TempC) return Quantise(last.Rpm, minRpm, maxRpm);

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (tempC < a.TempC || tempC > b.TempC) continue;

            var span = b.TempC - a.TempC;
            if (span <= 0.0001) return Quantise(b.Rpm, minRpm, maxRpm);

            var t = (tempC - a.TempC) / span;
            var rpm = a.Rpm + t * (b.Rpm - a.Rpm);
            return Quantise((int)Math.Round(rpm), minRpm, maxRpm);
        }

        return Quantise(last.Rpm, minRpm, maxRpm);
    }

    /// <summary>Rounds to the nearest 100 RPM, because the protocol transmits rpm/100.</summary>
    /// <summary>
    /// The RPM the thermal guard demands: a percentage of the maximum, quantised so it
    /// lands on a value the wire can actually carry.
    /// </summary>
    public static int SpinUpRpm(SafetySettings safety)
    {
        var raw = (int)Math.Round(safety.MaxRpm * (safety.SpinUpPercent / 100.0));
        return Quantise(raw, 0, safety.MaxRpm);
    }

    public static int Quantise(int rpm, int minRpm, int maxRpm)
    {
        var clamped = Math.Clamp(rpm, minRpm, maxRpm);
        return (int)(Math.Round(clamped / 100.0) * 100);
    }

    private static bool IsSorted(IReadOnlyList<CurvePoint> points)
    {
        for (var i = 1; i < points.Count; i++)
            if (points[i].TempC < points[i - 1].TempC)
                return false;
        return true;
    }
}
