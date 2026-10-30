using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace MZTools;

internal interface IRepairAction
{
    string IssueCode { get; }
    string Description { get; }
    DskRepairRisk Risk { get; }
    DskOperationPreview Preview(DskDocument document);
    void Apply(DskDocument document, DskOperationPreview preview);
    void PostValidation(DskDocument document);
}

internal static class DskFilesystemRepairService
{
    internal static IRepairAction For(DskDocument document) => document.FileSystem switch
    {
        FsmzFileSystem => new FsmzAllocationRepair(),
        MrsFileSystem => new MrsBlockCountRepair(),
        CpmFileSystem when PersonalCpmSystemInstaller.IsPersonalLayout(document) => new PersonalCpmDirectoryRepair(),
        _ => throw new InvalidDataException("No unambiguous metadata repair is available. CP/M has no allocation bitmap to rebuild.")
    };
}

internal sealed class MrsBlockCountRepair : IRepairAction
{
    public string IssueCode => "MRS_BLOCK_COUNT_MISMATCH";
    public string Description => "Reconcile MRS directory block counts with uniquely owned FAT blocks; retain 3 FAT + 6 directory sectors.";
    public DskRepairRisk Risk => DskRepairRisk.SafeDeterministic;
    public DskOperationPreview Preview(DskDocument document)
    {
        var candidate = document.Clone();
        if (candidate.FileSystem is not MrsFileSystem fs || fs.FatSectorCount != 3 || fs.DirectorySectorCount != 6)
            throw new InvalidDataException("Safe MRS count repair requires the native 3 FAT + 6 directory sector layout.");
        var analysis = DskAnalyzer.Analyze(candidate);
        if (analysis.Issues.Any(i => (i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe && i.Code != IssueCode) || i.Code is "MRS_FAT_ORPHAN_BLOCK" or "MRS_DUPLICATE_FILE_ID"))
            throw new InvalidDataException("MRS ownership is ambiguous; no safe count repair is available.");
        var fat = fs.AllocationSnapshot(); var entries = fs.ReadDirectory();
        foreach (var entry in entries)
        {
            int count = fat.Take(candidate.Image.Tracks.Count * 9).Count(b => b == entry.StartBlock);
            if (count == 0) throw new InvalidDataException("A directory file has no FAT-owned blocks; its intended payload cannot be reconstructed.");
            fs.SetBlockCount(entry, checked((ushort)count));
        }
        var output = candidate.Image.Serialize(); PostValidation(document.Reopen(output));
        var original = document.Serialize();
        var permitted = analysis.Sectors.Where(s => s.Role.HasFlag(DskSectorRole.Directory)).SelectMany(s => Enumerable.Range(checked((int)s.FileOffset), s.DataLength).Where(i => (i - s.FileOffset) % 32 is 14 or 15)).ToHashSet();
        if (original.Length != output.Length || Enumerable.Range(0, output.Length).Any(i => original[i] != output[i] && !permitted.Contains(i))) throw new InvalidDataException("MRS repair changed bytes outside directory block-count fields.");
        return new(original, output, IssueCode + "\n" + Description + "\nAffected structures: MRS directory block counts. Affected file payloads: none.\nOnly directory block-count fields change. All FAT and sector payload bytes remain identical.\nAnalyzer before:\n" + analysis.Report() + "\nAnalyzer after:\n" + DskAnalyzer.Analyze(document.Reopen(output)).Report(), document);
    }
    public void Apply(DskDocument document, DskOperationPreview preview) { PostValidation(document.Reopen(preview.Result)); preview.Apply(document); }
    public void PostValidation(DskDocument document) { if (document.FileSystem is not MrsFileSystem || DskAnalyzer.Analyze(document).Errors != 0) throw new InvalidDataException("MRS post-validation failed."); }
}

