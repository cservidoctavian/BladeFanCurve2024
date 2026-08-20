using BladeFanCurve.Hardware;

namespace BladeFanCurve.Lighting;

/// <summary>Everything an effect is allowed to know about the world when it renders a frame.</summary>
public sealed class EffectContext
{
    public int Rows { get; init; } = RazerChroma.Rows;
    public int Columns { get; init; } = RazerChroma.Columns;

    /// <summary>Seconds since the effect started, already scaled by the speed setting.</summary>
    public double Time;

    /// <summary>Seconds since the previous frame, already scaled by the speed setting.</summary>
    public double Delta;

    public RgbColor Primary = new(0, 255, 136);
    public RgbColor Secondary = new(51, 85, 255);

    // Live telemetry, so lighting can react to the machine rather than just to a clock.
    public double? CpuTempC;
    public double? GpuTempC;
    public int CpuRpm;
    public int GpuRpm;
    public int MinRpm = 2000;
    public int MaxRpm = 5000;

    public Random Rng = new();
}

public abstract class SoftwareEffect
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>True when the effect reads CPU/GPU telemetry, so the UI can say so.</summary>
    public virtual bool UsesTelemetry => false;

    /// <summary>Called when the effect is selected, so stateful effects can start clean.</summary>
    public virtual void Reset(EffectContext ctx) { }

    public abstract void Render(RgbColor[,] frame, EffectContext ctx);

    protected static void Fill(RgbColor[,] frame, RgbColor c)
    {
        for (var r = 0; r < frame.GetLength(0); r++)
        for (var k = 0; k < frame.GetLength(1); k++)
            frame[r, k] = c;
    }
}

// ---------------------------------------------------------------- simple ones

public sealed class SolidEffect : SoftwareEffect
{
    public override string Id => "solid";
    public override string Name => "Solid";
    public override string Description => "One colour across every key.";

    public override void Render(RgbColor[,] frame, EffectContext ctx) => Fill(frame, ctx.Primary);
}

public sealed class GradientEffect : SoftwareEffect
{
    public override string Id => "gradient";
    public override string Name => "Gradient";
    public override string Description => "A still fade from the first colour to the second.";

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        for (var r = 0; r < ctx.Rows; r++)
        for (var c = 0; c < ctx.Columns; c++)
            frame[r, c] = RgbColor.Lerp(ctx.Primary, ctx.Secondary, c / (double)(ctx.Columns - 1));
    }
}

public sealed class BreathingSoftEffect : SoftwareEffect
{
    public override string Id => "breathe-soft";
    public override string Name => "Breathe (smooth)";
    public override string Description => "Like the built-in breathing, but rendered here so the fade is smooth.";

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        // Sine gives a soft turn at both ends; squaring biases it toward the dark half
        // so it reads as breathing rather than as a flashing light.
        var phase = (Math.Sin(ctx.Time * 1.6) + 1) / 2;
        Fill(frame, ctx.Primary.Scale(0.06 + 0.94 * phase * phase));
    }
}

public sealed class ColourCycleEffect : SoftwareEffect
{
    public override string Id => "cycle";
    public override string Name => "Colour cycle";
    public override string Description => "The whole keyboard walks through the spectrum together.";

    public override void Render(RgbColor[,] frame, EffectContext ctx) =>
        Fill(frame, RgbColor.FromHsv(ctx.Time * 60));
}

public sealed class RainbowWaveEffect : SoftwareEffect
{
    public override string Id => "rainbow";
    public override string Name => "Rainbow wave";
    public override string Description => "A spectrum that travels across the keyboard.";

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        for (var r = 0; r < ctx.Rows; r++)
        for (var c = 0; c < ctx.Columns; c++)
            frame[r, c] = RgbColor.FromHsv(ctx.Time * 90 - c * (360.0 / ctx.Columns) - r * 4);
    }
}

