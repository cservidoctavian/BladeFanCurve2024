using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BladeFanCurve.Hardware;

/// <summary>Fan zone identifiers used by the 0x0D command class.</summary>
public enum FanZone : byte
{
    Cpu = 0x01,
    Gpu = 0x02,
}

/// <summary>Razer performance ("power") modes.</summary>
public enum PerfMode : byte
{
    Balanced = 0x00,
    Gaming = 0x01,
    Creator = 0x02,
    Custom = 0x04,
}

public readonly record struct PerfModeState(PerfMode Mode, bool ManualFan);

/// <summary>
/// Talks to a Razer laptop's embedded controller over the same HID feature-report
/// channel Synapse uses (command class 0x0D).
///
///   set fan rpm     class 0x0D id 0x01 size 0x03  args [v, zone, rpm/100]
///   get fan rpm     class 0x0D id 0x81 size 0x03  args [0x00, zone]      -> args[2] * 100
///   set perf mode   class 0x0D id 0x02 size 0x04  args [0x00, zone, mode, manualFanFlag]
///   get perf mode   class 0x0D id 0x82 size 0x04  args [0x00, zone]      -> args[2]=mode args[3]=flag
///   read tachometer class 0x0D id 0x88 size 0x04  args [0x00, zone]      -> args[2] * 100
/// </summary>
public sealed class RazerLaptopDevice : IDisposable
{
    public const int RazerVendorId = 0x1532;

    private const byte ClassLaptop = 0x0D;
    private const byte ClassStandard = 0x00;
    private const byte CmdSetFanRpm = 0x01;
    private const byte CmdGetFanRpm = 0x81;
    private const byte CmdSetPerfMode = 0x02;
    private const byte CmdGetPerfMode = 0x82;
    private const byte CmdGetTachometer = 0x88;
    private const byte CmdGetFirmware = 0x81; // with ClassStandard

    /// <summary>Transaction ids seen across Razer laptop generations, most likely first.</summary>
    private static readonly byte[] CandidateTransactionIds = { 0x1F, 0x08, 0x3F, 0xFF, 0x00, 0x88, 0x9F };

    /// <summary>Some firmware needs longer than others to prepare a reply.</summary>
    private static readonly int[] CandidateDelaysMs = { 30, 90, 200 };

    private readonly object _io = new();
    private SafeFileHandle? _handle;
    private int _featureLength;

