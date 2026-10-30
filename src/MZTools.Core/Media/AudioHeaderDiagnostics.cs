using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal sealed record AudioHeaderRejection(long Sample, int Channel, bool Inverted, string Detector,
    string Reason, ushort RecordedChecksum, ushort CalculatedChecksum, string HeaderSha256);

internal sealed class AudioHeaderDiagnostics
{
    private const int Limit = 128;
    private readonly List<AudioHeaderRejection> items = [];
    internal IReadOnlyList<AudioHeaderRejection> Items => items;
    internal int Total { get; private set; }
    internal int Omitted => Total - items.Count;
    internal void Reject(AudioHeaderRejection value) { Total++; if (items.Count < Limit) items.Add(value); }
    internal string Explain(string fallback) => Total == 0 ? fallback :
        $"{Total} checksum-valid header decode(s) were rejected by header validation. " +
        (items.FirstOrDefault()?.Reason ?? "See rejected-header diagnostics.");
}

internal static class AudioHeaderValidation
{
    internal static string? RejectionReason(byte[] header)
    {
        if (header.Length != 128) return "An MZF header must contain exactly 128 bytes.";
        if (header[0] == 0) return "MZF file type is zero (undefined).";
        // Names may terminate before byte 17. Following bytes belong to the
        // original header and can contain spaces or historical memory contents.
        return header.AsSpan(1, 17).IndexOfAny((byte)0x0D, (byte)0x00) < 0
            ? "The 17-byte MZF name field contains no CR or zero terminator." : null;
    }
}
