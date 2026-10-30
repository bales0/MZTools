using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal static class DskDefragmentService
{
    internal static DskOperationPreview Preview(DskDocument document)
    {
        var source = document.Clone();
        var analysis = DskAnalyzer.Analyze(source);
        if (source.IsReadOnly || analysis.Errors != 0 || analysis.Issues.Any(i => i.Code.Contains("ORPHAN", StringComparison.Ordinal) || i.Code is "CPM_EXTENT_GAP" or "CPM_DUPLICATE_EXTENT"))
            throw new InvalidDataException("Defragmentation requires unambiguous ownership with no orphan blocks or incomplete/duplicate extents.");
        if (source.FileSystem is not (FsmzFileSystem or CpmFileSystem or MrsFileSystem)) throw new InvalidDataException("Defragmentation supports FSMZ, CP/M and MRS only.");
        byte[] original = source.Serialize();
        bool native = PersonalCpmSystemInstaller.IsPersonalLayout(source);
        var entries = source.FileSystem.ReadDirectory().OrderBy(e => native && e.User == 0 && e.Name == "PCPM" && e.Extension == "SYS" ? 0 : 1).ThenBy(e =>
            source.FileSystem is CpmFileSystem c ? c.DirectoryEntriesFor(e).Min(p => p.Index) : int.Parse(e.Key)).ToArray();
        var payloads = entries.Select(source.FileSystem.Extract).ToArray();
        var targetImage = DskImage.Parse(original);
        IDskFileSystem targetFs;
        if (source.FileSystem is FsmzFileSystem fsmz)
        {
            FsmzFileSystem.Format(targetImage, fsmz.DirectoryLimit == 127);
            var target = new FsmzFileSystem(targetImage, fsmz.DirectoryLimit == 127);
            target.SetVolume(fsmz.VolumeNumber); target.SetRebuiltBounds(fsmz.FileAreaBlock, fsmz.LastBlock); targetFs = target;
        }
        else if (source.FileSystem is CpmFileSystem cpm)
        {
            if (cpm.ReadRawDirectory().Any(raw => raw[0] != 0xE5 && raw[0] > 15))
                throw new InvalidDataException("CP/M special directory entries/timestamps are not supported by this defragmenter; metadata would be lost.");
            CpmFileSystem.Format(targetImage, cpm.Dpb);
            if (!CpmFileSystem.TryOpen(targetImage, cpm.Dpb, out var target) || target == null) throw new InvalidDataException("The clean CP/M target could not be opened.");
            targetFs = target;
        }
        else
        {
            var mrs = (MrsFileSystem)source.FileSystem;
            if (mrs.FatSectorCount != 3 || mrs.DirectorySectorCount != 6 || mrs.DirectoryBlock != 39)
                throw new InvalidDataException("Defragmentation requires the native 3 FAT + 6 directory sector MRS layout.");
            MrsFileSystem.Format(targetImage);
            if (!MrsFileSystem.TryOpen(targetImage, out var target) || target == null) throw new InvalidDataException("The clean MRS target could not be opened.");
            targetFs = target;
        }
        var mrsMetadata = new List<(DskFileEntry Target, byte[] Original)>();
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            targetFs.Insert(entry.Name + (entry.Extension.Length == 0 ? "" : "." + entry.Extension), payloads[index], entry.FileType, entry.LoadAddress, entry.ExecuteAddress, entry.User);
            var inserted = targetFs.ReadDirectory().Single(e => e.Name == entry.Name && e.Extension == entry.Extension && e.User == entry.User);
            if (targetFs is FsmzFileSystem targetFsmz) targetFsmz.RestoreDirectoryMetadata(inserted, ((FsmzFileSystem)source.FileSystem).DirectoryMetadata(entry));
            else if (targetFs is CpmFileSystem targetCpm)
            {
                var oldExtents = ((CpmFileSystem)source.FileSystem).DirectoryEntriesFor(entry).OrderBy(p => p.Raw[14]).ThenBy(p => p.Raw[12]).ToArray();
                var newExtents = targetCpm.DirectoryEntriesFor(inserted).OrderBy(p => p.Raw[14]).ThenBy(p => p.Raw[12]).ToArray();
                if (oldExtents.Length != newExtents.Length) throw new InvalidDataException("CP/M extent structure cannot be preserved.");
                for (int e = 0; e < oldExtents.Length; e++)
                {
                    oldExtents[e].Raw.AsSpan(0, 16).CopyTo(newExtents[e].Raw);
                    targetCpm.WriteRawDirectoryEntry(newExtents[e].Index, newExtents[e].Raw);
                }
            }
            else mrsMetadata.Add((inserted, ((MrsFileSystem)source.FileSystem).DirectoryMetadata(entry)));
        }
        if (targetFs is MrsFileSystem targetMrs) targetMrs.RestoreDefragmentedMetadata(mrsMetadata);
        byte[] output = targetImage.Serialize();
        var reopened = source.Reopen(output);
        // Preserve an explicitly chosen FSMZ directory variant when native detection is ambiguous.
        if (source.FileSystem is FsmzFileSystem originalFsmz && reopened.FileSystem is FsmzFileSystem newFsmz && originalFsmz.DirectoryLimit != newFsmz.DirectoryLimit)
            throw new InvalidDataException("The FSMZ directory variant could not be retained after reopening.");
        if (reopened.FileSystem.Type != source.FileSystem.Type || DskAnalyzer.Analyze(reopened).Errors != 0) throw new InvalidDataException("Defragmented target did not pass reopen/Analyzer validation.");
        var after = reopened.FileSystem.ReadDirectory();
        if (after.Count != entries.Length) throw new InvalidDataException("Directory count changed.");
        for (int index = 0; index < entries.Length; index++)
        {
            var old = entries[index];
            var current = after.Single(e => e.Name == old.Name && e.Extension == old.Extension && e.User == old.User);
            if (!payloads[index].AsSpan().SequenceEqual(reopened.FileSystem.Extract(current)) || current.FileType != old.FileType || current.LoadAddress != old.LoadAddress || current.ExecuteAddress != old.ExecuteAddress || current.Locked != old.Locked || current.ReadOnly != old.ReadOnly || current.System != old.System || current.Archived != old.Archived)
                throw new InvalidDataException("Defragmentation did not preserve complete payload/metadata.");
            if (source.FileSystem is FsmzFileSystem oldFs)
            {
                byte[] a = oldFs.DirectoryMetadata(old), b = ((FsmzFileSystem)reopened.FileSystem).DirectoryMetadata(current);
                if (!a.AsSpan(0, 30).SequenceEqual(b.AsSpan(0, 30))) throw new InvalidDataException("FSMZ raw directory metadata changed.");
            }
            if (source.FileSystem is MrsFileSystem oldMrs && !oldMrs.DirectoryMetadata(old).AsSpan().SequenceEqual(((MrsFileSystem)reopened.FileSystem).DirectoryMetadata(current)))
                throw new InvalidDataException("MRS raw directory metadata/file ID changed.");
        }
        if (native && entries.Any(e => e.User == 0 && e.Name == "PCPM" && e.Extension == "SYS") && !PersonalCpmSystemInstaller.IsSystemFirst(reopened))
            throw new InvalidDataException("PCPM.SYS must remain in directory entry 0.");
        // Existing boot/system and all sectors outside directory/allocation/file data remain byte-identical.
        var permitted = analysis.Sectors.Where(s => (s.Role & (DskSectorRole.Directory | DskSectorRole.AllocationMap | DskSectorRole.Fat | DskSectorRole.Data | DskSectorRole.Free)) != 0).Select(s => s.Address).ToHashSet();
        foreach (var beforeSector in analysis.Sectors.Where(s => !permitted.Contains(s.Address)))
        {
            var sector = reopened.Image.Tracks[beforeSector.Track]!.Sectors[beforeSector.PhysicalIndex];
            if (!beforeSector.Data.AsSpan().SequenceEqual(sector.Data)) throw new InvalidDataException("Defragmentation changed boot/system or unrelated sector data.");
        }
        var afterAnalysis = DskAnalyzer.Analyze(reopened);
        var report = new StringBuilder($"Defragment {source.FileSystem.DisplayName}\nFiles: {entries.Length}. Payload verification: passed. Metadata verification: passed.\nBoot/system sectors are preserved. Save remains separate.\n");
        int moved = 0;
        foreach (var old in entries)
        {
            var current = after.Single(e => e.Name == old.Name && e.Extension == old.Extension && e.User == old.User);
            int[] beforeBlocks = FileBlocks(analysis, old.Key), afterBlocks = FileBlocks(afterAnalysis, current.Key);
            if (!beforeBlocks.SequenceEqual(afterBlocks)) moved++;
            report.AppendLine($"User {old.User}: {old.Name}{(old.Extension.Length == 0 ? "" : "." + old.Extension)} — old allocation blocks [{string.Join(",", beforeBlocks)}] → new allocation blocks [{string.Join(",", afterBlocks)}]");
        }
        report.AppendLine($"Files moved: {moved}; free space: {source.FileSystem.FreeBytes} → {reopened.FileSystem.FreeBytes} B")
            .AppendLine($"Largest free region: {LargestFreeRegion(source, analysis)} → {LargestFreeRegion(reopened, afterAnalysis)} B (logical allocation order)")
            .AppendLine("Analyzer before:").AppendLine(analysis.Report()).AppendLine("Analyzer after:").AppendLine(afterAnalysis.Report());
        return new(original, output, report.ToString(), document);
    }

    private static int[] FileBlocks(DskLayoutModel analysis, string key) => analysis.Sectors.SelectMany(s => s.Owners)
        .Where(o => o.FileKey == key).OrderBy(o => o.FileBlock).Select(o => o.Block).Distinct().ToArray();

    private static long LargestFreeRegion(DskDocument document, DskLayoutModel analysis)
    {
        int[] free;
        int blockSize;
        if (document.FileSystem is CpmFileSystem cpm)
        {
            var used = analysis.Sectors.SelectMany(s => s.Owners).Select(o => o.Block).ToHashSet();
            int reserved = (cpm.Dpb.Al0 << 8) | cpm.Dpb.Al1;
            free = Enumerable.Range(0, cpm.Dpb.Dsm + 1).Where(b => !used.Contains(b) && (b >= 16 || (reserved & (1 << (15 - b))) == 0)).ToArray();
            blockSize = cpm.Dpb.BlockSize;
        }
        else
        {
            free = analysis.Sectors.Where(s => s.Role.HasFlag(DskSectorRole.Free) && !s.Role.HasFlag(DskSectorRole.Reserved) && s.Owners.Count == 0)
                .SelectMany(s => s.LogicalBlocks).Distinct().Order().ToArray();
            blockSize = document.FileSystem is FsmzFileSystem ? 256 : 512;
        }
        int longest = 0, run = 0, previous = -2;
        foreach (int block in free) { run = block == previous + 1 ? run + 1 : 1; longest = Math.Max(longest, run); previous = block; }
        return (long)longest * blockSize;
    }
}
