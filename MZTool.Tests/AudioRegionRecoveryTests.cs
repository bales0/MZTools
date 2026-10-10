using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers.Binary;
using NAudio.SoundFile;

namespace MZTools.Tests;

public class AudioRegionRecoveryTests
{
    [Fact]
    public void DeepMeasurements_KeepSelectedSilentChannelAndExcludeOtherDetectors()
    {
        var data = new AudioRecoveryMeasurements(44100, 0, 1000,
            new() { Channel = 1, Schmitt = false, Inverted = false }, null, null);
        data.IncludeChannel(0); data.IncludeChannel(1);
        data.Observe(0, WavPulseMode.ZeroCrossing, true, 10, 20);
        data.Observe(1, WavPulseMode.Schmitt, true, 10, 20);
        var silent = Assert.Single(data.Finish());
        Assert.Equal(2, silent.Block.Channel);
        Assert.All(silent.Block.Distributions, d => Assert.Equal(0, d.Count));
        data.Observe(1, WavPulseMode.ZeroCrossing, true, 10, 20);
        Assert.Equal(1, Assert.Single(data.Finish()).Block.Distributions.Sum(d => d.Count));
    }

    [Fact]
    public void DeepPassHistograms_UseConsumedDetectorPulsesAndKeepPassesSeparate()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            SharpTapeExporter.Export(path, [TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2, 3, 4]))], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800);
            using var reader = new WavPcmStreamReader(path);
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount,
                options: new() { ZeroCrossing = false, ThresholdScales = [.65, 1.4] });
            Assert.Equal(2, result.Passes.Count);
            foreach (var pass in result.Passes)
            {
                long count = 0; double sum = 0;
                WavHeuristicAnalyzer.VisitRangeSignal(reader, 0, reader.Format.FrameCount, new(), pass.ThresholdScale, (_, _, _) => { },
                    (ch, mode, high, duration, end) =>
                    {
                        if (mode != WavPulseMode.Schmitt || end - duration < 0) return;
                        count++; sum += duration;
                    }, default);
                Assert.Equal(2, pass.Measurements.Count);
                foreach (var measurement in pass.Measurements)
                {
                    Assert.Equal("Schmitt", measurement.Block.DecodeDetector);
                    Assert.Equal(count, measurement.Block.Distributions.Sum(d => d.Count));
                    double measuredSum = measurement.Block.Distributions.SelectMany(d => d.Histogram).Sum(b => b.DurationMicroseconds * b.Count) * reader.Format.SampleRate / 1e6;
                    Assert.Equal(sum, measuredSum, 5);
                }
            }
            Assert.NotSame(result.Passes[0].Measurements, result.Passes[1].Measurements);
            var source = AudioSignalAnalysisService.Analyze(path);
            var preview = AudioRangeInspection.Analyze(source, 0, source.FrameCount, new(), 0, WavPulseMode.ZeroCrossing, 1, false, null, waveformOnly: true);
            Assert.All(preview.Block.Distributions, d => Assert.Equal(0, d.Count));
            Assert.NotEmpty(preview.Analysis.Waveform.Buckets);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AdaptiveDeadband_IgnoresSmallAlternatingLobesWithoutChangingPulseSpacing()
    {
        var ordinary = new List<double>(); var protectedPulses = new List<double>();
        var a = new AdaptivePeakCrossing((_, duration, _, _) => ordinary.Add(duration));
        var b = new AdaptivePeakCrossing((_, duration, _, _) => protectedPulses.Add(duration), .05);
        for (int i = 0; i < 1600; i++)
        {
            int phase = i % 80;
            double value = phase < 30 ? .8 : phase is >= 40 and < 70 ? -.8 : i % 2 == 0 ? .02 : -.02;
            a.Process(i, value, .01); b.Process(i, value, .01);
        }
        a.Flush(); b.Flush();
        Assert.True(ordinary.Count > protectedPulses.Count * 2);
        Assert.InRange(protectedPulses.Count, 35, 40);
        Assert.All(protectedPulses, duration => Assert.InRange(duration, 39, 41));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { ZeroDeadband = double.NaN }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { ZeroDeadband = -.01 }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { ZeroDeadband = 1.01 }.Validate(1));
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x4D)]
    [InlineData(0xA6)]
    public void EarlyNameTermination_PreservesOriginalHeaderAcrossHeuristicAndManual(int padding)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        byte[] mzf = TapeTestData.CreateMzf([1, 2, 3, 4, 5], name: "EARLY");
        mzf[6] = 0x0D; Array.Fill(mzf, (byte)padding, 7, 11);
        try
        {
            SharpTapeExporter.Export(path, [TapeTestData.ReadRecord(mzf)], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800);
            var heuristic = WavHeuristicAnalyzer.AnalyzeFile(path);
            using var reader = new WavPcmStreamReader(path);
            var manual = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount, options: new() { ZeroCrossing = false, ThresholdScales = [1] });
            Assert.Equal(mzf[..128], Assert.Single(heuristic.Records).GetSerializedHeader());
            Assert.Equal(mzf[..128], Assert.Single(manual.Records).GetSerializedHeader());
            Assert.Equal(mzf[128..], heuristic.Records[0].Body.MzfBody); Assert.Equal(mzf[128..], manual.Records[0].Body.MzfBody);
            Assert.Empty(heuristic.Failures); Assert.Empty(manual.Failures);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectedValidChecksumHeader_IsExplainedInsteadOfReportedAsMissingSignal()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        byte[] mzf = TapeTestData.CreateMzf([1, 2, 3]); Array.Fill(mzf, (byte)'X', 1, 17);
        try
        {
            SharpTapeExporter.Export(path, [TapeTestData.ReadRecord(mzf)], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800);
            var automatic = WavHeuristicAnalyzer.AnalyzeFile(path);
            using var reader = new WavPcmStreamReader(path);
            var manual = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount, options: new() { ZeroCrossing = false, ThresholdScales = [1] });
            Assert.Empty(automatic.Records); Assert.Empty(manual.Records);
            Assert.Contains(automatic.Failures, f => f.Reason.Contains("rejected by header validation"));
            Assert.Contains(manual.Failures, f => f.Reason.Contains("rejected by header validation"));
            Assert.NotEmpty(automatic.Statistics.RejectedHeaders); Assert.NotEmpty(manual.RejectedHeaders);
            Assert.All(manual.RejectedHeaders, r => { Assert.Contains("terminator", r.Reason); Assert.Equal(r.RecordedChecksum, r.CalculatedChecksum); Assert.InRange(r.Sample, 1, reader.Format.FrameCount); Assert.Equal(64, r.HeaderSha256.Length); });
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    public void StandardImport_StreamsAllSupportedPcmDepthsWithoutChangingDecoder(int bits)
    {
        string source = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1), target = source + ".converted.wav";
        try
        {
            using var input = new WavPcmStreamReader(source);
            using (var writer = new BinaryWriter(File.Create(target)))
            {
                int bytes = bits / 8; long size = input.Format.FrameCount * bytes;
                writer.Write("RIFF"u8); writer.Write((uint)(36 + size + (size & 1))); writer.Write("WAVEfmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write(input.Format.SampleRate); writer.Write(input.Format.SampleRate * (uint)bytes); writer.Write((ushort)bytes); writer.Write((ushort)bits); writer.Write("data"u8); writer.Write((uint)size);
                input.ReadFrames((_, left, _) => { int value = left >> (24 - bits); if (bits == 8) value += 128; for (int i = 0; i < bytes; i++) writer.Write((byte)(value >> (8 * i))); });
                if ((size & 1) != 0) writer.Write((byte)0);
            }
            var expected = Assert.Single(SharpTapeImporter.ReadFile(source)); var actual = Assert.Single(SharpTapeImporter.ReadFile(target));
            Assert.Equal(expected.GetSerializedHeader(), actual.GetSerializedHeader()); Assert.Equal(expected.Body.MzfBody, actual.Body.MzfBody);
        }
        finally { File.Delete(source); File.Delete(target); }
    }

    [Fact]
    public void HeaderRejectionDiagnostics_AreBoundedWithoutLosingEventCount()
    {
        var diagnostics = new AudioHeaderDiagnostics();
        for (int i = 0; i < 1000; i++) diagnostics.Reject(new(i, 1, false, "Schmitt", "Invalid header", 3, 3, "hash"));
        Assert.Equal(1000, diagnostics.Total); Assert.Equal(128, diagnostics.Items.Count); Assert.Equal(872, diagnostics.Omitted);
    }

    [Fact]
    [Trait("Category", "OptionalAudioFixture")]
    public void RealSelection_HeuristicAndLocalManualMatchValidatedOriginalBytes()
    {
        string? path = Environment.GetEnvironmentVariable("MZTOOLS_AUDIO_RECOVERY_FIXTURE");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.True(File.Exists(path), "Configured audio fixture must exist.");
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(path));
        var expected = new[] {
            ("Exploding Fist", 37744, 0x10F0, 0x10F0, 19836, "68533B2931F667F7FCCE53F6D16606F8391B2C1F73D7242A0BBCCBF7D6951F25"),
            ("MANIC MINER 800", 36864, 0x2000, 0x2000, 16306, "B1E23A8CB5A56AA26C45F88E7309FCA78E3108E027CFBAFB1AD43E1273162F15"),
            ("S-BASIC", 27552, 0x1200, 0x7D79, 28919, "19C1F5172674D527042E9E26B7F9F32E01E27517AD6007672A9F47B8F8A3D327") };
        var result = WavHeuristicAnalyzer.AnalyzeFile(path); Assert.Equal(3, result.Records.Count); Assert.Empty(result.Failures);
        using var input = new WavPcmStreamReader(path);
        long[] bounds = [0, 276L * input.Format.SampleRate, 512L * input.Format.SampleRate, input.Format.FrameCount];
        for (int i = 0; i < expected.Length; i++)
        {
            var (name, length, load, exec, checksum, hash) = expected[i]; var r = result.Records[i];
            Assert.Equal(name, SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname)); Assert.Equal(length, r.Body.MzfBody.Length);
            Assert.Equal(load, BinaryPrimitives.ReadUInt16LittleEndian(r.RawHeader.AsSpan(20, 2))); Assert.Equal(exec, BinaryPrimitives.ReadUInt16LittleEndian(r.RawHeader.AsSpan(22, 2)));
            Assert.Equal(checksum, unchecked((ushort)r.Body.MzfBody.Sum(b => System.Numerics.BitOperations.PopCount((uint)b))));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(r.GetSerializedHeader().Concat(r.Body.MzfBody).ToArray())));
            var local = WavHeuristicAnalyzer.AnalyzeRange(path, bounds[i], bounds[i + 1], options: new() { ZeroCrossing = false, ThresholdScales = [1] });
            var decoded = Assert.Single(local.Records); Assert.Empty(local.Failures);
            Assert.Equal(r.GetSerializedHeader(), decoded.GetSerializedHeader()); Assert.Equal(r.Body.MzfBody, decoded.Body.MzfBody);
        }
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(0, 1, false, false)]
    [InlineData(1, -1, true, false)]
    [InlineData(2, 1, false, false)]
    [InlineData(0, -1, true, true)]
    public void SelectionWaveExport_WritesExactShiftedSelectedPcmAndKeepsSource(int mode, int shift, bool invert, bool mix)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"), output = path + ".selection.wav";
        short[] left = [1000, -1000, 2000, -2000, 3000, -3000, 4000, -4000], right = [11, 22, 33, 44, 55, 66, 77, 88];
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8); writer.Write(68u); writer.Write("WAVEfmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2);
                writer.Write(44100u); writer.Write(176400u); writer.Write((ushort)4); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(32u);
                for (int i = 0; i < left.Length; i++) { writer.Write(left[i]); writer.Write(right[i]); }
            }
            var file = new FileInfo(path); byte[] original = File.ReadAllBytes(path);
            var source = new AudioSignalAnalysis { Source = path, SourceLength = file.Length, SourceModifiedUtc = file.LastWriteTimeUtc, SampleRate = 44100, BitsPerSample = 16, ChannelCount = 2, FrameCount = 8 };
            var result = AudioSelectionWaveExporter.Export(source, output, 2, 7, new(shift, invert, mix), mode);
            Assert.Equal(2, result.StartSample); Assert.Equal(7, result.EndSample); Assert.Equal(mode == 2 && !mix ? 2 : 1, result.Channels);
            using var exported = new WavPcmStreamReader(output);
            Assert.Equal(16, exported.Format.BitsPerSample); Assert.Equal(16, result.BitsPerSample); Assert.Equal(44100u, exported.Format.SampleRate); Assert.Equal(5, exported.Format.FrameCount);
            exported.ReadFrames((sample, l, r) =>
            {
                int index = (int)sample + 2, expectedLeft = left[index] * 256, expectedRight = right[index - shift] * 256 * (invert ? -1 : 1);
                if (mix) expectedLeft = expectedRight = (expectedLeft + expectedRight) / 2;
                expectedLeft = (int)Math.Round(expectedLeft / 256.0, MidpointRounding.AwayFromZero) * 256;
                expectedRight = (int)Math.Round(expectedRight / 256.0, MidpointRounding.AwayFromZero) * 256;
                Assert.Equal(mode == 1 && !mix ? expectedRight : expectedLeft, l);
                if (result.Channels == 2) Assert.Equal(expectedRight, r);
            });
            long bytes = 5 * result.Channels * 2;
            Assert.Equal(44 + bytes + (bytes & 1), new FileInfo(output).Length);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Throws<ArgumentException>(() => AudioSelectionWaveExporter.Export(source, path, 2, 7, new(), 0));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Theory]
    [InlineData(8, 22050, false)]
    [InlineData(16, 48000, false)]
    [InlineData(24, 96000, false)]
    [InlineData(8, 44100, true)]
    [InlineData(16, 48000, true)]
    [InlineData(24, 96000, true)]
    public void SelectionWaveExport_PreservesSourceResolutionAndExactUntransformedSamples(int bits, int rate, bool flac)
    {
        string wav = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"), path = flac ? wav + ".flac" : wav, output = wav + ".selection.wav";
        int limit = 1 << (bits - 1), bytesPerSample = bits / 8;
        int[] samples = [-limit, -1, 0, 1, limit - 1];
        try
        {
            using (var writer = new BinaryWriter(File.Create(wav)))
            {
                int dataSize = samples.Length * bytesPerSample;
                writer.Write("RIFF"u8); writer.Write((uint)(36 + dataSize + (dataSize & 1))); writer.Write("WAVEfmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write((uint)rate); writer.Write((uint)(rate * bytesPerSample)); writer.Write((ushort)bytesPerSample); writer.Write((ushort)bits); writer.Write("data"u8); writer.Write((uint)dataSize);
                foreach (int sample in samples)
                    for (int b = 0; b < bytesPerSample; b++) writer.Write((byte)((bits == 8 ? sample + 128 : sample) >> (8 * b)));
                if ((dataSize & 1) != 0) writer.Write((byte)0);
            }
            if (flac) { using var input = new SoundFileReader(wav); SoundFileWriter.CreateSoundFile(path, input); }
            using var inputReader = AudioSignalAnalysisService.OpenReader(path);
            var info = new FileInfo(path); byte[] original = File.ReadAllBytes(path);
            var source = new AudioSignalAnalysis { Source = path, SourceLength = info.Length, SourceModifiedUtc = info.LastWriteTimeUtc, SampleRate = inputReader.Format.SampleRate,
                BitsPerSample = inputReader.Format.BitsPerSample, ChannelCount = 1, FrameCount = inputReader.Format.FrameCount };
            var result = AudioSelectionWaveExporter.Export(source, output, 0, source.FrameCount, new(), 0);
            using var exported = new WavPcmStreamReader(output);
            Assert.Equal(source.BitsPerSample, exported.Format.BitsPerSample); Assert.Equal(source.BitsPerSample, result.BitsPerSample); Assert.Equal((uint)rate, exported.Format.SampleRate);
            var before = new List<int>(); var after = new List<int>();
            inputReader.ReadFrames((_, l, _) => before.Add(l)); exported.ReadFrames((_, l, _) => after.Add(l));
            Assert.Equal(before, after); Assert.Equal(original, File.ReadAllBytes(path));
            long size = source.FrameCount * (source.BitsPerSample / 8);
            Assert.Equal(44 + size + (size & 1), new FileInfo(output).Length);
        }
        finally { File.Delete(output); if (flac) File.Delete(path); File.Delete(wav); }
    }

    [Fact]
    public void SelectionWaveExport_CancellationPreservesDestinationAndCleansTemporaryFile()
    {
        string path = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1);
        string folder = Path.Combine(Path.GetTempPath(), "mztools-selection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, "selection.wav");
        try
        {
            var source = AudioSignalAnalysisService.Analyze(path); byte[] original = File.ReadAllBytes(path), marker = [1, 2, 3]; File.WriteAllBytes(output, marker);
            using var cancellation = new CancellationTokenSource();
            var progress = new ImmediateProgress<WavAnalysisProgress>(p => { if (p.Fraction > 0) cancellation.Cancel(); });
            Assert.Throws<OperationCanceledException>(() => AudioSelectionWaveExporter.Export(source, output, 0, source.FrameCount, new(), 0, progress, cancellation.Token));
            Assert.Equal(marker, File.ReadAllBytes(output)); Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(folder));
            var result = AudioSelectionWaveExporter.Export(source, output, 0, source.FrameCount, new(), 0);
            using var exported = new WavPcmStreamReader(output);
            Assert.Equal(source.FrameCount, exported.Format.FrameCount); Assert.Equal(source.FrameCount, result.EndSample);
            Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), Assert.Single(WavHeuristicAnalyzer.AnalyzeFile(output).Records).Body.MzfBody);
        }
        finally { File.Delete(path); File.Delete(output); Directory.Delete(folder); }
    }
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    [Fact]
    public void AdaptivePeakMidpoint_InterpolatesOffsetSignalWithoutCreatingSilenceEdges()
    {
        var intervals = new List<(bool High, double Duration, long End)>();
        var detector = new AdaptivePeakCrossing((h, d, e, _) => intervals.Add((h, d, e)));
        for (int i = 0; i < 420; i++) detector.Process(i, .2 + .6 * Math.Sin(2 * Math.PI * i / 21), .05);
        detector.Flush();
        Assert.InRange(intervals.Count, 35, 40);
        Assert.All(intervals.Skip(2), pulse => Assert.InRange(pulse.Duration, 10.25, 10.75));
        Assert.Contains(intervals, pulse => pulse.Duration != Math.Floor(pulse.Duration));
        Assert.True(intervals.Zip(intervals.Skip(1)).All(p => p.First.High != p.Second.High && p.First.End < p.Second.End));
        int before = intervals.Count;
        for (int i = 420; i < 10000; i++) detector.Process(i, 0, .05);
        detector.Flush(); Assert.InRange(intervals.Count - before, 0, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferencePasses_IncludeCatalogAndUserProfilesAndRecoverWithSelectedDetector(bool peakDetector)
    {
        string path = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1);
        try
        {
            byte[] original = File.ReadAllBytes(path);
            using var reader = AudioSignalAnalysisService.OpenReader(path);
            var user = AudioPulseReference.User("My ROM", 238, 259, 469, 487);
            var refs = AudioPulseReferences.BuiltIn.Append(user).ToArray();
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount, options: new AudioRecoveryOptions {
                Adaptive = false, ReferenceTests = true, References = refs, HalfSample = true, ReferenceTolerance = .2,
                ZeroCrossing = !peakDetector, Schmitt = false, AdaptiveZeroCrossing = peakDetector, Inverted = false, ThresholdScales = [1] });
            Assert.Equal(refs.Length + 1, result.Passes.Count);
            Assert.Equal("Reference header discovery", result.Passes[0].Test);
            Assert.Equal(refs.Select(r => r.Name), result.Passes.Skip(1).Select(p => p.Reference));
            Assert.All(result.Passes.Skip(1), p => { Assert.True(p.HalfSample); Assert.Equal(.2, p.ReferenceTolerance); Assert.NotEmpty(p.ReferenceSource!); Assert.NotEmpty(p.ReferenceEvidence!); });
            Assert.Contains(result.Passes, p => p.Reference == "My ROM" && p.ValidHeaders > 0 && p.ValidPayloads > 0);
            Assert.Equal(Enumerable.Range(0, 32).Select(n => (byte)n), Assert.Single(result.Records).Body.MzfBody);
            Assert.All(result.Candidates, c => { Assert.Equal(peakDetector ? WavPulseMode.AdaptiveZeroCrossing : WavPulseMode.ZeroCrossing, c.PulseMode); Assert.False(c.Inverted); });
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(1, 1, 3, 2, 10)]
    [InlineData(-1, 0, 3, 1, 20)]
    [InlineData(0, 0, 4, 1, 10)]
    public void Alignment_PreservesLeftCoordinatesAndUsesOnlyCommonSamples(int shift, long first, int count, int firstLeft, int firstRight)
    {
        using var reader = new MemoryPcm([1, 2, 3, 4], [10, 20, 30, 40]);
        var output = new List<(long Sample, int L, int R)>();
        new AudioPcmTransform(shift).Visit(reader, 0, 4, (s, l, r) => output.Add((s, l, r)), default);
        Assert.Equal(count, output.Count); Assert.Equal((first, firstLeft, firstRight), output[0]);
        Assert.Equal(first + count - 1, output[^1].Sample);
    }

    [Theory]
    [InlineData((int)TapeProfile.Tc1_2, "TC 1:2 compatibility waveform")]
    [InlineData((int)TapeProfile.Ic1_3, "Intercopy writer 2800 Bd (7:3)")]
    public void ReferenceOnly_FindsRomHeadersWithFastCopierPayloads(int profile, string referenceName)
    {
        string path = AudioSignalAnalysisTests.MakeTape((TapeProfile)profile);
        try
        {
            using var reader = AudioSignalAnalysisService.OpenReader(path);
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount, options: new AudioRecoveryOptions {
                Adaptive = false, ReferenceTests = true, References = [AudioPulseReferences.BuiltIn.Single(r => r.Name == referenceName)],
                Schmitt = false, Inverted = false, HalfSample = true, ReferenceTolerance = .2 });
            Assert.Equal(0, result.Passes[0].ValidPayloads);
            Assert.True(result.Passes[0].ValidHeaders > 0);
            Assert.True(result.Passes[1].ValidPayloads > 0);
            Assert.Equal(Enumerable.Range(0, 32).Select(n => (byte)n), Assert.Single(result.Records).Body.MzfBody);
            Assert.Contains(referenceName, result.Recoveries[0].Payload.Candidate.RecoveryTest);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReferenceTests_RejectMissingGroupsUnboundedSweepsAndInvalidTolerance()
    {
        var valid = AudioPulseReference.User("test", 100, 100, 200, 200);
        var options = new AudioRecoveryOptions { Adaptive = false, ReferenceTests = true, References = [valid] };
        options.Validate(1);
        Assert.Throws<ArgumentException>(() => (options with { References = [] }).Validate(1));
        Assert.Throws<ArgumentException>(() => (options with { References = Enumerable.Repeat(valid, 65).ToArray() }).Validate(1));
        Assert.Throws<ArgumentException>(() => (options with { ReferenceTolerance = double.NaN }).Validate(1));
        Assert.Throws<ArgumentException>(() => (options with { ReferenceTolerance = .009 }).Validate(1));
        Assert.Throws<ArgumentException>(() => (options with { References = [valid with { Guides = [valid.Guides[0]] }] }).Validate(1));
        Assert.Throws<ArgumentException>(() => (options with { ZeroCrossing = false, Schmitt = false }).Validate(1));
    }

    [Fact]
    public void Alignment_MixesPcmBeforeExtremaAndValidatesLimitsAndCancellation()
    {
        using var reader = new MemoryPcm([100, -100, 700], [-100, 100, -700]);
        var output = new List<int>();
        new AudioPcmTransform(Mix: true).Visit(reader, 0, 3, (_, l, r) => { Assert.Equal(l, r); output.Add(l); }, default);
        Assert.All(output, value => Assert.Equal(0, value));
        output.Clear(); new AudioPcmTransform(InvertRight: true, Mix: true).Visit(reader, 0, 3, (_, l, _) => output.Add(l), default);
        Assert.Equal(new[] { 100, -100, 700 }, output);
        using var saturated = new MemoryPcm([0], [-8388608]);
        new AudioPcmTransform(InvertRight: true).Visit(saturated, 0, 1, (_, _, r) => Assert.Equal(8388607, r), default);
        Assert.Throws<ArgumentException>(() => new AudioPcmTransform(88201).Bounds(reader.Format, 0, 3));
        Assert.Throws<ArgumentException>(() => new AudioPcmTransform(3).Bounds(reader.Format, 0, 3));
        Assert.Throws<ArgumentException>(() => new AudioPcmTransform(Mix: true).Validate(reader.Format with { Channels = 1 }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new AudioPcmTransform().Visit(reader, 0, 3, (_, _, _) => { }, cancelled.Token));
    }

    [Fact]
    public void AlignedInvertedStereoMix_RecoversOriginalBytesAndMatchesMonoHistogramAndEnvelope()
    {
        string mono = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1), stereo = mono + ".aligned.wav";
        const int delay = 19;
        try
        {
            byte[] original = File.ReadAllBytes(mono), data = new byte[44 + (original.Length - 44) * 2];
            original.AsSpan(0, 44).CopyTo(data);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 88200); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), data.Length - 44);
            for (int i = 44; i < original.Length; i++) { data[44 + (i - 44) * 2] = original[i]; data[45 + (i - 44) * 2] = i - 44 < delay ? (byte)128 : (byte)(256 - original[i - delay]); }
            File.WriteAllBytes(stereo, data); byte[] hash = SHA256.HashData(data);
            var source = AudioSignalAnalysisService.Analyze(stereo); var monoSource = AudioSignalAnalysisService.Analyze(mono);
            var transform = new AudioPcmTransform(-delay, true, true);
            var recovered = WavHeuristicAnalyzer.AnalyzeRange(stereo, 0, source.FrameCount, options: new AudioRecoveryOptions { Transform = transform, ThresholdScales = [1] });
            Assert.Equal(Enumerable.Range(0, 32).Select(n => (byte)n), Assert.Single(recovered.Records).Body.MzfBody);
            Assert.Equal(source.FrameCount - delay, recovered.EndSample);
            Assert.All(recovered.Passes, p => { Assert.Equal("Mix", p.Channels); Assert.Contains("-19 samples", p.AudioTransform); });
            var preview = AudioRangeInspection.Analyze(source, 0, source.FrameCount, transform, 0, WavPulseMode.ZeroCrossing, 1, false, null);
            var baseline = AudioRangeInspection.Analyze(monoSource, 0, source.FrameCount - delay, new(), 0, WavPulseMode.ZeroCrossing, 1, false, null);
            for (int i = 0; i < 4; i++) Assert.Equal(baseline.Block.Distributions[i].Histogram, preview.Block.Distributions[i].Histogram);
            Assert.Equal(baseline.Analysis.Waveform.Buckets, preview.Analysis.Waveform.Buckets);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(stereo)));
        }
        finally { File.Delete(mono); File.Delete(stereo); }
    }

    [Fact]
    public void RangeHistogram_ExistsWithoutAProgramAndLocalizesClippingInsideTheRequestedInterval()
    {
        string path = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1);
        try
        {
            byte[] data = File.ReadAllBytes(path); Array.Fill(data, (byte)128, 44, 600); Array.Fill(data, (byte)255, 44 + 30, 4); File.WriteAllBytes(path, data);
            var file = new FileInfo(path);
            var source = new AudioSignalAnalysis { Source = path, SourceLength = file.Length, SourceModifiedUtc = file.LastWriteTimeUtc,
                SampleRate = 44100, BitsPerSample = 8, ChannelCount = 1, FrameCount = data.Length - 44, Format = "WAV" };
            var preview = AudioRangeInspection.Analyze(source, 20, 200, new(), 0, WavPulseMode.ZeroCrossing, 1, false, null);
            Assert.Equal(20, preview.Start); Assert.Equal(200, preview.End);
            Assert.True(preview.Block.Distributions.Sum(d => d.Count) > 0);
            var clipping = Assert.Single(preview.Analysis.Issues, i => i.Code == "AUDIO_CLIPPING");
            Assert.Equal(30, clipping.StartSample); Assert.Equal(34, clipping.EndSample);
            Assert.All(preview.Analysis.Issues, i => Assert.True(i.StartSample >= 20 && i.EndSample <= 200));
            Assert.Empty(WavHeuristicAnalyzer.AnalyzeRange(path, 20, 200).Records);
            Assert.All(preview.Analysis.Regions, r => Assert.Null(r.Record));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => AudioRangeInspection.Analyze(source, 20, 200, new(), 0, WavPulseMode.Schmitt, 1, false, null, cancelled.Token));
            File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc.AddSeconds(2));
            Assert.Throws<IOException>(() => AudioRangeInspection.Analyze(source, 20, 200, new(), 0, WavPulseMode.ZeroCrossing, 1, false, null));
        }
        finally { File.Delete(path); }
    }

    private sealed class MemoryPcm(int[] left, int[] right) : IPcmAudioStreamReader
    {
        public PcmAudioFormat Format => new(2, 44100, 24, 6, 0, left.Length * 6);
        public string SourceFormat => "Test PCM";
        public void ReadFrames(Action<long, int, int> consume) => ReadFrames(0, left.Length, consume);
        public void ReadFrames(long start, long count, Action<long, int, int> consume) { for (long i = start; i < start + count; i++) consume(i, left[(int)i], right[(int)i]); }
        public void Dispose() { }
    }

    [Fact]
    public void ManualTests_SelectDetectorsPolarityAndTimingAndRetainProvenance()
    {
        string path = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1);
        try
        {
            using var reader = AudioSignalAnalysisService.OpenReader(path);
            var options = new AudioRecoveryOptions { Adaptive = false, FixedTiming = true, Fuzzy = true, Schmitt = false,
                Inverted = false, Channel = 0, ShortMicroseconds = 238, TimeScales = [.5, 1, 2] };
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount, options: options);
            Assert.Single(result.Records); Assert.Equal(4, result.Passes.Count);
            Assert.All(result.Candidates, c => { Assert.Equal(WavPulseMode.ZeroCrossing, c.PulseMode); Assert.False(c.Inverted); Assert.Equal(0, c.Channel); Assert.NotEmpty(c.RecoveryTest); });
            Assert.Contains(result.Passes, p => p.Test == "Fixed time scale" && p.TimeScale == 1 && p.ValidPayloads > 0);
            Assert.Contains(result.Passes, p => p.Test == "Fuzzy pulse length" && p.FuzzyTolerance == .35 && p.ValidPayloads > 0);
            Assert.DoesNotContain(result.Passes, p => p.Test == "Adaptive");
            var schmitt = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount,
                options: new AudioRecoveryOptions { ZeroCrossing = false, ThresholdScales = [1], Inverted = false });
            Assert.Single(schmitt.Records); Assert.Single(schmitt.Passes);
            Assert.All(schmitt.Candidates, c => Assert.Equal(WavPulseMode.Schmitt, c.PulseMode));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ManualTests_RejectEmptyChoicesInvalidNumbersAndUnboundedSweeps()
    {
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { ZeroCrossing = false, Schmitt = false }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { Normal = false, Inverted = false }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { Adaptive = false }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { Channel = 1 }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { ThresholdScales = [double.NaN] }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { FixedTiming = true, TimeScales = Enumerable.Repeat(1.0, 9).ToArray() }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { FixedTiming = true, ShortMicroseconds = double.PositiveInfinity }.Validate(1));
        Assert.Throws<ArgumentException>(() => new AudioRecoveryOptions { Fuzzy = true, FuzzyTolerance = .61 }.Validate(1));
    }

    [Fact]
    public void RangeRecovery_FlacMatchesWavAndStereoCanRecoverInvertedRightChannel()
    {
        string path = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1), flac = path + ".flac", stereo = path + ".stereo.wav";
        try
        {
            using (var source = new SoundFileReader(path)) SoundFileWriter.CreateSoundFile(flac, source);
            using var reader = AudioSignalAnalysisService.OpenReader(path);
            var a = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount);
            var b = WavHeuristicAnalyzer.AnalyzeRange(flac, 0, reader.Format.FrameCount);
            Assert.Equal(Assert.Single(a.Records).GetSerializedHeader(), Assert.Single(b.Records).GetSerializedHeader());
            Assert.Equal(a.Records[0].Body.MzfBody, b.Records[0].Body.MzfBody);
            byte[] original = File.ReadAllBytes(path), data = new byte[44 + (original.Length - 44) * 2];
            original.AsSpan(0, 44).CopyTo(data);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 88200); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), data.Length - 44);
            for (int i = 44; i < original.Length; i++) { data[44 + (i - 44) * 2] = 128; data[45 + (i - 44) * 2] = (byte)(255 - original[i]); }
            File.WriteAllBytes(stereo, data);
            var c = WavHeuristicAnalyzer.AnalyzeRange(stereo, 0, reader.Format.FrameCount);
            Assert.Equal(a.Records[0].Body.MzfBody, Assert.Single(c.Records).Body.MzfBody);
            Assert.All(c.Recoveries, r => { Assert.Equal(1, r.Header.Candidate.Channel); Assert.Equal(1, r.Payload.Candidate.Channel); Assert.True(r.Header.Candidate.Inverted); });
        }
        finally { File.Delete(path); File.Delete(flac); File.Delete(stereo); }
    }

    [Theory]
    [InlineData((int)TapeProfile.Normal1_1)]
    [InlineData((int)TapeProfile.Tc1_2)]
    [InlineData((int)TapeProfile.Ic1_3)]
    public void RangeRecovery_ReusesChecksumsAndKeepsSourceBytes(int profile)
    {
        string path = AudioSignalAnalysisTests.MakeTape((TapeProfile)profile);
        try
        {
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            using var reader = AudioSignalAnalysisService.OpenReader(path);
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount);
            var record = Assert.Single(result.Records);
            Assert.Equal("VISUAL", SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname));
            Assert.Equal(Enumerable.Range(0, 32).Select(n => (byte)n), record.Body.MzfBody);
            Assert.Equal(4, result.Passes.Count); Assert.Single(result.Recoveries);
            Assert.All(result.Candidates, c => Assert.True(c.StartSample >= 0 && c.EndSample <= reader.Format.FrameCount));
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PayloadOnlyRange_RequiresNoInventedHeaderAndCanRecoverRawCandidate()
    {
        string path = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1);
        try
        {
            var analysis = WavHeuristicAnalyzer.AnalyzeFile(path);
            var payload = analysis.Statistics.RecordRecoveries[0].Payload.Candidate;
            long start = payload.SignalTrace!.LeaderStart, end = payload.EndSample;
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, start, end, expectedLength: 32);
            Assert.Empty(result.Records);
            Assert.Contains(result.Candidates, c => c.Kind == SharpBlockKind.Payload && c.ChecksumValid && c.Data.SequenceEqual(analysis.Records[0].Body.MzfBody));
            Assert.All(result.Candidates, c => Assert.True(c.StartSample >= start && c.EndSample <= end));
            Assert.All(result.Failures, f => Assert.True(f.StartSample >= start && f.EndSample <= end));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RangeRecovery_ValidatesBoundsLengthAndHonorsCancellation()
    {
        string path = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1);
        try
        {
            using var reader = AudioSignalAnalysisService.OpenReader(path); long frames = reader.Format.FrameCount;
            Assert.Throws<ArgumentOutOfRangeException>(() => WavHeuristicAnalyzer.AnalyzeRange(path, -1, frames));
            Assert.Throws<ArgumentOutOfRangeException>(() => WavHeuristicAnalyzer.AnalyzeRange(path, 3, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => WavHeuristicAnalyzer.AnalyzeRange(path, 0, frames + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => WavHeuristicAnalyzer.AnalyzeRange(path, 0, frames, 65536));
            using var cancellation = new CancellationTokenSource();
            var progress = new ImmediateProgress(_ => cancellation.Cancel());
            Assert.Throws<OperationCanceledException>(() => WavHeuristicAnalyzer.AnalyzeRange(path, 0, frames, progress: progress, token: cancellation.Token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ComparisonReferences_KeepRomContextsSeparateFromCopierAndCompatibilityValues()
    {
        var rom = AudioPulseReferences.BuiltIn.Single(r => r.Name == "MZ-800 ROM 1Z-013B");
        Assert.Contains(rom.Guides, g => g.Group == "SHORT High" && g.Microseconds == 237.956);
        Assert.Equal(new[] { 258.819, 256, 252.617, 255.436 }, rom.Guides.Where(g => g.Group == "SHORT Low").Select(g => g.Microseconds));
        var pal = AudioPulseReferences.BuiltIn.Single(r => r.Name.Contains("013A PAL"));
        Assert.Contains(pal.Guides, g => g.Microseconds == 242.749);
        var writer = AudioPulseReferences.BuiltIn.Single(r => r.Name.Contains("writer 3200"));
        Assert.Equal("COPIER-WRITER-EXACT; project alias 1:4", writer.Evidence);
        Assert.Equal(new[] { 76.969, 117.286, 157.604, 179.595 }, writer.Guides.Select(g => g.Microseconds));
        var timer = AudioPulseReferences.BuiltIn.Single(r => r.Name == "TurboCopy 1.22 timer OUT0 1:2");
        Assert.Contains("not exact cassette WRITE edges", timer.Evidence);
        Assert.Equal(new[] { 141.818, 140.909, 282.727, 282.727 }, timer.Guides.Select(g => g.Microseconds));
        Assert.All(AudioPulseReferences.BuiltIn, r =>
        {
            Assert.StartsWith("specification/CMT_", r.Source);
            Assert.All(r.Guides, g => Assert.True(double.IsFinite(g.Microseconds) && g.Microseconds > 0));
            Assert.Equal(4, r.Guides.Select(g => g.Group).Distinct().Count());
        });
    }

    [Fact]
    public void UserReference_ValidatesValuesAndExportsProvenance()
    {
        Assert.Throws<ArgumentException>(() => AudioPulseReference.User("", 1, 2, 3, 4));
        Assert.Throws<ArgumentException>(() => AudioPulseReference.User("Invalid", 0, 2, 3, 4));
        Assert.Throws<ArgumentException>(() => AudioPulseReference.User("Invalid", double.NaN, 2, 3, 4));
        Assert.Throws<ArgumentException>(() => AudioPulseReference.User("Invalid", double.PositiveInfinity, 2, 3, 4));
        var custom = AudioPulseReference.User("Measured target", 240.5, 260, 480, 500);
        var model = new AudioSignalAnalysis { ComparisonReference = custom, ShowComparisonReference = true, VisiblePulseGroups = ["SHORT High", "LONG High"] };
        using var json = JsonDocument.Parse(AudioAnalysisReport.Export(model, "json"));
        Assert.Equal(240.5, json.RootElement.GetProperty("ComparisonReference").GetProperty("Guides")[0].GetProperty("Microseconds").GetDouble());
        foreach (string format in new[] { "csv", "txt" })
        {
            string report = AudioAnalysisReport.Export(model, format);
            Assert.Contains(custom.Source, report); Assert.Contains(custom.Evidence, report); Assert.Contains("240.5", report);
        }
    }

    private sealed class ImmediateProgress(Action<WavAnalysisProgress> callback) : IProgress<WavAnalysisProgress>
    { public void Report(WavAnalysisProgress value) => callback(value); }
}
