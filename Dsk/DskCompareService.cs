using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal enum DskDiffLevel { Container, PhysicalSectors, Filesystem }
internal enum DskDiffState { Same, Changed, OnlyInLeft, OnlyInRight, Unavailable }
internal sealed record DskDiffItem(DskDiffLevel Level, string Name, DskDiffState State, string Detail,
    byte[]? LeftBytes = null, byte[]? RightBytes = null,
    DskSectorAddress? LeftAddress = null, DskSectorAddress? RightAddress = null,
    string? LeftFileKey = null, string? RightFileKey = null,
    byte[]? LeftStructure = null, byte[]? RightStructure = null)
{
    public string Status => State switch { DskDiffState.OnlyInLeft => "only in left", DskDiffState.OnlyInRight => "only in right", _ => State.ToString().ToLowerInvariant() };
    public bool HasHexDiff => LeftBytes != null || RightBytes != null || LeftStructure != null || RightStructure != null;
}
internal sealed record DskByteDifference(int Offset, byte? Left, byte? Right)
{
    public string Address => $"0x{Offset:X8}";
    public string LeftHex => Left?.ToString("X2") ?? "--";
    public string RightHex => Right?.ToString("X2") ?? "--";
}
internal sealed record DskComparison(byte[] LeftImage, byte[] RightImage, DskLayoutModel LeftLayout, DskLayoutModel RightLayout,
    IReadOnlyList<DskDiffItem> Items, string LeftName, string RightName)
{
    internal bool Identical => LeftImage.AsSpan().SequenceEqual(RightImage);
    internal string Summary => (Identical ? "Images are byte-identical." : "Images differ.") + "\n" +
        string.Join(" | ", Enum.GetValues<DskDiffLevel>().Select(level => $"{level}: {Items.Count(i => i.Level == level && i.State is not (DskDiffState.Same or DskDiffState.Unavailable))} differences"));
}

internal static class DskCompareService
{
    internal static IEnumerable<DskByteDifference> ByteDifferences(byte[]? left, byte[]? right)
    {
        int count = Math.Max(left?.Length ?? 0, right?.Length ?? 0);
        for (int i = 0; i < count; i++)
        {
            byte? a = left != null && i < left.Length ? left[i] : null;
            byte? b = right != null && i < right.Length ? right[i] : null;
            if (a != b) yield return new(i, a, b);
        }
    }

