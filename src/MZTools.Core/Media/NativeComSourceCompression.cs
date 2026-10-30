namespace MZTools;

internal static class NativeComSourceCompression
{
    internal const string UnknownToolTip = "No supported ZX0/ZX7 loader signature was recognized. Other or custom compression may be present.";

    internal static string Text(NativeProgramImage? image)
    {
        if (image == null) return "Source compression: —";
        if (image.Compression is not { } info) return "Source compression: Not detected (none or unsupported)";
        return $"Source compression: {info.Algorithm.ToString().ToUpperInvariant()} · {info.Direction} · {(info.EmbeddedLoader ? "Embedded loader" : "Separate loader")}";
    }

    internal static string? ToolTip(NativeProgramImage? image) => image == null ? null
        : image.Compression == null ? UnknownToolTip
        : "Recognized source loader and encoding. This information is independent of the output compression policy.";
}
