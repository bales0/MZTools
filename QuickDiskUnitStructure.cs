using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;

namespace MZTools;

internal static class QuickDiskUnitStructure
{
    internal static string DescribeBlock(QuickDiskHostBlock block, QdReadResult result)
    {
        byte[] bytes = block.Bytes;
        var text = new StringBuilder();
        text.AppendLine($"Host: {result.Analysis.Identification.DisplayName}; block #{block.Index}");
        text.AppendLine($"Sync start: cell {block.CellOffset}; framed record starts after 7 decoded sync bytes (cell {block.CellOffset + 112}).");
        text.AppendLine($"Length: {bytes.Length}/{block.ExpectedLength} decoded bytes; {(block.IsComplete ? "complete" : "truncated")}; CRC {(block.IntegrityValid ? "valid" : "invalid/unavailable")}");
        text.AppendLine($"CRC region: decoded +0x7, expected {block.ExpectedLength - 7} bytes including trailing CRC; poly 0x8005, init 0, reversed-byte feed.");
        if (result.PhysicalProfile is { } profile)
        {
            long windowCell = profile.WindowStart * 8L;
            text.AppendLine($"Start relative to switch window: {block.CellOffset - windowCell} bitcells.");
            if (profile.BitRate > 0) text.AppendLine(FormattableString.Invariant($"Track-relative start time: {block.CellOffset * 1000.0 / profile.BitRate:F3} ms (container bit rate {profile.BitRate})."));
        }
        if (result.Analysis.Content is RolandQuickDiskContent roland)
        {
            var previous = roland.Blocks.LastOrDefault(b => b.CellOffset < block.CellOffset);
            if (previous != null) text.AppendLine($"Gap from previous decoded block end: {block.CellOffset - previous.CellOffset - previous.CellLength} bitcells.");
            if (block.Index == 1 && roland.InternalName != null) text.AppendLine("Internal name: " + roland.InternalName);
            // Roland and SHARP share these frame/header fields. Describe only checked
            // ranges; do not fabricate a native MZF record or infer a different device.
            if (bytes.Length >= 11 && bytes[7] == 0xA5)
            {
                text.AppendLine($"Shared SHARP/Roland frame marker: +0x7=A5; kind/count +0x8=0x{bytes[8]:X2}.");
                if (block.Index == 0) text.AppendLine($"Declared following block count: {bytes[8]}.");
                else
                {
                    int declared = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(9, 2));
                    text.AppendLine($"Declared frame data length: {declared}; data start: decoded +0xB (cell {block.CellOffset + 176}).");
                    if (block.Index == 1 && bytes[8] == 0 && declared == 64 && bytes.Length >= 75)
                    {
                        text.AppendLine($"Shared header file type: 0x{bytes[11]:X2}; size field: {BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(31, 2))}.");
                        text.AppendLine($"Shared LOAD field: 0x{BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(33, 2)):X4}; EXEC: 0x{BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(35, 2)):X4}.");
                        string metadata = new string(bytes.AsSpan(37, 38).ToArray().Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray());
                        text.AppendLine("Header metadata bytes (+0x25..+0x4A, printable view): " + metadata);
                    }
                }
            }
        }
        if (result.Analysis.Content is AkaiQuickDiskContent akai)
        {
            bool marker = bytes.Length >= 19 && bytes.AsSpan(8, 11).SequenceEqual("S700 FORMAT"u8);
            text.AppendLine($"Subformat: {akai.Subformat}; S700 FORMAT marker at decoded +0x8: {(marker ? "present" : "absent")}.");
            text.AppendLine("Long-block data remains decoded binary data; audio/sample conversion is not performed.");
        }
        text.AppendLine("Export contains exactly the displayed decoded block, including decoded sync/framing/CRC bytes; it is not an image-format conversion.");
        return text.ToString();
    }
}
