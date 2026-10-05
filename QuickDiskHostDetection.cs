// Format logic adapted from HXCFE_QuickDisk_Toolkit (qd_roland.c, qd_akai.c,
// qd_mo5.c, trk_utils.c), Copyright (C) 2006-2022 Jean-François DEL NERO.
// GPL-2.0-or-later, compatible with this project's GPL-3.0-or-later license.
// Distributed WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal enum QuickDiskHostFormat { Unknown, SharpMz, Roland, AkaiUnknown, AkaiS612, AkaiS700, ThomsonMo5 }
internal enum QuickDiskIdentificationConfidence { Unknown, Possible, Probable, High }
internal sealed record QuickDiskOriginInfo(string FormatFamily, string? ProbableDevice,
    QuickDiskIdentificationConfidence FormatConfidence, QuickDiskIdentificationConfidence DeviceConfidence);
internal sealed record QuickDiskIdentification(QuickDiskHostFormat HostFormat,
    QuickDiskIdentificationConfidence Confidence, string DisplayName, bool IsNativeSharpMz,
    IReadOnlyList<string> Evidence, IReadOnlyList<string> Warnings, QuickDiskOriginInfo Origin, bool IsBlank = false);
internal abstract record QuickDiskDecodedContent;
internal sealed record SharpMzQuickDiskContent(IReadOnlyList<TapeRecord> Records) : QuickDiskDecodedContent;
internal sealed record QuickDiskHostBlock(int Index, int CellOffset, int ExpectedLength, byte[] Bytes,
    bool IntegrityValid, string Description)
{
    internal bool IsComplete => Bytes.Length == ExpectedLength;
    internal int CellLength => Bytes.Length * 16;
}
internal sealed record RolandQuickDiskContent(IReadOnlyList<QuickDiskHostBlock> Blocks, string? InternalName) : QuickDiskDecodedContent;
internal sealed record AkaiQuickDiskContent(QuickDiskHostFormat Subformat, QuickDiskHostBlock Block) : QuickDiskDecodedContent;
internal sealed record Mo5QdSector(int PhysicalSequence, int SectorId, int LogicalSector, int CellOffset,
    int CellLength, byte[] Data, bool HeaderValid, bool DataValid, bool Duplicate);
internal sealed record Mo5QuickDiskContent(IReadOnlyList<Mo5QdSector> Sectors) : QuickDiskDecodedContent
{
    internal byte[] ExportRaw()
    {
        if (Sectors.Count != 400 || Sectors.Any(s => !s.HeaderValid || !s.DataValid || s.Duplicate) ||
            Sectors.Select(s => s.LogicalSector).Distinct().Count() != 400)
            throw new InvalidDataException("Raw export requires all 400 unique, checksum-valid MO5 sectors.");
        var result = new byte[51200];
        foreach (var sector in Sectors) sector.Data.CopyTo(result, (sector.LogicalSector - 1) * 128);
        return result;
    }
}
internal sealed record UnknownQuickDiskContent : QuickDiskDecodedContent;
internal sealed record QuickDiskHostAnalysis(QuickDiskIdentification Identification, QuickDiskDecodedContent Content)
{
    internal IReadOnlyList<TapeRecord> Records => Content is SharpMzQuickDiskContent sharp ? sharp.Records : Array.Empty<TapeRecord>();
    internal static QuickDiskHostAnalysis Sharp(IReadOnlyList<TapeRecord> records) => new(
        new(QuickDiskHostFormat.SharpMz, QuickDiskIdentificationConfidence.High, "SHARP MZ QuickDisk", true,
            [$"Validated complete SHARP frame sequence and CRCs: {records.Count} files.",
                "Header file types: " + string.Join(", ", records.Select(r => $"0x{r.Header.MzfFtype:X2}"))], [],
            new("SHARP MZ QuickDisk", "MZ-800 / MZ-1F11 family", QuickDiskIdentificationConfidence.High, QuickDiskIdentificationConfidence.Probable)),
        new SharpMzQuickDiskContent(records));
}

