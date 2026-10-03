using System.Text.Json.Serialization;
using BladeFanCurve.Hardware;

namespace BladeFanCurve.Config;

public sealed class CurvePoint
{
    public double TempC { get; set; }
    public int Rpm { get; set; }

    public CurvePoint() { }

    public CurvePoint(double tempC, int rpm)
    {
        TempC = tempC;
        Rpm = rpm;
    }

    public CurvePoint Clone() => new(TempC, Rpm);
}

public sealed class FanCurveConfig
{
    public List<CurvePoint> Points { get; set; } = new();

    public FanCurveConfig Clone() => new() { Points = Points.Select(p => p.Clone()).ToList() };

    /// <summary>
    /// Tuned for a Ryzen HS-class mobile CPU (Tjmax 100 °C). These chips boost until
    /// they reach the mid-90s and sit there under sustained load — that is normal, not
    /// an emergency — so the curve deliberately stays quiet well past 80 °C.
    /// </summary>
    public static FanCurveConfig DefaultCpu() => new()
    {
        Points =
        {
            new CurvePoint(50, 2000),
            new CurvePoint(62, 2200),
            new CurvePoint(72, 2600),
            new CurvePoint(80, 3200),
            new CurvePoint(87, 4000),
            new CurvePoint(93, 5000),
        }
    };

    /// <summary>Tuned for an RTX 40-series laptop GPU, which throttles around 87 °C.</summary>
    public static FanCurveConfig DefaultGpu() => new()
    {
        Points =
        {
            new CurvePoint(45, 2000),
            new CurvePoint(55, 2200),
            new CurvePoint(65, 2700),
            new CurvePoint(73, 3300),
            new CurvePoint(80, 4200),
            new CurvePoint(85, 5000),
        }
    };

    public static FanCurveConfig Flat(int rpm) => new()
    {
        Points = { new CurvePoint(30, rpm), new CurvePoint(95, rpm) }
    };
}

/// <summary>
/// The power side of a profile. Every field defaults to "leave it alone" so that a
/// bare profile never starts changing the Windows power plan or the refresh rate by
/// itself. The three shipped profiles carry the fixed presets below, and selecting
/// one of them on the Fan curves tab applies its preset in full.
/// </summary>
public sealed class ProfilePower
{
    /// <summary>
    /// <see cref="RefreshHz"/> value meaning "the highest rate the panel offers at the
    /// current resolution", so a preset does not have to hard-code 240 Hz.
    /// </summary>
    public const int HighestRefreshRate = -1;

    // The three schemes every Windows install ships with. Kept as strings here rather
    // than borrowed from WindowsPowerPlan so this file stays free of Windows imports.
    private const string PowerSaverPlan = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerformancePlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    /// <summary>Razer performance mode: Balanced, Gaming, Creator, Custom. Empty leaves it.</summary>
    public string PerfMode { get; set; } = "";

    /// <summary>
    /// Used when <see cref="PerfMode"/> is Custom but the firmware does not expose the
    /// boost commands. Custom mode without boost control would leave the machine on
    /// whatever the firmware happens to default Custom to, which is not what the
    /// profile asked for — so it drops to this named mode instead.
    /// </summary>
    public string FallbackPerfMode { get; set; } = "";

    /// <summary>
    /// Low, Medium, High, Boost. The controller only honours these in Custom mode, so
    /// setting a boost level while <see cref="PerfMode"/> is Balanced or Gaming does
    /// nothing at all.
    /// </summary>
    public string CpuBoost { get; set; } = "";

    /// <summary>Low, Medium, High. Same Custom-mode requirement as the CPU.</summary>
    public string GpuBoost { get; set; } = "";

    /// <summary>Windows power scheme GUID, or empty to leave the current one.</summary>
    public string WindowsPlan { get; set; } = "";

    /// <summary>Power-mode overlay: efficiency, recommended, performance. Empty leaves it.</summary>
    public string PowerOverlay { get; set; } = "";

    /// <summary>
    /// Display refresh rate in Hz, 0 to leave it, or <see cref="HighestRefreshRate"/>
    /// for the panel's maximum. Dropping to 60 Hz is a real battery saving.
    /// </summary>
    public int RefreshHz { get; set; }

    public ProfilePower Clone() => (ProfilePower)MemberwiseClone();

    // ------------------------------------------------------------- presets
    //
    // Razer exposes power as steps, not watts: Custom mode with a CPU level (Low,
    // Medium, High, Boost) and a GPU level (Low, Medium, High). The watt figures that
    // do exist belong to the named modes used as fallbacks — Balanced runs a 35 W CPU
    // target, Gaming 55 W — for firmware that has no level commands.

