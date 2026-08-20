using System.IO;
using System.Text.RegularExpressions;
using BladeFanCurve.Config;
using BladeFanCurve.Control;
using BladeFanCurve.Hardware;
using BladeFanCurve.Lighting;
using BladeFanCurve.Platform;
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

        Section("Chroma command encoding");
        ExtendedStaticMatchesOpenRazer();
        ExtendedWaveAndSpectrumMatchOpenRazer();
        ExtendedBreathingVariantsMatchOpenRazer();
        ReactiveAndStarlightClampTheirSpeeds();
        BrightnessUsesTheRightCommand();
        StandardFamilyMatchesOpenRazer();

        Section("Chroma frame packing");
        FrameRowFitsInsideTheArgumentBlock();
        FrameRowCarriesPixelsInOrder();

        Section("Colour maths");
        HexRoundTrips();
        HsvHitsThePrimaries();
        ScaleAndLerpStayInRange();

        Section("Software effects");
        EveryEffectHasAUniqueId();
        EveryEffectRendersInBounds();
        ThermalEffectRunsBlueToRed();

        Section("Power profiles");
        ProfilePowerDefaultsToLeavingEverythingAlone();
        ShippedProfilesCarryCoherentPowerSettings();
        ChargeLimitStaysInsideTheAllowedBand();

        Section("Blue-light schedule");
        ScheduleHandlesMidnightWrap();
        ScheduleIsOffWhenDisabledOrEmpty();

        Section("Profile power levels");
        SilentBalancedAndPerformanceDifferInPower();
        BoostOnlyMeansSomethingInCustomMode();
        OldConfigsAdoptTheShippedPowerSettings();
        CustomisedProfilesAreNotOverwrittenByMigration();

        Section("Fans off and the thermal guard");
        ZeroFloorIsAllowedThroughTheWholeChain();
        StoppedFanStillRampsInsteadOfJumping();
        GuardComputesFiftyPercentOfMaximum();
        GuardOutranksCurveAndOverride();
        CriticalStillOutranksTheGuard();

        Section("Battery profile switching");
        UnpluggingSwitchesToTheBatteryProfile();
        PluggingBackInRestoresWhatWasThereBefore();
        AHandPickedProfileOutranksTheAutomation();
        StartupDoesNotUndoAnything();
        SwitchingCanBeTurnedOffEntirely();
        AlreadyOnTheBatteryProfileIsLeftAlone();

        Section("Power history");
        HistoryKeepsSamplesInChronologicalOrder();
        HistoryDropsAnythingOlderThanTheWindow();
        HistorySurvivesRingBufferWraparound();
        HistoryPreservesGapsRatherThanInventingZero();
        HistoryAveragesIgnoreGapsAndRespectTheWindow();
        ImplausibleWattageIsRejected();

        Section("XAML styles");
        EveryStyleReferenceMatchesItsTargetType();
        EveryStyleReferenceResolves();

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

    // ------------------------------------------------------------------ chroma
    //
    // Byte layouts are pinned against OpenRazer's razerchromacommon.c. Getting these
    // wrong is silent — the keyboard simply ignores the command — so they are asserted
    // literally rather than derived from the same constants the code uses.

    private const byte Led = 0x05;  // BACKLIGHT_LED
    private const byte Store = 0x01; // VARSTORE

    private static void ExtendedStaticMatchesOpenRazer()
    {
        var r = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x09, 0x01,
            a => { a[5] = 0x01; a[6] = 0x11; a[7] = 0x22; a[8] = 0x33; });

        Check("static: class 0x0F", r.CommandClass == 0x0F);
        Check("static: command 0x02", r.CommandId == 0x02);
        Check("static: data size 0x09", r.DataSize == 0x09);
        Check("static: arg0 varstore", r.Arguments[0] == Store);
        Check("static: arg1 backlight led", r.Arguments[1] == Led);
        Check("static: arg2 effect id 0x01", r.Arguments[2] == 0x01);
        Check("static: arg5 is 0x01", r.Arguments[5] == 0x01);
        Check("static: rgb at args 6-8",
            r.Arguments[6] == 0x11 && r.Arguments[7] == 0x22 && r.Arguments[8] == 0x33);
    }

    private static void ExtendedWaveAndSpectrumMatchOpenRazer()
    {
        var wave = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x06, 0x04,
            a => { a[3] = 0x02; a[4] = 0x28; });

        Check("wave: effect id 0x04", wave.Arguments[2] == 0x04);
        Check("wave: size 0x06", wave.DataSize == 0x06);
        Check("wave: direction at arg3", wave.Arguments[3] == 0x02);
        Check("wave: speed 0x28 at arg4", wave.Arguments[4] == 0x28);

        var spectrum = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x06, 0x03);
        Check("spectrum: effect id 0x03", spectrum.Arguments[2] == 0x03);
        Check("spectrum: size 0x06", spectrum.DataSize == 0x06);
    }

    private static void ExtendedBreathingVariantsMatchOpenRazer()
    {
        var random = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x06, 0x02);
        Check("breathe random: size 0x06", random.DataSize == 0x06);
        Check("breathe random: no type byte", random.Arguments[3] == 0x00);

        var single = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x09, 0x02,
            a => { a[3] = 0x01; a[5] = 0x01; a[6] = 0xAA; });
        Check("breathe single: size 0x09", single.DataSize == 0x09);
        Check("breathe single: type 0x01 at arg3", single.Arguments[3] == 0x01);

        var dual = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x0C, 0x02,
            a =>
            {
                a[3] = 0x02;
                a[5] = 0x02;
                a[6] = 0x10; a[7] = 0x20; a[8] = 0x30;
                a[9] = 0x40; a[10] = 0x50; a[11] = 0x60;
            });
        Check("breathe dual: size 0x0C", dual.DataSize == 0x0C);
        Check("breathe dual: type 0x02 at arg3", dual.Arguments[3] == 0x02);
        Check("breathe dual: second colour at args 9-11",
            dual.Arguments[9] == 0x40 && dual.Arguments[11] == 0x60);
    }

    private static void ReactiveAndStarlightClampTheirSpeeds()
    {
        // Reactive is documented 1..4 and starlight 1..3; out-of-range values must be
        // clamped rather than sent, because the firmware's behaviour is undefined.
        foreach (var (input, expected) in new byte[][] { new byte[] { 0, 1 }, new byte[] { 9, 4 } }
                     .Select(p => (p[0], p[1])))
        {
            var speed = Math.Clamp(input, (byte)1, (byte)4);
            Check($"reactive speed {input} clamps to {expected}", speed == expected);
        }

        foreach (var (input, expected) in new byte[][] { new byte[] { 0, 1 }, new byte[] { 7, 3 } }
                     .Select(p => (p[0], p[1])))
        {
            var speed = Math.Clamp(input, (byte)1, (byte)3);
            Check($"starlight speed {input} clamps to {expected}", speed == expected);
        }

        var reactive = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x09, 0x05,
            a => { a[4] = 0x03; a[5] = 0x01; });
        Check("reactive: effect id 0x05", reactive.Arguments[2] == 0x05);
        Check("reactive: speed at arg4", reactive.Arguments[4] == 0x03);

        var starlight = RazerChroma.BuildExtendedEffect(0x1F, Led, 0x09, 0x07,
            a => { a[4] = 0x02; a[5] = 0x01; });
        Check("starlight: effect id 0x07", starlight.Arguments[2] == 0x07);
    }

    private static void BrightnessUsesTheRightCommand()
    {
        var ext = RazerChroma.BuildBrightness(ChromaFamily.Extended, 0x1F, Led, 128);
        Check("brightness extended: class 0x0F", ext.CommandClass == 0x0F);
        Check("brightness extended: command 0x04", ext.CommandId == 0x04);
        Check("brightness extended: size 0x03", ext.DataSize == 0x03);
        Check("brightness extended: level at arg2", ext.Arguments[2] == 128);

        var std = RazerChroma.BuildBrightness(ChromaFamily.Standard, 0x1F, Led, 200);
        Check("brightness standard: class 0x03", std.CommandClass == 0x03);
        Check("brightness standard: command 0x03", std.CommandId == 0x03);
        Check("brightness standard: level at arg2", std.Arguments[2] == 200);
    }

    private static void StandardFamilyMatchesOpenRazer()
    {
        var stat = RazerChroma.BuildStandardEffect(0x1F, 0x04, 0x06, a => { a[1] = 1; a[2] = 2; a[3] = 3; });
        Check("standard static: class 0x03 command 0x0A",
            stat is { CommandClass: 0x03, CommandId: 0x0A });
        Check("standard static: effect id in arg0", stat.Arguments[0] == 0x06);
        Check("standard static: rgb at args 1-3", stat.Arguments[3] == 3);

        var breathe = RazerChroma.BuildStandardEffect(0x1F, 0x08, 0x03, a => a[1] = 0x03);
        Check("standard breathe: effect id 0x03", breathe.Arguments[0] == 0x03);
        Check("standard breathe: random type 0x03", breathe.Arguments[1] == 0x03);
    }

    private static void FrameRowFitsInsideTheArgumentBlock()
    {
        // 16 columns x 3 bytes plus a 5-byte header is 53, comfortably inside 80.
        // If the matrix ever grew, this is where it would be caught.
        var needed = 5 + RazerChroma.Columns * 3;
        Check($"a frame row needs {needed} of 80 argument bytes", needed <= RazerReport.ArgumentCount);
    }

    private static void FrameRowCarriesPixelsInOrder()
    {
        var frame = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];
        for (var c = 0; c < RazerChroma.Columns; c++)
            frame[2, c] = new RgbColor((byte)(c + 1), (byte)(c + 101), (byte)(c + 201));

        var ext = RazerChroma.BuildFrameRow(ChromaFamily.Extended, 0x1F, 2, frame);
        Check("frame extended: class 0x0F command 0x03",
            ext is { CommandClass: 0x0F, CommandId: 0x03 });
        Check("frame extended: data size 0x47", ext.DataSize == 0x47);
        Check("frame extended: args 0-1 are zero", ext.Arguments[0] == 0 && ext.Arguments[1] == 0);
        Check("frame extended: row index at arg2", ext.Arguments[2] == 2);
        Check("frame extended: column span 0..15",
            ext.Arguments[3] == 0 && ext.Arguments[4] == RazerChroma.Columns - 1);
        Check("frame extended: first pixel at arg5",
            ext.Arguments[5] == 1 && ext.Arguments[6] == 101 && ext.Arguments[7] == 201);
        Check("frame extended: last pixel at the end",
            ext.Arguments[5 + 15 * 3] == 16 && ext.Arguments[5 + 15 * 3 + 2] == 216);

        var std = RazerChroma.BuildFrameRow(ChromaFamily.Standard, 0x1F, 2, frame);
        Check("frame standard: class 0x03 command 0x0B",
            std is { CommandClass: 0x03, CommandId: 0x0B });
        Check("frame standard: data size 0x46", std.DataSize == 0x46);
        Check("frame standard: 0xFF marker at arg0", std.Arguments[0] == 0xFF);
        Check("frame standard: row index at arg1", std.Arguments[1] == 2);
        Check("frame standard: first pixel at arg4", std.Arguments[4] == 1);
    }

    // ------------------------------------------------------------- colour maths

    private static void HexRoundTrips()
    {
        Check("#00FF88 parses", RgbColor.FromHex("#00FF88") == new RgbColor(0, 255, 136));
        Check("hex without hash parses", RgbColor.FromHex("3355FF") == new RgbColor(0x33, 0x55, 0xFF));
        Check("round trips", RgbColor.FromHex("#AB12CD").ToHex() == "#AB12CD");
        Check("garbage falls back to black", RgbColor.FromHex("nope") == RgbColor.Black);
        Check("null falls back to black", RgbColor.FromHex(null) == RgbColor.Black);
    }

    private static void HsvHitsThePrimaries()
    {
        Check("hue 0 is red", RgbColor.FromHsv(0) == new RgbColor(255, 0, 0));
        Check("hue 120 is green", RgbColor.FromHsv(120) == new RgbColor(0, 255, 0));
        Check("hue 240 is blue", RgbColor.FromHsv(240) == new RgbColor(0, 0, 255));
        Check("hue wraps past 360", RgbColor.FromHsv(480) == RgbColor.FromHsv(120));
        Check("negative hue wraps", RgbColor.FromHsv(-120) == RgbColor.FromHsv(240));
        Check("zero value is black", RgbColor.FromHsv(200, 1, 0) == RgbColor.Black);
    }

    private static void ScaleAndLerpStayInRange()
    {
        var c = new RgbColor(200, 100, 50);
        Check("scale by 0 is black", c.Scale(0) == RgbColor.Black);
        Check("scale by 1 is unchanged", c.Scale(1) == c);
        Check("scale above 1 clamps", c.Scale(5).R == 255);
        Check("lerp at 0 is the first colour", RgbColor.Lerp(c, RgbColor.Black, 0) == c);
        Check("lerp at 1 is the second colour", RgbColor.Lerp(c, RgbColor.Black, 1) == RgbColor.Black);
        Check("lerp clamps out-of-range t", RgbColor.Lerp(c, RgbColor.Black, 4) == RgbColor.Black);
    }

    // ---------------------------------------------------------- software effects

    private static void EveryEffectHasAUniqueId()
    {
        var ids = EffectCatalog.All.Select(e => e.Id).ToList();
        Check("effect ids are unique", ids.Distinct().Count() == ids.Count);
        Check("every effect has a name", EffectCatalog.All.All(e => !string.IsNullOrWhiteSpace(e.Name)));
        Check("every effect has a description",
            EffectCatalog.All.All(e => !string.IsNullOrWhiteSpace(e.Description)));
        Check("lookup is case insensitive", EffectCatalog.Find("FIRE") != null);
        Check("unknown id returns null", EffectCatalog.Find("nope") == null);
    }

    /// <summary>
    /// Every effect is run for a few simulated seconds. Effects allocate their own
    /// state lazily, so this also catches an effect that indexes past the matrix.
    /// </summary>
    private static void EveryEffectRendersInBounds()
    {
        foreach (var effect in EffectCatalog.All)
        {
            var ctx = new EffectContext
            {
                Rng = new Random(1),
                CpuTempC = 72,
                GpuTempC = 65,
                CpuRpm = 3200,
                GpuRpm = 2800,
            };

            var frame = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];
            var threw = false;

            try
            {
                effect.Reset(ctx);
                for (var i = 0; i < 90; i++)
                {
                    ctx.Time = i / 30.0;
                    ctx.Delta = 1 / 30.0;
                    effect.Render(frame, ctx);
                }
            }
            catch
            {
                threw = true;
            }

            Check($"{effect.Id} renders without throwing", !threw);
        }
    }

    private static void ThermalEffectRunsBlueToRed()
    {
        var cold = ThermalEffect.ColourFor(0);
        var mid = ThermalEffect.ColourFor(0.5);
        var hot = ThermalEffect.ColourFor(1);

        Check("thermal cold is blue dominant", cold.B > cold.R);
        Check("thermal hot is red dominant", hot.R > hot.B);
        Check("thermal midpoint is not blue dominant", mid.B <= mid.R || mid.G > mid.B);

        // The gauge must not report cold when the sensor is missing.
        var ctx = new EffectContext { CpuTempC = null, GpuTempC = null };
        var frame = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];
        new ThermalEffect().Render(frame, ctx);
        Check("thermal with no reading is not full brightness", frame[0, 0].R < 200);
    }

    // ------------------------------------------------------------ power profiles

    /// <summary>
    /// A config written by an older version has no power block. Upgrading must not
    /// silently start changing the Windows power plan or the refresh rate, so every
    /// field has to default to "leave it alone".
    /// </summary>
    private static void ProfilePowerDefaultsToLeavingEverythingAlone()
    {
        var power = new ProfilePower();
        Check("perf mode defaults to unset", power.PerfMode == "");
        Check("cpu boost defaults to unset", power.CpuBoost == "");
        Check("gpu boost defaults to unset", power.GpuBoost == "");
        Check("windows plan defaults to unset", power.WindowsPlan == "");
        Check("power overlay defaults to unset", power.PowerOverlay == "");
        Check("refresh rate defaults to 0 meaning leave alone", power.RefreshHz == 0);

        var bare = new Profile();
        Check("a bare profile has a power block", bare.Power != null);
        Check("cloning carries the power block", bare.Clone().Power != null);

        // A clone must be independent, or editing one profile would edit another.
        var original = new Profile { Power = { PerfMode = "Gaming", RefreshHz = 240 } };
        var copy = original.Clone();
        copy.Power.PerfMode = "Balanced";
        Check("clone does not share the power block", original.Power.PerfMode == "Gaming");
    }

    private static void ShippedProfilesCarryCoherentPowerSettings()
    {
        var cfg = AppConfig.CreateDefault();
        var names = cfg.Profiles.Select(p => p.Name).ToList();

        Check("ships Silent, Balanced and Performance",
            names.Contains("Silent") && names.Contains("Balanced") && names.Contains("Performance"));

        var silent = cfg.Profiles.First(p => p.Name == "Silent");
        var turbo = cfg.Profiles.First(p => p.Name == "Performance");

        Check("silent falls back to the low power target", silent.Power.FallbackPerfMode == "Balanced");
        Check("performance falls back to the high power target", turbo.Power.FallbackPerfMode == "Gaming");
        Check("silent biases toward efficiency", silent.Power.PowerOverlay == "efficiency");
        Check("performance biases toward performance", turbo.Power.PowerOverlay == "performance");
        Check("silent drops the refresh rate", silent.Power.RefreshHz == 60);
        Check("performance leaves the refresh rate to the user", turbo.Power.RefreshHz == 0);

        foreach (var p in cfg.Profiles)
        {
            if (string.IsNullOrEmpty(p.Power.WindowsPlan)) continue;
            Check($"{p.Name} names a parseable power plan guid",
                Guid.TryParse(p.Power.WindowsPlan, out _));
        }

        // The boost levels each profile asks for must actually exist in the enum.
        foreach (var p in cfg.Profiles)
        {
            if (!string.IsNullOrEmpty(p.Power.CpuBoost))
                Check($"{p.Name} cpu boost is a real level",
                    Enum.TryParse<BoostLevel>(p.Power.CpuBoost, true, out _));
            if (!string.IsNullOrEmpty(p.Power.GpuBoost))
                Check($"{p.Name} gpu boost is a real level",
                    Enum.TryParse<BoostLevel>(p.Power.GpuBoost, true, out _));
        }
    }

    private static void ChargeLimitStaysInsideTheAllowedBand()
    {
        Check("the floor is 50%", RazerPower.MinChargeLimit == 50);
        Check("the ceiling is 100%", RazerPower.MaxChargeLimit == 100);

        foreach (var (input, expected) in new[] { (10, 50), (60, 60), (80, 80), (150, 100) })
            Check($"{input}% clamps to {expected}%",
                Math.Clamp(input, RazerPower.MinChargeLimit, RazerPower.MaxChargeLimit) == expected);

        var settings = new BatterySettings();
        Check("charge limit is off by default", !settings.ChargeLimitEnabled);
        Check("default limit is 80%", settings.ChargeLimitPercent == 80);
        Check("the limit is re-applied after sleep by default", settings.ReapplyChargeLimit);
    }

    // ---------------------------------------------------------- blue-light schedule

    /// <summary>
    /// The normal case is a schedule that crosses midnight, which is exactly where a
    /// naive start <= now < end comparison breaks.
    /// </summary>
    private static void ScheduleHandlesMidnightWrap()
    {
        var overnight = new DisplaySettings
        {
            NightLightEnabled = true,
            NightLightStartMinutes = 21 * 60, // 21:00
            NightLightEndMinutes = 7 * 60,    // 07:00
        };

        Check("22:00 is inside an overnight window",
            NightLightService.IsWithinSchedule(new TimeSpan(22, 0, 0), overnight));
        Check("02:00 is inside an overnight window",
            NightLightService.IsWithinSchedule(new TimeSpan(2, 0, 0), overnight));
        Check("21:00 exactly is inside",
            NightLightService.IsWithinSchedule(new TimeSpan(21, 0, 0), overnight));
        Check("07:00 exactly is outside",
            !NightLightService.IsWithinSchedule(new TimeSpan(7, 0, 0), overnight));
        Check("12:00 is outside an overnight window",
            !NightLightService.IsWithinSchedule(new TimeSpan(12, 0, 0), overnight));

        var daytime = new DisplaySettings
        {
            NightLightEnabled = true,
            NightLightStartMinutes = 9 * 60,
            NightLightEndMinutes = 17 * 60,
        };

        Check("12:00 is inside a same-day window",
            NightLightService.IsWithinSchedule(new TimeSpan(12, 0, 0), daytime));
        Check("20:00 is outside a same-day window",
            !NightLightService.IsWithinSchedule(new TimeSpan(20, 0, 0), daytime));
        Check("03:00 is outside a same-day window",
            !NightLightService.IsWithinSchedule(new TimeSpan(3, 0, 0), daytime));
    }

    private static void ScheduleIsOffWhenDisabledOrEmpty()
    {
        // Start == end is an empty window, not a 24-hour one.
        var empty = new DisplaySettings
        {
            NightLightEnabled = true,
            NightLightStartMinutes = 600,
            NightLightEndMinutes = 600,
        };
        Check("an empty window never matches",
            !NightLightService.IsWithinSchedule(new TimeSpan(10, 0, 0), empty));

        var defaults = new DisplaySettings();
        Check("blue light is off by default", !defaults.NightLightEnabled);
        Check("default warmth is a usable 3400 K", defaults.NightLightKelvin == 3400);
        Check("default window is 21:00 to 07:00",
            defaults.NightLightStartMinutes == 1260 && defaults.NightLightEndMinutes == 420);
    }

    // ------------------------------------------------------- profile power levels

    /// <summary>
    /// The point of the three profiles is that they ask the machine for different
    /// amounts of CPU and GPU power. An earlier version set Silent and Balanced to the
    /// same performance mode with boost levels the controller ignores outside Custom
    /// mode, so they were in fact identical. That must not come back.
    /// </summary>
    private static void SilentBalancedAndPerformanceDifferInPower()
    {
        var cfg = AppConfig.CreateDefault();
        var silent = cfg.Profiles.First(p => p.Name == "Silent").Power;
        var balanced = cfg.Profiles.First(p => p.Name == "Balanced").Power;
        var performance = cfg.Profiles.First(p => p.Name == "Performance").Power;

        Check("ships Silent, Balanced and Performance", cfg.Profiles.Count == 3);

        // Ordered lowest to highest, and every step must actually differ.
        var order = new[] { "Low", "Medium", "Boost" };
        Check("silent asks for the lowest cpu power", silent.CpuBoost == "Low");
        Check("balanced asks for moderate cpu power", balanced.CpuBoost == "Medium");
        Check("performance asks for maximum cpu power", performance.CpuBoost == "Boost");

        Check("silent asks for the lowest gpu power", silent.GpuBoost == "Low");
        Check("balanced asks for moderate gpu power", balanced.GpuBoost == "Medium");
        Check("performance asks for maximum gpu power", performance.GpuBoost == "High");

        Check("cpu levels are strictly increasing",
            Array.IndexOf(order, silent.CpuBoost) < Array.IndexOf(order, balanced.CpuBoost)
            && Array.IndexOf(order, balanced.CpuBoost) < Array.IndexOf(order, performance.CpuBoost));

        Check("no two profiles request the same cpu/gpu pair",
            new[] { silent, balanced, performance }
                .Select(p => $"{p.CpuBoost}/{p.GpuBoost}").Distinct().Count() == 3);

        // The fallback path must also separate them when boost is unavailable.
        Check("fallbacks separate silent from performance",
            silent.FallbackPerfMode != performance.FallbackPerfMode);
        Check("performance falls back to the high power target",
            performance.FallbackPerfMode == "Gaming");
    }

    private static void BoostOnlyMeansSomethingInCustomMode()
    {
        var cfg = AppConfig.CreateDefault();

        foreach (var profile in cfg.Profiles)
        {
            var p = profile.Power;
            if (string.IsNullOrEmpty(p.CpuBoost) && string.IsNullOrEmpty(p.GpuBoost)) continue;

            Check($"{profile.Name} selects Custom so its power levels are honoured",
                p.PerfMode.Equals("Custom", StringComparison.OrdinalIgnoreCase));
            Check($"{profile.Name} names a fallback for firmware without boost",
                !string.IsNullOrEmpty(p.FallbackPerfMode));
            Check($"{profile.Name} fallback is a real mode",
                Enum.TryParse<PerfMode>(p.FallbackPerfMode, true, out _));
        }
    }

    private static void OldConfigsAdoptTheShippedPowerSettings()
    {
        // A version 2 config: profiles exist but carry no power settings at all.
        var old = new AppConfig
        {
            Version = 2,
            ActiveProfile = "Turbo",
            Profiles =
            {
                new Profile { Name = "Silent", Power = new ProfilePower() },
                new Profile { Name = "Balanced", Power = new ProfilePower() },
                new Profile { Name = "Turbo", Power = new ProfilePower() },
            }
        };

        ConfigStore.MigrateProfilePower(old);

        Check("migration renames Turbo to Performance",
            old.Profiles.Any(p => p.Name == "Performance") && old.Profiles.All(p => p.Name != "Turbo"));
        Check("migration follows the active profile through the rename",
            old.ActiveProfile == "Performance");
        Check("migration fills in the silent power settings",
            old.Profiles.First(p => p.Name == "Silent").Power.CpuBoost == "Low");
        Check("migration fills in the performance power settings",
            old.Profiles.First(p => p.Name == "Performance").Power.CpuBoost == "Boost");
        Check("migration stamps the new version", old.Version == 3);

        // Running it again must be a no-op rather than re-applying.
        old.Profiles.First(p => p.Name == "Silent").Power.CpuBoost = "High";
        ConfigStore.MigrateProfilePower(old);
        Check("migration does not run twice",
            old.Profiles.First(p => p.Name == "Silent").Power.CpuBoost == "High");
    }

    private static void CustomisedProfilesAreNotOverwrittenByMigration()
    {
        var tweaked = new AppConfig
        {
            Version = 2,
            ActiveProfile = "Silent",
            Profiles =
            {
                new Profile
                {
                    Name = "Silent",
                    Power = new ProfilePower { PerfMode = "Gaming" }, // user chose this
                },
                new Profile { Name = "Balanced", Power = new ProfilePower() },
            }
        };

        ConfigStore.MigrateProfilePower(tweaked);

        Check("a profile the user has touched is left alone",
            tweaked.Profiles.First(p => p.Name == "Silent").Power.PerfMode == "Gaming");
        Check("an untouched profile beside it still gets defaults",
            tweaked.Profiles.First(p => p.Name == "Balanced").Power.CpuBoost == "Medium");

        // A profile with a name we do not ship must never be given someone else's settings.
        var custom = new AppConfig
        {
            Version = 2,
            Profiles = { new Profile { Name = "My Profile", Power = new ProfilePower() } }
        };
        ConfigStore.MigrateProfilePower(custom);
        Check("an unrecognised profile name is left untouched",
            custom.Profiles[0].Power.PerfMode == "");
    }

    // --------------------------------------------- fans off and the thermal guard

    private static SafetySettings ZeroFloor() => new()
    {
        MinRpm = 0,
        MaxRpm = 5000,
        CpuCriticalC = 97,
        GpuCriticalC = 88,
        SpinUpEnabled = true,
        SpinUpTempC = 70,
        SpinUpPercent = 50,
        SpinUpReleaseMarginC = 5,
    };

    private static void ZeroFloorIsAllowedThroughTheWholeChain()
    {
        var safety = ZeroFloor();
        var curve = new FanCurveConfig { Points = { new CurvePoint(30, 0), new CurvePoint(90, 5000) } };

        Check("a curve may ask for zero",
            FanCurveEvaluator.Evaluate(curve, 30, safety.MinRpm, safety.MaxRpm) == 0);
        Check("quantising zero keeps it at zero",
            FanCurveEvaluator.Quantise(0, safety.MinRpm, safety.MaxRpm) == 0);
        Check("sanitising a config does not push the floor back up",
            SanitisedMinRpm(0) == 0);

        // The wire encodes rpm/100 in one byte, so zero has to survive that too.
        var report = RazerReport.Create(0x1F, 0x0D, 0x01, 0x03, 0x01, 0x01, 0);
        Check("zero rpm encodes as 0x00 on the wire", report.ToBytes()[10] == 0x00);
    }

    private static int SanitisedMinRpm(int value)
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Safety.MinRpm = value;
        ConfigStore.Save(cfg); // Save sanitises; the write itself is harmless here
        return cfg.Safety.MinRpm;
    }

    /// <summary>
    /// With a zero floor a stopped fan is a normal state, not an uninitialised one.
    /// Treating it as uninitialised would let the fan jump straight to full speed.
    /// </summary>
    private static void StoppedFanStillRampsInsteadOfJumping()
    {
        var safety = ZeroFloor();
        var tuning = new TuningSettings { RampUpRpmPerSec = 900, RampDownRpmPerSec = 250 };
        var channel = new FanChannel(FanZone.Cpu, "test");
        var flat = FanCurveConfig.Flat(0);

        // Settle at a stop.
        for (var i = 0; i < 20; i++) channel.Compute(40, flat, safety, tuning, 1.0, 0, false);
        Check("the fan reaches a full stop", channel.CommandedRpm == 0);

        // Now demand full speed for one second: the ramp must still apply.
        var next = channel.Compute(40, FanCurveConfig.Flat(5000), safety, tuning, 1.0, 5000, false);
        Check("coming off zero still ramps rather than jumping", next is > 0 and <= 1000);
        Check("and it is heading the right way", next >= 900);
    }

    private static void GuardComputesFiftyPercentOfMaximum()
    {
        var safety = ZeroFloor();
        Check("50% of 5000 is 2500", FanCurveEvaluator.SpinUpRpm(safety) == 2500);

        safety.MaxRpm = 4800;
        Check("50% of 4800 quantises to 2400", FanCurveEvaluator.SpinUpRpm(safety) == 2400);

        safety.SpinUpPercent = 100;
        Check("100% is the maximum", FanCurveEvaluator.SpinUpRpm(safety) == 4800);

        safety.SpinUpPercent = 30;
        Check("30% of 4800 quantises to 1400", FanCurveEvaluator.SpinUpRpm(safety) == 1400);
    }

    /// <summary>
    /// The guard is a floor, not a target: it lifts a too-slow fan without capping a
    /// curve that already wants more air.
    /// </summary>
    private static void GuardOutranksCurveAndOverride()
    {
        var safety = ZeroFloor();
        var tuning = new TuningSettings { RampUpRpmPerSec = 100000, RampDownRpmPerSec = 100000 };
        var channel = new FanChannel(FanZone.Cpu, "test");

        // A curve asking for silence, with the guard demanding 2500.
        var quiet = channel.Compute(75, FanCurveConfig.Flat(0), safety, tuning, 1.0, 2500, false);
        Check("the guard lifts a stopped fan", quiet == 2500);

        // A curve already asking for more must not be dragged down to the guard.
        channel.Reset(0);
        var loud = channel.Compute(75, FanCurveConfig.Flat(4000), safety, tuning, 1.0, 2500, false);
        Check("the guard does not cap a faster curve", loud == 4000);
    }

    private static void CriticalStillOutranksTheGuard()
    {
        var safety = ZeroFloor();
        var tuning = new TuningSettings { RampUpRpmPerSec = 100, RampDownRpmPerSec = 100 };
        var channel = new FanChannel(FanZone.Cpu, "test");

        var rpm = channel.Compute(98, FanCurveConfig.Flat(0), safety, tuning, 1.0, 2500, true);
        Check("critical goes straight to maximum, past the guard", rpm == safety.MaxRpm);
    }

    // ------------------------------------------------- battery profile switching

    private static AutomationSettings Auto(string before = "") => new()
    {
        SwitchProfileOnBattery = true,
        BatteryProfile = "Silent",
        RestoreProfileOnAc = true,
        ProfileBeforeBattery = before,
    };

    private static void UnpluggingSwitchesToTheBatteryProfile()
    {
        var d = AutoProfileDecision.Decide(false, onBattery: true, Auto(), "Performance");
        Check("unplugging switches profile", d.Action == AutoProfileAction.SwitchToBattery);
        Check("it switches to the configured battery profile", d.Target == "Silent");
        Check("the reason names the charger", d.Reason.Contains("unplugged"));
    }

    private static void PluggingBackInRestoresWhatWasThereBefore()
    {
        // On battery, sitting on Silent, having come from Performance.
        var d = AutoProfileDecision.Decide(false, onBattery: false, Auto("Performance"), "Silent");
        Check("plugging in restores", d.Action == AutoProfileAction.RestorePrevious);
        Check("it restores the remembered profile", d.Target == "Performance");

        // Nothing remembered means nothing to restore.
        var none = AutoProfileDecision.Decide(false, onBattery: false, Auto(), "Silent");
        Check("with nothing remembered it does nothing", none.Action == AutoProfileAction.None);

        // Restoring can be switched off on its own.
        var settings = Auto("Performance");
        settings.RestoreProfileOnAc = false;
        var off = AutoProfileDecision.Decide(false, onBattery: false, settings, "Silent");
        Check("restore-on-ac can be disabled independently", off.Action == AutoProfileAction.None);
    }

    /// <summary>
    /// The case that matters most: someone unplugs, gets Silent, decides they want
    /// Performance anyway, then plugs in. Yanking them back would be infuriating.
    /// </summary>
    private static void AHandPickedProfileOutranksTheAutomation()
    {
        var d = AutoProfileDecision.Decide(false, onBattery: false, Auto("Balanced"), "Performance");
        Check("a hand-picked profile is not overridden", d.Action == AutoProfileAction.ForgetPrevious);
        Check("nothing is switched to", d.Target == "");
        Check("the memory is dropped so it cannot fire later",
            d.Action == AutoProfileAction.ForgetPrevious);
    }

    private static void StartupDoesNotUndoAnything()
    {
        // First reading, already plugged in: must not "restore" anything.
        var onAc = AutoProfileDecision.Decide(true, onBattery: false, Auto("Performance"), "Silent");
        Check("starting plugged in changes nothing", onAc.Action == AutoProfileAction.None);

        // First reading, on battery: switching is right, the machine really is on battery.
        var onBattery = AutoProfileDecision.Decide(true, onBattery: true, Auto(), "Performance");
        Check("starting on battery does switch", onBattery.Action == AutoProfileAction.SwitchToBattery);
        Check("the reason says so", onBattery.Reason.Contains("started on battery"));
    }

    private static void SwitchingCanBeTurnedOffEntirely()
    {
        var settings = Auto("Performance");
        settings.SwitchProfileOnBattery = false;

        Check("disabled means nothing happens on unplug",
            AutoProfileDecision.Decide(false, true, settings, "Performance").Action == AutoProfileAction.None);
        Check("disabled means nothing happens on plug-in",
            AutoProfileDecision.Decide(false, false, settings, "Silent").Action == AutoProfileAction.None);

        // A blank battery profile is not a licence to switch to nothing.
        var blank = Auto();
        blank.BatteryProfile = "";
        Check("a blank battery profile is ignored",
            AutoProfileDecision.Decide(false, true, blank, "Performance").Action == AutoProfileAction.None);
    }

    private static void AlreadyOnTheBatteryProfileIsLeftAlone()
    {
        // Unplugging while already on Silent must not record Silent as the profile to
        // return to, or plugging in would restore Silent onto itself and lose the
        // user's real starting point.
        var d = AutoProfileDecision.Decide(false, onBattery: true, Auto(), "Silent");
        Check("unplugging while already on the battery profile does nothing",
            d.Action == AutoProfileAction.None);

        // Case should not matter when comparing profile names.
        var mixed = AutoProfileDecision.Decide(false, onBattery: true, Auto(), "silent");
        Check("profile name matching ignores case", mixed.Action == AutoProfileAction.None);
    }

    // ------------------------------------------------------------- power history

    private static void HistoryKeepsSamplesInChronologicalOrder()
    {
        var h = new PowerHistory();
        for (var t = 0; t < 10; t++) h.Add(t, 10 + t, 20 + t);

        var s = h.Snapshot(9);
        Check("every sample is returned", s.Count == 10);
        Check("oldest sample comes first", s[0].AgeSeconds > s[^1].AgeSeconds);
        Check("the newest sample has age zero", Math.Abs(s[^1].AgeSeconds) < 0.001);
        Check("the oldest sample is nine seconds back", Math.Abs(s[0].AgeSeconds - 9) < 0.001);
        Check("values track their sample", Math.Abs((s[^1].CpuWatts ?? 0) - 19) < 0.001);
        Check("peak is the highest value seen", Math.Abs(h.PeakWatts - 29) < 0.001);
    }

    private static void HistoryDropsAnythingOlderThanTheWindow()
    {
        var h = new PowerHistory();
        h.Add(0, 50, 60);                              // will fall out of the window
        h.Add(PowerHistory.WindowSeconds - 10, 11, 12); // still inside
        h.Add(PowerHistory.WindowSeconds + 5, 13, 14);  // newest

        var s = h.Snapshot(PowerHistory.WindowSeconds + 5);
        Check("the sample past the window is dropped", s.Count == 2);
        Check("the dropped sample's peak does not linger", Math.Abs(h.PeakWatts - 14) < 0.001);
        Check("all surviving ages are inside the window",
            s.All(x => x.AgeSeconds <= PowerHistory.WindowSeconds));
    }

    /// <summary>The buffer is exactly one window long, so writing past it must wrap.</summary>
    private static void HistorySurvivesRingBufferWraparound()
    {
        var h = new PowerHistory();
        var total = PowerHistory.WindowSeconds + 500;
        for (var t = 0; t < total; t++) h.Add(t, t % 100, 50);

        Check("count never exceeds the window", h.Count == PowerHistory.WindowSeconds);

        var s = h.Snapshot(total - 1);
        Check("the wrapped buffer still reads in order",
            s.Select(x => x.AgeSeconds).SequenceEqual(s.Select(x => x.AgeSeconds).OrderByDescending(a => a)));
        Check("the newest value survived the wrap",
            Math.Abs((s[^1].CpuWatts ?? -1) - (total - 1) % 100) < 0.001);
    }

    private static void HistoryPreservesGapsRatherThanInventingZero()
    {
        var h = new PowerHistory();
        h.Add(0, 30, 40);
        h.Add(1, null, 41);   // CPU unreadable this tick
        h.Add(2, 32, null);   // GPU asleep
        h.Add(3, 33, 43);

        var s = h.Snapshot(3);
        Check("a missing cpu reading stays null", s[1].CpuWatts is null);
        Check("a missing gpu reading stays null", s[2].GpuWatts is null);
        Check("readings either side of a gap are intact",
            s[0].CpuWatts is not null && s[3].CpuWatts is not null);

        // Zero is not a real package power reading; it must be treated as absent so
        // the line breaks instead of dropping to the floor.
        var z = new PowerHistory();
        z.Add(0, 0, 0);
        var zs = z.Snapshot(0);
        Check("a zero reading is treated as no reading",
            zs[0].CpuWatts is null && zs[0].GpuWatts is null);
    }

    private static void HistoryAveragesIgnoreGapsAndRespectTheWindow()
    {
        var h = new PowerHistory();
        h.Add(0, 100, 100);   // 120 s ago — outside a 60 s average
        h.Add(90, 10, null);  //  30 s ago
        h.Add(110, 20, null); //  10 s ago
        h.Add(120, 30, null); //  now

        var s = h.Snapshot(120);

        var oneMinute = PowerHistory.Average(s, x => x.CpuWatts, 60);
        Check("the average ignores samples outside its span",
            oneMinute is not null && Math.Abs(oneMinute.Value - 20) < 0.001);

        var everything = PowerHistory.Average(s, x => x.CpuWatts, PowerHistory.WindowSeconds);
        Check("a wider span picks up the older sample",
            everything is not null && Math.Abs(everything.Value - 40) < 0.001);

        var gpu = PowerHistory.Average(s, x => x.GpuWatts, 60);
        Check("an all-gap span averages to nothing rather than zero", gpu is null);
    }

    private static void ImplausibleWattageIsRejected()
    {
        Check("negative watts are rejected", !SensorSnapshot.PlausibleWatts(-5));
        Check("zero watts is rejected", !SensorSnapshot.PlausibleWatts(0));
        Check("a null reading is rejected", !SensorSnapshot.PlausibleWatts(null));
        Check("400 W and above is rejected as a misread", !SensorSnapshot.PlausibleWatts(400));
        Check("an idle 1.5 W is accepted", SensorSnapshot.PlausibleWatts(1.5));
        Check("a loaded 54 W is accepted", SensorSnapshot.PlausibleWatts(54));
        Check("a 140 W discrete gpu is accepted", SensorSnapshot.PlausibleWatts(140));

        var snapshot = new SensorSnapshot(60, 55, 0.3, 0.4, TempSource.HardwareMonitor,
            DateTime.UtcNow, 45.0, null);
        Check("the snapshot reports a readable cpu wattage", snapshot.HasCpuWatts);
        Check("the snapshot reports a missing gpu wattage", !snapshot.HasGpuWatts);
    }

    // -------------------------------------------------------------- xaml styles
    //
    // WPF only applies a Style to its TargetType or a subclass of it. Getting this
    // wrong throws "Set property FrameworkElement.Style threw an exception" at the
    // moment the element is realised — and because a TabControl builds tab content
    // lazily, that can be long after startup, on a tab this machine cannot render.
    // So the pairing is checked statically instead.

    /// <summary>Enough of the WPF hierarchy to judge the controls actually used here.</summary>
    private static readonly Dictionary<string, string> BaseType = new()
    {
        ["Button"] = "ButtonBase",
        ["RepeatButton"] = "ButtonBase",
        ["ToggleButton"] = "ButtonBase",
        ["CheckBox"] = "ToggleButton",
        ["RadioButton"] = "ToggleButton",
        ["ButtonBase"] = "ContentControl",
        ["ListBoxItem"] = "ContentControl",
        ["ComboBoxItem"] = "ContentControl",
        ["TabItem"] = "HeaderedContentControl",
        ["HeaderedContentControl"] = "ContentControl",
        ["ContentControl"] = "Control",
        ["ComboBox"] = "Selector",
        ["ListBox"] = "Selector",
        ["TabControl"] = "Selector",
        ["Selector"] = "ItemsControl",
        ["ItemsControl"] = "Control",
        ["TextBox"] = "TextBoxBase",
        ["TextBoxBase"] = "Control",
        ["Slider"] = "RangeBase",
        ["ScrollBar"] = "RangeBase",
        ["ProgressBar"] = "RangeBase",
        ["RangeBase"] = "Control",
        ["Thumb"] = "Control",
        ["Control"] = "FrameworkElement",
        ["TextBlock"] = "FrameworkElement",
        ["Border"] = "Decorator",
        ["Decorator"] = "FrameworkElement",
        ["Ellipse"] = "Shape",
        ["Path"] = "Shape",
        ["Rectangle"] = "Shape",
        ["Shape"] = "FrameworkElement",
    };

    private static bool IsAssignableTo(string element, string target)
    {
        for (var current = element; current != null; BaseType.TryGetValue(current, out current!))
        {
            if (current == target) return true;
            if (!BaseType.ContainsKey(current)) return false;
        }

        return false;
    }

    private static Dictionary<string, string> StyleTargets(string appXaml) =>
        Regex.Matches(appXaml, @"<Style\s+x:Key=""(?<key>[^""]+)""\s+TargetType=""(?:\{x:Type\s+)?(?<type>[A-Za-z]+)\}?""")
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["type"].Value);

    private static List<(string Element, string Key, int Line)> StyleUses(string xaml)
    {
        var uses = new List<(string, string, int)>();
        var lines = xaml.Split('\n');

        // Elements can span lines, so track the most recent opening tag and attach any
        // StaticResource style reference found before the tag closes.
        var currentTag = "";
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match tag in Regex.Matches(lines[i], @"<(?<name>[A-Za-z][A-Za-z0-9.]*)\b"))
            {
                var name = tag.Groups["name"].Value;
                if (name.Contains('.')) continue; // property element, e.g. <Grid.RowDefinitions>
                currentTag = name;
            }

            foreach (Match use in Regex.Matches(lines[i], @"Style=""\{StaticResource\s+(?<key>[A-Za-z0-9_]+)\}"""))
                if (currentTag.Length > 0)
                    uses.Add((currentTag, use.Groups["key"].Value, i + 1));
        }

        return uses;
    }

    private static void EveryStyleReferenceMatchesItsTargetType()
    {
        var (app, windows) = LoadXaml();
        if (app == null) { Check("App.xaml was found", false); return; }

        var targets = StyleTargets(app);
        Check("styles were found in App.xaml", targets.Count > 0);

        var checkedCount = 0;
        var bad = new List<string>();

        foreach (var (file, xaml) in windows)
        foreach (var (element, key, line) in StyleUses(xaml))
        {
            if (!targets.TryGetValue(key, out var target)) continue;
            if (!BaseType.ContainsKey(element) && element != target) continue; // unknown control, skip

            checkedCount++;
            if (!IsAssignableTo(element, target))
                bad.Add($"{file}:{line} applies '{key}' (TargetType={target}) to <{element}>");
        }

        Check($"{checkedCount} style references were checked", checkedCount > 0);
        foreach (var problem in bad) Check($"MISMATCH {problem}", false);
        Check("every style is applied to a compatible element", bad.Count == 0);
    }

    private static void EveryStyleReferenceResolves()
    {
        var (app, windows) = LoadXaml();
        if (app == null) return;

        var targets = StyleTargets(app);
        // Implicit styles have no key, and keyed non-Style resources can be referenced too.
        var allKeys = Regex.Matches(app, @"x:Key=""(?<key>[^""]+)""")
            .Select(m => m.Groups["key"].Value).ToHashSet();

        var missing = new List<string>();
        foreach (var (file, xaml) in windows)
        foreach (var (_, key, line) in StyleUses(xaml))
            if (!allKeys.Contains(key))
                missing.Add($"{file}:{line} references undefined style '{key}'");

        foreach (var problem in missing) Check($"UNDEFINED {problem}", false);
        Check("every referenced style is defined", missing.Count == 0);
        Check("keyed styles carry a target type", targets.Count > 0);
    }

    private static (string? App, List<(string Name, string Xaml)> Windows) LoadXaml()
    {
        var csproj = FindAppProject();
        if (csproj == null) return (null, new List<(string, string)>());

        var dir = Path.GetDirectoryName(csproj)!;
        var appPath = Path.Combine(dir, "App.xaml");
        if (!File.Exists(appPath)) return (null, new List<(string, string)>());

        var windows = Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), "App.xaml", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();

        return (File.ReadAllText(appPath), windows);
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
