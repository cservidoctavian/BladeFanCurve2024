using System.Diagnostics;
using System.Management;

namespace BladeFanCurve.Sensors;

public sealed record ThermalZoneReading(string Name, double Celsius);

/// <summary>
/// Reads ACPI thermal zones through WMI.
///
/// This exists because the usual way to read an AMD CPU's temperature — SMN
/// registers via a ring-0 driver — needs a kernel driver that this app does not
/// ship, and that Windows 11's Memory Integrity blocks anyway. ACPI thermal zones
/// are exposed by the firmware, need no driver, and are plenty accurate enough to
/// drive a fan curve.
///
/// Two providers are tried because laptops vary in which one the firmware fills in:
///   root\WMI    MSAcpi_ThermalZoneTemperature              (tenths of a Kelvin)
///   root\CIMV2  Win32_PerfFormattedData_..._ThermalZoneInformation (Kelvin)
/// </summary>
public sealed class AcpiThermalProvider : IDisposable
{
    private const double KelvinOffset = 273.15;

    /// <summary>WMI round trips cost tens of milliseconds, so readings are cached.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMilliseconds(1500);

    private readonly object _lock = new();
    private ManagementObjectSearcher? _acpiSearcher;
    private ManagementObjectSearcher? _perfSearcher;

    private IReadOnlyList<ThermalZoneReading> _cached = Array.Empty<ThermalZoneReading>();
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public bool Available { get; private set; }
    public string Status { get; private set; } = "not initialised";

    public void Open()
    {
        lock (_lock)
        {
            try
            {
                _acpiSearcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\WMI"),
                    new ObjectQuery("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[acpi] root\\WMI unavailable: {ex.Message}");
                _acpiSearcher = null;
            }

            try
            {
                _perfSearcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\CIMV2"),
                    new ObjectQuery("SELECT Name, HighPrecisionTemperature, Temperature " +
                                    "FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[acpi] root\\CIMV2 unavailable: {ex.Message}");
                _perfSearcher = null;
            }

            var zones = ReadZones(force: true);
            Available = zones.Count > 0;
            Status = Available
                ? $"{zones.Count} ACPI thermal zone(s): " +
                  string.Join(", ", zones.Select(z => $"{z.Name} {z.Celsius:0.0}°C"))
                : "no ACPI thermal zones reported any usable value";
        }
    }

    /// <summary>Hottest plausible zone, which is the one that tracks the CPU on a laptop.</summary>
    public double? ReadHottestCelsius()
    {
        var zones = ReadZones(force: false);
        if (zones.Count == 0) return null;
        return zones.Max(z => z.Celsius);
    }

    public IReadOnlyList<ThermalZoneReading> ReadZones(bool force)
    {
        lock (_lock)
        {
            if (!force && DateTime.UtcNow - _cachedAtUtc < CacheLifetime) return _cached;

            var zones = new List<ThermalZoneReading>();
            CollectAcpi(zones);
            if (zones.Count == 0) CollectPerf(zones);

            _cached = zones;
            _cachedAtUtc = DateTime.UtcNow;
            return zones;
        }
    }

    private void CollectAcpi(List<ThermalZoneReading> zones)
    {
        if (_acpiSearcher == null) return;

        try
        {
            using var results = _acpiSearcher.Get();
            foreach (var o in results)
            {
                using var mo = (ManagementObject)o;
                var raw = mo["CurrentTemperature"];
                if (raw == null) continue;

                // Tenths of a Kelvin.
                var celsius = Convert.ToDouble(raw) / 10.0 - KelvinOffset;
                if (!Plausible(celsius)) continue;

                zones.Add(new ThermalZoneReading(ShortenZoneName(mo["InstanceName"] as string), celsius));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[acpi] MSAcpi query failed: {ex.Message}");
        }
    }

    private void CollectPerf(List<ThermalZoneReading> zones)
    {
        if (_perfSearcher == null) return;

        try
        {
            using var results = _perfSearcher.Get();
            foreach (var o in results)
            {
                using var mo = (ManagementObject)o;

                double? celsius = null;

                // HighPrecisionTemperature is in tenths of a Kelvin; Temperature is whole Kelvin.
                if (mo["HighPrecisionTemperature"] is { } hp)
                {
                    var v = Convert.ToDouble(hp) / 10.0 - KelvinOffset;
                    if (Plausible(v)) celsius = v;
                }

                if (celsius is null && mo["Temperature"] is { } t)
                {
                    var v = Convert.ToDouble(t) - KelvinOffset;
                    if (Plausible(v)) celsius = v;
                }

                if (celsius is { } c)
                    zones.Add(new ThermalZoneReading(ShortenZoneName(mo["Name"] as string), c));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[acpi] perf counter query failed: {ex.Message}");
        }
    }

    /// <summary>Rejects the placeholder values firmware uses for "no reading".</summary>
    private static bool Plausible(double celsius) => celsius is > 5 and < 125;

    private static string ShortenZoneName(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return "zone";

        // "ACPI\ThermalZone\TZ01_0" -> "TZ01_0"
        var parts = instanceName.Split('\\', '/');
        return parts[^1];
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _acpiSearcher?.Dispose();
            _perfSearcher?.Dispose();
            _acpiSearcher = null;
            _perfSearcher = null;
        }
    }
}
