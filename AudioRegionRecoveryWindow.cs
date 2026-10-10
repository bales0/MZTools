using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed partial class AudioRegionRecoveryWindow : Window
{
    private readonly AudioSignalAnalysis source;
    private readonly TextBox from = new() { Width = 115, Margin = new(5) }, to = new() { Width = 115, Margin = new(5) }, length = new() { Width = 85, Margin = new(5) };
    private readonly Button scan = new() { Content = "Run deeper analysis", Margin = new(5), Padding = new(9, 4, 9, 4) };
    private readonly Button cancel = new() { Content = "Cancel analysis", Margin = new(5), IsEnabled = false };
    private readonly Button saveProgram = new() { Content = "Save selected program as MZF…", Margin = new(5), IsEnabled = false };
    private readonly Button saveCandidate = new() { Content = "Save selected candidate as raw bytes…", Margin = new(5), IsEnabled = false };
    private readonly Button candidateHex = new() { Content = "Hex view…", Margin = new(5), Padding = new(9, 4, 9, 4), IsEnabled = false };
    private readonly MenuItem candidateHexMenu = new() { Header = "Hex view…", IsEnabled = false };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(5) };
    private readonly ProgressBar progress = new() { Height = 6, Maximum = 1 };
    private readonly DataGrid programs = AudioSignalAnalyzerWindow.Grid(), candidates = AudioSignalAnalyzerWindow.Grid(), passes = AudioSignalAnalyzerWindow.Grid(), messages = AudioSignalAnalyzerWindow.Grid();
    private CancellationTokenSource? work;
    private bool closed;
    private readonly WrapPanel testSettings = new();
    private readonly CheckBox adaptive = Choice("Adaptive", true), fixedTiming = Choice("Fixed time scale", false), fuzzy = Choice("Fuzzy pulse length", false);
    private readonly CheckBox zero = Choice("Zero crossing", true), schmitt = Choice("Hysteresis (Schmitt)", false), adaptiveCrossing = Choice("Adaptive zero crossing (peak midpoint)", false);
    private readonly CheckBox normal = Choice("Normal polarity", true), inverted = Choice("Inverted polarity", true);
    private readonly CheckBox referenceTests = Choice("Reference profile tests", false), halfSample = Choice("Test each pulse at ±0.5 sample", false);
    private readonly ComboBox referenceScope = new() { Width = 170, Margin = new(5) };
    private readonly ListBox referenceSelection = new() { DisplayMemberPath = "Name", SelectionMode = SelectionMode.Extended, Height = 110, MinWidth = 400, Margin = new(5) };
    private readonly TextBox referenceTolerance = new() { Text = "20", Width = 60, Margin = new(5) };
    private readonly ComboBox channel = new() { Width = 120, Margin = new(5) };
    private readonly TextBox zeroDeadband = new() { Text = "0", Width = 65, Margin = new(5), ToolTip = "Minimum gate around zero after DC filtering, ±% of full-scale PCM. 0 disables the additional gate. Does not move samples." };
    private readonly TextBox thresholds = new() { Text = "0.4; 0.65; 1; 1.4", Width = 145, Margin = new(5) }, timeScales = new() { Text = "0.9; 1; 1.1", Width = 125, Margin = new(5) }, shortPulse = new() { Text = "238", Width = 70, Margin = new(5) }, tolerance = new() { Text = "35", Width = 60, Margin = new(5) };
    private static CheckBox Choice(string label, bool selected) => new() { Content = label, IsChecked = selected, Margin = new(6), VerticalAlignment = VerticalAlignment.Center };
    internal AudioRegionRecoveryResult? Result { get; private set; }
    internal AudioRegionRecoveryWindow(Window owner, AudioSignalAnalysis source, long start, long end)
    {
        this.source = source; Owner = owner; Title = "Selected audio — deeper analysis / recovery preview";
        Width = 1200; Height = 900; MinWidth = 850; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = Path.GetFileName(source.Source), FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        top.Children.Add(new TextBlock { Text = "Choose one or more detectors and classification tests. All checked detectors run automatically with the selected tests. Adaptive classification can be off when reference, fixed timing or fuzzy tests are selected. The common ±0.5-sample option tests pulse lengths without moving PCM or timestamps. Include leader, header and payload for a complete program; supply the expected length to search for a payload without a header. Checksums remain mandatory for valid blocks.", TextWrapping = TextWrapping.Wrap, Margin = new(5) });
        top.Children.Add(testSettings);
        foreach (var choice in new Control[] { adaptive, fixedTiming, fuzzy, zero, schmitt, adaptiveCrossing, normal, inverted, referenceTests, halfSample }) testSettings.Children.Add(choice);
        channel.Items.Add("All channels"); channel.Items.Add(source.ChannelCount == 1 ? "Mono" : "Left"); if (source.ChannelCount == 2) { channel.Items.Add("Right"); channel.Items.Add("Mix (L + R) / 2"); } channel.SelectedIndex = 0; testSettings.Children.Add(channel);
        foreach (var item in new (string Label, TextBox Input)[] { ("Gating scales (; separated)", thresholds), ("Ignore near zero (±% FS)", zeroDeadband), ("SHORT classifier (µs)", shortPulse), ("Time scales (; separated)", timeScales), ("Fuzzy tolerance (%)", tolerance) })
        {
            var field = new StackPanel { Orientation = Orientation.Horizontal };
            field.Children.Add(new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new(4) }); field.Children.Add(item.Input); testSettings.Children.Add(field);
        }
        var timingNote = new TextBlock { Text = "Fixed timing uses SHORT × time scale and LONG = 2 × SHORT for the classifier's physical LOW half (HIGH after polarity inversion). Fuzzy matching chooses the nearer SHORT/LONG duration within the tolerance; it also relaxes leader matching. Time scale and fuzzy are separate test families. Lists accept semicolons or spaces.", TextWrapping = TextWrapping.Wrap, Margin = new(5), FontSize = 12 };
        top.Children.Add(timingNote);
        var referencePanel = new StackPanel();
        referenceScope.Items.Add("All profiles"); referenceScope.Items.Add("Selected profiles"); referenceScope.SelectedIndex = 0;
        referenceSelection.ItemsSource = referenceControl.References; referenceSelection.SelectedIndex = 0;
        referenceSelection.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = SystemColors.HighlightBrush;
        referenceSelection.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = SystemColors.HighlightTextBrush;
        var referenceInputs = new WrapPanel(); referenceInputs.Children.Add(referenceScope); referenceInputs.Children.Add(new TextBlock { Text = "Tolerance (%)", VerticalAlignment = VerticalAlignment.Center }); referenceInputs.Children.Add(referenceTolerance); 
        referencePanel.Children.Add(referenceInputs); referencePanel.Children.Add(referenceSelection);
        referencePanel.Children.Add(new TextBlock { Text = "Select profiles with Ctrl / Shift. Add or edit user values under Histogram → Pulse references. All profiles includes those user values. Each profile tests all documented High/Low durations within this explicit tolerance. Checksums must still match. A separate header-discovery pass allows ROM headers with faster copier payloads.", TextWrapping = TextWrapping.Wrap, Margin = new(5) });
        var referenceExpander = new Expander { Header = "Reference test settings / profile selection", Content = referencePanel, MaxWidth = 1000, Margin = new(5) }; testSettings.Children.Add(referenceExpander);
        void UpdateSettings() { zeroDeadband.IsEnabled = adaptiveCrossing.IsChecked == true; thresholds.IsEnabled = schmitt.IsChecked == true || adaptiveCrossing.IsChecked == true; shortPulse.IsEnabled = timeScales.IsEnabled = fixedTiming.IsChecked == true; tolerance.IsEnabled = fuzzy.IsChecked == true; referencePanel.IsEnabled = referenceTests.IsChecked == true; referenceSelection.IsEnabled = referenceScope.SelectedIndex == 1; }
        zero.Click += (_, _) => UpdateSettings(); schmitt.Checked += (_, _) => UpdateSettings(); schmitt.Unchecked += (_, _) => UpdateSettings(); adaptiveCrossing.Checked += (_, _) => UpdateSettings(); adaptiveCrossing.Unchecked += (_, _) => UpdateSettings(); referenceTests.Click += (_, _) => UpdateSettings(); referenceScope.SelectionChanged += (_, _) => UpdateSettings();
        schmitt.Click += (_, _) => UpdateSettings(); fixedTiming.Click += (_, _) => UpdateSettings(); fuzzy.Click += (_, _) => UpdateSettings(); UpdateSettings();
        var inputs = new WrapPanel(); top.Children.Add(inputs);
        foreach (var item in new (string Label, TextBox Input)[] { ("From (s)", from), ("To (s)", to), ("Expected payload (B, blank = unknown)", length) })
        {
            var field = new StackPanel { Orientation = Orientation.Horizontal };
            field.Children.Add(new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new(4) }); field.Children.Add(item.Input); inputs.Children.Add(field);
        }
        from.Text = (start / (double)source.SampleRate).ToString("0.######", CultureInfo.CurrentCulture);
        to.Text = (end / (double)source.SampleRate).ToString("0.######", CultureInfo.CurrentCulture);
        inputs.Children.Add(scan); inputs.Children.Add(cancel);
        top.Children.Add(progress); top.Children.Add(status);
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(saveProgram); footer.Children.Add(saveCandidate); footer.Children.Add(candidateHex); footer.Children.Add(exportWave);
        var close = new Button { Content = "Close", Margin = new(5), Padding = new(9, 4, 9, 4) }; footer.Children.Add(close); close.Click += (_, _) => Close();
        var tabs = new TabControl(); root.Children.Add(tabs);
        foreach (var item in new (string Name, DataGrid Table)[] { ("Programs", programs), ("Block candidates", candidates), ("Scan passes", passes), ("Messages", messages) })
            tabs.Items.Add(new TabItem { Header = item.Name, Content = item.Table });
        InitializePreview(top, tabs);
        InitializeHeaderless(top, tabs, footer);
        scan.Click += async (_, _) => await Scan(); cancel.Click += (_, _) => { work?.Cancel(); previewWork?.Cancel(); zoomWork?.Cancel(); wavWork?.Cancel(); };
        exportWave.Click += async (_, _) => await ChooseWaveExport(); waveform.SelectionChanged += UpdateWaveExport;
        programs.SelectionChanged += (_, _) => saveProgram.IsEnabled = work == null && wavWork == null && programs.SelectedItem != null;
        candidates.SelectionChanged += (_, _) => UpdateCandidateActions();
        candidates.ContextMenu = new ContextMenu(); candidates.ContextMenu.Items.Add(candidateHexMenu);
        candidates.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = ItemsControl.ContainerFromElement(candidates, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row == null) candidates.UnselectAll();
            else { candidates.UnselectAll(); candidates.SelectedItem = row.Item; row.Focus(); }
        };
        candidates.ContextMenuOpening += (_, e) => { UpdateCandidateActions(); if (!candidateHexMenu.IsEnabled) e.Handled = true; };
        candidateHex.Click += (_, _) => ShowCandidateHex(); candidateHexMenu.Click += (_, _) => ShowCandidateHex();
        saveProgram.Click += (_, _) => ExportProgram(); saveCandidate.Click += (_, _) => ExportCandidate();
        Closed += (_, _) => { closed = true; work?.Cancel(); previewTimer.Stop(); previewWork?.Cancel(); zoomWork?.Cancel(); wavWork?.Cancel(); };
        status.Text = "Results are a separate preview. The source recording and current analysis remain unchanged. Raw candidates may have a failed checksum and are not complete MZF programs.";
    }
    internal async Task Scan()
    {
        if (work != null || wavWork != null) return;
        var cancellation = new CancellationTokenSource(); work = cancellation;
        UpdateCandidateActions();
        UpdateWaveExport();
        scan.IsEnabled = from.IsEnabled = to.IsEnabled = length.IsEnabled = saveProgram.IsEnabled = saveCandidate.IsEnabled = false;
        testSettings.IsEnabled = decodeScope.IsEnabled = unknownLength.IsEnabled = diagnosticsEnabled.IsEnabled = false;
        alignmentSettings.IsEnabled = false;
        previewSettings.IsEnabled = false;
        cancel.IsEnabled = true; progress.Value = 0;
        try
        {
            double Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : double.NaN;
            double first = Parse(from.Text), last = Parse(to.Text);
            if (!double.IsFinite(first) || !double.IsFinite(last) || first < 0 || last <= first || last > source.DurationSeconds + .000001) throw new ArgumentException("Enter a nonempty interval inside this recording, in seconds.");
            long start = checked((long)Math.Round(first * source.SampleRate)), end = Math.Min(source.FrameCount, checked((long)Math.Round(last * source.SampleRate)));
            int? expected = string.IsNullOrWhiteSpace(length.Text) ? null : int.TryParse(length.Text, out int bytes) ? bytes : throw new ArgumentException("Expected payload length must be an integer in bytes, or blank.");
            double[] List(string text) => text.Split(new[] { ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
            var transform = ReadTransform();
            var options = new AudioRecoveryOptions { PayloadOnly = decodeScope.SelectedIndex == 1, UnknownLength = decodeScope.SelectedIndex == 1 && unknownLength.IsChecked == true, Diagnostics = diagnosticsEnabled.IsChecked == true, Adaptive = adaptive.IsChecked == true, FixedTiming = fixedTiming.IsChecked == true, Fuzzy = fuzzy.IsChecked == true,
                AdaptiveZeroCrossing = adaptiveCrossing.IsChecked == true, ZeroDeadband = adaptiveCrossing.IsChecked == true ? Parse(zeroDeadband.Text) / 100 : 0, ReferenceTests = referenceTests.IsChecked == true,
                References = referenceScope.SelectedIndex == 0 ? referenceControl.References.ToArray() : referenceSelection.SelectedItems.Cast<AudioPulseReference>().ToArray(), ReferenceTolerance = Parse(referenceTolerance.Text) / 100, HalfSample = halfSample.IsChecked == true,
                ZeroCrossing = zero.IsChecked == true, Schmitt = schmitt.IsChecked == true, Normal = normal.IsChecked == true, Inverted = inverted.IsChecked == true,
                Channel = channel.SelectedIndex == 0 ? null : transform.Mix ? 0 : channel.SelectedIndex - 1, Transform = transform, ThresholdScales = schmitt.IsChecked == true || adaptiveCrossing.IsChecked == true ? List(thresholds.Text) : [1],
                ShortMicroseconds = Parse(shortPulse.Text), TimeScales = List(timeScales.Text), FuzzyTolerance = Parse(tolerance.Text) / 100 };
            if (options.UnknownLength) expected = null;
            options.Validate(transform.Mix ? 1 : source.ChannelCount);
            await RefreshPreview();
            cancellation.Token.ThrowIfCancellationRequested();
            var reporter = new Progress<WavAnalysisProgress>(p => { if (!closed && work == cancellation) { status.Text = p.Stage + $" — {p.Fraction:P0}"; progress.Value = p.Fraction; } });
            var result = await Task.Run(() =>
            {
                var file = new FileInfo(source.Source);
                if (file.Length != source.SourceLength || file.LastWriteTimeUtc != source.SourceModifiedUtc) throw new IOException("The source changed since analysis. Analyze it again.");
                return WavHeuristicAnalyzer.AnalyzeRange(source.Source, start, end, expected, reporter, cancellation.Token, options);
            }, cancellation.Token);
            if (closed || cancellation.IsCancellationRequested) return;
            Result = result;
            scannedTransform = transform;
            completedHistogramSettings = HistogramSettingsKey();
            RefreshHistogram();
            ApplyScanFindings(result);
            programs.ItemsSource = result.Records.Select((r, i) => new { Number = i + 1, Name = SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname), Bytes = r.Body.MzfBody.Length,
                Profile = TapeProfileNames.ToDisplayName(r.Profile), HeaderChecksum = result.Recoveries[i].Header.Candidate.ChecksumValid ? "OK" : "Failed",
                PayloadChecksum = result.Recoveries[i].ReconstructionUsed ? "Reconstructed by existing checksum recovery" : result.Recoveries[i].Payload.Candidate.ChecksumValid ? "OK" : "Failed", result.Recoveries[i].ReconstructionUsed,
                HeaderTests = result.Recoveries[i].Header.Candidate.RecoveryTest, PayloadTests = result.Recoveries[i].Payload.Candidate.RecoveryTest }).ToArray();
            candidates.ItemsSource = result.Candidates.Select((c, i) => new { Number = i + 1, Block = c.Kind.ToString(), Bytes = c.Data.Length, Channel = c.Channel + 1, c.Inverted,
                Detector = c.PulseMode.ToString(), Status = CandidateStatus(c), c.ChecksumValid, c.ChecksumAvailable, c.LengthVerified, c.BoundaryEvidence, LeaderSample = c.SignalTrace?.LeaderStart, MarkSample = c.SignalTrace?.SyncStart, c.RecordedChecksum, c.CalculatedChecksum, StartSeconds = c.StartSample / (double)result.SampleRate, EndSeconds = c.EndSample / (double)result.SampleRate, Tests = c.RecoveryTest }).ToArray();
            passes.ItemsSource = result.Passes;
            recoveryEvents.ItemsSource = result.Diagnostics;
            messages.ItemsSource = result.Failures.Select(f => new { Category = "Unresolved", f.Reason, f.ExpectedLength, StartSeconds = f.StartSample / (double)result.SampleRate, EndSeconds = f.EndSample / (double)result.SampleRate })
                .Concat(result.RejectedHeaders.Select(r => new { Category = "Header rejected", Reason = $"{r.Reason} Channel {r.Channel}; {r.Detector}; inverted {r.Inverted}; checksum {r.RecordedChecksum:X4}/{r.CalculatedChecksum:X4}; header SHA-256 {r.HeaderSha256}", ExpectedLength = (ushort?)null, StartSeconds = r.Sample / (double)result.SampleRate, EndSeconds = r.Sample / (double)result.SampleRate }))
                .Concat(result.OmittedHeaderRejections == 0 ? [] : new[] { new { Category = "Header rejected", Reason = $"{result.OmittedHeaderRejections} further events omitted (bounded diagnostics).", ExpectedLength = (ushort?)null, StartSeconds = start / (double)result.SampleRate, EndSeconds = end / (double)result.SampleRate } }).ToArray();
            if (result.Records.Count > 0) programs.SelectedIndex = 0;
            if (result.Candidates.Count > 0) candidates.SelectedIndex = 0;
            progress.Value = 1;
            status.Text = $"{(options.PayloadOnly ? "Payload only: no tape header inferred. " : "")}Recovery events: {result.Diagnostics.Count} ({result.OmittedDiagnostics} further events omitted). Found {result.Records.Count} complete program(s), {result.Candidates.Count} unique block candidate(s), {result.Failures.Count} unresolved finding(s). Candidates include failed checksums; raw export does not certify valid or executable data. Repeated matching candidates across passes are deduplicated.";
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "Range analysis cancelled. Previous preview retained."; }
        catch (Exception ex) { if (!closed) status.Text = "Cannot analyze this interval: " + ex.Message; }
        finally
        {
            cancellation.Dispose(); work = null;
            if (!closed) UpdateCandidateActions();
            if (!closed) UpdateWaveExport();
            if (!closed) { testSettings.IsEnabled = alignmentSettings.IsEnabled = previewSettings.IsEnabled = decodeScope.IsEnabled = diagnosticsEnabled.IsEnabled = true; unknownLength.IsEnabled = decodeScope.SelectedIndex == 1; }
            if (!closed) { scan.IsEnabled = from.IsEnabled = to.IsEnabled = true; length.IsEnabled = !(decodeScope.SelectedIndex == 1 && unknownLength.IsChecked == true); cancel.IsEnabled = false; saveProgram.IsEnabled = programs.SelectedItem != null; saveCandidate.IsEnabled = candidates.SelectedItem != null; }
        }
    }
    private static int Number(object row) => (int)row.GetType().GetProperty("Number")!.GetValue(row)! - 1;
    private void UpdateCandidateActions()
    {
        bool available = work == null && wavWork == null && Result != null && candidates.SelectedItem != null;
        saveCandidate.IsEnabled = candidateHex.IsEnabled = candidateHexMenu.IsEnabled = available;
        syntheticMzf.IsEnabled = available && Result!.Candidates[Number(candidates.SelectedItem!)].Kind == SharpBlockKind.Payload && Result.Candidates[Number(candidates.SelectedItem!)].ChecksumAvailable && Result.Candidates[Number(candidates.SelectedItem!)].ChecksumValid;
    }
    private void ShowCandidateHex()
    {
        if (work != null || wavWork != null || Result == null || candidates.SelectedItem == null) return;
        int index = Number(candidates.SelectedItem);
        if ((uint)index >= Result.Candidates.Count) return;
        var candidate = Result.Candidates[index];
        string checksum = !candidate.ChecksumAvailable ? "UNKNOWN" : candidate.ChecksumValid ? "OK" : "FAILED";
        string heading = $"Candidate {index + 1} — {candidate.Kind} — {candidate.Data.Length:N0} B\n" +
            $"Checksum: {checksum}; recorded 0x{candidate.RecordedChecksum:X4}; calculated 0x{candidate.CalculatedChecksum:X4}\n" +
            $"Channel {candidate.Channel + 1}; {candidate.PulseMode}; {(candidate.Inverted ? "inverted" : "normal")}; samples [{candidate.StartSample:N0}, {candidate.EndSample:N0})\n" +
            "Offsets are relative to the candidate bytes. Checksum bytes are reported above, outside the dump.\n" +
            "OFFSET    HEX                                                   ASCII             SHASCII (EU)";
        var browser = new HexBrowser { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        browser.ShowRawData(heading, candidate.Data);
        browser.Title = $"Block candidate {index + 1} — {candidate.Kind} — checksum {checksum}";
        browser.Show();
    }
    private void ExportProgram()
    {
        if (Result == null || programs.SelectedItem == null) return;
        var record = Result.Records[Number(programs.SelectedItem)];
        var dialog = new SaveFileDialog { Filter = "MZF program (*.mzf)|*.mzf", AddExtension = true, FileName = "recovered-program.mzf" };
        if (dialog.ShowDialog(this) != true) return;
        try { TapeDocumentWriter.SaveMzf(dialog.FileName, record, preserveTrailing: true, createSidecar: true); status.Text = "Recovered program saved: " + dialog.FileName; }
        catch (Exception ex) { status.Text = "Cannot save: " + ex.Message; }
    }
    private void ExportCandidate()
    {
        if (Result == null || candidates.SelectedItem == null) return;
        var candidate = Result.Candidates[Number(candidates.SelectedItem)];
        var dialog = new SaveFileDialog { Filter = "Raw candidate bytes (*.bin)|*.bin", AddExtension = true, FileName = candidate.ChecksumValid && candidate.LengthVerified ? "checksum-valid-candidate.bin" : "unverified-candidate.bin" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dialog.FileName, candidate.Data); status.Text = $"Raw {candidate.Kind} candidate saved: {CandidateStatus(candidate)}. No MZF header was invented."; }
        catch (Exception ex) { status.Text = "Cannot save: " + ex.Message; }
    }
}