    /// <summary>
    /// Lowest power everywhere: both chips on Low, power saver, efficiency. The refresh
    /// rate is left at whatever the panel is already running — selecting Silent, by
    /// hand or by pulling the charger, does not drop it to 60 Hz.
    /// </summary>
    public static ProfilePower SilentPreset() => new()
    {
        PerfMode = "Custom",
        FallbackPerfMode = "Balanced",
        CpuBoost = "Low",
        GpuBoost = "Low",
        WindowsPlan = PowerSaverPlan,
        PowerOverlay = "efficiency",
        RefreshHz = 0, // leave the current refresh rate alone
    };

    /// <summary>
    /// The 35 W-class CPU target with the CPU level on High, and the GPU on High, with
    /// the panel on its top refresh rate.
    /// </summary>
    public static ProfilePower BalancedPreset() => new()
    {
        PerfMode = "Custom",
        FallbackPerfMode = "Balanced",
        CpuBoost = "High",
        GpuBoost = "High",
        WindowsPlan = BalancedPlan,
        PowerOverlay = "recommended",
        RefreshHz = HighestRefreshRate,
    };

    /// <summary>
    /// Everything at maximum: CPU on full Boost (the 55 W ceiling), GPU on its top
    /// level, the high-performance plan, best-performance mode and the panel's top
    /// refresh rate.
    /// </summary>
    public static ProfilePower PerformancePreset() => new()
    {
        PerfMode = "Custom",
        FallbackPerfMode = "Gaming",
        CpuBoost = "Boost",
        GpuBoost = "High",
        WindowsPlan = HighPerformancePlan,
        PowerOverlay = "performance",
        RefreshHz = HighestRefreshRate,
    };

    /// <summary>The fixed preset for a shipped profile name, or null for any other profile.</summary>
    public static ProfilePower? PresetFor(string? profileName) => profileName switch
    {
        "Silent" => SilentPreset(),
        "Balanced" => BalancedPreset(),
        "Performance" => PerformancePreset(),
        _ => null,
    };

    /// <summary>One line for the profile picker, e.g. "CPU high · GPU high · balanced plan · top Hz".</summary>
    public string Describe()
    {
        var parts = new List<string>();

        // A hand-edited config can carry nulls, so nothing here assumes a string.
        var mode = PerfMode ?? "";
        var cpu = CpuBoost ?? "";
        var gpu = GpuBoost ?? "";

        var custom = mode.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        if (custom && cpu.Length > 0) parts.Add($"CPU {cpu.ToLowerInvariant()}");
        if (custom && gpu.Length > 0) parts.Add($"GPU {gpu.ToLowerInvariant()}");
        if (!custom && mode.Length > 0) parts.Add($"{mode} mode");

        var plan = (WindowsPlan ?? "").Trim().ToLowerInvariant() switch
        {
            "" => null,
            PowerSaverPlan => "power saver",
            BalancedPlan => "balanced plan",
            HighPerformancePlan => "high performance",
            _ => "custom plan",
        };
        if (plan != null) parts.Add(plan);

        if (RefreshHz == HighestRefreshRate) parts.Add("top Hz");
        else if (RefreshHz > 0) parts.Add($"{RefreshHz} Hz");

        return parts.Count == 0 ? "fan curves only" : string.Join("  ·  ", parts);
    }
}

public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public FanCurveConfig CpuFan { get; set; } = FanCurveConfig.DefaultCpu();
    public FanCurveConfig GpuFan { get; set; } = FanCurveConfig.DefaultGpu();
    public ProfilePower Power { get; set; } = new();

    public Profile Clone() => new()
    {
        Name = Name,
        CpuFan = CpuFan.Clone(),
        GpuFan = GpuFan.Clone(),
        Power = Power.Clone(),
    };
}

public sealed class DisplaySettings
{
    public bool NightLightEnabled { get; set; }

    /// <summary>Minutes past midnight. Default 21:00.</summary>
    public int NightLightStartMinutes { get; set; } = 21 * 60;

    /// <summary>Minutes past midnight. Default 07:00, i.e. the schedule crosses midnight.</summary>
    public int NightLightEndMinutes { get; set; } = 7 * 60;

    /// <summary>1200 (very warm) to 6500 (neutral).</summary>
    public int NightLightKelvin { get; set; } = 3400;
}

public sealed class BatterySettings
{
    /// <summary>Stop charging at <see cref="ChargeLimitPercent"/> to reduce cell wear.</summary>
    public bool ChargeLimitEnabled { get; set; }

