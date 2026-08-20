using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BladeFanCurve.Config;
using BladeFanCurve.Control;
using BladeFanCurve.Sensors;

namespace BladeFanCurve;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly ControlLoop _loop;
    private readonly SensorService _sensors;

    private bool _loading = true;
    private bool _reallyClose;

    /// <summary>
    /// Dragging a curve point raises CurveChanged on every mouse move. Writing the
    /// config file and waking the control loop that often would hammer both the disk
    /// and the USB link, so saves are coalesced.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _saveDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(350)
    };

    private static readonly Brush DotIdle = Frozen("#6E7784");
    private static readonly Brush DotGood = Frozen("#5FD69C");
    private static readonly Brush DotWarn = Frozen("#E7B44C");
    private static readonly Brush DotBad = Frozen("#F0645C");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public MainWindow(AppConfig config, ControlLoop loop, SensorService sensors)
    {
        _config = config;
        _loop = loop;
        _sensors = sensors;

        InitializeComponent();

        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? "" : $"v{version.Major}.{version.Minor}";

        RebuildProfileSegments();
        LoadSettings();
        RefreshSensorLists();
        RefreshStartupState();

        foreach (var entry in Log.Recent()) LogList.Items.Add(entry.ToString());
        ScrollLogToEnd();

        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            PersistNow();
        };

        Log.EntryWritten += OnLogEntry;
        _loop.StatusUpdated += OnStatusUpdated;

        _loading = false;
        OnStatusUpdated(_loop.Status);
    }

    // ------------------------------------------------------------ window chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximise_Click(sender, e);
            return;
        }

        try { DragMove(); } catch { /* released mid-drag */ }
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------ status

    private void OnStatusUpdated(ControlStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStatusUpdated(status));
            return;
        }

        StatusDot.Fill = status.Mode switch
        {
            ControlMode.Manual or ControlMode.Override => DotGood,
            ControlMode.Critical => DotBad,
            ControlMode.Failsafe or ControlMode.Searching => DotWarn,
            _ => DotIdle,
        };

        StatusText.Text = status.Mode switch
        {
            ControlMode.Manual => "Curves are driving the fans",
            ControlMode.Override => "Manual override active",
            ControlMode.Critical => "Critical temperature — fans at maximum",
            ControlMode.Failsafe => "Failsafe — fans handed back to the laptop",
            ControlMode.Searching => "Looking for the Razer controller…",
            _ => "Fan control is off",
        };

        DeviceText.Text = status.DeviceConnected
            ? $"{status.DeviceName}  |  1532:{status.DeviceProductId:X4}  |  txn 0x{status.TransactionId:X2}"
            : status.Message ?? "No device";

        UpdateTile(status.CpuTempC, status.CpuLoad, status.CpuFanTargetRpm, status.CpuFanMeasuredRpm,
            CpuTempText, CpuLoadText, CpuRpmText, CpuMeasText, CpuBarFill, CpuBarRest);

        UpdateTile(status.GpuTempC, status.GpuLoad, status.GpuFanTargetRpm, status.GpuFanMeasuredRpm,
            GpuTempText, GpuLoadText, GpuRpmText, GpuMeasText, GpuBarFill, GpuBarRest);

        CpuCurveEditor.CurrentTemp = status.CpuTempC;
        CpuCurveEditor.CurrentRpm = status.CpuFanTargetRpm;
        GpuCurveEditor.CurrentTemp = status.GpuTempC;
        GpuCurveEditor.CurrentRpm = status.GpuFanTargetRpm;

        CpuCurveReadout.Text = status.CpuTempC is { } ct
            ? $"{ct:0}°C → {status.CpuFanTargetRpm} rpm"
            : "waiting for a reading";
        GpuCurveReadout.Text = status.GpuTempC is { } gt
            ? $"{gt:0}°C → {status.GpuFanTargetRpm} rpm"
            : "dGPU idle — following the CPU";

        if (string.IsNullOrEmpty(status.SensorNote))
        {
            SensorNoteBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            SensorNoteBar.Visibility = Visibility.Visible;
            SensorNoteText.Text = status.SensorNote;
        }
    }

    /// <summary>Fills one of the two stat tiles.</summary>
    private void UpdateTile(double? tempC, double? load, int targetRpm, int measuredRpm,
        TextBlock tempText, TextBlock loadText, TextBlock rpmText, TextBlock measText,
        ColumnDefinition fill, ColumnDefinition rest)
    {
        tempText.Text = tempC is { } t ? $"{t:0}" : "—";
        loadText.Text = load is { } l ? $"{l:0}% load" : "—";
        rpmText.Text = targetRpm > 0 ? $"{targetRpm} rpm" : "— rpm";
        measText.Text = measuredRpm > 0 ? $"meas {measuredRpm}" : "";

        // The bar spans 30 °C to 100 °C, which is the useful range on this hardware.
        var fraction = tempC is { } v ? Math.Clamp((v - 30.0) / 70.0, 0, 1) : 0;
        fill.Width = new GridLength(fraction, GridUnitType.Star);
        rest.Width = new GridLength(1 - fraction, GridUnitType.Star);
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogEntry(entry));
            return;
        }

        LogList.Items.Add(entry.ToString());
        while (LogList.Items.Count > 500) LogList.Items.RemoveAt(0);
        ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    // ------------------------------------------------------------ profiles

    /// <summary>Rebuilds the segmented profile picker from the config.</summary>
    private void RebuildProfileSegments()
    {
        _loading = true;
        ProfileSegments.Children.Clear();

        foreach (var profile in _config.Profiles)
        {
            var name = profile.Name;
            var item = new RadioButton
            {
                Content = name,
                GroupName = "profiles",
                Style = (Style)FindResource("SegmentItem"),
                IsChecked = name == _config.ActiveProfile,
            };

            item.Checked += (_, _) =>
            {
                if (_loading) return;
                _config.ActiveProfile = name;
                BindCurves();
                CpuCurveEditor.InvalidateVisual();
                GpuCurveEditor.InvalidateVisual();
                PersistNow();
            };

            ProfileSegments.Children.Add(item);
        }

        _loading = false;
        BindCurves();
    }

    private void BindCurves()
    {
        var profile = _config.GetActiveProfile();

        CpuCurveEditor.MinRpm = _config.Safety.MinRpm;
        CpuCurveEditor.MaxRpm = _config.Safety.MaxRpm;
        GpuCurveEditor.MinRpm = _config.Safety.MinRpm;
        GpuCurveEditor.MaxRpm = _config.Safety.MaxRpm;

        CpuCurveEditor.Curve = profile.CpuFan;
        GpuCurveEditor.Curve = profile.GpuFan;

        OverrideSlider.Minimum = _config.Safety.MinRpm;
        OverrideSlider.Maximum = _config.Safety.MaxRpm;

        EnabledToggle.IsChecked = _config.Enabled;
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        var copy = _config.GetActiveProfile().Clone();
        copy.Name = UniqueName(copy.Name + " copy");
        _config.Profiles.Add(copy);
        _config.ActiveProfile = copy.Name;
        RebuildProfileSegments();
        PersistNow();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_config.Profiles.Count <= 1)
        {
            MessageBox.Show(this, "At least one profile has to stay.", "Blade Fan Curve",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var active = _config.GetActiveProfile();
        if (MessageBox.Show(this, $"Delete the profile \"{active.Name}\"?", "Blade Fan Curve",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _config.Profiles.Remove(active);
        _config.ActiveProfile = _config.Profiles[0].Name;
        RebuildProfileSegments();
        PersistNow();
    }

    private void ResetCurves_Click(object sender, RoutedEventArgs e)
    {
        var profile = _config.GetActiveProfile();
        profile.CpuFan = FanCurveConfig.DefaultCpu();
        profile.GpuFan = FanCurveConfig.DefaultGpu();
        BindCurves();
        CpuCurveEditor.InvalidateVisual();
        GpuCurveEditor.InvalidateVisual();
        PersistNow();
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        var n = 2;
        while (_config.Profiles.Any(p => p.Name == name)) name = $"{baseName} {n++}";
        return name;
    }

    private void CurveEditor_CurveChanged(object? sender, EventArgs e) => Persist();

    // ------------------------------------------------------------ toggles

    private void EnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.Enabled = EnabledToggle.IsChecked == true;
        Log.Info(_config.Enabled ? "Fan control switched on." : "Fan control switched off.");
        PersistNow();
    }

    private void RestoreAuto_Click(object sender, RoutedEventArgs e)
    {
        EnabledToggle.IsChecked = false;
        _config.Enabled = false;
        PersistNow();
        _loop.RestoreAutoImmediate("requested from the UI");
    }

    private void Override_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = OverrideToggle.IsChecked == true;
        OverrideSlider.IsEnabled = on;
        _loop.SetManualOverride(on ? (int)OverrideSlider.Value : null);
    }

    private void OverrideSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverrideValueText == null) return;
        OverrideValueText.Text = $"{(int)e.NewValue} rpm";
        if (!_loading && OverrideToggle.IsChecked == true) _loop.SetManualOverride((int)e.NewValue);
    }

    // ------------------------------------------------------------ settings

    private void LoadSettings()
    {
        _loading = true;

        MinRpmBox.Text = _config.Safety.MinRpm.ToString(CultureInfo.InvariantCulture);
        MaxRpmBox.Text = _config.Safety.MaxRpm.ToString(CultureInfo.InvariantCulture);
        CpuCritBox.Text = _config.Safety.CpuCriticalC.ToString("0.#", CultureInfo.InvariantCulture);
        GpuCritBox.Text = _config.Safety.GpuCriticalC.ToString("0.#", CultureInfo.InvariantCulture);
        StaleBox.Text = _config.Safety.SensorStaleSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        BatteryToggle.IsChecked = _config.Safety.RevertToAutoOnBattery;

        PollBox.Text = _config.Tuning.PollIntervalMs.ToString(CultureInfo.InvariantCulture);
        FallRateBox.Text = _config.Tuning.TempFallRateCPerSec.ToString("0.##", CultureInfo.InvariantCulture);
        RampUpBox.Text = _config.Tuning.RampUpRpmPerSec.ToString(CultureInfo.InvariantCulture);
        RampDownBox.Text = _config.Tuning.RampDownRpmPerSec.ToString(CultureInfo.InvariantCulture);
        SharedFloorToggle.IsChecked = _config.Tuning.SharedFloor;

        CmdDelayBox.Text = _config.Device.CommandDelayMs.ToString(CultureInfo.InvariantCulture);
        StartMinimisedToggle.IsChecked = _config.StartMinimized;

        SensorSourceText.Text = _sensors.SourceExplanation;

        _loading = false;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _config.Safety.MinRpm = ParseInt(MinRpmBox.Text, _config.Safety.MinRpm);
        _config.Safety.MaxRpm = ParseInt(MaxRpmBox.Text, _config.Safety.MaxRpm);
        _config.Safety.CpuCriticalC = ParseDouble(CpuCritBox.Text, _config.Safety.CpuCriticalC);
        _config.Safety.GpuCriticalC = ParseDouble(GpuCritBox.Text, _config.Safety.GpuCriticalC);
        _config.Safety.SensorStaleSeconds = ParseDouble(StaleBox.Text, _config.Safety.SensorStaleSeconds);
        _config.Safety.RevertToAutoOnBattery = BatteryToggle.IsChecked == true;

        _config.Tuning.PollIntervalMs = ParseInt(PollBox.Text, _config.Tuning.PollIntervalMs);
        _config.Tuning.TempFallRateCPerSec = ParseDouble(FallRateBox.Text, _config.Tuning.TempFallRateCPerSec);
        _config.Tuning.RampUpRpmPerSec = ParseInt(RampUpBox.Text, _config.Tuning.RampUpRpmPerSec);
        _config.Tuning.RampDownRpmPerSec = ParseInt(RampDownBox.Text, _config.Tuning.RampDownRpmPerSec);
        _config.Tuning.SharedFloor = SharedFloorToggle.IsChecked == true;

        _config.Device.CommandDelayMs = ParseInt(CmdDelayBox.Text, _config.Device.CommandDelayMs);
        _config.StartMinimized = StartMinimisedToggle.IsChecked == true;

        _config.CpuSensorId = (CpuSensorCombo.SelectedItem as SensorDescriptor)?.Id;
        _config.GpuSensorId = (GpuSensorCombo.SelectedItem as SensorDescriptor)?.Id;
        _sensors.PinnedCpuSensorId = _config.CpuSensorId;
        _sensors.PinnedGpuSensorId = _config.GpuSensorId;

        PersistNow();
        LoadSettings();
        BindCurves();
        CpuCurveEditor.InvalidateVisual();
        GpuCurveEditor.InvalidateVisual();

        SettingsStatusText.Text = $"Saved at {DateTime.Now:HH:mm:ss}.";
    }

    private void RefreshSensors_Click(object sender, RoutedEventArgs e)
    {
        RefreshSensorLists();
        SensorSourceText.Text = _sensors.SourceExplanation;
    }

    private void RefreshSensorLists()
    {
        var sensors = _sensors.EnumerateTemperatureSensors();

        FillCombo(CpuSensorCombo, sensors, _config.CpuSensorId);
        FillCombo(GpuSensorCombo, sensors, _config.GpuSensorId);

        static void FillCombo(ComboBox combo, IReadOnlyList<SensorDescriptor> items, string? selectedId)
        {
            combo.Items.Clear();
            combo.Items.Add("(automatic)");
            foreach (var s in items) combo.Items.Add(s);

            combo.SelectedIndex = 0;
            if (string.IsNullOrEmpty(selectedId)) return;

            foreach (var item in combo.Items)
                if (item is SensorDescriptor d && d.Id == selectedId)
                {
                    combo.SelectedItem = item;
                    return;
                }
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenPath(ConfigStore.Directory);

    private void OpenLog_Click(object sender, RoutedEventArgs e) =>
        OpenPath(File.Exists(ConfigStore.LogPath) ? ConfigStore.LogPath : ConfigStore.Directory);

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {path}", ex); }
    }

    // ------------------------------------------------------------ startup task

    private void RefreshStartupState()
    {
        var installed = StartupTask.IsInstalled();
        StartupStateText.Text = installed
            ? "Registered as an elevated logon task — no UAC prompt at startup."
            : "Not registered. Blade Fan Curve will not start on its own.";
        InstallStartupButton.IsEnabled = !installed;
        RemoveStartupButton.IsEnabled = installed;
    }

    private void InstallStartup_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = StartupTask.Install();
        SettingsStatusText.Text = message;
        Log.Info($"Startup task install: {message}");
        RefreshStartupState();
        if (!ok) MessageBox.Show(this, message, "Blade Fan Curve", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RemoveStartup_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = StartupTask.Remove();
        SettingsStatusText.Text = message;
        Log.Info($"Startup task remove: {message}");
        RefreshStartupState();
        if (!ok) MessageBox.Show(this, message, "Blade Fan Curve", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ------------------------------------------------------------ diagnostics

    private void SelfTest_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsBox.Text = "Running…";
        var loop = _loop;
        Task.Run(() =>
        {
            string text;
            try { text = loop.RunSelfTest(); }
            catch (Exception ex) { text = $"Self-test failed: {ex}"; }
            Dispatcher.BeginInvoke(() => DiagnosticsBox.Text = text);
        });
    }

    private void ProbeMaxRpm_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Both fans will run at full speed for a few seconds while the controller " +
                "is asked what its ceiling is.\n\nCarry on?",
                "Find max fan RPM", MessageBoxButton.OKCancel, MessageBoxImage.Information)
            != MessageBoxResult.OK) return;

        DiagnosticsBox.Text = "Probing…";
        var loop = _loop;
        Task.Run(() =>
        {
            string text;
            try { text = loop.ProbeMaximumRpm(); }
            catch (Exception ex) { text = $"Probe failed: {ex}"; }
            Dispatcher.BeginInvoke(() => DiagnosticsBox.Text = text);
        });
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsBox.Text = "Collecting…";
        var loop = _loop;
        Task.Run(() =>
        {
            string text, path;
            try
            {
                text = DiagnosticReport.Build(loop.ProbeLog, loop.RunSelfTest());
                path = DiagnosticReport.Save(text);
            }
            catch (Exception ex)
            {
                text = $"Report failed: {ex}";
                path = "";
            }

            Dispatcher.BeginInvoke(() =>
            {
                DiagnosticsBox.Text = text;
                if (string.IsNullOrEmpty(path)) return;

                SettingsStatusText.Text = $"Report saved to {path}";
                Log.Info($"Diagnostic report written to {path}");
                OpenPath(path);
            });
        });
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DiagnosticsBox.Text); }
        catch (Exception ex) { Log.Error("Clipboard copy failed", ex); }
    }

    // ------------------------------------------------------------ window

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            // Closing the window keeps the control loop alive in the tray, otherwise
            // the fans would silently revert every time the window is dismissed.
            e.Cancel = true;
            if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); PersistNow(); }
            Hide();
            return;
        }

        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); PersistNow(); }
        Log.EntryWritten -= OnLogEntry;
        _loop.StatusUpdated -= OnStatusUpdated;
        base.OnClosing(e);
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    /// <summary>Re-reads config that was changed elsewhere (usually the tray menu).</summary>
    public void SyncFromConfig()
    {
        _loading = true;
        EnabledToggle.IsChecked = _config.Enabled;

        foreach (var child in ProfileSegments.Children)
            if (child is RadioButton rb)
                rb.IsChecked = (rb.Content as string) == _config.ActiveProfile;

        BindCurves();
        CpuCurveEditor.InvalidateVisual();
        GpuCurveEditor.InvalidateVisual();
        _loading = false;
    }

    /// <summary>Coalesced save — use this for anything that can fire rapidly.</summary>
    private void Persist()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void PersistNow()
    {
        ConfigStore.Save(_config);
        _loop.UpdateConfig(_config);
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
