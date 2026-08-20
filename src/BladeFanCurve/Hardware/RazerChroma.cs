using System.Text;

namespace BladeFanCurve.Hardware;

/// <summary>A single keyboard LED colour. Kept as bytes because that is what goes on the wire.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);

    /// <param name="h">Hue in degrees; wrapped automatically.</param>
    /// <param name="s">0..1</param>
    /// <param name="v">0..1</param>
    public static RgbColor FromHsv(double h, double s = 1.0, double v = 1.0)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;

        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new RgbColor(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    public RgbColor Scale(double factor) =>
        new(Byte(R / 255.0 * factor), Byte(G / 255.0 * factor), Byte(B / 255.0 * factor));

    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbColor(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    public static RgbColor Max(RgbColor a, RgbColor b) =>
        new(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static RgbColor FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Black;
        var s = hex.TrimStart('#');
        if (s.Length != 6) return Black;
        try
        {
            return new RgbColor(
                Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch
        {
            return Black;
        }
    }
}

/// <summary>Which of the two Chroma command families this keyboard answers on.</summary>
public enum ChromaFamily
{
    Unknown,
    /// <summary>Command class 0x0F. What the 2024 Blades use.</summary>
    Extended,
    /// <summary>Command class 0x03. Older devices.</summary>
    Standard,
}

public enum WaveDirection : byte
{
    Right = 0x01,
    Left = 0x02,
}

/// <summary>
/// Keyboard lighting over the same HID feature-report channel as fan control.
///
/// Two command families exist and which one a device speaks is not discoverable from
/// its product id alone, so the family is probed once with a read-only "get
/// brightness" and remembered.
///
/// Extended (class 0x0F, the 2024 Blades):
///   effect        id 0x02  args [varstore, ledId, effectId, ...]
///   brightness    id 0x04  args [varstore, ledId, level]
///   frame row     id 0x03  args [0x00, 0x00, row, startCol, stopCol, rgb...]
///
/// Standard (class 0x03, older):
///   effect        id 0x0A  args [effectId, ...]
///   brightness    id 0x03  args [varstore, ledId, level]
///   frame row     id 0x0B  args [0xFF, row, startCol, stopCol, rgb...]
///
/// Byte layouts follow OpenRazer's razerchromacommon.c.
/// </summary>
public sealed class RazerChroma
{
    // Storage targets. VARSTORE survives a reboot, NOSTORE does not.
    private const byte NoStore = 0x00;
    private const byte VarStore = 0x01;

    // LED ids.
    private const byte ZeroLed = 0x00;
    private const byte BacklightLed = 0x05;

    private const byte ClassExtended = 0x0F;
    private const byte ClassStandard = 0x03;

    // Extended command ids.
    private const byte ExtEffect = 0x02;
    private const byte ExtFrameRow = 0x03;
    private const byte ExtBrightness = 0x04;
    private const byte ExtGetBrightness = 0x84;

    // Standard command ids.
    private const byte StdEffect = 0x0A;
    private const byte StdFrameRow = 0x0B;
    private const byte StdBrightness = 0x03;
    private const byte StdGetBrightness = 0x83;

    // Extended effect ids.
    private const byte FxNone = 0x00;
    private const byte FxStatic = 0x01;
    private const byte FxBreathing = 0x02;
    private const byte FxSpectrum = 0x03;
    private const byte FxWave = 0x04;
    private const byte FxReactive = 0x05;
    private const byte FxStarlight = 0x07;
    private const byte FxCustomFrame = 0x08;

    // Standard effect ids (MATRIX_EFFECT_* in OpenRazer).
    private const byte StdFxOff = 0x00;
    private const byte StdFxWave = 0x01;
    private const byte StdFxReactive = 0x02;
    private const byte StdFxBreathing = 0x03;
    private const byte StdFxSpectrum = 0x04;
    private const byte StdFxCustomFrame = 0x05;
    private const byte StdFxStatic = 0x06;
    private const byte StdFxStarlight = 0x19;

    /// <summary>Blade keyboards are addressed as a 6 x 16 grid regardless of physical key count.</summary>
    public const int Rows = 6;
    public const int Columns = 16;

    private readonly RazerLaptopDevice _device;
    private byte _ledId = BacklightLed;

    public ChromaFamily Family { get; private set; } = ChromaFamily.Unknown;
    public byte TransactionId { get; set; }
    public bool IsReady => Family != ChromaFamily.Unknown;

    public RazerChroma(RazerLaptopDevice device)
    {
        _device = device;
        TransactionId = device.TransactionId;
    }

    // ------------------------------------------------------------------ probe

    /// <summary>
    /// Works out which command family and LED id this keyboard answers on, using a
    /// read-only brightness query so nothing visible changes.
    /// </summary>
    public bool Probe(out string log)
    {
        var sb = new StringBuilder();

        // Blade lighting is documented on transaction id 0x1F and 0x3F. Start with
        // whatever fan control already proved works on this machine.
        var transactions = new List<byte> { TransactionId };
        foreach (var t in new byte[] { 0x1F, 0x3F, 0x08, 0xFF })
            if (!transactions.Contains(t)) transactions.Add(t);

        foreach (var txn in transactions)
        foreach (var (family, cls, cmd) in new[]
                 {
                     (ChromaFamily.Extended, ClassExtended, ExtGetBrightness),
                     (ChromaFamily.Standard, ClassStandard, StdGetBrightness),
                 })
        foreach (var led in new[] { BacklightLed, ZeroLed })
        {
            var request = RazerReport.Create(txn, cls, cmd, 0x03, VarStore, led);
            if (!_device.TrySend(request, out var reply) || reply == null) continue;

            Family = family;
            _ledId = led;
            TransactionId = txn;
            sb.AppendLine($"Chroma: {family} family, led id 0x{led:X2}, txn 0x{txn:X2}, " +
                          $"brightness {reply.Arguments[2]}");
            log = sb.ToString();
            return true;
        }

        sb.AppendLine("Chroma: no reply to either command family; keyboard lighting unavailable.");
        log = sb.ToString();
        return false;
    }

    // ------------------------------------------------------- hardware effects

    /// <summary>Turns the backlight off at the controller.</summary>
    public bool Off() => Family == ChromaFamily.Extended
        ? Effect(0x06, FxNone)
        : Standard(0x01, StdFxOff);

    public bool Static(RgbColor c) => Family == ChromaFamily.Extended
        ? Effect(0x09, FxStatic, args => { args[5] = 0x01; Write(args, 6, c); })
        : Standard(0x04, StdFxStatic, args => Write(args, 1, c));

    public bool BreathingRandom() => Family == ChromaFamily.Extended
        ? Effect(0x06, FxBreathing)
        : Standard(0x08, StdFxBreathing, args => args[1] = 0x03);

    public bool BreathingSingle(RgbColor c) => Family == ChromaFamily.Extended
        ? Effect(0x09, FxBreathing, args => { args[3] = 0x01; args[5] = 0x01; Write(args, 6, c); })
        : Standard(0x08, StdFxBreathing, args => { args[1] = 0x01; Write(args, 2, c); });

    public bool BreathingDual(RgbColor a, RgbColor b) => Family == ChromaFamily.Extended
        ? Effect(0x0C, FxBreathing, args =>
        {
            args[3] = 0x02;
            args[5] = 0x02;
            Write(args, 6, a);
            Write(args, 9, b);
        })
        : Standard(0x08, StdFxBreathing, args =>
        {
            args[1] = 0x02;
            Write(args, 2, a);
            Write(args, 5, b);
        });

    public bool Spectrum() => Family == ChromaFamily.Extended
        ? Effect(0x06, FxSpectrum)
        : Standard(0x01, StdFxSpectrum);

    public bool Wave(WaveDirection direction) => Family == ChromaFamily.Extended
        ? Effect(0x06, FxWave, args =>
        {
            args[3] = (byte)direction;
            args[4] = 0x28; // speed, as OpenRazer sends it
        })
        : Standard(0x02, StdFxWave, args => args[1] = (byte)direction);

    /// <param name="speed">1 (fastest) to 4 (slowest).</param>
    public bool Reactive(RgbColor c, byte speed) => Family == ChromaFamily.Extended
        ? Effect(0x09, FxReactive, args =>
        {
            args[4] = Math.Clamp(speed, (byte)1, (byte)4);
            args[5] = 0x01;
            Write(args, 6, c);
        })
        : Standard(0x05, StdFxReactive, args =>
        {
            args[1] = Math.Clamp(speed, (byte)1, (byte)4);
            Write(args, 2, c);
        });

    /// <param name="speed">1 (fastest) to 3 (slowest).</param>
    public bool StarlightRandom(byte speed) => Family == ChromaFamily.Extended
        ? Effect(0x06, FxStarlight, args => args[4] = Math.Clamp(speed, (byte)1, (byte)3))
        : Standard(0x03, StdFxStarlight, args => { args[1] = 0x03; args[2] = speed; });

    public bool StarlightSingle(RgbColor c, byte speed) => Family == ChromaFamily.Extended
        ? Effect(0x09, FxStarlight, args =>
        {
            args[4] = Math.Clamp(speed, (byte)1, (byte)3);
            args[5] = 0x01;
            Write(args, 6, c);
        })
        : Standard(0x06, StdFxStarlight, args =>
        {
            args[1] = 0x01;
            args[2] = speed;
            Write(args, 3, c);
        });

    public bool StarlightDual(RgbColor a, RgbColor b, byte speed) => Family == ChromaFamily.Extended
        ? Effect(0x0C, FxStarlight, args =>
        {
            args[4] = Math.Clamp(speed, (byte)1, (byte)3);
            args[5] = 0x02;
            Write(args, 6, a);
            Write(args, 9, b);
        })
        : Standard(0x09, StdFxStarlight, args =>
        {
            args[1] = 0x02;
            args[2] = speed;
            Write(args, 3, a);
            Write(args, 6, b);
        });

    // ------------------------------------------------------------- brightness

    /// <param name="level">0-255.</param>
    public bool SetBrightness(byte level) =>
        _device.TrySend(BuildBrightness(Family, TransactionId, _ledId, level), out _);

    public bool TryGetBrightness(out byte level)
    {
        level = 0;
        var (cls, cmd) = Family == ChromaFamily.Extended
            ? (ClassExtended, ExtGetBrightness)
            : (ClassStandard, StdGetBrightness);

        var request = RazerReport.Create(TransactionId, cls, cmd, 0x03, VarStore, _ledId);
        if (!_device.TrySend(request, out var reply) || reply == null) return false;

        level = reply.Arguments[2];
        return true;
    }

    // ----------------------------------------------------------- custom frame

    /// <summary>
    /// Streams a whole 6 x 16 frame: one write per row, then a latch telling the
    /// controller to display what was just uploaded. Row writes are fire-and-forget,
    /// because waiting for seven acknowledgements per frame caps the frame rate at
    /// about five per second.
    /// </summary>
    public bool SetFrame(RgbColor[,] frame)
    {
        if (!IsReady) return false;

        for (var row = 0; row < Rows; row++)
            if (!SetRow(row, frame))
                return false;

        return LatchCustomFrame();
    }

    // Extended puts two leading zero bytes before row/start/stop; standard uses a
    // single 0xFF marker. Pixel data follows immediately after in both cases.
    private bool SetRow(int row, RgbColor[,] frame) =>
        _device.TrySendNoReply(BuildFrameRow(Family, TransactionId, row, frame));

    private bool LatchCustomFrame()
    {
        var request = Family == ChromaFamily.Extended
            ? RazerReport.Create(TransactionId, ClassExtended, ExtEffect, 0x0C, NoStore, ZeroLed, FxCustomFrame)
            : RazerReport.Create(TransactionId, ClassStandard, StdEffect, 0x02, StdFxCustomFrame, NoStore);

        return _device.TrySendNoReply(request);
    }

    // ---------------------------------------------------------------- helpers

    private bool Effect(byte size, byte effectId, Action<byte[]>? fill = null) =>
        _device.TrySend(BuildExtendedEffect(TransactionId, _ledId, size, effectId, fill), out _);

    private bool Standard(byte size, byte effectId, Action<byte[]>? fill = null) =>
        _device.TrySend(BuildStandardEffect(TransactionId, size, effectId, fill), out _);

    // The report builders are static and separate from the sending so that the byte
    // layouts can be verified against OpenRazer's without a keyboard attached.

    internal static RazerReport BuildExtendedEffect(byte txn, byte ledId, byte size, byte effectId,
        Action<byte[]>? fill = null)
    {
        var args = new byte[16];
        args[0] = VarStore;
        args[1] = ledId;
        args[2] = effectId;
        fill?.Invoke(args);
        return RazerReport.Create(txn, ClassExtended, ExtEffect, size, args);
    }

    internal static RazerReport BuildStandardEffect(byte txn, byte size, byte effectId,
        Action<byte[]>? fill = null)
    {
        var args = new byte[16];
        args[0] = effectId;
        fill?.Invoke(args);
        return RazerReport.Create(txn, ClassStandard, StdEffect, size, args);
    }

    internal static RazerReport BuildBrightness(ChromaFamily family, byte txn, byte ledId, byte level)
    {
        var (cls, cmd) = family == ChromaFamily.Extended
            ? (ClassExtended, ExtBrightness)
            : (ClassStandard, StdBrightness);
        return RazerReport.Create(txn, cls, cmd, 0x03, VarStore, ledId, level);
    }

    internal static RazerReport BuildFrameRow(ChromaFamily family, byte txn, int row, RgbColor[,] frame)
    {
        var headerLength = family == ChromaFamily.Extended ? 5 : 4;
        var args = new byte[headerLength + Columns * 3];

        if (family == ChromaFamily.Extended)
        {
            args[0] = 0x00;
            args[1] = 0x00;
            args[2] = (byte)row;
            args[3] = 0x00;
            args[4] = Columns - 1;
        }
        else
        {
            args[0] = 0xFF;
            args[1] = (byte)row;
            args[2] = 0x00;
            args[3] = Columns - 1;
        }

        for (var col = 0; col < Columns; col++)
        {
            var offset = headerLength + col * 3;
            var c = frame[row, col];
            args[offset] = c.R;
            args[offset + 1] = c.G;
            args[offset + 2] = c.B;
        }

        var (cls, cmd, size) = family == ChromaFamily.Extended
            ? (ClassExtended, ExtFrameRow, (byte)0x47)
            : (ClassStandard, StdFrameRow, (byte)0x46);

        return RazerReport.Create(txn, cls, cmd, size, args);
    }

    private static void Write(byte[] args, int offset, RgbColor c)
    {
        args[offset] = c.R;
        args[offset + 1] = c.G;
        args[offset + 2] = c.B;
    }
}
