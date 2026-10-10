using MZTools;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Globalization;

// WPF Application is process-wide. Run this separately from the existing unit
// suite, whose disk UI test owns an Application on another STA dispatcher.
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            AudioSignalAnalyzerWindow? window = null; string? generated = null;
            try
            {
                string path = args.Length > 0 ? Path.GetFullPath(args[0]) : generated = GenerateTape();
                string folder = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "mztools-audio-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                window = new AudioSignalAnalyzerWindow(); window.Show(); await window.Analyze(path);
                var model = (AudioSignalAnalysis?)Field(window, "analysis");
                if (model == null || model.Blocks.Count == 0) throw new Exception("No decoded blocks: " + ((TextBlock)Field(window, "status")!).Text);
                Console.WriteLine($"{model.Format}: {model.DurationSeconds:F2}s; {model.Blocks.Count} blocks; {model.Regions.Count} regions; {model.Issues.Count} issues; {model.Quality}");
                foreach (var format in new[] { "json", "csv", "txt" }) File.WriteAllText(Path.Combine(folder, "analysis." + format), AudioAnalysisReport.Export(model, format));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(window, folder, "records");
                var exportChart = (AudioSignalChart)Field(window, "waveform")!;
                exportChart.SetSelection(1, Math.Min(model.FrameCount, 1000));
                var savedView = (exportChart.Start, exportChart.End);
                var exportPath = Path.Combine(folder, "visual-selection.wav");
                var wavExport = await window.ExportSelectionWave(exportPath) ?? throw new Exception("Visual WAV export failed.");
                using (var exported = new WavPcmStreamReader(exportPath))
                using (var original = AudioSignalAnalysisService.OpenReader(path))
                {
                    if (exported.Format.SampleRate != model.SampleRate || exported.Format.BitsPerSample != model.BitsPerSample || exported.Format.Channels != model.ChannelCount || exported.Format.FrameCount != wavExport.EndSample - wavExport.StartSample) throw new Exception("Visual export changed source format or interval.");
                    var expected = new List<(int, int)>(); var actual = new List<(int, int)>();
                    new AudioPcmTransform().Visit(original, wavExport.StartSample, wavExport.EndSample, (_, l, r) => expected.Add((l, r)), default);
                    exported.ReadFrames((_, l, r) => actual.Add((l, r)));
                    if (!expected.SequenceEqual(actual)) throw new Exception("Visual WAV export changed PCM samples.");
                }
                if ((exportChart.Start, exportChart.End) != savedView) throw new Exception("Visual WAV export reset zoom.");
                var cancelledPath = Path.Combine(folder, "visual-cancelled.wav"); File.WriteAllBytes(cancelledPath, [1, 2, 3]);
                var cancelledExport = window.ExportSelectionWave(cancelledPath);
                ((Button)Field(window, "cancel")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (await cancelledExport != null || !File.ReadAllBytes(cancelledPath).SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("Visual export cancellation replaced destination.");
                if (!((Button)Field(window, "exportWave")!).IsEnabled || !((Button)Field(window, "open")!).IsEnabled) throw new Exception("Visual export did not restore controls.");
                var decoded = WavHeuristicAnalyzer.AnalyzeFile(path);
                foreach (bool detailed in new[] { false, true })
                {
                    var summary = detailed ? new WavAnalysisStatisticsWindow(decoded.Statistics) : new WavAnalysisStatisticsWindow(new AudioFileImportResult[] {
                        new() { SourceFile = path, Records = decoded.Records, Analysis = decoded },
                        new() { SourceFile = "failed.flac", Records = [], Error = new InvalidDataException("Decode failed") },
                        new() { SourceFile = "cancelled.wav", Records = [], Cancelled = true } });
                    summary.Show();
                    try
                    {
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var reportTabs = (TabControl)Field(summary, "reportTabs")!;
                        if (reportTabs.Items.Count != 5) throw new Exception("Structured report tabs missing.");
                        var programs = (DataGrid)((TabItem)reportTabs.Items[1]).Content;
                        if (programs.Items.Count != decoded.Records.Count || programs.Columns.Count < 6) throw new Exception("Report programs or columns missing.");
                        Save(summary, folder, detailed ? "audio-statistics" : "audio-import-summary");
                        reportTabs.SelectedIndex = 3; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var messages = (DataGrid)((TabItem)reportTabs.Items[3]).Content;
                        if (!detailed && messages.Items.Count != decoded.Statistics.Failures.Count + decoded.Statistics.RejectedHeaders.Count + (decoded.Statistics.OmittedHeaderRejections > 0 ? 1 : 0) + 2) throw new Exception("Failure/cancellation/rejected-header messages missing.");
                    }
                    finally { summary.Close(); }
                }
                var choice = (ComboBox)Field(window, "channelChoice")!;
                string[] expectedChannels = model.ChannelCount == 1 ? ["Mono"] : ["Left", "Right", "Overlay L/R"];
                if (!choice.Items.Cast<string>().SequenceEqual(expectedChannels)) throw new Exception("Channel names do not match the capture.");
                var records = (DataGrid)Field(window, "records")!;
                foreach (var column in records.Columns)
                    if (!column.CanUserResize || column.MaxWidth <= 320) throw new Exception("Column resizing is still capped.");
                var nameColumn = records.Columns.First(c => c.SortMemberPath == "Name");
                nameColumn.Width = 460; window.UpdateLayout();
                if (nameColumn.ActualWidth < 450) throw new Exception("Column cannot expand past its old limit.");
                var tabs = (TabControl)Field(window, "tabs")!; tabs.SelectedIndex = 1;
                var pulsePanel = (DockPanel)((ScrollViewer)((TabItem)tabs.Items[1]).Content).Content;
                if (!pulsePanel.Children.OfType<TabControl>().Single().Items.Cast<TabItem>().Select(t => t.Header.ToString()).SequenceEqual(new[] { "Histogram", "High / Low scatter", "Drift" })) throw new Exception("Unexpected pulse chart tabs; Drift values must not be instantiated.");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(window, folder, "histogram");
                var waveform = (AudioSignalChart)Field(window, "waveform")!;
                var regions = (DataGrid)Field(window, "regions")!;
                var payloadRegion = model.Regions.First(r => r.Kind == "Payload");
                var payloadBlock = model.Blocks.First(b => b.Record == payloadRegion.Record && b.Block == "Payload" && b.Channel == payloadRegion.Channel);
                records.SelectedItem = payloadBlock;
                if (!ReferenceEquals(regions.SelectedItem, payloadRegion)) throw new Exception("Record selection did not synchronize Regions.");
                tabs.SelectedIndex = 0; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                records.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                var recordParts = model.Blocks.Where(b => b.Record == payloadBlock.Record).ToArray();
                if (waveform.Start > recordParts.Min(b => b.StartSample) || waveform.End < recordParts.Max(b => b.EndSample)) throw new Exception("Record double-click did not show the whole record.");
                tabs.SelectedIndex = 5; regions.SelectedItem = payloadRegion;
                regions.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                if (!ReferenceEquals(records.SelectedItem, payloadBlock) || waveform.Start > payloadRegion.StartSample || waveform.End < payloadRegion.EndSample) throw new Exception("Region double-click navigation failed.");
                waveform.SelectAtSample(payloadRegion.StartSample + 1);
                if (!ReferenceEquals(regions.SelectedItem, payloadRegion) || !ReferenceEquals(records.SelectedItem, payloadBlock)) throw new Exception("Waveform click did not synchronize both tables.");
                tabs.SelectedIndex = 0; waveform.Focus(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var selectedRow = records.ItemContainerGenerator.ContainerFromItem(payloadBlock) as DataGridRow;
                if (selectedRow?.Background is not SolidColorBrush selectionBrush || selectionBrush.Color != Color.FromRgb(24, 103, 178)) throw new Exception("Inactive selected row lost its strong highlight.");
                Save(window, folder, "selected-record");
                if (records.SelectionMode != DataGridSelectionMode.Extended || regions.SelectionMode != DataGridSelectionMode.Extended) throw new Exception("Multiple selection is unavailable.");
                records.SelectedItems.Add(model.Blocks.First(b => b != payloadBlock));
                if (records.SelectedItems.Count != 2) throw new Exception("Selecting another block discarded the first.");
                await WaitForPulseSelection(window);
                if (Field(window, "selectionBlock") is not AudioBlockAnalysis multiple || multiple.Distributions.Sum(d => d.Count) == 0) throw new Exception("Multiple block pulse statistics missing.");
                window.MeasureSelection([(1, Math.Min(model.FrameCount, model.SampleRate))]);
                await WaitForPulseSelection(window);
                if (Field(window, "selectionBlock") is not AudioBlockAnalysis arbitrary || arbitrary.StartSample != 1 || arbitrary.EndSample != Math.Min(model.FrameCount, model.SampleRate)) throw new Exception("Arbitrary waveform pulse statistics missing.");
                records.UnselectAll(); records.SelectedItem = payloadBlock;
                var reference = (AudioPulseReferenceControl)Field(window, "referenceControl")!;
                var culture = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");
                    reference.AddUser("UI custom reference", ["240,5", "260", "480", "500"]);
                }
                finally { CultureInfo.CurrentCulture = culture; }
                var histogram = (AudioSignalChart)Field(window, "histogram")!;
                if (histogram.HistogramSeries?.Count != 4 || histogram.ReferenceOverlay?.Guides[0].Microseconds != 240.5) throw new Exception("Combined histogram or user reference failed.");
                var pulseChoices = (CheckBox[])Field(window, "pulseGroups")!;
                pulseChoices[0].IsChecked = false;
                if (histogram.HistogramSeries?.Count != 3 || histogram.HistogramSeries.Any(s => s.Group == "SHORT High")) throw new Exception("Pulse group visibility did not apply.");
                pulseChoices[0].IsChecked = true;
                tabs.SelectedIndex = 1; reference.IsExpanded = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(window, folder, "user-reference"); reference.IsExpanded = false;
                if (!AudioAnalysisReport.Export(model, "json").Contains("UI custom reference")) throw new Exception("Custom comparison provenance was not exported.");
                long first = Math.Max(0, recordParts.Min(b => b.StartSample) - model.SampleRate / 2), last = Math.Min(model.FrameCount, recordParts.Max(b => b.EndSample) + model.SampleRate / 2);
                waveform.SelectAudioRange(first, last);
                if (records.SelectedItem != null || regions.SelectedItem != null || waveform.SelectionStart != first || waveform.SelectionEnd != last) throw new Exception("Arbitrary selection retained a false record/region association.");
                var recovery = new AudioRegionRecoveryWindow(window, model, first, last); recovery.Show();
                try
                {
                    var candidateHexButton = (Button)Field(recovery, "candidateHex")!;
                    var candidateHexMenu = (MenuItem)Field(recovery, "candidateHexMenu")!;
                    if (candidateHexButton.IsEnabled || candidateHexMenu.IsEnabled) throw new Exception("Candidate hex is enabled without a result.");
                    await recovery.RefreshPreview();
                    var beforeScanHistogram = (AudioSignalChart)Field(recovery, "histogram")!;
                    if (beforeScanHistogram.HistogramSeries?.Sum(d => d.Count) is > 0) throw new Exception("Waveform preview generated histogram pulses before Deep analysis.");
                    await recovery.Scan();
                    if (recovery.Result?.Records.Count is not > 0 || recovery.Result.Passes.Count != 1) throw new Exception("Recovery preview failed: " + ((TextBlock)Field(recovery, "status")!).Text);
                    var candidateTable = (DataGrid)Field(recovery, "candidates")!;
                    foreach (var kind in new[] { SharpBlockKind.Header, SharpBlockKind.Payload })
                    {
                        int index = recovery.Result.Candidates.ToList().FindIndex(c => c.Kind == kind);
                        candidateTable.SelectedItem = candidateTable.Items[index];
                        var candidate = recovery.Result.Candidates[index]; byte[] before = candidate.Data.ToArray();
                        if (!candidateHexButton.IsEnabled || !candidateHexMenu.IsEnabled) throw new Exception("Candidate hex is disabled for a selected block.");
                        if (kind == SharpBlockKind.Header) candidateHexButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else candidateHexMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        var browser = app.Windows.OfType<HexBrowser>().Single(w => w.Owner == recovery);
                        try
                        {
                            var content = (StackPanel)Field(browser, "contentPanel")!;
                            string metadata = ((TextBlock)content.Children[0]).Text, dump = ((TextBlock)content.Children[1]).Text;
                            var bytes = dump.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .SelectMany(line => line.Substring(10, 52).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))).ToArray();
                            if (!before.SequenceEqual(bytes) || !before.SequenceEqual(candidate.Data)) throw new Exception("Candidate hex shows different bytes or modifies data.");
                            if (!browser.Title.Contains($"candidate {index + 1}") || !metadata.Contains($"CHECKSUM: {(candidate.ChecksumValid ? "OK" : "FAILED")}") || !metadata.Contains($"0X{candidate.RecordedChecksum:X4}")) throw new Exception("Candidate hex lost block identity/checksum metadata.");
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(browser, folder, "candidate-hex-" + kind);
                        }
                        finally { browser.Close(); }
                    }
                    candidateTable.UnselectAll();
                    if (candidateHexButton.IsEnabled || candidateHexMenu.IsEnabled) throw new Exception("Candidate hex remains enabled without selection.");
                    candidateTable.SelectedIndex = 0;
                    var manualHistogram = (AudioSignalChart)Field(recovery, "histogram")!;
                    var manualReference = (AudioPulseReferenceControl)Field(recovery, "referenceControl")!;
                    if (manualHistogram.HistogramSeries?.Count != 4 || manualHistogram.HistogramSeries.Sum(d => d.Count) == 0 || manualHistogram.ReferenceOverlay?.Name != "UI custom reference") throw new Exception("Manual histogram or inherited user reference missing.");
                    manualReference.AddUser("Manual custom", ["240", "260", "480", "500"]);
                    manualHistogram.ShowHistogramRange(100, 600);
                    var manualVisualTabs = (TabControl)Field(recovery, "visualTabs")!; manualVisualTabs.SelectedIndex = 1;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var originalManualSeries = manualHistogram.HistogramSeries!;
                    var zoomSeries = originalManualSeries.ToArray();
                    zoomSeries[0] = zoomSeries[0] with { Count = zoomSeries[0].Count + 1, Histogram = zoomSeries[0].Histogram.Append(new AudioHistogramBin(33000, 1)).ToArray() };
                    manualHistogram.HistogramSeries = zoomSeries;
                    manualHistogram.ShowHistogramRange(0, 1021.5);
                    var edgeRange = manualHistogram.HistogramRange;
                    for (int n = 0; n < 30; n++) { manualHistogram.ZoomHistogram(2, .9); manualHistogram.ZoomHistogram(.5, .9); }
                    if (Math.Abs(manualHistogram.HistogramRange.Min - edgeRange.Min) > .00001 || Math.Abs(manualHistogram.HistogramRange.Max - edgeRange.Max) > .00001)
                        throw new Exception("Repeated boundary zoom moved the main histogram peaks out of view.");
                    if (!manualHistogram.CaptureMouse()) throw new Exception("Cannot capture histogram for zoom-during-pan test.");
                    try
                    {
                        var pointerBefore = Mouse.GetPosition(manualHistogram);
                        typeof(AudioSignalChart).GetField("dragStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manualHistogram, (Point?)new Point(pointerBefore.X - 20, pointerBefore.Y));
                        typeof(AudioSignalChart).GetField("dragHistogram", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manualHistogram, manualHistogram.HistogramRange);
                        typeof(AudioSignalChart).GetField("dragMoved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manualHistogram, true);
                        var beforeWheel = manualHistogram.HistogramRange;
                        manualHistogram.ZoomHistogram(2, .9); var afterWheel = manualHistogram.HistogramRange;
                        if (!Equals(Field(manualHistogram, "dragHistogram"), afterWheel)) throw new Exception("Wheel zoom did not rebase the active histogram drag.");
                        manualHistogram.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
                        if (manualHistogram.HistogramRange != afterWheel) throw new Exception("MouseMove restored the stale histogram drag range after zoom.");
                        manualHistogram.ZoomHistogram(.5, .9);
                        if (Math.Abs(manualHistogram.HistogramRange.Min - beforeWheel.Min) > .00001 || Math.Abs(manualHistogram.HistogramRange.Max - beforeWheel.Max) > .00001)
                            throw new Exception("A stationary MouseMove after wheel zoom discarded the boundary anchor.");
                    }
                    finally { manualHistogram.ReleaseMouseCapture(); }
                    manualHistogram.HistogramSeries = originalManualSeries;
                    manualHistogram.ShowHistogramRange(100, 600); manualVisualTabs.SelectedIndex = 0;
                    var retainedHistogramRange = manualHistogram.HistogramRange;
                    var manualGroups = (CheckBox[])Field(recovery, "pulseGroups")!; manualGroups[0].IsChecked = false;
                    if (manualHistogram.HistogramSeries?.Count != 3 || manualHistogram.ReferenceOverlay?.Name != "Manual custom") throw new Exception("Manual histogram choices/reference editing failed.");
                    if (manualHistogram.HistogramRange != retainedHistogramRange) throw new Exception("Histogram group choice reset zoom.");
                    manualGroups[0].IsChecked = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(recovery, folder, "recovery-preview");
                    var completed = recovery.Result;
                    var stopped = recovery.Scan(); ((Button)Field(recovery, "cancel")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await stopped;
                    if (!ReferenceEquals(completed, recovery.Result) || !((TextBlock)Field(recovery, "status")!).Text.Contains("cancelled")) throw new Exception("Recovery cancellation lost the previous preview.");
                    ((CheckBox)Field(recovery, "adaptive")!).IsChecked = false;
                    var fixedTest = (CheckBox)Field(recovery, "fixedTiming")!; fixedTest.IsChecked = true; fixedTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var fuzzyTest = (CheckBox)Field(recovery, "fuzzy")!; fuzzyTest.IsChecked = true; fuzzyTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    foreach (string name in new[] { "zero", "schmitt", "adaptiveCrossing" }) ((CheckBox)Field(recovery, name)!).IsChecked = true;
                    ((TextBox)Field(recovery, "thresholds")!).Text = "1";
                    await recovery.Scan();
                    var observedDetectors = recovery.Result!.Passes.SelectMany(p => p.Measurements).Select(m => m.Block.DecodeDetector).Distinct().ToArray();
                    if (!new[] { "ZeroCrossing", "Schmitt", "AdaptiveZeroCrossing" }.All(observedDetectors.Contains)) throw new Exception("Checked detectors did not all run.");
                    ((CheckBox)Field(recovery, "zero")!).IsChecked = false;
                    ((CheckBox)Field(recovery, "adaptiveCrossing")!).IsChecked = false;
                    ((CheckBox)Field(recovery, "halfSample")!).IsChecked = true;
                    ((CheckBox)Field(recovery, "inverted")!).IsChecked = false;
                    ((ComboBox)Field(recovery, "channel")!).SelectedIndex = payloadBlock.Channel;
                    ((TextBox)Field(recovery, "shortPulse")!).Text = payloadBlock.Distributions.First(d => d.Group == "SHORT High").Median!.Value.ToString("G", CultureInfo.InvariantCulture);
                    ((TextBox)Field(recovery, "timeScales")!).Text = "1"; ((TextBox)Field(recovery, "thresholds")!).Text = "1";
                    var manual = recovery.Scan();
                    if (((WrapPanel)Field(recovery, "testSettings")!).IsEnabled) throw new Exception("Manual settings were editable during scan.");
                    if (candidateHexButton.IsEnabled || candidateHexMenu.IsEnabled) throw new Exception("Candidate hex remains enabled during a scan.");
                    await manual;
                    if (recovery.Result == completed || recovery.Result?.Passes.Count != 2 || recovery.Result.Records.Count == 0 || recovery.Result.Passes.Any(p => p.Test == "Adaptive")) throw new Exception("Selected manual tests failed: " + ((TextBlock)Field(recovery, "status")!).Text);
                    if (recovery.Result.Candidates.Any(c => c.PulseMode != WavPulseMode.Schmitt || c.Inverted || string.IsNullOrEmpty(c.RecoveryTest))) throw new Exception("Manual detector/polarity/provenance was lost.");
                    if (recovery.Result.Passes.Any(p => !p.HalfSample || p.Reference != null)) throw new Exception("Common half-sample setting did not reach non-reference tests.");
                    if (manualHistogram.HistogramRange != retainedHistogramRange) throw new Exception("Analysis refresh reset histogram zoom.");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(recovery, folder, "manual-tests-preview");
                }
                finally { recovery.Close(); }
                // Headerless controls use the same candidate/hex/export workflow.
                var rawWindow = new AudioRegionRecoveryWindow(window, model, Math.Max(0, payloadBlock.StartSample - model.SampleRate * 4), payloadBlock.EndSample); rawWindow.Show();
                try
                {
                    ((ComboBox)Field(rawWindow, "decodeScope")!).SelectedIndex = 1;
                    // Use the verified recovered byte length, never infer it from histogram counts.
                    var full = WavHeuristicAnalyzer.AnalyzeFile(path, includeCandidates: true);
                    var selectedPayload = full.Statistics.RecordRecoveries[0].Payload.Candidate;
                    ((TextBox)Field(rawWindow, "from")!).Text = ((selectedPayload.SignalTrace?.LeaderStart ?? selectedPayload.StartSample) / (double)model.SampleRate).ToString("R", CultureInfo.CurrentCulture);
                    ((TextBox)Field(rawWindow, "to")!).Text = (selectedPayload.EndSample / (double)model.SampleRate).ToString("R", CultureInfo.CurrentCulture);
                    ((TextBox)Field(rawWindow, "length")!).Text = selectedPayload.Data.Length.ToString();
                    ((TextBox)Field(rawWindow, "thresholds")!).Text = "1";
                    ((CheckBox)Field(rawWindow, "schmitt")!).IsChecked = true;
                    await rawWindow.Scan();
                    if (rawWindow.Result?.Records.Count != 0 || !rawWindow.Result.Candidates.Any(c => c.Kind == SharpBlockKind.Payload && c.ChecksumValid && c.Data.SequenceEqual(selectedPayload.Data))) throw new Exception("Payload only UI requires a header or lost bytes.");
                    var rawCandidates = (DataGrid)Field(rawWindow, "candidates")!;
                    rawCandidates.SelectedIndex = rawWindow.Result.Candidates.ToList().FindIndex(c => c.Kind == SharpBlockKind.Payload && c.ChecksumValid);
                    if (!((Button)Field(rawWindow, "syntheticMzf")!).IsEnabled) throw new Exception("Explicit synthetic MZF action unavailable for verified payload.");
                    var eventsTable = (DataGrid)Field(rawWindow, "recoveryEvents")!;
                    if (eventsTable.Items.Count == 0) throw new Exception("Recovery events are missing from the UI.");
                    var eventRow = rawWindow.Result.Diagnostics.First(e => e.Code == "MARK_FOUND"); eventsTable.SelectedItem = eventRow;
                    eventsTable.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                    var rawWave = (AudioSignalChart)Field(rawWindow, "waveform")!;
                    if (rawWave.Start > eventRow.Sample || rawWave.End < eventRow.Sample) throw new Exception("Recovery event did not navigate waveform.");
                    var selectedUnknown = (CheckBox)Field(rawWindow, "unknownLength")!; selectedUnknown.IsChecked = true; selectedUnknown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (((TextBox)Field(rawWindow, "length")!).IsEnabled) throw new Exception("Known length remains editable in unknown mode.");
                    await rawWindow.Scan();
                    if (!rawWindow.Result!.Candidates.Any(c => c.ChecksumValid && c.Data.SequenceEqual(selectedPayload.Data))) throw new Exception("Unknown selected-end UI failed.");
                    ((CheckBox)Field(rawWindow, "diagnosticsEnabled")!).IsChecked = false;
                    await rawWindow.Scan();
                    if (((DataGrid)Field(rawWindow, "recoveryEvents")!).Items.Count != 0) throw new Exception("Recovery diagnostics cannot be disabled.");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(rawWindow, folder, "headerless-recovery");
                }
                finally { rawWindow.Close(); }
                records.SelectedItem = payloadBlock;
                foreach (string kind in new[] { "Waveform", "Histogram", "Scatter", "Drift" })
                {
                    var chart = new AudioSignalChart { Kind = kind, Analysis = model, Block = model.Blocks[1], Distribution = model.Blocks[1].Distributions[0],
                        HistogramSeries = kind == "Histogram" ? model.Blocks[1].Distributions : null, ReferenceOverlay = kind == "Histogram" ? reference.Selected : null };
                    chart.Fit(); chart.Measure(new(1000, 250)); chart.Arrange(new(0, 0, 1000, 250));
                    var bitmap = new RenderTargetBitmap(1000, 250, 96, 96, PixelFormats.Pbgra32); bitmap.Render(chart);
                    var pixels = new byte[1000 * 250 * 4]; bitmap.CopyPixels(pixels, 1000 * 4, 0);
                    bool colored = false; for (int pixel = 0; pixel < pixels.Length; pixel += 4) if (Math.Abs(pixels[pixel] - pixels[pixel + 2]) > 30) { colored = true; break; }
                    if (!colored) throw new Exception(kind + " did not render any colored data/reference primitives.");
                    if (kind == "Histogram")
                    {
                        // Actual four-lane axis markers, including two colours at an identical duration.
                        chart.ReferenceOverlay = AudioPulseReference.User("Coincident references", 240, 240, 480, 480); chart.InvalidateVisual(); bitmap.Render(chart); bitmap.CopyPixels(pixels, 1000 * 4, 0);
                        foreach (var brush in new[] { Brushes.Blue, Brushes.Red, Brushes.Green, Brushes.DarkOrange })
                        {
                            bool found = false;
                            for (int y = 219; y <= 226; y++) for (int x = 65; x < 982; x++)
                            { int p = (y * 1000 + x) * 4; if (pixels[p] == brush.Color.B && pixels[p + 1] == brush.Color.G && pixels[p + 2] == brush.Color.R) found = true; }
                            if (!found) throw new Exception("Reference axis strip lost colour " + brush.Color);
                        }
                        for (int i = 0; i < 160; i++) { chart.ZoomHistogram(.5, .93); if (i % 20 == 0) bitmap.Render(chart); }
                        for (int i = 0; i < 160; i++) { chart.ZoomHistogram(2, .93); if (i % 20 == 0) bitmap.Render(chart); }
                        chart.FitHistogram();
                        var series = chart.HistogramSeries!.ToArray();
                        void RenderHistogram() { chart.InvalidateVisual(); chart.Measure(new(1000, 250)); chart.Arrange(new(0, 0, 1000, 250)); bitmap.Render(chart); }
                        series[0] = series[0] with { Count = series[0].Count + 7, Histogram = series[0].Histogram.Append(new AudioHistogramBin(33000, 7)).ToArray() };
                        chart.HistogramSeries = series; chart.FitHistogram(); RenderHistogram();
                        if (chart.HistogramViewportLabel != "FULL RANGE") throw new Exception("Fit histogram has no full-range indicator.");
                        long total = series.Sum(d => d.Histogram.Sum(b => b.Count));
                        if (chart.HistogramRange.Max < 33000 || chart.RenderedHistogramCount != total) throw new Exception($"Fit histogram lost long pulses: range {chart.HistogramRange}, rendered {chart.RenderedHistogramCount}/{total}.");
                        chart.ShowHistogramRange(100, 600); RenderHistogram();
                        if (!chart.HistogramViewportLabel.Contains("ZOOM / PAN")) throw new Exception("Histogram zoom is not identified.");
                        var zoomRange = chart.HistogramRange;
                        long visible = series.Sum(d => d.Histogram.Where(b => b.DurationMicroseconds >= zoomRange.Min && b.DurationMicroseconds <= zoomRange.Max).Sum(b => b.Count));
                        if (chart.RenderedHistogramCount != visible || visible >= total) throw new Exception("Histogram zoom clamps offscreen pulses onto edge bins.");
                        chart.ZoomHistogram(.5);
                        if (Math.Abs(chart.HistogramRange.Max - chart.HistogramRange.Min - 250) > .0001) throw new Exception("Histogram zoom width is incorrect.");
                        chart.ShowHistogramRange(200, 450);
                        if (Math.Abs(chart.HistogramRange.Min - 200) > .0001) throw new Exception("Histogram pan did not move the duration interval.");
                        chart.ShowHistogramRange(10000, 11000); RenderHistogram();
                        if (chart.RenderedHistogramCount != 0) throw new Exception("Empty histogram viewport displays offscreen pulses.");
                        chart.FitHistogram(); RenderHistogram();
                        if (chart.RenderedHistogramCount != total || series.Sum(d => d.Count) != total) throw new Exception("Histogram navigation changed statistics.");
                        chart.ShowHistogramRange(100, 600); RenderHistogram();
                    }
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(folder, "chart-" + kind + ".png")); encoder.Save(output);
                }
                var longModel = new AudioSignalAnalysis { SampleRate = 44100, ChannelCount = 2, FrameCount = 82_000_000,
                    Blocks = Enumerable.Range(0, 18).Select(i => model.Blocks[1] with { Record = i + 1, Name = "Program " + (i + 1), StartSample = i * 4_000_000L, EndSample = i * 4_000_000L + 3_500_000 }).ToArray(),
                    Regions = Enumerable.Range(0, 18).SelectMany(i => new[] {
                        new AudioSignalRegion("Payload", i * 4_000_000L, i * 4_000_000L + 3_500_000, i + 1, 1, "UI overview fixture") { SampleRate = 44100 },
                        new AudioSignalRegion("Unassigned audio", i * 4_000_000L + 3_500_000, (i + 1) * 4_000_000L, null, null, "UI overview fixture") { SampleRate = 44100 } }).ToArray(),
                    Waveform = new WaveformRenderModel(Enumerable.Range(0, 65536).Select(i => new WaveformBucket(i * 1251L, (i + 1) * 1251L, -.5f, .7f, -.6f, .8f)).ToArray(), 82_000_000, 2) };
                var wide = new AudioSignalChart { Analysis = longModel }; wide.Fit(); wide.Measure(new(1000, 250)); wide.Arrange(new(0, 0, 1000, 250));
                var performanceWindow = new Window { Width = 1000, Height = 300, Content = wide };
                performanceWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(performanceWindow, folder, "18-record-overview");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    for (int pan = 0; pan < 12; pan++)
                    {
                        wide.ShowRange(pan * 100000, pan * 100000 + 40_000_000);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var bitmap = new RenderTargetBitmap(1000, 250, 96, 96, PixelFormats.Pbgra32); bitmap.Render(wide);
                        if (wide.RenderedBucketCount > 1002) throw new Exception("Zoomed-out waveform did not simplify to viewport width.");
                    }
                }
                finally { performanceWindow.Close(); }
                Console.WriteLine($"Long stereo waveform: 12 pan renders in {watch.ElapsedMilliseconds} ms; {wide.RenderedBucketCount} visible buckets from 65,536.");
                waveform.ShowRange(123, 1000); var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (waveform.Envelope?.Buckets.FirstOrDefault().StartSample != 123 && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
                if (waveform.Envelope?.Buckets.FirstOrDefault().StartSample != 123) throw new Exception("Sample-level zoom did not complete: " + ((TextBlock)Field(window, "status")!).Text);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(window, folder, "zoom");
                var issues = (DataGrid)Field(window, "issues")!; tabs.SelectedIndex = 4;
                if (model.Issues.Count > 0)
                {
                    var issue = model.Issues[0]; issues.SelectedItem = issue;
                    issues.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                    if (!ReferenceEquals(issue, issues.SelectedItem) || waveform.Start > issue.StartSample || waveform.End < issue.EndSample) throw new Exception("Issue navigation failed.");
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(window, folder, "issues");
                // Cancel a fresh analysis and retain the previous completed result.
                var cancelled = window.Analyze(path); ((Button)Field(window, "cancel")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await cancelled;
                if (!((TextBlock)Field(window, "status")!).Text.Contains("cancelled")) throw new Exception("Cancellation did not update the UI.");
                if (!ReferenceEquals(model, Field(window, "analysis"))) throw new Exception("Cancellation discarded the previous result.");
                string stereo = GenerateTape(stereo: true);
                try
                {
                    await window.Analyze(stereo);
                    if (!choice.Items.Cast<string>().SequenceEqual(new[] { "Left", "Right", "Overlay L/R" })) throw new Exception("Stereo channel choices are incorrect.");
                    choice.SelectedIndex = 1;
                    if (waveform.ChannelMode != 1) throw new Exception("Right channel selection was not applied.");
                }
                finally { File.Delete(stereo); }
                string delayed = GenerateTape(stereo: true, delay: 17, inverted: true, clipped: true);
                try
                {
                    await window.Analyze(delayed);
                    var alignedSource = (AudioSignalAnalysis)Field(window, "analysis")!;
                    var aligned = new AudioRegionRecoveryWindow(window, alignedSource, 0, alignedSource.FrameCount); aligned.Show();
                    try
                    {
                        ((TextBox)Field(aligned, "shiftMilliseconds")!).Text = (-17000.0 / alignedSource.SampleRate).ToString("G", CultureInfo.InvariantCulture);
                        if (((TextBox)Field(aligned, "shiftSamples")!).Text != "-17") throw new Exception("Milliseconds were not converted to sample shift.");
                        var inverse = (CheckBox)Field(aligned, "invertRight")!; inverse.IsChecked = true; inverse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        ((ComboBox)Field(aligned, "channel")!).SelectedIndex = 3;
                        ((TextBox)Field(aligned, "thresholds")!).Text = "1";
                        await aligned.Scan();
                        if (aligned.Result?.Records.Count != 1 || aligned.Result.Passes.Any(p => p.Channels != "Mix" || !p.AudioTransform.Contains("-17 samples"))) throw new Exception("Aligned stereo mix did not reach decoding: " + ((TextBlock)Field(aligned, "status")!).Text);
                        var alignedWaveform = (AudioSignalChart)Field(aligned, "waveform")!;
                        var alignedHistogram = (AudioSignalChart)Field(aligned, "histogram")!;
                        if (alignedWaveform.Analysis?.ChannelCount != 1 || alignedHistogram.HistogramSeries?.Sum(d => d.Count) is not > 0) throw new Exception("Mix preview/histogram missing.");
                        async Task CheckSelectionWave(string name, AudioPcmTransform transform, bool right)
                        {
                            long viewStart = alignedWaveform.Start, viewEnd = alignedWaveform.End;
                            long? selectionStart = alignedWaveform.SelectionStart, selectionEnd = alignedWaveform.SelectionEnd;
                            if (!((Button)Field(aligned, "exportWave")!).IsEnabled) throw new Exception("WAV export is disabled for a completed selected preview.");
                            string destination = Path.Combine(folder, "selection-" + name + ".wav");
                            var result = await aligned.ExportSelectionWave(destination) ?? throw new Exception("WAV export failed: " + ((TextBlock)Field(aligned, "status")!).Text);
                            using var original = AudioSignalAnalysisService.OpenReader(alignedSource.Source);
                            using var exported = new WavPcmStreamReader(destination);
                            var expected = new List<int>(); var actual = new List<int>();
                            int scale = 1 << (24 - alignedSource.BitsPerSample), limit = 1 << (alignedSource.BitsPerSample - 1);
                            transform.Visit(original, result.StartSample, result.EndSample, (_, left, r) => expected.Add((int)Math.Clamp(Math.Round((right ? r : left) / (double)scale, MidpointRounding.AwayFromZero), -limit, limit - 1) * scale), default);
                            exported.ReadFrames((_, left, _) => actual.Add(left));
                            if (result.Channels != 1 || exported.Format.BitsPerSample != alignedSource.BitsPerSample || exported.Format.SampleRate != alignedSource.SampleRate || !expected.SequenceEqual(actual))
                                throw new Exception("WAV selection does not match the transformed " + name + " PCM.");
                            if (alignedWaveform.Start != viewStart || alignedWaveform.End != viewEnd || alignedWaveform.SelectionStart != selectionStart || alignedWaveform.SelectionEnd != selectionEnd)
                                throw new Exception("WAV export reset waveform zoom or selection.");
                        }
                        alignedWaveform.SetSelection(100, 200);
                        await CheckSelectionWave("average", new(-17, true, true), false);
                        var problemTable = (DataGrid)Field(aligned, "problems")!;
                        var clipping = problemTable.Items.OfType<AudioAnalysisIssue>().First(i => i.Code == "AUDIO_CLIPPING");
                        alignedWaveform.SelectIssueAt(clipping.StartSample);
                        if (!Equals(problemTable.SelectedItem, clipping)) throw new Exception("Waveform marker did not synchronize Problems.");
                        problemTable.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                        if (alignedWaveform.Start > clipping.StartSample || alignedWaveform.End < clipping.EndSample) throw new Exception("Problem double-click did not zoom to the exact span.");
                        var zoomWatch = System.Diagnostics.Stopwatch.StartNew();
                        while (alignedWaveform.Envelope?.Buckets.All(b => b.EndSample - b.StartSample == 1) != true && zoomWatch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
                        if (alignedWaveform.Envelope?.Buckets.All(b => b.EndSample - b.StartSample == 1) != true) throw new Exception("Aligned sample zoom did not load PCM.");
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(aligned, folder, "aligned-mix-problem");
                        long retainedStart = alignedWaveform.Start, retainedEnd = alignedWaveform.End;
                        long? selectedStart = alignedWaveform.SelectionStart, selectedEnd = alignedWaveform.SelectionEnd;
                        void CheckRetainedView()
                        {
                            if (alignedWaveform.Start != retainedStart || alignedWaveform.End != retainedEnd || alignedWaveform.SelectionStart != selectedStart || alignedWaveform.SelectionEnd != selectedEnd)
                                throw new Exception("Manual operation reset zoom or selection.");
                        }
                        var retainedMeasurements = alignedHistogram.HistogramSeries!;
                        var retainedPassResult = aligned.Result;
                        ((CheckBox)Field(aligned, "zero")!).IsChecked = false;
                        ((CheckBox)Field(aligned, "schmitt")!).IsChecked = true;
                        ((TextBox)Field(aligned, "thresholds")!).Text = "1.4; 0.65";
                        await aligned.RefreshPreview();
                        CheckRetainedView();
                        if (!((TextBlock)Field(aligned, "measurement")!).Text.Contains("OUTDATED") || !ReferenceEquals(retainedPassResult, aligned.Result)) throw new Exception("Changed settings did not mark the retained Deep histogram outdated.");
                        if (!alignedHistogram.HistogramSeries!.SequenceEqual(retainedMeasurements)) throw new Exception("Waveform preview remeasured the Deep histogram.");
                        await aligned.Scan(); CheckRetainedView();
                        if (Field(aligned, "histogramPass") != null || Field(aligned, "histogramDetector") != null) throw new Exception("Obsolete histogram analysis selectors remain.");
                        if (aligned.Result!.Passes.Count != 2 || ((TextBlock)Field(aligned, "measurement")!).Text.Contains("OUTDATED")) throw new Exception("Latest Deep histogram was not refreshed.");
                        var representative = aligned.Result.Passes.OrderByDescending(p => p.ValidHeaders + p.ValidPayloads).First();
                        var consumed = representative.Measurements.Where(m => m.Block.DecodeDetector == AudioRecoveryHistogram.Detector(aligned.Result, representative));
                        if (!consumed.Any(m => m.Block.Distributions.SequenceEqual(alignedHistogram.HistogramSeries!))) throw new Exception("Histogram summed or remeasured completed passes.");
                        ((TextBox)Field(aligned, "thresholds")!).Text = "1";
                        ((CheckBox)Field(aligned, "schmitt")!).IsChecked = false;
                        ((CheckBox)Field(aligned, "adaptiveCrossing")!).IsChecked = true;
                        ((TextBox)Field(aligned, "zeroDeadband")!).Text = "3";
                        await aligned.Scan(); CheckRetainedView();
                        if (!((TextBlock)Field(aligned, "measurement")!).Text.Contains("AdaptiveZeroCrossing") || alignedHistogram.HistogramSeries?.Sum(d => d.Count) is not > 0 || aligned.Result!.Passes[0].ZeroDeadbandPercent != 3) throw new Exception("Adaptive histogram/deadband provenance missing.");
                        ((TextBox)Field(aligned, "shiftSamples")!).Text = "-16";
                        await aligned.RefreshPreview(); CheckRetainedView();
                        ((ComboBox)Field(aligned, "channel")!).SelectedIndex = 1;
                        await aligned.RefreshPreview(); CheckRetainedView();
                        await CheckSelectionWave("left", new(-16, true, false), false);
                        ((ComboBox)Field(aligned, "channel")!).SelectedIndex = 2;
                        await aligned.RefreshPreview(); CheckRetainedView();
                        await CheckSelectionWave("right", new(-16, true, false), true);
                        ((TextBox)Field(aligned, "shiftSamples")!).Text = "-17";
                        ((ComboBox)Field(aligned, "channel")!).SelectedIndex = 3;
                        ((CheckBox)Field(aligned, "adaptive")!).IsChecked = false;
                        ((CheckBox)Field(aligned, "referenceTests")!).IsChecked = true;
                        ((CheckBox)Field(aligned, "halfSample")!).IsChecked = true;
                        ((CheckBox)Field(aligned, "schmitt")!).IsChecked = false;
                        ((CheckBox)Field(aligned, "adaptiveCrossing")!).IsChecked = true;
                        ((ComboBox)Field(aligned, "referenceScope")!).SelectedIndex = 1;
                        var selector = (ListBox)Field(aligned, "referenceSelection")!;
                        var userReference = AudioPulseReference.User("UI decoder reference", 238, 259, 469, 487);
                        ((AudioPulseReferenceControl)Field(aligned, "referenceControl")!).UseReference(userReference, true);
                        selector.SelectedItem = userReference;
                        await aligned.Scan(); CheckRetainedView();
                        if (aligned.Result?.Records.Count != 1 || aligned.Result.Passes.Count != 2 || aligned.Result.Passes[1].Reference != userReference.Name || !aligned.Result.Passes[1].HalfSample)
                            throw new Exception("UI reference selection did not reach decoder: " + ((TextBlock)Field(aligned, "status")!).Text);
                        var tooltip = (ToolTip)alignedWaveform.ToolTip;
                        var placements = tooltip.CustomPopupPlacementCallback(new Size(200, 100), new Size(alignedWaveform.ActualWidth, alignedWaveform.ActualHeight), new Point());
                        if (tooltip.Placement != System.Windows.Controls.Primitives.PlacementMode.Custom || placements.Length != 4) throw new Exception("Tooltip has no cursor-safe fallback positions.");
                        var pointer = Mouse.GetPosition(alignedWaveform);
                        if (placements.Any(p => new Rect(p.Point, new Size(200, 100)).Contains(pointer))) throw new Exception("Tooltip fallback obscures the cursor.");
                        var referenceExpander = ((WrapPanel)Field(aligned, "testSettings")!).Children.OfType<Expander>().Single();
                        referenceExpander.IsExpanded = true;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(aligned, folder, "reference-test-settings");
                        referenceExpander.IsExpanded = false;
                        ((TabControl)Field(aligned, "visualTabs")!).SelectedIndex = 1;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(aligned, folder, "aligned-mix-histogram");
                        var retainedWaveform = alignedWaveform.Analysis;
                        var cancelPreview = aligned.RefreshPreview(); ((Button)Field(aligned, "cancel")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        try { await cancelPreview; throw new Exception("Preview cancellation did not cancel measurement."); } catch (OperationCanceledException) { }
                        if (!ReferenceEquals(retainedWaveform, alignedWaveform.Analysis)) throw new Exception("Preview cancellation discarded the completed histogram/waveform.");
                    }
                    finally { aligned.Close(); }
                }
                finally { File.Delete(delayed); }
                Console.WriteLine("PASS: combined histogram, custom reference, record/region/waveform navigation, inactive highlight, recovery preview/cancellation, overview performance, reports and sample zoom. Artifacts: " + folder);
                app.Shutdown(0);
            }
            catch (Exception ex) { Console.WriteLine(ex); app.Shutdown(1); }
            finally { window?.Close(); if (generated != null) File.Delete(generated); }
        };
        app.Run();
    }
    private static async Task WaitForPulseSelection(AudioSignalAnalyzerWindow window)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Field(window, "pulseWork") != null && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(30);
        if (Field(window, "pulseWork") != null) throw new Exception("Selection pulse measurement timed out.");
    }
    private static object? Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(obj);
    private static void Save(Window window, string folder, string name)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
    private static string GenerateTape(bool stereo = false, int delay = 0, bool inverted = false, bool clipped = false)
    {
        string path = Path.Combine(Path.GetTempPath(), "mztools-ui-input-" + Guid.NewGuid().ToString("N") + ".wav");
        byte[] mzf = new byte[160]; mzf[0] = 1; "VISUAL"u8.CopyTo(mzf.AsSpan(1)); mzf[17] = 13;
        BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(18), 32); BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(20), 0x1200); BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(22), 0x1200);
        for (int n = 128; n < mzf.Length; n++) mzf[n] = (byte)n;
        using var reader = new BinaryReader(new MemoryStream(mzf)); var record = new MZTFileReader().ReadMzfRecord(reader);
        SharpTapeExporter.Export(path, [record], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800);
        if (clipped) { byte[] fixture = File.ReadAllBytes(path); Array.Fill(fixture, (byte)255, 54, 4); File.WriteAllBytes(path, fixture); }
        if (stereo)
        {
            byte[] original = File.ReadAllBytes(path), data = new byte[44 + (original.Length - 44) * 2];
            original.AsSpan(0, 44).CopyTo(data);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(28)) * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), data.Length - 44);
            for (int n = 44; n < original.Length; n++) { data[44 + (n - 44) * 2] = original[n]; byte r = n - 44 < delay ? (byte)128 : original[n - delay]; data[45 + (n - 44) * 2] = inverted ? (byte)Math.Clamp(256 - r, 0, 255) : r; }
            File.WriteAllBytes(path, data);
        }
        return path;
    }
}
