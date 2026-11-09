using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Views;

// Tools are applications/actions, not permanent workspaces.
public sealed partial class MainWindow
{
    private MenuItem? comImportMenu;

    private void ConfigureMainMenus()
    {
        var file = MainMenu.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Header, "_File"));
        if (file is not null && !file.Items.OfType<MenuItem>().Any(m => Equals(m.Header, "New _disk…")))
        {
            // Keep all New actions together; existing New Tape and New QuickDisk bindings remain.
            file.Items.Insert(2, new MenuItem { Header = "New _disk…", Command = Model.NewDiskCommand });
        }

        // Drop the development/readiness top-level menu. Each document keeps its own Report tab.
        var reports = MainMenu.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Header, "_Report") || Equals(m.Header, "_Reports"));
        if (reports is not null) MainMenu.Items.Remove(reports);

        var tools = new MenuItem { Header = "_Tools" };
        var audio = new MenuItem { Header = "Audio Analyzer…" };
        audio.Click += async (_, _) => await OpenAudioForAnalysisAsync();
        tools.Items.Add(audio);
        tools.Items.Add(new MenuItem { Header = "Recorder…", Command = Model.RecorderCommand });
        tools.Items.Add(new MenuItem { Header = "Batch processing…", Command = Model.BatchPreviewCommand });
        tools.Items.Add(new Separator());

        var convert = new MenuItem { Header = "MZF / M12 → COM…" };
        convert.Click += async (_, _) => await ConvertNativeProgramAsync();
        tools.Items.Add(convert);
        comImportMenu = new MenuItem { Header = "Import MZF / M12 as COM into CP/M disk…" };
        comImportMenu.Click += async (_, _) => await ImportNativeComIntoDiskAsync();
        tools.Items.Add(comImportMenu);
        MainMenu.Items.Insert(Math.Min(1, MainMenu.Items.Count), tools);
        UpdateComImportMenu();
    }

    private void UpdateComImportMenu()
    {
        if (comImportMenu is null) return;
        var disk = Model.ActiveDisk;
        comImportMenu.IsEnabled = !Model.Documents.IsBusy && disk is not null && !disk.HasDraft &&
            disk.HasProjection && NativeComImportService.UnavailableReason(disk.Model) is null;
    }

    private Task OpenAudioForAnalysisAsync()
    {
        var analysis=new AnalysisWindow();analysis.Show(this);
        return Task.CompletedTask;
    }

    private async Task ImportNativeComIntoDiskAsync()
    {
        var disk = Model.ActiveDisk;
        var dialogs = Model.DiskDialogs;
        if (disk is null || dialogs is null) return;
        await Model.Documents.ExecuteAsync(async ct =>
        {
            var paths = await dialogs.PickDiskFilesAsync(FilePickerPurpose.ImportNativeComSource,"Select MZF / M12 to import as COM");
            if (paths.Count == 0) return false;
            return await Model.ImportNativeComFromPathAsync(disk, paths[0], ct);
        });
    }

    private async Task ConvertNativeProgramAsync()
    {
        var dialogs = Model.DiskDialogs;
        if (dialogs is null) return;
        await Model.Documents.ExecuteAsync(async ct =>
        {
            var paths = await dialogs.PickDiskFilesAsync(FilePickerPurpose.ConvertNativeComSource,"Select MZF / M12 to convert to COM");
            var path = paths.FirstOrDefault();
            if (path is null) return false;
            if (Path.GetExtension(path).ToLowerInvariant() is not (".mzf" or ".m12" or ".mz0" or ".mz7"))
                throw new InvalidDataException("Select a standalone MZF or M12 program.");
            var sourceHash = AtomicFileTransaction.Hash(path);
            var image = await Task.Run(() => MzfNativeImageParser.Read(path), ct);
            var profiles = CpmTargetProfile.BuiltIns.Select(p => p.Name).ToArray();
            var modes = Enum.GetNames<NativeMachineMode>();
            var compressions = NativeComCompressionChoice.Choices.Select(c => c.Label).ToArray();
            var options = await dialogs.DiskFormAsync("MZF/M12 → COM", [
                new("target", "Target CP/M", profiles[0], profiles),
                new("mode", "Machine mode", nameof(NativeMachineMode.Mz800Monitor), modes),
                new("ceiling", "COM ceiling (hex)", "C000"),
                new("compression", "COM compression", compressions[0], compressions)
            ], NativeComSourceCompression.Text(image) + "\nCreates a native COM loader with verified source LOAD/EXEC and memory compatibility. This does not modify the MZF/M12.");
            if (options is null) return false;
            int ceiling = DiskSession.Hex16(options["ceiling"]);
            if (ceiling is < 0x1200 or > 0xE000)
                throw new InvalidDataException("COM ceiling must be hexadecimal 1200..E000.");
            var conversion = await Task.Run(() => NativeComConversionService.Build(image,
                CpmTargetProfile.Create(options["target"], ceiling),
                Enum.Parse<NativeMachineMode>(options["mode"]),
                NativeComCompressionChoice.Choices.Single(c => c.Label == options["compression"]).Options,
                cancellationToken: ct), ct);
            if (AtomicFileTransaction.Hash(path) != sourceHash)
                throw new IOException("Source was changed while converting.");
            if (!await dialogs.ConfirmDiskAsync("COM conversion preview", conversion.Report +
                $"\n\nOutput: {conversion.Bytes.Length:N0} bytes. Source remains unchanged.")) return false;
            string defaultName = NativeComConversionService.SuggestName(image.Name);
            var output = await dialogs.PickDiskOutputAsync("Save COM", defaultName);
            if (output is null) return false;
            if (!Path.GetExtension(output).Equals(".com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a .com output path.");
            if (AtomicFileTransaction.Hash(path) != sourceHash)
                throw new IOException("Source was changed before export.");
            await Model.Documents.WriteExportAsync(new Dictionary<string, byte[]> { [output] = conversion.Bytes }, ct);
            return true;
        });
    }
}
