using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BladeFanCurve.Hardware;

/// <summary>
/// Minimal HID access built directly on setupapi/hid.dll.
///
/// This exists instead of a HID library for one reason: on Windows the HID class
/// driver refuses to hand user mode a read/write handle to a device that acts as a
/// system keyboard or mouse — CreateFile returns ERROR_ACCESS_DENIED. A Razer
/// laptop's control interface *is* its keyboard, so every general-purpose HID
/// wrapper fails to open it.
///
/// The way around it is to open the handle with a desired-access mask of zero.
/// HidD_SetFeature and HidD_GetFeature issue IOCTLs declared FILE_ANY_ACCESS, so
/// they still work on a zero-access handle, and a zero-access open also does not
/// conflict with Synapse or with Windows' own keyboard stack.
/// </summary>
internal static class NativeHid
{
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>
    /// Tried in order. Zero access comes first: it is the most likely to be granted
    /// and is sufficient for feature reports.
    /// </summary>
    internal static readonly (uint Mask, string Name)[] AccessModes =
    {
        (0u, "none"),
        (GENERIC_READ, "read"),
        (GENERIC_READ | GENERIC_WRITE, "read+write"),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle handle, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, int bufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int detailSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    // ------------------------------------------------------------------ API

    /// <summary>Every HID interface path currently present on the system.</summary>
    public static IReadOnlyList<string> EnumerateDevicePaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var hidGuid);

        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return paths;

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface); index++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0) continue;

                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA is 8 on x64, 6 on x86 —
                    // it describes the fixed header, not the whole allocation.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, out _, IntPtr.Zero))
                        continue;

                    var path = Marshal.PtrToStringUni(buffer + 4);
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    /// <summary>
    /// Opens a HID path, trying progressively more permissive access masks.
    /// Returns null if none worked; <paramref name="grantedAccess"/> names the one
    /// that did, which is worth logging.
    /// </summary>
    public static SafeFileHandle? Open(string path, out string grantedAccess, out int lastError)
    {
        grantedAccess = "none";
        lastError = 0;

        foreach (var (mask, name) in AccessModes)
        {
            var handle = CreateFile(path, mask, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                grantedAccess = name;
                return handle;
            }

            lastError = Marshal.GetLastWin32Error();
            handle.Dispose();
        }

        return null;
    }

    public static bool TryGetInfo(SafeFileHandle handle, out HidInfo info)
    {
        info = default!;

        var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attributes)) return false;

        ushort usage = 0, usagePage = 0, featureLength = 0, inputLength = 0, outputLength = 0;
        if (HidD_GetPreparsedData(handle, out var preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) == 0x00110000) // HIDP_STATUS_SUCCESS
                {
                    usage = caps.Usage;
                    usagePage = caps.UsagePage;
                    featureLength = caps.FeatureReportByteLength;
                    inputLength = caps.InputReportByteLength;
                    outputLength = caps.OutputReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        info = new HidInfo(attributes.VendorID, attributes.ProductID, attributes.VersionNumber,
            usagePage, usage, featureLength, inputLength, outputLength, ReadProductName(handle));
        return true;
    }

    private static string ReadProductName(SafeFileHandle handle)
    {
        try
        {
            var sb = new StringBuilder(256);
            if (HidD_GetProductString(handle, sb, sb.Capacity * 2) && sb.Length > 0) return sb.ToString();

            sb.Clear();
            if (HidD_GetManufacturerString(handle, sb, sb.Capacity * 2) && sb.Length > 0) return sb.ToString();
        }
        catch { /* string descriptors are optional */ }

        return "(no product string)";
    }

    public static bool SetFeature(SafeFileHandle handle, byte[] buffer) =>
        HidD_SetFeature(handle, buffer, buffer.Length);

    public static bool GetFeature(SafeFileHandle handle, byte[] buffer) =>
        HidD_GetFeature(handle, buffer, buffer.Length);

    public static int LastError() => Marshal.GetLastWin32Error();
}

public sealed record HidInfo(
    ushort VendorId,
    ushort ProductId,
    ushort Version,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportLength,
    ushort InputReportLength,
    ushort OutputReportLength,
    string ProductName);
