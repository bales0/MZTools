using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MZTools;

internal enum DiskCollisionPolicy { Skip, Rename, Replace, Cancel }
internal sealed record DiskCopyRequest(string SourceKey, string? TargetName = null);
internal sealed record DiskCopyFileChange(string SourceName,string TargetName,string Result,long SourceBytes,long? TargetBytes,
    int SourceUser,int? TargetUser,IReadOnlyList<string[]> Metadata);
internal sealed record DiskCopyPreview(DskOperationPreview Operation, IReadOnlyList<string> MetadataChanges, IReadOnlyList<string>? TargetKeys = null,
    IReadOnlyList<DiskCopyFileChange>? Files = null)
{
    internal bool RequiresLossConfirmation => MetadataChanges.Count > 0;
    internal ReportPresentation Presentation()
    {
        var original=ReportPresentation.FromText(string.Join("\n",Operation.Report.Split('\n').Where(line=>
            !(line.Contains(" -> ") && line.Contains("; payload ")) && !line.TrimStart().StartsWith("Target metadata:") && !line.TrimEnd().EndsWith(": skipped collision.") && !MetadataChanges.Contains(line.Trim()))));
        var files=Files??Array.Empty<DiskCopyFileChange>();
        var tables=new List<ReportTable> {
            new("Files",new[] {"Source file","Target file","Action / validation","Source bytes","Target bytes","Source user","Target user"},
                files.Select(f=>new[] {f.SourceName,f.TargetName,f.Result,f.SourceBytes.ToString(),f.TargetBytes?.ToString()??"—",f.SourceUser.ToString(),f.TargetUser?.ToString()??"—"}).ToArray()),
            new("File properties",new[] {"Source file","Target file","Property","Source","Target"},
                files.SelectMany(f=>f.Metadata.Select(m=>new[] {f.SourceName,f.TargetName,m[0],m[1],m[2]})).ToArray()),
            new("Metadata changes",new[] {"Details"},MetadataChanges.Count==0?new[] {new[] {"No metadata changes reported."}}:MetadataChanges.Select(s=>new[] {s}).ToArray())
        };
        tables.AddRange(original.Tables.Select(t=>t.Title=="Metadata changes / losses"?t with {Title="Notes"}:t));
        return new("Copy files to current disk",$"{files.Count} selected · {files.Count(f=>f.TargetBytes!=null)} to copy · {files.Count(f=>f.TargetBytes==null)} skipped · Apply changes the target in memory; Save remains separate.",Operation.Report,tables);
    }
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
        var fileChanges = new List<DiskCopyFileChange>();
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
                if (collision == DiskCollisionPolicy.Skip) { report.AppendLine($"{originalName}: skipped collision."); fileChanges.Add(new(originalName,name,"Skipped: existing file",file.Data.Length,null,entry.User,null,Array.Empty<string[]>())); continue; }
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
            string Address(ushort? value)=>value is ushort a?$"0x{a:X4}":"Not represented";
            fileChanges.Add(new(originalName,name,existing==null?"Copy; payload verified":collision==DiskCollisionPolicy.Rename?"Rename; payload verified":"Replace; payload verified",
                file.Data.Length,extracted.Length,entry.User,inserted.User,new[] {
                    new[] {"Type",file.FileType?.ToString("X2")??"Not represented",candidate.FileSystem is CpmFileSystem?"Not represented":inserted.FileType.ToString("X2")},
                    new[] {"LOAD",Address(file.LoadAddress),candidate.FileSystem is CpmFileSystem?"Not represented":Address(inserted.LoadAddress)},
                    new[] {"EXEC",Address(file.ExecuteAddress),candidate.FileSystem is CpmFileSystem?"Not represented":Address(inserted.ExecuteAddress)},
                    new[] {"Read-only",file.ReadOnly.ToString(),inserted.ReadOnly.ToString()},new[] {"System",file.System.ToString(),inserted.System.ToString()},
                    new[] {"Archived",file.Archived.ToString(),inserted.Archived.ToString()},new[] {"Locked",file.Locked.ToString(),inserted.Locked.ToString()}
                }));
            report.AppendLine($"{originalName} -> {name}; user {entry.User} -> {user}; payload {file.Data.Length} B; type {file.FileType}; LOAD={file.LoadAddress:X4}; EXEC={file.ExecuteAddress:X4}; RO/SYS/ARC={file.ReadOnly}/{file.System}/{file.Archived}; lock={file.Locked}; payload verification passed.");
            report.AppendLine($"  Target metadata: type={inserted.FileType}; LOAD={inserted.LoadAddress:X4}; EXEC={inserted.ExecuteAddress:X4}; user={inserted.User}; RO/SYS/ARC={inserted.ReadOnly}/{inserted.System}/{inserted.Archived}; lock={inserted.Locked}. Filesystem-only fields not represented by the target are listed as changes/losses.");
        }
        var output = candidate.Image.Serialize(); var reopened = target.Reopen(output);
        if (DskAnalyzer.Analyze(reopened).Errors != 0) throw new InvalidDataException("Reopened target failed validation.");
        report.AppendLine($"Target free space after: {reopened.FileSystem.FreeBytes} B\nTarget directory after: {DirectoryCapacity(reopened.FileSystem)}\nDirectory capacity/allocation: insertion and reopen validation passed.\nMetadata changes / losses:\n{string.Join("\n", changes)}\nDirectory slots, allocation positions and format-specific raw directory fields are rebuilt. Supported metadata is preserved as listed above.\nSource remains unchanged. Apply changes only the target in memory; Save / Save As is separate.");
        return new(new(target.Serialize(), output, report.ToString(), target), changes, targetKeys,fileChanges);
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