    /// <summary>50-100. 100 means charge normally.</summary>
    public int ChargeLimitPercent { get; set; } = 80;

    /// <summary>Re-apply the limit on resume and at intervals, since the EC forgets it across sleep.</summary>
    public bool ReapplyChargeLimit { get; set; } = true;
}

public sealed class AutomationSettings
{
    /// <summary>Switch profile when the charger is unplugged.</summary>
    public bool SwitchProfileOnBattery { get; set; } = true;

    /// <summary>The profile to select on battery. Ignored if no profile has this name.</summary>
    public string BatteryProfile { get; set; } = "Silent";

    /// <summary>Go back to the previous profile when the charger goes back in.</summary>
    public bool RestoreProfileOnAc { get; set; } = true;

    /// <summary>
    /// Which profile was active before the charger came out. Persisted rather than
    /// held in memory so that unplugging, closing the lid overnight and plugging back
    /// in tomorrow still restores the right one.
    /// </summary>
    public string ProfileBeforeBattery { get; set; } = "";
}

public sealed class SafetySettings
{
    /// <summary>
    /// Never command a fan below this. Set it to 0 to let the fans stop completely —
    /// the thermal guard below is what keeps that safe.
    /// </summary>
    public int MinRpm { get; set; } = 2000;

    /// <summary>
    /// Thermal guard. Independent of the curves and of any manual override: once
    /// either package reaches <see cref="SpinUpTempC"/>, both fans are forced to at
    /// least <see cref="SpinUpPercent"/> of <see cref="MaxRpm"/>. This is what makes a
    /// 0 RPM floor safe to use — the fans can idle silently, but they cannot stay
    /// stopped while the machine is heating up.
    /// </summary>
    public bool SpinUpEnabled { get; set; } = true;

    public double SpinUpTempC { get; set; } = 70;

    /// <summary>Percentage of MaxRpm to force once the guard engages.</summary>
    public int SpinUpPercent { get; set; } = 50;

    /// <summary>How far the temperature must fall before the guard lets go again.</summary>
    public double SpinUpReleaseMarginC { get; set; } = 5;

    /// <summary>Upper bound sent to the EC. The EC clamps to its own maximum anyway.</summary>
    public int MaxRpm { get; set; } = 5000;

    /// <summary>
    /// At or above this CPU temperature both fans go to maximum regardless of the curve.
    /// Ryzen HS parts have Tjmax 100 °C and legitimately run in the mid-90s under load,
    /// so this sits at 97 rather than the ~92 that suits an Intel H-series part.
    /// </summary>
    public double CpuCriticalC { get; set; } = 97;

    public double GpuCriticalC { get; set; } = 88;

    /// <summary>How far below the critical point the temperature must fall before the override releases.</summary>
    public double CriticalReleaseMarginC { get; set; } = 8;

    /// <summary>Minimum time the critical override stays engaged once triggered.</summary>
    public double CriticalHoldSeconds { get; set; } = 15;

    /// <summary>If no usable temperature arrives for this long, hand control back to the laptop.</summary>
    public double SensorStaleSeconds { get; set; } = 6;

    /// <summary>Hand control back to the laptop while running on battery.</summary>
    public bool RevertToAutoOnBattery { get; set; } = false;
}

public sealed class TuningSettings
{
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>Temperature rises are followed instantly; falls are limited to this many °C per second.</summary>
    public double TempFallRateCPerSec { get; set; } = 1.5;

    public int RampUpRpmPerSec { get; set; } = 900;
    public int RampDownRpmPerSec { get; set; } = 250;

    /// <summary>Only resend a set point when it moves at least this far (the wire granularity is 100 RPM).</summary>
    public int RpmDeadband { get; set; } = 100;

    /// <summary>Re-assert manual mode this often so a firmware reset does not silently take over.</summary>
    public double ReassertSeconds { get; set; } = 20;

    /// <summary>Apply the higher of the two curve demands to both fans.</summary>
    public bool SharedFloor { get; set; } = false;
}

public sealed class DeviceSettings
{
    public int CommandDelayMs { get; set; } = 30;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerfMode PerfMode { get; set; } = PerfMode.Balanced;

    /// <summary>Optional override; 0 means "probe automatically".</summary>
    public int ForceProductId { get; set; }

    /// <summary>Optional override; 0 means "probe automatically".</summary>
    public int ForceTransactionId { get; set; }

