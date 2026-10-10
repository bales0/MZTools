using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.SoundFile;

namespace MZTools.Tests;

public class AudioSignalAnalysisTests
{
    [Fact]
    public void WaveformLevels_BoundRenderCountPreserveSpikesAndKeepSampleDetail()
    {
        var source = Enumerable.Range(0, 65537).Select(i => new WaveformBucket(i, i + 1,
            i == 12345 ? -1 : 0, i == 54321 ? 1 : 0, i == 33333 ? -.75f : 0, i == 33334 ? .8f : 0)).ToArray();
        var model = new WaveformRenderModel(source, source.Length, 2);
        var fit = model.VisibleBuckets(0, source.Length, 1000);
        Assert.InRange(fit.Count, 1, 1001);
        Assert.Equal(-1, fit.Min(b => b.LeftMin)); Assert.Equal(1, fit.Max(b => b.LeftMax));
        Assert.Equal(-.75f, fit.Min(b => b.RightMin)); Assert.Equal(.8f, fit.Max(b => b.RightMax));
        var zoom = model.VisibleBuckets(12340, 12350, 1000);
        Assert.Equal(source.Skip(12340).Take(10), zoom);
        Assert.Empty(model.VisibleBuckets(65537, 70000, 1000));
        var panned = model.VisibleBuckets(32000, 40000, 1000);
        Assert.InRange(panned.Count, 1, 1001);
        Assert.All(panned, b => Assert.True(b.EndSample > 32000 && b.StartSample < 40000));
        Assert.Equal(-.75f, panned.Min(b => b.RightMin)); Assert.Equal(.8f, panned.Max(b => b.RightMax));
    }

    [Fact]
    public void HistogramRange_FitsDataReferenceAndOutliers()
    {
        var counter = new AudioDurationCounter();
        foreach (int duration in new[] { 230, 250, 275 }) counter.Add(duration);
        var range = AudioHistogramRange.Create(counter.Finish("SHORT High", 1000000, 260), 1000000);
        Assert.InRange(range.Min, 220, 230); Assert.InRange(range.Max, 275, 285);
        Assert.True(45 / (range.Max - range.Min) > .8);
        var distant = AudioHistogramRange.Create(counter.Finish("SHORT High", 1000000, 800), 1000000);
        Assert.True(distant.Min < 230 && distant.Max > 800);
        counter.Add(2000);
        Assert.True(AudioHistogramRange.Create(counter.Finish("SHORT High", 1000000, 260), 1000000).Max > 2000);
    }

    [Fact]
    public void HistogramRange_SingleDurationAndEmptyRemainFinite()
    {
        var counter = new AudioDurationCounter(); counter.Add(250);
        var range = AudioHistogramRange.Create(counter.Finish("SHORT High", 1000000, 250), 44100);
        Assert.True(double.IsFinite(range.Min) && double.IsFinite(range.Max));
        Assert.True(range.Max > range.Min && range.Min > 0);
        var empty = AudioHistogramRange.Create(new AudioDurationCounter().Finish("LONG Low", 44100, null), 44100);
        Assert.True(empty.Max > empty.Min);
    }

    [Fact]
    public void UnassignedAudio_IsComplementOfMergedClampedBlocks()
    {
        var regions = AudioRegionCoverage.Unassigned(100, new[] { (60L, 90L), (-10L, 10L), (5L, 20L), (30L, 40L), (35L, 60L), (90L, 90L) });
        Assert.Equal(new[] { (20L, 30L), (90L, 100L) }, regions.Select(r => (r.StartSample, r.EndSample)));
        Assert.All(regions, r => { Assert.Null(r.Record); Assert.Contains("not a confirmed silent gap", r.Evidence); });
        Assert.Single(AudioRegionCoverage.Unassigned(100, Array.Empty<(long, long)>()));
        Assert.Empty(AudioRegionCoverage.Unassigned(100, new[] { (-1L, 101L) }));
        Assert.Empty(AudioRegionCoverage.Unassigned(0, Array.Empty<(long, long)>()));
    }

    [Fact]
    public void Distribution_WeightedQuantilesPopulationDeviationAndExactHistogram()
    {
        var counter = new AudioDurationCounter();
        foreach (int n in new[] { 1, 1, 3, 5 }) counter.Add(n);
        var result = counter.Finish("SHORT High", 1000000, 2);
        Assert.Equal(4, result.Count); Assert.Equal(2.5, result.Mean); Assert.Equal(2, result.Median);
        Assert.Equal(Math.Sqrt(2.75), result.StdDev!.Value, 10);
        Assert.Equal(1, result.P5); Assert.Equal(4.7, result.P95!.Value, 10);
        Assert.Equal(25, result.DeviationPercent); Assert.Equal(2, result.Histogram[0].Count);
    }

