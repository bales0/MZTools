using System;
using System.IO;
using System.Linq;

namespace MZTools;

internal enum DskFilePropertyKind { None, Cpm, Mrs }
internal sealed record DskFileProperties(int User, bool ReadOnly, bool System, bool Archived, ushort Load, ushort Execute)
{
    internal static DskFileProperties From(DskFileEntry entry) => new(entry.User, entry.ReadOnly, entry.System,
        entry.Archived, entry.LoadAddress, entry.ExecuteAddress);
}

internal static class DskFilePropertyService
{
    internal static DskFileEntry Apply(DskDocument document, DskFileEntry entry, DskFileProperties properties)
    {
        var kind = DskCapabilityService.GetFilePropertyKind(document);
        if (kind == DskFilePropertyKind.None) throw new InvalidOperationException("Editable file properties are not available for this filesystem or read-only image.");
        byte[] original = document.Serialize();
        var candidate = DskDocument.Open(original);
        if (DskCapabilityService.GetFilePropertyKind(candidate) != kind) throw new InvalidDataException("Filesystem detection changed before editing.");
        var before = document.FileSystem.ReadDirectory();
        var selected = candidate.FileSystem.ReadDirectory().SingleOrDefault(e => e.Key == entry.Key && e.Name == entry.Name && e.Extension == entry.Extension)
            ?? throw new FileNotFoundException("The selected file no longer exists. Refresh the file list.");
        if (kind == DskFilePropertyKind.Cpm)
        {
            if (properties.Load != selected.LoadAddress || properties.Execute != selected.ExecuteAddress)
                throw new InvalidDataException("CP/M does not natively store LOAD/EXEC metadata.");
            ((CpmFileSystem)candidate.FileSystem).UpdateAttributes(selected, properties.User, properties.ReadOnly, properties.System, properties.Archived);
        }
        else
        {
            if (properties.User != selected.User || properties.ReadOnly != selected.ReadOnly || properties.System != selected.System || properties.Archived != selected.Archived)
                throw new InvalidDataException("MRS does not support CP/M user/attribute fields.");
            ((MrsFileSystem)candidate.FileSystem).SetAddresses(selected, properties.Load, properties.Execute);
        }
        byte[] output = candidate.Image.Serialize();
        var reopened = DskDocument.Open(output);
        if (DskCapabilityService.CompatibilityKey(reopened) != DskCapabilityService.CompatibilityKey(document) ||
            reopened.IsReadOnly || DskAnalyzer.Analyze(reopened).Errors != 0)
            throw new InvalidDataException("Changed properties did not pass filesystem validation; the document was not modified.");
        var after = reopened.FileSystem.ReadDirectory();
        if (before.Count != after.Count) throw new InvalidDataException("Directory count changed unexpectedly.");
        foreach (var old in before)
        {
            var updated = after.SingleOrDefault(e => old.Key == entry.Key
                ? e.Name == old.Name && e.Extension == old.Extension && (kind != DskFilePropertyKind.Cpm || e.User == properties.User)
                : e.Key == old.Key);
            if (updated == null || !document.FileSystem.Extract(old).AsSpan().SequenceEqual(reopened.FileSystem.Extract(updated)))
                throw new InvalidDataException("File data changed unexpectedly; properties were not applied.");
        }
        var edited = after.Single(e => e.Name == selected.Name && e.Extension == selected.Extension &&
            (kind != DskFilePropertyKind.Cpm || e.User == properties.User));
        if (!original.AsSpan().SequenceEqual(output)) document.ReplaceContents(output);
        return edited;
    }
}
