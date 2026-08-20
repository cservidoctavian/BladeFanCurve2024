using System.IO;
using System.Text.Json;

namespace BladeFanCurve.Config;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> under %AppData%\BladeFanCurve.
/// Writes go to a temp file and are then moved into place, so a power loss
/// mid-save cannot leave a truncated config behind.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BladeFanCurve");

    public static string ConfigPath => Path.Combine(Directory, "config.json");

    public static string LogPath => Path.Combine(Directory, "bladefancurve.log");

    /// <summary>
    /// Written the moment manual fan control is engaged and deleted when control is
    /// handed back. If it is present at startup the previous run did not shut down
    /// cleanly, so the fans are returned to automatic before anything else happens.
    /// </summary>
    public static string ManualModeMarkerPath => Path.Combine(Directory, "manual-mode.active");

    public static AppConfig Load()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!File.Exists(ConfigPath))
            {
                var fresh = AppConfig.CreateDefault();
                Save(fresh);
                return fresh;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (config == null || config.Profiles.Count == 0) return AppConfig.CreateDefault();

            Sanitise(config);
            return config;
        }
        catch
        {
            return AppConfig.CreateDefault();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            Sanitise(config);

            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            // A failed save must never take the control loop down.
        }
    }

    /// <summary>Clamps anything a hand-edited config could get wrong.</summary>
    private static void Sanitise(AppConfig c)
    {
        c.Safety.MinRpm = Math.Clamp(c.Safety.MinRpm, 0, 10000);
        c.Safety.MaxRpm = Math.Clamp(c.Safety.MaxRpm, c.Safety.MinRpm, 10000);
        c.Safety.CpuCriticalC = Math.Clamp(c.Safety.CpuCriticalC, 60, 105);
        c.Safety.GpuCriticalC = Math.Clamp(c.Safety.GpuCriticalC, 60, 105);
        c.Safety.CriticalReleaseMarginC = Math.Clamp(c.Safety.CriticalReleaseMarginC, 1, 25);
        c.Safety.CriticalHoldSeconds = Math.Clamp(c.Safety.CriticalHoldSeconds, 0, 120);
        c.Safety.SensorStaleSeconds = Math.Clamp(c.Safety.SensorStaleSeconds, 2, 60);

        c.Tuning.PollIntervalMs = Math.Clamp(c.Tuning.PollIntervalMs, 250, 10000);
        c.Tuning.TempFallRateCPerSec = Math.Clamp(c.Tuning.TempFallRateCPerSec, 0.1, 50);
        c.Tuning.RampUpRpmPerSec = Math.Clamp(c.Tuning.RampUpRpmPerSec, 50, 10000);
        c.Tuning.RampDownRpmPerSec = Math.Clamp(c.Tuning.RampDownRpmPerSec, 25, 10000);
        c.Tuning.RpmDeadband = Math.Clamp(c.Tuning.RpmDeadband, 100, 1000);
        c.Tuning.ReassertSeconds = Math.Clamp(c.Tuning.ReassertSeconds, 5, 300);

        c.Device.CommandDelayMs = Math.Clamp(c.Device.CommandDelayMs, 5, 500);
        c.Device.SetRpmArg0 = c.Device.SetRpmArg0 == 0 ? 0 : 1;

        c.Lighting ??= new LightingSettings();
        c.Lighting.Brightness = Math.Clamp(c.Lighting.Brightness, 0, 255);
        c.Lighting.Speed = Math.Clamp(c.Lighting.Speed, 0.1, 4.0);
        c.Lighting.WaveDirection = c.Lighting.WaveDirection == 2 ? 2 : 1;
        c.Lighting.ReactiveSpeed = Math.Clamp(c.Lighting.ReactiveSpeed, 1, 4);
        c.Lighting.StarlightSpeed = Math.Clamp(c.Lighting.StarlightSpeed, 1, 3);
        // Each software frame is seven HID writes, so the ceiling is about bus traffic
        // rather than about what looks smooth.
        c.Lighting.SoftwareFps = Math.Clamp(c.Lighting.SoftwareFps, 5, 60);
        if (string.IsNullOrWhiteSpace(c.Lighting.Effect)) c.Lighting.Effect = "hw-static";
        c.Lighting.PrimaryColor = NormaliseHex(c.Lighting.PrimaryColor, "#00FF88");
        c.Lighting.SecondaryColor = NormaliseHex(c.Lighting.SecondaryColor, "#3355FF");

        c.Display ??= new DisplaySettings();
        // Below 1200 K the image is essentially monochrome orange; above 6500 the
        // approximation stops being meaningful.
        c.Display.NightLightKelvin = Math.Clamp(c.Display.NightLightKelvin, 1200, 6500);
        c.Display.NightLightStartMinutes = Math.Clamp(c.Display.NightLightStartMinutes, 0, 1439);
        c.Display.NightLightEndMinutes = Math.Clamp(c.Display.NightLightEndMinutes, 0, 1439);

        c.Battery ??= new BatterySettings();
        c.Battery.ChargeLimitPercent = Math.Clamp(c.Battery.ChargeLimitPercent,
            Hardware.RazerPower.MinChargeLimit, Hardware.RazerPower.MaxChargeLimit);

        foreach (var profile in c.Profiles)
        {
            profile.Power ??= new ProfilePower();
            // 0 means "leave the refresh rate alone"; anything else must be plausible.
            if (profile.Power.RefreshHz is not 0 and (< 24 or > 500)) profile.Power.RefreshHz = 0;
        }

        foreach (var profile in c.Profiles)
        {
            SanitiseCurve(profile.CpuFan);
            SanitiseCurve(profile.GpuFan);
        }

        if (c.Profiles.All(p => p.Name != c.ActiveProfile))
            c.ActiveProfile = c.Profiles[0].Name;

        void SanitiseCurve(FanCurveConfig curve)
        {
            if (curve.Points.Count < 2)
            {
                curve.Points = FanCurveConfig.DefaultCpu().Points;
                return;
            }

            foreach (var p in curve.Points)
            {
                p.TempC = Math.Clamp(p.TempC, 0, 110);
                p.Rpm = Math.Clamp(p.Rpm, c.Safety.MinRpm, c.Safety.MaxRpm);
            }

            curve.Points = curve.Points.OrderBy(p => p.TempC).ToList();
        }
    }

    /// <summary>
    /// Accepts "#RRGGBB", "RRGGBB" or anything else and always returns a well-formed
    /// "#RRGGBB", so a hand-edited config cannot produce an unparseable colour.
    /// </summary>
    private static string NormaliseHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var s = value.Trim().TrimStart('#');
        if (s.Length != 6) return fallback;

        foreach (var ch in s)
            if (!Uri.IsHexDigit(ch))
                return fallback;

        return "#" + s.ToUpperInvariant();
    }
}
