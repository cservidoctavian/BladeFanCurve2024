using System.Text;

namespace BladeFanCurve.Hardware;

/// <summary>Discrete boost levels. Razer exposes steps, not watts.</summary>
public enum BoostLevel : byte
{
    Low = 0x00,
    Medium = 0x01,
    High = 0x02,
    Boost = 0x03,
}

public enum BoostTarget : byte
{
    Cpu = 0x01,
    Gpu = 0x02,
}

/// <summary>
/// Power and battery commands beyond fan control.
///
/// Two of these — CPU/GPU boost and the charge limit — are not documented anywhere
/// public that could be verified, so this class never writes a command it has not
/// first proven exists by issuing the matching read. A read is non-mutating: if the
/// firmware does not implement the feature it answers "not supported" and nothing
/// happens. Only once a read succeeds is the corresponding write offered, and every
/// write is confirmed by reading the value back.
///
///   get boost      class 0x0D id 0x87 size 0x03  args [0x00, target]   -> args[2] = level
///   set boost      class 0x0D id 0x07 size 0x03  args [0x00, target, level]
///   get charge cap class 0x07 id 0x92 size 0x03  args [0x00]           -> args[1] on, args[2] %
///   set charge cap class 0x07 id 0x12 size 0x03  args [0x00, on, %]
///
/// The perf mode command (class 0x0D id 0x02) lives on RazerLaptopDevice because fan
/// control needs it too. That command is the real TDP lever: Razer's Balanced mode
/// runs a 35 W CPU target and Gaming runs 55 W.
/// </summary>
public sealed class RazerPower
{
    private const byte ClassLaptop = 0x0D;
    private const byte ClassBattery = 0x07;

    private const byte CmdSetBoost = 0x07;
    private const byte CmdGetBoost = 0x87;
    private const byte CmdSetChargeLimit = 0x12;
    private const byte CmdGetChargeLimit = 0x92;

    /// <summary>Razer's own UI limits the threshold to this band; going outside it is refused.</summary>
    public const int MinChargeLimit = 50;
    public const int MaxChargeLimit = 100;

    private readonly RazerLaptopDevice _device;

    /// <summary>True only when the firmware answered the read-only boost query.</summary>
    public bool SupportsBoost { get; private set; }

    /// <summary>True only when the firmware answered the read-only charge-limit query.</summary>
    public bool SupportsChargeLimit { get; private set; }

    public RazerPower(RazerLaptopDevice device) => _device = device;

    /// <summary>
    /// Establishes which optional features this firmware actually has, using reads
    /// only. Nothing here can change the machine's state.
    /// </summary>
    public string Probe()
    {
        var sb = new StringBuilder();

        SupportsBoost = TryGetBoost(BoostTarget.Cpu, out var cpuBoost);
        sb.AppendLine(SupportsBoost
            ? $"CPU/GPU boost   : supported (CPU currently {cpuBoost})"
            : "CPU/GPU boost   : not exposed by this firmware");

        SupportsChargeLimit = TryGetChargeLimit(out var on, out var percent);
        sb.AppendLine(SupportsChargeLimit
            ? $"Charge limit    : supported ({(on ? $"on at {percent}%" : "off")})"
            : "Charge limit    : not exposed by this firmware");

        return sb.ToString();
    }

    // ------------------------------------------------------------------- boost

    public bool TryGetBoost(BoostTarget target, out BoostLevel level)
    {
        level = BoostLevel.Medium;

        var request = RazerReport.Create(_device.TransactionId, ClassLaptop, CmdGetBoost, 0x03,
            0x00, (byte)target);
        if (!_device.TrySend(request, out var reply) || reply == null) return false;

        var raw = reply.Arguments[2];
        if (raw > (byte)BoostLevel.Boost) return false;

        level = (BoostLevel)raw;
        return true;
    }

    /// <summary>
    /// Sets a boost level and confirms it by reading back. Returns false if the
    /// feature was never proven present, if the write was rejected, or if the
    /// read-back disagrees — so a silent no-op reports as a failure.
    /// </summary>
    public bool SetBoost(BoostTarget target, BoostLevel level)
    {
        if (!SupportsBoost) return false;

        // The GPU has no top "boost" step; asking for one would be out of range.
        if (target == BoostTarget.Gpu && level == BoostLevel.Boost) level = BoostLevel.High;

        var request = RazerReport.Create(_device.TransactionId, ClassLaptop, CmdSetBoost, 0x03,
            0x00, (byte)target, (byte)level);
        if (!_device.TrySend(request, out _)) return false;

        Thread.Sleep(40);
        return TryGetBoost(target, out var actual) && actual == level;
    }

    // ----------------------------------------------------------- charge limit

    public bool TryGetChargeLimit(out bool enabled, out int percent)
    {
        enabled = false;
        percent = 100;

        var request = RazerReport.Create(_device.TransactionId, ClassBattery, CmdGetChargeLimit, 0x03, 0x00);
        if (!_device.TrySend(request, out var reply) || reply == null) return false;

        var raw = reply.Arguments[2];

        // A threshold outside the plausible band means this reply is not what we think
        // it is, so the feature is reported as unsupported rather than misread.
        if (raw is < MinChargeLimit or > MaxChargeLimit) return false;

        enabled = reply.Arguments[1] != 0x00;
        percent = raw;
        return true;
    }

    public bool SetChargeLimit(bool enabled, int percent)
    {
        if (!SupportsChargeLimit) return false;

        percent = Math.Clamp(percent, MinChargeLimit, MaxChargeLimit);

        // 100% means "charge normally", which is the same thing as the feature off.
        if (percent >= MaxChargeLimit) enabled = false;

        var request = RazerReport.Create(_device.TransactionId, ClassBattery, CmdSetChargeLimit, 0x03,
            0x00, enabled ? (byte)0x01 : (byte)0x00, (byte)percent);
        if (!_device.TrySend(request, out _)) return false;

        Thread.Sleep(60);
        if (!TryGetChargeLimit(out var actualOn, out var actualPercent)) return false;

        return actualOn == enabled && (!enabled || actualPercent == percent);
    }
}
