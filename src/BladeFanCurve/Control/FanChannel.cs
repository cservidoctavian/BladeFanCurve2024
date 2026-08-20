using BladeFanCurve.Config;
using BladeFanCurve.Hardware;

namespace BladeFanCurve.Control;

/// <summary>
/// Per-fan state: temperature smoothing, curve lookup and RPM ramping.
///
/// Temperature rises are followed immediately so a sudden load spike gets airflow
/// straight away; falls are rate limited, which is what stops the fan oscillating
/// around a curve knee. RPM is then rate limited separately so the fan never steps
/// audibly from one speed to another.
/// </summary>
public sealed class FanChannel
{
    public FanZone Zone { get; }
    public string Name { get; }

    public double SmoothedTempC { get; private set; } = double.NaN;
    public int CurveDemandRpm { get; private set; }
    public int CommandedRpm { get; private set; }
    public int MeasuredRpm { get; set; }
    public int LastSentRpm { get; private set; } = -1;

    public FanChannel(FanZone zone, string name)
    {
        Zone = zone;
        Name = name;
    }

    public void Reset(int rpm)
    {
        SmoothedTempC = double.NaN;
        CommandedRpm = rpm;
        CurveDemandRpm = rpm;
        LastSentRpm = -1;
        MeasuredRpm = 0;
    }

    public void MarkSent(int rpm) => LastSentRpm = rpm;

    /// <summary>Computes the RPM this fan should be running at right now.</summary>
    public int Compute(
        double tempC,
        FanCurveConfig curve,
        SafetySettings safety,
        TuningSettings tuning,
        double dtSeconds,
        int floorRpm,
        bool criticalOverride)
    {
        dtSeconds = Math.Clamp(dtSeconds, 0.05, 10.0);

        if (double.IsNaN(SmoothedTempC)) SmoothedTempC = tempC;
        else if (tempC >= SmoothedTempC) SmoothedTempC = tempC;
        else SmoothedTempC = Math.Max(tempC, SmoothedTempC - tuning.TempFallRateCPerSec * dtSeconds);

        CurveDemandRpm = FanCurveEvaluator.Evaluate(curve, SmoothedTempC, safety.MinRpm, safety.MaxRpm);

        var target = Math.Max(CurveDemandRpm, floorRpm);
        if (criticalOverride) target = safety.MaxRpm;

        target = Math.Clamp(target, safety.MinRpm, safety.MaxRpm);

        if (CommandedRpm <= 0)
        {
            CommandedRpm = target;
        }
        else if (target > CommandedRpm)
        {
            // A critical override is allowed to bypass the ramp limit entirely.
            var step = criticalOverride ? int.MaxValue : (int)Math.Round(tuning.RampUpRpmPerSec * dtSeconds);
            CommandedRpm = (int)Math.Min((long)CommandedRpm + step, target);
        }
        else if (target < CommandedRpm)
        {
            var step = (int)Math.Round(tuning.RampDownRpmPerSec * dtSeconds);
            CommandedRpm = (int)Math.Max((long)CommandedRpm - step, target);
        }

        CommandedRpm = FanCurveEvaluator.Quantise(CommandedRpm, safety.MinRpm, safety.MaxRpm);
        return CommandedRpm;
    }

    /// <summary>True when the newly computed value is worth putting on the wire.</summary>
    public bool ShouldSend(int deadband) =>
        LastSentRpm < 0 || Math.Abs(CommandedRpm - LastSentRpm) >= deadband;
}
