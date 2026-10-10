using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MZTools;

internal sealed partial class AudioSignalAnalyzerWindow
{
    private CancellationTokenSource? pulseWork;
    private AudioBlockAnalysis? selectionBlock;
    private (long Start, long End)[] selectedIntervals = [];

    private void CancelSelectionMeasurement()
    {
        pulseWork?.Cancel();
        selectionBlock = null; selectedIntervals = [];
    }

    private static (long Start, long End)[] RowRanges(DataGrid grid) => grid.SelectedItems.Cast<object>()
        .Select(item => item switch {
            AudioBlockAnalysis b => (b.StartSample, b.EndSample),
            AudioSignalRegion r => (r.StartSample, r.EndSample),
            _ => (0L, 0L)
        }).Where(r => r.Item2 > r.Item1).ToArray();

    private void SelectRows(DataGrid grid)
    {
        var spans = RowRanges(grid);
        if (spans.Length == 0) return;
        waveform.SetSelection(spans.Min(r => r.Start), spans.Max(r => r.End));
        MeasureSelection(spans);
    }

    private void ShowSelectedRows(DataGrid grid)
    {
        var spans = RowRanges(grid);
        if (spans.Length > 0) waveform.ShowRange(spans.Min(r => r.Start), spans.Max(r => r.End));
    }

    internal async void MeasureSelection((long Start, long End)[] spans)
    {
        pulseWork?.Cancel();
        if (analysis == null || closed) return;
        selectedIntervals = AudioRangeInspection.MergeRanges(spans);
        if (selectedIntervals.Length == 0) { CancelSelectionMeasurement(); return; }
        var intervals = selectedIntervals; var source = analysis;
        var reference = records.SelectedItem as AudioBlockAnalysis;
        bool mix = source.ChannelCount == 2 && channelChoice.SelectedIndex == 2;
        int selectedChannel = source.ChannelCount == 2 && channelChoice.SelectedIndex == 1 ? 1 : 0;
        var cancellation = new CancellationTokenSource(); pulseWork = cancellation;
        selectionBlock = null;
        histogram.HistogramSeries = []; distributions.ItemsSource = null;
        foreach (var chart in new[] { histogram, scatter, drift }) { chart.Block = null; chart.InvalidateVisual(); }
        blockSummary.Text = "Measuring selected audio…";
        try
        {
            await Task.Delay(150, cancellation.Token);
            var block = await Task.Run(() => AudioRangeInspection.AnalyzeSelection(source, intervals,
                selectedChannel, mix, reference, cancellation.Token), cancellation.Token);
            if (closed || cancellation.IsCancellationRequested || analysis != source) return;
            selectionBlock = block;
            foreach (var chart in new[] { histogram, scatter, drift }) { chart.Block = block; chart.InvalidateVisual(); }
            distributions.ItemsSource = block.Distributions;
            blockSummary.Text = $"Selected audio • {block.PulseMeasurement}\n{intervals.Sum(r => r.End - r.Start):N0} selected samples • {block.Distributions.Sum(d => d.Count):N0} measured pulse halves. Gaps between selected intervals are excluded.";
            SelectDistribution();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !cancellation.IsCancellationRequested) blockSummary.Text = "Cannot measure selected audio: " + ex.Message; }
        finally { if (pulseWork == cancellation) pulseWork = null; cancellation.Dispose(); }
    }
}
