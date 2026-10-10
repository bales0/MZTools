namespace MZTools;

internal static class QuickDiskFormatLabels
{
    internal static string Display(TapeDocumentFormat format) => format switch
    {
        TapeDocumentFormat.QdSharpLegacy => "Sharp/MZ legacy",
        TapeDocumentFormat.QdHxc => "HxC physical",
        TapeDocumentFormat.QdFlashFloppy => "Uniform-track physical QD",
        _ => format.ToString()
    };
    internal static string Display(QdImageFormat format) => format switch
    {
        QdImageFormat.SharpLegacyLogical => "Sharp/MZ legacy",
        QdImageFormat.HxcPhysical => "HxC physical",
        QdImageFormat.FlashFloppyPhysical => "Uniform-track physical QD",
        _ => "Unknown"
    };
}
