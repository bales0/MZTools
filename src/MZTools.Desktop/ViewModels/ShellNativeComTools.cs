using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

// UI integration only. COM generation and import stay in the shared Core.
public sealed partial class ShellViewModel
{
    // Replace the Avalonia disk Import command after the desktop host is constructed.
    // Non-CP/M and IPL paths retain the original import behavior.
    internal void EnableNativeComDiskImport()
    {
        DiskImportCommand = new AsyncCommand(async () =>
        {
            var disk = ActiveDisk;
            if (disk is null || DiskDialogs is null) return;
            await Documents.ExecuteAsync(async ct =>
            {
                if (disk.CanEditIpl) return await EditIplAsync(disk, "IPL add programs", ct);
                var paths = await DiskDialogs.PickDiskFilesAsync("Import files to DSK / HFE", true);
                if (paths.Count == 0) return false;

                bool nativeSource = paths.Count == 1 && IsComSource(paths[0]);
                if (nativeSource && disk.HasProjection && disk.Model.FileSystem is CpmFileSystem)
                {
                    var answer = await DiskDialogs.DiskFormAsync("Import native program into CP/M", [
                        new("action", "Import method", "Convert to executable COM", ["Convert to executable COM", "Import with existing disk rules"])
                    ], "Convert uses the shared MZF/M12 → COM loader and validates the destination CP/M image. Original file import keeps the source bytes; it is not a runnable COM.");
                    if (answer is null) return false;
                    if (answer["action"] == "Convert to executable COM")
                        return await ImportNativeComFromPathAsync(disk, paths[0], ct);
                }
                var preview = await Task.Run(() => DiskOperations.Import(disk, paths, ct), ct);
                return await AcceptDiskAsync(disk, preview, ct);
            });
        }, () => CanUseDisk && DiskDialogs is not null &&
                 (ActiveDisk?.CanEdit == true || ActiveDisk?.CanEditIpl == true) && ActiveDisk?.HasDraft == false);
        Changed(nameof(DiskImportCommand));
        DiskImportCommand.Refresh();
    }

    private static bool IsComSource(string path) =>
        Path.GetExtension(path).Equals(".mzf", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".m12", StringComparison.OrdinalIgnoreCase);

    internal async Task<bool> ImportNativeComFromPathAsync(DiskSession disk, string path, CancellationToken ct)
    {
        if (DiskDialogs is null) return false;
        if (NativeComImportService.UnavailableReason(disk.Model) is { } reason)
            throw new InvalidOperationException(reason);

        if (!IsComSource(path))
            throw new InvalidDataException("Select a standalone .mzf or .m12 source for COM import.");
        string initialHash = AtomicFileTransaction.Hash(path)
            ?? throw new FileNotFoundException("The source program was not found.", path);
        var image = await Task.Run(() => MzfNativeImageParser.Read(path), ct);
        var profiles = CpmTargetProfile.BuiltIns.Select(p => p.Name).ToArray();
        var modes = Enum.GetNames<NativeMachineMode>();
        var compression = NativeComCompressionChoice.Choices.Select(c => c.Label).ToArray();
        var options = await DiskDialogs.DiskFormAsync("MZF/M12 → COM · CP/M import", [
            new("target", "CP/M target", profiles[0], profiles),
            new("mode", "Machine mode", nameof(NativeMachineMode.Mz800Monitor), modes),
            new("ceiling", "COM ceiling (hex)", "C000"),
            new("compression", "COM compression", compression[0], compression),
            new("name", "COM filename", NativeComConversionService.SuggestName(image.Name)),
            new("collision", "Existing file", "Cancel", Enum.GetNames<DiskCollisionPolicy>())
        ], "Native loader compatibility, original compression, address ranges and disk capacity are checked. Source remains unchanged.");
        if (options is null) return false;

        int ceiling = DiskSession.Hex16(options["ceiling"]);
        if (ceiling is < 0x1200 or > 0xE000)
            throw new InvalidDataException("COM ceiling must be hexadecimal 1200..E000.");
        var result = await Task.Run(() => NativeComConversionService.Build(image,
            CpmTargetProfile.Create(options["target"], ceiling),
            Enum.Parse<NativeMachineMode>(options["mode"]),
            NativeComCompressionChoice.Choices.Single(c => c.Label == options["compression"]).Options,
            cancellationToken: ct), ct);
        if (AtomicFileTransaction.Hash(path) != initialHash)
            throw new IOException("The source program changed during conversion.");
        var converted = NativeComImportService.Preview(disk.Model, options["name"], result,
            Enum.Parse<DiskCollisionPolicy>(options["collision"]));
        var proposed = DiskOperations.Preview(disk, c => converted.Operation.Apply(c), converted.Operation.Report);
        if (!await DiskDialogs.ConfirmDiskAsync("Import COM into CP/M image", proposed.Report)) return false;
        ct.ThrowIfCancellationRequested();
        if (AtomicFileTransaction.Hash(path) != initialHash)
            throw new IOException("The source program changed before import.");
        disk.Apply(proposed.Candidate, proposed.Revision);
        return true;
    }
}
