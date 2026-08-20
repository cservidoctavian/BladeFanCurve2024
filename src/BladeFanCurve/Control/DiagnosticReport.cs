using System.IO;
using System.Security.Principal;
using System.Text;
using BladeFanCurve.Hardware;

namespace BladeFanCurve.Control;

/// <summary>
/// Writes a complete picture of what the machine looks like from this process:
/// every HID interface, what access Windows granted on each, and what the Razer
/// probe saw. This is the one artefact worth sending to someone when discovery
/// fails, because it distinguishes "the device is invisible" from "the device is
/// visible but will not open" from "it opens but does not speak command class 0x0D".
/// </summary>
public static class DiagnosticReport
{
    public static string Build(string probeLog, string selfTest)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Blade Fan Curve — diagnostic report");
        sb.AppendLine($"Generated  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version: {typeof(DiagnosticReport).Assembly.GetName().Version}");
        sb.AppendLine($"OS         : {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($"Process     : {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, elevated = {IsElevated()}");
        sb.AppendLine($"Machine    : {Environment.MachineName}");
        sb.AppendLine();

        sb.AppendLine("=== Kernel driver (PawnIO) ===");
        sb.AppendLine(Platform.PawnIoStatus.Describe());
        sb.AppendLine($"  registry version : {Platform.PawnIoStatus.InstalledVersion() ?? "(not installed)"}");
        sb.AppendLine($"  device reachable : {Platform.PawnIoStatus.DevicePresent()}");
        sb.AppendLine();

        sb.AppendLine("=== All HID interfaces ===");
        sb.AppendLine("Every HID device visible to this process, with the access mask Windows granted.");
        sb.AppendLine("A Razer laptop's control interface is normally VID 1532 with feature length 91.");
        sb.AppendLine();
        AppendHidDump(sb);

        sb.AppendLine();
        sb.AppendLine("=== Razer discovery probe ===");
        sb.AppendLine(probeLog);

        sb.AppendLine();
        sb.AppendLine("=== Self-test ===");
        sb.AppendLine(selfTest);

        sb.AppendLine();
        sb.AppendLine("=== Recent log ===");
        foreach (var entry in Log.Recent()) sb.AppendLine(entry.ToString());

        return sb.ToString();
    }

    private static void AppendHidDump(StringBuilder sb)
    {
        List<string> paths;
        try
        {
            paths = NativeHid.EnumerateDevicePaths().ToList();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Enumeration failed: {ex}");
            return;
        }

        sb.AppendLine($"{paths.Count} interface(s) present.");
        sb.AppendLine();
        sb.AppendLine("  VID:PID    usage   feat in  out  access      product");
        sb.AppendLine("  ---------- ------- ---- ---- ---- ----------- -------------------------------");

        var razer = new List<string>();

        foreach (var path in paths)
        {
            var handle = NativeHid.Open(path, out var access, out var error);
            if (handle == null)
            {
                sb.AppendLine($"  (could not open: win32 {error})  {path}");
                continue;
            }

            using (handle)
            {
                if (!NativeHid.TryGetInfo(handle, out var info))
                {
                    sb.AppendLine($"  (no attributes)  {path}");
                    continue;
                }

                var line =
                    $"  {info.VendorId:X4}:{info.ProductId:X4}  " +
                    $"{info.UsagePage:X2}:{info.Usage:X2}   " +
                    $"{info.FeatureReportLength,4} {info.InputReportLength,4} {info.OutputReportLength,4} " +
                    $"{access,-11} {info.ProductName}";

                if (info.VendorId == RazerLaptopDevice.RazerVendorId)
                {
                    razer.Add(line);
                    razer.Add($"      path: {path}");
                }
                else
                {
                    sb.AppendLine(line);
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  --- Razer devices ({razer.Count(l => !l.TrimStart().StartsWith("path:"))}) ---");
        if (razer.Count == 0)
            sb.AppendLine("  NONE. No VID 1532 device is visible to this process at all.");
        else
            foreach (var line in razer) sb.AppendLine(line);
    }

    /// <summary>Writes the report next to the config and returns the path.</summary>
    public static string Save(string content)
    {
        Directory.CreateDirectory(Config.ConfigStore.Directory);
        var path = Path.Combine(Config.ConfigStore.Directory,
            $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