public sealed class ScannerEffect : SoftwareEffect
{
    public override string Id => "scanner";
    public override string Name => "Scanner";
    public override string Description => "A single bright column sweeping side to side, with a trailing fade.";

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        Fill(frame, RgbColor.Black);

        // Triangle wave over the column range gives a constant-speed sweep that turns
        // sharply at each end, unlike a sine which would slow down there.
        var span = ctx.Columns - 1;
        var t = ctx.Time * 0.9 % 2.0;
        var head = (t <= 1 ? t : 2 - t) * span;

        for (var c = 0; c < ctx.Columns; c++)
        {
            var distance = Math.Abs(c - head);
            if (distance > 3.5) continue;

            var intensity = Math.Pow(1 - distance / 3.5, 2.2);
            for (var r = 0; r < ctx.Rows; r++)
                frame[r, c] = ctx.Primary.Scale(intensity);
        }
    }
}

// ------------------------------------------------------------- stateful ones

public sealed class FireEffect : SoftwareEffect
{
    private double[,]? _heat;
    private double _carry;

    public override string Id => "fire";
    public override string Name => "Fire";
    public override string Description => "Heat rises from the space bar and flickers out toward the function row.";

    public override void Reset(EffectContext ctx) => _heat = null;

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        _heat ??= new double[ctx.Rows, ctx.Columns];

        // Advance on a fixed step so the flame looks the same regardless of frame rate.
        _carry += ctx.Delta;
        while (_carry >= 0.033)
        {
            _carry -= 0.033;
            Step(ctx);
        }

        for (var r = 0; r < ctx.Rows; r++)
        for (var c = 0; c < ctx.Columns; c++)
            frame[r, c] = Palette(_heat[r, c], ctx);
    }

    private void Step(EffectContext ctx)
    {
        var heat = _heat!;
        var bottom = ctx.Rows - 1;

        // Cool everything, then pull heat upward from the row below.
        for (var r = 0; r < bottom; r++)
        for (var c = 0; c < ctx.Columns; c++)
        {
            var left = heat[r + 1, Math.Max(0, c - 1)];
            var mid = heat[r + 1, c];
            var right = heat[r + 1, Math.Min(ctx.Columns - 1, c + 1)];
            heat[r, c] = (left + mid * 2 + right) / 4.0 * 0.82;
        }

        // Fresh fuel along the bottom row, with occasional flare-ups.
        for (var c = 0; c < ctx.Columns; c++)
        {
            var target = ctx.Rng.NextDouble() < 0.18 ? 1.0 : 0.55 + ctx.Rng.NextDouble() * 0.3;
            heat[bottom, c] += (target - heat[bottom, c]) * 0.45;
        }
    }

    /// <summary>Black to the primary colour to white, so the flame takes on the chosen hue.</summary>
    private static RgbColor Palette(double heat, EffectContext ctx)
    {
        heat = Math.Clamp(heat, 0, 1);
        if (heat < 0.5) return ctx.Primary.Scale(heat / 0.5 * 0.9);

        var t = (heat - 0.5) / 0.5;
        return RgbColor.Lerp(ctx.Primary, new RgbColor(255, 255, 210), t * 0.75);
    }
}

public sealed class RainEffect : SoftwareEffect
{
    private double[]? _position;
    private double[]? _speed;
    private bool[]? _active;

    public override string Id => "rain";
    public override string Name => "Rain";
    public override string Description => "Drops fall down the columns leaving a fading trail.";

    public override void Reset(EffectContext ctx) => _position = null;

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        if (_position == null || _position.Length != ctx.Columns)
        {
            _position = new double[ctx.Columns];
            _speed = new double[ctx.Columns];
            _active = new bool[ctx.Columns];
        }

        Fill(frame, RgbColor.Black);

