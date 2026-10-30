namespace MZTools.Tests;

public class AudioSelectionTests
{
    [Fact]
    public void HistogramZoomOut_FullSpanDoesNotReverseClampLimitsThroughRounding()
    {
        var bounds = new AudioHistogramRange(38.09523809523809, 300);
        Assert.True(bounds.Max - (bounds.Max - bounds.Min) < bounds.Min);
        Assert.Equal(bounds, bounds.Constrain(-1000, 1000, 125000.0 / 44100));
        var narrow = bounds.Constrain(299.9, 300, 125000.0 / 44100);
        Assert.True(narrow.Min >= bounds.Min && narrow.Max <= bounds.Max && narrow.Max > narrow.Min);
        Assert.Equal(bounds, bounds.Constrain(narrow.Min - 1000, narrow.Max + 1000, 125000.0 / 44100));
    }

    [Fact]
    public void OverlappingSelections_AreMergedWithoutFillingUnselectedGaps()
    {
        Assert.Equal(new[] { (10L, 40L), (70L, 90L) },
            AudioRangeInspection.MergeRanges([(20, 40), (10, 30), (70, 80), (80, 90), (12, 15), (5, 5)]));
    }

    [Fact]
    public void WeightedHistogramMerge_PreservesStatistics()
    {
        var weighted = new AudioDurationCounter(); var individual = new AudioDurationCounter();
        weighted.Add(10.5, 3); weighted.Add(50, 7);
        for (int i = 0; i < 10; i++) individual.Add(i < 3 ? 10.5 : 50);
        var a = weighted.Finish("SHORT High", 44100, 238);
        var b = individual.Finish("SHORT High", 44100, 238);
        Assert.Equal(b.Count, a.Count); Assert.Equal(b.Mean!.Value, a.Mean!.Value, 8);
        Assert.Equal(b.StdDev!.Value, a.StdDev!.Value, 8);
        Assert.Equal(b.Median, a.Median); Assert.Equal(b.P5, a.P5); Assert.Equal(b.P95, a.P95);
        Assert.Equal(b.Histogram, a.Histogram);
    }

    [Fact]
    public void SelectionAndDeepHistogram_DoNotMultiplyPulsesAcrossRepeatedRangesOrDetectors()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            SharpTapeExporter.Export(path, [TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2, 3, 4]))],
                SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800);
            var source = AudioSignalAnalysisService.Analyze(path);
            var single = AudioRangeInspection.AnalyzeSelection(source, [(0, source.FrameCount)], 0, false, null);
            var overlapping = AudioRangeInspection.AnalyzeSelection(source,
                [(0, source.FrameCount), (source.FrameCount / 4, source.FrameCount / 2)], 0, false, null);
            Assert.True(single.Distributions.Sum(d => d.Count) > 0);
            for (int i = 0; i < 4; i++) Assert.Equal(single.Distributions[i].Histogram, overlapping.Distributions[i].Histogram);
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, source.FrameCount, options:
                new() { ZeroCrossing = true, Schmitt = true, AdaptiveZeroCrossing = true, ThresholdScales = [.65, 1.4] });
            Assert.All(result.Passes, pass => {
                Assert.Equal(3, pass.Measurements.Select(m => m.Block.DecodeDetector).Distinct().Count());
                string selected = AudioRecoveryHistogram.Detector(result, pass);
                Assert.Contains(selected, new[] { "ZeroCrossing", "Schmitt", "AdaptiveZeroCrossing" });
                Assert.True(pass.Measurements.Where(m => m.Block.DecodeDetector == selected).All(m => m.Block.Distributions.Sum(d => d.Count) > 0));
            });
        }
        finally { File.Delete(path); }
    }
}
