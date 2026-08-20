using System.Text;
using LibreHardwareMonitor.Hardware;

namespace BladeFanCurve.Sensors;

public sealed record SensorDescriptor(string Id, string HardwareName, string SensorName, HardwareType HardwareType)
{
    public string Display => $"{HardwareName} — {SensorName}";
    public override string ToString() => Display;
}

/// <summary>
/// Supplies CPU and GPU temperatures, in layers.
///
/// LibreHardwareMonitor is tried first: when its ring-0 driver is available it gives
/// the real CPU package temperature. That driver is not shipped with the library and
/// is blocked by Memory Integrity on current Windows 11, so when it is missing the
/// service falls back to ACPI thermal zones over WMI, which need no driver.
///
/// GPU temperature comes from LibreHardwareMonitor either way, because NVIDIA
/// reports it through user-mode NVAPI rather than through the kernel driver.
/// </summary>
public sealed class SensorService : IDisposable
{
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    private static readonly string[] CpuPreferredNames =
        { "Core (Tctl/Tdie)", "CPU Package", "Core Max", "Core Average", "CPU Cores", "Tctl", "Package" };

    private static readonly string[] GpuPreferredNames =
        { "GPU Core", "GPU Hot Spot", "GPU Temperature", "GPU" };

    private static readonly HardwareType[] GpuTypes =
        { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel };

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly AcpiThermalProvider _acpi = new();
    private readonly object _lock = new();
    private bool _open;

    public string? PinnedCpuSensorId { get; set; }
    public string? PinnedGpuSensorId { get; set; }

    /// <summary>Which layer is currently supplying the CPU temperature.</summary>
    public TempSource CpuSource { get; private set; } = TempSource.None;

    /// <summary>Human-readable explanation for the diagnostics pane and the status bar.</summary>
    public string SourceExplanation { get; private set; } = "Sensors have not been opened yet.";

