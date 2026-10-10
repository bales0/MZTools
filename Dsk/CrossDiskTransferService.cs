using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MZTools;

internal enum DiskCollisionPolicy { Skip, Rename, Replace, Cancel }
internal sealed record DiskCopyRequest(string SourceKey, string? TargetName = null);
internal sealed record DiskCopyPreview(DskOperationPreview Operation, IReadOnlyList<string> MetadataChanges, IReadOnlyList<string>? TargetKeys = null)
{
    internal bool RequiresLossConfirmation => MetadataChanges.Count > 0;
}

internal static class CrossDiskTransferService
{
    internal static bool Supports(DskDocument? document) => document != null && !document.IsReadOnly &&
        document.FileSystem is FsmzFileSystem or CpmFileSystem or MrsFileSystem;

    internal static DiskCopyPreview Preview(DskDocument source, DskDocument target,
        IReadOnlyList<DiskCopyRequest> requests, DiskCollisionPolicy collision)
    {
        if (!Supports(source) || !Supports(target)) throw new InvalidDataException("Copy requires consistent FSMZ, CP/M or MRS disks.");
        if (!Enum.IsDefined(collision)) throw new ArgumentException("Unknown collision policy.");
        if (requests.Count == 0 || requests.Select(r => r.SourceKey).Distinct().Count() != requests.Count)
            throw new InvalidDataException("Select distinct source files.");
        var sourceCopy = source.Clone(); var candidate = target.Clone();
        if (DskAnalyzer.Analyze(sourceCopy).Errors != 0 || DskAnalyzer.Analyze(candidate).Errors != 0)
            throw new InvalidDataException("Resolve allocation/container errors before copying.");
        var report = new StringBuilder($"Copy files: {source.FileSystem.DisplayName} -> {target.FileSystem.DisplayName}\nSource SHA-256: {Hash(source.Serialize())}\nTarget free space before: {target.FileSystem.FreeBytes} B\nTarget directory before: {DirectoryCapacity(target.FileSystem)}\nCollision policy: {collision}\n");
        var changes = new List<string>();
        var targetKeys = new List<string>();
        foreach (var request in requests)
        {
            var entry = sourceCopy.FileSystem.ReadDirectory().Single(e => e.Key == request.SourceKey);
            var file = DskFileTransferService.Capture(sourceCopy.FileSystem, entry);
            string originalName = entry.Name + (entry.Extension.Length == 0 ? "" : "." + entry.Extension);
            string name = request.TargetName ?? originalName;
            DskFileTransferService.ValidateName(name, candidate.FileSystem);
            int user = sourceCopy.FileSystem is CpmFileSystem && candidate.FileSystem is CpmFileSystem ? entry.User : 0;
            if (PersonalCpmSystemInstaller.IsPersonalLayout(candidate) && user == 0 && name.Equals("PCPM.SYS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native PCPM.SYS must be installed through Boot / System, with IPL and slot #0 validation.");
            DskFileEntry? Existing(string value) => candidate.FileSystem.ReadDirectory().SingleOrDefault(e =>
                (e.Name + (e.Extension.Length == 0 ? "" : "." + e.Extension)).Equals(value, StringComparison.OrdinalIgnoreCase) &&
                (candidate.FileSystem is not CpmFileSystem || e.User == user));
            var existing = Existing(name);
            if (existing != null)
            {
                if (collision == DiskCollisionPolicy.Cancel) throw new InvalidDataException($"Name collision: {name} (user {user}). No changes applied.");
                if (collision == DiskCollisionPolicy.Skip) { report.AppendLine($"{originalName}: skipped collision."); continue; }
                if (collision == DiskCollisionPolicy.Rename)
                {
                    int dot = name.LastIndexOf('.');
                    string stem = dot > 0 ? name[..dot] : name, extension = dot > 0 ? name[(dot + 1)..] : "";
                    int limit = candidate.FileSystem is FsmzFileSystem ? 16 - (extension.Length == 0 ? 0 : extension.Length + 1) : 8;
                    int suffix = 1;
                    do
                    {
                        string ending = "~" + suffix++;
                        if (ending.Length >= limit) throw new IOException("No collision-free filename is available.");
                        name = stem[..Math.Min(stem.Length, limit - ending.Length)] + ending + (extension.Length == 0 ? "" : "." + extension);
                    } while (Existing(name) != null);
                }
                else
                {
                    if (existing.ReadOnly || existing.Locked || existing.System ||
                        (PersonalCpmSystemInstaller.IsPersonalLayout(candidate) && existing.User == 0 && existing.Name == "PCPM" && existing.Extension == "SYS"))
                        throw new InvalidDataException($"{name}: protected/system file cannot be replaced by file copy.");
                    candidate.FileSystem.Delete(existing);
                }
            }
            DskFileTransferService.ValidateName(name, candidate.FileSystem);
            DskFileTransferService.AddMetadataChanges(sourceCopy.FileSystem, candidate.FileSystem, file, name, changes);
            var inserted = DskFileTransferService.InsertCaptured(candidate.FileSystem, sourceCopy.FileSystem, file, name);
            targetKeys.Add(inserted.Key);
            if (sourceCopy.FileSystem is MrsFileSystem && candidate.FileSystem is MrsFileSystem)
                changes.Add($"{name}: MRS file ID / FAT ownership rebuilt ({entry.StartBlock} -> {inserted.StartBlock}); LOAD/EXEC retained.");
            byte[] extracted = candidate.FileSystem.Extract(inserted);
            if (extracted.Length < file.Data.Length || !extracted.AsSpan(0, file.Data.Length).SequenceEqual(file.Data))
                throw new InvalidDataException($"{name}: payload verification failed.");
            if (extracted.Length != file.Data.Length) changes.Add($"{name}: record/block padding adds {extracted.Length - file.Data.Length} B; exact original length is not representable.");
            report.AppendLine($"{originalName} -> {name}; user {entry.User} -> {user}; payload {file.Data.Length} B; type {file.FileType}; LOAD={file.LoadAddress:X4}; EXEC={file.ExecuteAddress:X4}; RO/SYS/ARC={file.ReadOnly}/{file.System}/{file.Archived}; lock={file.Locked}; payload verification passed.");
            report.AppendLine($"  Target metadata: type={inserted.FileType}; LOAD={inserted.LoadAddress:X4}; EXEC={inserted.ExecuteAddress:X4}; user={inserted.User}; RO/SYS/ARC={inserted.ReadOnly}/{inserted.System}/{inserted.Archived}; lock={inserted.Locked}. Filesystem-only fields not represented by the target are listed as changes/losses.");
        }
        var output = candidate.Image.Serialize(); var reopened = target.Reopen(output);
        if (DskAnalyzer.Analyze(reopened).Errors != 0) throw new InvalidDataException("Reopened target failed validation.");
        report.AppendLine($"Target free space after: {reopened.FileSystem.FreeBytes} B\nTarget directory after: {DirectoryCapacity(reopened.FileSystem)}\nDirectory capacity/allocation: insertion and reopen validation passed.\nMetadata changes / losses:\n{string.Join("\n", changes)}\nDirectory slots, allocation positions and format-specific raw directory fields are rebuilt. Supported metadata is preserved as listed above.\nSource remains unchanged. Apply changes only the target in memory; Save / Save As is separate.");
        return new(new(target.Serialize(), output, report.ToString(), target), changes, targetKeys);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string DirectoryCapacity(IDskFileSystem fs)
    {
        var files = fs.ReadDirectory();
        return fs switch
        {
            CpmFileSystem c => $"{files.Sum(e => e.Extents)}/{c.Dpb.Drm + 1} extent slots used",
            FsmzFileSystem f => $"{files.Count}/{f.DirectoryLimit} file slots used",
            MrsFileSystem => $"{files.Count} files; native capacity enforced by MRS allocator",
            _ => "unknown"
        };
    }
}
