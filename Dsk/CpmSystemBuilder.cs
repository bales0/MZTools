using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MZTools;

internal sealed record CpmSystemBuildOptions(
    SystemBrandingPolicy PreserveBranding = SystemBrandingPolicy.PreserveAll);

internal sealed record CpmSystemBuildPreflight(bool CanBuild, IReadOnlyList<string> Errors, string Report);

internal sealed record CpmSystemChangedRange(
    int PhysicalTrack, int DescriptorIndex, byte C, byte H, byte R, byte N,
    int Offset, int Length, string OriginalSha256, string ReplacementSha256);

internal sealed record CpmSystemBuildReport(
    string SourceProfile,
    string TargetProfile,
    DskGeometrySignature Geometry,
    CpmDpbSignature Dpb,
    BootLoaderKind BootLoader,
    CpmTransferMode TransferMode,
    IReadOnlyList<int> SystemPhysicalTracks,
    IReadOnlyList<CpmSystemChangedRange> ChangedRanges,
    IReadOnlyList<string> BrandingChanges,
    string SourceSha256,
    string OriginalTargetSha256,
    string ResultSha256)
{
    internal string ToText()
    {
        var text = new StringBuilder()
            .AppendLine("MZTools CP/M System Builder report")
            .AppendLine($"Source profile: {SourceProfile}")
            .AppendLine($"Target profile: {TargetProfile}")
            .AppendLine($"Geometry: {Geometry.Cylinders} cylinders × {Geometry.Sides} sides; {Geometry.PhysicalTracks} physical tracks; descriptor SHA-256 {Geometry.DescriptorSha256}")
            .AppendLine($"DPB: SPT={Dpb.Spt}, BSH={Dpb.Bsh}, BLM={Dpb.Blm}, EXM={Dpb.Exm}, DSM={Dpb.Dsm}, DRM={Dpb.Drm}, AL0={Dpb.Al0:X2}, AL1={Dpb.Al1:X2}, CKS={Dpb.Cks}, OFF={Dpb.Off}, block={Dpb.BlockSize}, inverted={Dpb.Inverted}")
            .AppendLine($"Boot loader: {BootLoader}")
            .AppendLine($"Transfer mode: {TransferMode}")
            .AppendLine($"System physical tracks: {string.Join(", ", SystemPhysicalTracks)}")
            .AppendLine($"Source SHA-256: {SourceSha256}")
            .AppendLine($"Original target SHA-256: {OriginalTargetSha256}")
            .AppendLine($"Result SHA-256: {ResultSha256}")
            .AppendLine($"Changed byte ranges: {ChangedRanges.Count}");
        foreach (CpmSystemChangedRange range in ChangedRanges)
            text.AppendLine($"  track {range.PhysicalTrack}, descriptor {range.DescriptorIndex}, C/H/R/N={range.C}/{range.H}/{range.R}/{range.N}, bytes {range.Offset}..{range.Offset + range.Length - 1}, {range.OriginalSha256} -> {range.ReplacementSha256}");
        text.AppendLine("Branding changes: " + (BrandingChanges.Count == 0 ? "none (verified template bytes preserved)" : string.Join("; ", BrandingChanges)));
        return text.ToString();
    }
}

internal sealed record CpmSystemBuildResult(byte[] ImageBytes, CpmSystemBuildReport Report);

