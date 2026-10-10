using Microsoft.Win32;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed partial class AudioSignalAnalyzerWindow : Window
{
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, Margin = new(5), FontSize = 14 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(5) };
    private readonly TextBlock blockSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(5) };
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 1 };
    private readonly Button open = new() { Content = "Open WAV / FLAC…", Margin = new(4), Padding = new(10, 4, 10, 4) };
    private readonly Button cancel = new() { Content = "Cancel analysis", Margin = new(4), IsEnabled = false };
    private readonly Button report = new() { Content = "Export report…", Margin = new(4), IsEnabled = false };
    private readonly AudioSignalChart waveform = new() { Kind = "Waveform", Height = 190 };
    private readonly AudioSignalChart histogram = new() { Kind = "Histogram" };
    private readonly AudioSignalChart scatter = new() { Kind = "Scatter" };
    private readonly AudioSignalChart drift = new() { Kind = "Drift" };
    private readonly DataGrid records = Grid(), distributions = Grid(), channels = Grid(), issues = Grid(), agreement = Grid(), regions = Grid();
    private readonly CheckBox[] pulseGroups = AudioPulseReference.Groups.Select(g => new CheckBox { Content = g, IsChecked = true, Foreground = AudioSignalChart.PulseBrush(g), Margin = new(7, 4, 7, 4) }).ToArray();
    private readonly AudioPulseReferenceControl referenceControl = new();
    private readonly Button deepAnalysis = new() { Content = "Analyze selected audio…", IsEnabled = false, Margin = new(4), Padding = new(8, 3, 8, 3) };
    private readonly TabControl tabs = new();
    private readonly ComboBox channelChoice = new() { Width = 150, Margin = new(5), IsEnabled = false };
    private AudioSignalAnalysis? analysis;
    private CancellationTokenSource? work, rangeWork;
    private bool closed;
    private bool synchronizingSelection;
    internal AudioSignalAnalyzerWindow(Window? owner = null, string? source = null)
    {
        Title = "Visual Media Analysis — WAV / FLAC"; Width = 1200; Height = 850; MinWidth = 850; MinHeight = 650;
        if (owner != null) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        var root = new DockPanel { Margin = new(10) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var actions = new WrapPanel(); actions.Children.Add(open); actions.Children.Add(cancel); actions.Children.Add(report); actions.Children.Add(exportWave);
        AddButton(actions, "Copy summary", () => { if (analysis != null) Clipboard.SetText(heading.Text + "\n" + analysis.Limits); });
        top.Children.Add(actions); top.Children.Add(heading); top.Children.Add(progress); top.Children.Add(status);
        top.Children.Add(waveform);
        var navigation = new WrapPanel(); top.Children.Add(navigation);
        AddButton(navigation, "Fit", waveform.Fit); AddButton(navigation, "Zoom +", () => waveform.Zoom(.5)); AddButton(navigation, "Zoom −", () => waveform.Zoom(2));
        AddButton(navigation, "Selected block", () => { if (records.SelectedItems.Count > 1) ShowSelectedRows(records); else if (records.SelectedItem is AudioBlockAnalysis b) waveform.ShowRange(b.StartSample, b.EndSample); });
        AddButton(navigation, "Selected record", () => { if (analysis != null && records.SelectedItem is AudioBlockAnalysis b) { var selectedRecords = records.SelectedItems.Cast<AudioBlockAnalysis>().Select(p => p.Record).ToHashSet(); var parts = analysis.Blocks.Where(p => selectedRecords.Contains(p.Record)).ToArray(); waveform.ShowRange(parts.Min(p => p.StartSample), parts.Max(p => p.EndSample)); } });
        navigation.Children.Add(deepAnalysis);
        deepAnalysis.Click += (_, _) => { if (analysis != null) new AudioRegionRecoveryWindow(this, analysis, waveform.SelectionStart ?? waveform.Start, waveform.SelectionEnd ?? waveform.End).ShowDialog(); };
        channelChoice.SelectionChanged += (_, _) => { waveform.ChannelMode = Math.Max(0, channelChoice.SelectedIndex); waveform.InvalidateVisual(); UpdateWaveExport(); if (selectedIntervals.Length > 0) MeasureSelection(selectedIntervals); }; navigation.Children.Add(channelChoice);
        navigation.Children.Add(new TextBlock { Text = "Time in seconds • amplitude in FS • sample bounds are half-open", Margin = new(8), VerticalAlignment = VerticalAlignment.Center });
        records.SelectionMode = regions.SelectionMode = DataGridSelectionMode.Extended;
        root.Children.Add(tabs);
        AddTab("Records / blocks", records);
        records.AutoGeneratedColumns += (_, _) =>
        {
            string[] order = ["Record", "Name", "Block", "Profile", "Channel", "ChecksumValid", "Recovered", "StartSample", "EndSample", "Inverted", "DecodeDetector"];
            int display = 0;
            foreach (string property in order) { var column = records.Columns.FirstOrDefault(c => c.SortMemberPath == property); if (column != null) column.DisplayIndex = display++; }
        };
        var pulsePanel = new DockPanel(); var pulseHeader = new StackPanel(); DockPanel.SetDock(pulseHeader, Dock.Top); pulsePanel.Children.Add(pulseHeader);
        pulseHeader.Children.Add(blockSummary);
        var pulseChoices = new WrapPanel(); pulseHeader.Children.Add(pulseChoices);
        foreach (var check in pulseGroups) { pulseChoices.Children.Add(check); check.Checked += (_, _) => SelectDistribution(); check.Unchecked += (_, _) => SelectDistribution(); }
        pulseHeader.Children.Add(referenceControl); referenceControl.Changed += SelectDistribution;
        distributions.Height = 110; pulseHeader.Children.Add(distributions);
        var charts = new TabControl(); charts.Items.Add(new TabItem { Header = "Histogram", Content = histogram }); charts.Items.Add(new TabItem { Header = "High / Low scatter", Content = scatter }); charts.Items.Add(new TabItem { Header = "Drift", Content = drift }); pulsePanel.Children.Add(charts);
        AddTab("Pulse statistics", new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pulsePanel }); AddTab("Channels / levels", channels); AddTab("Detector agreement", agreement); AddTab("Issues", issues); AddTab("Regions", regions);
        AddTab("Definitions", new ScrollViewer { Content = new TextBlock { Text = new AudioSignalAnalysis().Limits + "\n\nPulse agreement: one-to-one physical-level matches with end positions within max(2 samples, 10% of pulse duration), bounded 64-pulse queues. Classification agreement compares SHORT/LONG for matched pulses.\n\nOnly regions with decoder trace positions are labelled leader/sync/data/checksum. Unassigned audio contributes to Estimated noise; it may contain music, speech or undecodable tape data.\n\nSelected blocks and arbitrary interval pulse measurements use standard ZeroCrossing preprocessing, even when decoding used Schmitt or selective recovery. Short-term variation is the per-window local standard deviation; drift is change in timing scale between windows; global speed offset is reported separately.\n\nHistogram bins are exact sample-duration counts. Scatter is bounded to 20,000 pairs by deterministic thinning. Charts are read-only; this analyzer never rewrites the capture.", TextWrapping = TextWrapping.Wrap, Margin = new(12), FontSize = 14 } });
        open.Click += (_, _) => ChooseSource(); cancel.Click += (_, _) => { work?.Cancel(); wavWork?.Cancel(); }; report.Click += (_, _) => ExportReport();
        records.SelectionChanged += (_, _) =>
        {
            if (synchronizingSelection) return;
            if (records.SelectedItems.Count > 1) { SelectRows(records); return; }
            CancelSelectionMeasurement();
            SelectBlock();
            if (!synchronizingSelection && records.SelectedItem is AudioBlockAnalysis b) Navigate(b.StartSample, b.EndSample, b.Record, b.Block, zoom: false, channel: b.Channel);
        };
        records.MouseDoubleClick += (_, _) => { if (records.SelectedItems.Count > 1) { ShowSelectedRows(records); return; } if (records.SelectedItem is AudioBlockAnalysis b) NavigateRecord(b); };
        issues.SelectionChanged += (_, _) =>
        {
            if (synchronizingSelection || issues.SelectedItem is not AudioAnalysisIssue issue) return;
            Navigate(issue.StartSample, issue.EndSample, issue.Record, issue.Block, synchronizeIssue: false, channel: issue.Channel);
        };
        issues.MouseDoubleClick += (_, _) => { if (issues.SelectedItem is AudioAnalysisIssue issue) Navigate(issue.StartSample, issue.EndSample, issue.Record, issue.Block, synchronizeIssue: false, channel: issue.Channel); };
        regions.SelectionChanged += (_, _) => { if (synchronizingSelection) return; if (regions.SelectedItems.Count > 1) { SelectRows(regions); return; } if (regions.SelectedItem is AudioSignalRegion region) NavigateRegion(region, zoom: false); };
        regions.MouseDoubleClick += (_, _) => { if (regions.SelectedItems.Count > 1) { ShowSelectedRows(regions); return; } if (regions.SelectedItem is AudioSignalRegion region) NavigateRegion(region); };
        waveform.RegionSelected += region => NavigateRegion(region, zoom: false);
        waveform.RangeSelected += (start, end) =>
        {
            synchronizingSelection = true;
            try { records.SelectedItem = regions.SelectedItem = issues.SelectedItem = null; }
            finally { synchronizingSelection = false; }
            MeasureSelection([(start, end)]);
            status.Text = $"Selected audio: {start / (double)analysis!.SampleRate:F6} … {end / (double)analysis.SampleRate:F6} s. Use Analyze selected audio for a deeper scan.";
        };
        waveform.SelectionChanged += UpdateWaveExport;
        exportWave.Click += async (_, _) => await ChooseWaveExport();
        waveform.ViewChanged += RefreshEnvelope; drift.ViewChanged += (start, end) => waveform.ShowRange(start, end);
        Closed += (_, _) => { closed = true; work?.Cancel(); rangeWork?.Cancel(); pulseWork?.Cancel(); wavWork?.Cancel(); };
        if (source != null) Loaded += async (_, _) => await Analyze(source);
        else heading.Text = "Captured / measured PCM — select a WAV or FLAC capture. Analysis does not change the file.";
    }
    internal static DataGrid Grid()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, Margin = new(4), EnableRowVirtualization = true, EnableColumnVirtualization = true };
        var selected = new SolidColorBrush(Color.FromRgb(24, 103, 178));
        grid.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = selected;
        grid.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.White;
        var rowStyle = new Style(typeof(DataGridRow)); var rowTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowTrigger.Setters.Add(new Setter(Control.BackgroundProperty, selected)); rowTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); rowStyle.Triggers.Add(rowTrigger); grid.RowStyle = rowStyle;
        var cellStyle = new Style(typeof(DataGridCell)); var cellTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        cellTrigger.Setters.Add(new Setter(Control.BackgroundProperty, selected)); cellTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); cellStyle.Triggers.Add(cellTrigger); grid.CellStyle = cellStyle;
        grid.AutoGeneratingColumn += (_, e) =>
        {
            if (e.PropertyType != typeof(string) && (typeof(IEnumerable).IsAssignableFrom(e.PropertyType) || e.PropertyType == typeof(AudioDetectorAgreement))) { e.Cancel = true; return; }
            e.Column.Header = System.Text.RegularExpressions.Regex.Replace(e.PropertyName, "(?<=[a-z])(?=[A-Z])", " ");
            e.Column.SortMemberPath = e.PropertyName;
            e.Column.CanUserResize = true;
            if (e.PropertyName is "Description" or "Evidence" or "Definition" or "PulseMeasurement" or "Tests" or "HeaderTests" or "PayloadTests") e.Column.Width = 320;
            var type = Nullable.GetUnderlyingType(e.PropertyType) ?? e.PropertyType;
            if (e.Column is DataGridTextColumn text && text.Binding is System.Windows.Data.Binding binding)
                binding.StringFormat = type == typeof(double) || type == typeof(float) ? "0.####" : type == typeof(long) || type == typeof(int) ? "N0" : null;
        };
        return grid;
    }
    private void AddTab(string title, UIElement content) => tabs.Items.Add(new TabItem { Header = title, Content = content });
    private static void AddButton(Panel panel, string title, Action action)
    { var button = new Button { Content = title, Margin = new(4), Padding = new(8, 3, 8, 3) }; button.Click += (_, _) => action(); panel.Children.Add(button); }
    private void ChooseSource()
    {
        var dialog = new OpenFileDialog { Filter = "Audio captures (*.wav;*.flac)|*.wav;*.flac", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _ = Analyze(dialog.FileName);
    }
    internal async Task Analyze(string path)
    {
        if (wavWork != null) return;
        CancelSelectionMeasurement();
        work?.Cancel(); rangeWork?.Cancel(); var cancellation = new CancellationTokenSource(); work = cancellation;
        open.IsEnabled = report.IsEnabled = deepAnalysis.IsEnabled = false; cancel.IsEnabled = true; progress.Value = 0;
        UpdateWaveExport();
        status.Text = "Analyzing…";
        var reporter = new Progress<WavAnalysisProgress>(p => { if (!closed && work == cancellation) { status.Text = $"{p.Stage} — {p.Fraction:P0}"; progress.Value = p.Fraction; } });
        try
        {
            var result = await Task.Run(() => AudioSignalAnalysisService.Analyze(path, reporter, cancellation.Token), cancellation.Token);
            if (closed || cancellation.IsCancellationRequested) return;
            analysis = result;
            deepAnalysis.IsEnabled = true;
            channelChoice.ItemsSource = result.ChannelCount == 1 ? new[] { "Mono" } : new[] { "Left", "Right", "Overlay L/R" };
            channelChoice.SelectedIndex = result.ChannelCount == 1 ? 0 : 2;
            channelChoice.IsEnabled = result.ChannelCount > 1;
            heading.Text = $"{Path.GetFileName(result.Source)} • {result.Format} • {result.SampleRate:N0} Hz / {result.BitsPerSample} bit / {result.ChannelCount} ch • {result.DurationSeconds:F2} s\nCaptured / measured PCM • {result.Quality}: {result.QualityExplanation}\nPreferred channel: {result.BestChannel} — {result.BestChannelReasons}";
            records.ItemsSource = result.Blocks; channels.ItemsSource = result.Channels; issues.ItemsSource = result.Issues;
            agreement.ItemsSource = result.DecodeAgreement; regions.ItemsSource = result.Regions;
            foreach (var chart in new[] { waveform, histogram, scatter, drift }) { chart.Analysis = result; chart.Envelope = null; chart.InvalidateVisual(); }
            waveform.SetSelection(0, 0);
            waveform.Fit(); if (result.Blocks.Count > 0) records.SelectedIndex = 0;
            SelectDistribution();
            status.Text = "Analysis complete. Select an issue or double-click a region to locate it. Pulse durations and references are in µs."; progress.Value = 1; report.IsEnabled = true;
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "Analysis cancelled."; }
        catch (Exception ex) { if (!closed) status.Text = "Cannot analyze: " + ex.Message; }
        finally { if (!closed && work == cancellation) { open.IsEnabled = true; cancel.IsEnabled = false; report.IsEnabled = deepAnalysis.IsEnabled = analysis != null; } cancellation.Dispose(); if (work == cancellation) work = null; UpdateWaveExport(); }
    }
    private void SelectBlock()
    {
        var b = records.SelectedItem as AudioBlockAnalysis;
        foreach (var chart in new[] { histogram, scatter, drift }) { chart.Block = b; chart.InvalidateVisual(); }
        distributions.ItemsSource = b?.Distributions;
        referenceControl.FollowBlock(b);
        blockSummary.Text = b == null ? "Select a decoded block in Records / blocks." : $"{b.Record}: {b.Name} / {b.Block} • {b.Profile} • reference {b.ReferenceName}\nChannel {b.Channel}, {b.DecodeDetector}, inverted {b.Inverted}, checksum {(b.ChecksumValid ? "valid" : "failed")}, recovered {b.Recovered}\n{b.PulseMeasurement}\nGlobal scale {b.GlobalTimingScale:F4} • speed {b.SpeedOffsetPercent:+0.00;-0.00;0}% • pulse agreement {b.Agreement.PulseAgreementPercent:F1}% • classification agreement {b.Agreement.ClassificationAgreementPercent:F1}%";
        SelectDistribution();
    }
    private void SelectDistribution()
    {
        var selected = pulseGroups.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToArray();
        histogram.HistogramSeries = (selectedIntervals.Length > 0 ? selectionBlock : records.SelectedItem as AudioBlockAnalysis)?.Distributions.Where(d => selected.Contains(d.Group)).ToArray();
        histogram.ReferenceOverlay = referenceControl.Selected; histogram.ShowReferenceGuides = referenceControl.ShowGrid;
        if (analysis != null) { analysis.ComparisonReference = referenceControl.Selected; analysis.ShowComparisonReference = referenceControl.ShowGrid; analysis.VisiblePulseGroups = selected; }
        histogram.InvalidateVisual();
    }
    internal void NavigateRecord(AudioBlockAnalysis block)
    {
        if (analysis == null) return;
        var parts = analysis.Blocks.Where(b => b.Record == block.Record).ToArray();
        Navigate(parts.Min(b => b.StartSample), parts.Max(b => b.EndSample), block.Record, block.Block, channel: block.Channel);
    }
    internal void NavigateRegion(AudioSignalRegion region, bool zoom = true)
    {
        Navigate(region.StartSample, region.EndSample, region.Record, region.Kind, zoom: zoom, channel: region.Channel, exactRegion: region);
        MeasureSelection([(region.StartSample, region.EndSample)]);
    }
    private void Navigate(long start, long end, int? record, string? kind, bool zoom = true, bool synchronizeIssue = true, int? channel = null, AudioSignalRegion? exactRegion = null)
    {
        if (analysis == null) return;
        var b = analysis.Blocks.FirstOrDefault(b => (!record.HasValue || b.Record == record) && (!channel.HasValue || b.Channel == channel) && (kind == null || kind == "Recovered" || kind.StartsWith(b.Block, StringComparison.OrdinalIgnoreCase)) && b.StartSample < Math.Max(end, start + 1) && b.EndSample > start);
        synchronizingSelection = true;
        try
        {
            records.SelectedItem = b; if (b != null) records.ScrollIntoView(b);
            regions.SelectedItem = exactRegion ?? (b == null ? null : analysis.Regions.FirstOrDefault(r => r.Record == b.Record && r.Channel == b.Channel && (r.Kind == b.Block || r.Kind == b.Block + " (partial trace)")));
            if (regions.SelectedItem != null) regions.ScrollIntoView(regions.SelectedItem);
            if (synchronizeIssue) { issues.SelectedItem = b == null ? null : analysis.Issues.FirstOrDefault(i => i.Record == b.Record && (i.Block == null || i.Block == b.Block)); if (issues.SelectedItem != null) issues.ScrollIntoView(issues.SelectedItem); }
        }
        finally { synchronizingSelection = false; }
        waveform.SetSelection(start, end);
        if (zoom) { long pad = Math.Max(4, (end - start) / 20); waveform.ShowRange(Math.Max(0, start - pad), end + pad); }
        status.Text = $"Selected samples [{start:N0}, {end:N0}) — {start / (double)analysis.SampleRate:F6} … {end / (double)analysis.SampleRate:F6} s";
    }
    private async void RefreshEnvelope(long start, long end)
    {
        rangeWork?.Cancel(); if (analysis == null || closed) return;
        var source = analysis; var cancellation = new CancellationTokenSource(); rangeWork = cancellation;
        waveform.Envelope = source.Waveform; waveform.InvalidateVisual();
        // The base envelope is sufficient at fit scale; zoom reads only visible PCM.
        if (end - start > source.FrameCount / 8) { waveform.Envelope = source.Waveform; waveform.InvalidateVisual(); cancellation.Dispose(); rangeWork = null; return; }
        try
        {
            await Task.Delay(100, cancellation.Token);
            int buckets = Math.Max(1000, (int)waveform.ActualWidth * 2);
            var result = await Task.Run(() =>
            {
                var file = new FileInfo(source.Source);
                if (file.Length != source.SourceLength || file.LastWriteTimeUtc != source.SourceModifiedUtc) throw new IOException("The source changed since analysis. Analyze it again.");
                return WaveformRenderModel.ReadRange(source.Source, start, end, buckets, cancellation.Token);
            }, cancellation.Token);
            if (!closed && !cancellation.IsCancellationRequested && analysis == source) { waveform.Envelope = result; waveform.InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !cancellation.IsCancellationRequested) status.Text = "Cannot read zoomed PCM: " + ex.Message; }
        finally { cancellation.Dispose(); if (rangeWork == cancellation) rangeWork = null; }
    }
    private void ExportReport()
    {
        if (analysis == null) return;
        var dialog = new SaveFileDialog { Filter = "JSON report (*.json)|*.json|CSV report (*.csv)|*.csv|Text report (*.txt)|*.txt", FileName = Path.GetFileNameWithoutExtension(analysis.Source) + "-analysis", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, AudioAnalysisReport.Export(analysis, new[] { "json", "csv", "txt" }[dialog.FilterIndex - 1])); status.Text = "Report saved: " + dialog.FileName; }
        catch (Exception ex) { status.Text = "Cannot export report: " + ex.Message; }
    }
}
