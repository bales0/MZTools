using System;
using System.IO;
using System.Linq;

namespace MZTools;

internal sealed record NativeComImportPreview(DskOperationPreview Operation, string FileKey);

internal static class NativeComImportService
{
    internal static string? UnavailableReason(DskDocument? disk) => disk == null
        ? "No current disk. Open a writable CP/M disk to import; Export / Save is available without a disk."
        : disk.IsReadOnly ? "The current disk is read-only."
        : disk.FileSystem is not CpmFileSystem ? "Import requires a CP/M filesystem. COM files cannot be run from this filesystem."
        : null;

    internal static NativeComImportPreview Preview(DskDocument disk, string name, MzNativeComResult conversion)
    {
        if (UnavailableReason(disk) is { } reason) throw new InvalidOperationException(reason);
        if (!conversion.Plan.Supported) throw new InvalidDataException(conversion.Plan.UnsupportedReason ?? "Unsupported conversion.");
        var original = disk.Serialize();
        var candidate = disk.Clone();
        if (DskAnalyzer.Analyze(candidate).Errors != 0) throw new InvalidDataException("Resolve current disk allocation/container errors before importing.");
        DskFileTransferService.ValidateName(name, candidate.FileSystem);
        if (!Path.GetExtension(name).Equals(".COM", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Use a CP/M 8.3 filename with the .COM extension.");
        if (candidate.FileSystem.ReadDirectory().Any(e => e.User == 0 &&
            (e.Name + "." + e.Extension).Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{name} already exists in user 0. Choose another name; existing files are preserved.");
        candidate.FileSystem.Insert(name, conversion.Bytes);
        candidate.MarkModified();
        byte[] output = candidate.Serialize();
        var reopened = disk.Reopen(output);
        if (reopened.FileSystem is not CpmFileSystem || reopened.IsReadOnly || DskAnalyzer.Analyze(reopened).Errors != 0)
            throw new InvalidDataException("Imported COM failed filesystem validation; current disk unchanged.");
        var entry = reopened.FileSystem.ReadDirectory().Single(e => e.User == 0 &&
            (e.Name + "." + e.Extension).Equals(name, StringComparison.OrdinalIgnoreCase));
        byte[] extracted = reopened.FileSystem.Extract(entry);
        if (extracted.Length < conversion.Bytes.Length || !extracted.AsSpan(0, conversion.Bytes.Length).SequenceEqual(conversion.Bytes))
            throw new InvalidDataException("Imported COM payload verification failed; current disk unchanged.");
        string report = conversion.Report + $"\nImport to current disk: {disk.FilePath ?? "unsaved image"}\nFile: {name}; CP/M user: 0\nCP/M record padding: {extracted.Length - conversion.Bytes.Length} B\nThe tape header is embedded for the native loader, not stored as CP/M file metadata.\nApply modifies the current image in memory. Save / Save As writes the image to disk.";
        return new(new DskOperationPreview(original, output, report, disk), entry.Key);
    }
}
