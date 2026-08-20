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
using BladeFanCurve.Hardware;
using BladeFanCurve.Lighting;
using BladeFanCurve.Platform;
using BladeFanCurve.UI;
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

        BuildEffectList();
        LoadLightingSettings();
        _loop.LightingAttached += OnLightingAttached;
        if (_loop.Lighting != null) OnLightingAttached(_loop.Lighting);

        LoadPowerTab();

        _previewTimer.Tick += (_, _) => TickPreview();
        _previewTimer.Start();

        _loading = false;
        OnStatusUpdated(_loop.Status);
    }

    // ------------------------------------------------------------------- power

    private readonly NightLightService _nightLight = new();

    /// <summary>An empty tag means "this profile does not touch that setting".</summary>
    private static void FillCombo(ComboBox box, IEnumerable<(string Tag, string Label)> items, string selected)
    {
        box.Items.Clear();
        foreach (var (tag, label) in items)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == selected)
            {
                box.SelectedItem = item;
                return;
            }

        box.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox box) =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "";

    private void LoadPowerTab()
    {
        var rates = DisplayControl.AvailableRefreshRates();

        var plans = new List<(string, string)> { ("", "Leave unchanged") };
        try
        {
            plans.AddRange(WindowsPowerPlan.Enumerate().Select(p => (p.Id.ToString(), p.Name)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enumerate power plans: {ex.Message}");
        }

        _powerPlans = plans;
        _refreshRates = rates;

        // Display card
        RefreshRateBox.Items.Clear();
        foreach (var hz in rates) RefreshRateBox.Items.Add(new ComboBoxItem { Content = $"{hz} Hz", Tag = hz });

        var current = DisplayControl.GetCurrentMode();
        DisplayModeText.Text = current == null
            ? "Could not read the display mode."
            : $"Currently {current}. Changing this affects the built-in panel.";

        foreach (ComboBoxItem item in RefreshRateBox.Items)
            if ((int)item.Tag == current?.RefreshHz)
                RefreshRateBox.SelectedItem = item;

        // Colour profiles
        IccProfileBox.Items.Clear();
        var profiles = DisplayControl.InstalledColourProfiles();
        IccProfileBox.Items.Add(new ComboBoxItem { Content = "System default", Tag = "" });
        foreach (var p in profiles) IccProfileBox.Items.Add(new ComboBoxItem { Content = p, Tag = p });
        IccProfileBox.SelectedIndex = 0;
        IccStatusText.Text = profiles.Count == 0
            ? "No ICC profiles are installed on this machine."
            : $"{profiles.Count} profile(s) installed.";

        // Blue light
        NightLightToggle.IsChecked = _config.Display.NightLightEnabled;
        NightStartBox.Text = MinutesToText(_config.Display.NightLightStartMinutes);
        NightEndBox.Text = MinutesToText(_config.Display.NightLightEndMinutes);
        KelvinSlider.Value = _config.Display.NightLightKelvin;
        UpdateKelvinText();

        // Battery
        ChargeLimitToggle.IsChecked = _config.Battery.ChargeLimitEnabled;
        Charge60.IsChecked = _config.Battery.ChargeLimitPercent <= 60;
        Charge80.IsChecked = _config.Battery.ChargeLimitPercent is > 60 and <= 80;
        Charge100.IsChecked = _config.Battery.ChargeLimitPercent > 80;

        LoadProfilePower();
        RefreshPowerSupportText();

        _nightLight.StateChanged += on => Dispatcher.BeginInvoke(() =>
            NightLightStatus.Text = on ? "Filter is on now." : "Filter is off right now.");
        _nightLight.Update(_config.Display);
        _nightLight.Start();
    }


    /// <summary>Safe to call before the power tab has been built, e.g. during construction.</summary>
    private void TryLoadProfilePower()
    {
        if (PerfModeBox == null) return;
        try { LoadProfilePower(); } catch { /* the tab is not built yet */ }
    }

    private List<(string Tag, string Label)> _powerPlans = new();
    private IReadOnlyList<int> _refreshRates = Array.Empty<int>();

    private void LoadProfilePower()
    {
        var profile = _config.GetActiveProfile();
        var power = profile.Power ??= new ProfilePower();

        ProfilePowerTitle.Text = $"P R O F I L E  ·  {profile.Name.ToUpperInvariant()}";

        FillCombo(PerfModeBox, new[]
        {
            ("", "Leave unchanged"),
            ("Balanced", "Balanced  ·  35 W CPU"),
            ("Gaming", "Gaming  ·  55 W CPU"),
            ("Creator", "Creator"),
            ("Custom", "Custom"),
        }, power.PerfMode);

        FillCombo(CpuBoostBox, new[]
        {
            ("", "Leave unchanged"), ("Low", "Low"), ("Medium", "Medium"),
            ("High", "High"), ("Boost", "Boost"),
        }, power.CpuBoost);

        FillCombo(GpuBoostBox, new[]
        {
            ("", "Leave unchanged"), ("Low", "Low"), ("Medium", "Medium"), ("High", "High"),
        }, power.GpuBoost);

        FillCombo(PowerPlanBox, _powerPlans, power.WindowsPlan);

        FillCombo(PowerOverlayBox, new[]
        {
            ("", "Leave unchanged"),
            ("efficiency", "Best power efficiency"),
            ("recommended", "Recommended"),
            ("performance", "Best performance"),
        }, power.PowerOverlay);

        var rateOptions = new List<(string, string)> { ("0", "Leave unchanged") };
        rateOptions.AddRange(_refreshRates.Select(hz => (hz.ToString(), $"{hz} Hz")));
        FillCombo(ProfileRefreshBox, rateOptions, power.RefreshHz.ToString());
    }

    private void RefreshPowerSupportText()
    {
        var power = _loop.Power;

        BoostSupportText.Text = power switch
        {
            null => "Waiting for the controller.",
            { SupportsBoost: true } => "Boost levels are supported. They only take effect in Custom mode.",
            _ => "This firmware does not expose CPU/GPU boost, so those two are ignored. "
               + "Performance mode still changes the power target.",
        };

        var supportsLimit = power is { SupportsChargeLimit: true };
        ChargeLimitToggle.IsEnabled = supportsLimit;
        ChargeLimitRow.IsEnabled = supportsLimit;

        if (!supportsLimit)
            BatteryStatusText.Text = power == null
                ? "Waiting for the controller."
                : "This firmware does not expose a charge limit, so this cannot be set from here. "
                + "Razer Synapse may still be able to.";
        else if (string.IsNullOrEmpty(BatteryStatusText.Text))
            BatteryStatusText.Text = "Supported by this firmware.";
    }

    private static string MinutesToText(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, 0, 1439)).ToString(@"hh\:mm");

    private static int TextToMinutes(string? text, int fallback)
    {
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
            return (int)parsed.TotalMinutes;

        return fallback;
    }

    private void UpdateKelvinText() => KelvinText.Text = $"{(int)KelvinSlider.Value} K";

    private void ProfilePower_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading) return;

        var power = _config.GetActiveProfile().Power ??= new ProfilePower();
        power.PerfMode = SelectedTag(PerfModeBox);
        power.CpuBoost = SelectedTag(CpuBoostBox);
        power.GpuBoost = SelectedTag(GpuBoostBox);
        power.WindowsPlan = SelectedTag(PowerPlanBox);
        power.PowerOverlay = SelectedTag(PowerOverlayBox);
        power.RefreshHz = int.TryParse(SelectedTag(ProfileRefreshBox), out var hz) ? hz : 0;

        Persist();
    }

    private void ApplyProfilePower_Click(object sender, RoutedEventArgs e)
    {
        ProfilePowerStatus.Text = "Applying…";
        Task.Run(() =>
        {
            var report = _loop.ApplyProfilePower(_config);
            Dispatcher.BeginInvoke(() =>
            {
                ProfilePowerStatus.Text = report;
                RefreshPowerSupportText();
                LoadPowerTabDisplayOnly();
            });
        });
    }

    /// <summary>Re-reads just the display state, since applying a profile may have changed it.</summary>
    private void LoadPowerTabDisplayOnly()
    {
        var current = DisplayControl.GetCurrentMode();
        if (current == null) return;

        DisplayModeText.Text = $"Currently {current}. Changing this affects the built-in panel.";
        foreach (ComboBoxItem item in RefreshRateBox.Items)
            if ((int)item.Tag == current.RefreshHz)
                RefreshRateBox.SelectedItem = item;
    }

    private void ApplyRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (RefreshRateBox.SelectedItem is not ComboBoxItem { Tag: int hz }) return;

        DisplayControl.SetRefreshRate(hz, out var message);
        DisplayModeText.Text = message;
        LoadPowerTabDisplayOnly();
    }

    private void Display_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loading) return;

        _config.Display.NightLightEnabled = NightLightToggle.IsChecked == true;
        _config.Display.NightLightStartMinutes = TextToMinutes(NightStartBox.Text, 21 * 60);
        _config.Display.NightLightEndMinutes = TextToMinutes(NightEndBox.Text, 7 * 60);
        _config.Display.NightLightKelvin = (int)KelvinSlider.Value;

        NightStartBox.Text = MinutesToText(_config.Display.NightLightStartMinutes);
        NightEndBox.Text = MinutesToText(_config.Display.NightLightEndMinutes);

        _nightLight.Update(_config.Display);
        Persist();
    }

    private void Kelvin_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateKelvinText();
        if (!_loading) Display_Changed(sender, new RoutedEventArgs());
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Display_Changed(sender, e);
        Keyboard.ClearFocus();
    }

    /// <summary>Shows the chosen warmth for a couple of seconds regardless of the schedule.</summary>
    private void PreviewWarmth_Click(object sender, RoutedEventArgs e)
    {
        DisplayControl.ApplyColourTemperature((int)KelvinSlider.Value);
        NightLightStatus.Text = "Previewing…";

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _nightLight.Update(_config.Display); // puts back whatever the schedule wants
            NightLightStatus.Text = "";
        };
        timer.Start();
    }

    private void ResetColour_Click(object sender, RoutedEventArgs e)
    {
        NightLightToggle.IsChecked = false;
        DisplayControl.ResetColour();
        NightLightStatus.Text = "Colour reset to neutral.";
    }

    private void Battery_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loading) return;

        _config.Battery.ChargeLimitEnabled = ChargeLimitToggle.IsChecked == true;
        _config.Battery.ChargeLimitPercent =
            Charge60.IsChecked == true ? 60 :
            Charge80.IsChecked == true ? 80 : 100;

        Persist();

        BatteryStatusText.Text = "Applying…";
        Task.Run(() =>
        {
            var report = _loop.ApplyChargeLimit(_config);
            Dispatcher.BeginInvoke(() => BatteryStatusText.Text = report);
        });
    }

    // ---------------------------------------------------------------- lighting

    private readonly System.Windows.Threading.DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };

    private readonly EffectContext _previewCtx = new();
    private SoftwareEffect? _previewEffect;
    private readonly RgbColor[,] _previewFrame = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];
    private DateTime _previewStarted = DateTime.UtcNow;
    private bool _liveFrames;

    private static readonly string[] Swatches =
    {
        "#00FF88", "#00E5FF", "#3355FF", "#B14CFF", "#FF3B7B", "#FF6A00", "#FFD400", "#FFFFFF",
    };

    private void BuildEffectList()
    {
        EffectList.Items.Clear();
        AddEffectHeader("O N   T H E   K E Y B O A R D");

        foreach (var fx in LightingEngine.HardwareEffects)
            EffectList.Items.Add(new ListBoxItem
            {
                Content = fx.Name,
                Tag = fx.Id,
                ToolTip = fx.Description,
                Padding = new Thickness(14, 7, 14, 7),
            });

        AddEffectHeader("R E N D E R E D   H E R E");

        foreach (var fx in EffectCatalog.All)
            EffectList.Items.Add(new ListBoxItem
            {
                Content = fx.UsesTelemetry ? fx.Name + "  ·" : fx.Name,
                Tag = fx.Id,
                ToolTip = fx.Description,
                Padding = new Thickness(14, 7, 14, 7),
            });

        BuildSwatches(PrimaryPresets, hex => { PrimaryHex.Text = hex; Lighting_Changed(this, null!); });
        BuildSwatches(SecondaryPresets, hex => { SecondaryHex.Text = hex; Lighting_Changed(this, null!); });
    }

    private void AddEffectHeader(string text) =>
        EffectList.Items.Add(new ListBoxItem
        {
            Content = text,
            IsEnabled = false,
            Focusable = false,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedDim"),
            Padding = new Thickness(14, 12, 14, 6),
        });

    private void BuildSwatches(Panel host, Action<string> onPick)
    {
        host.Children.Clear();
        foreach (var hex in Swatches)
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 5, 0),
                Cursor = Cursors.Hand,
                Background = Frozen(hex),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("CardBorder"),
                ToolTip = hex,
            };
            var captured = hex;
            swatch.MouseLeftButtonUp += (_, _) => onPick(captured);
            host.Children.Add(swatch);
        }
    }

    private void LoadLightingSettings()
    {
        var l = _config.Lighting;

        LightingEnabled.IsChecked = l.Enabled;
        PrimaryHex.Text = l.PrimaryColor;
        SecondaryHex.Text = l.SecondaryColor;
        BrightnessSlider.Value = l.Brightness;
        SpeedSlider.Value = l.Speed;
        FpsSlider.Value = l.SoftwareFps;
        WaveRight.IsChecked = l.WaveDirection != 2;
        WaveLeft.IsChecked = l.WaveDirection == 2;

        foreach (ListBoxItem item in EffectList.Items)
            if (item.Tag as string == l.Effect)
            {
                EffectList.SelectedItem = item;
                break;
            }

        UpdateLightingControls();
    }

    private void OnLightingAttached(LightingEngine engine)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LightingModeText.Text = $"chroma · {engine.Family.ToString().ToLowerInvariant()}";
            LightingStatusText.Text = "";
            engine.FrameRendered += OnLightingFrame;
        });
    }

    /// <summary>
    /// Arrives on the render thread. The preview shows exactly the frame the keyboard
    /// was sent, rather than a second guess at what the effect looks like.
    /// </summary>
    private void OnLightingFrame(RgbColor[,] frame)
    {
        _liveFrames = true;
        Dispatcher.BeginInvoke(() => Preview.SetFrame(frame));
    }

    /// <summary>
    /// Drives the preview for hardware effects, which produce no frames here because
    /// the keyboard controller renders them. The approximation is clearly a preview,
    /// not a claim about exact timing.
    /// </summary>
    private void TickPreview()
    {
        if (!IsVisible || _liveFrames || _previewEffect == null) return;

        _previewCtx.Time = (DateTime.UtcNow - _previewStarted).TotalSeconds * Math.Clamp(SpeedSlider.Value, 0.1, 4);
        _previewCtx.Delta = 0.05;
        _previewCtx.Primary = RgbColor.FromHex(PrimaryHex.Text);
        _previewCtx.Secondary = RgbColor.FromHex(SecondaryHex.Text);

        _previewEffect.Render(_previewFrame, _previewCtx);

        var brightness = Math.Clamp(BrightnessSlider.Value, 0, 255) / 255.0;
        var copy = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];
        for (var r = 0; r < RazerChroma.Rows; r++)
        for (var c = 0; c < RazerChroma.Columns; c++)
            copy[r, c] = _previewFrame[r, c].Scale(brightness);

        Preview.SetFrame(copy);
    }

    /// <summary>Closest software equivalent of a hardware effect, for the preview only.</summary>
    private static SoftwareEffect? PreviewFor(string id) => id switch
    {
        "hw-off" => null,
        "hw-static" => new SolidEffect(),
        "hw-breathe" or "hw-breathe-dual" or "hw-breathe-random" => new BreathingSoftEffect(),
        "hw-spectrum" => new ColourCycleEffect(),
        "hw-wave" => new RainbowWaveEffect(),
        "hw-reactive" => new SolidEffect(),
        "hw-starlight" or "hw-starlight-dual" or "hw-starlight-random" => new StarfieldEffect(),
        _ => null,
    };

    private void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EffectList.SelectedItem is not ListBoxItem { Tag: string id }) return;

        _config.Lighting.Effect = id;
        UpdateLightingControls();
        if (!_loading) ApplyLighting();
    }

    /// <summary>Hides the controls an effect does not use, so the panel never lies.</summary>
    private void UpdateLightingControls()
    {
        var id = _config.Lighting.Effect;
        var isHardware = id.StartsWith("hw-", StringComparison.OrdinalIgnoreCase);

        var hw = LightingEngine.HardwareEffects.FirstOrDefault(f => f.Id == id);
        var sw = EffectCatalog.Find(id);

        var usesPrimary = isHardware ? hw?.UsesPrimary == true : true;
        var usesSecondary = isHardware
            ? hw?.UsesSecondary == true
            : sw is GradientEffect or RippleEffect or StarfieldEffect or FanMeterEffect;

        PrimaryRow.Visibility = usesPrimary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryRow.Visibility = usesSecondary ? Visibility.Visible : Visibility.Collapsed;
        DirectionRow.Visibility = hw?.UsesDirection == true ? Visibility.Visible : Visibility.Collapsed;
        SpeedRow.Visibility = isHardware
            ? (hw?.UsesSpeed == true ? Visibility.Visible : Visibility.Collapsed)
            : Visibility.Visible;
        FpsRow.Visibility = isHardware ? Visibility.Collapsed : Visibility.Visible;

        EffectDescription.Text = isHardware
            ? (hw?.Description ?? "") + "  Runs on the keyboard controller and keeps going after this app closes."
            : (sw?.Description ?? "") + (sw?.UsesTelemetry == true
                ? "  Driven by live temperature and fan readings."
                : "  Rendered here and streamed to the keyboard.");

        // Hardware effects send no frames back, so drive the preview locally instead.
        _liveFrames = false;
        _previewEffect = isHardware ? PreviewFor(id) : sw;
        _previewStarted = DateTime.UtcNow;
        _previewEffect?.Reset(_previewCtx);
        if (_previewEffect == null) Preview.Clear();

        UpdateLightingReadouts();
    }

    private void UpdateLightingReadouts()
    {
        BrightnessText.Text = $"{(int)Math.Round(BrightnessSlider.Value / 255.0 * 100)}%";
        SpeedText.Text = $"{SpeedSlider.Value:0.0}×";
        FpsText.Text = $"{(int)FpsSlider.Value} fps";
        PrimarySwatch.Background = Frozen(RgbColor.FromHex(PrimaryHex.Text).ToHex());
        SecondarySwatch.Background = Frozen(RgbColor.FromHex(SecondaryHex.Text).ToHex());
    }

    private void LightingSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLightingReadouts();
        if (!_loading) ApplyLighting();
    }

    private void Lighting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        UpdateLightingReadouts();
        ApplyLighting();
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Lighting_Changed(sender, e);
        Keyboard.ClearFocus();
    }

    private void PickPrimary_Click(object sender, RoutedEventArgs e) => PickColour(PrimaryHex);
    private void PickSecondary_Click(object sender, RoutedEventArgs e) => PickColour(SecondaryHex);

    /// <summary>Uses the WinForms colour dialog — WPF has no built-in picker.</summary>
    private void PickColour(TextBox target)
    {
        var current = RgbColor.FromHex(target.Text);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        target.Text = new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B).ToHex();
        Lighting_Changed(this, null!);
    }

    private void ApplyLighting()
    {
        var l = _config.Lighting;
        l.Enabled = LightingEnabled.IsChecked == true;
        l.PrimaryColor = RgbColor.FromHex(PrimaryHex.Text).ToHex();
        l.SecondaryColor = RgbColor.FromHex(SecondaryHex.Text).ToHex();
        l.Brightness = (int)Math.Round(BrightnessSlider.Value);
        l.Speed = Math.Round(SpeedSlider.Value, 2);
        l.SoftwareFps = (int)Math.Round(FpsSlider.Value);
        l.WaveDirection = WaveLeft.IsChecked == true ? 2 : 1;

        if (_loop.Lighting == null)
        {
            LightingStatusText.Text =
                "No Chroma interface found yet. Lighting will be applied once the keyboard answers.";
            return;
        }

        _liveFrames = false;
        _loop.ApplyLighting(_config);
        Persist();
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
        if (IsLoaded || !_loading) TryLoadProfilePower();

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
