using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BladeFanCurve.Platform;

public sealed record DisplayMode(int Width, int Height, int RefreshHz)
{
    public override string ToString() => $"{Width} x {Height}  @  {RefreshHz} Hz";
}

/// <summary>
/// Display settings reachable without a vendor driver: resolution and refresh rate
/// through the standard display API, and colour through the gamma ramp and the
/// system's ICC profile association.
///
/// What is deliberately absent: panel overdrive / response time, and hardware gamut
/// switching. Neither is exposed by any documented Razer command — they are scaler
/// features — so this class does not pretend to offer them. Setting the ICC profile
/// changes what colour-managed applications do; it is not the panel-level clamp.
/// </summary>
public static class DisplayControl
{
    private const int EnumCurrentSettings = -1;
    private const int CdsUpdateRegistry = 0x01;
    private const int CdsTest = 0x02;
    private const int DispChangeSuccessful = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE devMode,
        IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref RAMP ramp);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ref RAMP ramp);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
    }

    // ------------------------------------------------------------ refresh rate

    public static DisplayMode? GetCurrentMode()
    {
        var mode = new DEVMODE { dmDeviceName = "", dmFormName = "" };
        mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

        return EnumDisplaySettings(null, EnumCurrentSettings, ref mode)
            ? new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency)
            : null;
    }

    /// <summary>Refresh rates available at the resolution currently in use.</summary>
    public static IReadOnlyList<int> AvailableRefreshRates()
    {
        var current = GetCurrentMode();
        if (current == null) return Array.Empty<int>();

        var rates = new SortedSet<int>();
        var mode = new DEVMODE { dmDeviceName = "", dmFormName = "" };
        mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

        for (var i = 0; EnumDisplaySettings(null, i, ref mode); i++)
        {
            if (mode.dmPelsWidth != current.Width || mode.dmPelsHeight != current.Height) continue;
            // 0 and 1 are the driver's way of saying "hardware default".
            if (mode.dmDisplayFrequency > 1) rates.Add((int)mode.dmDisplayFrequency);
        }

        return rates.Reverse().ToList();
    }

    /// <summary>
    /// Changes refresh rate at the current resolution. The mode is validated with a
    /// test call first, so an unsupported rate is refused instead of blanking the
    /// screen and waiting for the fifteen-second revert.
    /// </summary>
    public static bool SetRefreshRate(int hz, out string message)
    {
        var current = GetCurrentMode();
        if (current == null)
        {
            message = "Could not read the current display mode.";
            return false;
        }

        if (current.RefreshHz == hz)
        {
            message = $"Already at {hz} Hz.";
            return true;
        }

        var mode = new DEVMODE { dmDeviceName = "", dmFormName = "" };
        mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
        {
            message = "Could not read the current display mode.";
            return false;
        }

        mode.dmDisplayFrequency = (uint)hz;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        var test = ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != DispChangeSuccessful)
        {
            message = $"The display refused {hz} Hz at {current.Width} x {current.Height}.";
            return false;
        }

        var result = ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (result == DispChangeSuccessful)
        {
            message = $"Refresh rate set to {hz} Hz.";
            return true;
        }

        message = $"Windows rejected the change (code {result}).";
        return false;
    }

    // ------------------------------------------------------------------ colour

    /// <summary>
    /// Applies a colour temperature and gamma through the display LUT.
    /// </summary>
    /// <param name="kelvin">1200 (very warm) to 6500 (neutral).</param>
    /// <param name="brightness">0.1 to 1.0, applied on top of the panel's own backlight.</param>
    public static bool ApplyColourTemperature(int kelvin, double brightness = 1.0)
    {
        var (rf, gf, bf) = TemperatureToScale(kelvin);
        brightness = Math.Clamp(brightness, 0.1, 1.0);

        var ramp = new RAMP { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };

        for (var i = 0; i < 256; i++)
        {
            var value = i * 257.0 * brightness; // 257 maps 0..255 onto 0..65535
            ramp.Red[i] = Clamp16(value * rf);
            ramp.Green[i] = Clamp16(value * gf);
            ramp.Blue[i] = Clamp16(value * bf);
        }

        return WithScreenDc(hdc => SetDeviceGammaRamp(hdc, ref ramp));
    }

    /// <summary>Puts the LUT back to a straight identity ramp.</summary>
    public static bool ResetColour()
    {
        var ramp = new RAMP { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };
        for (var i = 0; i < 256; i++)
        {
            var value = Clamp16(i * 257.0);
            ramp.Red[i] = value;
            ramp.Green[i] = value;
            ramp.Blue[i] = value;
        }

        return WithScreenDc(hdc => SetDeviceGammaRamp(hdc, ref ramp));
    }

    /// <summary>
    /// Tanner Helland's black-body approximation, which is the same curve f.lux and
    /// Redshift use. Above 6500 K it flattens out, so warm-only is all that is offered.
    /// </summary>
    private static (double R, double G, double B) TemperatureToScale(int kelvin)
    {
        var t = Math.Clamp(kelvin, 1200, 6500) / 100.0;

        var r = t <= 66 ? 255.0 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        var g = t <= 66
            ? 99.4708025861 * Math.Log(t) - 161.1195681661
            : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        var b = t >= 66 ? 255.0 : t <= 19 ? 0.0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;

        return (Math.Clamp(r, 0, 255) / 255.0,
                Math.Clamp(g, 0, 255) / 255.0,
                Math.Clamp(b, 0, 255) / 255.0);
    }

    private static ushort Clamp16(double v) => (ushort)Math.Clamp(v, 0, 65535);

    private static bool WithScreenDc(Func<IntPtr, bool> action)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;

        try
        {
            return action(hdc);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    // -------------------------------------------------------------------- ICC

    private static string ColorDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");

    /// <summary>ICC/ICM profiles installed on this machine, by file name.</summary>
    public static IReadOnlyList<string> InstalledColourProfiles()
    {
        try
        {
            if (!Directory.Exists(ColorDirectory)) return Array.Empty<string>();

            return Directory.EnumerateFiles(ColorDirectory)
                .Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string Describe()
    {
        var sb = new StringBuilder();
        var mode = GetCurrentMode();

        sb.AppendLine($"  current mode : {mode?.ToString() ?? "unknown"}");
        sb.AppendLine($"  refresh rates: {string.Join(", ", AvailableRefreshRates().Select(r => r + " Hz"))}");
        sb.AppendLine($"  icc profiles : {InstalledColourProfiles().Count} installed");
        return sb.ToString();
    }
}
