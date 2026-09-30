using System.Buffers.Binary;

namespace ExHyperV.Models;

// A separate, guest-to-host status pipe: never inject these bytes into USB/IP.
internal sealed class UsbGuestHealth
{
    public const int RecordBytes = 32;
    public const uint Magic = 0x48565845; // EXVH, little endian
    public const long FreshMilliseconds = 5000;
    private sealed record Report(uint State, uint Port, uint Problem, uint Sequence, long Received);
    private Report? _report;

    public bool Update(ReadOnlySpan<byte> bytes, long now)
    {
        if (bytes.Length != RecordBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 1) return false;
        uint state = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint port = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint problem = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        var previous = Volatile.Read(ref _report);
        if (state > 2 || port > 255 || (state == 1 && (port == 0 || problem != 0)) ||
            (previous != null && unchecked((int)(sequence - previous.Sequence)) <= 0)) return false;
        Volatile.Write(ref _report, new(state, port, problem, sequence, now));
        return true;
    }

    public bool IsReady(long now)
    {
        var report = Volatile.Read(ref _report);
        return report is { State: 1 } && now - report.Received <= FreshMilliseconds;
    }

    public bool HasError(long now)
    {
        var report = Volatile.Read(ref _report);
        return report != null && (report.State == 2 || now - report.Received > FreshMilliseconds);
    }

    public bool IsExpired(long now) => Volatile.Read(ref _report) is { } report &&
        now - report.Received > 15000;
}
