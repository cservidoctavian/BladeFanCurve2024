using System.Diagnostics;
using BladeFanCurve.Config;
using BladeFanCurve.Control;
using BladeFanCurve.Hardware;

namespace BladeFanCurve.Lighting;

/// <summary>Describes a hardware effect for the UI, including which controls it actually uses.</summary>
public sealed record HardwareEffectInfo(
    string Id,
    string Name,
    string Description,
    bool UsesPrimary = false,
    bool UsesSecondary = false,
    bool UsesSpeed = false,
    bool UsesDirection = false);

/// <summary>
/// Owns keyboard lighting.
///
/// Hardware effects are a single command: the keyboard controller runs them on its own
/// and they survive the app closing. Software effects are rendered here and streamed as
/// custom frames, which costs USB traffic but allows anything, including effects driven
/// by CPU and GPU temperature.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    public static readonly IReadOnlyList<HardwareEffectInfo> HardwareEffects = new[]
    {
        new HardwareEffectInfo("hw-off", "Off", "Backlight off."),
        new HardwareEffectInfo("hw-static", "Static", "One steady colour.", UsesPrimary: true),
        new HardwareEffectInfo("hw-breathe", "Breathe", "Fades one colour in and out.", UsesPrimary: true),
        new HardwareEffectInfo("hw-breathe-dual", "Breathe (two colours)", "Alternates between two colours.",
            UsesPrimary: true, UsesSecondary: true),
        new HardwareEffectInfo("hw-breathe-random", "Breathe (random)", "Fades through random colours."),
        new HardwareEffectInfo("hw-spectrum", "Spectrum", "Cycles the whole spectrum."),
        new HardwareEffectInfo("hw-wave", "Wave", "A spectrum wave across the keyboard.", UsesDirection: true),
        new HardwareEffectInfo("hw-reactive", "Reactive", "Keys light up when pressed, then fade.",
            UsesPrimary: true, UsesSpeed: true),
        new HardwareEffectInfo("hw-starlight", "Starlight", "Random keys twinkle.",
            UsesPrimary: true, UsesSpeed: true),
        new HardwareEffectInfo("hw-starlight-dual", "Starlight (two colours)", "Twinkles in two colours.",
            UsesPrimary: true, UsesSecondary: true, UsesSpeed: true),
        new HardwareEffectInfo("hw-starlight-random", "Starlight (random)", "Twinkles in random colours.",
            UsesSpeed: true),
    };

    private readonly RazerChroma _chroma;
    private readonly object _gate = new();
    private readonly RgbColor[,] _frame = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private SoftwareEffect? _active;
    private LightingSettings _settings = new();
    private readonly EffectContext _ctx = new();

    /// <summary>Raised on the render thread with a copy of each frame, for the UI preview.</summary>
    public event Action<RgbColor[,]>? FrameRendered;

    public bool IsAvailable => _chroma.IsReady;
    public ChromaFamily Family => _chroma.Family;
    public string ProbeLog { get; private set; } = "";

    public LightingEngine(RazerLaptopDevice device)
    {
        _chroma = new RazerChroma(device);
    }

    /// <summary>Finds the command family. Safe to call again after a reconnect.</summary>
    public bool Initialise()
    {
        var ok = _chroma.Probe(out var log);
        ProbeLog = log;
        Log.Info(log.TrimEnd());
        return ok;
    }

    // ------------------------------------------------------------------ apply

    public void Apply(LightingSettings settings)
    {
        _settings = settings;

        if (!_chroma.IsReady)
        {
            Log.Warn("Lighting: no Chroma interface, ignoring effect change.");
            return;
        }

        StopRenderLoop();

        if (!settings.Enabled)
        {
            _chroma.Off();
            return;
        }

        _ctx.Primary = RgbColor.FromHex(settings.PrimaryColor);
        _ctx.Secondary = RgbColor.FromHex(settings.SecondaryColor);

        if (settings.Effect.StartsWith("hw-", StringComparison.OrdinalIgnoreCase))
        {
            _chroma.SetBrightness((byte)Math.Clamp(settings.Brightness, 0, 255));
            ApplyHardwareEffect(settings);
            return;
        }

        var effect = EffectCatalog.Find(settings.Effect) ?? EffectCatalog.All[0];
        _active = effect;
        _active.Reset(_ctx);

        // Software frames carry their own brightness, so the controller stays at full
        // scale and the render does the dimming.
        _chroma.SetBrightness(255);
        StartRenderLoop();
    }

    private void ApplyHardwareEffect(LightingSettings s)
    {
        var primary = RgbColor.FromHex(s.PrimaryColor);
        var secondary = RgbColor.FromHex(s.SecondaryColor);
        var direction = s.WaveDirection == 2 ? WaveDirection.Left : WaveDirection.Right;

        var ok = s.Effect.ToLowerInvariant() switch
        {
            "hw-off" => _chroma.Off(),
            "hw-static" => _chroma.Static(primary),
            "hw-breathe" => _chroma.BreathingSingle(primary),
            "hw-breathe-dual" => _chroma.BreathingDual(primary, secondary),
            "hw-breathe-random" => _chroma.BreathingRandom(),
            "hw-spectrum" => _chroma.Spectrum(),
            "hw-wave" => _chroma.Wave(direction),
            "hw-reactive" => _chroma.Reactive(primary, (byte)Math.Clamp(s.ReactiveSpeed, 1, 4)),
            "hw-starlight" => _chroma.StarlightSingle(primary, (byte)Math.Clamp(s.StarlightSpeed, 1, 3)),
            "hw-starlight-dual" => _chroma.StarlightDual(primary, secondary, (byte)Math.Clamp(s.StarlightSpeed, 1, 3)),
            "hw-starlight-random" => _chroma.StarlightRandom((byte)Math.Clamp(s.StarlightSpeed, 1, 3)),
            _ => _chroma.Static(primary),
        };

        if (!ok) Log.Warn($"Lighting: the keyboard rejected effect '{s.Effect}'.");
    }

    // ------------------------------------------------------------ render loop

    private void StartRenderLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _thread = new Thread(() => RenderLoop(token))
        {
            IsBackground = true,
            Name = "Lighting",
            // Below normal: a late frame is invisible, a late fan command is not.
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void StopRenderLoop()
    {
        var cts = _cts;
        var thread = _thread;
        _cts = null;
        _thread = null;

        if (cts == null) return;

        try
        {
            cts.Cancel();
            thread?.Join(500);
            cts.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[lighting] stop: {ex.Message}");
        }
    }

    private void RenderLoop(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var previous = 0.0;
        var scaled = 0.0;

        while (!token.IsCancellationRequested)
        {
            var fps = Math.Clamp(_settings.SoftwareFps, 5, 60);
            var frameTime = 1000.0 / fps;
            var started = clock.Elapsed.TotalMilliseconds;

            try
            {
                var now = clock.Elapsed.TotalSeconds;
                var speed = Math.Clamp(_settings.Speed, 0.1, 4.0);
                var delta = (now - previous) * speed;
                previous = now;
                scaled += delta;

                lock (_gate)
                {
                    _ctx.Time = scaled;
                    _ctx.Delta = delta;
                    _ctx.Primary = RgbColor.FromHex(_settings.PrimaryColor);
                    _ctx.Secondary = RgbColor.FromHex(_settings.SecondaryColor);

                    _active?.Render(_frame, _ctx);

                    var brightness = Math.Clamp(_settings.Brightness, 0, 255) / 255.0;
                    if (brightness < 0.999)
                        for (var r = 0; r < RazerChroma.Rows; r++)
                        for (var c = 0; c < RazerChroma.Columns; c++)
                            _frame[r, c] = _frame[r, c].Scale(brightness);

                    _chroma.SetFrame(_frame);
                    RaiseFrame();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[lighting] render: {ex.Message}");
            }

            var elapsed = clock.Elapsed.TotalMilliseconds - started;
            var wait = (int)Math.Max(1, frameTime - elapsed);
            if (token.WaitHandle.WaitOne(wait)) break;
        }
    }

    private void RaiseFrame()
    {
        var handler = FrameRendered;
        if (handler == null) return;

        var copy = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];
        Array.Copy(_frame, copy, _frame.Length);

        try
        {
            handler(copy);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[lighting] preview: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- telemetry

    /// <summary>Fed from the control loop so temperature-driven effects have something to show.</summary>
    public void UpdateTelemetry(double? cpuC, double? gpuC, int cpuRpm, int gpuRpm, int minRpm, int maxRpm)
    {
        lock (_gate)
        {
            _ctx.CpuTempC = cpuC;
            _ctx.GpuTempC = gpuC;
            _ctx.CpuRpm = cpuRpm;
            _ctx.GpuRpm = gpuRpm;
            _ctx.MinRpm = minRpm;
            _ctx.MaxRpm = maxRpm;
        }
    }

    /// <summary>Renders one frame of an effect without touching the keyboard, for the UI preview.</summary>
    public static RgbColor[,] Preview(SoftwareEffect effect, EffectContext ctx, double seconds)
    {
        var frame = new RgbColor[ctx.Rows, ctx.Columns];
        effect.Reset(ctx);

        // Step rather than jump, so stateful effects show a settled picture.
        const double step = 1.0 / 30;
        for (var t = 0.0; t < seconds; t += step)
        {
            ctx.Time = t;
            ctx.Delta = step;
            effect.Render(frame, ctx);
        }

        return frame;
    }

    /// <summary>
    /// Leaves the keyboard in a sane state on the way out. A custom frame persists in
    /// the controller, so without this the last rendered frame would freeze on screen
    /// after the app closes.
    /// </summary>
    public void RestoreOnExit()
    {
        StopRenderLoop();
        if (!_chroma.IsReady) return;

        try
        {
            _chroma.SetBrightness((byte)Math.Clamp(_settings.Brightness, 0, 255));

            if (_settings.Effect.StartsWith("hw-", StringComparison.OrdinalIgnoreCase))
                return; // already a controller-side effect; it keeps running by itself

            switch (_settings.RestoreOnExit?.ToLowerInvariant())
            {
                case "off":
                    _chroma.Off();
                    break;
                case "spectrum":
                    _chroma.Spectrum();
                    break;
                case "leave":
                    break;
                default:
                    _chroma.Static(RgbColor.FromHex(_settings.PrimaryColor));
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[lighting] restore: {ex.Message}");
        }
    }

    public void Dispose() => StopRenderLoop();
}
