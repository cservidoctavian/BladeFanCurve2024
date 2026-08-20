namespace BladeFanCurve.Sensors;

public enum TempSource
{
    None,

    /// <summary>Real package temperature, needs the ring-0 driver.</summary>
    HardwareMonitor,

    /// <summary>ACPI thermal zone via WMI. Driver-free.</summary>
    AcpiThermalZone,

    /// <summary>No CPU reading at all, so the CPU fan is following the GPU.</summary>
    BorrowedFromGpu,
}

public sealed record SensorSnapshot(
    double? CpuTemp,
    double? GpuTemp,
    double? CpuLoad,
    double? GpuLoad,
    TempSource CpuSource,
    DateTime TimestampUtc,
    double? CpuWatts = null,
    double? GpuWatts = null)
{
    public bool HasCpu => CpuTemp is > 0 and < 130;
    public bool HasGpu => GpuTemp is > 0 and < 130;

    /// <summary>
    /// Package power is only ever plausible in this band. A laptop CPU that reports
    /// 400 W is reporting something else, so the reading is discarded rather than
    /// drawn.
    /// </summary>
    public static bool PlausibleWatts(double? w) => w is > 0.05 and < 400;

    public bool HasCpuWatts => PlausibleWatts(CpuWatts);
    public bool HasGpuWatts => PlausibleWatts(GpuWatts);

    /// <summary>Highest valid temperature, used for shared floors and the critical check.</summary>
    public double? Hottest
    {
        get
        {
            double? h = null;
            if (HasCpu) h = CpuTemp;
            if (HasGpu && (h is null || GpuTemp > h)) h = GpuTemp;
            return h;
        }
    }

    public static SensorSnapshot Empty => new(null, null, null, null, TempSource.None, DateTime.UtcNow);
}
