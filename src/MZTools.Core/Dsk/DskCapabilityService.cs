using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal sealed record DskCompatibilityResult(bool IsCompatible, string Reason)
{
    internal static DskCompatibilityResult Allowed => new(true, "Compatible detected filesystem, DPB and physical layout.");
}

internal sealed record DskBootSystemProfile(string Id, string DisplayName,
    DskFileSystemType CompatibleFileSystem, string CompatibleDpbId,
    string CompatibilityKey, IReadOnlyList<int> RequiredSystemTracks,
    string SourceKind = "User-selected source DSK");

// UI-independent: neither names nor nominal capacities decide compatibility.
internal static class DskCapabilityService
{
    internal static bool CanInspectStructure(DskDocument? document) => document?.FileSystem is CpmFileSystem or FsmzFileSystem or MrsFileSystem;

    internal static DskFilePropertyKind GetFilePropertyKind(DskDocument? document) => document == null || document.IsReadOnly
        ? DskFilePropertyKind.None : document.FileSystem switch
        {
            CpmFileSystem => DskFilePropertyKind.Cpm,
            MrsFileSystem => DskFilePropertyKind.Mrs,
            FsmzFileSystem => DskFilePropertyKind.Fsmz,
            _ => DskFilePropertyKind.None
        };

    internal static IReadOnlyList<DskConversionProfile> GetAvailableConversions(DskDocument document)
    {
        if (document.IsReadOnly || document.FileSystem.Type is not (DskFileSystemType.Fsmz or DskFileSystemType.Cpm or DskFileSystemType.Mrs)) return [];
        if (DskAnalyzer.Analyze(document).Errors != 0) return [];
        var profiles = new List<DskConversionProfile>();
        if (document.FileSystem is FsmzFileSystem fsmz)
        {
            var target = fsmz.DirectoryLimit == 63 ? DskConversionTarget.Fsmz127 : DskConversionTarget.Fsmz63;
            profiles.Add(new("fsmz-directory", target == DskConversionTarget.Fsmz127 ? "IPLDISK / FSMZ 127 entries" : "MZ-BASIC / FSMZ 63 entries",
                target, DskConversionMode.DirectoryConversion));
        }
        foreach (var target in Enum.GetValues<DskConversionTarget>())
        {
            if (document.FileSystem.Type == DskFileSystemType.Fsmz && target is DskConversionTarget.Fsmz63 or DskConversionTarget.Fsmz127) continue;
            if (document.FileSystem.Type == DskFileSystemType.Mrs && target == DskConversionTarget.Mrs) continue;
            if (document.FileSystem is CpmFileSystem cpm && DskFileTransferService.IsCpmTarget(target) &&
                ((CpmFileSystem)DskFileTransferService.CreateTarget(target).FileSystem).Dpb.Name == cpm.Dpb.Name) continue;
            profiles.Add(new("copy-" + target, DskFileTransferService.TargetName(target), target, DskConversionMode.FileCopy));
        }
        return profiles;
    }

    internal static DskCompatibilityResult CanConvert(DskDocument document, DskConversionProfile profile)
    {
        var result = DskFileTransferService.Preflight(document, profile);
        return new(result.CanConvert, result.Report);
    }

    internal static string CompatibilityKey(DskDocument document)
    {
        var image = document.Image;
        var key = new StringBuilder($"{document.FileSystem.Type}|{image.TrackCount}|{image.SideCount}");
        foreach (var track in image.Tracks)
        {
            if (track == null) { key.Append("|missing"); continue; }
            key.Append($"|{track.Cylinder},{track.Side},{track.DefaultSizeCode},{track.Gap}");
            foreach (var sector in track.Sectors)
                key.Append($";{sector.Cylinder},{sector.Side},{sector.SectorId},{sector.SizeCode},{sector.Data.Length}");
        }
        if (document.FileSystem is CpmFileSystem cpm)
        {
            var d = cpm.Dpb;
            key.Append($"|DPB:{d.Spt},{d.Bsh},{d.Blm},{d.Exm},{d.Dsm},{d.Drm},{d.Al0},{d.Al1},{d.Cks},{d.Off},{d.BlockSize},{d.Inverted}");
            key.Append("|tracks:").Append(d.PhysicalTrackMap == null ? "linear" : string.Join(",", d.PhysicalTrackMap));
            key.Append("|sectors:").Append(d.PhysicalSectorMap == null ? "linear" : string.Join(";", d.PhysicalSectorMap.Select(s => $"{s.AbsoluteTrack},{s.SectorId}")));
        }
        return key.ToString();
    }

