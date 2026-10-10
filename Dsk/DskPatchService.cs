using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MZTools;

internal sealed record DskPatchRange(int PhysicalTrack, int DescriptorIndex, byte C, byte H, byte R, byte N,
    int Offset, string ExpectedOriginalHex, string ExpectedOriginalSha256, string ReplacementHex);
internal sealed record DskPatch(int Version, string SourceSha256, string TargetSha256,
    DskGeometrySignature SourceGeometry, DskGeometrySignature TargetGeometry, IReadOnlyList<DskPatchRange> Ranges);

internal sealed class DskPatchPreview
{
    private readonly byte[] image;
    internal DskPatchPreview(byte[] image, string sourceSha256, string report)
    { this.image = (byte[])image.Clone(); SourceSha256 = sourceSha256; Report = report; }
    internal byte[] ResultBytes => (byte[])image.Clone();
    internal string SourceSha256 { get; }
    internal string Report { get; }
}

internal static class DskPatchService
{
    internal const int MaximumJsonBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static DskPatch Export(byte[] sourceBytes, byte[] targetBytes)
    {
        var source = DskDocument.Open(sourceBytes); var target = DskDocument.Open(targetBytes);
        var sourceGeometry = DskGeometrySignature.From(source.Image);
        var targetGeometry = DskGeometrySignature.From(target.Image);
        if (sourceGeometry != targetGeometry || sourceBytes.Length != targetBytes.Length)
            throw new InvalidDataException("Sector patches require identical geometry, descriptor order and image length.");
        var ranges = new List<DskPatchRange>();
        for (int track = 0; track < source.Image.Tracks.Count; track++)
        {
            var left = source.Image.Tracks[track]; var right = target.Image.Tracks[track];
            if (left == null) continue;
            for (int descriptor = 0; descriptor < left.Sectors.Count; descriptor++)
            {
                var a = left.Sectors[descriptor]; var b = right!.Sectors[descriptor];
                int start = 0;
                while (start < a.Data.Length)
                {
                    while (start < a.Data.Length && a.Data[start] == b.Data[start]) start++;
                    if (start == a.Data.Length) break;
                    int end = start + 1;
                    while (end < a.Data.Length && a.Data[end] != b.Data[end]) end++;
                    byte[] expected = a.Data.AsSpan(start, end - start).ToArray();
                    ranges.Add(new(track, descriptor, a.Cylinder, a.Side, a.SectorId, a.SizeCode, start,
                        Convert.ToHexString(expected), DskHexEditService.Hash(expected), Convert.ToHexString(b.Data.AsSpan(start, end - start))));
                    start = end;
                }
            }
        }
        var patch = new DskPatch(1, DskHexEditService.Hash(sourceBytes), DskHexEditService.Hash(targetBytes), sourceGeometry, targetGeometry, ranges);
        // A sector-only export must reproduce every target byte, including container headers and padding.
        var preview = Preview(source, patch);
        if (!preview.ResultBytes.AsSpan().SequenceEqual(targetBytes))
            throw new InvalidDataException("The comparison includes header, padding or trailing-data changes unsupported by sector patches.");
        return patch;
    }

    internal static string ToJson(DskPatch patch) => JsonSerializer.Serialize(patch, JsonOptions);
    internal static DskPatch FromJson(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new InvalidDataException("Patch exceeds the 16 MiB limit.");
        try { return JsonSerializer.Deserialize<DskPatch>(json, JsonOptions) ?? throw new InvalidDataException("Empty patch."); }
        catch (JsonException exception) { throw new InvalidDataException("Invalid DSK patch JSON.", exception); }
    }

    internal static DskPatch Read(string path)
    {
        if (new FileInfo(path).Length > MaximumJsonBytes) throw new InvalidDataException("Patch exceeds the 16 MiB limit.");
        return FromJson(File.ReadAllText(path));
    }

