using System;
using System.IO;
using System.Linq;

namespace MZTools;

internal static class TapeSavePolicy
{
    internal static bool SupportsEmpty(TapeDocumentFormat format) => format is
        TapeDocumentFormat.Mzt or TapeDocumentFormat.Mzq or TapeDocumentFormat.Qdf or
        TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy;
    internal static string Extension(TapeDocumentFormat format) => format switch
    {
        TapeDocumentFormat.Mzf => ".mzf", TapeDocumentFormat.M12 => ".m12", TapeDocumentFormat.Mzt => ".mzt",
        TapeDocumentFormat.Mzq => ".mzq", TapeDocumentFormat.Qdf => ".qdf",
        TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy => ".qd",
        _ => throw new NotSupportedException("Unsupported document format.")
    };
    internal static bool CanPreserveImported(TapeDocument document, TapeDocumentFormat output) =>
        document.IsQuickDisk && !document.IsModified && output == document.Format;
    internal static bool Validate(TapeDocument document, TapeDocumentFormat output, string extension, out string error)
    {
        if (document.IsReadOnlyQuickDisk) { error = "Non-SHARP/unknown QuickDisk is read-only. Use the inspector's decoded-data export."; return false; }
        if (document.Records.Count == 0 && !SupportsEmpty(output)) { error = $"The {extension.ToUpperInvariant()} format requires one file."; return false; }
        bool quick = output is TapeDocumentFormat.Mzq or TapeDocumentFormat.Qdf or TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy;
        if (quick && !QuickDiskLimits.TryValidateForSave(document.Records.Count, CanPreserveImported(document, output), out error)) return false;
        if (output == TapeDocumentFormat.Qdf && QDFFileReader.HeaderSize + document.Records.Sum(r => (long)QDFFileReader.FileOverhead + r.Body.DataSize) > QDFFileReader.ImageSize)
        { error = $"The selected files need {QDFFileReader.HeaderSize + document.Records.Sum(r => (long)QDFFileReader.FileOverhead + r.Body.DataSize)} bytes, but a QDF image can contain only {QDFFileReader.ImageSize} bytes."; return false; }
        if (output == TapeDocumentFormat.QdSharpLegacy && !SharpLegacyQdCodec.TryValidateCapacity(document.Records, out error)) return false;
        if (output is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy && !QuickDiskPhysicalWriter.TryValidateCapacity(document.Records,
            output == TapeDocumentFormat.QdHxc ? QdImageFormat.HxcPhysical : QdImageFormat.FlashFloppyPhysical, out error,
            output == document.Format ? document.QuickDiskProfile : null)) return false;
        error = ""; return true;
    }
}
