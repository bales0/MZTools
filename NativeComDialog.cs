using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class NativeComDialog : Window
{
    internal string? ImportedFileKey { get; private set; }

    internal NativeComDialog(Window owner, TapeRecord? current = null, bool multipart = false, DskDocument? target = null)
    {
        Owner = owner; Title = "MZF/M12 → COM"; Width = 820; Height = 740;
        MinWidth = 660; MinHeight = 490; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NativeProgramImage? image = null;
        string? inputError = null;
        try { if (current != null) image = MzfNativeImageParser.Parse(current.DeepClone()); }
        catch (Exception e) { inputError = e.Message; }
        bool knownMultipart = multipart;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var options = new StackPanel(); DockPanel.SetDock(options, Dock.Top); root.Children.Add(options);
        options.Children.Add(new TextBlock { Text = "Convert a machine-code program to a COM file for a CP/M disk.\nUse RESET to leave the program. Additional tape loads and external files are not included.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8) });
        var open = new Button { Content = "Open MZF / M12...", Margin = new Thickness(4) };
        var profiles = new ComboBox { ItemsSource = CpmTargetProfile.BuiltIns, SelectedIndex = 0, MinWidth = 150, Margin = new Thickness(4) };
        var modes = new ComboBox { ItemsSource = Enum.GetValues<NativeMachineMode>(), SelectedItem = NativeMachineMode.Mz800Monitor, MinWidth = 170, Margin = new Thickness(4) };
        var ceiling = new TextBox { Text = "C000", Width = 70, Margin = new Thickness(4), ToolTip = "Exclusive COM load ceiling, hex. The live BDOS vector is also checked by Stage 0 before takeover." };
        options.Children.Add(open); open.HorizontalAlignment = HorizontalAlignment.Left;
        var sourceInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        options.Children.Add(sourceInfo);
        var row = new WrapPanel();
        row.Children.Add(new TextBlock { Text = "CP/M:", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(profiles);
        row.Children.Add(new TextBlock { Text = "Native state:", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(modes);
        row.Children.Add(new TextBlock { Text = "COM ceiling (hex):", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(ceiling);
        var compression = new CompressionOptionsControl(); compression.ConfigureTarget(CompressionTarget.NativeCom);
        options.Children.Add(compression);
        options.Children.Add(new Expander { Header = "Advanced settings", Content = row, Margin = new Thickness(4) });
        var importName = new TextBox { Text = NativeComConversionService.SuggestName(image?.Name ?? "PROGRAM"), Width = 140, Margin = new Thickness(4), ToolTip = "Decoded MZF header name, sanitized and shortened to CP/M 8.3. Editable for import and export." };
        var targetRow = new WrapPanel();
        targetRow.Children.Add(new TextBlock { Text = "COM filename (import user 0):", VerticalAlignment = VerticalAlignment.Center }); targetRow.Children.Add(importName);
        options.Children.Add(targetRow);
        var availability = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 8) };
        options.Children.Add(availability);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(4, 0, 4, 8), Visibility = Visibility.Collapsed };
        options.Children.Add(progress);
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var export = new Button { Content = "Export / Save...", IsEnabled = false, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        var import = new Button { Content = "Import to current disk", IsEnabled = false, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetShowOnDisabled(export, true); ToolTipService.SetShowOnDisabled(import, true);
        var saveReport = new Button { Content = "Save report...", Margin = new Thickness(4) };
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(4) };
        actions.Children.Add(import); actions.Children.Add(export); actions.Children.Add(close); close.Click += (_, _) => Close();
        var report = new TextBox { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var details = new DockPanel(); DockPanel.SetDock(saveReport, Dock.Bottom); details.Children.Add(saveReport); details.Children.Add(report);
        options.Children.Add(new Expander { Header = "Technical details", Content = details, Margin = new Thickness(4), MaxHeight = 260 });
        root.Children.Remove(options);
        root.Children.Add(new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        MzNativeComResult? result = null;
        CancellationTokenSource? analysisCancellation = null;
        int analysisVersion = 0;
        Closed += (_, _) => { analysisVersion++; analysisCancellation?.Cancel(); };
        void UpdateImportAvailability()
        {
            import.IsEnabled = false;
            if (result == null) return;
            string? importReason = NativeComImportService.UnavailableReason(target);
            if (importReason == null)
            {
                try { NativeComImportService.Preview(target!, importName.Text.Trim(), result); import.IsEnabled = true; }
                catch (Exception e) { importReason = e.Message; }
            }
            import.ToolTip = importReason ?? "Add the COM file to the current CP/M disk. Save the disk afterwards.";
            availability.Text = $"Ready: {result.Bytes.Length:N0} bytes. The original MZF/M12 is unchanged.\n" +
                (importReason == null ? "Import adds the file to CP/M user 0. Save the disk afterwards." : "Save is available. Import: " + importReason);
        }
        async void Analyze()
        {
            int version = ++analysisVersion;
            analysisCancellation?.Cancel();
            analysisCancellation?.Dispose();
            analysisCancellation = new CancellationTokenSource();
            var token = analysisCancellation.Token;
            result = null; export.IsEnabled = false; import.IsEnabled = false;
            progress.Visibility = Visibility.Collapsed;
            void Unavailable(string reason)
            {
                availability.Text = reason;
                export.ToolTip = reason; import.ToolTip = reason;
            }
            try
            {
                sourceInfo.Text = image == null ? "No program selected." : $"{image.Name} · file type {image.Type:X2} ({(image.Type == 0x4D ? "custom header loader" : SharpMzEncoding.ConvertFtypeToDescription(image.Type).Trim())})";
                if (image == null) { report.Text = inputError ?? "No input."; Unavailable(inputError == null ? "Open an MZF/M12 file or select a program in the tape list." : "This file could not be read as an MZF/M12 program. Check the file format. See Technical details for the read error."); return; }
                string? sourceReason = NativeComFeedback.SourceProblem(image, knownMultipart);
                if (sourceReason != null) { report.Text = sourceReason; Unavailable(sourceReason); return; }
                string value = ceiling.Text.Trim(); if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
                if (!int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int top) || top is < 0x1200 or > 0xE000)
                    throw new ArgumentException("COM ceiling must be hexadecimal 1200..E000.");
                var profile = CpmTargetProfile.Create(((CpmTargetProfile)profiles.SelectedItem).Name, top);
                var mode = (NativeMachineMode)modes.SelectedItem;
                var source = image;
                if (!compression.TryGetOptions(out var selectedOptions, out string compressionError))
                { report.Text = compressionError; Unavailable(compressionError); return; }
                var policy = compression.KeepSource ? null : selectedOptions;
                bool multiple = knownMultipart;
                if (policy == null)
                {
                    var plan = NativeLoaderPlacementAnalyzer.Analyze(source, profile, mode, true, multiple);
                    report.Text = plan.Report;
                    if (!plan.Supported) { Unavailable(NativeComFeedback.ConversionProblem(plan.UnsupportedReason ?? "")); return; }
                }
                Unavailable("Preparing the COM file and checking that it can be loaded...");
                progress.Visibility = Visibility.Visible;
                report.Text = "Preparing conversion...";
                var prepared = await Task.Run(() => NativeComConversionService.Build(source, profile, mode, policy, multiple, token), token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (version != analysisVersion || token.IsCancellationRequested) return;
                    result = prepared; report.Text = result.Report; progress.Visibility = Visibility.Collapsed; export.IsEnabled = true;
                    export.ToolTip = "Save the converted COM as a separate file.";
                    UpdateImportAvailability();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (version == analysisVersion) { report.Text = e.ToString(); Unavailable(NativeComFeedback.ConversionProblem(e.Message)); } }); }
            finally { await Dispatcher.InvokeAsync(() => { if (version == analysisVersion) progress.Visibility = Visibility.Collapsed; }); }
        }
        profiles.SelectionChanged += (_, _) => Analyze(); modes.SelectionChanged += (_, _) => Analyze(); ceiling.TextChanged += (_, _) => Analyze();
        compression.OptionsChanged += (_, _) => Analyze();
        importName.TextChanged += (_, _) => UpdateImportAvailability();
        open.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Sharp single-record programs|*.mzf;*.m12;*.mz0;*.mz7" };
            if (picker.ShowDialog(this) != true) return;
            image = null; inputError = null; result = null; export.IsEnabled = false; import.IsEnabled = false;
            try { image = MzfNativeImageParser.Read(picker.FileName); importName.Text = NativeComConversionService.SuggestName(image.Name); knownMultipart = false; Analyze(); }
            catch (Exception e) { inputError = e.Message; Analyze(); }
        };
        import.Click += (_, _) =>
        {
            if (result == null || target == null) return;
            try
            {
                var candidate = NativeComImportService.Preview(target, importName.Text.Trim(), result);
                candidate.Operation.Apply(target);
                ImportedFileKey = candidate.FileKey;
                Close();
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); Analyze(); }
        };
        export.Click += (_, _) =>
        {
            if (result == null || image == null) return;
            var captured = result;
            var picker = new SaveFileDialog { Filter = "One-way native loader|*.com", DefaultExt = ".com", AddExtension = true, FileName = importName.Text.Trim() };
            if (picker.ShowDialog(this) != true) return;
            try { WriteVerified(picker.FileName, captured.Bytes); availability.Text = "Saved: " + picker.FileName; }
            catch (Exception e) { MessageBox.Show(this, e.Message, "COM file could not be saved"); }
        };
        saveReport.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Native COM report|*.txt", FileName = "native-com-report.txt" };
            if (picker.ShowDialog(this) == true) try { File.WriteAllText(picker.FileName, report.Text); } catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        Analyze();
    }

    internal static void WriteVerified(string path, byte[] bytes)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".mztools-com-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw new IOException("COM verification failed; destination unchanged.");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
