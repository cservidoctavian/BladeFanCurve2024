using System.Diagnostics;
using System.IO;
using System.Text;
using BladeFanCurve.Config;
using BladeFanCurve.Hardware;
using BladeFanCurve.Lighting;
using BladeFanCurve.Sensors;
using BladeFanCurve.Platform;

namespace BladeFanCurve.Control;

/// <summary>
/// The control loop. Owns the Razer device and the sensor service, converts
/// temperatures into fan RPM, and — importantly — knows every way it can fail and
/// hands the fans back to the laptop's own thermal management when it does.
/// </summary>
public sealed class ControlLoop : IDisposable
{
    private const int DiscoveryBackoffSeconds = 8;
    private const int MaxConsecutiveWriteFailures = 5;
    private const int TachoEveryNCycles = 3;

    private readonly SensorService _sensors;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _wake = new(false);

    private readonly FanChannel _cpuFan = new(FanZone.Cpu, "CPU fan");
    private readonly FanChannel _gpuFan = new(FanZone.Gpu, "GPU fan");

    private AppConfig _config;
    private RazerLaptopDevice? _device;
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    private bool _manualEngaged;
    private bool _rpmVariantVerified;
    private int _verifyPendingRpm = -1;
    private int _consecutiveWriteFailures;
    private int _cycle;

    private double _lastDiscoveryAt = double.NegativeInfinity;
    private double _lastAssertAt = double.NegativeInfinity;
    private double _lastGoodSensorAt;
    private double _criticalUntil = double.NegativeInfinity;
    private double _lastTickAt;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private int? _overrideRpm;
    private volatile bool _diagnosticsBusy;

    public event Action<ControlStatus>? StatusUpdated;

    public ControlStatus Status { get; private set; } = new();
    public string ProbeLog { get; private set; } = "(not probed yet)";

    /// <summary>Keyboard lighting, once a device has been found. Null when unavailable.</summary>
    public LightingEngine? Lighting { get; private set; }

    /// <summary>Raised when lighting becomes available, so the UI can bind its preview.</summary>
    public event Action<LightingEngine>? LightingAttached;
    public RazerLaptopDevice? Device => _device;

    public ControlLoop(AppConfig config, SensorService sensors)
    {
        _config = config;
        _sensors = sensors;
    }

    // ---------------------------------------------------------------- lifecycle

