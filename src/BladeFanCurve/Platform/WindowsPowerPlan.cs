using System.Runtime.InteropServices;
using System.Text;

namespace BladeFanCurve.Platform;

public sealed record PowerScheme(Guid Id, string Name);

/// <summary>
/// Windows power schemes and the "power mode" overlay — the slider that appears under
/// the battery flyout on modern Windows. They are separate things: the scheme sets the
/// detailed policy, the overlay biases it toward efficiency or performance on top.
/// </summary>
public static class WindowsPowerPlan
{
    // The three schemes every Windows install ships with.
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    // Overlay GUIDs. An all-zero overlay means "recommended", i.e. no bias.
    public static readonly Guid OverlayNone = Guid.Empty;
    public static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private const uint AccessScheme = 16;
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingGuid,
        uint accessFlags, uint index, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    // Not in the SDK headers, but a stable export since Windows 10 1709 and the only
    // way to move the power-mode slider programmatically.
    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerGetActualOverlayScheme")]
    private static extern uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Every power scheme currently defined on this machine.</summary>
    public static IReadOnlyList<PowerScheme> Enumerate()
    {
        var schemes = new List<PowerScheme>();

        for (uint index = 0; ; index++)
        {
            uint size = 16;
            var buffer = new byte[16];

            var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                AccessScheme, index, buffer, ref size);
            if (result != ErrorSuccess) break;

            var id = new Guid(buffer);
            schemes.Add(new PowerScheme(id, ReadFriendlyName(id) ?? id.ToString()));
        }

        return schemes;
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        var result = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (result is not (ErrorSuccess or ErrorMoreData) || size == 0) return null;

        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != ErrorSuccess)
            return null;

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static Guid? GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != ErrorSuccess || pointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public static bool SetActiveScheme(Guid scheme) =>
        PowerSetActiveScheme(IntPtr.Zero, ref scheme) == ErrorSuccess;

    /// <summary>
    /// Moves the power-mode slider. Fails harmlessly on builds that predate it, and on
    /// machines where the OEM has replaced the overlay set.
    /// </summary>
    public static bool SetOverlay(Guid overlay)
    {
        try
        {
            return PowerSetActiveOverlayScheme(overlay) == ErrorSuccess;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static Guid? GetOverlay()
    {
        try
        {
            return PowerGetActualOverlayScheme(out var overlay) == ErrorSuccess ? overlay : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static string Describe()
    {
        var sb = new StringBuilder();
        var active = GetActiveScheme();

        foreach (var scheme in Enumerate())
            sb.AppendLine($"  {(scheme.Id == active ? "*" : " ")} {scheme.Name}  {scheme.Id}");

        var overlay = GetOverlay();
        sb.AppendLine($"  power mode overlay: {DescribeOverlay(overlay)}");
        return sb.ToString();
    }

    public static string DescribeOverlay(Guid? overlay) => overlay switch
    {
        null => "unavailable",
        { } g when g == OverlayBestEfficiency => "best power efficiency",
        { } g when g == OverlayBestPerformance => "best performance",
        { } g when g == Guid.Empty => "recommended",
        _ => overlay.ToString() ?? "unknown",
    };
}
