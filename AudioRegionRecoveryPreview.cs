using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MZTools;

internal sealed partial class AudioRegionRecoveryWindow
{
    private readonly AudioSignalChart waveform = new() { Kind = "Waveform", Height = 190, ShowIssueMarkers = true };
    private readonly AudioSignalChart histogram = new() { Kind = "Histogram", Height = 220 };
    private readonly AudioPulseReferenceControl referenceControl = new();
    private readonly CheckBox[] pulseGroups = AudioPulseReference.Groups.Select(g => Choice(g, true)).ToArray();
    private readonly DataGrid distributions = AudioSignalAnalyzerWindow.Grid(), problems = AudioSignalAnalyzerWindow.Grid();
    private readonly WrapPanel alignmentSettings = new();
    private readonly WrapPanel previewSettings = new();
    private readonly TextBox shiftSamples = new() { Text = "0", Width = 75, Margin = new(4) }, shiftMilliseconds = new() { Text = "0", Width = 80, Margin = new(4) };
    private readonly CheckBox invertRight = Choice("Invert right polarity", false);
    private string? completedHistogramSettings;
    private AudioRecoveryMeasurement? displayedMeasurement;
    private readonly ComboBox measurementChannel = new() { Width = 100, Margin = new(4) }, measurementPolarity = new() { Width = 100, Margin = new(4) };
    private readonly TextBlock measurement = new() { Margin = new(5), TextWrapping = TextWrapping.Wrap };
    private readonly TabControl visualTabs = new();
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? previewWork, zoomWork;
    private AudioRangePreview? preview;
    private AudioPcmTransform displayTransform = new();
    private AudioPcmTransform? scannedTransform;
    private bool updatingShift, refreshingHistogram;

