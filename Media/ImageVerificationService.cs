using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MZTools;

internal sealed record ImageVerificationResult(DskIssueSeverity Severity, string Sha256, string Report);

internal static class ImageVerificationService
{
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static ImageVerificationResult Verify(byte[] bytes, DskDocument? interpretation = null)
    {
        string hash = Hash(bytes);
        var report = new StringBuilder($"Verify Image — read-only\nSHA-256: {hash}\nSize: {bytes.Length} B\n");
        try
        {
            if (bytes.Length >= 8 && (bytes.AsSpan(0, 8).SequenceEqual("HXCPICFE"u8) || bytes.AsSpan(0, 8).SequenceEqual("HXCHFEV3"u8)))
            {
                var hfe = HfeImage.Parse(bytes);
                report.AppendLine($"HFE{(hfe.IsV3 ? "v3" : "")}: header/LUT, track offsets/lengths/overlaps validated.\nGeometry: {hfe.Cylinders} x {hfe.Sides}");
                int unknown = 0, crc = 0, weak = 0;
                foreach (var track in hfe.Tracks)
                {
                    if (track.Encoding == PhysicalEncoding.Unknown || track.Sectors.Count == 0) unknown++;
                    crc += track.Sectors.Count(s => !s.HeaderCrcValid || !s.DataCrcValid);
                    if (track.WeakBitMask.Any(b => b != 0)) weak++;
                    report.AppendLine($"C{track.Cylinder} H{track.Side}: encoding={track.Encoding}; decoded sectors={track.Sectors.Count}; CRC={(track.Sectors.Count == 0 ? "unknown / not decoded" : track.Sectors.Count(s => !s.HeaderCrcValid || !s.DataCrcValid).ToString())}; weak metadata={(hfe.IsV3 ? track.WeakBitMask.Any(b => b != 0).ToString() : "not represented by HFE v1")}");
                }
                report.AppendLine(HfeSemanticInspectionService.Inspect(hfe).Report);
                report.AppendLine("Canonical structure: no physical rewrite proposed. Undecoded regions and preservation bytes retained. No runtime boot certification.");
                return new(crc > 0 || unknown > 0 || weak > 0 ? DskIssueSeverity.Warning : DskIssueSeverity.Info, hash, report.ToString());
            }
            var document = interpretation?.Reopen(bytes) ?? DskDocument.Open(bytes);
            var model = DskAnalyzer.Analyze(document); var boot = DskBootInfo.Inspect(document);
            report.AppendLine($"Extended DSK signature/header/track table/track lengths/descriptors: parsed.\nDeclared tracks: {document.Image.TrackCount} x {document.Image.SideCount}; present: {model.Tracks.Count(t => !t.IsMissing)}; missing: {model.Tracks.Count(t => t.IsMissing)}\nTrailing data: {document.Image.ContainerTrailingData.Length} B (preserved)\nFilesystem: {document.FileSystem.DisplayName}\nBoot/system: {boot.System}; bootable: {boot.Bootable} (static inspection; no runtime certification)");
            foreach (var track in document.Image.Tracks.Where(t => t != null))
            {
                var ids = track!.Sectors.Select(s => (int)s.SectorId).Order().ToArray();
                report.AppendLine($"C{track.Cylinder} H{track.Side}: sectors={ids.Length}; IDs={string.Join(",", ids)}; duplicates={ids.Length - ids.Distinct().Count()}; missing within observed ID range={(ids.Length == 0 ? "unknown" : string.Join(",", Enumerable.Range(ids[0], ids[^1] - ids[0] + 1).Except(ids)))}; sizes={string.Join(",", track.Sectors.Select(s => s.Data.Length).Distinct())}");
            }
            report.AppendLine("Canonical creator padding: " + (ContainerNormalizeService.Candidate(document).AsSpan().SequenceEqual(bytes) ? "yes" : "non-canonical; explicit Normalize available"));
            report.AppendLine(model.Report());
            return new(model.Unsafe > 0 ? DskIssueSeverity.Unsafe : model.Errors > 0 ? DskIssueSeverity.Error : model.Warnings > 0 ? DskIssueSeverity.Warning : DskIssueSeverity.Info, hash, report.ToString());
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or InvalidOperationException)
        { return new(DskIssueSeverity.Error, hash, report.AppendLine("Validation failed: " + e.Message).ToString()); }
    }
}

internal static class ContainerNormalizeService
{
    // The creator's trailing ASCII spaces are documented text padding. No opaque
    // reserved bytes, inactive descriptors, track padding or trailing bytes are erased.
    internal static byte[] Candidate(DskDocument document)
    {
        byte[] result = document.Serialize();
        int last = 0x22 + 13;
        while (last >= 0x22 && (result[last] == 0 || result[last] == 0x20)) result[last--] = 0;
        return result;
    }
    internal static DskOperationPreview Preview(DskDocument document)
    {
        byte[] before = document.Serialize(), after = Candidate(document);
        var comparison = DskCompareService.Compare(document, document.Reopen(after));
        if (comparison.Items.Any(i => i.Level != DskDiffLevel.Container && i.State is not (DskDiffState.Same or DskDiffState.Unavailable)))
            throw new InvalidDataException("Normalization changed physical or filesystem semantics.");
        return new(before, after, $"Normalize Container — explicit creator text padding only\nBefore SHA-256: {ImageVerificationService.Hash(before)}\nAfter SHA-256: {ImageVerificationService.Hash(after)}\nAffected structure: header creator field trailing space/NUL padding (0x22..0x2F).\nFilesystem semantic impact: none. All sector payloads/descriptors, geometry, boot/system, reserved/preservation bytes, track padding and trailing data remain byte-identical. Payload verification passed.\nAnalyzer before:\n{DskAnalyzer.Analyze(document).Report()}\nAnalyzer after:\n{DskAnalyzer.Analyze(document.Reopen(after)).Report()}\nSave remains separate.", document);
    }
}
