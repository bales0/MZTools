using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal sealed record HfeSemanticInspection(bool Available, string Report, string Filesystem = "Unknown",
    string Layout = "", int FileCount = 0, long UsedBytes = 0, long FreeBytes = 0,
    string BootType = "Unknown", string BootProfile = "", string BootableState = "Unverified")
{
    internal BatchContentItem[] Contents { get; init; } = [];
}

internal static class HfeSemanticInspectionService
{
    internal static HfeSemanticInspection Inspect(HfeImage source)
    {
        // Build only a private sector projection. Never expose an editable document or write
        // this synthetic container back over the physical image.
        try
        {
            if (source.Tracks.Any(t => t.WeakBitMask.Any(b => b != 0) || t.Sectors.Any(s => !s.HeaderCrcValid || !s.DataCrcValid)))
                return new(false, "Filesystem inspection unavailable: weak cells or CRC errors prevent a certain sector interpretation. Physical inspection remains available.");
            var document = DskDocument.Open(MediaConversionService.HfeToDsk(source).Output);
            if (document.FileSystem is not (FsmzFileSystem or CpmFileSystem or MrsFileSystem))
                return new(false, "No validated Sharp filesystem was recognized. Physical inspection remains available.");
            // A decoded Sharp sector layout requires matching address fields and exact ID sets.
            // Rotational order is retained in the projection; it is not a filesystem address map.
            foreach (var track in source.Tracks)
            {
                int firstId = track.Sectors.Min(s => (int)s.R);
                if (track.Sectors.Any(s => s.C != track.Cylinder || s.H != track.Side || s.DataMark != 0xFB) ||
                    !track.Sectors.Select(s => (int)s.R).Order().SequenceEqual(Enumerable.Range(firstId, track.Sectors.Count)) ||
                    (firstId != 1 && !(document.FileSystem is CpmFileSystem cpm && cpm.Dpb.Name == CpmDpb.Sds400.Name && track.Side == 0 && firstId == 11)))
                    return new(false, "Decoded sector identities do not exactly match a known Sharp layout. Physical inspection remains available.");
            }
            var analysis = DskAnalyzer.Analyze(document);
            if (analysis.Errors != 0 || document.IsReadOnly)
                return new(false, "Filesystem projection failed validation. No semantic interpretation was attached.\n" + analysis.Report());
            var files = document.FileSystem.ReadDirectory();
            foreach (var file in files) document.FileSystem.Extract(file);
            var boot = DskBootInfo.Inspect(document);
            string profile = CpmSystemProfileRegistry.TryResolveVerifiedSource(document)?.DisplayName ?? "";
            var report = new StringBuilder("HFE filesystem inspection — read-only\nPrivate projection from CRC-valid decoded sectors; HFE cells, timing and source bytes remain unchanged.\nContainer creator, gaps and padding in this projection are synthetic and are not capture evidence.\n")
                .AppendLine($"Filesystem / layout: {document.FileSystem.DisplayName}")
                .AppendLine($"Files: {files.Count}; used: {document.FileSystem.UsedBytes} B; free: {document.FileSystem.FreeBytes} B")
                .AppendLine($"Boot/system: {boot.System}; bootable state: {boot.Bootable}; profile: {profile}");
            foreach (var file in files)
                report.AppendLine($"User {file.User}: {file.Name}{(file.Extension.Length == 0 ? "" : "." + file.Extension)}; {file.Size} B; type={file.FileType}; LOAD={file.LoadAddress:X4}; EXEC={file.ExecuteAddress:X4}; locked={file.Locked}; RO/SYS/ARC={file.ReadOnly}/{file.System}/{file.Archived}");
            report.AppendLine(analysis.Report());
            return new(true, report.ToString(), document.FileSystem.Type.ToString(), document.FileSystem.DisplayName,
                files.Count, document.FileSystem.UsedBytes, document.FileSystem.FreeBytes, boot.System, profile, boot.Bootable)
            { Contents = files.Select((file, index) => BatchContentItem.FromDisk(file, index + 1)).ToArray() };
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or InvalidOperationException)
        {
            return new(false, "Filesystem inspection unavailable: " + e.Message + "\nPhysical inspection remains available.");
        }
    }
}
