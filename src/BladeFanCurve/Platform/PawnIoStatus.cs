using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BladeFanCurve.Platform;

/// <summary>
/// Detects PawnIO, the kernel driver LibreHardwareMonitor 0.9.x needs for anything
/// that lives behind a model-specific register: CPU package temperature, CPU package
/// power, and the Ryzen SMU sensors.
///
/// Worth being precise about, because the obvious guess is wrong. Older versions of
/// the library used WinRing0, which Microsoft's vulnerable-driver blocklist and
/// Memory Integrity do block — so "turn off Core Isolation" is the advice all over
/// the internet. Version 0.9.6 does not ship or use WinRing0 at all; it loads
/// sandboxed bytecode modules through PawnIO instead. If PawnIO is absent, turning
/// off Memory Integrity achieves nothing except making the machine less safe.
/// </summary>
public static class PawnIoStatus
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    public const string DownloadUrl = "https://pawnio.eu/";

    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>Installed version string, or null when PawnIO is not installed.</summary>
    public static string? InstalledVersion()
    {
        // Check both registry views: the installer may be 32- or 64-bit.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (key?.GetValue("DisplayVersion") is string v && v.Length > 0) return v;
            }
            catch
            {
                // Registry access denied; fall through to the device check.
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the driver is actually loaded right now, which is the thing that
    /// matters. An install that failed to start would still show a registry entry.
    /// </summary>
    public static bool DevicePresent()
    {
        try
        {
            using var handle = CreateFile(DevicePath, 0, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            return !handle.IsInvalid;
        }
        catch
        {
            return false;
        }
    }

    public static bool Available => DevicePresent() || InstalledVersion() != null;

    /// <summary>A sentence for the UI that says what is true and what to do about it.</summary>
    public static string Describe()
    {
        var version = InstalledVersion();
        var device = DevicePresent();

        return (version, device) switch
        {
            (not null, true) => $"PawnIO {version} is installed and running.",
            (not null, false) =>
                $"PawnIO {version} is installed but its driver is not running. A reboot usually "
                + "starts it.",
            (null, true) => "The PawnIO driver is running.",
            (null, false) =>
                "PawnIO is not installed. LibreHardwareMonitor needs it to read CPU package power "
                + "and package temperature — those live behind model-specific registers that no "
                + "user-mode API exposes. Install it from pawnio.eu and restart this app.",
        };
    }
}