        for (var c = 0; c < ctx.Columns; c++)
        {
            if (!_active![c])
            {
                // Stagger starts so the columns never march in step.
                if (ctx.Rng.NextDouble() >= 0.9 * ctx.Delta * 3) continue;
                _active[c] = true;
                _position![c] = -1;
                _speed![c] = 4 + ctx.Rng.NextDouble() * 6;
            }

            _position![c] += _speed![c] * ctx.Delta;
            if (_position[c] - 3 > ctx.Rows)
            {
                _active[c] = false;
                continue;
            }

            for (var r = 0; r < ctx.Rows; r++)
            {
                var distance = _position[c] - r;
                if (distance < 0 || distance > 3) continue;

                var intensity = 1 - distance / 3.0;
                frame[r, c] = RgbColor.Max(frame[r, c],
                    RgbColor.Lerp(ctx.Primary, new RgbColor(255, 255, 255), distance < 0.4 ? 0.6 : 0)
                        .Scale(intensity * intensity));
            }
        }
    }
}

public sealed class RippleEffect : SoftwareEffect
{
    private readonly List<(double Row, double Col, double Age)> _ripples = new();
    private double _sinceSpawn;

    public override string Id => "ripple";
    public override string Name => "Ripple";
    public override string Description => "Rings spread outward from points on the keyboard.";

    public override void Reset(EffectContext ctx)
    {
        _ripples.Clear();
        _sinceSpawn = 0;
    }

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        _sinceSpawn += ctx.Delta;
        if (_sinceSpawn > 0.55)
        {
            _sinceSpawn = 0;
            _ripples.Add((ctx.Rng.Next(ctx.Rows), ctx.Rng.Next(ctx.Columns), 0));
        }

        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var r = _ripples[i];
            r.Age += ctx.Delta;
            if (r.Age > 2.2) _ripples.RemoveAt(i);
            else _ripples[i] = r;
        }

        Fill(frame, RgbColor.Black);

        foreach (var (row, col, age) in _ripples)
        {
            var radius = age * 9;
            var fade = Math.Max(0, 1 - age / 2.2);

            for (var r = 0; r < ctx.Rows; r++)
            for (var c = 0; c < ctx.Columns; c++)
            {
                // Rows are much taller than columns are wide on a real keyboard, so
                // stretch the vertical axis to keep the ring looking circular.
                var dr = (r - row) * 2.2;
                var dc = c - col;
                var distance = Math.Sqrt(dr * dr + dc * dc);

                var onRing = 1 - Math.Min(1, Math.Abs(distance - radius) / 1.8);
                if (onRing <= 0) continue;

                var colour = RgbColor.Lerp(ctx.Primary, ctx.Secondary, Math.Min(1, radius / 12));
                frame[r, c] = RgbColor.Max(frame[r, c], colour.Scale(onRing * onRing * fade));
            }
        }
    }
}

public sealed class StarfieldEffect : SoftwareEffect
{
    private double[,]? _phase;
    private double[,]? _rate;

    public override string Id => "starfield";
    public override string Name => "Starfield";
    public override string Description => "Keys twinkle in and out at their own pace.";

    public override void Reset(EffectContext ctx) => _phase = null;

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        if (_phase == null || _phase.GetLength(1) != ctx.Columns)
        {
            _phase = new double[ctx.Rows, ctx.Columns];
            _rate = new double[ctx.Rows, ctx.Columns];
            for (var r = 0; r < ctx.Rows; r++)
            for (var c = 0; c < ctx.Columns; c++)
            {
                _phase[r, c] = ctx.Rng.NextDouble();
                _rate[r, c] = 0.25 + ctx.Rng.NextDouble() * 0.9;
            }
        }

        for (var r = 0; r < ctx.Rows; r++)
        for (var c = 0; c < ctx.Columns; c++)
        {
            _phase[r, c] += _rate![r, c] * ctx.Delta;
            if (_phase[r, c] > 1)
            {
                _phase[r, c] = 0;
                _rate[r, c] = 0.25 + ctx.Rng.NextDouble() * 0.9;
            }

            var intensity = Math.Sin(_phase[r, c] * Math.PI);
            var colour = RgbColor.Lerp(ctx.Primary, ctx.Secondary, (c / (double)ctx.Columns + r * 0.1) % 1);
            frame[r, c] = colour.Scale(intensity * intensity);
        }
    }
}