    internal static DskPatchPreview Preview(DskDocument document, DskPatch patch)
    {
        if (patch.Version != 1 || patch.Ranges == null || patch.SourceGeometry == null || patch.TargetGeometry == null)
            throw new InvalidDataException("Unsupported patch version or missing fields.");
        byte[] original = document.Serialize();
        if (DskHexEditService.Hash(original) != patch.SourceSha256)
            throw new InvalidDataException("Source SHA-256 differs from the patch; nothing was changed.");
        var snapshot = document.Clone();
        if (DskGeometrySignature.From(snapshot.Image) != patch.SourceGeometry || patch.SourceGeometry != patch.TargetGeometry)
            throw new InvalidDataException("Patch geometry or descriptor order differs from the source.");
        byte[] result = (byte[])original.Clone();
        var intervals = new List<(int Start, int End)>();
        var report = new StringBuilder("MZTools DSK sector patch preview\n")
            .AppendLine($"Source SHA-256: {patch.SourceSha256}").AppendLine($"Target SHA-256: {patch.TargetSha256}")
            .AppendLine($"Geometry: {patch.SourceGeometry.Cylinders} × {patch.SourceGeometry.Sides}; descriptor SHA-256 {patch.SourceGeometry.DescriptorSha256}")
            .AppendLine($"Changed ranges: {patch.Ranges.Count}");
        foreach (var range in patch.Ranges)
        {
            if (range == null || (uint)range.PhysicalTrack >= snapshot.Image.Tracks.Count ||
                snapshot.Image.Tracks[range.PhysicalTrack] is not { } track || (uint)range.DescriptorIndex >= track.Sectors.Count)
                throw new InvalidDataException("Patch references an absent physical track/descriptor.");
            var sector = track.Sectors[range.DescriptorIndex];
            if (sector.Cylinder != range.C || sector.Side != range.H || sector.SectorId != range.R || sector.SizeCode != range.N)
                throw new InvalidDataException("Patch C/H/R/N does not match the addressed descriptor.");
            byte[] expected = Hex(range.ExpectedOriginalHex); byte[] replacement = Hex(range.ReplacementHex);
            if (expected.Length == 0 || expected.Length != replacement.Length || range.Offset < 0 || range.Offset > sector.Data.Length - expected.Length)
                throw new InvalidDataException("Patch byte range or replacement size is invalid.");
            if (DskHexEditService.Hash(expected) != range.ExpectedOriginalSha256 ||
                !sector.Data.AsSpan(range.Offset, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException("Patch expected original bytes/hash differ from the source.");
            int position = checked(track.FileOffset + DskImage.TrackHeaderSize + track.Sectors.Take(range.DescriptorIndex).Sum(s => s.Data.Length) + range.Offset);
            intervals.Add((position, position + expected.Length));
            replacement.CopyTo(result, position);
            report.AppendLine($"Track {range.PhysicalTrack}, descriptor {range.DescriptorIndex}, C/H/R/N={range.C}/{range.H}/{range.R}/{range.N}, bytes 0x{range.Offset:X}..0x{range.Offset + expected.Length - 1:X}")
                .AppendLine($"  {range.ExpectedOriginalHex} → {range.ReplacementHex}");
        }
        var ordered = intervals.OrderBy(i => i.Start).ToArray();
        for (int index = 1; index < ordered.Length; index++)
            if (ordered[index].Start < ordered[index - 1].End) throw new InvalidDataException("Overlapping patch ranges are not allowed.");
        if (DskHexEditService.Hash(result) != patch.TargetSha256)
            throw new InvalidDataException("Patched result SHA-256 differs from the exact target. Container/header changes cannot be exported as a sector patch.");
        var candidate = document.Reopen(result);
        if (!candidate.Image.Serialize().AsSpan().SequenceEqual(result))
            throw new InvalidDataException("Patched container cannot be serialized byte-preservingly.");
        candidate = document.Reopen(candidate.Image.Serialize());
        if (DskGeometrySignature.From(candidate.Image) != patch.TargetGeometry)
            throw new InvalidDataException("Patched geometry differs from the target signature.");
        var analysis = DskAnalyzer.Analyze(candidate);
        if (candidate.IsReadOnly || analysis.Errors != 0 ||
            (snapshot.FileSystem.Type is not (DskFileSystemType.Raw or DskFileSystemType.BootOnly) &&
             snapshot.FileSystem.Type != candidate.FileSystem.Type))
            throw new InvalidDataException("Patched result failed filesystem/Analyzer validation: " + analysis.Summary);
        report.AppendLine($"Filesystem: {snapshot.FileSystem.DisplayName} → {candidate.FileSystem.DisplayName}")
            .AppendLine(analysis.Summary).AppendLine("Apply replaces the current document. Saving is a separate operation.");
        return new(result, patch.SourceSha256, report.ToString());
    }

    internal static void Apply(DskDocument document, DskPatchPreview preview)
    {
        if (DskHexEditService.Hash(document.Serialize()) != preview.SourceSha256)
            throw new InvalidDataException("Document changed since preview. Preview the patch again.");
        if (!document.Serialize().AsSpan().SequenceEqual(preview.ResultBytes)) document.ReplaceContents(preview.ResultBytes);
    }

    private static byte[] Hex(string value)
    {
        if (value == null) throw new InvalidDataException("Missing patch bytes.");
        try { return Convert.FromHexString(value); }
        catch (FormatException exception) { throw new InvalidDataException("Invalid patch hexadecimal bytes.", exception); }
    }
}