    /// <summary>
    /// First argument byte of the "set fan rpm" command, which differs between
    /// firmware generations. 0x01 is correct for the 2024 Blade family (02B6/02B7);
    /// older models want 0x00. Wrong guesses are detected and corrected at runtime
    /// by reading the set point back, so this is only a starting point.
    /// </summary>
    public int SetRpmArg0 { get; set; } = 0x01;
}

public sealed class LightingSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Either a hardware effect id (prefixed "hw-", run by the keyboard controller) or a
    /// software effect id from <see cref="Lighting.EffectCatalog"/>, rendered here.
    /// </summary>
    public string Effect { get; set; } = "hw-static";

    public string PrimaryColor { get; set; } = "#00FF88";
    public string SecondaryColor { get; set; } = "#3355FF";

    /// <summary>0-255.</summary>
    public int Brightness { get; set; } = 255;

    /// <summary>Multiplier applied to time in software effects.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>1 = right, 2 = left.</summary>
    public int WaveDirection { get; set; } = 1;

    /// <summary>1 (fastest) to 4 (slowest).</summary>
    public int ReactiveSpeed { get; set; } = 2;

    /// <summary>1 (fastest) to 3 (slowest).</summary>
    public int StarlightSpeed { get; set; } = 2;

    /// <summary>Frame rate for software effects. Each frame is seven HID writes.</summary>
    public int SoftwareFps { get; set; } = 30;

    /// <summary>What to leave on the keyboard at exit: static, spectrum, off or leave.</summary>
    public string RestoreOnExit { get; set; } = "static";
}

public sealed class AppConfig
{
    /// <summary>
    /// 5: Silent leaves the refresh rate alone instead of dropping it to 60 Hz.
    /// 4: the shipped profiles carry fixed power presets (see <see cref="ProfilePower"/>).
    /// 3: profiles carry power settings at all.
    /// </summary>
    public int Version { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public string ActiveProfile { get; set; } = "Balanced";
    public string? CpuSensorId { get; set; }
    public string? GpuSensorId { get; set; }

    public List<Profile> Profiles { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
    public TuningSettings Tuning { get; set; } = new();
    public DeviceSettings Device { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public DisplaySettings Display { get; set; } = new();
    public BatterySettings Battery { get; set; } = new();

    public Profile GetActiveProfile()
    {
        var p = Profiles.FirstOrDefault(x => x.Name == ActiveProfile);
        if (p != null) return p;
        if (Profiles.Count == 0) Profiles.Add(new Profile { Name = ActiveProfile });
        return Profiles[0];
    }

    public static AppConfig CreateDefault() => new()
    {
        ActiveProfile = "Balanced",
        Profiles =
        {
            new Profile
            {
                // Quiet and cool: lowest CPU and GPU power and the power-saver plan. The
                // refresh rate is left wherever it already is.
                Power = ProfilePower.SilentPreset(),
                Name = "Silent",
                CpuFan = new FanCurveConfig
                {
                    Points =
                    {
                        new CurvePoint(55, 2000), new CurvePoint(68, 2000), new CurvePoint(78, 2400),
                        new CurvePoint(85, 3000), new CurvePoint(91, 4000), new CurvePoint(96, 5000),
                    }
                },
                GpuFan = new FanCurveConfig
                {
                    Points =
                    {
                        new CurvePoint(50, 2000), new CurvePoint(62, 2000), new CurvePoint(72, 2400),
                        new CurvePoint(79, 3000), new CurvePoint(84, 4000), new CurvePoint(87, 5000),
                    }
                },
            },
            new Profile
            {
                Name = "Balanced",
                CpuFan = FanCurveConfig.DefaultCpu(),
                GpuFan = FanCurveConfig.DefaultGpu(),
                Power = ProfilePower.BalancedPreset(),
            },
            new Profile
            {
                // Everything off the leash: full CPU boost, top GPU level, the
                // high-performance plan and the panel's top refresh rate.
                Power = ProfilePower.PerformancePreset(),
                Name = "Performance",
                CpuFan = new FanCurveConfig
                {
                    Points =
                    {
                        new CurvePoint(45, 2400), new CurvePoint(55, 2900), new CurvePoint(65, 3500),
                        new CurvePoint(74, 4200), new CurvePoint(82, 4800), new CurvePoint(88, 5000),
                    }
                },
                GpuFan = new FanCurveConfig
                {
                    Points =
                    {
                        new CurvePoint(40, 2400), new CurvePoint(50, 2900), new CurvePoint(58, 3500),
                        new CurvePoint(67, 4200), new CurvePoint(74, 4800), new CurvePoint(80, 5000),
                    }
                },
            },
        }
    };
}