// ------------------------------------------------------------ telemetry ones

/// <summary>
/// The reason this app is a good place to put lighting: the keyboard becomes a
/// temperature gauge. Colour runs blue - green - amber - red, and the number of lit
/// columns tracks how far through the range the machine currently is.
/// </summary>
public sealed class ThermalEffect : SoftwareEffect
{
    public override string Id => "thermal";
    public override string Name => "Thermal";
    public override string Description => "Colour and fill follow the hottest of CPU and GPU.";
    public override bool UsesTelemetry => true;

    public const double ColdC = 45;
    public const double HotC = 95;

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        var temp = Math.Max(ctx.CpuTempC ?? 0, ctx.GpuTempC ?? 0);
        if (temp <= 0)
        {
            // No reading: a slow dim pulse rather than a confident wrong colour.
            Fill(frame, new RgbColor(60, 60, 70).Scale(0.4 + 0.3 * Math.Sin(ctx.Time)));
            return;
        }

        var t = Math.Clamp((temp - ColdC) / (HotC - ColdC), 0, 1);
        var colour = ColourFor(t);
        var lit = t * ctx.Columns;

        for (var r = 0; r < ctx.Rows; r++)
        for (var c = 0; c < ctx.Columns; c++)
        {
            // Partial brightness on the boundary column makes the gauge read smoothly
            // instead of stepping a whole column at a time.
            var fill = Math.Clamp(lit - c, 0, 1);
            frame[r, c] = colour.Scale(0.08 + 0.92 * fill);
        }
    }

    /// <summary>Hue 210 (blue) down to 0 (red), staying saturated the whole way.</summary>
    public static RgbColor ColourFor(double t) => RgbColor.FromHsv(210 - 210 * Math.Clamp(t, 0, 1));
}

/// <summary>The same idea applied to fan speed, which is useful when tuning a curve.</summary>
public sealed class FanMeterEffect : SoftwareEffect
{
    public override string Id => "fan-meter";
    public override string Name => "Fan meter";
    public override string Description => "Top rows show the CPU fan, bottom rows the GPU fan.";
    public override bool UsesTelemetry => true;

    public override void Render(RgbColor[,] frame, EffectContext ctx)
    {
        var span = Math.Max(1, ctx.MaxRpm - ctx.MinRpm);
        var cpu = Math.Clamp((ctx.CpuRpm - ctx.MinRpm) / (double)span, 0, 1);
        var gpu = Math.Clamp((ctx.GpuRpm - ctx.MinRpm) / (double)span, 0, 1);

        var split = ctx.Rows / 2;

        for (var r = 0; r < ctx.Rows; r++)
        {
            var isCpu = r < split;
            var level = isCpu ? cpu : gpu;
            var colour = isCpu ? ctx.Primary : ctx.Secondary;
            var lit = level * ctx.Columns;

            for (var c = 0; c < ctx.Columns; c++)
            {
                var fill = Math.Clamp(lit - c, 0, 1);
                frame[r, c] = colour.Scale(0.06 + 0.94 * fill);
            }
        }
    }
}

public static class EffectCatalog
{
    /// <summary>Every software effect, in the order the UI lists them.</summary>
    public static IReadOnlyList<SoftwareEffect> All { get; } = new SoftwareEffect[]
    {
        new SolidEffect(),
        new GradientEffect(),
        new BreathingSoftEffect(),
        new ColourCycleEffect(),
        new RainbowWaveEffect(),
        new ScannerEffect(),
        new StarfieldEffect(),
        new RippleEffect(),
        new RainEffect(),
        new FireEffect(),
        new ThermalEffect(),
        new FanMeterEffect(),
    };

    public static SoftwareEffect? Find(string? id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}
