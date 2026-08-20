using BladeFanCurve.Config;
using BladeFanCurve.Control;
using System.Threading;

namespace BladeFanCurve.Platform;

/// <summary>
/// A scheduled blue-light filter driven through the display LUT.
///
/// This deliberately does not try to drive Windows' own Night Light. That feature is
/// controlled by an undocumented binary blob under CloudStore in the registry whose
/// format changes between builds; poking it is fragile and breaks silently. Driving
/// the gamma ramp directly is a stable, documented API and gives finer control over
/// the warmth, at the cost of not moving the Windows toggle.
/// </summary>
public sealed class NightLightService : IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private DisplaySettings _settings = new();
    private bool _applied;

    /// <summary>Raised when the filter turns on or off, so the UI can reflect it.</summary>
    public event Action<bool>? StateChanged;

    public bool IsActive { get; private set; }

    public void Start()
    {
        // Half a minute is plenty: the schedule has minute resolution and the check is
        // a couple of comparisons.
        _timer = new Timer(_ => Evaluate(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public void Update(DisplaySettings settings)
    {
        lock (_gate) _settings = settings;
        Evaluate();
    }

    private void Evaluate()
    {
        DisplaySettings settings;
        lock (_gate) settings = _settings;

        try
        {
            var shouldBeOn = settings.NightLightEnabled && IsWithinSchedule(DateTime.Now.TimeOfDay, settings);

            if (shouldBeOn)
            {
                // Reapplied every tick on purpose: anything else that touches the LUT
                // (a game exiting, a driver reset, a resume) would otherwise leave the
                // filter silently switched off.
                DisplayControl.ApplyColourTemperature(settings.NightLightKelvin);
                if (!_applied) Announce(true);
                _applied = true;
            }
            else if (_applied)
            {
                DisplayControl.ResetColour();
                _applied = false;
                Announce(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Blue-light filter: {ex.Message}");
        }
    }

    /// <summary>Handles schedules that cross midnight, which is the normal case.</summary>
    public static bool IsWithinSchedule(TimeSpan now, DisplaySettings s)
    {
        var start = TimeSpan.FromMinutes(Math.Clamp(s.NightLightStartMinutes, 0, 1439));
        var end = TimeSpan.FromMinutes(Math.Clamp(s.NightLightEndMinutes, 0, 1439));

        if (start == end) return false;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    private void Announce(bool on)
    {
        Log.Info(on ? "Blue-light filter on." : "Blue-light filter off.");
        try { StateChanged?.Invoke(on); } catch { /* UI handler must not break the timer */ }
    }

    /// <summary>Always restores a neutral LUT — leaving the screen tinted after exit would be rude.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;

        if (!_applied) return;

        try
        {
            DisplayControl.ResetColour();
            _applied = false;
        }
        catch
        {
            // Nothing useful to do at shutdown.
        }
    }
}
