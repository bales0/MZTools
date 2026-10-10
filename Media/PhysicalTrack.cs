using System;
using System.Collections.Generic;

namespace MZTools;

[Flags]
internal enum MediaCapabilities { None = 0, Detect = 1, Read = 2, Write = 4, SectorModel = 8, PhysicalTrackModel = 16, PreserveWeakBits = 32, PreserveTiming = 64 }
internal sealed record MediaDetection(bool Matches, string Variant);
internal interface IMediaFormat
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<string> Extensions { get; }
    MediaCapabilities Capabilities { get; }
    MediaDetection Identify(ReadOnlySpan<byte> source);
}

internal enum PhysicalEncoding { Mfm, Fm, Unknown }
internal class PhysicalDiskImage
{
    internal int Cylinders { get; init; }
    internal int Sides { get; init; }
    internal IReadOnlyList<PhysicalTrack> Tracks { get; init; } = [];
}
internal sealed record PhysicalTiming(long CellOffset, int Divisor)
{
    internal double BitRate => 18_000_000.0 / Divisor;
}
internal sealed record PhysicalSector(byte C, byte H, byte R, byte N, byte[] Data, long HeaderCell, long DataCell,
    bool HeaderCrcValid, bool DataCrcValid, byte AddressMark, byte DataMark, byte[] WeakBitMask);
internal sealed class PhysicalTrack
{
    internal int Cylinder { get; init; }
    internal int Side { get; init; }
    internal double? Rpm { get; init; }
    internal int? BitRate { get; init; }
    internal PhysicalEncoding Encoding { get; init; }
    internal byte? ContainerEncoding { get; init; }
    internal required byte[] PackedBitCells { get; init; }
    internal required long BitCellCount { get; init; }
    internal byte[] WeakBitMask { get; init; } = [];
    internal IReadOnlyList<PhysicalTiming> Timing { get; init; } = [];
    internal IReadOnlyList<long> IndexCells { get; init; } = [];
    internal IReadOnlyList<PhysicalSector> Sectors { get; set; } = [];
}

// Shared LSB-packed physical storage, also used by the QuickDisk MFM codec.
internal static class PackedBitCells
{
    internal static bool Get(ReadOnlySpan<byte> bytes, int cell) => (bytes[cell >> 3] & (1 << (cell & 7))) != 0;
    internal static void Set(Span<byte> bytes, int cell, bool value)
    {
        if (value) bytes[cell >> 3] |= (byte)(1 << (cell & 7));
        else bytes[cell >> 3] &= (byte)~(1 << (cell & 7));
    }
    internal static byte Reverse(byte value)
    {
        int result = 0; for (int i = 0; i < 8; i++) result = (result << 1) | ((value >> i) & 1); return (byte)result;
    }
}