    internal static DskComparison Compare(DskDocument leftDocument, DskDocument rightDocument)
    {
        // Work exclusively on snapshots, including unsaved edits. Parsing/analysis never touches the originals.
        byte[] leftBytes = leftDocument.Serialize(), rightBytes = rightDocument.Serialize();
        var left = DskDocument.Open(leftBytes); var right = DskDocument.Open(rightBytes);
        var lm = DskAnalyzer.Analyze(left); var rm = DskAnalyzer.Analyze(right);
        var rows = new List<DskDiffItem>();
        void Value(DskDiffLevel level, string name, string? a, string? b, DskSectorAddress? la = null, DskSectorAddress? ra = null)
            => rows.Add(new(level, name, State(a, b), $"Left: {a ?? "absent"}\nRight: {b ?? "absent"}", LeftAddress: la, RightAddress: ra));
        void Bytes(DskDiffLevel level, string name, byte[]? a, byte[]? b, DskSectorAddress? la = null, DskSectorAddress? ra = null)
            => rows.Add(new(level, name, State(a, b), $"Left: {a?.Length.ToString() ?? "absent"} B\nRight: {b?.Length.ToString() ?? "absent"} B\nChanged byte offsets: {ByteDifferences(a, b).Count()}", a, b, la, ra));
        Value(DskDiffLevel.Container, "Track count", left.Image.TrackCount.ToString(), right.Image.TrackCount.ToString());
        Value(DskDiffLevel.Container, "Side count", left.Image.SideCount.ToString(), right.Image.SideCount.ToString());
        Value(DskDiffLevel.Container, "Creator", left.Image.Creator, right.Image.Creator);
        Bytes(DskDiffLevel.Container, "Raw container header", leftBytes[..256], rightBytes[..256]);
        Bytes(DskDiffLevel.Container, "Container trailing data", left.Image.ContainerTrailingData, right.Image.ContainerTrailingData);
        int trackCount = Math.Max(left.Image.Tracks.Count, right.Image.Tracks.Count);
        for (int t = 0; t < trackCount; t++)
        {
            var lt = t < left.Image.Tracks.Count ? left.Image.Tracks[t] : null;
            var rt = t < right.Image.Tracks.Count ? right.Image.Tracks[t] : null;
            string? Shape(DskImage.DskTrack? track, bool present) => !present ? null : track == null ? "missing track (tsize=0)" : $"block size {track.BlockSize} B; {DskDocumentFactory.TrackGeometry(track)}";
            var la = lt?.Sectors.Count > 0 ? new DskSectorAddress(t, 0) : null;
            var ra = rt?.Sectors.Count > 0 ? new DskSectorAddress(t, 0) : null;
            Value(DskDiffLevel.Container, $"Track {t}: geometry/block size", Shape(lt, t < left.Image.Tracks.Count), Shape(rt, t < right.Image.Tracks.Count), la, ra);
            byte[]? Header(DskImage.DskTrack? track, byte[] image) => track == null ? null : image.AsSpan(track.FileOffset, 256).ToArray();
            byte[]? Padding(DskImage.DskTrack? track, byte[] image) => track == null ? null :
                image.AsSpan(track.FileOffset + 256 + track.Sectors.Sum(s => s.Data.Length), track.BlockSize - 256 - track.Sectors.Sum(s => s.Data.Length)).ToArray();
            Bytes(DskDiffLevel.Container, $"Track {t}: raw header/descriptors", Header(lt, leftBytes), Header(rt, rightBytes), la, ra);
            Bytes(DskDiffLevel.Container, $"Track {t}: padding", Padding(lt, leftBytes), Padding(rt, rightBytes), la, ra);
            for (int s = 0; s < Math.Max(lt?.Sectors.Count ?? 0, rt?.Sectors.Count ?? 0); s++)
            {
                var a = lt != null && s < lt.Sectors.Count ? lt.Sectors[s] : null;
                var b = rt != null && s < rt.Sectors.Count ? rt.Sectors[s] : null;
                var ad = a?.SerializeDescriptor(); var bd = b?.SerializeDescriptor();
                bool metadata = !Equal(ad, bd), data = !Equal(a?.Data, b?.Data);
                string Identity(DskImage.DskSector? sector) => sector == null ? "absent" : $"C/H/R/N={sector.Cylinder}/{sector.Side}/{sector.SectorId}/{sector.SizeCode}; {sector.Data.Length} B; ST1/ST2={sector.FdcStatus1:X2}/{sector.FdcStatus2:X2}";
                string details = $"Physical identity: track index {t}, descriptor index {s}. C/H/R/N are compared at this position, not matched by R alone.\nLeft: {Identity(a)}\nRight: {Identity(b)}\n" +
                    $"Metadata changed: {metadata}\nData changed: {data}\nByte count changed: {a?.Data.Length != b?.Data.Length}\nST1/ST2 changed: {a?.FdcStatus1 != b?.FdcStatus1 || a?.FdcStatus2 != b?.FdcStatus2}\nChanged data byte offsets: {ByteDifferences(a?.Data, b?.Data).Count()}";
                rows.Add(new(DskDiffLevel.PhysicalSectors, $"Track {t}, sector index {s}", a == null ? DskDiffState.OnlyInRight : b == null ? DskDiffState.OnlyInLeft : metadata || data ? DskDiffState.Changed : DskDiffState.Same,
                    details, a?.Data, b?.Data, a == null ? null : new(t, s), b == null ? null : new(t, s), LeftStructure: ad, RightStructure: bd));
            }
        }
        if (left.FileSystem.Type != right.FileSystem.Type || left.FileSystem.Type is DskFileSystemType.Raw or DskFileSystemType.BootOnly || lm.Errors != 0 || rm.Errors != 0)
            rows.Add(new(DskDiffLevel.Filesystem, "Filesystem comparison", DskDiffState.Unavailable,
                $"Logical comparison unavailable: left={left.FileSystem.DisplayName}, right={right.FileSystem.DisplayName}. Requires the same recognized filesystem family and consistent allocation. Physical/container comparison remains available."));
        else
        {
            try
            {
                string Key(DskFileEntry e, bool cpm) => (cpm ? $"{e.User}:" : "") + e.Name + (e.Extension.Length == 0 ? "" : "." + e.Extension);
                var af = left.FileSystem.ReadDirectory().ToDictionary(e => Key(e, left.FileSystem is CpmFileSystem), StringComparer.OrdinalIgnoreCase);
                var bf = right.FileSystem.ReadDirectory().ToDictionary(e => Key(e, right.FileSystem is CpmFileSystem), StringComparer.OrdinalIgnoreCase);
                if (left.FileSystem is CpmFileSystem ac && right.FileSystem is CpmFileSystem bc)
                    Value(DskDiffLevel.Filesystem, "CP/M DPB and physical allocation mapping", Dpb(ac.Dpb), Dpb(bc.Dpb));
                if (left.FileSystem is FsmzFileSystem ax && right.FileSystem is FsmzFileSystem bx)
                {
                    Value(DskDiffLevel.Filesystem, "FSMZ directory variant", ax.DirectoryLimit.ToString(), bx.DirectoryLimit.ToString());
                    Bytes(DskDiffLevel.Filesystem, "FSMZ DINFO / volume / bounds / allocation bitmap", ax.DinfoSnapshot(), bx.DinfoSnapshot(), ById(left, 1, 16), ById(right, 1, 16));
                }
                if (left.FileSystem is MrsFileSystem am && right.FileSystem is MrsFileSystem bm)
                    Bytes(DskDiffLevel.Filesystem, "MRS FAT ownership / reserved blocks", am.AllocationSnapshot(), bm.AllocationSnapshot(), ById(left, 4, 1), ById(right, 4, 1));
                foreach (string key in af.Keys.Union(bf.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                {
                    af.TryGetValue(key, out var a); bf.TryGetValue(key, out var b);
                    byte[]? dataA = a == null ? null : left.FileSystem.Extract(a), dataB = b == null ? null : right.FileSystem.Extract(b);
                    byte[]? nativeA = a == null ? null : Native(left.FileSystem, a), nativeB = b == null ? null : Native(right.FileSystem, b);
                    string? metadataA = a == null ? null : Metadata(left.FileSystem, a), metadataB = b == null ? null : Metadata(right.FileSystem, b);
                    string? allocationA = a == null ? null : Allocation(left.FileSystem, a, lm), allocationB = b == null ? null : Allocation(right.FileSystem, b, rm);
                    bool data = !Equal(dataA, dataB), metadata = metadataA != metadataB, allocation = allocationA != allocationB;
                    rows.Add(new(DskDiffLevel.Filesystem, key, a == null ? DskDiffState.OnlyInRight : b == null ? DskDiffState.OnlyInLeft : data || metadata || allocation ? DskDiffState.Changed : DskDiffState.Same,
                        $"Content changed: {data}\nMetadata changed: {metadata}\nAllocation changed: {allocation}\n\nLeft metadata: {metadataA ?? "absent"}\nRight metadata: {metadataB ?? "absent"}\n\nLeft allocation: {allocationA ?? "absent"}\nRight allocation: {allocationB ?? "absent"}\n\nCP/M user is part of file identity; a user-area move is shown as only-left / only-right, without guessing file correspondence.",
                        dataA, dataB, FileAddress(lm, a), FileAddress(rm, b), a?.Key, b?.Key, nativeA, nativeB));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException)
            {
                rows.RemoveAll(r => r.Level == DskDiffLevel.Filesystem);
                rows.Add(new(DskDiffLevel.Filesystem, "Filesystem comparison", DskDiffState.Unavailable, "Ambiguous or unreadable filesystem: " + ex.Message));
            }
        }
        return new(leftBytes, rightBytes, lm, rm, rows,
            (leftDocument.FilePath ?? "Left image") + (leftDocument.IsModified ? " (unsaved edits)" : ""),
            (rightDocument.FilePath ?? "Right image") + (rightDocument.IsModified ? " (unsaved edits)" : ""));
    }

    private static bool Equal(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
    private static string Dpb(CpmDpb d) => $"{d.Name}: SPT={d.Spt}, BSH={d.Bsh}, BLM={d.Blm}, EXM={d.Exm}, DSM={d.Dsm}, DRM={d.Drm}, AL0={d.Al0:X2}, AL1={d.Al1:X2}, CKS={d.Cks}, OFF={d.Off}, block size={d.BlockSize}, inverted={d.Inverted}\n" +
        "Physical track map: " + (d.PhysicalTrackMap == null ? "linear" : string.Join(",", d.PhysicalTrackMap)) + "\nPhysical sector map: " +
        (d.PhysicalSectorMap == null ? "linear" : string.Join(";", d.PhysicalSectorMap.Select(s => $"{s.AbsoluteTrack}:{s.SectorId}")));
    private static DskDiffState State(byte[]? a, byte[]? b) => a == null && b != null ? DskDiffState.OnlyInRight : b == null && a != null ? DskDiffState.OnlyInLeft : Equal(a, b) ? DskDiffState.Same : DskDiffState.Changed;
    private static DskDiffState State(string? a, string? b) => a == null && b != null ? DskDiffState.OnlyInRight : b == null && a != null ? DskDiffState.OnlyInLeft : a == b ? DskDiffState.Same : DskDiffState.Changed;
    private static DskSectorAddress? ById(DskDocument d, int t, byte r) => t < d.Image.Tracks.Count && d.Image.Tracks[t] is { } track && track.Sectors.FindIndex(s => s.SectorId == r) is int index && index >= 0 ? new(t, index) : null;
    private static DskSectorAddress? FileAddress(DskLayoutModel map, DskFileEntry? entry) => entry == null ? null : map.Sectors.FirstOrDefault(s => s.Owners.Any(o => o.FileKey == entry.Key))?.Address;
    private static byte[]? Native(IDskFileSystem fs, DskFileEntry e) => fs switch
    {
        FsmzFileSystem f => f.DirectoryMetadata(e), MrsFileSystem m => m.DirectoryMetadata(e),
        CpmFileSystem c => c.DirectoryEntriesFor(e).OrderBy(p => p.Index).SelectMany(p => p.Raw).ToArray(), _ => null
    };
    private static string Metadata(IDskFileSystem fs, DskFileEntry e)
    {
        string details = $"name={e.Name}.{e.Extension}; size={e.Size}; type={e.FileType}; LOAD={e.LoadAddress:X4}; EXEC={e.ExecuteAddress:X4}; user={e.User}; RO={e.ReadOnly}; SYS={e.System}; ARC={e.Archived}; lock={e.Locked}; note={e.Notes}";
        if (fs is CpmFileSystem c) details += "; raw extent metadata (user/name/attrs/EX/S1/S2/RC): " + string.Join(";", c.DirectoryEntriesFor(e).Select(p => Convert.ToHexString(p.Raw.AsSpan(0, 16))).Order());
        if (fs is FsmzFileSystem f) details += "; raw slot metadata: " + Convert.ToHexString(f.DirectoryMetadata(e).AsSpan(0, 30));
        if (fs is MrsFileSystem m)
        {
            var raw = m.DirectoryMetadata(e); details += $"; file ID={raw[11]}; raw directory entry: " + Convert.ToHexString(raw);
        }
        return details;
    }
    private static string Allocation(IDskFileSystem fs, DskFileEntry e, DskLayoutModel map)
    {
        string details = $"slot/key={e.Key}; start/file ID={e.StartBlock}; blocks={e.Blocks}; extents={e.Extents}; " +
            string.Join(";", map.Sectors.SelectMany(s => s.Owners.Where(o => o.FileKey == e.Key).Select(o => $"b{o.Block}/file{o.FileBlock}/ex{o.Extent}@{s.Track}:{s.PhysicalIndex}+{o.Offset}/{o.Length}")).Order());
        if (fs is CpmFileSystem c) details += "; raw extent slots/pointers: " + string.Join(";", c.DirectoryEntriesFor(e).Select(p => $"{p.Index}:{Convert.ToHexString(p.Raw.AsSpan(16))}"));
        return details;
    }
}