// Raw QD track bytes store chronological bitcells LSB-first. The toolkit reverses
// those bytes before its MSB-first bit reader. Reading them LSB-first is equivalent.
// MO5 decoded bytes are MSB-first; Roland/Akai reverse each decoded byte, and reverse
// it again when feeding their non-reflected CRC-16 (poly 8005, initial 0000).
internal static class QuickDiskHostBitstream
{
    internal static byte Reverse(byte value)
    {
        value = (byte)((value >> 4) | (value << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
    }
    internal static int Bit(ReadOnlySpan<byte> track, int cell) => (track[cell / 8] >> (cell % 8)) & 1;
    internal static IReadOnlyList<int> Find(ReadOnlySpan<byte> track, ReadOnlySpan<byte> pattern, int limit = 1024)
    {
        var found = new List<int>();
        int length = checked(track.Length * 8), patternLength = pattern.Length * 8;
        if (pattern.IsEmpty) return found;
        // Rolling prefix keeps random tracks linear, even for long sync patterns.
        uint prefix = 0, wanted = 0;
        int prefixBits = Math.Min(32, patternLength);
        for (int i = 0; i < prefixBits; i++) wanted = (wanted << 1) | (uint)((pattern[i / 8] >> (7 - i % 8)) & 1);
        uint mask = prefixBits == 32 ? uint.MaxValue : (1u << prefixBits) - 1;
        for (int cell = 0; cell < length; cell++)
        {
            prefix = ((prefix << 1) | (uint)Bit(track, cell)) & mask;
            int start = cell - prefixBits + 1;
            if (start < 0 || start > length - patternLength || prefix != wanted) continue;
            int bit = prefixBits;
            for (; bit < patternLength; bit++)
                if (Bit(track, start + bit) != ((pattern[bit / 8] >> (7 - bit % 8)) & 1)) break;
            if (bit == patternLength) { found.Add(start); if (found.Count >= limit) break; }
        }
        return found;
    }
    internal static byte[] Decode(ReadOnlySpan<byte> track, int start, int count, bool reverse)
    {
        if (start < 0 || start > (long)track.Length * 8 || count < 0) throw new ArgumentOutOfRangeException(nameof(start));
        int available = (int)Math.Min(count, ((long)track.Length * 8 - start) / 16);
        var result = new byte[available];
        for (int index = 0; index < available; index++)
        {
            byte value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int cell = start + index * 16 + bit * 2;
                value = (byte)((value << 1) | (Bit(track, cell) == 0 ? Bit(track, cell + 1) : 0));
            }
            result[index] = reverse ? Reverse(value) : value;
        }
        return result;
    }
    internal static ushort Crc16(ReadOnlySpan<byte> bytes, bool reverse = false)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)((reverse ? Reverse(value) : value) << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x8005 : 0));
        }
        return crc;
    }
    internal static byte Sum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0; foreach (byte value in bytes) sum += value; return (byte)sum;
    }
}

internal static class QuickDiskHostDetector
{
    private sealed record Candidate(int Score, QuickDiskHostAnalysis Analysis);
    private static readonly byte[] LongSync = [0x94,0x4A,0x94,0x4A,0x94,0x4A,0x94,0x4A,0x94,0x4A,0x94,0x4A,0x94,0x4A,0x44,0x91];
    private static readonly byte[] Mo5HeaderSync = [0xA9,0x14,0xA9,0x14,0xA9,0x14,0xA9,0x14,0xA9,0x14,0x44,0x91];
    private static readonly byte[] Mo5DataSync = [0xA9,0x14,0xA9,0x14,0xA9,0x14,0xA9,0x14,0xA9,0x14,0x91,0x44];

