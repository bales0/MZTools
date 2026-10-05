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
    CpmTransferMode? TransferMode,
    IReadOnlyList<int> SystemPhysicalTracks,
    IReadOnlyList<CpmSystemChangedRange> ChangedRanges,
    IReadOnlyList<string> BrandingChanges,
    string SourceSha256,
    string OriginalTargetSha256,
    string ResultSha256,
    string SystemAreaSha256,
    CpmRuntimeVerification Verification)
{
    internal CpmSystemStorageKind Storage { get; init; } = CpmSystemStorageKind.HiddenSystemTracks;
    internal CpmFileBasedSystemFingerprint? SystemFileFingerprint { get; init; }
    internal int? SystemFileDirectorySlot { get; init; }
    internal int? RelocatedFirstEntrySlot { get; init; }
    internal IReadOnlyList<int> SystemFileAllocationBlocks { get; init; } = [];
    internal bool OtherTargetFilesPreserved { get; init; }
    internal string ToText()
    {
        var text = new StringBuilder()
            .AppendLine("MZTools CP/M System Builder report")
            .AppendLine($"Source profile: {SourceProfile}")
            .AppendLine($"Target profile: {TargetProfile}")
            .AppendLine($"Geometry: {Geometry.Cylinders} cylinders × {Geometry.Sides} sides; {Geometry.PhysicalTracks} physical tracks; descriptor SHA-256 {Geometry.DescriptorSha256}")
            .AppendLine($"DPB: SPT={Dpb.Spt}, BSH={Dpb.Bsh}, BLM={Dpb.Blm}, EXM={Dpb.Exm}, DSM={Dpb.Dsm}, DRM={Dpb.Drm}, AL0={Dpb.Al0:X2}, AL1={Dpb.Al1:X2}, CKS={Dpb.Cks}, OFF={Dpb.Off}, block={Dpb.BlockSize}, inverted={Dpb.Inverted}")
            .AppendLine($"Boot loader: {(BootLoader == BootLoaderKind.Unknown ? "Unverified" : BootLoader.ToString())}")
            .AppendLine($"Transfer mode: {TransferMode?.ToString() ?? "Unverified"}")
            .AppendLine($"System physical tracks: {string.Join(", ", SystemPhysicalTracks)}")
            .AppendLine($"Source SHA-256: {SourceSha256}")
            .AppendLine($"Source system-area SHA-256: {SystemAreaSha256}")
            .AppendLine($"Verification: {(Verification == CpmRuntimeVerification.None ? "Unverified" : Verification.ToString())}; no runtime boot certification inferred")
            .AppendLine($"Original target SHA-256: {OriginalTargetSha256}")
            .AppendLine($"Result SHA-256: {ResultSha256}")
            .AppendLine($"Changed byte ranges: {ChangedRanges.Count}");
        foreach (CpmSystemChangedRange range in ChangedRanges)
            text.AppendLine($"  track {range.PhysicalTrack}, descriptor {range.DescriptorIndex}, C/H/R/N={range.C}/{range.H}/{range.R}/{range.N}, bytes {range.Offset}..{range.Offset + range.Length - 1}, {range.OriginalSha256} -> {range.ReplacementSha256}");
        text.AppendLine("Branding changes: " + (BrandingChanges.Count == 0 ? "none (source boot/system bytes preserved)" : string.Join("; ", BrandingChanges)));
        text.AppendLine($"System storage: {Storage}");
        if (SystemFileFingerprint is { } file)
        {
            text.AppendLine($"System file: {file.SystemFileName}; user {file.User}; SYS={file.SystemAttribute}; {file.SystemFileSize} B; payload SHA-256 {file.SystemFileSha256}");
            text.AppendLine($"PCPM.SYS directory slot: {SystemFileDirectorySlot}")
                .AppendLine($"Previous slot 0 entry relocated: {(RelocatedFirstEntrySlot is int slot ? $"yes (to slot {slot})" : "no")}")
                .AppendLine($"PCPM.SYS allocation blocks: {string.Join(", ", SystemFileAllocationBlocks)}")
                .AppendLine($"Other target files preserved: {(OtherTargetFilesPreserved ? "yes" : "no")}");
        }
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
        if (profile.Storage == CpmSystemStorageKind.BootTrackPlusSystemFile)
            return PersonalCpmSystemInstaller.Preflight(target, source, profile, options);
        if (PersonalCpmSystemInstaller.IsPersonalLayout(target) || PersonalCpmSystemInstaller.IsPersonalLayout(source))
            return new(false, ["Native P-CP/M80 requires the file-based IPL + PCPM.SYS installer."], "Install rejected: native P-CP/M80 cannot use hidden system tracks.");
        var errors = new List<string>();
        if (target.FileSystem is not CpmFileSystem) errors.Add("Target is not a recognized CP/M filesystem.");
        if (source.FileSystem is not CpmFileSystem) errors.Add("Source is not a recognized CP/M filesystem.");
        if (target.IsReadOnly) errors.Add("Target filesystem is inconsistent or read-only.");
        if (source.IsReadOnly) errors.Add("Source filesystem is inconsistent or read-only.");
        if (options.PreserveBranding != profile.BrandingPolicy)
            errors.Add("This profile supports only its default preserve-branding policy; explicit branding replacement is not configured.");

        try
        {
            string sourceSha = CpmSystemFingerprint.HashSystemArea(source.Image, profile.SystemPhysicalTracks);
            if (!profile.SystemAreaSha256.Equals(sourceSha, StringComparison.OrdinalIgnoreCase))
                errors.Add($"System-area SHA-256 {sourceSha} does not match profile {profile.SystemAreaSha256}.");
            if (source.FileSystem is CpmFileSystem fs && !profile.SystemPhysicalTracks.SequenceEqual(
                DskDocumentFactory.GetSystemPhysicalTracks(fs.Dpb, source.Image)))
                errors.Add("Source system-track list does not match the profile.");
        }
        catch (InvalidDataException exception) { errors.Add(exception.Message); }
        if (DskGeometrySignature.From(source.Image) != profile.Geometry)
            errors.Add("Source physical geometry or descriptor order does not match the profile.");
        if (source.FileSystem is CpmFileSystem sourceFs && CpmDpbSignature.From(sourceFs.Dpb) != profile.Dpb)
            errors.Add("Source CP/M DPB or physical map does not match the profile.");
        if (target.FileSystem is CpmFileSystem targetFs)
        {
            if (!profile.SystemPhysicalTracks.SequenceEqual(DskDocumentFactory.GetSystemPhysicalTracks(targetFs.Dpb, target.Image)))
                errors.Add("Target system-track list does not match the profile.");
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

        return profile.Storage == CpmSystemStorageKind.BootTrackPlusSystemFile
            ? PersonalCpmSystemInstaller.Build(target, source, profile)
            : BuildCore(target, source, profile.SystemPhysicalTracks, profile);
    }

    internal static CpmSystemBuildResult BuildCompatible(DskDocument target, DskDocument source)
    {
        var check = DskCapabilityService.CanInstallBootSystem(target, source);
        if (!check.IsCompatible) throw new InvalidDataException(check.Reason);
        if (PersonalCpmSystemInstaller.IsPersonalLayout(target))
            return PersonalCpmSystemInstaller.Build(target, source, CpmSystemProfileRegistry.TryResolveVerifiedSource(source));
        var tracks = DskDocumentFactory.GetSystemPhysicalTracks(((CpmFileSystem)target.FileSystem).Dpb, target.Image).ToArray();
        var errors = new List<string>();
        ValidateSystemAllocationSeparation(target, ((CpmFileSystem)target.FileSystem).Dpb, tracks, errors);
        AddAnalysisErrors("Target", target, errors); AddAnalysisErrors("Source", source, errors);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        return BuildCore(target, source, tracks, null);
    }

    private static CpmSystemBuildResult BuildCore(DskDocument target, DskDocument source,
        IReadOnlyList<int> tracks, CpmSystemProfile? profile)
    {

        byte[] original = target.Serialize();
        byte[] working = (byte[])original.Clone();
        DskImage workingImage = DskImage.Parse(working);
        foreach (int trackIndex in tracks)
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
        ValidateCandidate(target, candidate, original, tracks);
        byte[] result = candidate.Serialize();
        CpmSystemBuildReport report = CreateReport(target, source, candidate, profile, tracks, original, result);
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

    private static void ValidateCandidate(DskDocument originalDocument, DskDocument candidate,
        byte[] originalBytes, IReadOnlyList<int> tracks)
    {
        if (candidate.IsReadOnly || candidate.FileSystem is not CpmFileSystem)
            throw new InvalidDataException("Built image no longer has a consistent writable CP/M filesystem.");
        if (DskGeometrySignature.From(candidate.Image) != DskGeometrySignature.From(originalDocument.Image) ||
            CpmDpbSignature.From(((CpmFileSystem)candidate.FileSystem).Dpb) != CpmDpbSignature.From(((CpmFileSystem)originalDocument.FileSystem).Dpb))
            throw new InvalidDataException("Build changed the target geometry, descriptor order or DPB.");
        DskLayoutModel analysis = DskAnalyzer.Analyze(candidate);
        if (analysis.Errors != 0)
            throw new InvalidDataException("Built image failed Analyzer validation: " +
                string.Join("; ", analysis.Issues.Where(i => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe).Select(i => i.Code + ": " + i.Description)));
        if (!candidate.Image.Serialize().AsSpan().SequenceEqual(candidate.Serialize()))
            throw new InvalidDataException("Built container is not byte-preserving after reopen/serialize.");

        HashSet<int> systemTracks = tracks.ToHashSet();
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

    internal static CpmSystemBuildReport CreateReport(DskDocument target, DskDocument source,
        DskDocument candidate, CpmSystemProfile? profile, IReadOnlyList<int> tracks, byte[] original, byte[] result,
        IReadOnlyList<int>? changedTracks = null)
    {
        var ranges = new List<CpmSystemChangedRange>();
        foreach (int trackIndex in changedTracks ?? tracks)
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
        return new(profile?.DisplayName ?? "Compatible source DSK — OS/version unverified", target.FileSystem.DisplayName,
            DskGeometrySignature.From(target.Image), CpmDpbSignature.From(((CpmFileSystem)target.FileSystem).Dpb),
            profile?.BootLoader ?? BootLoaderKind.Unknown, profile?.TransferMode, tracks, ranges,
            [], Sha(source.Serialize()), Sha(original), Sha(result), CpmSystemFingerprint.HashSystemArea(source.Image, tracks),
            profile?.Verification ?? CpmRuntimeVerification.None);
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
