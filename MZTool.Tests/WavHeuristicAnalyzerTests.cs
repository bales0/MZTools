namespace MZTools.Tests;

using System.Buffers.Binary;
using NAudio.SoundFile;

public class WavHeuristicAnalyzerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgramAssembly_AcceptsCompletePairWithOnePolarity(bool inverted)
    {
        byte[] mzf = TapeTestData.CreateMzf([1, 2, 3], name: inverted ? "INVERTED" : "NORMAL");
        WavAssemblyResult result = WavHeuristicAnalyzer.AssembleCandidatesForTest(
            [
                Candidate(SharpBlockKind.Header, mzf[..128], inverted, true, 100, 200),
                Candidate(SharpBlockKind.Payload, mzf[128..], inverted, true, 300, 400)
            ],
            1000,
            1000);

        Assert.Single(result.Records);
        Assert.Empty(result.Failures);
        WavRecoveryInfo recovery = Assert.Single(result.Recoveries);
        Assert.Equal(inverted, recovery.Header.Candidate.Inverted);
        Assert.Equal(inverted, recovery.Payload.Candidate.Inverted);
    }

    [Fact]
    public void ProgramAssembly_CompleteValidPairBeatsBetterHeaderTimingOfWrongPolarity()
    {
        byte[] mzf = TapeTestData.CreateMzf([0x10, 0x20, 0x30], name: "RIGHT");
        WavAssemblyResult result = WavHeuristicAnalyzer.AssembleCandidatesForTest(
            [
                Candidate(SharpBlockKind.Header, mzf[..128], false, true, 100, 200, timingError: 0.05),
                Candidate(SharpBlockKind.Header, mzf[..128], true, true, 101, 201, timingError: 0.001),
                Candidate(SharpBlockKind.Payload, mzf[128..], false, true, 300, 400),
                Candidate(SharpBlockKind.Payload, [0xFF, 0xEE, 0xDD], true, false, 300, 400)
            ],
            1000,
            1000);

        Assert.Single(result.Records);
        Assert.Empty(result.Failures);
        WavRecoveryInfo recovery = Assert.Single(result.Recoveries);
        Assert.False(recovery.Header.Candidate.Inverted);
        Assert.False(recovery.Payload.Candidate.Inverted);
    }

    [Fact]
    public void ProgramAssembly_SelectsPolarityIndependentlyForEveryProgram()
    {
        var candidates = new List<SharpBlockCandidate>();
        bool[] polarities = [false, true, false];
        for (int index = 0; index < polarities.Length; index++)
        {
            byte[] mzf = TapeTestData.CreateMzf([(byte)(index + 1)], name: $"PROGRAM{index + 1}");
            long offset = index * 10_000;
            candidates.Add(Candidate(SharpBlockKind.Header, mzf[..128], polarities[index], true, offset + 100, offset + 200));
            candidates.Add(Candidate(SharpBlockKind.Payload, mzf[128..], polarities[index], true, offset + 300, offset + 400));
        }

        WavAssemblyResult result = WavHeuristicAnalyzer.AssembleCandidatesForTest(candidates, 1000, 40_000);

        Assert.Equal(3, result.Records.Count);
        Assert.Equal(polarities, result.Recoveries.Select(value => value.Header.Candidate.Inverted));
        Assert.All(result.Recoveries, value =>
            Assert.Equal(value.Header.Candidate.Inverted, value.Payload.Candidate.Inverted));
    }

    [Fact]
    public void ProgramAssembly_DoesNotPairHeaderAndPayloadWithDifferentPolarity()
    {
        byte[] mzf = TapeTestData.CreateMzf([1, 2, 3]);
        WavAssemblyResult result = WavHeuristicAnalyzer.AssembleCandidatesForTest(
            [
                Candidate(SharpBlockKind.Header, mzf[..128], false, true, 100, 200),
                Candidate(SharpBlockKind.Payload, mzf[128..], true, true, 300, 400)
            ],
            1000,
            1000);

        Assert.Empty(result.Records);
        WavAnalysisFailure failure = Assert.Single(result.Failures);
        Assert.True(failure.HeaderFound);
        Assert.False(failure.PayloadCandidatesFound);
    }

    [Fact]
    public void ProgramAssembly_AllowsHeaderAndPayloadFromDifferentStereoChannels()
    {
        byte[] mzf = TapeTestData.CreateMzf([1, 2, 3]);
        WavAssemblyResult result = WavHeuristicAnalyzer.AssembleCandidatesForTest(
            [
                Candidate(SharpBlockKind.Header, mzf[..128], false, true, 100, 200, channel: 0),
                Candidate(SharpBlockKind.Payload, mzf[128..], false, true, 300, 400, channel: 1)
            ],
            1000,
            1000);

        Assert.Single(result.Records);
        Assert.Equal(0, result.Recoveries[0].Header.Candidate.Channel);
        Assert.Equal(1, result.Recoveries[0].Payload.Candidate.Channel);
    }

    [Fact]
    public void ProgramAssembly_PreservesValidProgramsAroundFailedProgram()
    {
        var candidates = new List<SharpBlockCandidate>();
        for (int index = 0; index < 3; index++)
        {
            byte[] mzf = TapeTestData.CreateMzf([(byte)(index + 1)], name: $"PARTIAL{index + 1}");
            long offset = index * 10_000;
            candidates.Add(Candidate(SharpBlockKind.Header, mzf[..128], false, true, offset + 100, offset + 200));
            candidates.Add(Candidate(
                SharpBlockKind.Payload,
                index == 1 ? [0xFF] : mzf[128..],
                false,
                index != 1,
                offset + 300,
                offset + 400));
        }

        WavAssemblyResult result = WavHeuristicAnalyzer.AssembleCandidatesForTest(candidates, 1000, 40_000);

        Assert.Equal(2, result.Records.Count);
        WavAnalysisFailure failure = Assert.Single(result.Failures);
        Assert.Equal(new byte[] { 1, 3 }, result.Records.Select(value => value.Body.MzfBody[0]));
        (long Start, long End) interval = Assert.Single(
            WavHeuristicAnalyzer.GetSelectiveRecoveryIntervals(result.Failures, 40_000));
        Assert.Equal(failure.StartSample, interval.Start);
        Assert.Equal(failure.EndSample, interval.End);
        Assert.DoesNotContain(result.Recoveries, value =>
            value.Header.Candidate.StartSample >= interval.Start &&
            value.Header.Candidate.StartSample < interval.End);
    }

    [Theory]
    [InlineData(333.240634920635, "0:05:33,2")]
    [InlineData(3661.96, "1:01:02,0")]
    public void Statistics_FormatsDurationToTenths(
        double durationSeconds,
        string expected)
    {
        Assert.Equal(expected, WavAnalysisStatisticsWindow.FormatDuration(durationSeconds));
    }

    [Fact]
    public void BatchReport_AggregatesFilesIntoOneSummaryModel()
    {
        TapeRecord record = CreateRecord(TapeProfile.Normal1_1, "BATCH");
        AudioFileImportResult[] results =
        [
            new AudioFileImportResult
            {
                SourceFile = @"C:\audio\first.wav",
                Records = [record]
            },
            new AudioFileImportResult
            {
                SourceFile = @"C:\audio\second.flac",
                Records = [],
                Error = new InvalidDataException("decode failed")
            }
        ];

        string report = WavAnalysisStatisticsWindow.BuildBatchText(results);

        Assert.Contains("Files: 2", report);
        Assert.Contains("Imported: 1", report);
        Assert.Contains("Failed: 1", report);
        Assert.Contains("first.wav", report);
        Assert.Contains("second.flac", report);
        Assert.Contains("decode failed", report);
        Assert.Contains("BATCH", report);
    }

    [Theory]
    [InlineData(1, 8, 22050)]
    [InlineData(1, 8, 44100)]
    [InlineData(1, 16, 44100)]
    [InlineData(1, 16, 88200)]
    [InlineData(1, 24, 96000)]
    [InlineData(2, 16, 44100)]
    [InlineData(2, 24, 96000)]
    public void StreamingReader_ReadsSupportedPcmFormats(
        ushort channels,
        ushort bits,
        uint sampleRate)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, CreatePcmWav(channels, bits, sampleRate, 32));
            using var reader = new WavPcmStreamReader(path);
            long frames = 0;
            reader.ReadFrames((_, _, _) => frames++);

            Assert.Equal(channels, reader.Format.Channels);
            Assert.Equal(bits, reader.Format.BitsPerSample);
            Assert.Equal(sampleRate, reader.Format.SampleRate);
            Assert.Equal(32, frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(12, 44100, "bit depth")]
    [InlineData(16, 32000, "sample rate")]
    public void StreamingReader_RejectsUnsupportedPcm(
        ushort bits,
        uint sampleRate,
        string expectedMessage)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, CreatePcmWav(1, bits, sampleRate, 8));
            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new WavPcmStreamReader(path));
            Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(15)]
    public void HeuristicImporter_RoundTripsSupportedStaticProfiles(int profileValue)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        TapeRecord expected = CreateRecord((TapeProfile)profileValue);
        try
        {
            SharpTapeExporter.Export(
                path,
                [expected],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);

            TapeRecord actual = Assert.Single(WavHeuristicAnalyzer.ReadFile(path));

            Assert.Equal(expected.Body.MzfBody, actual.Body.MzfBody);
            Assert.Equal(expected.Profile, actual.Profile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HeuristicImporter_AcceptsInvertedOffsetStereo()
    {
        string monoPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-mono.wav");
        string stereoPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-stereo.wav");
        TapeRecord expected = CreateRecord(TapeProfile.Normal1_1);
        try
        {
            SharpTapeExporter.Export(
                monoPath,
                [expected],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);
            byte[] mono = File.ReadAllBytes(monoPath);
            File.WriteAllBytes(stereoPath, Convert8BitMonoToOffsetStereo(mono));

            TapeRecord actual = Assert.Single(WavHeuristicAnalyzer.ReadFile(stereoPath));

            Assert.Equal(expected.Body.MzfBody, actual.Body.MzfBody);
        }
        finally
        {
            File.Delete(monoPath);
            File.Delete(stereoPath);
        }
    }

    [Fact]
    public void HeuristicStatistics_ReportsInvertedSelectedSignal()
    {
        string normalPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-normal.wav");
        string invertedPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-inverted.wav");
        try
        {
            SharpTapeExporter.Export(
                normalPath,
                [CreateRecord(TapeProfile.Normal1_1)],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);
            byte[] wav = File.ReadAllBytes(normalPath);
            for (int index = 44; index < wav.Length; index++)
            {
                wav[index] = (byte)(255 - wav[index]);
            }
            File.WriteAllBytes(invertedPath, wav);

            WavHeuristicAnalysisResult analysis = WavHeuristicAnalyzer.AnalyzeFile(invertedPath);
            WavRecoveryInfo recovery = Assert.Single(analysis.Statistics.RecordRecoveries);

            Assert.True(recovery.Header.Candidate.Inverted);
            Assert.True(recovery.Payload.Candidate.Inverted);
            Assert.False(analysis.Statistics.SelectiveRecoveryUsed);
        }
        finally
        {
            File.Delete(normalPath);
            File.Delete(invertedPath);
        }
    }

    [Fact]
    public void HeuristicImporter_DetectsPolarityPerRecord()
    {
        string normalPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-normal.wav");
        string invertedPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-inverted.wav");
        string tapePath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-mixed.wav");
        try
        {
            SharpTapeExporter.Export(
                normalPath,
                [CreateRecord(TapeProfile.Normal1_1, "NORMAL")],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);
            SharpTapeExporter.Export(
                invertedPath,
                [CreateRecord(TapeProfile.Normal1_1, "INVERTED")],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);

            byte[] normal = File.ReadAllBytes(normalPath);
            byte[] inverted = File.ReadAllBytes(invertedPath);
            for (int index = 44; index < inverted.Length; index++)
            {
                inverted[index] = (byte)(255 - inverted[index]);
            }
            File.WriteAllBytes(tapePath, Concatenate8BitMonoWavs(normal, inverted));

            WavHeuristicAnalysisResult analysis = WavHeuristicAnalyzer.AnalyzeFile(tapePath);

            Assert.Equal(2, analysis.Records.Count);
            Assert.Equal(2, analysis.Statistics.RecordRecoveries.Count);
            Assert.False(analysis.Statistics.RecordRecoveries[0].Header.Candidate.Inverted);
            Assert.False(analysis.Statistics.RecordRecoveries[0].Payload.Candidate.Inverted);
            Assert.True(analysis.Statistics.RecordRecoveries[1].Header.Candidate.Inverted);
            Assert.True(analysis.Statistics.RecordRecoveries[1].Payload.Candidate.Inverted);
            Assert.False(analysis.Statistics.SelectiveRecoveryUsed);
        }
        finally
        {
            File.Delete(normalPath);
            File.Delete(invertedPath);
            File.Delete(tapePath);
        }
    }

    [Fact]
    public void HeuristicImporter_ReadsStreamingFlac()
    {
        string wavPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        string flacPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.flac");
        TapeRecord expected = CreateRecord(TapeProfile.Normal1_1);
        try
        {
            SharpTapeExporter.Export(
                wavPath,
                [expected],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);
            using (var source = new SoundFileReader(wavPath))
            {
                SoundFileWriter.CreateSoundFile(flacPath, source);
            }

            WavHeuristicAnalysisResult analysis = WavHeuristicAnalyzer.AnalyzeFile(flacPath);
            WavHeuristicAnalysisResult wavAnalysis = WavHeuristicAnalyzer.AnalyzeFile(wavPath);
            TapeRecord actual = Assert.Single(analysis.Records);

            Assert.Equal(expected.Body.MzfBody, actual.Body.MzfBody);
            Assert.Equal("FLAC", analysis.Statistics.SourceFormat);
            Assert.Equal((uint)44100, analysis.Statistics.Format.SampleRate);
            Assert.Equal((ushort)1, analysis.Statistics.Format.Channels);
            WavRecoveryInfo recovery = Assert.Single(analysis.Statistics.RecordRecoveries);
            Assert.Equal(expected.Profile, recovery.Header.Candidate.Profile);
            Assert.False(recovery.Header.Candidate.Inverted);
            Assert.False(recovery.Payload.Candidate.Inverted);
            Assert.Equal(
                wavAnalysis.Records.Select(value => value.Body.MzfBody),
                analysis.Records.Select(value => value.Body.MzfBody));
            Assert.Equal(
                wavAnalysis.Statistics.RecordRecoveries.Select(value => value.Header.Candidate.Inverted),
                analysis.Statistics.RecordRecoveries.Select(value => value.Header.Candidate.Inverted));

            TapeRecord standard = Assert.Single(SharpTapeImporter.ReadFile(flacPath));
            Assert.Equal(expected.Body.MzfBody, standard.Body.MzfBody);
        }
        finally
        {
            File.Delete(wavPath);
            File.Delete(flacPath);
        }
    }

    private static TapeRecord CreateRecord(TapeProfile profile, string name = "HEURISTIC")
    {
        byte[] bodyBytes = [0x10, 0x20, 0x30, 0x40, 0x55, 0xAA];
        var header = new MZQFileHeader
        {
            MzfFtype = 0x01,
            MzfFname = System.Text.Encoding.ASCII.GetBytes(name.PadRight(16)[..16]),
            MzfFnameEnd = 0x0D,
            MzfSize = (ushort)bodyBytes.Length,
            MzfStart = 0x1200,
            MzfExec = 0x1200,
            MzfHeaderDescription = new byte[104]
        };
        var body = new MZQFileBody
        {
            DataSize = (ushort)bodyBytes.Length,
            MzfBody = bodyBytes,
            TrailingData = []
        };
        TapeRecord result = TapeRecord.FromLegacy(header, body);
        result.Profile = profile;
        result.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
        return result;
    }

    private static SharpBlockCandidate Candidate(
        SharpBlockKind kind,
        byte[] data,
        bool inverted,
        bool checksumValid,
        long start,
        long end,
        int channel = 0,
        double timingError = 0.01) => new()
    {
        Kind = kind,
        Channel = channel,
        CopyIndex = 0,
        Inverted = inverted,
        PulseMode = WavPulseMode.ZeroCrossing,
        Data = data,
        ChecksumValid = checksumValid,
        RecordedChecksum = checksumValid ? (ushort)1 : (ushort)2,
        CalculatedChecksum = 1,
        StartSample = start,
        EndSample = end,
        LeaderAverage = 10,
        LeaderStdDev = 0.1,
        PulseConfidence = 0.9,
        TimingError = timingError,
        Profile = TapeProfile.Normal1_1,
        ProfileEvidence = SharpProfileEvidence.TimingOnly
    };

    private static byte[] CreatePcmWav(ushort channels, ushort bits, uint sampleRate, int frames)
    {
        int bytesPerSample = (bits + 7) / 8;
        ushort blockAlign = checked((ushort)(channels * bytesPerSample));
        byte[] result = new byte[44 + (frames * blockAlign)];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        "WAVEfmt "u8.CopyTo(result.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(28, 4),
            checked(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34, 2), bits);
        "data"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), (uint)(frames * blockAlign));
        if (bits == 8)
        {
            result.AsSpan(44).Fill(128);
        }
        return result;
    }

    private static byte[] Convert8BitMonoToOffsetStereo(byte[] mono)
    {
        int frames = mono.Length - 44;
        byte[] stereo = new byte[44 + (frames * 4)];
        mono.AsSpan(0, 44).CopyTo(stereo);
        BinaryPrimitives.WriteUInt16LittleEndian(stereo.AsSpan(22, 2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(stereo.AsSpan(28, 4), 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(stereo.AsSpan(32, 2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(stereo.AsSpan(34, 2), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(stereo.AsSpan(40, 4), (uint)(frames * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(stereo.AsSpan(4, 4), (uint)(stereo.Length - 8));
        for (int frame = 0; frame < frames; frame++)
        {
            int centered = mono[44 + frame] - 128;
            short left = checked((short)(centered * 180));
            short right = checked((short)(-centered * 180 + 6000));
            BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(44 + (frame * 4), 2), left);
            BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(46 + (frame * 4), 2), right);
        }
        return stereo;
    }

    private static byte[] Concatenate8BitMonoWavs(byte[] first, byte[] second)
    {
        Assert.Equal(first.AsSpan(20, 16).ToArray(), second.AsSpan(20, 16).ToArray());
        int firstDataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(40, 4)));
        int secondDataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(40, 4)));
        byte[] result = new byte[44 + firstDataLength + secondDataLength];
        first.AsSpan(0, 44).CopyTo(result);
        first.AsSpan(44, firstDataLength).CopyTo(result.AsSpan(44));
        second.AsSpan(44, secondDataLength).CopyTo(result.AsSpan(44 + firstDataLength));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(40, 4),
            (uint)(firstDataLength + secondDataLength));
        return result;
    }
}
