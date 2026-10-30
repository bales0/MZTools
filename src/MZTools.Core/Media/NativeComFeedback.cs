using System;

namespace MZTools;

// Keep user guidance separate from the complete memory-layout and compression report.
internal static class NativeComFeedback
{
    internal static string? SourceProblem(NativeProgramImage image, bool multipart = false)
    {
        if (!NativeHeaderExecution.SupportedType(image))
            return image.Type == 0x4D
                ? "Cannot convert this type 4D program: its header loader is not a supported jump into the supplied program. Use the original tape; Technical details describes the supported layout."
                : $"Cannot convert this file: its header declares type {image.Type:X2} ({SharpMzEncoding.ConvertFtypeToDescription(image.Type).Trim()}). " +
                    "The converter supports OBJ machine-code programs and validated type 4D header loaders. Other types require their original loader, interpreter or program.";
        if (multipart)
            return "Cannot convert this program: it requires multiple tape parts. COM conversion includes only one independent program. Use the complete tape instead.";
        if (image.Dependencies.Count > 0)
            return "Cannot convert this file: it contains additional data after the program, which may be another tape part. " +
                "The converter cannot determine how to load it. Open a separate, standalone MZF/M12 program or use the complete tape.";
        if (image.Segments.Count != 1)
            return "Cannot convert this program: it loads several separate memory blocks. This converter currently supports a single block only. Use the original tape instead.";
        return null;
    }

    internal static string ConversionProblem(string diagnostic)
    {
        if (diagnostic.Contains("overwrite the executable header", StringComparison.Ordinal))
            return "The embedded ZX7 decoder would replace this program's header loader. Uncheck ZX7 embedded loader or choose ZX0.";
        if (diagnostic.Contains("No safe compression/loader combination", StringComparison.Ordinal))
            return "Cannot create a loadable COM with these settings, even after trying compression. " +
                "The program and its loader must fit in the available memory without overwriting each other. Use the original tape; the individual checks are in Technical details.";
        if (diagnostic.Contains("COM ceiling must", StringComparison.Ordinal))
            return "The memory limit in Advanced settings is invalid. Enter a hexadecimal value between 1200 and E000 (default C000).";
        if (diagnostic.Contains("too large", StringComparison.OrdinalIgnoreCase))
            return "The program is too large to load as a COM with these settings. Choose Auto compression and try again.";
        if (diagnostic.Contains("monitor map", StringComparison.Ordinal))
            return "The program uses memory reserved by the selected machine mode. Check its required MZ-700/MZ-800 mode in Advanced settings. " +
                "All-RAM mode is suitable only for programs that set up their own machine environment.";
        if (diagnostic.Contains("EntryPoint", StringComparison.Ordinal) || diagnostic.Contains("invalid segment/header", StringComparison.Ordinal))
            return "The program has invalid size, load or start information in its header. Check that the source is a complete MZF/M12 file.";
        if (diagnostic.Contains("No safe LOW/HIGH", StringComparison.Ordinal))
            return "The loader would overlap the program in memory. Try Auto compression. If it still fails, use the original tape.";
        if (diagnostic.Contains("round-trip verification failed", StringComparison.Ordinal))
            return "Compression could not be verified: unpacking did not restore the original program. No COM file was created. Try Keep source instead.";
        return "Conversion failed with the selected settings. No COM file was created. Open Technical details for the specific error.";
    }
}
