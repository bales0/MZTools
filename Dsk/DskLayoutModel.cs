using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MZTools;

[Flags]
internal enum DskSectorRole
{
    Unknown = 0, Boot = 1, System = 2, Directory = 4, AllocationMap = 8,
    Fat = 16, Data = 32, Free = 64, Reserved = 128, Padding = 256, Missing = 512, Invalid = 1024,
    NativeIpl = 2048, SystemFile = 4096
}

internal enum DskIssueSeverity { Info, Warning, Error, Unsafe }

internal sealed record DskSectorAddress(int Track, int Sector);
internal sealed record DskAllocationOwner(string FileKey, string FileName, int Block,
    int LogicalSector, int Offset, int Length, int FileBlock, int? Extent = null, int? User = null);

internal sealed class DskSectorLayout
{
    public required DskSectorAddress Address { get; init; }
    public int Track => Address.Track;
    public int PhysicalIndex => Address.Sector;
    public string SelectionLabel => $"T{Track} : #{PhysicalIndex} — R={R}, {Role}";
    public byte C { get; init; }
    public byte H { get; init; }
    public byte R { get; init; }
    public byte N { get; init; }
    public byte St1 { get; init; }
    public byte St2 { get; init; }
    public required byte[] Data { get; init; }
    public int DataLength => Data.Length;
    public int DeclaredDataLength { get; init; }
    public long DescriptorFileOffset { get; init; }
    public long FileOffset { get; init; }
    public DskSectorRole Role { get; internal set; }
    public List<DskAllocationOwner> Owners { get; } = new();
    public List<int> LogicalBlocks { get; } = new();
    public List<int> AllocationBlocks { get; } = new();
    public bool HasIssue { get; internal set; }
    public string Files => string.Join(", ", Owners.Select(o => o.FileName).Distinct());
    public string Detail => $"Physical track: {Track}\nCylinder: {C}\nSide: {H}\nPhysical sector index: {PhysicalIndex}\nSector ID R: {R}\nSize code N: {N}\nData size: {DataLength} B\nStored descriptor length (+6/+7): {DeclaredDataLength} B\nDescriptor length image offset: 0x{DescriptorFileOffset + 6:X}\nFile offset: 0x{FileOffset:X}\nST1: {St1:X2}\nST2: {St2:X2}\nFilesystem role: {Role}\nLogical blocks: {string.Join(", ", LogicalBlocks)}\nAllocation blocks: {string.Join(", ", AllocationBlocks)}\n" +
        string.Join("\n", Owners.Select(o => $"File: {o.FileName} [{o.FileKey}], file block: {o.FileBlock}, user: {o.User}, extent: {o.Extent}, logical 128B sector: {o.LogicalSector}, bytes {o.Offset}..{o.Offset + o.Length - 1}"));
}

internal sealed record DskTrackLayout(int PhysicalIndex, int Cylinder, int Side, long FileOffset,
    int BlockSize, int PaddingLength, bool IsMissing, IReadOnlyList<DskSectorLayout> Sectors);

internal sealed record DskAnalysisIssue(string Code, DskIssueSeverity Severity, string Description,
    string Detail, int? Track = null, int? Sector = null, int? Block = null, string? FileKey = null)
{
    public string Location => string.Join(" / ", new[] {
        Track is int t ? $"Track {t}" : null, Sector is int s ? $"Sector index {s}" : null,
        Block is int b ? $"Block {b}" : null, FileKey != null ? $"File {FileKey}" : null }.Where(s => s != null));
    public string Explanation => DskIssueHelp.DetailedText(this);
}

internal sealed class DskLayoutModel
{
    public string FileSystem { get; internal set; } = "Raw / unknown";
    public int Cylinders { get; internal set; }
    public int Sides { get; internal set; }
    public int FileCount { get; internal set; }
    public List<DskTrackLayout> Tracks { get; } = new();
    public List<DskAnalysisIssue> Issues { get; } = new();
    public IEnumerable<DskSectorLayout> Sectors => Tracks.SelectMany(t => t.Sectors);
    public int Errors => Issues.Count(i => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe);
    public int Warnings => Issues.Count(i => i.Severity == DskIssueSeverity.Warning);
    public int Unsafe => Issues.Count(i => i.Severity == DskIssueSeverity.Unsafe);
    public int Information => Issues.Count(i => i.Severity == DskIssueSeverity.Info);
    public string Summary => $"Image: {(Errors > 0 ? "Errors" : Warnings > 0 ? "Warnings" : "OK")} | Filesystem: {FileSystem}\nTracks: {Cylinders} × {Sides} | Sectors: {Sectors.Count()} | Files: {FileCount}\nErrors/Unsafe: {Errors} | Warnings: {Warnings} | Info: {Issues.Count(i => i.Severity == DskIssueSeverity.Info)}";
    public string Report()
    {
        var report = new StringBuilder("MZTools DSK analysis — read-only\n").AppendLine(Summary);
        foreach (var issue in Issues) report.AppendLine().AppendLine(issue.Explanation);
        return report.ToString();
    }
}