    internal static QuickDiskHostAnalysis Detect(ReadOnlySpan<byte> track)
    {
        var candidates = new List<Candidate>();
        var warnings = new List<string>();
        var frames = QuickDiskMfmCodec.FindSharpFrames(track);
        if (frames.Count != 0)
        {
            try
            {
                var records = SharpQdFrameCodec.DecodeRecords(frames).Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2)).ToList();
                // A validated zero-file FNBLK is a formatted SHARP disk, not an
                // unformatted track. Complete competing host evidence may override it;
                // stray syncs or partially valid Roland blocks must not do so.
                candidates.Add(new(records.Count > 0 ? 120 : 100, QuickDiskHostAnalysis.Sharp(records)));
            }
            catch (InvalidDataException) { warnings.Add("SHARP-like frames were found, but no complete validated SHARP image was decoded."); }
        }
        else if (QuickDiskMfmCodec.ContainsPlausibleSharpFrame(track)) warnings.Add("Partial SHARP-like framing found; this is not enough to identify SHARP media.");
        var positions = QuickDiskHostBitstream.Find(track, LongSync);
        if (positions.Count > 0)
        {
            var roland = DetectRoland(track, positions); if (roland != null) candidates.Add(roland);
            var akai = DetectAkai(track, positions); if (akai != null) candidates.Add(akai);
            if (roland == null && akai == null) warnings.Add("Shared Roland/Akai sync found, but no expected block set/length with valid CRC was decoded (possibly damaged or truncated).");
        }
        var mo5 = DetectMo5(track); if (mo5 != null) candidates.Add(mo5);
        var ranked = candidates.OrderByDescending(c => c.Score).ToArray();
        if (ranked.Length == 0) return Unknown(QuickDiskPhysicalReader.IsBlankTrack(track) && warnings.Count == 0 && positions.Count == 0, [], warnings);
        if (ranked.Length > 1 && ranked[0].Score - ranked[1].Score < 15)
            return Unknown(false, ranked.Select(c => $"Candidate: {c.Analysis.Identification.DisplayName} ({c.Analysis.Identification.Confidence})").ToArray(),
                ["Conflicting host evidence; identification is ambiguous."]);
        var winner = ranked[0].Analysis;
        return winner with { Identification = winner.Identification with {
            Evidence = winner.Identification.Evidence.Concat(ranked.Select(c => $"Candidate: {c.Analysis.Identification.DisplayName}, score {c.Score}, confidence {c.Analysis.Identification.Confidence}")).ToArray(),
            Warnings = winner.Identification.Warnings.Concat(warnings).ToArray() } };
    }
    private static QuickDiskHostAnalysis Unknown(bool blank, IReadOnlyList<string> evidence, IReadOnlyList<string> warnings) => new(
        new(QuickDiskHostFormat.Unknown, QuickDiskIdentificationConfidence.Unknown, blank ? "Unformatted / blank QuickDisk" : "Unknown physical QuickDisk", false,
            evidence, warnings.Concat(blank ? Array.Empty<string>() : ["No supported host format could be identified."]).ToArray(),
            new("Unknown", null, QuickDiskIdentificationConfidence.Unknown, QuickDiskIdentificationConfidence.Unknown), blank), new UnknownQuickDiskContent());

    private static Candidate? DetectRoland(ReadOnlySpan<byte> track, IReadOnlyList<int> positions)
    {
        // Three disjoint blocks are required; one shared sync is not Roland evidence.
        int[] crcLengths = [4, 0x46, 0xC0A6];
        Candidate? best = null;
        foreach (int start in positions.Take(32))
        {
            var blocks = new List<QuickDiskHostBlock>();
            int next = start;
            for (int index = 0; index < 3; index++)
            {
                int position = positions.FirstOrDefault(p => p >= next, -1);
                if (position < 0) break;
                int size = 7 + crcLengths[index];
                byte[] bytes = QuickDiskHostBitstream.Decode(track, position, size, true);
                bool valid = bytes.Length == size && QuickDiskHostBitstream.Crc16(bytes.AsSpan(7), true) == 0;
                blocks.Add(new(index, position, size, bytes, valid, $"Roland block {index}: CRC {(valid ? "valid" : "invalid/truncated")}"));
                next = position + size * 16;
            }
            int validCount = blocks.Count(b => b.IntegrityValid);
            if (blocks.Count < 2 || validCount < 1) continue;
            bool complete = blocks.Count == 3 && validCount == 3;
            var confidence = complete ? QuickDiskIdentificationConfidence.High : QuickDiskIdentificationConfidence.Probable;
            string? name = null;
            if (blocks.Count > 1 && blocks[1].Bytes.Length >= 0x1C)
            {
                var nameBytes = blocks[1].Bytes.AsSpan(0xC, 16);
                int end = nameBytes.IndexOf((byte)13); if (end >= 0) nameBytes = nameBytes[..end];
                name = new string(nameBytes.ToArray().Select(b => b is >= 32 and <= 126 ? (char)b : '_').ToArray()).TrimEnd('_', ' ');
            }
            bool s10 = blocks.Count > 1 && blocks[1].IntegrityValid &&
                Encoding.ASCII.GetString(blocks[1].Bytes.AsSpan(7, blocks[1].Bytes.Length - 9)).Contains("Roland S10", StringComparison.OrdinalIgnoreCase);
            var evidence = blocks.Select(b => b.Description + $"; cell {b.CellOffset}; {b.Bytes.Length}/{b.ExpectedLength} decoded bytes").ToList();
            if (!string.IsNullOrWhiteSpace(name)) evidence.Add("Internal name: " + name);
            if (s10) evidence.Add("Internal CRC-validated header metadata: Roland S10.");
            var analysis = new QuickDiskHostAnalysis(new(QuickDiskHostFormat.Roland, confidence, "Roland QuickDisk", false,
                evidence, complete ? [] : ["Missing, truncated or CRC-invalid Roland blocks."],
                new("Roland QuickDisk", s10 ? "likely S-10" : null, confidence, s10 ? QuickDiskIdentificationConfidence.Probable : QuickDiskIdentificationConfidence.Unknown)),
                new RolandQuickDiskContent(blocks, name));
            // The supplied S-10 image also decodes as one valid SHARP MZF record.
            // Content-derived device metadata plus all three fixed CRC regions is
            // more specific than the shared framing. Without it SHARP keeps priority.
            var candidate = new Candidate(complete ? (s10 ? 160 : 110) : 45 + validCount * 10, analysis);
            if (best == null || candidate.Score > best.Score) best = candidate;
        }
        return best;
    }
    private static Candidate? DetectAkai(ReadOnlySpan<byte> track, IReadOnlyList<int> positions)
    {
        Candidate? best = null;
        foreach (int position in positions.Take(32))
        {
            byte[] bytes = QuickDiskHostBitstream.Decode(track, position, 7 + 0xFC23, true);
            bool s700 = bytes.Length >= 19 && bytes.AsSpan(8, 11).SequenceEqual("S700 FORMAT"u8);
            int crcLength = s700 ? 0xC0BD : 0xFC23, size = 7 + crcLength;
            bool valid = bytes.Length >= size && QuickDiskHostBitstream.Crc16(bytes.AsSpan(7, crcLength), true) == 0;
            // Marker without CRC is only partial evidence; absent marker without CRC is NOT S612.
            if (!valid && !s700) continue;
            if (bytes.Length > size) bytes = bytes[..size];
            var host = valid ? (s700 ? QuickDiskHostFormat.AkaiS700 : QuickDiskHostFormat.AkaiS612) : QuickDiskHostFormat.AkaiUnknown;
            var confidence = valid ? QuickDiskIdentificationConfidence.High : QuickDiskIdentificationConfidence.Possible;
            var block = new QuickDiskHostBlock(0, position, size, bytes, valid, $"Akai long block: CRC {(valid ? "valid" : "invalid/truncated")}");
            var evidence = new[] { "Shared Roland/Akai sync found.", $"Decoded block: {bytes.Length}/{size} bytes; CRC {(valid ? "valid" : "invalid/truncated")}",
                s700 ? "Explicit S700 FORMAT marker present." : "S612-size block with valid CRC; no S700 FORMAT marker." };
            var analysis = new QuickDiskHostAnalysis(new(host, confidence, host == QuickDiskHostFormat.AkaiS612 ? "Akai S612-format QuickDisk" : s700 ? "Akai S700-family QuickDisk" : "Possible Akai QuickDisk", false,
                evidence, valid ? [] : ["Akai block is truncated or CRC-invalid; device identification is not confirmed."],
                new("Akai QuickDisk", valid ? (s700 ? "S700-format" : "S612-format") : null, confidence, valid ? QuickDiskIdentificationConfidence.Probable : QuickDiskIdentificationConfidence.Unknown)),
                new AkaiQuickDiskContent(host, block));
            var candidate = new Candidate(valid ? 110 : 35, analysis);
            if (best == null || candidate.Score > best.Score) best = candidate;
        }
        return best;
    }
    internal static int Mo5LogicalSector(int physicalId)
    {
        if (physicalId is < 1 or > 400) throw new ArgumentOutOfRangeException(nameof(physicalId));
        int index = physicalId - 1;
        if (index >= 384) return new[] { 1, 9, 5, 13 }[index % 4] + (index - 384) / 4;
        int group = index / 64, lane = index % 4, within = index % 64 / 4;
        int[] starts = group == 0 ? [321,33,225,129] : group == 5 ? [17,241,145,49] : [321 + group * 16,321 - group * 16,225 - group * 16,129 - group * 16];
        return starts[lane] + within;
    }
    private static Candidate? DetectMo5(ReadOnlySpan<byte> track)
    {
        var headers = QuickDiskHostBitstream.Find(track, Mo5HeaderSync);
        if (headers.Count < 3) return null;
        var dataPositions = QuickDiskHostBitstream.Find(track, Mo5DataSync);
        var sectors = new List<Mo5QdSector>();
        foreach (int sync in headers)
        {
            int headerStart = sync + 80;
            byte[] header = QuickDiskHostBitstream.Decode(track, headerStart, 4, false);
            if (header.Length != 4 || header[0] != 0xA5) continue;
            int id = (header[1] << 8) | header[2]; if (id is < 1 or > 400) continue;
            bool headerValid = QuickDiskHostBitstream.Sum(header.AsSpan(0, 3)) == header[3];
            int dataSync = dataPositions.FirstOrDefault(p => p >= headerStart + 64 && p - headerStart < 1024, -1);
            int dataStart = dataSync >= 0 ? dataSync + 80 : -1;
            byte[] dataRecord = dataStart >= 0 ? QuickDiskHostBitstream.Decode(track, dataStart, 130, false) : [];
            bool dataValid = dataRecord.Length == 130 && dataRecord[0] == 0x5A && QuickDiskHostBitstream.Sum(dataRecord.AsSpan(0, 129)) == dataRecord[129];
            byte[] payload = dataRecord.Length == 130 ? dataRecord.AsSpan(1, 128).ToArray() : [];
            int end = dataStart >= 0 ? dataStart + dataRecord.Length * 16 : headerStart + 64;
            sectors.Add(new(sectors.Count + 1, id, Mo5LogicalSector(id), sync, end - sync, payload, headerValid, dataValid, false));
        }
        var duplicateIds = sectors.GroupBy(s => s.SectorId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        sectors = sectors.Select(s => s with { Duplicate = duplicateIds.Contains(s.SectorId) }).ToList();
        int valid = sectors.Count(s => s.HeaderValid && s.DataValid && !s.Duplicate);
        if (valid < 3) return null;
        bool complete = valid == 400 && sectors.Count == 400;
        var confidence = complete ? QuickDiskIdentificationConfidence.High : valid >= 20 ? QuickDiskIdentificationConfidence.Probable : QuickDiskIdentificationConfidence.Possible;
        var warnings = new List<string>();
        if (!complete) warnings.Add($"Incomplete or damaged MO5 sector set: {valid}/400 unique checksum-valid sectors.");
        if (duplicateIds.Count > 0) warnings.Add("Duplicate MO5 sector IDs: " + string.Join(", ", duplicateIds.Order()));
        foreach (var sector in sectors.Where(s => !s.HeaderValid || !s.DataValid)) warnings.Add($"Sector {sector.SectorId}: header checksum {(sector.HeaderValid ? "valid" : "invalid")}; data checksum {(sector.DataValid ? "valid" : "invalid/missing/truncated")}");
        return new(complete ? 110 : valid >= 20 ? 80 : 35,
            new(new(QuickDiskHostFormat.ThomsonMo5, confidence, "Thomson MO5 QuickDisk", false,
                [$"MO5 A5/5A record structure; {sectors.Count} sectors; {valid} unique sectors with valid header/data checksums."], warnings,
                new("Thomson MO5 QuickDisk", "MO5 / CQ90-028 family", confidence, QuickDiskIdentificationConfidence.Probable)), new Mo5QuickDiskContent(sectors)));
    }
}

