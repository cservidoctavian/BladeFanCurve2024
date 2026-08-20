using System.Drawing;
using System.Windows.Forms;
using BladeFanCurve.Config;
using BladeFanCurve.Control;

namespace BladeFanCurve.UI;

/// <summary>
/// Tray icon: live readout in the tooltip, quick profile switching, and the only
/// place the app can actually be quit from.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _profilesItem;

    private readonly AppConfig _config;
    private readonly ControlLoop _loop;

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;
    public event Action? ConfigChangedExternally;

    public TrayManager(AppConfig config, ControlLoop loop)
    {
        _config = config;
        _loop = loop;

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Fan control on") { CheckOnClick = true, Checked = config.Enabled };
        _enabledItem.Click += (_, _) =>
        {
            _config.Enabled = _enabledItem.Checked;
            Log.Info(_config.Enabled ? "Fan control switched on (tray)." : "Fan control switched off (tray).");
            ConfigStore.Save(_config);
            _loop.UpdateConfig(_config);
            ConfigChangedExternally?.Invoke();
        };

        _profilesItem = new ToolStripMenuItem("Profile");

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_profilesItem);
        menu.Items.Add(new ToolStripSeparator());

        var openItem = new ToolStripMenuItem("Open Blade Fan Curve");
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => ShowWindowRequested?.Invoke();
        menu.Items.Add(openItem);

        var restoreItem = new ToolStripMenuItem("Hand fans back to Razer");
        restoreItem.Click += (_, _) =>
        {
            _config.Enabled = false;
            _enabledItem.Checked = false;
            ConfigStore.Save(_config);
            _loop.UpdateConfig(_config);
            _loop.RestoreAutoImmediate("requested from the tray");
            ConfigChangedExternally?.Invoke();
        };
        menu.Items.Add(restoreItem);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Quit (fans return to Razer)");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Blade Fan Curve",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();

        RebuildProfiles();
        _loop.StatusUpdated += OnStatusUpdated;
    }

    public void RebuildProfiles()
    {
        _profilesItem.DropDownItems.Clear();
        foreach (var profile in _config.Profiles)
        {
            var name = profile.Name;
            var item = new ToolStripMenuItem(name) { Checked = name == _config.ActiveProfile };
            item.Click += (_, _) =>
            {
                _config.ActiveProfile = name;
                ConfigStore.Save(_config);
                _loop.UpdateConfig(_config);
                RebuildProfiles();
                ConfigChangedExternally?.Invoke();
            };
            _profilesItem.DropDownItems.Add(item);
        }
    }

    public void SyncFromConfig()
    {
        _enabledItem.Checked = _config.Enabled;
        RebuildProfiles();
    }

    private void OnStatusUpdated(ControlStatus status)
    {
        var mode = status.Mode switch
        {
            ControlMode.Manual => "curve",
            ControlMode.Override => "manual",
            ControlMode.Critical => "CRITICAL",
            ControlMode.Failsafe => "failsafe",
            ControlMode.Searching => "searching",
            _ => "off",
        };

        var line = $"{status.ShortSummary}  •  {mode}";
        _statusItem.Text = line;

        // The tray tooltip is limited to 63 characters.
        var tooltip = $"Blade Fan Curve — {line}";
        _icon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = icon;
            _icon.ShowBalloonTip(6000);
        }
        catch { /* the shell can refuse balloons */ }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted != null) return extracted;
            }
        }
        catch { /* fall through */ }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _loop.StatusUpdated -= OnStatusUpdated;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
