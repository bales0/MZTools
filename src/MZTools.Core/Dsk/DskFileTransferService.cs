using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal enum DskConversionTarget { Fsmz63, Fsmz127, PersonalCpm, Sds400, LecDd, LecHd, Mrs }
internal enum DskConversionMode { DirectoryConversion, FileCopy }
internal sealed record DskConversionProfile(string Id, string DisplayName, DskConversionTarget Target, DskConversionMode Mode)
{
    internal string ModeLabel => Mode == DskConversionMode.DirectoryConversion
        ? "Rebuild related directory layout in a new image" : "Create new image and copy compatible files";
    public override string ToString() => DisplayName + " — " + ModeLabel;
}

internal sealed record DskTransferFile(string Name, string Extension, byte[] Data, byte? FileType,
    ushort? LoadAddress, ushort? ExecuteAddress, int User, bool ReadOnly, bool System, bool Archived,
    bool Locked, byte[]? OriginalMetadata);

internal sealed record DskConversionPreflight(int CompatibleFiles, int TotalFiles, IReadOnlyList<string> Issues,
    IReadOnlyList<string> MetadataChanges, byte[]? OutputImage, string Source, string Target, string Mode)
{
    internal bool CanConvert => Issues.Count == 0 && OutputImage != null;
    internal string Report => $"Source: {Source}\nTarget: {Target}\nMode: {Mode}\n\n" +
        $"{CompatibleFiles}/{TotalFiles} files can be copied. Strict policy: all files must pass.\n" +
        "Original document remains unchanged; output must be saved under a different path.\n\n" +
        "Will preserve: filenames and source payload bytes.\n" +
        (MetadataChanges.Count == 0 ? "No supported file metadata is lost.\n" : "Metadata changes / losses and source warnings:\n" + string.Join("\n", MetadataChanges.Select(s => "  " + s)) + "\n") +
        "\n" + (Issues.Count == 0 ? "Directory, names, duplicates, allocation and capacity preflight passed." :
            "Conversion refused:\n" + string.Join("\n", Issues.Select(s => "  " + s)));
}