internal static class QuickDiskAnalysisReport
{
    internal static string Build(QdReadResult result)
    {
        var identification = result.Analysis.Identification;
        var text = new StringBuilder("MZTools QuickDisk analysis\n");
        text.AppendLine($"Container: {result.Format}");
        text.AppendLine($"Host detection: {identification.DisplayName}");
        text.AppendLine($"Confidence: {identification.Confidence}");
        text.AppendLine($"Native SHARP MZ: {(identification.IsNativeSharpMz ? "Yes" : "No / not detected")}");
        text.AppendLine($"Format family: {identification.Origin.FormatFamily}");
        text.AppendLine($"Probable device: {identification.Origin.ProbableDevice ?? "Unknown"} ({identification.Origin.DeviceConfidence})");
        if (result.PhysicalProfile is { } profile)
            text.AppendLine($"Tracks/sides: 1/1\nBit rate: {profile.BitRate}\nTrack length: {profile.TrackLength} bytes\nSwitch window: {profile.WindowStart}..{profile.WindowEnd} bytes\nTrack file offset: 0x{profile.DataOffset:X}");
        text.AppendLine("Decoded units: " + (result.Analysis.Content switch
        {
            SharpMzQuickDiskContent sharp => $"{sharp.Records.Count} SHARP files",
            RolandQuickDiskContent roland => $"{roland.Blocks.Count} Roland blocks; {roland.Blocks.Count(b => b.IntegrityValid)} CRC-valid",
            AkaiQuickDiskContent akai => $"1 Akai block; CRC {(akai.Block.IntegrityValid ? "valid" : "invalid/truncated")}",
            Mo5QuickDiskContent mo5 => $"{mo5.Sectors.Count}/400 MO5 sectors; {mo5.Sectors.Count(s => s.HeaderValid && s.DataValid && !s.Duplicate)} unique checksum-valid; {mo5.Sectors.Sum(s => s.Data.Length)} decoded data bytes",
            _ => "No supported decoded units"
        }));
        text.AppendLine("Evidence:"); foreach (string value in identification.Evidence) text.AppendLine("  " + value);
        text.AppendLine("Warnings:"); foreach (string value in identification.Warnings) text.AppendLine("  " + value);
        if (!identification.IsNativeSharpMz) text.AppendLine("This is not an identified native SHARP MZ-800 QuickDisk format. Non-SHARP/unknown data is read-only.");
        return text.ToString();
    }
}