    public int CommandDelayMs { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;

    public byte TransactionId { get; private set; }

    /// <summary>First argument byte of the "set fan rpm" command for this firmware.</summary>
    public byte SetRpmArg0 { get; set; } = 0x01;

    /// <summary>Flips between the two known "set fan rpm" argument layouts.</summary>
    public byte SwitchSetRpmVariant()
    {
        SetRpmArg0 = SetRpmArg0 == 0x00 ? (byte)0x01 : (byte)0x00;
        return SetRpmArg0;
    }

    public string ProductName { get; private set; } = "";
    public int ProductId { get; private set; }
    public string DevicePath { get; private set; } = "";
    public string GrantedAccess { get; private set; } = "";
    public bool IsConnected => _handle is { IsInvalid: false, IsClosed: false };

    private RazerLaptopDevice() { }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Finds the control interface by probing every Razer HID interface that is big
    /// enough to carry a 90-byte control report. Probing uses read-only commands, so
    /// it cannot change device state.
    /// </summary>
    public static RazerLaptopDevice? Discover(out string probeLog)
    {
        var log = new StringBuilder();
        RazerLaptopDevice? found = null;

        List<string> paths;
        try
        {
            paths = NativeHid.EnumerateDevicePaths().ToList();
        }
        catch (Exception ex)
        {
            probeLog = $"HID enumeration failed: {ex.GetType().Name}: {ex.Message}";
            return null;
        }

        log.AppendLine($"Enumerated {paths.Count} HID interface(s) on this machine.");

        var razerCount = 0;
        var candidates = new List<(string Path, HidInfo Info, string Access)>();

        foreach (var path in paths)
        {
            var handle = NativeHid.Open(path, out var access, out var error);
            if (handle == null)
            {
                if (LooksRazer(path))
                    log.AppendLine($"  {Short(path)}  -> cannot open at all (win32 error {error})");
                continue;
            }

            using (handle)
            {
                if (!NativeHid.TryGetInfo(handle, out var info)) continue;
                if (info.VendorId != RazerVendorId) continue;

                razerCount++;
                log.AppendLine(
                    $"  1532:{info.ProductId:X4} iface={InterfaceOf(path)} " +
                    $"usage={info.UsagePage:X2}:{info.Usage:X2} feature={info.FeatureReportLength} " +
                    $"access={access}  \"{info.ProductName}\"");

                if (info.FeatureReportLength >= RazerReport.WireSize)
                    candidates.Add((path, info, access));
                else
                    log.AppendLine($"      skipped: feature report is {info.FeatureReportLength} bytes, " +
                                   $"needs at least {RazerReport.WireSize}");
            }
        }

        log.AppendLine($"Found {razerCount} Razer interface(s), {candidates.Count} able to carry a control report.");
        log.AppendLine();

        // Probe the lowest interface numbers first — that is where the control
        // endpoint lives on every Razer laptop seen so far.
        foreach (var (path, info, access) in candidates.OrderBy(c => InterfaceRank(c.Path)))
        {
            log.AppendLine($"Probing 1532:{info.ProductId:X4} iface={InterfaceOf(path)} (access={access})");

            var handle = NativeHid.Open(path, out var grantedAccess, out var openError);
            if (handle == null)
            {
                log.AppendLine($"  -> reopen failed (win32 error {openError})");
                continue;
            }

            var device = new RazerLaptopDevice
            {
                _handle = handle,
                _featureLength = info.FeatureReportLength,
                ProductId = info.ProductId,
                ProductName = info.ProductName,
                DevicePath = path,
                GrantedAccess = grantedAccess,
            };

            var known = KnownModels.Find(info.ProductId);
            if (known != null) device.SetRpmArg0 = known.SetRpmArg0;

            var talks = false;

            foreach (var delay in CandidateDelaysMs)
            {
                device.CommandDelayMs = delay;

                foreach (var txn in CandidateTransactionIds)
                {
                    device.TransactionId = txn;

                    if (device.TryGetPerfMode(FanZone.Cpu, out var state))
                    {
                        log.AppendLine($"  -> ANSWERED on txn 0x{txn:X2}, delay {delay} ms " +
                                       $"(mode={state.Mode}, manual fan={state.ManualFan})");
                        found = device;
                        break;
                    }

                    if (!talks && device.TryGetFirmwareVersion(out var firmware))
                    {
                        talks = true;
                        log.AppendLine($"  -> device responds on txn 0x{txn:X2} (firmware {firmware}) " +
                                       "but did not answer the laptop command class here");
                    }
                }

                if (found != null) break;
            }

            if (found != null) break;

            log.AppendLine(talks
                ? "  -> talks, but no fan control on this interface"
                : "  -> no reply on any transaction id or delay");
            handle.Dispose();
        }

        if (found == null)
        {
            log.AppendLine();
            log.AppendLine("No Razer laptop control interface answered.");
            if (razerCount == 0)
                log.AppendLine("No Razer HID devices were visible at all — check Device Manager.");
            else if (candidates.Count == 0)
                log.AppendLine("Razer devices were found but none exposes a 91-byte feature report.");
        }

        probeLog = log.ToString();
        return found;
    }

    // ---------------------------------------------------------------- commands

    public bool TryGetFirmwareVersion(out string version)
    {
        version = "";
        var request = RazerReport.Create(TransactionId, ClassStandard, CmdGetFirmware, 0x02);
        if (!TrySendReceive(request, out var reply) || reply == null) return false;
        version = $"v{reply.Arguments[0]}.{reply.Arguments[1]}";
        return true;
    }

    public bool TryGetPerfMode(FanZone zone, out PerfModeState state)
    {
        state = default;
        var request = RazerReport.Create(TransactionId, ClassLaptop, CmdGetPerfMode, 0x04, 0x00, (byte)zone);
        if (!TrySendReceive(request, out var reply) || reply == null) return false;

        var mode = reply.Arguments[2];
        state = new PerfModeState(
            Enum.IsDefined(typeof(PerfMode), mode) ? (PerfMode)mode : PerfMode.Balanced,
            reply.Arguments[3] != 0x00);
        return true;
    }

    public bool SetPerfMode(FanZone zone, PerfMode mode, bool manualFan)
    {
        var request = RazerReport.Create(TransactionId, ClassLaptop, CmdSetPerfMode, 0x04,
            0x00, (byte)zone, (byte)mode, manualFan ? (byte)0x01 : (byte)0x00);
        return TrySendReceive(request, out _);
    }

    /// <summary>Hands fan control back to the laptop's own thermal management.</summary>
    public bool RestoreAutomaticFans(PerfMode mode = PerfMode.Balanced)
    {
        var ok = true;
        foreach (var zone in new[] { FanZone.Cpu, FanZone.Gpu })
            ok &= SetPerfMode(zone, mode, manualFan: false);
        return ok;
    }

    /// <summary>Puts both zones under manual RPM control.</summary>
    public bool EnableManualFans(PerfMode mode = PerfMode.Balanced)
    {
        var ok = true;
        foreach (var zone in new[] { FanZone.Cpu, FanZone.Gpu })
            ok &= SetPerfMode(zone, mode, manualFan: true);
        return ok;
    }

    /// <summary>RPM is transmitted as rpm/100, so values are quantised to 100 RPM.</summary>
    public bool SetFanRpm(FanZone zone, int rpm)
    {
        var encoded = rpm / 100;
        if (encoded is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(rpm), rpm, "RPM must encode into a single byte (0-25500).");

        var request = RazerReport.Create(TransactionId, ClassLaptop, CmdSetFanRpm, 0x03,
            SetRpmArg0, (byte)zone, (byte)encoded);
        return TrySendReceive(request, out _);
    }

    public bool TryGetFanRpmSetpoint(FanZone zone, out int rpm)
    {
        rpm = 0;
        var request = RazerReport.Create(TransactionId, ClassLaptop, CmdGetFanRpm, 0x03, 0x00, (byte)zone);
        if (!TrySendReceive(request, out var reply) || reply == null) return false;
        rpm = reply.Arguments[2] * 100;
        return true;
    }

    /// <summary>
    /// Asks the EC for an RPM it cannot reach and reads back what it accepted, which
    /// is the hardware ceiling. Spins the fans up loudly for a moment.
    /// </summary>
    public bool TryProbeMaximumRpm(FanZone zone, out int maxRpm)
    {
        maxRpm = 0;
        if (!SetFanRpm(zone, 9000)) return false;
        Thread.Sleep(250);
        if (!TryGetFanRpmSetpoint(zone, out var accepted)) return false;
        maxRpm = accepted;
        return accepted > 0;
    }

    /// <summary>Reads the measured fan speed. Not supported by every firmware.</summary>
    public bool TryGetFanTachometer(FanZone zone, out int rpm)
    {
        rpm = 0;
        var request = RazerReport.Create(TransactionId, ClassLaptop, CmdGetTachometer, 0x04, 0x00, (byte)zone);
        if (!TrySendReceive(request, out var reply) || reply == null) return false;
        rpm = reply.Arguments[2] * 100;
        return true;
    }

    // ---------------------------------------------------------------- transport

    private bool TrySendReceive(RazerReport request, out RazerReport? reply)
    {
        reply = null;
        lock (_io)
        {
            var handle = _handle;
            if (handle is null || handle.IsInvalid || handle.IsClosed) return false;

            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    if (!NativeHid.SetFeature(handle, request.ToWireBytes(_featureLength)))
                    {
                        Debug.WriteLine($"[razer] HidD_SetFeature failed, win32 {NativeHid.LastError()}");
                        return false;
                    }

                    Thread.Sleep(CommandDelayMs);

                    var inBuffer = new byte[_featureLength];
                    inBuffer[0] = 0x00; // report id
                    if (!NativeHid.GetFeature(handle, inBuffer))
                    {
                        Debug.WriteLine($"[razer] HidD_GetFeature failed, win32 {NativeHid.LastError()}");
                        return false;
                    }

                    var parsed = RazerReport.FromWireBytes(inBuffer);

                    if (parsed.StatusCode == RazerStatus.Busy)
                    {
                        Thread.Sleep(CommandDelayMs * (attempt + 2));
                        continue;
                    }

                    if (!parsed.IsSuccess) return false;
                    if (!parsed.Echoes(request)) return false;

                    reply = parsed;
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[razer] transport error: {ex.Message}");
                    if (attempt == MaxRetries - 1) return false;
                    Thread.Sleep(40 * (attempt + 1));
                }
            }

            return false;
        }
    }

    /// <summary>Re-opens the handle after a suspend/resume or a USB reset.</summary>
    public bool Reconnect()
    {
        lock (_io)
        {
            try
            {
                _handle?.Dispose();
                _handle = null;

                var handle = NativeHid.Open(DevicePath, out var access, out _);
                if (handle == null) return false;

                if (!NativeHid.TryGetInfo(handle, out var info))
                {
                    handle.Dispose();
                    return false;
                }

                _handle = handle;
                _featureLength = info.FeatureReportLength;
                GrantedAccess = access;
                return TryGetPerfMode(FanZone.Cpu, out _);
            }
            catch
            {
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool LooksRazer(string path) =>
        path.Contains("vid_1532", StringComparison.OrdinalIgnoreCase);

    private static string Short(string path)
    {
        var m = Regex.Match(path, @"(vid_[0-9a-f]{4}&pid_[0-9a-f]{4}[^#]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : path;
    }

    private static string InterfaceOf(string path)
    {
        var m = Regex.Match(path, @"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "--";
    }

    private static int InterfaceRank(string path)
    {
        var iface = InterfaceOf(path);
        return int.TryParse(iface, System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : 99;
    }

    public void Dispose()
    {
        lock (_io)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }
}
