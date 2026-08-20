using System.IO;
using System.Text.RegularExpressions;
using BladeFanCurve.Config;
using BladeFanCurve.Control;
using BladeFanCurve.Hardware;
using BladeFanCurve.Sensors;

namespace BladeFanCurve.Tests;

/// <summary>
/// Verifies the parts that can be checked without Razer hardware attached:
/// report encoding, the CRC, curve interpolation and the safety clamps.
/// Run with: dotnet run --project tests/ProtocolTests
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Section("Report encoding");
        ReportLayoutIsExactlyNinetyBytes();
        WireBufferHasReportIdPrefix();
        CrcIsXorOfBytesTwoToEightySeven();
        SetFanRpmMatchesHandComputedCrc();
        RoundTripsThroughBytes();
        RejectsOversizedArguments();

        Section("Command shapes");
        SetFanRpmEncodesRpmOverOneHundred();
        PerfModeCarriesZoneModeAndFanFlag();

        Section("Curve evaluation");
        InterpolatesBetweenPoints();
        HoldsFirstAndLastValueOutsideTheCurve();
        ClampsToSafetyLimits();
        QuantisesToHundredRpm();
        HandlesUnsortedPoints();

        Section("Channel behaviour");
        RampLimitsSpeedIncrease();
        CriticalOverrideBypassesRampAndCurve();
        FallingTemperatureIsRateLimited();
        NeverGoesBelowTheFloor();
        DeadbandSuppressesTinyChanges();

        Section("Config safety");
        DefaultCurvesStayWithinLimits();
        DefaultProfilesAreMonotonic();
        CurvesReachMaximumBeforeTheCriticalPoint();
        CriticalPointsSuitTheHardware();

        Section("Model table");
        KnownModelsAreConsistent();
        TargetMachineIsRecognised();

        Section("HID access strategy");
        ZeroAccessIsTriedFirst();

        Section("Build configuration");
        WpfIncompatibleSettingsAreNotEnabled();

        Section("Sensor fallback");
        HottestIgnoresMissingReadings();
        DegradedSourcesStillProduceATemperature();
        ImplausibleReadingsAreRejected();

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- encoding

    private static void ReportLayoutIsExactlyNinetyBytes()
    {
        var report = RazerReport.Create(0x1F, 0x0D, 0x01, 0x03, 0x00, 0x01, 0x1E);
        Check("struct is 90 bytes", report.ToBytes().Length == 90);

        var b = report.ToBytes();
        Check("byte 1 = transaction id", b[1] == 0x1F);
        Check("byte 5 = data size", b[5] == 0x03);
        Check("byte 6 = command class", b[6] == 0x0D);
        Check("byte 7 = command id", b[7] == 0x01);
        Check("byte 8 = first argument", b[8] == 0x00);
        Check("byte 10 = third argument", b[10] == 0x1E);
    }

    private static void WireBufferHasReportIdPrefix()
    {
        var report = RazerReport.Create(0x1F, 0x0D, 0x82, 0x04, 0x00, 0x01);
        var wire = report.ToWireBytes();
        Check("wire buffer is 91 bytes", wire.Length == 91);
        Check("wire[0] is report id 0x00", wire[0] == 0x00);
        Check("wire[2] is the transaction id", wire[2] == 0x1F);
        Check("wire[7] is the command class", wire[7] == 0x0D);

        var padded = report.ToWireBytes(128);
        Check("larger feature buffers are zero padded", padded.Length == 128 && padded[100] == 0x00);
    }

    private static void CrcIsXorOfBytesTwoToEightySeven()
    {
        var bytes = new byte[90];
        for (var i = 0; i < 90; i++) bytes[i] = (byte)(i * 7);

        byte expected = 0;
        for (var i = 2; i < 88; i++) expected ^= bytes[i];

        Check("crc matches the OpenRazer definition", RazerReport.ComputeCrc(bytes) == expected);

        // Bytes 0, 1, 88 and 89 must not take part.
        var mutated = (byte[])bytes.Clone();
        mutated[0] ^= 0xFF;
        mutated[1] ^= 0xFF;
        mutated[88] ^= 0xFF;
        mutated[89] ^= 0xFF;
        Check("crc ignores status, txn, crc and reserved",
            RazerReport.ComputeCrc(mutated) == expected);
    }

    private static void SetFanRpmMatchesHandComputedCrc()
    {
        // set fan rpm, cpu zone, 3000 rpm -> args 0x00, 0x01, 0x1E
        // crc = 0x03 ^ 0x0D ^ 0x01 ^ 0x00 ^ 0x01 ^ 0x1E
        const byte handComputed = 0x03 ^ 0x0D ^ 0x01 ^ 0x00 ^ 0x01 ^ 0x1E;

        var report = RazerReport.Create(0x1F, 0x0D, 0x01, 0x03, 0x00, 0x01, 0x1E);
        var bytes = report.ToBytes();
        Check($"set-fan-rpm crc is 0x{handComputed:X2}", bytes[88] == handComputed);
    }

    private static void RoundTripsThroughBytes()
    {
        var original = RazerReport.Create(0x1F, 0x0D, 0x02, 0x04, 0x00, 0x02, 0x00, 0x01);
        var parsed = RazerReport.FromBytes(original.ToBytes());

        Check("round trip keeps transaction id", parsed.TransactionId == 0x1F);
        Check("round trip keeps class/id", parsed is { CommandClass: 0x0D, CommandId: 0x02 });
        Check("round trip keeps arguments",
            parsed.Arguments[1] == 0x02 && parsed.Arguments[3] == 0x01);
        Check("parsed report validates its own crc", parsed.CrcValid());

        var wire = RazerReport.FromWireBytes(original.ToWireBytes());
        Check("wire round trip keeps class", wire.CommandClass == 0x0D);
        Check("Echoes matches the request", wire.Echoes(original));
    }

    private static void RejectsOversizedArguments()
    {
        var threw = false;
        try { RazerReport.Create(0x1F, 0x0D, 0x01, 0x03, new byte[81]); }
        catch (ArgumentException) { threw = true; }
        Check("more than 80 argument bytes is rejected", threw);
    }

    // ------------------------------------------------------------- commands

    private static void SetFanRpmEncodesRpmOverOneHundred()
    {
        foreach (var (rpm, encoded) in new[] { (2000, 20), (3400, 34), (5000, 50) })
        {
            var report = RazerReport.Create(0x1F, 0x0D, 0x01, 0x03, 0x00, 0x01, (byte)(rpm / 100));
            Check($"{rpm} rpm encodes as 0x{encoded:X2}", report.ToBytes()[10] == encoded);
        }
    }

    private static void PerfModeCarriesZoneModeAndFanFlag()
    {
        var report = RazerReport.Create(0x1F, 0x0D, 0x02, 0x04,
            0x00, (byte)FanZone.Gpu, (byte)PerfMode.Balanced, 0x01);
        var b = report.ToBytes();

        Check("perf mode targets the gpu zone", b[9] == 0x02);
        Check("perf mode is balanced", b[10] == 0x00);
        Check("manual fan flag is set", b[11] == 0x01);
    }

    // ------------------------------------------------------------- curves

    private static void InterpolatesBetweenPoints()
    {
        var curve = new FanCurveConfig
        {
            Points = { new CurvePoint(50, 2000), new CurvePoint(70, 4000) }
        };

        Check("midpoint interpolates", FanCurveEvaluator.Evaluate(curve, 60, 0, 6000) == 3000);
        Check("quarter point interpolates", FanCurveEvaluator.Evaluate(curve, 55, 0, 6000) == 2500);
        Check("exact knee returns the knee", FanCurveEvaluator.Evaluate(curve, 70, 0, 6000) == 4000);
    }

    private static void HoldsFirstAndLastValueOutsideTheCurve()
    {
        var curve = new FanCurveConfig
        {
            Points = { new CurvePoint(50, 2200), new CurvePoint(80, 4800) }
        };

        Check("below the curve holds the first point", FanCurveEvaluator.Evaluate(curve, 20, 0, 6000) == 2200);
        Check("above the curve holds the last point", FanCurveEvaluator.Evaluate(curve, 99, 0, 6000) == 4800);
    }

    private static void ClampsToSafetyLimits()
    {
        var curve = new FanCurveConfig
        {
            Points = { new CurvePoint(40, 500), new CurvePoint(90, 9000) }
        };

        Check("a too-low point is raised to the floor",
            FanCurveEvaluator.Evaluate(curve, 40, 2000, 5000) == 2000);
        Check("a too-high point is capped at the ceiling",
            FanCurveEvaluator.Evaluate(curve, 90, 2000, 5000) == 5000);
    }

    private static void QuantisesToHundredRpm()
    {
        var curve = new FanCurveConfig
        {
            Points = { new CurvePoint(50, 2000), new CurvePoint(51, 2100) }
        };

        var value = FanCurveEvaluator.Evaluate(curve, 50.37, 0, 6000);
        Check($"result is a multiple of 100 (got {value})", value % 100 == 0);
        Check("quantise rounds to nearest hundred", FanCurveEvaluator.Quantise(2349, 0, 6000) == 2300);
        Check("quantise rounds up past the halfway point", FanCurveEvaluator.Quantise(2350, 0, 6000) == 2400);
    }

    private static void HandlesUnsortedPoints()
    {
        var curve = new FanCurveConfig
        {
            Points = { new CurvePoint(80, 4000), new CurvePoint(50, 2000), new CurvePoint(65, 3000) }
        };

        Check("unsorted points still evaluate correctly",
            FanCurveEvaluator.Evaluate(curve, 65, 0, 6000) == 3000);
        Check("unsorted points interpolate correctly",
            FanCurveEvaluator.Evaluate(curve, 57.5, 0, 6000) == 2500);
    }

    // ------------------------------------------------------------- channel

    private static void RampLimitsSpeedIncrease()
    {
        var safety = new SafetySettings { MinRpm = 2000, MaxRpm = 5000 };
        var tuning = new TuningSettings { RampUpRpmPerSec = 500, TempFallRateCPerSec = 1.5 };
        var curve = FanCurveConfig.Flat(5000);
        var channel = new FanChannel(FanZone.Cpu, "test");

        channel.Compute(50, FanCurveConfig.Flat(2000), safety, tuning, 1.0, 2000, false);
        var afterOneSecond = channel.Compute(90, curve, safety, tuning, 1.0, 2000, false);

        Check($"ramp up is limited to ~500 rpm/s (got {afterOneSecond})",
            afterOneSecond is >= 2400 and <= 2600);
    }

    private static void CriticalOverrideBypassesRampAndCurve()
    {
        var safety = new SafetySettings { MinRpm = 2000, MaxRpm = 5000 };
        var tuning = new TuningSettings { RampUpRpmPerSec = 100 };
        var channel = new FanChannel(FanZone.Cpu, "test");

        channel.Compute(40, FanCurveConfig.Flat(2000), safety, tuning, 1.0, 2000, false);
        var critical = channel.Compute(95, FanCurveConfig.Flat(2000), safety, tuning, 1.0, 2000, true);

        Check("critical jumps straight to maximum in one tick", critical == 5000);
    }

    private static void FallingTemperatureIsRateLimited()
    {
        var safety = new SafetySettings { MinRpm = 2000, MaxRpm = 5000 };
        var tuning = new TuningSettings { TempFallRateCPerSec = 2.0, RampDownRpmPerSec = 10000 };
        var curve = new FanCurveConfig
        {
            Points = { new CurvePoint(50, 2000), new CurvePoint(90, 5000) }
        };
        var channel = new FanChannel(FanZone.Cpu, "test");

        channel.Compute(90, curve, safety, tuning, 1.0, 2000, false);
        channel.Compute(50, curve, safety, tuning, 1.0, 2000, false);

        Check($"smoothed temp only fell 2°C in one second (got {channel.SmoothedTempC:0.0})",
            Math.Abs(channel.SmoothedTempC - 88.0) < 0.01);

        Check("a rise is followed immediately",
            RiseIsInstant(channel, curve, safety, tuning));

        static bool RiseIsInstant(FanChannel c, FanCurveConfig curve, SafetySettings s, TuningSettings t)
        {
            c.Compute(95, curve, s, t, 1.0, 2000, false);
            return Math.Abs(c.SmoothedTempC - 95.0) < 0.01;
        }
    }

    private static void NeverGoesBelowTheFloor()
    {
        var safety = new SafetySettings { MinRpm = 2400, MaxRpm = 5000 };
        var tuning = new TuningSettings();
        var channel = new FanChannel(FanZone.Cpu, "test");

        var rpm = channel.Compute(30, FanCurveConfig.Flat(1000), safety, tuning, 1.0, 2400, false);
        Check($"a curve below the minimum is lifted to it (got {rpm})", rpm == 2400);
    }

    private static void DeadbandSuppressesTinyChanges()
    {
        var channel = new FanChannel(FanZone.Cpu, "test");
        var safety = new SafetySettings { MinRpm = 2000, MaxRpm = 5000 };
        var tuning = new TuningSettings();

        var rpm = channel.Compute(60, FanCurveConfig.Flat(3000), safety, tuning, 1.0, 2000, false);
        Check("first value is always sent", channel.ShouldSend(100));
        channel.MarkSent(rpm);
        Check("an unchanged value is not resent", !channel.ShouldSend(100));
    }

    // ------------------------------------------------------------- config

    private static void DefaultCurvesStayWithinLimits()
    {
        var config = AppConfig.CreateDefault();
        var ok = true;

        foreach (var profile in config.Profiles)
        foreach (var curve in new[] { profile.CpuFan, profile.GpuFan })
        foreach (var point in curve.Points)
            if (point.Rpm < config.Safety.MinRpm || point.Rpm > config.Safety.MaxRpm)
                ok = false;

        Check("every default curve point sits inside the safety limits", ok);
    }

    private static void DefaultProfilesAreMonotonic()
    {
        var config = AppConfig.CreateDefault();
        var ok = true;

        foreach (var profile in config.Profiles)
        foreach (var curve in new[] { profile.CpuFan, profile.GpuFan })
            for (var i = 1; i < curve.Points.Count; i++)
                if (curve.Points[i].TempC <= curve.Points[i - 1].TempC ||
                    curve.Points[i].Rpm < curve.Points[i - 1].Rpm)
                    ok = false;

        Check("default curves rise in both temperature and rpm", ok);
        Check("three profiles ship by default", config.Profiles.Count == 3);
        Check("the active profile exists", config.GetActiveProfile().Name == config.ActiveProfile);
    }

    /// <summary>
    /// The critical override jumps straight to MaxRpm. If a curve has not already
    /// reached MaxRpm by the time the critical point is hit, crossing it produces an
    /// audible step. Every shipped curve should be at maximum before that happens.
    /// </summary>
    private static void CurvesReachMaximumBeforeTheCriticalPoint()
    {
        var config = AppConfig.CreateDefault();
        var ok = true;
        var worst = "";

        foreach (var profile in config.Profiles)
        {
            var cpu = FanCurveEvaluator.Evaluate(profile.CpuFan, config.Safety.CpuCriticalC,
                config.Safety.MinRpm, config.Safety.MaxRpm);
            if (cpu < config.Safety.MaxRpm) { ok = false; worst = $"{profile.Name} CPU reaches only {cpu}"; }

            var gpu = FanCurveEvaluator.Evaluate(profile.GpuFan, config.Safety.GpuCriticalC,
                config.Safety.MinRpm, config.Safety.MaxRpm);
            if (gpu < config.Safety.MaxRpm) { ok = false; worst = $"{profile.Name} GPU reaches only {gpu}"; }
        }

        Check($"every curve is already at maximum when critical triggers{(ok ? "" : " — " + worst)}", ok);
    }

    /// <summary>
    /// A Ryzen HS part has Tjmax 100 °C and legitimately sustains the mid-90s, so a
    /// critical point in the 80s would pin the fans at maximum during normal use.
    /// </summary>
    private static void CriticalPointsSuitTheHardware()
    {
        var safety = new SafetySettings();

        Check($"cpu critical leaves headroom under Tjmax 100 (is {safety.CpuCriticalC})",
            safety.CpuCriticalC is >= 95 and <= 99);
        Check($"gpu critical sits near the 87 °C throttle point (is {safety.GpuCriticalC})",
            safety.GpuCriticalC is >= 85 and <= 92);
        Check("cpu critical is above gpu critical", safety.CpuCriticalC > safety.GpuCriticalC);
        Check($"rpm floor is high enough to matter (is {safety.MinRpm})", safety.MinRpm >= 1500);
    }

    // ------------------------------------------------------------- models

    private static void KnownModelsAreConsistent()
    {
        var ids = new List<int>();
        for (var pid = 0x0200; pid <= 0x02FF; pid++)
        {
            var model = KnownModels.Find(pid);
            if (model != null) ids.Add(pid);
        }

        Check("the model table is populated", ids.Count >= 8);
        Check("no duplicate product ids", ids.Distinct().Count() == ids.Count);
        Check("every entry uses a documented argument layout",
            ids.All(id => KnownModels.Find(id)!.SetRpmArg0 is 0x00 or 0x01));
        Check("an unknown id still describes cleanly",
            KnownModels.Describe(0x0999).Contains("1532:0999"));
    }

    private static void TargetMachineIsRecognised()
    {
        var blade14 = KnownModels.Find(0x02B6);
        Check("1532:02B6 is known", blade14 != null);
        Check($"1532:02B6 is the Blade 14 2024 (got \"{blade14?.Name}\")",
            blade14 is { MarketingNumber: "RZ09-0508" });
        Check("1532:02B6 uses set-rpm argument layout 0x01", blade14?.SetRpmArg0 == 0x01);

        var blade16 = KnownModels.Find(0x02B7);
        Check("its 2024 sibling 1532:02B7 shares the argument layout",
            blade16?.SetRpmArg0 == blade14?.SetRpmArg0);

        Check("the shipped default matches the target machine",
            new DeviceSettings().SetRpmArg0 == blade14!.SetRpmArg0);
    }

    /// <summary>
    /// The whole reason discovery works on Windows: a Razer laptop's control
    /// interface is a system keyboard, and Windows refuses user-mode read/write
    /// handles to those. Opening with a desired-access mask of zero still permits
    /// HidD_SetFeature / HidD_GetFeature. If this order ever regresses to asking for
    /// read/write first, discovery silently fails again.
    /// </summary>
    private static void ZeroAccessIsTriedFirst()
    {
        var modes = NativeHid.AccessModes;

        Check("there are three access strategies", modes.Length == 3);
        Check($"the first attempt asks for no access (mask 0x{modes[0].Mask:X8})", modes[0].Mask == 0);
        Check("the first attempt is named \"none\"", modes[0].Name == "none");
        Check("masks widen monotonically",
            modes.Select(m => m.Mask).SequenceEqual(modes.Select(m => m.Mask).OrderBy(m => m)));
        Check("read/write is the last resort", modes[^1].Mask == (0x80000000 | 0x40000000));
    }

    // ------------------------------------------------------------- build config

    /// <summary>
    /// WPF is not supported in globalization-invariant mode: MS.Internal.FontCache
    /// .MajorLanguages constructs CultureInfo("en") while measuring text, which throws
    /// CultureNotFoundException and kills the window on first render. This shipped
    /// once; it must not ship again.
    /// </summary>
    private static void WpfIncompatibleSettingsAreNotEnabled()
    {
        var csproj = FindAppProject();
        if (csproj == null)
        {
            Check("app project file was found", false);
            return;
        }

        var xml = File.ReadAllText(csproj);
        var invariant = Regex.Match(xml,
            @"<InvariantGlobalization>\s*(?<v>true|false)\s*</InvariantGlobalization>",
            RegexOptions.IgnoreCase);

        Check("InvariantGlobalization is stated explicitly", invariant.Success);
        Check("InvariantGlobalization is NOT enabled (WPF cannot run under it)",
            !invariant.Success || invariant.Groups["v"].Value.Equals("false", StringComparison.OrdinalIgnoreCase));

        // These would break WPF the same way if anyone reached for them to cut size.
        foreach (var hostile in new[] { "PublishTrimmed", "UseSystemResourceKeys", "PublishAot" })
        {
            var m = Regex.Match(xml, $@"<{hostile}>\s*true\s*</{hostile}>", RegexOptions.IgnoreCase);
            Check($"{hostile} is not enabled", !m.Success);
        }
    }

    private static string? FindAppProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BladeFanCurve", "BladeFanCurve.csproj");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    // ------------------------------------------------------------- sensors

    /// <summary>
    /// Hottest feeds the critical override, so a missing sensor must never be read
    /// as 0 °C — that would mask a genuinely hot component.
    /// </summary>
    private static void HottestIgnoresMissingReadings()
    {
        var now = DateTime.UtcNow;

        var cpuOnly = new SensorSnapshot(78, null, null, null, TempSource.HardwareMonitor, now);
        Check("CPU only reports the CPU as hottest", cpuOnly.Hottest == 78);

        var gpuOnly = new SensorSnapshot(null, 71, null, null, TempSource.None, now);
        Check("GPU only reports the GPU as hottest", gpuOnly.Hottest == 71);

        var both = new SensorSnapshot(64, 82, null, null, TempSource.AcpiThermalZone, now);
        Check("with both, the higher one wins", both.Hottest == 82);

        Check("no readings at all yields null, not zero", SensorSnapshot.Empty.Hottest is null);
        Check("empty snapshot reports no CPU", !SensorSnapshot.Empty.HasCpu);
    }

    private static void DegradedSourcesStillProduceATemperature()
    {
        var now = DateTime.UtcNow;

        var acpi = new SensorSnapshot(63, 58, null, null, TempSource.AcpiThermalZone, now);
        Check("an ACPI-sourced reading counts as a real CPU reading", acpi.HasCpu);
        Check("the source is carried through for the UI", acpi.CpuSource == TempSource.AcpiThermalZone);

        // When no CPU sensor exists the service copies the GPU value across, so the
        // CPU fan keeps tracking real heat instead of parking at the curve floor.
        var borrowed = new SensorSnapshot(69, 69, null, null, TempSource.BorrowedFromGpu, now);
        Check("a borrowed reading still drives the CPU curve", borrowed.HasCpu);
        Check("borrowing is visible to the UI", borrowed.CpuSource == TempSource.BorrowedFromGpu);
        Check("the four source states are distinct",
            new[] { TempSource.None, TempSource.HardwareMonitor, TempSource.AcpiThermalZone, TempSource.BorrowedFromGpu }
                .Distinct().Count() == 4);
    }

    /// <summary>Firmware uses placeholder values like 0 K or 2732 for "no reading".</summary>
    private static void ImplausibleReadingsAreRejected()
    {
        var now = DateTime.UtcNow;

        Check("zero is not a temperature",
            !new SensorSnapshot(0, null, null, null, TempSource.None, now).HasCpu);
        Check("a nonsense high value is rejected",
            !new SensorSnapshot(2459, null, null, null, TempSource.None, now).HasCpu);
        Check("a negative value is rejected",
            !new SensorSnapshot(-273, null, null, null, TempSource.None, now).HasCpu);
        Check("a normal idle reading is accepted",
            new SensorSnapshot(41, null, null, null, TempSource.AcpiThermalZone, now).HasCpu);
        Check("a hot but real reading is accepted",
            new SensorSnapshot(96, null, null, null, TempSource.HardwareMonitor, now).HasCpu);
    }

    // ------------------------------------------------------------- harness

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"── {name} " + new string('─', Math.Max(0, 52 - name.Length)));
    }

    private static void Check(string description, bool condition)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  PASS  {description}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {description}");
        }
    }
}
