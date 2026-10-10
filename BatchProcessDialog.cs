using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools;

internal sealed class BatchProcessDialog : Window
{
    private BatchPreview? preview;
    private readonly List<string> selected = new();
    private CancellationTokenSource? running;
    private bool closeAfterStop;
    internal BatchProcessDialog(Window owner)
    {
        Owner = owner; Title = "Batch Process"; Width = 1120; Height = 800; MinWidth = 800; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(12) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var controls = new StackPanel();
        var controlScroll = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 480 };
        root.Children.Add(controlScroll);
        var footer = new StackPanel(); Grid.SetRow(footer, 2); root.Children.Add(footer);
        SizeChanged += (_, _) => controlScroll.MaxHeight = Math.Max(100, ActualHeight - footer.ActualHeight - 220);
        var summary = new TextBlock { Text = "Select files or a folder, then Preview. Preview does not write outputs.", TextWrapping = TextWrapping.Wrap }; footer.Children.Add(summary);
        var progressBar = new ProgressBar { Name = "BatchProgress", Height = 6, Margin = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
        footer.Children.Add(progressBar);
        var progress = new Progress<BatchProgress>(value =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsVisible || running?.IsCancellationRequested == true || progressBar.Visibility != Visibility.Visible) return;
                progressBar.Maximum = Math.Max(1, value.Total); progressBar.Value = value.Completed;
                summary.Text = $"{value.Stage}: {value.Completed}/{value.Total} — {Path.GetFileName(value.Input)}";
            }));
        });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal }; footer.Children.Add(actions);
        var stop = new Button { Name = "StopBatch", Content = "Stop", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 90, Margin = new Thickness(4) };
        footer.Children.Add(stop);
        stop.Click += (_, _) => { running?.Cancel(); stop.IsEnabled = false; summary.Text = "Stopping… Completed outputs are kept; pending work will be cancelled."; };
        Closing += (_, e) =>
        {
            if (running == null) return;
            e.Cancel = true; closeAfterStop = true; running.Cancel(); stop.IsEnabled = false;
            summary.Text = "Stopping before closing…";
        };
        void Row(string label, FrameworkElement editor, Button? browse = null)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) }; controls.Children.Add(row);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(editor, 1); row.Children.Add(editor);
            if (browse != null) { browse.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(browse, 2); row.Children.Add(browse); }
        }
        TextBox Field(string label, string value, Button? browse = null)
        {
            var box = new TextBox { Text = value }; Row(label, box, browse); return box;
        }
        var operation = new ComboBox { Name = "BatchOperation" };
        foreach (BatchOperation value in new[] { BatchOperation.TestIntegrity }.Concat(Enum.GetValues<BatchOperation>().Where(value => value is not (BatchOperation.Analyze or BatchOperation.TestIntegrity)))) operation.Items.Add(new ComboBoxItem { Content = BatchMediaPolicy.Label(value), Tag = value });
        operation.SelectedIndex = 0; Row("Operation", operation);
        BatchOperation CurrentOperation() => (BatchOperation)((ComboBoxItem)operation.SelectedItem).Tag;
        var operationHint = new TextBlock { Name = "OperationHint", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) }; controls.Children.Add(operationHint);
        var sourceModes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) }; controls.Children.Add(sourceModes);
        var filesMode = new RadioButton { Name = "SelectedFilesMode", Content = "Selected files", GroupName = "InputSource", IsChecked = true, Margin = new Thickness(0, 0, 20, 0) };
        var folderMode = new RadioButton { Name = "InputFolderMode", Content = "Input folder", GroupName = "InputSource" };
        sourceModes.Children.Add(filesMode); sourceModes.Children.Add(folderMode);
        var chooseFiles = new Button { Content = "Add files..." }; var chooseFolder = new Button { Content = "Browse..." }; var clearFiles = new Button { Name = "ClearFileList", Content = "Clear file list" };
        var selectedPaths = new TextBox { Name = "SelectedFilePaths", IsReadOnly = true, Text = "No files selected" };
        Row("Selected files", selectedPaths, chooseFiles);
        var folder = Field("Input folder", "", chooseFolder); folder.Name = "InputFolder";
        var extensions = Field("Folder extensions", BatchMediaPolicy.Extensions(CurrentOperation())); extensions.Name = "ExtensionFilter";
        var recursive = new CheckBox { Content = "Recursive folder scan" }; controls.Children.Add(recursive);
        var target = new ComboBox { Name = "ConversionTarget", ItemsSource = BatchMediaPolicy.Targets, SelectedIndex = 0 };
        bool SupportsCurrent(string path) => BatchMediaPolicy.Supports(CurrentOperation(), path, (string)target.SelectedItem);
        string CurrentExtensions() => BatchMediaPolicy.Extensions(CurrentOperation(), (string)target.SelectedItem);
        var sameAsSource = new TextBox { Text = "Same as source", IsReadOnly = true, IsEnabled = false };
        var targetField = new Grid(); targetField.Children.Add(target); targetField.Children.Add(sameAsSource); Row("Conversion target", targetField);
        var extractFormat = new ComboBox { Name = "ExtractFormat", ItemsSource = new[] { "Native", "BIN", "MZF", "M12", "MZT", "WAV", "FLAC", "LEP", "L16" }, SelectedIndex = 0 };
        Row("Extract output format", extractFormat);
        var compression = new CompressionOptionsControl { Name = "BatchCompression", Visibility = Visibility.Collapsed };
        compression.ConfigureTarget(CompressionTarget.MzfTape);
        compression.SetOptions(new(MzfCompressionAlgorithm.Auto)); controls.Children.Add(compression);
        var compressionHint = new TextBlock { Text = "Compression works on decoded program data, including recognized packed input. Batch output requires Skip bytes = 0. Audio input produces an MZT; QD input keeps its container.", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; controls.Children.Add(compressionHint);
        var sampleRate = new ComboBox { Name = "AudioSampleRate", ItemsSource = new[] { 44100, 22050 }, SelectedIndex = 0 }; Row("WAV / FLAC rate (Hz)", sampleRate);
        var heuristic = new CheckBox { Name = "UseHeuristicAnalysis", Content = "Use heuristic analysis (WAV / FLAC)", ToolTip = "Unchecked: standard decoder. Checked: heuristic timing/channel analysis and checksum-based recovery. Applies to audio inputs only." }; controls.Children.Add(heuristic);
        var chooseAuxiliary = new Button { Content = "Browse..." };
        var auxiliary = Field("Boot source / patch", "", chooseAuxiliary);
        var chooseOutput = new Button { Content = "Browse..." };
        var output = Field("Output folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MZTools Batch"), chooseOutput);
        var naming = Field("Naming template", "{name}_processed{ext}");
        var strict = new CheckBox { Content = "Strict all-or-nothing preflight (any invalid/incompatible input blocks execution)" }; controls.Children.Add(strict);
        var replace = new CheckBox { Content = "Replace originals (requires explicit confirmation)" }; controls.Children.Add(replace);
        var backup = new CheckBox { Content = "Keep .bak when replacing originals", IsChecked = true }; controls.Children.Add(backup);
        var listArea = new DockPanel(); Grid.SetRow(listArea, 1); root.Children.Add(listArea);
        var listHeader = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(listHeader, Dock.Top); listArea.Children.Add(listHeader);
        DockPanel.SetDock(clearFiles, Dock.Right); listHeader.Children.Add(clearFiles);
        listHeader.Children.Add(new TextBlock { Text = "Selected inputs / preview results", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        var grid = new DataGrid { Name = "InputFileList", AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, Margin = new Thickness(0, 6, 0, 10) }; listArea.Children.Add(grid);
        foreach (string property in new[] { "Input", "DetectedFormat", "Filesystem", "Operation", "Result", "Integrity", "Reason", "Output" }) grid.Columns.Add(new DataGridTextColumn { Header = property, Binding = new Binding(property), Width = property is "Input" or "Output" or "Reason" ? 230 : 135 });
        var dryRun = new Button { Content = "Preview / dry-run", Margin = new Thickness(4) }; var execute = new Button { Content = "Execute preview", IsEnabled = false, Margin = new Thickness(4) }; var report = new Button { Content = "Export TXT / CSV / JSON report...", IsEnabled = false, Margin = new Thickness(4) }; var details = new Button { Content = "Selected details...", Margin = new Thickness(4) }; actions.Children.Add(dryRun); actions.Children.Add(execute); actions.Children.Add(details); actions.Children.Add(report);
        int inputVersion = 0;
        bool closed = false;
        Closed += (_, _) => { closed = true; inputVersion++; };
        async Task ShowFolderInputs(int version)
        {
            string inputFolder = folder.Text;
            string filter = extensions.Text;
            bool recurse = recursive.IsChecked == true;
            try
            {
                await Task.Delay(250);
                if (closed || version != inputVersion) return;
                BatchOperation selectedOperation = CurrentOperation();
                string selectedTarget = (string)target.SelectedItem;
                string[] paths = await Task.Run(() => BatchProcessService.Scan(inputFolder, recurse, filter).Where(p => BatchMediaPolicy.Supports(selectedOperation, p, selectedTarget)).ToArray());
                if (closed || version != inputVersion) return;
                grid.ItemsSource = paths.Select(path => new BatchRow { Input = path, Operation = BatchMediaPolicy.Label(selectedOperation), Result = "Selected", Reason = "Preview required" }).ToArray();
                summary.Text = $"Input folder: {paths.Length} files. Preview to validate the operation.";
            }
            catch (Exception exception)
            {
                if (!closed && version == inputVersion) summary.Text = $"Cannot scan input folder: {exception.Message}";
            }
        }
        void Invalidate()
        {
            if (running != null) return;
            int version = ++inputVersion;
            extensions.ToolTip = "Folder scan filter. This operation/target accepts: " + CurrentExtensions();
            preview = null; execute.IsEnabled = false; report.IsEnabled = false; details.IsEnabled = false;
            selectedPaths.Text = selected.Count switch { 0 => "No files selected", 1 => selected[0], _ => $"{selected.Count} files selected — see list below" };
            selectedPaths.ToolTip = string.Join("\n", selected);
            grid.ItemsSource = selected.Select(path => new BatchRow { Input = path, Operation = BatchMediaPolicy.Label(CurrentOperation()), Result = SupportsCurrent(path) ? "Selected" : "Unsupported input", Reason = SupportsCurrent(path) ? "Preview required" : "Choose an operation/target for this file type." }).ToArray();
            bool fromFolder = folderMode.IsChecked == true && !string.IsNullOrWhiteSpace(folder.Text);
            summary.Text = fromFolder ? "Scanning input folder..." : $"Selected files: {selected.Count}. Preview to validate the operation.";
            if (fromFolder) _ = ShowFolderInputs(version);
            target.IsEnabled = CurrentOperation() == BatchOperation.ConvertFormat;
            target.Visibility = target.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            sameAsSource.Visibility = target.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
            sameAsSource.Text = CurrentOperation() is BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms
                ? "Program container (MZF/M12; MZT for audio; same QD container)" : "Same as source";
            extractFormat.IsEnabled = CurrentOperation() == BatchOperation.ExtractAll;
            operationHint.Text = CurrentOperation() switch
            {
                BatchOperation.TestIntegrity => "Lists contents, sizes, addresses and metadata, and tests available structure, CRC/checksums and recognized compressed streams in the same run. See Integrity checks for results and format limits. MZF/M12 have no payload checksum.",
                BatchOperation.ExtractAll => "Export each file/program into its own input's output directory. Native keeps disk filenames/data and exports tape programs as MZF. Tape/audio formats require stored type/LOAD/EXEC metadata; CP/M raw files use Native or BIN.",
                _ => "Preview prepares and verifies the selected operation before any user outputs are written."
            };
            compression.Visibility = CurrentOperation() == BatchOperation.CompressPrograms ? Visibility.Visible : Visibility.Collapsed;
            compressionHint.Visibility = CurrentOperation() is BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms ? Visibility.Visible : Visibility.Collapsed;
            sampleRate.IsEnabled = target.IsEnabled && target.SelectedItem is "WAV" or "FLAC" || extractFormat.IsEnabled && extractFormat.SelectedItem is "WAV" or "FLAC";
            heuristic.IsEnabled = CurrentExtensions().Contains(".wav", StringComparison.Ordinal);
            bool usesAuxiliary = CurrentOperation() is BatchOperation.InstallBootSystem or BatchOperation.ApplyPatch;
            auxiliary.IsEnabled = usesAuxiliary; chooseAuxiliary.IsEnabled = usesAuxiliary;
            backup.IsEnabled = replace.IsChecked == true;
            bool analysisOnly = BatchMediaPolicy.ReadOnly(CurrentOperation());
            if (analysisOnly) replace.IsChecked = false;
            output.IsEnabled = chooseOutput.IsEnabled = naming.IsEnabled = replace.IsEnabled = !analysisOnly;
            backup.IsEnabled = !analysisOnly && replace.IsChecked == true;
            bool tapeOutput = CurrentOperation() is BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms || target.IsEnabled && BatchTapeService.Targets.Contains((string)target.SelectedItem);
            if (tapeOutput) { replace.IsChecked = false; replace.IsEnabled = backup.IsEnabled = false; }
            if (extractFormat.IsEnabled) { replace.IsChecked = false; replace.IsEnabled = backup.IsEnabled = false; }
        }
        filesMode.Checked += (_, _) => { folder.Clear(); Invalidate(); };
        folderMode.Checked += (_, _) => { selected.Clear(); Invalidate(); };
        folder.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(folder.Text)) { selected.Clear(); folderMode.IsChecked = true; }
            Invalidate();
        };
        foreach (var box in new[] { extensions, auxiliary, output, naming }) box.TextChanged += (_, _) => Invalidate();
        foreach (var box in new[] { recursive, strict, replace, backup, heuristic }) { box.Checked += (_, _) => Invalidate(); box.Unchecked += (_, _) => Invalidate(); }
        operation.SelectionChanged += (_, _) =>
        {
            extensions.Text = CurrentExtensions();
            Invalidate();
        };
        compression.OptionsChanged += (_, _) => Invalidate();
        sampleRate.SelectionChanged += (_, _) => Invalidate();
        extractFormat.SelectionChanged += (_, _) => Invalidate();
        target.SelectionChanged += (_, _) => { extensions.Text = CurrentExtensions(); Invalidate(); };
        chooseFiles.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Multiselect = true, Filter = BatchMediaPolicy.PickerFilter(CurrentOperation(), (string)target.SelectedItem) };
            if (picker.ShowDialog(this) != true) return;
            string[] accepted = picker.FileNames.Where(SupportsCurrent).ToArray();
            if (accepted.Length != picker.FileNames.Length) MessageBox.Show(this, "Some selected extensions are unavailable for this operation/target. Supported: " + CurrentExtensions(), "Unsupported inputs", MessageBoxButton.OK, MessageBoxImage.Information);
            if (accepted.Length == 0) return;
            filesMode.IsChecked = true; folder.Clear(); selected.AddRange(accepted.Where(path => !selected.Contains(path, StringComparer.OrdinalIgnoreCase))); Invalidate();
        };
        clearFiles.Click += (_, _) => { if (running != null) return; selected.Clear(); folder.Clear(); filesMode.IsChecked = true; Invalidate(); };
        chooseFolder.Click += (_, _) => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName; };
        chooseOutput.Click += (_, _) => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) output.Text = picker.FolderName; };
        chooseAuxiliary.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Boot DSK / boot profile / JSON patch|*.dsk;*.json" }; if (picker.ShowDialog(this) == true) auxiliary.Text = picker.FileName; };
        async Task Work(Func<CancellationToken, Task> work)
        {
            if (running != null) return;
            using var cancellation = new CancellationTokenSource(); running = cancellation;
            controls.IsEnabled = false; actions.IsEnabled = false; clearFiles.IsEnabled = false; stop.IsEnabled = true;
            progressBar.Visibility = Visibility.Visible; progressBar.IsIndeterminate = true;
            try { await work(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { summary.Text = "Cancelled. No pending outputs were saved."; execute.IsEnabled = false; }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                running = null; controls.IsEnabled = true; actions.IsEnabled = true; clearFiles.IsEnabled = true; stop.IsEnabled = false;
                progressBar.Visibility = Visibility.Collapsed; grid.Items.Refresh();
                if (closeAfterStop) Close();
            }
        }
        dryRun.Click += async (_, _) => await Work(async token =>
        {
            inputVersion++;
            preview = null; execute.IsEnabled = report.IsEnabled = details.IsEnabled = false;
            if (!compression.TryGetOptions(out var compressionOptions, out string compressionError) && CurrentOperation() == BatchOperation.CompressPrograms) throw new InvalidOperationException(compressionError);
            var options = new BatchOptions(CurrentOperation(), output.Text, (string)target.SelectedItem, naming.Text, auxiliary.Text, strict.IsChecked == true, replace.IsChecked == true, backup.IsChecked == true,
                string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text, compressionOptions, (int)sampleRate.SelectedItem, heuristic.IsChecked == true, (string)extractFormat.SelectedItem);
            bool fromFolder = folderMode.IsChecked == true && options.InputRoot != null;
            bool recurse = recursive.IsChecked == true; string filter = extensions.Text;
            var files = fromFolder
                ? await Task.Run(() => BatchProcessService.Scan(options.InputRoot!, recurse, filter, token).Where(p => BatchMediaPolicy.Supports(options.Operation, p, options.TargetFormat)).ToList())
                : selected.ToList();
            if (files.Count == 0) throw new InvalidOperationException("Select at least one input file.");
            summary.Text = "Preparing read-only preflight...";
            preview = await Task.Run(() => BatchProcessService.Preview(files, options, progress, token));
            preview.Cancelled |= token.IsCancellationRequested;
            grid.ItemsSource = preview.Rows; summary.Text = (preview.Cancelled ? "Cancelled. Run Preview again to continue. " : "") + preview.Summary; report.IsEnabled = true; details.IsEnabled = true;
            execute.IsEnabled = !preview.Cancelled && preview.Rows.Any(r => r.Result is "Will modify" or "Will analyze") && !(options.StrictPreflight && preview.BlocksStrict);
        });
        execute.Click += async (_, _) =>
        {
            if (preview == null) return; var captured = preview;
            bool acceptLoss = !captured.Rows.Any(r => r.Result == "Will modify" && r.RequiresLossConfirmation);
            if (!acceptLoss) acceptLoss = MessageBox.Show(this, "Accept the metadata/physical information losses listed in the preview and selected details?", "Confirm batch losses", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!acceptLoss) return;
            bool acceptReplace = !captured.Options.ReplaceOriginals || MessageBox.Show(this, "Replace original images through verified temporary files and atomic replace?", "Confirm replacing originals", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!acceptReplace) return;
            await Work(async token => { summary.Text = "Executing verified per-file outputs..."; await Task.Run(() => BatchProcessService.Execute(captured, acceptLoss, acceptReplace, progress, token)); summary.Text = $"{(captured.Cancelled ? "Cancelled. " : "")}Completed: {captured.Rows.Count(r => r.Result == "Completed")}; analyzed: {captured.Rows.Count(r => r.Result == "Analyzed")}; cancelled: {captured.Rows.Count(r => r.Result == "Cancelled")}; errors: {captured.Rows.Count(r => r.Result == "Error")}. Export a report for full results."; execute.IsEnabled = false; });
        };
        details.Click += (_, _) => { if (grid.SelectedItem is BatchRow row) new BatchFileDetailsDialog(this, row).ShowDialog(); };
        report.Click += (_, _) =>
        {
            if (grid.ItemsSource is not IReadOnlyList<BatchRow> rows) return;
            var picker = new SaveFileDialog { Filter = "JSON report|*.json|CSV catalog|*.csv|Text report|*.txt", FileName = "batch-report" };
            if (picker.ShowDialog(this) != true) return;
            try { File.WriteAllText(picker.FileName, BatchProcessService.Report(new BatchPreview(preview?.Options ?? new(BatchOperation.TestIntegrity, ""), rows), Path.GetExtension(picker.FileName).TrimStart('.').ToLowerInvariant())); }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        Invalidate();
    }
}
