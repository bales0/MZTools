using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal sealed record DskSectorProperties(int SourceIndex, byte SectorId, byte St1, byte St2);
internal sealed record DskTrackProperties(int TrackIndex, byte Cylinder, byte Side, byte Gap, byte Filler,
    IReadOnlyList<DskSectorProperties> Sectors);

internal sealed class DskOperationPreview
{
    private readonly byte[] original;
    private readonly byte[] result;
    private readonly bool verifyInterpretation;
    private readonly CpmDpbSignature? interpretation;
    internal DskOperationPreview(byte[] original, byte[] result, string report, DskDocument? source = null)
    {
        this.original = (byte[])original.Clone(); this.result = (byte[])result.Clone();
        verifyInterpretation = source != null;
        interpretation = source?.AttachedDpb is { } dpb ? CpmDpbSignature.From(dpb) : null;
        var ranges = new List<(int Offset, int Length)>();
        for (int i = 0; i < Math.Max(original.Length, result.Length); i++)
        {
            if (i < original.Length && i < result.Length && original[i] == result[i]) continue;
            int start = i;
            while (i + 1 < Math.Max(original.Length, result.Length) &&
                (i + 1 >= original.Length || i + 1 >= result.Length || original[i + 1] != result[i + 1])) i++;
            ranges.Add((start, i - start + 1));
        }
        Report = report + $"\nBefore SHA-256: {ImageVerificationService.Hash(original)}\nAfter SHA-256: {ImageVerificationService.Hash(result)}\nChanged byte ranges: {ranges.Count}\n" +
            string.Join("\n", ranges.Take(500).Select(r => $"0x{r.Offset:X}..0x{r.Offset + r.Length - 1:X} ({r.Length} B)")) +
            (ranges.Count > 500 ? "\nOnly the first 500 ranges are displayed; the entire candidate was validated." : "");
    }
    internal string Report { get; }
    internal byte[] Result => (byte[])result.Clone();
    internal void Apply(DskDocument document)
    {
        if (!document.Serialize().AsSpan().SequenceEqual(original)) throw new InvalidOperationException("The document changed after preview. Preview again.");
        if (verifyInterpretation && interpretation != (document.AttachedDpb is { } dpb ? CpmDpbSignature.From(dpb) : null))
            throw new InvalidOperationException("The CP/M interpretation changed after preview. Preview again.");
        if (!original.AsSpan().SequenceEqual(result)) document.ReplaceContents(result);
    }
}

internal static class DskPhysicalPropertyService
{
    internal static DskOperationPreview Preview(DskDocument document, string creator, DskTrackProperties change)
    {
        if (creator.Length > 14 || creator.Any(c => c < 32 || c > 126)) throw new ArgumentException("Creator must contain at most 14 printable ASCII characters.");
        byte[] original = document.Serialize();
        var source = document.Clone(); var candidate = document.Clone();
        if ((uint)change.TrackIndex >= candidate.Image.Tracks.Count || candidate.Image.Tracks[change.TrackIndex] is not { } track)
            throw new InvalidDataException("Select a present physical track.");
        var sectors = track.Sectors.ToArray();
        if (change.Sectors.Count != sectors.Length || !change.Sectors.Select(s => s.SourceIndex).Order().SequenceEqual(Enumerable.Range(0, sectors.Length)))
            throw new InvalidDataException("Descriptor order must be a permutation of all source indexes.");
        candidate.Image.Creator = creator;
        track.Cylinder = change.Cylinder; track.Side = change.Side; track.Gap = change.Gap; track.Filler = change.Filler;
        track.Sectors.Clear();
        foreach (var property in change.Sectors)
        {
            var sector = sectors[property.SourceIndex];
            sector.SectorId = property.SectorId; sector.FdcStatus1 = property.St1; sector.FdcStatus2 = property.St2;
            track.Sectors.Add(sector);
        }
        byte[] output = candidate.Image.Serialize();
        var reopened = document.Reopen(output);
        var before = DskAnalyzer.Analyze(source); var after = DskAnalyzer.Analyze(reopened);
        if (after.Issues.Any(i => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe && i.Code != "DSK_FDC_STATUS" &&
            !(i.Code == "DSK_DUPLICATE_SECTOR_ID" && source.FileSystem.Type is DskFileSystemType.Raw or DskFileSystemType.BootOnly) &&
            !before.Issues.Any(old => old.Code == i.Code && old.Track == i.Track && old.Sector == i.Sector && old.FileKey == i.FileKey)))
            throw new InvalidDataException("Physical changes introduce Analyzer errors/unsafe issues. No changes were applied.\n" + after.Report());
        if (source.FileSystem.Type is not (DskFileSystemType.Raw or DskFileSystemType.BootOnly))
        {
            if (source.FileSystem.Type != reopened.FileSystem.Type || reopened.IsReadOnly) throw new InvalidDataException("Physical changes alter or invalidate the filesystem interpretation.");
            if (source.FileSystem is FsmzFileSystem oldVariant && reopened.FileSystem is FsmzFileSystem newVariant && oldVariant.DirectoryLimit != newVariant.DirectoryLimit)
                throw new InvalidDataException("Creator change would alter the FSMZ directory variant interpretation.");
            var oldFiles = source.FileSystem.ReadDirectory(); var newFiles = reopened.FileSystem.ReadDirectory();
            if (oldFiles.Count != newFiles.Count) throw new InvalidDataException("Directory changed after physical editing.");
            foreach (var old in oldFiles)
            {
                var updated = newFiles.SingleOrDefault(f => f.Key == old.Key && f.Name == old.Name && f.Extension == old.Extension && f.User == old.User);
                if (updated == null || !source.FileSystem.Extract(old).AsSpan().SequenceEqual(reopened.FileSystem.Extract(updated)))
                    throw new InvalidDataException("Physical changes would alter file contents or ownership.");
            }
        }
        // Restrict writes to the creator and selected track header/descriptors/data permutation.
        int start = track.FileOffset, end = start + track.BlockSize;
        if (original.Length != output.Length || Enumerable.Range(0, output.Length).Any(i => original[i] != output[i] &&
            !(i >= 0x22 && i < 0x30) && !(i >= start && i < end)))
            throw new InvalidDataException("Serialization changed bytes outside the selected track and creator.");
        var report = new StringBuilder("Physical Properties Preview\nSave is a separate action. Sector payloads are preserved; FDC status is metadata, not proof of a physical CRC error.\n")
            .AppendLine($"Creator: '{source.Image.Creator}' → '{creator}'")
            .AppendLine($"Track index {change.TrackIndex}: C={source.Image.Tracks[change.TrackIndex]!.Cylinder} → {track.Cylinder}, H={source.Image.Tracks[change.TrackIndex]!.Side} → {track.Side}")
            .AppendLine($"GAP#3: {source.Image.Tracks[change.TrackIndex]!.Gap:X2} → {track.Gap:X2}; filler: {source.Image.Tracks[change.TrackIndex]!.Filler:X2} → {track.Filler:X2}");
        foreach (var p in change.Sectors) report.AppendLine($"Source descriptor #{p.SourceIndex} → ID {p.SectorId}, ST1={p.St1:X2}, ST2={p.St2:X2}");
        report.AppendLine(after.Report());
        return new(original, output, report.ToString(), document);
    }
}