    public void Start()
    {
        lock (_lock)
        {
            if (_thread is { IsAlive: true }) return;

            if (File.Exists(ConfigStore.ManualModeMarkerPath))
            {
                Log.Warn("Previous run left the fans in manual mode — restoring automatic control first.");
                _manualEngaged = true; // so RestoreAuto actually sends the command once connected
            }

            _lastGoodSensorAt = _clock.Elapsed.TotalSeconds;
            _lastTickAt = _clock.Elapsed.TotalSeconds;

            _cts = new CancellationTokenSource();
            _thread = new Thread(() => Run(_cts.Token))
            {
                IsBackground = true,
                Name = "BladeFanCurve control loop",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            Log.Info("Control loop started.");
        }
    }

    /// <summary>Stops the loop and guarantees the fans go back to automatic control.</summary>
    public void StopAndRestoreAuto(string reason)
    {
        Thread? thread;
        lock (_lock)
        {
            _cts?.Cancel();
            _wake.Set();
            thread = _thread;
            _thread = null;
        }

        try { thread?.Join(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }

        // Stop streaming frames before the device goes away, and leave the keyboard on
        // a controller-side effect so the last rendered frame does not freeze there.
        try { Lighting?.RestoreOnExit(); } catch { /* never block the fan restore */ }

        RestoreAutoImmediate(reason);

        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Synchronous, best-effort return to automatic fans. Safe to call from a process
    /// exit handler, an unhandled exception handler or a session-ending handler.
    /// </summary>
    public void RestoreAutoImmediate(string reason)
    {
        try
        {
            var device = _device;
            if (device is { IsConnected: true })
            {
                var ok = device.RestoreAutomaticFans(EffectivePerfMode(_config));
                Log.Info($"Automatic fan control restored ({reason}){(ok ? "" : " — device did not acknowledge")}.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore automatic fans", ex);
        }
        finally
        {
            _manualEngaged = false;
            ClearMarker();
            _cpuFan.Reset(0);
            _gpuFan.Reset(0);
        }
    }

    /// <summary>Called after resume from sleep: the EC forgets manual mode.</summary>
    public void OnResume()
    {
        lock (_lock)
        {
            Log.Info("System resumed — re-establishing device link.");
            _manualEngaged = false;
            _lastAssertAt = double.NegativeInfinity;
            _lastGoodSensorAt = _clock.Elapsed.TotalSeconds;
            _consecutiveWriteFailures = 0;
            _cpuFan.Reset(0);
            _gpuFan.Reset(0);
            if (_device != null && !_device.Reconnect())
            {
                _device.Dispose();
                _device = null;
            }
        }

        // The EC forgets the charge threshold across suspend, so it has to be re-sent
        // rather than assumed to have survived.
        if (_config.Battery.ReapplyChargeLimit && _config.Battery.ChargeLimitEnabled
                                               && Power is { SupportsChargeLimit: true })
        {
            try { ApplyChargeLimit(_config); }
            catch (Exception ex) { Log.Warn($"Could not re-apply the charge limit: {ex.Message}"); }
        }

        _wake.Set();
    }

    public void UpdateConfig(AppConfig config)
    {
        bool profileChanged;
        lock (_lock)
        {
            profileChanged = _lastAppliedProfile != config.ActiveProfile;
            _config = config;
            if (_device != null) _device.CommandDelayMs = config.Device.CommandDelayMs;
            // Force the next cycle to re-send, since curves or limits may have moved.
            _cpuFan.MarkSent(-1);
            _gpuFan.MarkSent(-1);
        }

        // A profile is more than its curves, so switching one also moves the
        // performance mode, the Windows power plan and the refresh rate.
        if (profileChanged && _device is { IsConnected: true })
        {
            try { ApplyProfilePower(config); }
            catch (Exception ex) { Log.Warn($"Could not apply profile power settings: {ex.Message}"); }
        }

        _wake.Set();
    }

    public void SetManualOverride(int? rpm)
    {
        lock (_lock) _overrideRpm = rpm;
        Log.Info(rpm is { } r ? $"Manual override set to {r} RPM." : "Manual override cleared.");
        _wake.Set();
    }

    public int? ManualOverride { get { lock (_lock) return _overrideRpm; } }

    // ---------------------------------------------------------------- main loop

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            AppConfig cfg;
            int? overrideRpm;
            lock (_lock)
            {
                cfg = _config;
                overrideRpm = _overrideRpm;
            }

            try
            {
                Tick(cfg, overrideRpm);
            }
            catch (Exception ex)
            {
                Log.Error("Control loop iteration failed", ex);
                RestoreAutoImmediate("loop error");
                Publish(cfg, ControlMode.Failsafe, SensorSnapshot.Empty, $"Loop error: {ex.Message}");
            }

            try
            {
                _wake.Wait(cfg.Tuning.PollIntervalMs, token);
                _wake.Reset();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.Info("Control loop stopped.");
    }

    private void Tick(AppConfig cfg, int? overrideRpm)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastTickAt, 0.05, 10.0);
        _lastTickAt = now;
        _cycle++;

        var snapshot = _sensors.Poll();

        // A diagnostic is driving the fans directly; stay out of its way.
        if (_diagnosticsBusy)
        {
            Publish(cfg, Status.Mode, snapshot, "Diagnostics running — fan writes paused.");
            return;
        }

        // ---- disabled ------------------------------------------------------
        if (!cfg.Enabled)
        {
            if (_manualEngaged) RestoreAutoImmediate("control disabled");
            Publish(cfg, ControlMode.Disabled, snapshot, "Fan control is off — the laptop is managing its own fans.");
            return;
        }

        // ---- device --------------------------------------------------------
        if (_device is not { IsConnected: true })
        {
            if (now - _lastDiscoveryAt < DiscoveryBackoffSeconds)
            {
                Publish(cfg, ControlMode.Searching, snapshot, "Looking for the Razer control interface…");
                return;
            }

            _lastDiscoveryAt = now;
            var device = RazerLaptopDevice.Discover(out var probeLog);
            ProbeLog = probeLog;

            if (device == null)
            {
                Publish(cfg, ControlMode.Searching, snapshot,
                    "No Razer laptop control interface found. Close Razer Synapse and make sure this app runs as Administrator.");
                return;
            }

            device.CommandDelayMs = cfg.Device.CommandDelayMs;

            // Start from the layout a recognised model is known to use, then fall back
            // to the configured value. Either way it is verified against the hardware.
            var known = KnownModels.Find(device.ProductId);
            device.SetRpmArg0 = known?.SetRpmArg0 ?? (byte)(cfg.Device.SetRpmArg0 == 0 ? 0 : 1);

            _device = device;
            _manualEngaged = false;
            _rpmVariantVerified = false;
            _consecutiveWriteFailures = 0;
            _lastAssertAt = double.NegativeInfinity;
            Log.Info($"Connected to {KnownModels.Describe(device.ProductId)} " +
                     $"on transaction id 0x{device.TransactionId:X2}, set-rpm arg[0] 0x{device.SetRpmArg0:X2}.");

            // If a previous run crashed while in manual mode, undo that before doing anything else.
            if (File.Exists(ConfigStore.ManualModeMarkerPath))
            {
                device.RestoreAutomaticFans(EffectivePerfMode(cfg));
                ClearMarker();
                Log.Info("Cleared stale manual-mode state from a previous run.");
            }

            AttachPower(device, cfg);
            AttachLighting(device, cfg);
        }

        // ---- battery policy -------------------------------------------------
        if (cfg.Safety.RevertToAutoOnBattery && OnBattery())
        {
            if (_manualEngaged) RestoreAutoImmediate("running on battery");
            Publish(cfg, ControlMode.Disabled, snapshot, "On battery — fans handed back to the laptop.");
            return;
        }

        // ---- sensor health --------------------------------------------------
        var cpuTemp = snapshot.CpuTemp ?? snapshot.Hottest;
        if (cpuTemp is > 0) _lastGoodSensorAt = now;

        if (now - _lastGoodSensorAt > cfg.Safety.SensorStaleSeconds)
        {
            if (_manualEngaged)
            {
                RestoreAutoImmediate("no temperature readings");
                Log.Warn("Temperature readings stopped arriving — fans handed back to the laptop.");
            }

            Publish(cfg, ControlMode.Failsafe, snapshot,
                "No temperature readings. The fans are back under the laptop's own control.");
            return;
        }

        if (cpuTemp is not > 0)
        {
            // Transient miss inside the stale window: hold the previous command.
            Publish(cfg, _manualEngaged ? ControlMode.Manual : ControlMode.Searching, snapshot,
                "Waiting for a temperature reading…");
            return;
        }

        // A powered-down discrete GPU reports nothing; let the GPU fan follow the CPU
        // rather than idling at the curve floor.
        var gpuTemp = snapshot.HasGpu ? snapshot.GpuTemp!.Value : cpuTemp.Value;

        // ---- critical override ----------------------------------------------
        var criticalNow = cpuTemp.Value >= cfg.Safety.CpuCriticalC ||
                          (snapshot.HasGpu && gpuTemp >= cfg.Safety.GpuCriticalC);

        if (criticalNow)
        {
            if (_criticalUntil < now)
                Log.Warn($"Critical temperature reached (CPU {cpuTemp:0.0}°C, GPU {gpuTemp:0.0}°C) — fans to maximum.");
            _criticalUntil = now + cfg.Safety.CriticalHoldSeconds;
        }

        var releaseCpu = cfg.Safety.CpuCriticalC - cfg.Safety.CriticalReleaseMarginC;
        var releaseGpu = cfg.Safety.GpuCriticalC - cfg.Safety.CriticalReleaseMarginC;
        var stillHot = cpuTemp.Value > releaseCpu || (snapshot.HasGpu && gpuTemp > releaseGpu);
        var critical = criticalNow || (now < _criticalUntil && stillHot);

        // ---- engage / re-assert manual mode ----------------------------------
        var reassertDue = now - _lastAssertAt >= cfg.Tuning.ReassertSeconds;
        if (!_manualEngaged || reassertDue)
        {
            if (_device!.EnableManualFans(EffectivePerfMode(cfg)))
            {
                if (!_manualEngaged) Log.Info("Manual fan control engaged.");
                _manualEngaged = true;
                _lastAssertAt = now;
                WriteMarker();
                _consecutiveWriteFailures = 0;
            }
            else
            {
                Log.Warn($"Device refused the manual-mode command ({_consecutiveWriteFailures + 1}).");
                if (HandleWriteFailures(cfg, snapshot)) return;
            }
        }

        // ---- verify the set-rpm argument layout once --------------------------
        if (_manualEngaged && !_rpmVariantVerified && _verifyPendingRpm > 0)
        {
            if (_device!.TryGetFanRpmSetpoint(FanZone.Cpu, out var readBack))
            {
                if (Math.Abs(readBack - _verifyPendingRpm) <= 100)
                {
                    _rpmVariantVerified = true;
                    Log.Info($"Set-point verified: asked {_verifyPendingRpm} RPM, device reports {readBack} RPM.");
                }
                else
                {
                    var flipped = _device.SwitchSetRpmVariant();
                    Log.Warn($"Set-point mismatch (asked {_verifyPendingRpm}, got {readBack}) — " +
                             $"switched set-rpm argument layout to 0x{flipped:X2}.");
                    _rpmVariantVerified = true; // only flip once
                    _cpuFan.MarkSent(-1);
                    _gpuFan.MarkSent(-1);
                }
            }
            _verifyPendingRpm = -1;
        }

        // ---- compute targets --------------------------------------------------
        var profile = cfg.GetActiveProfile();

        var floor = cfg.Safety.MinRpm;
        if (cfg.Tuning.SharedFloor)
        {
            var a = FanCurveEvaluator.Evaluate(profile.CpuFan, cpuTemp.Value, cfg.Safety.MinRpm, cfg.Safety.MaxRpm);
            var b = FanCurveEvaluator.Evaluate(profile.GpuFan, gpuTemp, cfg.Safety.MinRpm, cfg.Safety.MaxRpm);
            floor = Math.Max(a, b);
        }

        int cpuRpm, gpuRpm;
        ControlMode mode;

        if (overrideRpm is { } fixedRpm && !critical)
        {
            var flat = FanCurveEvaluator.Quantise(fixedRpm, cfg.Safety.MinRpm, cfg.Safety.MaxRpm);
            cpuRpm = _cpuFan.Compute(cpuTemp.Value, FanCurveConfig.Flat(flat), cfg.Safety, cfg.Tuning, dt, flat, false);
            gpuRpm = _gpuFan.Compute(gpuTemp, FanCurveConfig.Flat(flat), cfg.Safety, cfg.Tuning, dt, flat, false);
            mode = ControlMode.Override;
        }
        else
        {
            cpuRpm = _cpuFan.Compute(cpuTemp.Value, profile.CpuFan, cfg.Safety, cfg.Tuning, dt, floor, critical);
            gpuRpm = _gpuFan.Compute(gpuTemp, profile.GpuFan, cfg.Safety, cfg.Tuning, dt, floor, critical);
            mode = critical ? ControlMode.Critical : ControlMode.Manual;
        }

        // ---- push to the device ------------------------------------------------
        var wrote = true;
        wrote &= SendIfNeeded(_cpuFan, cpuRpm, cfg, reassertDue);
        wrote &= SendIfNeeded(_gpuFan, gpuRpm, cfg, reassertDue);

        if (wrote) _consecutiveWriteFailures = 0;
        else if (HandleWriteFailures(cfg, snapshot)) return;

        if (!_rpmVariantVerified && _verifyPendingRpm < 0 && _cpuFan.LastSentRpm > 0)
            _verifyPendingRpm = _cpuFan.LastSentRpm;

        // ---- tachometer for display ---------------------------------------------
        if (_cycle % TachoEveryNCycles == 0)
        {
            if (_device!.TryGetFanTachometer(FanZone.Cpu, out var cpuMeasured)) _cpuFan.MeasuredRpm = cpuMeasured;
            if (_device.TryGetFanTachometer(FanZone.Gpu, out var gpuMeasured)) _gpuFan.MeasuredRpm = gpuMeasured;
        }

        Publish(cfg, mode, snapshot, critical ? "Critical temperature — fans at maximum." : null);
    }

    private bool SendIfNeeded(FanChannel channel, int rpm, AppConfig cfg, bool force)
    {
        if (!force && !channel.ShouldSend(cfg.Tuning.RpmDeadband)) return true;

        if (_device!.SetFanRpm(channel.Zone, rpm))
        {
            channel.MarkSent(rpm);
            Log.Debug($"{channel.Name} -> {rpm} RPM");
            return true;
        }

        Log.Warn($"{channel.Name}: device rejected {rpm} RPM.");
        return false;
    }

    /// <summary>Returns true when the caller should abandon this tick.</summary>
    private bool HandleWriteFailures(AppConfig cfg, SensorSnapshot snapshot)
    {
        _consecutiveWriteFailures++;
        if (_consecutiveWriteFailures < MaxConsecutiveWriteFailures) return false;

        Log.Warn("Too many failed device writes — reconnecting.");
        RestoreAutoImmediate("device stopped responding");

        var device = _device;
        _device = null;
        try
        {
            if (device != null && device.Reconnect())
            {
                _device = device;
                _consecutiveWriteFailures = 0;
                Log.Info("Reconnected to the device.");
            }
            else
            {
                device?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reconnect failed", ex);
        }

        Publish(cfg, ControlMode.Failsafe, snapshot,
            "Lost contact with the fan controller — the laptop is managing its own fans.");
        return true;
    }

    // ------------------------------------------------------------------- power

    /// <summary>Optional power and battery commands, once probed. Null when unavailable.</summary>
    public RazerPower? Power { get; private set; }

    /// <summary>What the read-only feature probe found, for the diagnostics report.</summary>
    public string PowerProbeLog { get; private set; } = "(not probed yet)";

    private string _lastAppliedProfile = "";

    /// <summary>
    /// Works out which optional power features this firmware has. The probe is
    /// read-only: no boost level or charge threshold is written until a matching read
    /// has proven the command exists.
    /// </summary>
    private void AttachPower(RazerLaptopDevice device, AppConfig cfg)
    {
        try
        {
            var power = new RazerPower(device);
            PowerProbeLog = power.Probe();
            Power = power;
            Log.Info(PowerProbeLog.TrimEnd());

            ApplyProfilePower(cfg);
            if (cfg.Battery.ChargeLimitEnabled) ApplyChargeLimit(cfg);
        }
        catch (Exception ex)
        {
            Log.Warn($"Power features unavailable: {ex.Message}");
            Power = null;
        }
    }

    /// <summary>
    /// The performance mode to run: the active profile's choice if it has one,
    /// otherwise the global device setting. This is Razer's real TDP lever — Balanced
    /// targets 35 W on the CPU, Gaming 55 W.
    /// </summary>
    private static PerfMode EffectivePerfMode(AppConfig cfg)
    {
        var wanted = cfg.GetActiveProfile().Power?.PerfMode;
        if (!string.IsNullOrWhiteSpace(wanted) && Enum.TryParse<PerfMode>(wanted, true, out var mode))
            return mode;

        return cfg.Device.PerfMode;
    }

    /// <summary>
    /// Applies everything a profile controls apart from the fan curves: performance
    /// mode, CPU/GPU boost, the Windows power scheme and overlay, and the refresh rate.
    /// Each step is independent — one failing does not stop the others — and each is
    /// skipped when the profile leaves it unset.
    /// </summary>
    public string ApplyProfilePower(AppConfig cfg)
    {
        var profile = cfg.GetActiveProfile();
        var power = profile.Power ?? new ProfilePower();
        var sb = new StringBuilder();
        _lastAppliedProfile = profile.Name;

        // ---- Razer performance mode -------------------------------------
        var device = _device;
        if (device is { IsConnected: true } && !string.IsNullOrWhiteSpace(power.PerfMode))
        {
            var mode = EffectivePerfMode(cfg);
            var ok = true;
            foreach (var zone in new[] { FanZone.Cpu, FanZone.Gpu })
                ok &= device.SetPerfMode(zone, mode, _manualEngaged);

            sb.AppendLine(ok
                ? $"performance mode: {mode}"
                : $"performance mode: {mode} — device did not acknowledge");
        }

        // ---- CPU / GPU boost --------------------------------------------
        var razerPower = Power;
        if (razerPower is { SupportsBoost: true })
        {
            if (Enum.TryParse<BoostLevel>(power.CpuBoost, true, out var cpuBoost))
                sb.AppendLine(razerPower.SetBoost(BoostTarget.Cpu, cpuBoost)
                    ? $"cpu boost: {cpuBoost}"
                    : $"cpu boost: {cpuBoost} — rejected or did not stick");

            if (Enum.TryParse<BoostLevel>(power.GpuBoost, true, out var gpuBoost))
                sb.AppendLine(razerPower.SetBoost(BoostTarget.Gpu, gpuBoost)
                    ? $"gpu boost: {gpuBoost}"
                    : $"gpu boost: {gpuBoost} — rejected or did not stick");
        }
        else if (!string.IsNullOrWhiteSpace(power.CpuBoost))
        {
            sb.AppendLine("cpu/gpu boost: not exposed by this firmware, skipped");
        }

        // ---- Windows power scheme and overlay ---------------------------
        if (!string.IsNullOrWhiteSpace(power.WindowsPlan) && Guid.TryParse(power.WindowsPlan, out var plan))
            sb.AppendLine(WindowsPowerPlan.SetActiveScheme(plan)
                ? "windows power plan set"
                : "windows power plan: could not be set");

        if (!string.IsNullOrWhiteSpace(power.PowerOverlay))
        {
            var overlay = power.PowerOverlay.ToLowerInvariant() switch
            {
                "efficiency" => WindowsPowerPlan.OverlayBestEfficiency,
                "performance" => WindowsPowerPlan.OverlayBestPerformance,
                _ => WindowsPowerPlan.OverlayNone,
            };
            sb.AppendLine(WindowsPowerPlan.SetOverlay(overlay)
                ? $"power mode: {WindowsPowerPlan.DescribeOverlay(overlay)}"
                : "power mode overlay: unavailable on this build");
        }

        // ---- refresh rate ------------------------------------------------
        if (power.RefreshHz > 0)
        {
            DisplayControl.SetRefreshRate(power.RefreshHz, out var message);
            sb.AppendLine($"display: {message}");
        }

        var report = sb.ToString().TrimEnd();
        if (report.Length > 0) Log.Info($"Profile '{profile.Name}' applied — {report.Replace("\r\n", "; ").Replace("\n", "; ")}");
        return report.Length > 0 ? report : "This profile does not change any power settings.";
    }

    /// <summary>
    /// Pushes the configured charge limit to the EC. The controller forgets it across
    /// a suspend, so this is called again on resume rather than only at startup.
    /// </summary>
    public string ApplyChargeLimit(AppConfig cfg)
    {
        var power = Power;
        if (power is not { SupportsChargeLimit: true })
            return "This firmware does not expose a charge limit.";

        var ok = power.SetChargeLimit(cfg.Battery.ChargeLimitEnabled, cfg.Battery.ChargeLimitPercent);
        var text = ok
            ? cfg.Battery.ChargeLimitEnabled
                ? $"Charging stops at {cfg.Battery.ChargeLimitPercent}%."
                : "Charge limit off — charging to 100%."
            : "The controller did not accept the charge limit.";

        Log.Info(text);
        return text;
    }

    // ---------------------------------------------------------------- lighting

    /// <summary>
    /// Keyboard lighting rides on the same HID interface as fan control, so it is set
    /// up once a device is found and torn down with it. A failure here is never fatal:
    /// lighting is a nicety, fan control is not.
    /// </summary>
    private void AttachLighting(RazerLaptopDevice device, AppConfig cfg)
    {
        try
        {
            Lighting?.Dispose();
            var engine = new LightingEngine(device);

            if (!engine.Initialise())
            {
                Lighting = null;
                return;
            }

            engine.Apply(cfg.Lighting);
            Lighting = engine;
            LightingAttached?.Invoke(engine);
        }
        catch (Exception ex)
        {
            Log.Warn($"Lighting unavailable: {ex.Message}");
            Lighting = null;
        }
    }

    /// <summary>Re-sends the current lighting configuration, e.g. after the user changes it.</summary>
    public void ApplyLighting(AppConfig cfg)
    {
        try
        {
            Lighting?.Apply(cfg.Lighting);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not apply lighting: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool OnBattery()
    {
        try
        {
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                   System.Windows.Forms.PowerLineStatus.Offline;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMarker()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Directory);
            if (!File.Exists(ConfigStore.ManualModeMarkerPath))
                File.WriteAllText(ConfigStore.ManualModeMarkerPath,
                    $"manual since {DateTime.Now:O} pid {Environment.ProcessId}");
        }
        catch { /* not fatal */ }
    }

    private static void ClearMarker()
    {
        try
        {
            if (File.Exists(ConfigStore.ManualModeMarkerPath))
                File.Delete(ConfigStore.ManualModeMarkerPath);
        }
        catch { /* not fatal */ }
    }

    private void Publish(AppConfig cfg, ControlMode mode, SensorSnapshot snapshot, string? message)
    {
        var status = new ControlStatus
        {
            Mode = mode,
            DeviceConnected = _device is { IsConnected: true },
            DeviceName = _device?.ProductName ?? "—",
            DeviceProductId = _device?.ProductId ?? 0,
            TransactionId = _device?.TransactionId ?? 0,
            CpuTempC = snapshot.HasCpu ? snapshot.CpuTemp : null,
            GpuTempC = snapshot.HasGpu ? snapshot.GpuTemp : null,
            CpuLoad = snapshot.CpuLoad,
            GpuLoad = snapshot.GpuLoad,
            CpuFanTargetRpm = _cpuFan.CommandedRpm,
            GpuFanTargetRpm = _gpuFan.CommandedRpm,
            CpuFanMeasuredRpm = _cpuFan.MeasuredRpm,
            GpuFanMeasuredRpm = _gpuFan.MeasuredRpm,
            SensorsHealthy = snapshot.HasCpu,
            CpuSource = snapshot.CpuSource,
            SensorNote = snapshot.CpuSource switch
            {
                Sensors.TempSource.AcpiThermalZone =>
                    "CPU reading is an ACPI thermal zone — the ring-0 driver for package temperature is unavailable.",
                Sensors.TempSource.BorrowedFromGpu =>
                    "No CPU sensor at all — the CPU fan is following the GPU temperature.",
                Sensors.TempSource.None when cfg.Enabled =>
                    "No temperature source available.",
                _ => null,
            },
            Message = message,
        };

        Status = status;

        // Temperature-driven lighting effects read from here rather than opening their
        // own sensor session.
        Lighting?.UpdateTelemetry(status.CpuTempC, status.GpuTempC,
            status.CpuFanTargetRpm, status.GpuFanTargetRpm,
            cfg.Safety.MinRpm, cfg.Safety.MaxRpm);

        try { StatusUpdated?.Invoke(status); } catch { /* UI handler must not break the loop */ }
    }

    /// <summary>
    /// Asks the EC for an impossible RPM on each zone and reads back what it accepted,
    /// which reveals the real hardware ceiling. The fans go loud for a couple of
    /// seconds. Normal control resumes as soon as it returns.
    /// </summary>
    public string ProbeMaximumRpm()
    {
        var device = _device;
        if (device is not { IsConnected: true }) return "Not connected — nothing to probe.";

        var cfg = _config;
        var sb = new StringBuilder();
        _diagnosticsBusy = true;

        try
        {
            if (!device.EnableManualFans(EffectivePerfMode(cfg)))
                return "The device would not accept manual fan mode, so the ceiling cannot be probed.";

            WriteMarker();
            Log.Info("Probing the maximum fan RPM — the fans will be loud for a moment.");

            foreach (var zone in new[] { FanZone.Cpu, FanZone.Gpu })
            {
                sb.AppendLine(device.TryProbeMaximumRpm(zone, out var max)
                    ? $"{zone} fan ceiling : {max} RPM"
                    : $"{zone} fan ceiling : no reply");
            }

            sb.AppendLine();
            sb.AppendLine("Set \"Maximum RPM\" on the Settings tab to the lower of these two,");
            sb.AppendLine("so the top of each curve corresponds to a speed the fan can reach.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Probe failed: {ex.Message}";
        }
        finally
        {
            _diagnosticsBusy = false;

            // Do not leave the fans pinned at maximum on the way out.
            if (!cfg.Enabled) RestoreAutoImmediate("maximum-rpm probe finished");
            else
            {
                _cpuFan.Reset(0);
                _gpuFan.Reset(0);
                _lastAssertAt = double.NegativeInfinity;
            }

            _wake.Set();
        }
    }

    /// <summary>One-shot hardware report for the diagnostics pane.</summary>
    public string RunSelfTest()
    {
        var sb = new StringBuilder();
        var device = _device;

        sb.AppendLine("=== Device ===");
        if (device is not { IsConnected: true })
        {
            sb.AppendLine("Not connected.");
            sb.AppendLine();
            sb.AppendLine("=== Probe log ===");
            sb.AppendLine(ProbeLog);
            return sb.ToString();
        }

        sb.AppendLine($"Model           : {KnownModels.Describe(device.ProductId)}");
        sb.AppendLine($"USB name        : {device.ProductName}");
        sb.AppendLine($"Granted access  : {device.GrantedAccess}");
        sb.AppendLine($"Transaction id  : 0x{device.TransactionId:X2}");
        sb.AppendLine($"Set-rpm arg[0]  : 0x{device.SetRpmArg0:X2}");
        sb.AppendLine($"Command delay   : {device.CommandDelayMs} ms");
        sb.AppendLine();

        sb.AppendLine("=== Live state ===");
        foreach (var zone in new[] { FanZone.Cpu, FanZone.Gpu })
        {
            sb.AppendLine($"[{zone}]");
            sb.AppendLine(device.TryGetPerfMode(zone, out var state)
                ? $"  perf mode     : {state.Mode}, manual fan = {state.ManualFan}"
                : "  perf mode     : no reply");
            sb.AppendLine(device.TryGetFanRpmSetpoint(zone, out var sp)
                ? $"  rpm set point : {sp}"
                : "  rpm set point : no reply");
            sb.AppendLine(device.TryGetFanTachometer(zone, out var tach)
                ? $"  measured rpm  : {tach}"
                : "  measured rpm  : not supported by this firmware");
        }

        sb.AppendLine();
        sb.AppendLine("=== Optional power features ===");
        sb.Append(PowerProbeLog);

        sb.AppendLine();
        sb.AppendLine("=== Windows power ===");
        sb.Append(WindowsPowerPlan.Describe());

        sb.AppendLine();
        sb.AppendLine("=== Display ===");
        sb.Append(DisplayControl.Describe());

        sb.AppendLine();
        sb.AppendLine("=== Sensors ===");
        sb.AppendLine(_sensors.DescribeSources());

        sb.AppendLine();
        sb.AppendLine("=== Probe log ===");
        sb.AppendLine(ProbeLog);
        return sb.ToString();
    }

    public void Dispose()
    {
        StopAndRestoreAuto("shutting down");
        Lighting?.Dispose();
        Lighting = null;
        _device?.Dispose();
        _device = null;
        _wake.Dispose();
    }
}