internal static class DskFileTransferService
{
    internal static void SaveConvertedImage(DskConversionPreflight result, string path, string? sourcePath)
    {
        if (!result.CanConvert) throw new InvalidDataException("Conversion has not passed strict preflight.");
        string destination = Path.GetFullPath(path);
        if (sourcePath != null && destination.Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Conversion must be saved under a different path; the source image cannot be overwritten.");
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                stream.Write(result.OutputImage!); stream.Flush(true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsCpmTarget(DskConversionTarget target) => target is DskConversionTarget.PersonalCpm or DskConversionTarget.Sds400 or DskConversionTarget.LecDd or DskConversionTarget.LecHd;
    internal static string TargetName(DskConversionTarget target) => target switch
    {
        DskConversionTarget.Fsmz63 => "MZ-BASIC / FSMZ 63 entries (320 KiB)",
        DskConversionTarget.Fsmz127 => "IPLDISK / FSMZ 127 entries (320 KiB)",
        DskConversionTarget.PersonalCpm => "P-CP/M80 original (320 KiB)",
        DskConversionTarget.Sds400 => "P-CP/M80 SDS/400",
        DskConversionTarget.LecDd => "LEC CP/M DD (720 KiB)",
        DskConversionTarget.LecHd => "LEC CP/M HD (1.44 MiB)",
        _ => "MRS (720 KiB)"
    };
    internal static DskDocument CreateTarget(DskConversionTarget target) => target switch
    {
        DskConversionTarget.Fsmz63 => DskDocumentFactory.CreateFsmz(false),
        DskConversionTarget.Fsmz127 => DskDocumentFactory.CreateFsmz(true),
        DskConversionTarget.PersonalCpm => DskDocumentFactory.CreatePersonalCpm80(false),
        DskConversionTarget.Sds400 => DskDocumentFactory.CreatePersonalCpm80(true),
        DskConversionTarget.LecDd => DskDocumentFactory.CreateCpm(false),
        DskConversionTarget.LecHd => DskDocumentFactory.CreateCpm(true),
        _ => DskDocumentFactory.CreateMrs()
    };

    internal static DskConversionPreflight Preflight(DskDocument source, DskConversionProfile profile)
    {
        var issues = new List<string>(); var changes = new List<string>();
        int count = 0, total = 0;
        DskConversionPreflight Result(byte[]? bytes = null) => new(count, total, issues, changes, bytes,
            source.FileSystem.DisplayName, profile.DisplayName, profile.ModeLabel);
        if (!DskCapabilityService.GetAvailableConversions(source).Contains(profile))
        { issues.Add("This conversion profile is not available for the detected filesystem/layout."); return Result(); }
        try
        {
            foreach (var warning in DskAnalyzer.Analyze(source).Issues.Where(i => i.Severity == DskIssueSeverity.Warning))
                changes.Add($"Source warning {warning.Code}: {warning.Description} (conversion copies captured payloads, not a verified recovery).");
            var entries = source.FileSystem.ReadDirectory(); total = entries.Count;
            var files = entries.Select(e => Capture(source.FileSystem, e)).ToArray();
            DskDocument target;
            if (profile.Mode == DskConversionMode.DirectoryConversion)
            {
                var fs = (FsmzFileSystem)source.FileSystem;
                bool extended = profile.Target == DskConversionTarget.Fsmz127;
                if (!extended && total > 63) { issues.Add($"{total} entries exceed the 63-entry target directory limit."); return Result(); }
                // All payloads and raw directory metadata are captured before transactional relocation.
                var image = DskImage.Parse(source.Image.Serialize());
                if (image.Tracks.Any(t => t == null || t.Sectors.Count != 16 ||
                    !t.Sectors.Select(s => (int)s.SectorId).Order().SequenceEqual(Enumerable.Range(1, 16)) ||
                    t.Sectors.Any(s => s.SizeCode != 1 || s.Data.Length != 256)))
                { issues.Add("Directory conversion requires complete FSMZ tracks with unique sector IDs 1..16 × 256 B."); return Result(); }
                image.Creator = extended ? "MZTools F127" : "MZTools F63";
                // Clear both directory regions on the private image, including removed
                // upper slots during shrink. Captured payloads are relocated afterwards.
                FsmzFileSystem.Format(image, true);
                var targetFs = new FsmzFileSystem(image, extended); targetFs.SetVolume(fs.VolumeNumber);
                int fileArea = Math.Max(fs.FileAreaBlock, extended ? 32 : 24);
                targetFs.SetRebuiltBounds(fileArea, Math.Min(fs.LastBlock, fileArea + 1999));
                target = new DskDocument(image, image.Serialize(), null, targetFs);
                changes.Add("Directory slots and data block positions may change (transactional relocation in the new image). Volume and complete raw file metadata are preserved.");
                changes.Add("Container creator records the 63/127 directory variant. Existing boot bytes are retained, but loader compatibility with the new directory variant is not certified.");
            }
            else
            {
                target = CreateTarget(profile.Target);
                changes.Add("Boot/system code is NOT copied. The target is a data-only formatted image (an IPL identification header alone is not a system).");
                changes.Add("Filesystem-specific raw directory fields, directory slots, allocation positions and volume identity are not copied.");
            }
            var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string name = file.Name + (file.Extension.Length == 0 ? "" : "." + file.Extension);
                try
                {
                    ValidateName(name, target.FileSystem);
                    string identity = target.FileSystem is CpmFileSystem ? $"{(source.FileSystem is CpmFileSystem ? file.User : 0)}:{name}" : name;
                    if (!targetNames.Add(identity)) throw new InvalidDataException("Duplicate target filename after user-area/name conversion.");
                    AddMetadataChanges(source.FileSystem, target.FileSystem, file, name, changes);
                    // Each attempted insertion uses its own candidate: failed allocation never contaminates the dry run.
                    var candidate = DskDocument.Open(target.Image.Serialize());
                    var inserted = InsertCaptured(candidate.FileSystem, source.FileSystem, file, name);
                    if (candidate.FileSystem is FsmzFileSystem destFsmz && source.FileSystem is FsmzFileSystem && profile.Mode == DskConversionMode.DirectoryConversion)
                        destFsmz.RestoreDirectoryMetadata(inserted, file.OriginalMetadata!);
                    var extracted = candidate.FileSystem.Extract(inserted);
                    if (extracted.Length < file.Data.Length || !extracted.AsSpan(0, file.Data.Length).SequenceEqual(file.Data))
                        throw new InvalidDataException("Target cannot preserve the source payload bytes.");
                    if (extracted.Length != file.Data.Length)
                        changes.Add($"{name}: target stores record/block length; adds {extracted.Length - file.Data.Length} padding bytes (exact source length is not representable).");
                    candidate.MarkModified(); target = candidate; count++;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or OverflowException)
                { issues.Add($"{name}: {ex.Message}"); }
            }
            if (issues.Count > 0) return Result();
            byte[] output = target.Image.Serialize();
            var reopened = DskDocument.Open(output);
            if (DskCapabilityService.CompatibilityKey(reopened) != DskCapabilityService.CompatibilityKey(target) || reopened.FileSystem.ReadDirectory().Count != files.Length || DskAnalyzer.Analyze(reopened).Errors != 0)
            { issues.Add("Reopened output did not pass filesystem/count/allocation validation."); return Result(); }
            if (reopened.FileSystem is FsmzFileSystem reopenedFs && reopenedFs.DirectoryLimit != (profile.Target == DskConversionTarget.Fsmz127 ? 127 : 63))
            { issues.Add("Target directory variant was not retained after reopening."); return Result(); }
            return Result(output);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or OverflowException)
        { issues.Add(ex.Message); return Result(); }
    }

    internal static void ValidateName(string name, IDskFileSystem target)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => c < 32 || c > 126 || "\\/:*?\"<>|".Contains(c)))
            throw new InvalidDataException("Filename contains unsupported characters; no automatic renaming is performed.");
        if (target is FsmzFileSystem)
        {
            if (name.Length > 16) throw new InvalidDataException("FSMZ filename exceeds 16 characters.");
            var encoded = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(name);
            if (!SharpMzEncoding.ConvertMzfNameToASCIIString(encoded.Concat(new byte[] { 13 }).ToArray()).Equals(name, StringComparison.Ordinal))
                throw new InvalidDataException("Filename is not losslessly representable in Sharp encoding.");
        }
        else
        {
            if (name.Contains(' ')) throw new InvalidDataException("Spaces are not representable in the target 8.3 filename.");
            string[] parts = name.Split('.');
            if (parts.Length > 2 || parts[0].Length is < 1 or > 8 || (parts.Length == 2 && parts[1].Length is < 1 or > 3))
                throw new InvalidDataException("Target filename must use the 8.3 format.");
            if (target is CpmFileSystem && name.Any(c => "[]=;,+".Contains(c)))
                throw new InvalidDataException("Filename contains unsupported CP/M characters.");
        }
    }

    internal static void AddMetadataChanges(IDskFileSystem source, IDskFileSystem target, DskTransferFile file, string name, List<string> changes)
    {
        if (source is CpmFileSystem && target is not CpmFileSystem)
            changes.Add($"{name}: loses CP/M user area ({file.User}), RO/SYS/ARC and extent/allocation metadata.");
        if (source is CpmFileSystem && target is MrsFileSystem)
            changes.Add($"{name}: LOAD/EXEC absent in source; stored as unset (0), not inferred.");
        if (source is not CpmFileSystem && target is CpmFileSystem)
            changes.Add($"{name}: loses LOAD/EXEC" + (source is FsmzFileSystem ? ", file type and lock" : ", MRS file ID/FAT ownership") + "; target user area is 0.");
        if (source is FsmzFileSystem && target is MrsFileSystem)
            changes.Add($"{name}: loses FSMZ type and lock; LOAD/EXEC retained.");
        if (source is not FsmzFileSystem && target is FsmzFileSystem)
            changes.Add($"{name}: generic binary type 1; " + (source is CpmFileSystem ? "LOAD/EXEC absent in source, stored as unset (0)." : "LOAD/EXEC retained; MRS file ID/FAT ownership lost."));
        if (source is CpmFileSystem && target is CpmFileSystem)
            changes.Add($"{name}: user area and RO/SYS/ARC retained; extent/allocation layout is rebuilt.");
        if (target is not FsmzFileSystem && name != name.ToUpperInvariant())
            changes.Add($"{name}: target filename is normalized to uppercase ({name.ToUpperInvariant()}).");
    }
    internal static DskTransferFile Capture(IDskFileSystem fs, DskFileEntry e) => new(e.Name, e.Extension, fs.Extract(e),
        fs is FsmzFileSystem ? e.FileType : null, fs is CpmFileSystem ? null : e.LoadAddress,
        fs is CpmFileSystem ? null : e.ExecuteAddress, e.User, e.ReadOnly, e.System, e.Archived, e.Locked,
        fs is FsmzFileSystem f ? f.DirectoryMetadata(e) : null);

    internal static DskFileEntry InsertCaptured(IDskFileSystem target, IDskFileSystem source, DskTransferFile file, string name)
    {
        int user = source is CpmFileSystem && target is CpmFileSystem ? file.User : 0;
        target.Insert(name, file.Data, file.FileType ?? 1, file.LoadAddress ?? 0, file.ExecuteAddress ?? 0, user);
        var inserted = target.ReadDirectory().Single(e =>
            (e.Name + (e.Extension.Length == 0 ? "" : "." + e.Extension)).Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (target is not CpmFileSystem || e.User == user));
        if (target is CpmFileSystem c && source is CpmFileSystem) c.SetAttributes(inserted, file.ReadOnly, file.System, file.Archived);
        if (target is FsmzFileSystem f && source is FsmzFileSystem) f.SetLocked(inserted, file.Locked);
        return inserted;
    }
}