internal sealed class PersonalCpmDirectoryRepair : IRepairAction
{
    public string IssueCode => "PCPM_SYS_NOT_FIRST_DIRECTORY_ENTRY";
    public string Description => "Move the existing initial PCPM.SYS extent to directory slot 0; preserve every extent and allocation byte.";
    public DskRepairRisk Risk => DskRepairRisk.SafeDeterministic;
    public DskOperationPreview Preview(DskDocument document)
    {
        var candidate = document.Clone();
        if (!PersonalCpmSystemInstaller.IsPersonalLayout(candidate) || candidate.FileSystem is not CpmFileSystem fs || DskAnalyzer.Analyze(candidate).Errors != 0) throw new InvalidDataException("Native P-CP/M80 repair requires valid, unique directory/allocation ownership.");
        var system = PersonalCpmSystemInstaller.FindSystemFile(candidate) ?? throw new InvalidDataException("PCPM.SYS is missing; no system file is fabricated.");
        var extents = fs.DirectoryEntriesFor(system);
        if (extents.Count != 1 || !PersonalCpmSystemInstaller.IsSystemDirectoryEntry(extents[0].Raw)) throw new InvalidDataException("PCPM.SYS extent metadata is ambiguous.");
        int index = extents[0].Index;
        if (index != 0)
        {
            var first = fs.ReadRawDirectoryEntry(0);
            if (first[0] != 0xE5 && first[0] > 15) throw new InvalidDataException("Directory slot 0 contains special metadata; no automatic relocation is available.");
            fs.WriteRawDirectoryEntry(0, extents[0].Raw); fs.WriteRawDirectoryEntry(index, first);
        }
        var output = candidate.Image.Serialize(); var reopened = document.Reopen(output); PostValidation(reopened);
        var before = document.FileSystem.ReadDirectory(); var after = reopened.FileSystem.ReadDirectory();
        if (before.Count != after.Count || before.Any(old => !document.FileSystem.Extract(old).AsSpan().SequenceEqual(reopened.FileSystem.Extract(after.Single(e => e.Key == old.Key))))) throw new InvalidDataException("Directory repair changed file contents.");
        return new(document.Serialize(), output, IssueCode + "\n" + Description + $"\nAffected structures: directory entries 0 and {index}; files: PCPM.SYS and the previous slot 0 entry.\nSwap existing directory entries 0 and {index}; no sectors or allocation blocks move. Save remains separate.\nAnalyzer before:\n" + DskAnalyzer.Analyze(document).Report() + "\nAnalyzer after:\n" + DskAnalyzer.Analyze(reopened).Report(), document);
    }
    public void Apply(DskDocument document, DskOperationPreview preview) { PostValidation(document.Reopen(preview.Result)); preview.Apply(document); }
    public void PostValidation(DskDocument document) { if (!PersonalCpmSystemInstaller.IsSystemFirst(document) || DskAnalyzer.Analyze(document).Errors != 0) throw new InvalidDataException("Native directory repair failed validation."); }
}

internal sealed class FsmzAllocationRepair : IRepairAction
{
    public string IssueCode => "FSMZ_BITMAP_MISMATCH / FSMZ_USED_COUNTER_MISMATCH";
    public string Description => "Add missing directory-owned allocation bits and rebuild used count; preserve orphan allocations.";
    public DskRepairRisk Risk => DskRepairRisk.SafeDeterministic;
    public DskOperationPreview Preview(DskDocument document)
    {
        var source = document.Clone();
        if (source.FileSystem is not FsmzFileSystem fs) throw new InvalidDataException("Repair requires a recognized FSMZ directory and valid DINFO bounds.");
        var analysis = DskAnalyzer.Analyze(source);
        if (analysis.Issues.Any(i => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe && i.Code is not ("FSMZ_BITMAP_MISMATCH" or "FS_LEGACY_WARNING")))
            throw new InvalidDataException("Allocation ownership is ambiguous; no safe bitmap repair is available.");
        byte[] original = source.Serialize(); byte[] info = fs.DinfoSnapshot();
        foreach (var entry in fs.ReadDirectory())
            for (int block = entry.StartBlock; block < entry.StartBlock + entry.Blocks; block++)
            {
                int r = block - fs.FileAreaBlock;
                if (r < 0 || r >= 2000 || block > fs.LastBlock) throw new InvalidDataException("File ownership lies outside DINFO bounds.");
                info[6 + r / 8] |= (byte)(1 << (r % 8));
            }
        int count = Enumerable.Range(0, 2000).Count(r => (info[6 + r / 8] & (1 << (r % 8))) != 0);
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2, 2), checked((ushort)(fs.FileAreaBlock + count)));
        var candidate = source.Clone();
        new DskBlockDevice(candidate.Image).WriteSector(1, 16, info, true);
        var output = candidate.Image.Serialize(); var reopened = source.Reopen(output);
        PostValidation(reopened);
        foreach (var old in source.FileSystem.ReadDirectory())
        {
            var current = reopened.FileSystem.ReadDirectory().Single(e => e.Key == old.Key);
            if (!source.FileSystem.Extract(old).AsSpan().SequenceEqual(reopened.FileSystem.Extract(current))) throw new InvalidDataException("Repair changed file data.");
        }
        int offset = checked((int)analysis.Sectors.Single(s => s.Track == 1 && s.R == 16).FileOffset);
        if (original.Length != output.Length || Enumerable.Range(0, output.Length).Any(i => original[i] != output[i] && (i < offset || i >= offset + 256))) throw new InvalidDataException("Repair changed unrelated bytes.");
        return new(original, output, IssueCode + "\n" + Description + $"\nAffected structures: DINFO allocation bitmap and used counter. Affected file payloads: none.\nAllocated data blocks: {count}; used count: {fs.FileAreaBlock + count}.\nOrphan bits remain allocated; their data is not discarded.\nAnalyzer before:\n" + analysis.Report() + "\nAnalyzer after:\n" + DskAnalyzer.Analyze(reopened).Report(), document);
    }
    public void Apply(DskDocument document, DskOperationPreview preview) { PostValidation(document.Reopen(preview.Result)); preview.Apply(document); }
    public void PostValidation(DskDocument document)
    {
        if (document.FileSystem is not FsmzFileSystem || DskAnalyzer.Analyze(document).Errors != 0) throw new InvalidDataException("Repaired filesystem failed post-validation.");
    }
}
