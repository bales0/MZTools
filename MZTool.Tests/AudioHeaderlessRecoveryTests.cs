using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace MZTools.Tests;

public class AudioHeaderlessRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HeaderlessFraming_ChannelsPolarityGlitchAndChecksumRemainExplicit(bool corrupt, bool glitch)
    {
        byte[] body = [0xA5, 0x35, 0x17, 0xFF, 1, 0, 0x91]; string path = RawWave(body, corrupt, glitch);
        try
        {
            using var input = new WavPcmStreamReader(path);
            var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, input.Format.FrameCount, body.Length,
                options: new() { PayloadOnly = true, ZeroCrossing = false, Channel = 1, ThresholdScales = [1] });
            Assert.All(result.Candidates, c => Assert.Equal(1, c.Channel));
            if (corrupt)
            {
                Assert.DoesNotContain(result.Candidates, c => c.ChecksumValid);
                Assert.Contains(result.Candidates, c => c.ChecksumAvailable && !c.ChecksumValid);
                Assert.Contains(result.Diagnostics, e => e.Code == "CHECKSUM_MISMATCH");
            }
            else Assert.Contains(result.Candidates, c => c.Inverted && c.ChecksumValid && c.Data.SequenceEqual(body));
            Assert.DoesNotContain(result.Candidates, c => c.Kind == SharpBlockKind.Header);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownLength_SingleChecksumMatchIsUnverifiedButSeparateMatchingCopiesProvideEvidence()
    {
        byte[] body = [1, 2, 0xAA, 0x55];
        foreach (bool duplicate in new[] { false, true })
        {
            string path = RawWave(body, duplicate: duplicate);
            try
            {
                using var input = new WavPcmStreamReader(path);
                var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, input.Format.FrameCount,
                    options: new() { PayloadOnly = true, UnknownLength = true, ZeroCrossing = false, Channel = 1, ThresholdScales = [1] });
                var valid = result.Candidates.Where(c => c.ChecksumValid && c.Data.SequenceEqual(body)).ToArray();
                Assert.NotEmpty(valid);
                Assert.All(valid, c => Assert.Equal(duplicate, c.LengthVerified));
                if (!duplicate) Assert.Contains("unverified length", AudioRegionRecoveryWindow.CandidateStatus(valid[0]));
            }
            finally { File.Delete(path); }
        }
    }

    private static string RawWave(byte[] data, bool corrupt = false, bool glitch = false, bool duplicate = false)
    {
        var signal = new List<double>(); signal.AddRange(Enumerable.Repeat(0.0, 2000));
        void Pulse(bool isLong) { signal.AddRange(Enumerable.Repeat(-.65, isLong ? 20 : 10)); signal.AddRange(Enumerable.Repeat(.65, isLong ? 22 : 12)); }
        void Byte(byte b) { for (int mask = 128; mask > 0; mask >>= 1) Pulse((b & mask) != 0); Pulse(true); }
        void Block()
        {
            for (int i = 0; i < 64; i++) Pulse(false);
            for (int i = 0; i < 16; i++) Pulse(true);
            for (int i = 0; i < 16; i++) Pulse(false);
            Pulse(true); Pulse(true);
            int first = signal.Count;
            foreach (byte b in data) Byte(b);
            if (glitch) signal[first + 5] = .01;
            ushort checksum = unchecked((ushort)data.Sum(b => System.Numerics.BitOperations.PopCount((uint)b)));
            if (corrupt) checksum ^= 1;
            Byte((byte)(checksum >> 8)); Byte((byte)checksum);
            signal.AddRange(Enumerable.Repeat(0.0, 30000));
        }
        Block(); if (duplicate) Block();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write((uint)(36 + signal.Count * 4)); writer.Write("WAVEfmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2);
        writer.Write(44100u); writer.Write(176400u); writer.Write((ushort)4); writer.Write((ushort)16); writer.Write("data"u8); writer.Write((uint)(signal.Count * 4));
        foreach (double sample in signal) { writer.Write((short)((sample * .7 + .04) * 32767)); writer.Write((short)((-sample * .9 - .03) * 32767)); }
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadOnly_KnownAndUnknownLengthsReuseSharedDecoder(bool unknown)
    {
        string tape = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1), crop = tape + ".payload.wav";
        try
        {
            var original = WavHeuristicAnalyzer.AnalyzeFile(tape, includeCandidates: true);
            var blocks = original.Candidates.Where(c => c.Kind == SharpBlockKind.Payload && c.ChecksumValid).ToArray();
            using var reader = new WavPcmStreamReader(tape); var info = new FileInfo(tape);
            var source = new AudioSignalAnalysis { Source = tape, SourceLength = info.Length, SourceModifiedUtc = info.LastWriteTimeUtc,
                SampleRate = reader.Format.SampleRate, BitsPerSample = reader.Format.BitsPerSample, ChannelCount = reader.Format.Channels, FrameCount = reader.Format.FrameCount };
            long start = blocks.Min(c => c.SignalTrace!.LeaderStart), end = blocks.Max(c => c.EndSample);
            AudioSelectionWaveExporter.Export(source, crop, start, end, new(), 0);
            using var input = new WavPcmStreamReader(crop);
            var result = WavHeuristicAnalyzer.AnalyzeRange(crop, 0, input.Format.FrameCount, unknown ? null : 32,
                options: new() { PayloadOnly = true, UnknownLength = unknown, ThresholdScales = [1] });
            Assert.Empty(result.Records); Assert.DoesNotContain(result.Candidates, c => c.Kind == SharpBlockKind.Header);
            Assert.Contains(result.Candidates, c => c.ChecksumAvailable && c.ChecksumValid && c.Data.SequenceEqual(original.Records[0].Body.MzfBody));
            Assert.Contains(result.Diagnostics, e => e.Code == "SCANNER_CREATED");
            Assert.Contains(result.Diagnostics, e => e.Code == "MARK_FOUND");
            Assert.All(result.Candidates.Where(c => c.ChecksumValid), c => Assert.InRange(c.EndSample, c.StartSample, input.Format.FrameCount));
            if (!unknown) Assert.All(result.Candidates.Where(c => c.ChecksumValid), c => Assert.True(c.LengthVerified));
            var bin = crop + ".bin"; try { var candidate = result.Candidates.First(c => c.ChecksumValid); File.WriteAllBytes(bin, candidate.Data); Assert.Equal(original.Records[0].Body.MzfBody, File.ReadAllBytes(bin)); } finally { File.Delete(bin); }
        }
        finally { File.Delete(tape); File.Delete(crop); }
    }

    [Fact]
    public void SyntheticHeader_PreservesPayloadAndUsesOnlyExplicitMetadata()
    {
        byte[] payload = [1, 2, 3, 4];
        var record = AudioSyntheticHeader.Create(payload, "RECOVERED", 1, 0x10F0, 0x1200, "USER METADATA");
        Assert.Equal(payload, record.Body.MzfBody);
        Assert.Equal("RECOVERED", SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname));
        Assert.Equal(0x10F0, BinaryPrimitives.ReadUInt16LittleEndian(record.RawHeader.AsSpan(20, 2)));
        Assert.Equal(0x1200, BinaryPrimitives.ReadUInt16LittleEndian(record.RawHeader.AsSpan(22, 2)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(record.RawHeader.AsSpan(18, 2)));
        Assert.Throws<ArgumentException>(() => AudioSyntheticHeader.Create(payload, "", 1, 0, 0, ""));
        Assert.Throws<ArgumentException>(() => AudioSyntheticHeader.Create(payload, "NAME", 0, 0, 0, ""));
    }

    [Fact]
    public void DiscoveryRecovery_CoversEntireRecordingWithoutAnyHeader()
    {
        Assert.Equal(new[] { (0L, 1000L) }, WavHeuristicAnalyzer.GetDiscoveryRecoveryIntervals([], 1000));
        var events = new AudioRecoveryDiagnostics();
        for (int i = 0; i < 10000; i++) events.Add(new("Test", "RESET", "bounded", i, i / 44100.0, 1, false, "ZeroCrossing", 0, null, 1, ""));
        Assert.Equal(16, events.Events.Count); Assert.Equal(9984, events.Omitted);
    }

    [Fact]
    public void HeuristicDiscovery_RecoversMissedHeadersWithTheSameSchmittPassAsManual()
    {
        string tape = AudioSignalAnalysisTests.MakeTape(TapeProfile.Normal1_1), weak = tape + ".weak.wav";
        try
        {
            var clean = WavHeuristicAnalyzer.AnalyzeFile(tape, includeCandidates: true);
            var headers = clean.Candidates.Where(c => c.Kind == SharpBlockKind.Header && c.SignalTrace != null).ToArray();
            using var reader = new WavPcmStreamReader(tape);
            using (var writer = new BinaryWriter(File.Create(weak)))
            {
                writer.Write("RIFF"u8); writer.Write((uint)(36 + reader.Format.FrameCount * 2)); writer.Write("WAVEfmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write(reader.Format.SampleRate); writer.Write(reader.Format.SampleRate * 2); writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write((uint)(reader.Format.FrameCount * 2));
                reader.ReadFrames((i, sample, _) =>
                {
                    short value = (short)(Math.Sign(sample) * 60);
                    if (sample < 0 && i % 31 == 0 && headers.Any(h => i > h.SignalTrace!.DataStart + 200 && i < h.SignalTrace.DataStart + 600)) value = 20;
                    writer.Write(value);
                });
            }
            var baseline = WavHeuristicAnalyzer.AnalyzeRange(weak, 0, reader.Format.FrameCount, options: new() { ThresholdScales = [1] });
            Assert.Empty(baseline.Records);
            var manual = WavHeuristicAnalyzer.AnalyzeRange(weak, 0, reader.Format.FrameCount, options: new() { ZeroCrossing = false, ThresholdScales = [.4] });
            Assert.Single(manual.Records);
            var automatic = WavHeuristicAnalyzer.AnalyzeFile(weak, includeCandidates: true, enableDiagnostics: true);
            Assert.Single(automatic.Records); Assert.True(automatic.Statistics.SelectiveRecoveryUsed);
            Assert.Equal(manual.Records[0].GetSerializedHeader(), automatic.Records[0].GetSerializedHeader());
            Assert.Equal(clean.Records[0].Body.MzfBody, automatic.Records[0].Body.MzfBody);
            Assert.Contains(automatic.Statistics.Diagnostics, e => e.Code == "UNRESOLVED_INTERVAL" && e.Sample == 0);
        }
        finally { File.Delete(tape); File.Delete(weak); }
    }

    [Fact]
    public void RealHeaderlessFixture_KnownLengthMatchesOriginalProgramWhenConfigured()
    {
        string? path = Environment.GetEnvironmentVariable("MZTOOLS_HEADERLESS_FIXTURE");
        if (string.IsNullOrWhiteSpace(path)) return;
        string originalPath = Environment.GetEnvironmentVariable("MZTOOLS_AUDIO_RECOVERY_FIXTURE") ?? throw new InvalidOperationException("Configure the original capture too.");
        using var input = new WavPcmStreamReader(path);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
        var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, input.Format.FrameCount, 37744,
            options: new() { PayloadOnly = true, ZeroCrossing = false, ThresholdScales = [.4, .65, 1, 1.4] });
        var original = WavHeuristicAnalyzer.AnalyzeFile(originalPath, includeCandidates: true, enableDiagnostics: true);
        byte[] expected = original.Records.Single(r => r.Body.MzfBody.Length == 37744).Body.MzfBody;
        Assert.Empty(result.Records);
        Assert.Contains(result.Candidates, c => c.ChecksumAvailable && c.ChecksumValid && c.Data.SequenceEqual(expected));
        var unknown = WavHeuristicAnalyzer.AnalyzeRange(path, 0, input.Format.FrameCount,
            options: new() { PayloadOnly = true, UnknownLength = true, ZeroCrossing = false, ThresholdScales = [.4, .65, 1, 1.4] });
        long verifiedEnd = result.Candidates.First(c => c.ChecksumValid && c.Data.SequenceEqual(expected)).EndSample;
        var selectedEnd = WavHeuristicAnalyzer.AnalyzeRange(path, 0, verifiedEnd,
            options: new() { PayloadOnly = true, UnknownLength = true, ZeroCrossing = false, ThresholdScales = [.4], Channel = 1 });
        Assert.Contains(unknown.Candidates, c => c.ChecksumValid && c.Data.SequenceEqual(expected) && !c.LengthVerified);
        Assert.Contains(selectedEnd.Candidates, c => c.ChecksumValid && c.Data.SequenceEqual(expected));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        string? report = Environment.GetEnvironmentVariable("MZTOOLS_HEADERLESS_AUDIT");
        if (!string.IsNullOrWhiteSpace(report))
            File.WriteAllText(report, JsonSerializer.Serialize(new { Hash = Convert.ToHexString(hash), input.Format, PayloadSha256 = Convert.ToHexString(SHA256.HashData(expected)),
                Candidates = result.Candidates.Select(c => new { c.Kind, c.Channel, c.Inverted, c.PulseMode, Length = c.Data.Length, c.ChecksumValid, c.RecordedChecksum, c.CalculatedChecksum, c.StartSample, c.EndSample, c.LengthVerified }),
                UnknownWhole = unknown.Candidates.Select(c => new { Length = c.Data.Length, c.Channel, c.ChecksumValid, c.LengthVerified, c.BoundaryEvidence }),
                UnknownSelectedEnd = selectedEnd.Candidates.Select(c => new { Length = c.Data.Length, c.Channel, c.ChecksumValid, c.LengthVerified, c.BoundaryEvidence }),
                Diagnostics = result.Diagnostics, result.OmittedDiagnostics, OriginalDiagnostics = original.Statistics.Diagnostics }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    [Fact]
    public void RealCapture_StandardHeuristicAndManualParityAuditWhenConfigured()
    {
        string? path = Environment.GetEnvironmentVariable("MZTOOLS_AUDIO_RECOVERY_FIXTURE");
        if (string.IsNullOrWhiteSpace(path)) return;
        byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
        string standard; try { standard = $"Recovered {SharpTapeImporter.ReadFile(path).Count} record(s)"; } catch (Exception ex) { standard = ex.GetType().Name + ": " + ex.Message; }
        using var reader = new WavPcmStreamReader(path);
        var automatic = WavHeuristicAnalyzer.AnalyzeFile(path, includeCandidates: true, enableDiagnostics: true);
        var manual = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount, options: new() { ThresholdScales = [.4, .65, 1, 1.4] });
        Assert.Equal(3, automatic.Records.Count); Assert.Equal(3, manual.Records.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(automatic.Records[i].GetSerializedHeader(), manual.Records[i].GetSerializedHeader());
            Assert.Equal(automatic.Records[i].Body.MzfBody, manual.Records[i].Body.MzfBody);
        }
        Assert.Equal(37744, automatic.Records[0].Body.MzfBody.Length);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        string? output = Environment.GetEnvironmentVariable("MZTOOLS_HEADERLESS_AUDIT");
        if (!string.IsNullOrWhiteSpace(output))
            File.WriteAllText(output + ".parity.json", JsonSerializer.Serialize(new { Source = path, Hash = Convert.ToHexString(hash), reader.Format, Standard = standard,
                HeuristicRecords = automatic.Records.Count, ManualRecords = manual.Records.Count,
                Records = automatic.Records.Select(r => new { Name = SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname), Bytes = r.Body.MzfBody.Length, PayloadSha256 = Convert.ToHexString(SHA256.HashData(r.Body.MzfBody)) }),
                HeuristicEvents = automatic.Statistics.Diagnostics, ManualEvents = manual.Diagnostics, manual.OmittedDiagnostics,
                Passes = manual.Passes }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
