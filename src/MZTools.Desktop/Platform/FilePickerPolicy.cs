using Avalonia.Platform.Storage;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

// UI compatibility only: the loader and Core remain responsible for content validation.
internal static class FilePickerPolicy
{
    private static string[] Tape => FormatChoice.All.Where(f => f.Key is "Mzf" or "M12" or "Mzt")
        .Select(f => f.Extension).Concat([".mz0", ".mz7"]).Distinct().ToArray();
    private static string[] QuickDisk => FormatChoice.All.Where(f => f.Key is not ("Mzf" or "M12" or "Mzt"))
        .Select(f => f.Extension).Distinct().ToArray();
    private static readonly string[] Disk = [".dsk", ".hfe"];
    private static readonly string[] Audio = [".wav", ".flac", ".wav.json"];
    private static readonly string[] SignalImport = [".wav", ".lep", ".l16"];
    private static readonly string[] Standalone = [".mzf", ".m12", ".mz0", ".mz7"];

    private static FilePickerFileType Group(string name, IEnumerable<string> extensions) =>
        new(name) { Patterns = extensions.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(e =>
            OperatingSystem.IsLinux() ? CaseVariants("*" + e) : ["*" + e]).ToArray() };

    // GTK globs can be case-sensitive. Enumerate the short suffix only, including .wav.json;
    // no directory enumeration and no broad *.json workaround.
    private static IEnumerable<string> CaseVariants(string pattern)
    {
        IEnumerable<string> variants = [""];
        foreach (char c in pattern)
            variants = variants.SelectMany(prefix => char.IsLetter(c)
                ? new[] { prefix + char.ToLowerInvariant(c), prefix + char.ToUpperInvariant(c) } : [prefix + c]).ToArray();
        return variants;
    }
    private static IReadOnlyList<FilePickerFileType> Groups(string name, params FilePickerFileType[] groups) =>
        new[] { new FilePickerFileType(name) { Patterns = groups.SelectMany(g => g.Patterns!).Distinct().ToArray() } }.Concat(groups).ToArray();
    private static IReadOnlyList<FilePickerFileType> Binary(string name, params string[] extensions) =>
        [Group(name, extensions), new("Any binary file (including nonstandard extensions)") { Patterns = ["*"] }];

    internal static FilePickerOpenOptions Options(FilePickerPurpose purpose, string title, bool multiple = false,
        TapeSession? target = null, string? batchExtensions = null)
    {
        if (purpose == FilePickerPurpose.ImportTapeRecords && target?.Model.IsReadOnlyQuickDisk == true)
            throw new InvalidOperationException("Non-SHARP/unknown QuickDisk is read-only; record import is unavailable.");
        IReadOnlyList<FilePickerFileType> filters = purpose switch
        {
            FilePickerPurpose.OpenMedia => Groups("All supported media", Group("QuickDisk images / containers", QuickDisk),
                Group("Disk images", Disk), Group("Tape containers", Tape), Group("Audio / complete recordings", Audio)),
            FilePickerPurpose.OpenAudio => [Group("Audio captures / complete recordings (*.wav;*.flac;*.wav.json)", Audio)],
            FilePickerPurpose.ImportTapeRecords => Groups("Supported record imports", Group("Tape containers", Tape),
                Group("SHARP QuickDisk records (host validated on import)", QuickDisk), Group("Audio / pulses (WAV, LEP, L16)", SignalImport)),
            FilePickerPurpose.ImportIplPrograms or FilePickerPurpose.ImportNativeComRecord =>
                Groups("Supported program containers", Group("Tape programs", Tape), Group("SHARP QuickDisk programs", QuickDisk)),
            FilePickerPurpose.ImportNativeComSource => [Group("Standalone MZF / M12 program", [".mzf", ".m12"])],
            FilePickerPurpose.ConvertNativeComSource => [Group("Standalone SHARP program", Standalone)],
            FilePickerPurpose.ImportDiskFiles => Binary("MZF program / binary data", ".mzf", ".bin"),
            FilePickerPurpose.ImportCpmDiskFiles => Binary("MZF / M12 (COM conversion) or binary / COM contents", ".mzf", ".m12", ".bin", ".com"),
            FilePickerPurpose.RawSectorImport or FilePickerPurpose.BootstrapImport => Binary("Raw binary data", ".bin"),
            FilePickerPurpose.CopyFromDisk or FilePickerPurpose.CompareDisk or FilePickerPurpose.BootSystemSource => [Group("Disk images (compatible projection required)", Disk)],
            FilePickerPurpose.PatchBaseline => [Group("DSK baseline image", [".dsk"])],
            FilePickerPurpose.ApplyDiskPatch => [Group("MZTools sector patch", [".mzpatch.json", ".json"])],
            FilePickerPurpose.BootSystemOrProfile => [Group("Boot DSK / boot profile", [".dsk", ".json"])],
            FilePickerPurpose.BatchInputs => [Group("Supported batch inputs", (batchExtensions ?? throw new ArgumentNullException(nameof(batchExtensions)))
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))],
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
        return new() { Title = title, AllowMultiple = multiple, FileTypeFilter = filters };
    }

    internal static FilePickerPurpose DiskImportPurpose(DiskSession disk) => disk.Model.FileSystem is RawDskFileSystem
        ? FilePickerPurpose.RawSectorImport : disk.Model.FileSystem is CpmFileSystem
        ? FilePickerPurpose.ImportCpmDiskFiles : FilePickerPurpose.ImportDiskFiles;

    internal static bool Matches(FilePickerOpenOptions options, string path) => options.FileTypeFilter!
        .SelectMany(g => g.Patterns!).Any(p => p is "*" or "*.*" || path.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase));

    internal static void Validate(FilePickerOpenOptions options, IEnumerable<string> paths)
    {
        foreach (string path in paths)
            if (!Matches(options, path)) throw new InvalidDataException("Unsupported input for this operation: " + path);
    }
}