    internal static IReadOnlyList<DskBootSystemProfile> GetAvailableBootSystems(DskDocument document)
    {
        if (document.IsReadOnly || document.FileSystem is not CpmFileSystem cpm) return [];
        try
        {
            if (PersonalCpmSystemInstaller.IsPersonalLayout(document))
                PersonalCpmSystemInstaller.ValidateGeometry(document);
            DskDocumentFactory.ValidateBootSystemSource(document, document);
            // Reject missing tracks, duplicate IDs, weak/variable-length sectors and FDC errors.
            foreach (var track in document.Image.Tracks)
            {
                if (track == null || track.Sectors.Count == 0 ||
                    track.Sectors.Select(s => s.SectorId).Distinct().Count() != track.Sectors.Count ||
                    track.Sectors.Any(s => s.SizeCode > 6 || s.Data.Length != (128 << s.SizeCode) || s.FdcStatus1 != 0 || s.FdcStatus2 != 0))
                    return [];
            }
            if (DskAnalyzer.Analyze(document).Errors != 0) return [];
            string key = CompatibilityKey(document);
            return [new("cpm-source-" + cpm.Dpb.Name, $"{cpm.DisplayName} — matching source DSK (version supplied by source)",
                DskFileSystemType.Cpm, cpm.Dpb.Name, key,
                DskDocumentFactory.GetSystemPhysicalTracks(cpm.Dpb, document.Image).ToArray())];
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return []; }
    }

    internal static string BootSystemAvailabilityReason(DskDocument? document)
    {
        if (document == null) return "No DSK document is loaded.";
        if (document.FileSystem.Type is DskFileSystemType.SingleIpl or DskFileSystemType.MultiIpl)
            return "Dedicated IPL images already contain their own IPL loader. Boot/System installation is not applicable.";
        if (GetAvailableBootSystems(document).Count > 0)
            return "Select an existing system DSK with exactly the same filesystem, DPB and physical layout. No bundled system/version is assumed.";
        return "No compatible boot/system installer available for this detected filesystem/layout or inconsistent image.";
    }

    internal static DskCompatibilityResult CanInstallBootSystem(DskDocument document, DskBootSystemProfile profile)
    {
        return GetAvailableBootSystems(document).Any(p => p == profile ||
            p.Id == profile.Id && p.CompatibilityKey == profile.CompatibilityKey &&
            p.CompatibleFileSystem == profile.CompatibleFileSystem && p.CompatibleDpbId == profile.CompatibleDpbId &&
            p.RequiredSystemTracks.SequenceEqual(profile.RequiredSystemTracks))
            ? DskCompatibilityResult.Allowed : new(false, "This boot/system profile does not match the detected filesystem, DPB and physical layout. " + BootSystemAvailabilityReason(document));
    }

    internal static DskCompatibilityResult CanInstallBootSystem(DskDocument target, DskDocument source)
    {
        if (PersonalCpmSystemInstaller.IsPersonalLayout(target) || PersonalCpmSystemInstaller.IsPersonalLayout(source))
            return PersonalCpmSystemInstaller.CheckCompatibility(target, source);
        if (GetAvailableBootSystems(target).Count == 0) return new(false, BootSystemAvailabilityReason(target));
        if (GetAvailableBootSystems(source).Count == 0)
        {
            string issues = string.Join("; ", DskAnalyzer.Analyze(source).Issues
                .Where(i => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe).Select(i => i.Code + ": " + i.Description));
            return new(false, "The source DSK is not a supported, consistent CP/M system layout. " + issues);
        }
        try { DskDocumentFactory.ValidateBootSystemSource(target, source); }
        catch (InvalidDataException ex) { return new(false, ex.Message); }
        if (CompatibilityKey(target) != CompatibilityKey(source))
            return new(false, "Source physical sector order, C/H/R/N, geometry, DPB or physical allocation map differs from the target.");
        if (!DskDocumentFactory.GetSystemPhysicalTracks(((CpmFileSystem)target.FileSystem).Dpb, target.Image).SequenceEqual(
            DskDocumentFactory.GetSystemPhysicalTracks(((CpmFileSystem)source.FileSystem).Dpb, source.Image)))
            return new(false, "Source system physical-track list differs from the target.");
        var boot = source.Image.Tracks[1]!;
        if (boot.Sectors.All(s => s.Data.All(b => b is 0 or 0xFF or 0xE5)))
            return new(false, "The source boot track contains only fill bytes; no system image is available.");
        if (!DskBootInfo.Inspect(source).HasSystemBytes)
            return new(false, "The source contains only an identification header, not installed system code.");
        return DskCompatibilityResult.Allowed;
    }

    internal static string Summary(DskDocument document) =>
        "Capabilities — Install Boot/System: " + BootSystemAvailabilityReason(document) +
        "\nConvert Format: " + (GetAvailableConversions(document).Count == 0 ? "No supported conversion." :
            string.Join("; ", GetAvailableConversions(document).Select(p => p.DisplayName + " (" + p.ModeLabel + ")"))) +
        "\n" + DskBootInfo.Inspect(document).Summary;
}

internal static class DskBootSystemService
{
    internal static void Install(DskDocument target, DskDocument source)
        => InstallWithReport(target, source);

    internal static CpmSystemBuildReport InstallWithReport(DskDocument target, DskDocument source)
    {
        var result = CpmSystemBuilder.BuildCompatible(target, source);
        target.ReplaceContents(result.ImageBytes);
        return result.Report;
    }
}