internal static class CpmSystemBuilder
{
    internal static CpmSystemBuildPreflight Preflight(
        DskDocument target, DskDocument source, CpmSystemProfile profile,
        CpmSystemBuildOptions? options = null)
    {
        options ??= new();
        var errors = new List<string>();
        if (target.FileSystem is not CpmFileSystem) errors.Add("Target is not a recognized CP/M filesystem.");
        if (source.FileSystem is not CpmFileSystem) errors.Add("Source is not a recognized CP/M filesystem.");
        if (target.IsReadOnly) errors.Add("Target filesystem is inconsistent or read-only.");
        if (source.IsReadOnly) errors.Add("Source filesystem is inconsistent or read-only.");
        if (options.PreserveBranding != profile.BrandingPolicy)
            errors.Add("This profile supports only its default preserve-branding policy; explicit branding replacement is not configured.");

        string sourceSha = Sha(source.Serialize());
        if (!profile.VerifiedTemplateSha256.Equals(sourceSha, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Source SHA-256 {sourceSha} does not match verified profile {profile.VerifiedTemplateSha256}.");
        if (DskGeometrySignature.From(source.Image) != profile.Geometry)
            errors.Add("Source physical geometry or descriptor order does not match the profile.");
        if (source.FileSystem is CpmFileSystem sourceFs && CpmDpbSignature.From(sourceFs.Dpb) != profile.Dpb)
            errors.Add("Source CP/M DPB or physical map does not match the profile.");
        if (target.FileSystem is CpmFileSystem targetFs)
        {
            if (DskGeometrySignature.From(target.Image) != profile.Geometry)
                errors.Add("Target physical geometry or descriptor order does not match the profile.");
            if (CpmDpbSignature.From(targetFs.Dpb) != profile.Dpb)
                errors.Add("Target CP/M DPB or physical map does not match the profile.");
            ValidateSystemAllocationSeparation(target, targetFs.Dpb, profile.SystemPhysicalTracks, errors);
        }
        if (!DskBootInfo.Inspect(source).HasSystemBytes) errors.Add("Source is data-only and contains no detectable system bytes.");

        AddAnalysisErrors("Target", target, errors);
        AddAnalysisErrors("Source", source, errors);
        if (source.FileSystem is CpmFileSystem && target.FileSystem is CpmFileSystem)
        {
            DskCompatibilityResult compatibility = DskCapabilityService.CanInstallBootSystem(target, source);
            if (!compatibility.IsCompatible) errors.Add(compatibility.Reason);
        }

        string report = errors.Count == 0
            ? $"Ready: {profile.DisplayName}; {profile.TransferMode}; tracks {string.Join(", ", profile.SystemPhysicalTracks)}. The target data filesystem will be preserved byte-for-byte."
            : "Build rejected:\n- " + string.Join("\n- ", errors.Distinct());
        return new(errors.Count == 0, errors.Distinct().ToArray(), report);
    }

    internal static CpmSystemBuildResult Build(
        DskDocument target, DskDocument source, CpmSystemProfile profile,
        CpmSystemBuildOptions? options = null)
    {
        options ??= new();
        CpmSystemBuildPreflight preflight = Preflight(target, source, profile, options);
        if (!preflight.CanBuild) throw new InvalidDataException(preflight.Report);

        byte[] original = target.Serialize();
        byte[] working = (byte[])original.Clone();
        DskImage workingImage = DskImage.Parse(working);
        foreach (int trackIndex in profile.SystemPhysicalTracks)
        {
            DskImage.DskTrack targetTrack = workingImage.Tracks[trackIndex]!;
            DskImage.DskTrack sourceTrack = source.Image.Tracks[trackIndex]!;
            int fileOffset = targetTrack.FileOffset + DskImage.TrackHeaderSize;
            for (int descriptor = 0; descriptor < targetTrack.Sectors.Count; descriptor++)
            {
                sourceTrack.Sectors[descriptor].Data.CopyTo(working, fileOffset);
                fileOffset += targetTrack.Sectors[descriptor].Data.Length;
            }
        }

        DskDocument candidate = DskDocument.Open(working);
        ValidateCandidate(target, candidate, original, profile);
        byte[] result = candidate.Serialize();
        CpmSystemBuildReport report = CreateReport(target, source, candidate, profile, original, result, options);
        return new(result, report);
    }

    internal static CpmSystemBuildReport Apply(
        DskDocument target, DskDocument source, CpmSystemProfile profile,
        CpmSystemBuildOptions? options = null)
    {
        CpmSystemBuildResult result = Build(target, source, profile, options);
        target.ReplaceContents(result.ImageBytes);
        return result.Report;
    }

    internal static void SaveValidatedResult(CpmSystemBuildResult result, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An output path is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("The output directory does not exist.");
        if (!Sha(result.ImageBytes).Equals(result.Report.ResultSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The build result no longer matches its report SHA-256.");
        DskDocument verification = DskDocument.Open(result.ImageBytes);
        if (verification.IsReadOnly || DskAnalyzer.Analyze(verification).Errors != 0)
            throw new InvalidDataException("The build result failed final save validation.");

        string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, result.ImageBytes);
            byte[] written = File.ReadAllBytes(temporary);
            if (!Sha(written).Equals(result.Report.ResultSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The temporary output file failed SHA-256 verification.");
            _ = DskDocument.Open(written);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateCandidate(DskDocument originalDocument, DskDocument candidate,
        byte[] originalBytes, CpmSystemProfile profile)
    {
        if (candidate.IsReadOnly || candidate.FileSystem is not CpmFileSystem)
            throw new InvalidDataException("Built image no longer has a consistent writable CP/M filesystem.");
        if (DskGeometrySignature.From(candidate.Image) != profile.Geometry ||
            CpmDpbSignature.From(((CpmFileSystem)candidate.FileSystem).Dpb) != profile.Dpb)
            throw new InvalidDataException("Build changed the target geometry, descriptor order or DPB.");
        DskLayoutModel analysis = DskAnalyzer.Analyze(candidate);
        if (analysis.Errors != 0)
            throw new InvalidDataException("Built image failed Analyzer validation: " +
                string.Join("; ", analysis.Issues.Where(i => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe).Select(i => i.Code + ": " + i.Description)));
        if (!candidate.Image.Serialize().AsSpan().SequenceEqual(candidate.Serialize()))
            throw new InvalidDataException("Built container is not byte-preserving after reopen/serialize.");

        HashSet<int> systemTracks = profile.SystemPhysicalTracks.ToHashSet();
        DskImage originalImage = DskImage.Parse(originalBytes);
        for (int trackIndex = 0; trackIndex < originalImage.Tracks.Count; trackIndex++)
        {
            if (systemTracks.Contains(trackIndex)) continue;
            DskImage.DskTrack left = originalImage.Tracks[trackIndex]!;
            DskImage.DskTrack right = candidate.Image.Tracks[trackIndex]!;
            for (int sector = 0; sector < left.Sectors.Count; sector++)
                if (!left.Sectors[sector].Data.AsSpan().SequenceEqual(right.Sectors[sector].Data))
                    throw new InvalidDataException($"Build changed non-system track {trackIndex}, descriptor {sector}.");
        }
        ValidateFilesUnchanged(originalDocument, candidate);
    }

    private static void ValidateFilesUnchanged(DskDocument before, DskDocument after)
    {
        IReadOnlyList<DskFileEntry> left = before.FileSystem.ReadDirectory();
        IReadOnlyList<DskFileEntry> right = after.FileSystem.ReadDirectory();
        if (left.Count != right.Count) throw new InvalidDataException("Build changed the CP/M directory.");
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].Key != right[index].Key ||
                !before.FileSystem.Extract(left[index]).AsSpan().SequenceEqual(after.FileSystem.Extract(right[index])))
                throw new InvalidDataException($"Build changed file {left[index].Key}.");
        }
    }

    private static CpmSystemBuildReport CreateReport(DskDocument target, DskDocument source,
        DskDocument candidate, CpmSystemProfile profile, byte[] original, byte[] result,
        CpmSystemBuildOptions options)
    {
        var ranges = new List<CpmSystemChangedRange>();
        foreach (int trackIndex in profile.SystemPhysicalTracks)
        {
            DskImage.DskTrack beforeTrack = target.Image.Tracks[trackIndex]!;
            DskImage.DskTrack afterTrack = candidate.Image.Tracks[trackIndex]!;
            for (int descriptor = 0; descriptor < beforeTrack.Sectors.Count; descriptor++)
            {
                byte[] before = beforeTrack.Sectors[descriptor].Data;
                byte[] after = afterTrack.Sectors[descriptor].Data;
                int start = 0;
                while (start < before.Length)
                {
                    while (start < before.Length && before[start] == after[start]) start++;
                    if (start == before.Length) break;
                    int end = start + 1;
                    while (end < before.Length && before[end] != after[end]) end++;
                    DskImage.DskSector sector = afterTrack.Sectors[descriptor];
                    ranges.Add(new(trackIndex, descriptor, sector.Cylinder, sector.Side, sector.SectorId, sector.SizeCode,
                        start, end - start, Sha(before.AsSpan(start, end - start)), Sha(after.AsSpan(start, end - start))));
                    start = end;
                }
            }
        }
        return new(profile.DisplayName, profile.Layout.ToString(), profile.Geometry, profile.Dpb,
            profile.BootLoader, profile.TransferMode, profile.SystemPhysicalTracks, ranges,
            [], Sha(source.Serialize()), Sha(original), Sha(result));
    }

    private static void AddAnalysisErrors(string role, DskDocument document, List<string> errors)
    {
        DskLayoutModel analysis = DskAnalyzer.Analyze(document);
        foreach (DskAnalysisIssue issue in analysis.Issues.Where(i => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe))
            errors.Add($"{role} Analyzer {issue.Code}: {issue.Description}");
    }

    private static void ValidateSystemAllocationSeparation(DskDocument document, CpmDpb dpb,
        IReadOnlyList<int> systemTracks, List<string> errors)
    {
        HashSet<int> reserved = systemTracks.ToHashSet();
        int sectorsPerTrack = dpb.Spt / 4;
        int bytesPerTrack = checked(sectorsPerTrack * 512);
        int logicalDataTracks = checked(((dpb.Dsm + 1) * dpb.BlockSize + bytesPerTrack - 1) / bytesPerTrack);
        for (int logicalTrack = dpb.Off; logicalTrack < dpb.Off + logicalDataTracks; logicalTrack++)
        {
            if (dpb.PhysicalSectorMap != null)
            {
                int start = (logicalTrack - dpb.Off) * sectorsPerTrack;
                for (int index = start; index < Math.Min(start + sectorsPerTrack, dpb.PhysicalSectorMap.Count); index++)
                    if (reserved.Contains(dpb.PhysicalSectorMap[index].AbsoluteTrack))
                        errors.Add($"System track {dpb.PhysicalSectorMap[index].AbsoluteTrack} overlaps CP/M directory/data allocation.");
            }
            else
            {
                int physicalTrack = dpb.PhysicalTrackMap == null ? logicalTrack :
                    logicalTrack < dpb.PhysicalTrackMap.Count ? dpb.PhysicalTrackMap[logicalTrack] : -1;
                if (physicalTrack >= 0 && reserved.Contains(physicalTrack))
                    errors.Add($"System track {physicalTrack} overlaps CP/M directory/data allocation.");
            }
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
