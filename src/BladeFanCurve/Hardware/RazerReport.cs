using System.Text;

namespace BladeFanCurve.Hardware;

/// <summary>
/// Status byte returned by the device in the first byte of a reply.
/// </summary>
public enum RazerStatus : byte
{
    NewCommand = 0x00,
    Busy = 0x01,
    Successful = 0x02,
    Failure = 0x03,
    NoResponseTimeout = 0x04,
    NotSupported = 0x05,
}

/// <summary>
/// The 90-byte control report every Razer device speaks over HID feature reports.
/// Layout is identical to OpenRazer's <c>struct razer_report</c>:
///
///   [0]      status
///   [1]      transaction id
///   [2..3]   remaining packets (big endian)
///   [4]      protocol type
///   [5]      data size
///   [6]      command class
///   [7]      command id
///   [8..87]  arguments (80 bytes)
///   [88]     crc  (XOR of bytes 2..87)
///   [89]     reserved
///
/// On the wire the buffer is prefixed with a report id of 0x00, so the HID
/// feature report is 91 bytes long.
/// </summary>
public sealed class RazerReport
{
    public const int Size = 90;
    public const int ArgumentCount = 80;
    public const int WireSize = Size + 1; // + report id

    public byte Status;
    public byte TransactionId;
    public ushort RemainingPackets;
    public byte ProtocolType;
    public byte DataSize;
    public byte CommandClass;
    public byte CommandId;
    public byte Crc;
    public byte Reserved;
    public readonly byte[] Arguments = new byte[ArgumentCount];

    public RazerStatus StatusCode => (RazerStatus)Status;
    public bool IsSuccess => Status == (byte)RazerStatus.Successful;

    public static RazerReport Create(byte transactionId, byte commandClass, byte commandId, byte dataSize,
        params byte[] arguments)
    {
        var report = new RazerReport
        {
            Status = (byte)RazerStatus.NewCommand,
            TransactionId = transactionId,
            RemainingPackets = 0,
            ProtocolType = 0x00,
            DataSize = dataSize,
            CommandClass = commandClass,
            CommandId = commandId,
        };

        if (arguments.Length > ArgumentCount)
            throw new ArgumentException($"At most {ArgumentCount} argument bytes are allowed.", nameof(arguments));

        Array.Copy(arguments, report.Arguments, arguments.Length);
        return report;
    }

    /// <summary>Serialises to the 90-byte struct, computing the CRC.</summary>
    public byte[] ToBytes()
    {
        var b = new byte[Size];
        b[0] = Status;
        b[1] = TransactionId;
        b[2] = (byte)(RemainingPackets >> 8);   // big endian
        b[3] = (byte)(RemainingPackets & 0xFF);
        b[4] = ProtocolType;
        b[5] = DataSize;
        b[6] = CommandClass;
        b[7] = CommandId;
        Array.Copy(Arguments, 0, b, 8, ArgumentCount);
        b[88] = ComputeCrc(b);
        b[89] = Reserved;
        return b;
    }

    /// <summary>Serialises to the 91-byte HID feature report (report id 0x00 first).</summary>
    public byte[] ToWireBytes(int bufferLength = WireSize)
    {
        if (bufferLength < WireSize)
            throw new ArgumentOutOfRangeException(nameof(bufferLength),
                $"Feature report buffer must be at least {WireSize} bytes.");

        var wire = new byte[bufferLength];
        wire[0] = 0x00; // report id
        Array.Copy(ToBytes(), 0, wire, 1, Size);
        return wire;
    }

    public static RazerReport FromBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size)
            throw new ArgumentException($"Expected at least {Size} bytes, got {b.Length}.", nameof(b));

        var r = new RazerReport
        {
            Status = b[0],
            TransactionId = b[1],
            RemainingPackets = (ushort)((b[2] << 8) | b[3]),
            ProtocolType = b[4],
            DataSize = b[5],
            CommandClass = b[6],
            CommandId = b[7],
            Crc = b[88],
            Reserved = b[89],
        };
        b.Slice(8, ArgumentCount).CopyTo(r.Arguments);
        return r;
    }

    /// <summary>Parses a 91-byte feature report (skipping the leading report id).</summary>
    public static RazerReport FromWireBytes(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < WireSize)
            throw new ArgumentException($"Expected at least {WireSize} bytes, got {wire.Length}.", nameof(wire));
        return FromBytes(wire.Slice(1, Size));
    }

    /// <summary>XOR of bytes 2..87 of the 90-byte struct.</summary>
    public static byte ComputeCrc(ReadOnlySpan<byte> report)
    {
        byte crc = 0;
        for (var i = 2; i < 88; i++)
            crc ^= report[i];
        return crc;
    }

    public bool CrcValid()
    {
        var b = new byte[Size];
        b[0] = Status;
        b[1] = TransactionId;
        b[2] = (byte)(RemainingPackets >> 8);
        b[3] = (byte)(RemainingPackets & 0xFF);
        b[4] = ProtocolType;
        b[5] = DataSize;
        b[6] = CommandClass;
        b[7] = CommandId;
        Array.Copy(Arguments, 0, b, 8, ArgumentCount);
        return ComputeCrc(b) == Crc;
    }

    /// <summary>True when the reply echoes the class/id of the request we sent.</summary>
    public bool Echoes(RazerReport request) =>
        CommandClass == request.CommandClass && CommandId == request.CommandId;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"status=0x{Status:X2}({StatusCode}) txn=0x{TransactionId:X2} ");
        sb.Append($"class=0x{CommandClass:X2} id=0x{CommandId:X2} size={DataSize} args=[");
        var n = Math.Min(Math.Max(DataSize, (byte)4), (byte)8);
        for (var i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append($"{Arguments[i]:X2}");
        }
        sb.Append(']');
        return sb.ToString();
    }
}
