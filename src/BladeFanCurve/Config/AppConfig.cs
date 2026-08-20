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
/// config written by an older version does not suddenly start changing the Windows
/// power plan or the refresh rate after an upgrade. Only the shipped default profiles
/// carry opinionated values.
/// </summary>
public sealed class ProfilePower
{
    /// <summary>Razer performance mode: Balanced, Gaming, Creator, Custom. Empty leaves it.</summary>
    public string PerfMode { get; set; } = "";

    /// <summary>Low, Medium, High, Boost. Only applied in Custom mode, and only if the firmware exposes it.</summary>
    public string CpuBoost { get; set; } = "";

    /// <summary>Low, Medium, High.</summary>
    public string GpuBoost { get; set; } = "";

    /// <summary>Windows power scheme GUID, or empty to leave the current one.</summary>
    public string WindowsPlan { get; set; } = "";

    /// <summary>Power-mode overlay: efficiency, recommended, performance. Empty leaves it.</summary>
    public string PowerOverlay { get; set; } = "";

    /// <summary>Display refresh rate in Hz, or 0 to leave it. Dropping to 60 Hz is a real battery saving.</summary>
    public int RefreshHz { get; set; }

    public ProfilePower Clone() => (ProfilePower)MemberwiseClone();
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

public sealed class SafetySettings
{
    /// <summary>Never command a fan below this. Protects against a badly drawn curve.</summary>
    public int MinRpm { get; set; } = 2000;

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
    public int Version { get; set; } = 3;
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
                // Quiet and cool: the low TDP mode, the power-saver plan, and 60 Hz,
                // which on a 240 Hz panel is a larger battery saving than anything else here.
                Power = new ProfilePower
                {
                    PerfMode = "Balanced",
                    CpuBoost = "Low",
                    GpuBoost = "Low",
                    WindowsPlan = "a1841308-3541-4fab-bc81-f71556f20b4a",
                    PowerOverlay = "efficiency",
                    RefreshHz = 60,
                },
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
                Power = new ProfilePower
                {
                    PerfMode = "Balanced",
                    CpuBoost = "Medium",
                    GpuBoost = "Medium",
                    WindowsPlan = "381b4222-f694-41f0-9685-ff5bb260df2e",
                    PowerOverlay = "recommended",
                    RefreshHz = 0, // leave the refresh rate wherever the user put it
                },
            },
            new Profile
            {
                // Everything off the leash. The refresh rate is left alone rather than
                // forced to maximum, because that is the user's call, not the profile's.
                Power = new ProfilePower
                {
                    PerfMode = "Gaming",
                    CpuBoost = "Boost",
                    GpuBoost = "High",
                    WindowsPlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                    PowerOverlay = "performance",
                    RefreshHz = 0,
                },
                Name = "Turbo",
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
