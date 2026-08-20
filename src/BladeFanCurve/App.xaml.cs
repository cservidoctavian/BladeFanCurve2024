using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using BladeFanCurve.Config;
using BladeFanCurve.Control;
using BladeFanCurve.Sensors;
using BladeFanCurve.UI;
using Microsoft.Win32;

namespace BladeFanCurve;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\BladeFanCurve.SingleInstance";

    private Mutex? _singleInstance;
    private AppConfig _config = null!;
    private SensorService _sensors = null!;
    private ControlLoop _loop = null!;
    private TrayManager _tray = null!;
    private MainWindow? _window;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless diagnostic: works even when discovery fails or another copy is
        // already running, so there is always a way to produce a report.
        if (e.Args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            RunHeadlessDiagnostic();
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Blade Fan Curve is already running — look for it in the notification area.",
                "Blade Fan Curve", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Info("──────────────────────────────────────────────");
        Log.Info($"Blade Fan Curve starting (elevated: {IsElevated()}).");

        if (!IsElevated())
        {
            MessageBox.Show(
                "Blade Fan Curve needs to run as Administrator.\n\n" +
                "The temperature sensors and the Razer control interface are both " +
                "only reachable from an elevated process.",
                "Blade Fan Curve", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // The tray icon runs on a WinForms message pump, so exceptions raised inside a
        // tray callback bypass WPF's DispatcherUnhandledException entirely and reach the
        // default WinForms crash dialog. Route them somewhere we control instead.
        try
        {
            System.Windows.Forms.Application.SetUnhandledExceptionMode(
                System.Windows.Forms.UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += OnWinFormsThreadException;
        }
        catch (InvalidOperationException)
        {
            // Already set because a WinForms window exists; not fatal.
        }

        // Every path out of the process has to put the fans back under Razer's control.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyRestore("process exit");
        SystemEvents.SessionEnding += (_, args) => EmergencyRestore($"session ending ({args.Reason})");
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SessionEnding += (_, _) => EmergencyRestore("windows session ending");

        _config = ConfigStore.Load();
        _sensors = new SensorService
        {
            PinnedCpuSensorId = _config.CpuSensorId,
            PinnedGpuSensorId = _config.GpuSensorId,
        };
        _loop = new ControlLoop(_config, _sensors);

        _tray = new TrayManager(_config, _loop);
        _tray.ShowWindowRequested += ShowMainWindow;
        _tray.ExitRequested += () => ShutdownCleanly("quit from the tray");
        _tray.ConfigChangedExternally += () => _window?.Dispatcher.BeginInvoke(() => _window?.SyncFromConfig());

        var startHidden = _config.StartMinimized ||
                          e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        // Opening the sensor library loads a kernel driver, which takes a moment.
        // Do it off the UI thread so the tray icon appears immediately.
        Task.Run(() =>
        {
            try
            {
                _sensors.Open();
                Log.Info("Hardware monitor opened.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not open the hardware monitor", ex);
                Dispatcher.BeginInvoke(() => _tray.ShowBalloon("Blade Fan Curve",
                    "Temperature sensors could not be opened. Fan control stays off.",
                    System.Windows.Forms.ToolTipIcon.Error));
            }

            _loop.Start();
        });

        if (!startHidden) ShowMainWindow();
        else _tray.ShowBalloon("Blade Fan Curve", "Running in the notification area.");
    }

    /// <summary>
    /// Always posted rather than run inline. A tray click already arrives on the UI
    /// thread, so calling this directly would build and show the window inside the
    /// WinForms callback frame, where a failure escapes WPF's exception handling.
    /// </summary>
    private void ShowMainWindow() => Dispatcher.BeginInvoke(ShowMainWindowCore);

    private void ShowMainWindowCore()
    {
        if (_window == null)
        {
            _window = new MainWindow(_config, _loop, _sensors);
            _window.Closed += (_, _) => _window = null;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Log.Info("System suspending — restoring automatic fans.");
                _loop.RestoreAutoImmediate("system suspending");
                break;
            case PowerModes.Resume:
                _loop.OnResume();
                break;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true; // the control loop keeps running; the fans stay managed
        MessageBox.Show($"Something went wrong in the interface:\n\n{e.Exception.Message}\n\n" +
                        "Fan control is still running. See the diagnostics tab for details.",
            "Blade Fan Curve", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnWinFormsThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the tray message pump", e.Exception);
        MessageBox.Show($"Something went wrong in the interface:\n\n{e.Exception.Message}\n\n" +
                        "Fan control is still running. See the diagnostics tab for details.",
            "Blade Fan Curve", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error($"Fatal error: {e.ExceptionObject}");
        EmergencyRestore("unhandled exception");
    }

    /// <summary>Last-ditch synchronous restore. Must not throw.</summary>
    private void EmergencyRestore(string reason)
    {
        try { _loop?.RestoreAutoImmediate(reason); }
        catch { /* nothing left to do */ }
    }

    private void ShutdownCleanly(string reason)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        Log.Info($"Shutting down ({reason}).");

        try { _window?.CloseForReal(); } catch { /* ignore */ }
        try { _loop.StopAndRestoreAuto(reason); } catch (Exception ex) { Log.Error("Shutdown", ex); }
        try { _sensors.Dispose(); } catch { /* ignore */ }
        try { _tray.Dispose(); } catch { /* ignore */ }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        EmergencyRestore("application exit");
        try { _loop?.Dispose(); } catch { /* ignore */ }
        try { _singleInstance?.ReleaseMutex(); } catch { /* ignore */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void RunHeadlessDiagnostic()
    {
        try
        {
            var device = Hardware.RazerLaptopDevice.Discover(out var probeLog);
            var selfTest = device == null
                ? "No device, so no self-test was run."
                : $"Connected to {Hardware.KnownModels.Describe(device.ProductId)} " +
                  $"on txn 0x{device.TransactionId:X2} (access: {device.GrantedAccess}).";
            device?.Dispose();

            var report = DiagnosticReport.Build(probeLog, selfTest);
            var path = DiagnosticReport.Save(report);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Diagnostic failed:\n\n{ex}", "Blade Fan Curve",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