    public SensorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };
    }

    public void Open()
    {
        lock (_lock)
        {
            if (_open) return;

            try
            {
                _computer.Open();
                _computer.Accept(_visitor);
            }
            catch (Exception ex)
            {
                SourceExplanation = $"LibreHardwareMonitor could not start: {ex.Message}";
            }

            _open = true;

            // Does the hardware monitor actually produce a CPU temperature? It only
            // can when its ring-0 driver loaded.
            var lhmCpu = ReadHardwareMonitorCpu();

            if (lhmCpu is > 0)
            {
                CpuSource = TempSource.HardwareMonitor;
                SourceExplanation = "CPU package temperature via LibreHardwareMonitor.";
                return;
            }

            _acpi.Open();

            if (_acpi.Available)
            {
                CpuSource = TempSource.AcpiThermalZone;
                SourceExplanation =
                    "The ring-0 driver LibreHardwareMonitor needs for AMD package temperature is not " +
                    "present, so the CPU curve is running from an ACPI thermal zone instead. " +
                    $"({_acpi.Status})";
            }
            else
            {
                CpuSource = TempSource.None;
                SourceExplanation =
                    "No CPU temperature source is available: the hardware monitor's kernel driver did " +
                    $"not load and {_acpi.Status}. The CPU fan will follow the GPU instead.";
            }
        }
    }

    public SensorSnapshot Poll()
    {
        lock (_lock)
        {
            if (!_open) return SensorSnapshot.Empty;

            try
            {
                _computer.Accept(_visitor);

                double? gpuTemp = null, cpuLoad = null, gpuLoad = null;

                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        cpuLoad ??= PickLoad(hw, "CPU Total");
                    }
                    else if (GpuTypes.Contains(hw.HardwareType))
                    {
                        var t = PickTemperature(hw, PinnedGpuSensorId, GpuPreferredNames);
                        if (t is > 0 && (gpuTemp is null || hw.HardwareType == HardwareType.GpuNvidia))
                        {
                            gpuTemp = t;
                            gpuLoad = PickLoad(hw, "GPU Core");
                        }
                    }
                }

                var (cpuTemp, source) = ReadCpuTemperature(gpuTemp);

                return new SensorSnapshot(cpuTemp, gpuTemp, cpuLoad, gpuLoad, source, DateTime.UtcNow);
            }
            catch
            {
                return SensorSnapshot.Empty;
            }
        }
    }

    /// <summary>Walks the layers in order of quality.</summary>
    private (double? Temp, TempSource Source) ReadCpuTemperature(double? gpuTemp)
    {
        var lhm = ReadHardwareMonitorCpu();
        if (lhm is > 0)
        {
            CpuSource = TempSource.HardwareMonitor;
            return (lhm, TempSource.HardwareMonitor);
        }

        if (_acpi.Available)
        {
            var acpi = _acpi.ReadHottestCelsius();
            if (acpi is > 0)
            {
                CpuSource = TempSource.AcpiThermalZone;
                return (acpi, TempSource.AcpiThermalZone);
            }
        }

        // Nothing CPU-specific. Borrowing the GPU reading keeps the CPU fan moving
        // with the machine's actual heat rather than parking it at the curve floor.
        if (gpuTemp is > 0)
        {
            CpuSource = TempSource.BorrowedFromGpu;
            return (gpuTemp, TempSource.BorrowedFromGpu);
        }

        CpuSource = TempSource.None;
        return (null, TempSource.None);
    }

    private double? ReadHardwareMonitorCpu()
    {
        try
        {
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu) continue;
                var t = PickTemperature(hw, PinnedCpuSensorId, CpuPreferredNames);
                if (t is > 0) return t;
            }
        }
        catch { /* treated as unavailable */ }

        return null;
    }

    public IReadOnlyList<SensorDescriptor> EnumerateTemperatureSensors()
    {
        lock (_lock)
        {
            var list = new List<SensorDescriptor>();
            if (!_open) return list;

            foreach (var hw in _computer.Hardware)
            {
                Collect(hw);
                foreach (var sub in hw.SubHardware) Collect(sub);
            }

            return list;

            void Collect(IHardware hw)
            {
                foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                    list.Add(new SensorDescriptor(s.Identifier.ToString(), hw.Name, s.Name, hw.HardwareType));
            }
        }
    }

    /// <summary>Full picture of what each layer can see, for the diagnostics pane.</summary>
    public string DescribeSources()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Active CPU source : {CpuSource}");
        sb.AppendLine($"Explanation       : {SourceExplanation}");
        sb.AppendLine();

        sb.AppendLine("LibreHardwareMonitor sensors:");
        var sensors = EnumerateTemperatureSensors();
        if (sensors.Count == 0)
            sb.AppendLine("  (none — this is what a missing ring-0 driver looks like)");
        else
            foreach (var s in sensors)
                sb.AppendLine($"  {s.HardwareType,-12} {s.HardwareName} / {s.SensorName}   [{s.Id}]");

        sb.AppendLine();
        sb.AppendLine("ACPI thermal zones (driver-free):");
        var zones = _acpi.ReadZones(force: true);
        if (zones.Count == 0)
            sb.AppendLine($"  (none — {_acpi.Status})");
        else
            foreach (var z in zones)
                sb.AppendLine($"  {z.Name,-14} {z.Celsius:0.0} °C");

        return sb.ToString();
    }

    private static double? PickTemperature(IHardware hw, string? pinnedId, string[] preferred)
    {
        var sensors = hw.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 130)
            .ToList();

        foreach (var sub in hw.SubHardware)
            sensors.AddRange(sub.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 130));

        if (sensors.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(pinnedId))
        {
            var pinned = sensors.FirstOrDefault(s => s.Identifier.ToString() == pinnedId);
            if (pinned?.Value is { } pv) return pv;
        }

        foreach (var name in preferred)
        {
            var match = sensors.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match?.Value is { } v) return v;
        }

        // Nothing recognised: fall back to the hottest reading on this hardware,
        // which errs on the side of more cooling rather than less.
        return sensors.Max(s => s.Value ?? 0);
    }

    private static double? PickLoad(IHardware hw, string name)
    {
        var s = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Load &&
                                               x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return s?.Value;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_open) return;
            try { _computer.Close(); } catch { /* driver already unloaded */ }
            _acpi.Dispose();
            _open = false;
        }
    }
}