    [Fact]
    public void EmptyDistribution_HasMissingMetricsAndSerializableReport()
    {
        var result = new AudioDurationCounter().Finish("LONG Low", 44100, null);
        Assert.Null(result.Mean); Assert.Null(result.Median); Assert.Empty(result.Histogram);
        using var json = JsonDocument.Parse(AudioAnalysisReport.Export(new(), "json"));
        Assert.Contains("Captured / measured PCM", json.RootElement.GetProperty("Layer").GetString());
    }

    [Fact]
    public void Envelope_PreservesExtremaFinalPartialBucketAndActualSampleBounds()
    {
        var envelope = new WaveformEnvelopeBuilder(100, 107, 3);
        int[] samples = [0, -8388608, 4194304, 0, 8388352, -4194304, 256];
        for (int n = 0; n < samples.Length; n++) envelope.Add(100 + n, samples[n], -samples[n] / 2);
        var buckets = envelope.Finish();
        Assert.Equal(3, buckets.Count); Assert.Equal(100, buckets[0].StartSample); Assert.Equal(103, buckets[0].EndSample);
        Assert.Equal(-1, buckets[0].LeftMin); Assert.Equal(.5f, buckets[0].LeftMax);
        Assert.Equal(106, buckets[2].StartSample); Assert.Equal(107, buckets[2].EndSample);
        Assert.Equal(buckets[2].LeftMin, buckets[2].LeftMax);
        Assert.Throws<ArgumentOutOfRangeException>(() => envelope.Add(107, 0, 0));
    }

    [Fact]
    public void PcmLevels_AreNormalizedAndClippingUsesOriginalBitDepth()
    {
        var levels = new AudioLevelAccumulator(16);
        levels.Add(-8388608, true, 0); levels.Add(8388352, true, 1); levels.Add(0, false, 2); levels.Add(4194304, false, 3);
        var result = levels.Finish(1, "Left", 1, 2, 2, null);
        Assert.Equal(2, result.ClippingCount); Assert.Equal(50, result.ClippingPercent);
        Assert.Equal(1, result.Peak); Assert.Equal(-1, result.NegativePeak);
        Assert.Equal(32767 / 32768.0, result.PositivePeak, 10);
        Assert.Equal((-1 + 32767 / 32768.0 + .5) / 4, result.DcOffset, 10);
        Assert.Equal(Math.Sqrt(.125), result.EstimatedNoiseRms!.Value, 10);
        Assert.True(result.EstimatedSeparationDb > 0);
    }

    [Fact]
    public void Agreement_IsOneToOneAndSeparatesPulseAndClassificationAgreement()
    {
        var c = new AudioPulseAgreementCounter();
        c.Add(WavPulseMode.ZeroCrossing, true, 100, 100, false);
        c.Add(WavPulseMode.Schmitt, true, 99, 102, false);
        c.Add(WavPulseMode.ZeroCrossing, false, 100, 200, false);
        c.Add(WavPulseMode.Schmitt, false, 102, 202, true);
        c.Add(WavPulseMode.Schmitt, true, 20, 250, false);
        var result = c.Finish();
        Assert.Equal(2, result.MatchedPulses); Assert.Equal(80, result.PulseAgreementPercent); Assert.Equal(50, result.ClassificationAgreementPercent);
    }

    [Fact]
    public void Drift_SeparatesGlobalScaleWindowVariationAndReferenceMeans()
    {
        var candidate = Candidate(); var block = new AudioBlockAccumulator(1, "TEST", candidate, TapeProfile.Normal1_1, false, 1000000);
        long end = 0;
        for (int n = 0; n < 512; n++) { int duration = n < 256 ? 238 : 262; end += duration; block.Add(WavPulseMode.ZeroCrossing, false, duration, end); }
        var result = block.Finish();
        Assert.Equal(2, result.Drift.Count); Assert.Equal(256, result.Drift[0].Pulses);
        Assert.True(result.Drift[1].TimingScale > result.Drift[0].TimingScale);
        Assert.Equal(0, result.Drift[0].LocalStdDevPercent, 5);
        Assert.True(result.SpeedOffsetPercent < 0); Assert.Equal(512, result.Distributions[0].Count);
        Assert.Equal(SharpZ80TimingTable.Rom800Normal.ShortHighMicroseconds, result.Distributions[0].Reference);
    }

