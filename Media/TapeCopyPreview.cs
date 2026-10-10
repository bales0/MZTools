using System;
using System.IO;
using System.Linq;

namespace MZTools;

internal sealed class TapeCopyPreview
{
    private readonly byte[] before;
    private readonly TapeDocumentFormat format;
    private readonly TapeRecord[] records;
    private readonly int insertion;
    internal string Report { get; }

    internal TapeCopyPreview(TapeDocument target, WorkspaceDragPacket packet, int insertion)
    {
        ValidateTarget(target, packet.Records.Length);
        records = WorkspaceDragTransfer.OpenRecords(packet);
        if (insertion < 0 || insertion > target.Records.Count) throw new ArgumentOutOfRangeException(nameof(insertion));
        this.insertion = insertion; format = target.Format;
        before = WorkspaceDragTransfer.Encode(WorkspaceDragTransfer.Capture(target.Records));
        Report = $"COPY {records.Length} record(s) into {target.FilePath ?? "unsaved workspace"}\nInsert at position {insertion + 1}; target records: {target.Records.Count} -> {target.Records.Count + records.Length}\n\n" +
            string.Join("\n", records.Select(r => $"{SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname)}: {r.Body.MzfBody.Length} B; type={r.Header.MzfFtype}; LOAD={r.Header.MzfStart:X4}; EXEC={r.Header.MzfExec:X4}; profile={r.Profile}; trailing={r.Body.TrailingData.Length} B")) +
            "\n\nHeaders, payloads, per-record trailing data and tape profiles are copied. Source container/sidecar binding is not copied.\nDuplicate names remain separate records; existing records are retained.\nSource remains unchanged. Apply changes only the target in memory; Save / Save As is separate.\nThe chosen output format may not store every tape metadata field; review Save options when saving.";
    }

    private static void ValidateTarget(TapeDocument target, int count)
    {
        if (target.IsReadOnlyQuickDisk) throw new InvalidOperationException("This QuickDisk is read-only.");
        if (target.IsQuickDisk && target.Records.Count + count > QuickDiskLimits.StandardDirectoryEntries)
            throw new InvalidDataException($"A standard QuickDisk can contain at most {QuickDiskLimits.StandardDirectoryEntries} records.");
    }

    internal void Apply(TapeDocument target)
    {
        ValidateTarget(target, records.Length);
        if (target.Format != format || !before.AsSpan().SequenceEqual(WorkspaceDragTransfer.Encode(WorkspaceDragTransfer.Capture(target.Records))))
            throw new InvalidOperationException("Target changed after preview. Create a new copy preview.");
        target.Records.InsertRange(insertion, records.Select(r => r.DeepClone()));
        if (target.Format == TapeDocumentFormat.None) target.Format = records.Length == 1 ? TapeDocumentFormat.Mzf : TapeDocumentFormat.Mzt;
        target.IsModified = true;
    }
}
