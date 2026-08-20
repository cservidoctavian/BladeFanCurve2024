using BladeFanCurve.Sensors;

namespace BladeFanCurve.Control;

public enum ControlMode
{
    /// <summary>Control loop is off; the laptop manages its own fans.</summary>
    Disabled,

    /// <summary>Searching for the Razer control interface.</summary>
    Searching,

    /// <summary>Curves are driving the fans.</summary>
    Manual,

    /// <summary>Manual RPM set by hand from the UI.</summary>
    Override,

    /// <summary>Critical temperature reached; fans forced to maximum.</summary>
    Critical,

    /// <summary>Something went wrong; fans handed back to the laptop deliberately.</summary>
    Failsafe,
}

public sealed record ControlStatus
{
    public ControlMode Mode { get; init; } = ControlMode.Disabled;
    public bool DeviceConnected { get; init; }
    public string DeviceName { get; init; } = "—";
    public int DeviceProductId { get; init; }
    public byte TransactionId { get; init; }

    public double? CpuTempC { get; init; }
    public double? GpuTempC { get; init; }
    public double? CpuLoad { get; init; }
    public double? GpuLoad { get; init; }

    public int CpuFanTargetRpm { get; init; }
    public int GpuFanTargetRpm { get; init; }
    public int CpuFanMeasuredRpm { get; init; }
    public int GpuFanMeasuredRpm { get; init; }

    public bool SensorsHealthy { get; init; }

    /// <summary>Which layer supplied the CPU temperature this tick.</summary>
    public TempSource CpuSource { get; init; } = TempSource.None;

    /// <summary>Set when the CPU reading is degraded, so the UI can say so.</summary>
    public string? SensorNote { get; init; }
    public string? Message { get; init; }
    public DateTime UpdatedLocal { get; init; } = DateTime.Now;

    public string ShortSummary
    {
        get
        {
            var cpu = CpuTempC is { } c ? $"{c:0}°C" : "—";
            var gpu = GpuTempC is { } g ? $"{g:0}°C" : "—";
            return $"CPU {cpu} / GPU {gpu}  •  {CpuFanTargetRpm} / {GpuFanTargetRpm} RPM";
        }
    }
}