    [Fact]
    public void QuietGap_IsExplicitlyEstimatedAndStopsAtLoudWindow()
    {
        var quiet = new AudioQuietRegions(1000);
        for (int n = 0; n < 300; n++) quiet.Add(n, n < 200 ? 0 : 8388608);
        var r = Assert.Single(quiet.Finish(300)); Assert.Equal(0, r.StartSample); Assert.Equal(200, r.EndSample);
        Assert.Contains("estimated", r.Kind); Assert.Contains("not proof", r.Evidence);
    }

    [Theory]
    [InlineData((int)TapeProfile.Normal1_1)]
    [InlineData((int)TapeProfile.Tc1_2)]
    [InlineData((int)TapeProfile.Ic1_3)]
    public void Capture_MapsActualLeaderSyncDataChecksumsAndKeepsInputUnchanged(int profileValue)
    {
        string path = MakeTape((TapeProfile)profileValue);
        try
        {
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            var result = AudioSignalAnalysisService.Analyze(path);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(2, result.Blocks.Count);
            foreach (string kind in new[] { "Header leader", "Header sync", "Header", "Header checksum", "Payload leader", "Payload sync", "Payload", "Payload checksum" })
            {
                var region = Assert.Single(result.Regions, r => r.Kind == kind);
                Assert.True(region.StartSample < region.EndSample, kind); Assert.InRange(region.EndSample, 1, result.FrameCount);
                Assert.Equal(region.StartSample / (double)result.SampleRate, region.StartSeconds); Assert.Equal(1, region.Record);
            }
            Assert.All(result.Blocks, b => { Assert.True(b.ChecksumValid); Assert.Equal(4, b.Distributions.Count); Assert.True(b.Distributions.Sum(d => d.Count) > 100); Assert.NotEmpty(b.Drift); });
            Assert.Equal(1, result.Channels[0].ValidRecords);
            Assert.Equal(1, result.DecodeAgreement[0].ZeroCrossValidRecords); Assert.Equal(1, result.DecodeAgreement[0].SchmittValidRecords);
            Assert.Equal(100, result.DecodeAgreement[0].RecordAgreementPercent);
            using var report = JsonDocument.Parse(AudioAnalysisReport.Export(result, "json"));
            Assert.Equal(2, report.RootElement.GetProperty("Blocks").GetArrayLength());
            Assert.Contains("pulse statistics", AudioAnalysisReport.Export(result, "csv"));
            Assert.Contains("samples [", AudioAnalysisReport.Export(result, "txt"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RepeatedPrograms_UseExistingAssemblyWithoutInflatingAgreementCounts()
    {
        string path = Path.Combine(Path.GetTempPath(), "audio-repeated-" + Guid.NewGuid() + ".wav");
        try
        {
            var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2, 3]));
            SharpTapeExporter.Export(path, [record, record.DeepClone()], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800);
            var result = AudioSignalAnalysisService.Analyze(path);
            // The existing decoder collapses identical repeated program content.
            // Diagnostics preserve that assembly policy and do not count detector/copy duplicates as programs.
            Assert.Single(WavHeuristicAnalyzer.ReadFile(path));
            Assert.Equal(1, result.Channels[0].ValidRecords); Assert.Equal(1, result.DecodeAgreement[0].ZeroCrossValidRecords);
            Assert.Equal(1, result.DecodeAgreement[0].SchmittValidRecords); Assert.Equal(100, result.DecodeAgreement[0].RecordAgreementPercent);
            Assert.Equal(2, result.Blocks.Count); Assert.Equal(2, result.DecodeAgreement[0].ZeroCrossValidBlocks);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FlacAndWav_HaveIdenticalPcmLevelsRegionsAndPulseStatistics()
    {
        string wav = MakeTape(TapeProfile.Normal1_1), flac = Path.ChangeExtension(wav, ".flac");
        try
        {
            using (var source = new SoundFileReader(wav)) SoundFileWriter.CreateSoundFile(flac, source);
            var a = AudioSignalAnalysisService.Analyze(wav); var b = AudioSignalAnalysisService.Analyze(flac);
            Assert.Equal("FLAC", b.Format); Assert.Equal(a.FrameCount, b.FrameCount);
            Assert.Equal(a.Channels[0].Rms, b.Channels[0].Rms, 8);
            Assert.Equal(a.Regions, b.Regions);
            Assert.Equal(a.Blocks[1].Distributions.Select(d => d.Count), b.Blocks[1].Distributions.Select(d => d.Count));
            Assert.Equal(a.Blocks[1].Distributions.Select(d => d.Mean), b.Blocks[1].Distributions.Select(d => d.Mean));
            var range = WaveformRenderModel.ReadRange(flac, 123, 1234, 500, default);
            Assert.Equal(123, range.Buckets.First().StartSample); Assert.Equal(1234, range.Buckets.Last().EndSample);
            Assert.InRange(range.Buckets.Count, 1, 500);
        }
        finally { File.Delete(wav); File.Delete(flac); }
    }

    [Fact]
    public void SilenceStereo_ReportsNoRecordsLowLevelAndMissingTimingInsteadOfGood()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            File.WriteAllBytes(path, Silence()); var result = AudioSignalAnalysisService.Analyze(path);
            Assert.Equal("Poor", result.Quality); Assert.StartsWith("Not assessable", result.BestChannel);
            Assert.Equal(2, result.Channels.Count); Assert.All(result.Channels, c => { Assert.Equal(0, c.Rms); Assert.Null(c.TimingErrorPercent); });
            Assert.Contains(result.Issues, i => i.Code == "AUDIO_NO_RECORDS"); Assert.Null(result.StereoRmsDifferenceDb);
            Assert.Throws<OperationCanceledException>(() => AudioSignalAnalysisService.Analyze(path, token: new CancellationToken(true)));
            Assert.Throws<OperationCanceledException>(() => WaveformRenderModel.ReadRange(path, 0, 100, 100, new CancellationToken(true)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StereoCapture_SelectsRecoverableChannelAndMapsIssuesAndRegions()
    {
        string mono = MakeTape(TapeProfile.Normal1_1), stereo = Path.ChangeExtension(mono, ".stereo.wav");
        try
        {
            byte[] original = File.ReadAllBytes(mono); byte[] data = new byte[44 + (original.Length - 44) * 2]; original.AsSpan(0, 44).CopyTo(data);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 88200); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), data.Length - 44);
            for (int n = 44; n < original.Length; n++) { data[44 + (n - 44) * 2] = 128; data[45 + (n - 44) * 2] = original[n]; }
            File.WriteAllBytes(stereo, data); var result = AudioSignalAnalysisService.Analyze(stereo);
            Assert.Equal("Right", result.BestChannel); Assert.Equal(0, result.Channels[0].ValidRecords); Assert.Equal(1, result.Channels[1].ValidRecords);
            Assert.All(result.Blocks, b => Assert.Equal(2, b.Channel)); Assert.Contains(result.Issues, i => i.Code == "AUDIO_CHANNEL_MISMATCH");
            var issue = Assert.Single(result.Issues, i => i.Code == "AUDIO_LOW_LEVEL"); Assert.Equal(1, issue.Channel); Assert.Equal(0, issue.StartSample); Assert.Equal(result.FrameCount, issue.EndSample);
            Assert.All(result.Regions.Where(r => r.Record.HasValue), r => Assert.Equal(2, r.Channel));
        }
        finally { File.Delete(mono); File.Delete(stereo); }
    }

    internal static string MakeTape(TapeProfile profile)
    {
        string path = Path.Combine(Path.GetTempPath(), "audio-analysis-" + Guid.NewGuid() + ".wav");
        var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf(Enumerable.Range(0, 32).Select(n => (byte)n).ToArray(), name: "VISUAL"));
        record.Profile = profile; SharpTapeExporter.Export(path, [record], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800); return path;
    }
    private static byte[] Silence()
    {
        byte[] data = new byte[44 + 44100 * 4];
        "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8); "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 2); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 44100 * 4); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16); "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), data.Length - 44); return data;
    }
    private static SharpBlockCandidate Candidate() => new()
    {
        Kind = SharpBlockKind.Payload, Channel = 0, CopyIndex = 0, Inverted = false, PulseMode = WavPulseMode.ZeroCrossing,
        Data = [1], ChecksumValid = true, RecordedChecksum = 1, CalculatedChecksum = 1, StartSample = 0, EndSample = 1_000_000,
        LeaderAverage = 250, LeaderStdDev = 0, PulseConfidence = 1
    };
}