    private static double NumberValue(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : double.NaN;
    private AudioPcmTransform ReadTransform()
    {
        if (!int.TryParse(shiftSamples.Text, out int samples)) throw new ArgumentException("Right shift must be an integer number of samples.");
        if (Math.Abs((long)samples) > source.SampleRate * 2L) throw new ArgumentException("Right shift must be within ±2 seconds.");
        return new(samples, invertRight.IsChecked == true, source.ChannelCount == 2 && channel.SelectedIndex == 3);
    }
    private void InitializePreview(StackPanel top, TabControl tabs)
    {
        histogram.Analysis = source;
        void Field(Panel panel, string title, Control input)
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal };
            pair.Children.Add(new TextBlock { Text = title, Margin = new(4), VerticalAlignment = VerticalAlignment.Center }); pair.Children.Add(input); panel.Children.Add(pair);
        }
        Field(alignmentSettings, "Right shift (samples)", shiftSamples); Field(alignmentSettings, "ms (+ = right)", shiftMilliseconds);
        void Button(Panel panel, string label, Action action) { var b = new Button { Content = label, Margin = new(4), Padding = new(7, 3, 7, 3) }; panel.Children.Add(b); b.Click += (_, _) => action(); }
        Button(alignmentSettings, "← 1 sample", () => { if (int.TryParse(shiftSamples.Text, out int n)) shiftSamples.Text = Math.Max(-2L * source.SampleRate, n - 1L).ToString(); });
        Button(alignmentSettings, "1 sample →", () => { if (int.TryParse(shiftSamples.Text, out int n)) shiftSamples.Text = Math.Min(2L * source.SampleRate, n + 1L).ToString(); });
        Button(alignmentSettings, "Reset", () => { shiftSamples.Text = "0"; invertRight.IsChecked = false; QueuePreview(); });
        alignmentSettings.Children.Add(invertRight);
        foreach (var child in alignmentSettings.Children.OfType<UIElement>()) child.IsEnabled = source.ChannelCount == 2;
        top.Children.Insert(top.Children.IndexOf(progress), alignmentSettings);
        var panel = new DockPanel();
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var nav = previewSettings; header.Children.Add(nav);
        Button(nav, "Fit selected interval", waveform.Fit); Button(nav, "Zoom +", () => waveform.Zoom(.5)); Button(nav, "Zoom −", () => waveform.Zoom(2));
        measurementChannel.Items.Add(source.ChannelCount == 1 ? "Mono" : "Left"); if (source.ChannelCount == 2) measurementChannel.Items.Add("Right"); measurementChannel.SelectedIndex = 0;
        measurementPolarity.Items.Add("Normal"); measurementPolarity.Items.Add("Inverted"); measurementPolarity.SelectedIndex = 0;
        Field(nav, "Histogram channel", measurementChannel); Field(nav, "Histogram polarity", measurementPolarity);
        header.Children.Add(measurement);
        waveform.Height = double.NaN;
        visualTabs.Items.Add(new TabItem { Header = "Waveform", Content = waveform });
        var pulsePanel = new StackPanel();
        visualTabs.Items.Add(new TabItem { Header = "Histogram", Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pulsePanel } });
        panel.Children.Add(visualTabs);
        var groups = new WrapPanel(); pulsePanel.Children.Add(groups);
        Button(groups, "Fit histogram", histogram.FitHistogram); Button(groups, "Zoom +", () => histogram.ZoomHistogram(.5)); Button(groups, "Zoom −", () => histogram.ZoomHistogram(2));
        foreach (var check in pulseGroups) { check.IsChecked = source.VisiblePulseGroups.Contains((string)check.Content); check.Foreground = AudioSignalChart.PulseBrush((string)check.Content); groups.Children.Add(check); check.Checked += (_, _) => RefreshHistogram(); check.Unchecked += (_, _) => RefreshHistogram(); }
        referenceControl.UseReference(source.ComparisonReference, source.ShowComparisonReference);
        pulsePanel.Children.Add(referenceControl); referenceControl.Changed += RefreshHistogram;
        distributions.Height = 105; pulsePanel.Children.Add(new Expander { Header = "Pulse statistics", Content = distributions }); pulsePanel.Children.Add(histogram);
        tabs.Items.Insert(0, new TabItem { Header = "Visual analysis", Content = panel });
        tabs.Items.Add(new TabItem { Header = "Problems", Content = problems }); tabs.SelectedIndex = 0;
        void Navigate(AudioAnalysisIssue issue, bool zoom)
        {
            waveform.SetSelection(issue.StartSample, issue.EndSample);
            if (zoom) { long pad = Math.Max(source.SampleRate / 100, (issue.EndSample - issue.StartSample) / 10); waveform.ShowRange(issue.StartSample - pad, issue.EndSample + pad); tabs.SelectedIndex = 0; visualTabs.SelectedIndex = 0; }
        }
        problems.SelectionChanged += (_, _) => { if (problems.SelectedItem is AudioAnalysisIssue issue) Navigate(issue, false); };
        problems.MouseDoubleClick += (_, _) => { if (problems.SelectedItem is AudioAnalysisIssue issue) Navigate(issue, true); };
        waveform.IssueSelected += issue => { problems.SelectedItem = issue; problems.ScrollIntoView(issue); };
        waveform.RangeSelected += (start, end) => { if (work != null || wavWork != null) return; from.Text = (start / (double)source.SampleRate).ToString("0.#########", CultureInfo.CurrentCulture); to.Text = (end / (double)source.SampleRate).ToString("0.#########", CultureInfo.CurrentCulture); QueuePreview(); };
        waveform.ViewChanged += (_, _) => QueueZoom();
        void Candidate(bool zoom)
        {
            if (work != null || Result == null || candidates.SelectedItem == null) return;
            var c = Result.Candidates[Number(candidates.SelectedItem)]; long first = c.SignalTrace?.LeaderStart ?? c.StartSample;
            waveform.SetSelection(first, c.EndSample); if (zoom) { waveform.ShowRange(first, c.EndSample); tabs.SelectedIndex = 0; visualTabs.SelectedIndex = 0; }
        }
        candidates.SelectionChanged += (_, _) => Candidate(false); candidates.MouseDoubleClick += (_, _) => Candidate(true);
        programs.MouseDoubleClick += (_, _) =>
        {
            if (Result == null || programs.SelectedItem == null) return;
            var r = Result.Recoveries[Number(programs.SelectedItem)];
            long first = r.Header.Candidate.SignalTrace?.LeaderStart ?? r.Header.Candidate.StartSample, last = r.Payload.Candidate.EndSample;
            waveform.SetSelection(first, last); waveform.ShowRange(first, last); tabs.SelectedIndex = 0; visualTabs.SelectedIndex = 0;
        };
        previewTimer.Tick += async (_, _) => { previewTimer.Stop(); await TryRefreshPreview(); };
        from.TextChanged += (_, _) => QueuePreview(); to.TextChanged += (_, _) => QueuePreview();
        channel.SelectionChanged += (_, _) => QueuePreview(); measurementChannel.SelectionChanged += (_, _) => RefreshHistogram();
        testSettings.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => RefreshHistogram()));
        testSettings.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => RefreshHistogram()));
        testSettings.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => RefreshHistogram()));
        testSettings.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => RefreshHistogram()));
        length.TextChanged += (_, _) => RefreshHistogram();
        measurementPolarity.SelectionChanged += (_, _) => RefreshHistogram();
        shiftSamples.TextChanged += (_, _) =>
        {
            if (updatingShift) return;
            updatingShift = true;
            if (int.TryParse(shiftSamples.Text, out int n)) shiftMilliseconds.Text = (n * 1000.0 / source.SampleRate).ToString("0.#########", CultureInfo.CurrentCulture);
            updatingShift = false; QueuePreview();
        };
        shiftMilliseconds.TextChanged += (_, _) =>
        {
            if (updatingShift) return;
            double ms = NumberValue(shiftMilliseconds.Text);
            updatingShift = true; shiftSamples.Text = double.IsFinite(ms) && Math.Abs(ms) <= 2000 ? ((int)Math.Round(ms * source.SampleRate / 1000)).ToString() : ""; updatingShift = false; QueuePreview();
        };
        invertRight.Click += (_, _) => QueuePreview();
        Loaded += (_, _) => QueuePreview();
    }
    private void QueuePreview()
    {
        if (closed || work != null || wavWork != null) return;
        previewReady = false; UpdateWaveExport();
        previewWork?.Cancel(); zoomWork?.Cancel(); previewTimer.Stop();
        try
        {
            var desired = ReadTransform();
            if (!desired.Mix && !displayTransform.Mix)
            { waveform.RightShiftSamples = desired.RightShiftSamples - displayTransform.RightShiftSamples; waveform.InvertRight = desired.InvertRight != displayTransform.InvertRight; waveform.InvalidateVisual(); }
        }
        catch (ArgumentException) { }
        RefreshHistogram(); previewTimer.Start();
    }
    private async Task TryRefreshPreview()
    {
        try { await RefreshPreview(); }
        catch (OperationCanceledException) { if (!closed && work == null && previewWork == null && !previewTimer.IsEnabled) measurement.Text = "Preview cancelled. Previous preview retained."; }
        catch (Exception ex) { if (!closed) measurement.Text = "Cannot preview: " + ex.Message + " Previous preview retained."; }
    }
    internal async Task RefreshPreview()
    {
        if (wavWork != null) return;
        previewReady = false; UpdateWaveExport();
        previewTimer.Stop(); previewWork?.Cancel(); zoomWork?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(work?.Token ?? CancellationToken.None); previewWork = cancellation;
        cancel.IsEnabled = true;
        if (work == null) progress.IsIndeterminate = true;
        try
        {
            double first = NumberValue(from.Text), last = NumberValue(to.Text);
            if (!double.IsFinite(first) || !double.IsFinite(last) || first < 0 || last <= first || last > source.DurationSeconds + .000001) throw new ArgumentException("Enter a nonempty interval inside this recording.");
            long start = checked((long)Math.Round(first * source.SampleRate)), end = Math.Min(source.FrameCount, checked((long)Math.Round(last * source.SampleRate)));
            var transform = ReadTransform();
            // Waveform preview reads PCM only. Pulse data belongs exclusively to
            // completed Deep analysis passes, including their exact detector settings.
            var result = await Task.Run(() => AudioRangeInspection.Analyze(source, start, end, transform, 0,
                WavPulseMode.ZeroCrossing, 1, false, null, cancellation.Token, waveformOnly: true), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (closed || previewWork != cancellation) return;
            bool retainView = preview != null;
            long viewStart = waveform.Start, viewEnd = waveform.End;
            long? selectionStart = waveform.SelectionStart, selectionEnd = waveform.SelectionEnd;
            preview = result; displayTransform = transform;
            waveform.Analysis = result.Analysis; waveform.Envelope = result.Analysis.Waveform;
            waveform.RightShiftSamples = 0; waveform.InvertRight = false;
            waveform.SingleChannelLabel = transform.Mix ? "Mix (L + R) / 2" : "Mono";
            waveform.ChannelMode = transform.Mix ? 0 : channel.SelectedIndex == 1 ? 0 : channel.SelectedIndex == 2 ? 1 : 2;
            waveform.RangeStart = result.Start; waveform.RangeEnd = result.End;
            if (retainView) waveform.ShowRange(viewStart, viewEnd); else waveform.Fit();
            if (retainView)
            {
                if (selectionStart.HasValue && selectionEnd.HasValue) waveform.SetSelection(Math.Clamp(selectionStart.Value, result.Start, result.End), Math.Clamp(selectionEnd.Value, result.Start, result.End));
                else waveform.SetSelection(0, 0);
            }
            else waveform.SetSelection(result.Start, result.End);
            problems.ItemsSource = result.Analysis.Issues;
            RefreshHistogram();
            if (Result != null && scannedTransform == transform && Result.StartSample == result.Start && Result.EndSample == result.End) ApplyScanFindings(Result);
            previewReady = true;
        }
        finally
        {
            if (previewWork == cancellation)
            {
                previewWork = null; progress.IsIndeterminate = false;
                UpdateWaveExport();
                if (work == null) cancel.IsEnabled = false;
            }
        }
    }
    private string HistogramSettingsKey()
    {
        var inputs = new[] { from, to, length, thresholds, shortPulse, timeScales, tolerance, referenceTolerance, zeroDeadband, shiftSamples };
        var choices = new System.Windows.Controls.Primitives.ToggleButton[] { zero, schmitt, adaptiveCrossing, adaptive, fixedTiming, fuzzy, normal, inverted, halfSample, referenceTests, invertRight, unknownLength };
        string profiles = referenceTests.IsChecked == true ? string.Join("|", (referenceScope.SelectedIndex == 0 ? referenceControl.References : referenceSelection.SelectedItems.Cast<AudioPulseReference>())
            .Select(r => r.Name + ":" + string.Join(";", r.Guides.Select(g => $"{g.Group}:{g.Microseconds:R}")))) : "";
        return string.Join("|", inputs.Select(t => t.Text)) + string.Join("|", choices.Select(c => c.IsChecked)) + $"|{channel.SelectedIndex}|{referenceScope.SelectedIndex}|{decodeScope.SelectedIndex}|{profiles}";
    }
    private void RefreshHistogram()
    {
        if (refreshingHistogram) return;
        refreshingHistogram = true;
        try { RefreshCompletedHistogram(); }
        finally { refreshingHistogram = false; }
    }
    private void RefreshCompletedHistogram()
    {
        if (Result == null || Result.Passes.Count == 0)
        {
            histogram.Block = null; histogram.HistogramSeries = []; distributions.ItemsSource = null;
            measurement.Text = "Histogram: run Deep analysis to collect pulse measurements. Waveform preview is independent.";
            histogram.InvalidateVisual(); return;
        }
        // One representative completed pass, never summed across classification sweeps.
        // Prefer the pass which recovered most checksum-valid blocks; ties keep scan order.
        var pass = Result.Passes.OrderByDescending(p => p.ValidHeaders + p.ValidPayloads).First();
        string detector = AudioRecoveryHistogram.Detector(Result, pass);
        var measurements = pass.Measurements.Where(m => m.Block.DecodeDetector == detector).ToArray();
        string firstChannelLabel = pass.Channels == "Mix" ? "Mix" : source.ChannelCount == 1 ? "Mono" : "Left";
        if (!Equals(measurementChannel.Items[0], firstChannelLabel))
        {
            int selectedChannel = measurementChannel.SelectedIndex;
            measurementChannel.Items[0] = firstChannelLabel;
            measurementChannel.SelectedIndex = Math.Max(0, selectedChannel);
        }
        bool rightAvailable = measurements.Any(m => m.Block.Channel == 2);
        measurementChannel.IsEnabled = rightAvailable && measurements.Any(m => m.Block.Channel == 1);
        int ch = measurementChannel.IsEnabled ? measurementChannel.SelectedIndex + 1 : measurements.FirstOrDefault()?.Block.Channel ?? 1;
        if (!measurementChannel.IsEnabled && measurements.Length > 0) measurementChannel.SelectedIndex = ch - 1;
        bool both = measurements.Any(m => m.Block.Inverted) && measurements.Any(m => !m.Block.Inverted);
        measurementPolarity.IsEnabled = both;
        bool polarity = both ? measurementPolarity.SelectedIndex == 1 : measurements.FirstOrDefault()?.Block.Inverted ?? false;
        if (!both) measurementPolarity.SelectedIndex = polarity ? 1 : 0;
        displayedMeasurement = measurements.FirstOrDefault(m => m.Block.Channel == ch && m.Block.Inverted == polarity);
        histogram.Block = displayedMeasurement?.Block;
        distributions.ItemsSource = displayedMeasurement?.Block.Distributions;
        var selected = pulseGroups.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToArray();
        histogram.HistogramSeries = displayedMeasurement?.Block.Distributions.Where(d => selected.Contains(d.Group)).ToArray() ?? [];
        histogram.ReferenceOverlay = referenceControl.Selected; histogram.ShowReferenceGuides = referenceControl.ShowGrid;
        bool stale = completedHistogramSettings != HistogramSettingsKey();
        measurement.Text = (stale ? "OUTDATED — settings changed; run Deep analysis again. Showing previous measurements.\n" : "") +
            $"Latest Deep analysis — automatic representative: {pass.Test} • {detector} ×{pass.ThresholdScale:G} • zero deadband ±{pass.ZeroDeadbandPercent:G}% FS • {pass.AudioTransform}\n" +
            $"[{Result.StartSample:N0}, {Result.EndSample:N0}) • channel {ch} • {(polarity ? "inverted" : "normal")} • {histogram.HistogramSeries.Sum(d => d.Count):N0} visible pulses. " +
            "One measured series is shown; repeated passes are not added. SHORT/LONG colours compare measured durations to the pass reference; rejected pulses are included. Reference guides do not remeasure audio." +
            (displayedMeasurement?.Omitted is > 0 ? $" {displayedMeasurement.Omitted:N0} further problem spans omitted; pulse counts include all measurements." : "");
        if (preview != null && scannedTransform == displayTransform && Result.StartSample == preview.Start && Result.EndSample == preview.End) ApplyScanFindings(Result);
        histogram.InvalidateVisual();
    }
    private void ApplyScanFindings(AudioRegionRecoveryResult result)
    {
        if (preview == null) return;
        var issues = preview.Analysis.Issues.Concat(displayedMeasurement?.Issues ?? []).Concat(result.Candidates.Where(c => !c.ChecksumValid).Select(c => new AudioAnalysisIssue("AUDIO_CHECKSUM_FAILURE", DskIssueSeverity.Warning,
            $"{c.Kind} checksum failed. The affected block is known; the exact damaged byte is not. {c.RecoveryTest}", c.SignalTrace?.DataStart ?? c.StartSample, c.EndSample, Channel: c.Channel + 1, Block: c.Kind.ToString())))
            .Concat(result.Diagnostics.Where(e => e.Code is "PULSE_CLASSIFICATION_RESET" or "BAD_BYTE_STOP" or "BLOCK_TRUNCATED").Select(e => new AudioAnalysisIssue("AUDIO_DECODER_RESET", DskIssueSeverity.Warning,
                $"{e.Code}; {e.Detector}; byte {e.ByteIndex}; {e.Context}", Math.Max(result.StartSample, e.Sample - Math.Max(1, (long)Math.Ceiling(e.PulseSamples))), Math.Min(result.EndSample, e.Sample + 1), Channel: e.Channel)))
            .Concat(result.Failures.Select(f => new AudioAnalysisIssue("AUDIO_UNRESOLVED_REGION", DskIssueSeverity.Error, f.Reason + " Exact damaged byte unknown.", f.StartSample, f.EndSample))).ToArray();
        var regions = result.Candidates.Select(c => new AudioSignalRegion(c.Kind.ToString(), c.SignalTrace?.LeaderStart ?? c.StartSample, c.EndSample, null, c.Channel + 1, c.ChecksumValid ? "Checksum-valid block candidate." : "Checksum-failed block candidate.") { SampleRate = source.SampleRate }).ToArray();
        waveform.Analysis = new AudioSignalAnalysis { Source = source.Source, SampleRate = source.SampleRate, ChannelCount = preview.Analysis.ChannelCount, FrameCount = source.FrameCount,
            Blocks = preview.Analysis.Blocks, Waveform = preview.Analysis.Waveform, Regions = regions.Length > 0 ? regions : preview.Analysis.Regions, Issues = issues };
        problems.ItemsSource = issues; waveform.InvalidateVisual();
    }
    private async void QueueZoom()
    {
        zoomWork?.Cancel();
        if (preview == null || waveform.End <= waveform.Start) return;
        if (waveform.End - waveform.Start > (preview.End - preview.Start) / 8) { waveform.Envelope = preview.Analysis.Waveform; waveform.InvalidateVisual(); return; }
        var current = preview; var transform = displayTransform; long first = waveform.Start, last = waveform.End;
        int maximumBuckets = Math.Max(1000, (int)waveform.ActualWidth * 2);
        using var cancellation = new CancellationTokenSource(); zoomWork = cancellation;
        try
        {
            await Task.Delay(100, cancellation.Token);
            var envelope = await Task.Run(() =>
            {
                AudioRangeInspection.VerifySource(source);
                using var reader = AudioSignalAnalysisService.OpenReader(source.Source);
                var builder = new WaveformEnvelopeBuilder(first, last, maximumBuckets);
                transform.Visit(reader, first, last, builder.Add, cancellation.Token);
                return new WaveformRenderModel(builder.Finish(), source.FrameCount, current.Analysis.ChannelCount);
            }, cancellation.Token);
            if (!closed && preview == current && !cancellation.IsCancellationRequested) { waveform.Envelope = envelope; waveform.InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) measurement.Text = "Cannot load zoom: " + ex.Message; }
        finally { if (zoomWork == cancellation) zoomWork = null; }
    }
}
