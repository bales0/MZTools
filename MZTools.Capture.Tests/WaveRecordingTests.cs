using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace MZTools.Capture.Tests;

public class WaveRecordingTests
{
    private static TimeSpan Timeout => TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(1048577)]
    [InlineData(int.MaxValue)]
    public void ConversionBuffer_HasAnExplicitMemoryBound(int bytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmWaveRecordingSink(new("not-created.wav") { ConversionBufferBytes = bytes }));
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,1,22050)]
    [InlineData(AudioSampleRepresentation.Unsigned8,2,44100)]
    [InlineData(AudioSampleRepresentation.Signed16,1,48000)]
    [InlineData(AudioSampleRepresentation.Signed16,2,88200)]
    [InlineData(AudioSampleRepresentation.Signed24,1,96000)]
    [InlineData(AudioSampleRepresentation.Signed24,2,48000)]
    public async Task IntegerArchive_PreservesEveryByteRateChannelsAndResolution(AudioSampleRepresentation representation,int channels,int rate)
    {
        using var folder = new RecordingTestFolder();
        var format = new AudioCaptureFormat(rate,channels,representation);
        byte[] payload = Enumerable.Range(0,17*format.BlockAlign).Select(i => (byte)(i*53)).ToArray();
        var report = await Record(format,payload,new(folder.PathFor("archive.wav")) { TapeNote = "Test tape",TapeSide = "B",ApplicationVersion = "test" });
        Assert.Equal(CaptureIntegrity.Complete,report.Integrity); Assert.True(report.SinkFinalized);
        var recording = report.Recording!; Assert.True(recording.SidecarFinalized); Assert.True(recording.SourceHashCoversSavedFramesExactly);
        var segment = Assert.Single(recording.Segments); Assert.True(segment.Published); Assert.True(segment.HeaderFinalized);
        byte[] file = File.ReadAllBytes(segment.Path); ValidateHeader(file,format,17);
        Assert.Equal(payload,file.AsSpan(44,payload.Length).ToArray());
        Assert.Equal(Hash(file),segment.FileSha256); Assert.Equal(Hash(payload),segment.PcmSha256);
        Assert.Equal(Hash(payload),recording.SourcePcmSha256); Assert.Equal(17,recording.SourceFramesHashed);
        Assert.False(recording.Conversion.ChangesContainer); Assert.Equal(rate,recording.Conversion.Output.SampleRate);
        using var sidecar = JsonDocument.Parse(File.ReadAllBytes(recording.SidecarPath!));
        var session = sidecar.RootElement.GetProperty("session"); Assert.Equal("Complete",session.GetProperty("integrity").GetString());
        Assert.Equal(17,session.GetProperty("statistics").GetProperty("writtenFrames").GetInt64());
        Assert.Equal("B",sidecar.RootElement.GetProperty("tapeSide").GetString());
        Assert.Equal("test",sidecar.RootElement.GetProperty("application").GetProperty("applicationVersion").GetString());
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Float32)]
    [InlineData(AudioSampleRepresentation.Signed32)]
    public async Task Conversion_IsNeverEnabledImplicitly(AudioSampleRepresentation representation)
    {
        using var folder = new RecordingTestFolder(); var format = new AudioCaptureFormat(48000,1,representation);
        var backend = new ManualBackend { ActualFormat = format }; var sink = new PcmWaveRecordingSink(new(folder.PathFor("no.wav")));
        await using var session = new CaptureSession(backend,new("manual",format));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.StartAsync(sink).WaitAsync(Timeout));
        var report = await session.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureSessionState.Failed,session.State); Assert.Null(report.Recording);
        Assert.Empty(Directory.GetFiles(folder.DirectoryPath));
    }

    [Fact]
    public async Task FloatToPcm24_HasExplicitRoundingSaturationAndNoDither()
    {
        using var folder = new RecordingTestFolder();
        float[] values = [-1,-.5f,-1f/16777216,0,1f/16777216,.5f,1,1.5f,-1.5f];
        int[] expected = [-8388608,-4194304,-1,0,1,4194304,8388607,8388607,-8388608];
        byte[] source = new byte[values.Length*4];
        for(int i=0;i<values.Length;i++) BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(i*4),values[i]);
        var report = await Record(new(48000,1,AudioSampleRepresentation.Float32),source,
            new(folder.PathFor("float.wav")) { AllowPcm24Conversion = true });
        var result = report.Recording!; var segment = Assert.Single(result.Segments);
        Assert.Equal(expected,ReadPcm24(File.ReadAllBytes(segment.Path).AsSpan(44,expected.Length*3)));
        Assert.Equal(3,result.SaturatedSamples); Assert.True(result.Conversion.MayLosePrecision);
        Assert.Equal(Hash(source),result.SourcePcmSha256); Assert.Contains("no dither",result.Conversion.Method);
    }

    [Fact]
    public async Task FormatFallback_ArchivesTheActualRateAndChannelsWithoutPretendingTheSourceWas24Bit()
    {
        using var folder = new RecordingTestFolder();
        var actual = new AudioCaptureFormat(96000,2,AudioSampleRepresentation.Float32);
        var backend = new ManualBackend { ActualFormat = actual };
        var sink = new PcmWaveRecordingSink(new(folder.PathFor("actual.wav")) { AllowPcm24Conversion = true });
        await using var session = CaptureSessionTests.Create(backend,fallback:true);
        await session.StartAsync(sink).WaitAsync(Timeout);
        byte[] source = new byte[8]; BinaryPrimitives.WriteSingleLittleEndian(source,-.5f); BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(4),.25f);
        backend.Last!.Emit(source,new(0,1,0)); var report = await session.StopAsync().WaitAsync(Timeout);
        Assert.Equal(CaptureSessionTests.Format,report!.Stream!.RequestedFormat); Assert.Equal(actual,report.Stream.ActualFormat);
        Assert.Equal(32,report.Recording!.Conversion.Source.BitsPerSample); Assert.Equal(24,report.Recording.Conversion.Output.BitsPerSample);
        var segment = Assert.Single(report.Recording.Segments);
        ValidateHeader(File.ReadAllBytes(segment.Path),new(96000,2,AudioSampleRepresentation.Signed24),1);
        Assert.Equal(8,report.Recording.SourceBytesHashed); Assert.Equal(6,report.Recording.SavedPcmBytes);
    }

    [Fact]
    public async Task Signed32To24_UsesAwayFromZeroAndRetainsBothFullScaleEnds()
    {
        using var folder = new RecordingTestFolder();
        int[] values = [int.MinValue,-384,-128,0,128,384,int.MaxValue];
        int[] expected = [-8388608,-2,-1,0,1,2,8388607];
        byte[] source = new byte[values.Length*4];
        for(int i=0;i<values.Length;i++) BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(i*4),values[i]);
        var report = await Record(new(96000,1,AudioSampleRepresentation.Signed32),source,
            new(folder.PathFor("integer32.wav")) { AllowPcm24Conversion = true });
        Assert.Equal(expected,ReadPcm24(File.ReadAllBytes(report.Recording!.Segments[0].Path).AsSpan(44,expected.Length*3)));
        Assert.Equal(1,report.Recording.SaturatedSamples);
        var lossless = AudioPcmQuantizer.Plan(new(48000,2,AudioSampleRepresentation.Signed32,24),true);
        Assert.False(lossless.MayLosePrecision); Assert.Equal(24,lossless.Output.ValidBitsPerSample);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteFloat_IsRejectedWithoutInventingAReplacement(float value)
    {
        byte[] source = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(source,value);
        var conversion = AudioPcmQuantizer.Plan(new(48000,1,AudioSampleRepresentation.Float32),true);
        Assert.Throws<InvalidDataException>(() => AudioPcmQuantizer.Convert(conversion,source,new byte[3]));
    }

    [Fact]
    public void FloatQuantization_HasHalfLsbErrorBoundAndDeterministicOutput()
    {
        var random = new Random(1234); float[] values = Enumerable.Range(0,1024).Select(_ => (float)((random.NextDouble()*2-1)*.999)).ToArray();
        byte[] source = new byte[values.Length*4];
        for(int i=0;i<values.Length;i++) BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(i*4),values[i]);
        var conversion = AudioPcmQuantizer.Plan(new(48000,1,AudioSampleRepresentation.Float32),true);
        byte[] first = new byte[values.Length*3], second = new byte[first.Length];
        AudioPcmQuantizer.Convert(conversion,source,first); AudioPcmQuantizer.Convert(conversion,source,second);
        Assert.Equal(first,second); int[] decoded = ReadPcm24(first);
        for(int i=0;i<values.Length;i++) Assert.InRange(Math.Abs(values[i]-decoded[i]/8388608.0),0,.500001/8388608);
    }

    [Fact]
    public void IntegerValidBits_AreLosslesslyRepackedWithoutRoundingUnusedPadding()
    {
        var conversion = AudioPcmQuantizer.Plan(new(48000,1,AudioSampleRepresentation.Signed32,24),true);
        int[] input = [0x00000180,unchecked((int)0xFFFFFF80),int.MaxValue,int.MinValue];
        byte[] bytes = new byte[input.Length*4], output = new byte[input.Length*3];
        for(int i=0;i<input.Length;i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i*4),input[i]);
        var counts = AudioPcmQuantizer.Convert(conversion,bytes,output);
        Assert.Equal(new[] {1,-1,8388607,-8388608},ReadPcm24(output)); Assert.Equal(0,counts.SaturatedSamples);
        Assert.False(conversion.MayLosePrecision);
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,1)]
    [InlineData(AudioSampleRepresentation.Signed16,2)]
    [InlineData(AudioSampleRepresentation.Signed24,1)]
    [InlineData(AudioSampleRepresentation.Signed24,2)]
    public async Task Segments_AreFrameAlignedNumberedAndHaveContinuousOffsets(AudioSampleRepresentation representation,int channels)
    {
        using var folder = new RecordingTestFolder(); var format = new AudioCaptureFormat(48000,channels,representation);
        byte[] payload = Enumerable.Range(0,11*format.BlockAlign).Select(i => (byte)(i*19)).ToArray();
        var report = await Record(format,payload,new(folder.PathFor("split.wav")) { SegmentDataLimit = 3*format.BlockAlign });
        var result = report.Recording!; Assert.Equal(4,result.Segments.Count); Assert.Equal(11,result.SavedFrames);
        var joined = new List<byte>(); long first = 0;
        for(int i=0;i<result.Segments.Count;i++)
        {
            var segment = result.Segments[i]; Assert.Equal(first,segment.FirstFrame); first += segment.Frames;
            Assert.Equal(i == 0 ? "split.wav" : $"split_{i+1:D3}.wav",System.IO.Path.GetFileName(segment.Path));
            byte[] file = File.ReadAllBytes(segment.Path); ValidateHeader(file,format,segment.Frames);
            joined.AddRange(file.AsSpan(44,(int)segment.DataBytes).ToArray()); Assert.True(segment.Published);
        }
        Assert.Equal(payload,joined.ToArray()); Assert.Equal(Hash(payload),result.SourcePcmSha256);
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,1)]
    [InlineData(AudioSampleRepresentation.Unsigned8,2)]
    [InlineData(AudioSampleRepresentation.Signed16,2)]
    [InlineData(AudioSampleRepresentation.Signed24,1)]
    [InlineData(AudioSampleRepresentation.Signed24,2)]
    public void FourGiBBoundary_Uses64BitCountsAndAccountsForRiffPadding(AudioSampleRepresentation representation,int channels)
    {
        var format = new AudioCaptureFormat(96000,channels,representation);
        long capacity = RiffWaveLayout.FrameCapacity(format);
        byte[] header = RiffWaveLayout.Header(format,capacity);
        long data = capacity*format.BlockAlign;
        Assert.Equal(data,BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
        Assert.Equal(36+data+(data&1),BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.InRange(36+data+(data&1),0,uint.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => RiffWaveLayout.Header(format,capacity+1));
        // Fake frame accounting crosses 4 GiB without allocating a recording-sized byte array.
        long remaining = capacity+19; var segmentFrames = new List<long>();
        while(remaining > 0) { long frames = Math.Min(remaining,capacity); segmentFrames.Add(frames); remaining -= frames; }
        Assert.Equal(new[] {capacity,19},segmentFrames);
    }

    [Fact]
    public async Task PartialWrite_RepairsHeaderPreservesAllBytesAndReportsActualSavedPrefix()
    {
        using var folder = new RecordingTestFolder(); var storage = new FaultStorage { FailAfterPcmBytes = 5 };
        byte[] payload = [1,2,3,4,5,6,7,8];
        var report = await Record(CaptureSessionTests.Format,payload,new(folder.PathFor("partial.wav")),storage);
        Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity); Assert.Equal(CaptureEndReason.SinkError,report.Reason);
        Assert.True(report.SinkFinalized); var result = report.Recording!;
        var segment = Assert.Single(result.Segments); Assert.True(segment.HeaderFinalized); Assert.False(segment.Published);
        Assert.Equal(2,segment.Frames); Assert.Equal(4,segment.DataBytes); Assert.Equal("05",segment.TrailingPartialFrameHex);
        byte[] partial = File.ReadAllBytes(segment.Path); Assert.Equal(payload.AsSpan(0,5).ToArray(),partial.AsSpan(44).ToArray());
        Assert.Equal(4L,(long)BinaryPrimitives.ReadUInt32LittleEndian(partial.AsSpan(40)));
        Assert.Equal(4,result.SourceFramesHashed); Assert.Equal(2,result.SavedFrames); Assert.False(result.SourceHashCoversSavedFramesExactly);
        Assert.Contains(report.Diagnostics,d => d.Code == "SINK_WRITE_FAILED"); Assert.True(result.SidecarFinalized);
        using var json = JsonDocument.Parse(File.ReadAllBytes(result.SidecarPath!));
        Assert.Equal("Incomplete",json.RootElement.GetProperty("status").GetString());
        Assert.False(File.Exists(folder.PathFor("partial.wav")));
    }

    [Fact]
    public async Task PersistentFinalizeFailure_KeepsOriginalPartialDataAndDoesNotClaimSuccess()
    {
        using var folder = new RecordingTestFolder(); var storage = new FaultStorage { FailRepairWrites = true };
        var report = await Record(CaptureSessionTests.Format,[1,2,3,4],new(folder.PathFor("failed.wav")),storage);
        Assert.False(report.SinkFinalized); Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);
        var segment = Assert.Single(report.Recording!.Segments); Assert.False(segment.HeaderFinalized); Assert.False(segment.Published);
        Assert.Equal(new byte[] {1,2,3,4},File.ReadAllBytes(segment.Path).AsSpan(44).ToArray());
        Assert.Contains(report.Diagnostics,d => d.Code == "SINK_FINALIZE_FAILED"); Assert.True(report.Recording.SidecarFinalized);
    }

    [Fact]
    public async Task SidecarFailure_IsSeparateFromSuccessfullySavedAudioAndLeavesMetadataAvailable()
    {
        using var folder = new RecordingTestFolder(); var storage = new FaultStorage { FailSidecarRename = true };
        var report = await Record(CaptureSessionTests.Format,[1,2,3,4],new(folder.PathFor("jsonfail.wav")),storage);
        Assert.Equal(CaptureEndReason.SinkError,report.Reason);
        Assert.Contains(report.Diagnostics,d => d.Code == "SIDECAR_WRITE_FAILED");
        Assert.True(report.SinkFinalized); Assert.True(report.Recording!.Segments[0].Published); Assert.False(report.Recording.SidecarFinalized);
        Assert.True(File.Exists(report.Recording.SidecarPath)); Assert.NotEmpty(report.Recording.Errors);
    }

    [Fact]
    public async Task ExistingDestination_IsNeverOverwritten()
    {
        using var folder = new RecordingTestFolder(); string path = folder.PathFor("existing.wav"); File.WriteAllBytes(path,[9,8,7]);
        var backend = new ManualBackend(); var sink = new PcmWaveRecordingSink(new(path)); await using var session = CaptureSessionTests.Create(backend);
        await Assert.ThrowsAsync<IOException>(() => session.StartAsync(sink).WaitAsync(Timeout));
        var report = await session.Completion!.WaitAsync(Timeout); Assert.Null(report.Recording);
        Assert.Equal(new byte[] {9,8,7},File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(folder.DirectoryPath));
    }

    [Fact]
    public async Task RenameCollision_KeepsBothExistingDestinationAndNewRecording()
    {
        using var folder = new RecordingTestFolder(); var storage = new FaultStorage { CreateCollisionOnRename = true };
        var report = await Record(CaptureSessionTests.Format,[1,2,3,4],new(folder.PathFor("collision.wav")),storage);
        Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity); var segment = Assert.Single(report.Recording!.Segments);
        Assert.True(segment.HeaderFinalized); Assert.False(segment.Published); Assert.True(File.Exists(segment.Path));
        Assert.Equal(new byte[] {0xA5},File.ReadAllBytes(folder.PathFor("collision.wav")));
        Assert.Equal(new byte[] {1,2,3,4},File.ReadAllBytes(segment.Path).AsSpan(44,4).ToArray());
    }

    [Fact]
    public async Task PermissionDenied_HasNoSilentSuccessOrUnrelatedFileModification()
    {
        using var folder = new RecordingTestFolder(); var backend = new ManualBackend();
        var sink = new PcmWaveRecordingSink(new(folder.PathFor("denied.wav")),new FaultStorage { DenyCreate = true });
        await using var session = CaptureSessionTests.Create(backend);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.StartAsync(sink).WaitAsync(Timeout));
        var report = await session.Completion!.WaitAsync(Timeout); Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);
        Assert.Empty(Directory.GetFiles(folder.DirectoryPath)); Assert.Null(report.Recording);
    }

    [Fact]
    public async Task StopBeforeAnyAudio_PreservesValidEmptyPartialButDoesNotOfferItForAnalysis()
    {
        using var folder = new RecordingTestFolder(); var report = await Record(CaptureSessionTests.Format,[],new(folder.PathFor("empty.wav")));
        Assert.Equal(CaptureIntegrity.NotRecorded,report.Integrity); Assert.False(report.Recording!.AnalyzerCompatible);
        var segment = Assert.Single(report.Recording.Segments); Assert.Equal(0,segment.Frames); Assert.True(segment.HeaderFinalized);
        Assert.False(segment.Published); Assert.Equal(44,new FileInfo(segment.Path).Length); Assert.True(report.Recording.SidecarFinalized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedRate_RequiresExplicitArchivalChoiceAndNeverResamples(bool allow)
    {
        using var folder = new RecordingTestFolder(); var format = new AudioCaptureFormat(192000,1,AudioSampleRepresentation.Signed16);
        var options = new WaveRecordingOptions(folder.PathFor("rate.wav")) { RequireAnalyzerCompatibleRate = !allow };
        if (allow)
        {
            var report = await Record(format,[1,2,3,4],options); Assert.False(report.Recording!.AnalyzerCompatible);
            Assert.Equal(192000L,(long)BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(report.Recording.Segments[0].Path).AsSpan(24)));
        }
        else
        {
            var backend = new ManualBackend { ActualFormat = format }; await using var session = new CaptureSession(backend,new("manual",format));
            await Assert.ThrowsAsync<NotSupportedException>(() => session.StartAsync(new PcmWaveRecordingSink(options)).WaitAsync(Timeout));
            await session.Completion!.WaitAsync(Timeout); Assert.Empty(Directory.GetFiles(folder.DirectoryPath));
        }
    }

    [Fact]
    public async Task SegmentRotation_DoesNotReadBackTheWholeFileWhileCaptureIsRunning()
    {
        using var folder = new RecordingTestFolder(); var storage = new FaultStorage(); var backend = new ManualBackend();
        var sink = new PcmWaveRecordingSink(new(folder.PathFor("live.wav")) { SegmentDataLimit = 8 },storage);
        await using var session = CaptureSessionTests.Create(backend); await session.StartAsync(sink).WaitAsync(Timeout);
        backend.Last!.Emit(Enumerable.Range(0,16).Select(i => (byte)i).ToArray(),new(0,8,0));
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency*10;
        while(session.Statistics!.WrittenFrames < 8)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline) throw new TimeoutException();
            await Task.Yield();
        }
        Assert.Equal(44,Interlocked.Read(ref storage.ReadBytes)); // Header only, not a recording-sized read.
        Assert.Equal(CaptureSessionState.Recording,session.State);
        var report = await session.StopAsync().WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.Complete,report!.Integrity);
        Assert.All(report.Recording!.Segments,s => { Assert.True(s.PcmMatchesWrittenBytes); Assert.NotNull(s.FileSha256); });
    }

    [Fact]
    public async Task ClosedSegmentModification_IsDetectedBeforeDeclaringRecordingComplete()
    {
        using var folder = new RecordingTestFolder(); var backend = new ManualBackend();
        var sink = new PcmWaveRecordingSink(new(folder.PathFor("changed.wav")) { SegmentDataLimit = 4 });
        await using var session = CaptureSessionTests.Create(backend); await session.StartAsync(sink).WaitAsync(Timeout);
        backend.Last!.Emit([1,2,3,4,5,6,7,8],new(0,4,0));
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency*10;
        while(session.Statistics!.WrittenFrames < 4)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline) throw new TimeoutException();
            await Task.Yield();
        }
        using(var file = new FileStream(folder.PathFor("changed.wav"),FileMode.Open,FileAccess.Write))
        { file.Position = 44; file.WriteByte(0xA5); }
        var report = await session.StopAsync().WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.Incomplete,report!.Integrity); Assert.False(report.SinkFinalized);
        Assert.False(report.Recording!.Segments[0].PcmMatchesWrittenBytes);
        Assert.Contains(report.Recording.Errors,e => e.Contains("differs",StringComparison.Ordinal));
    }

    private static async Task<CaptureSessionReport> Record(AudioCaptureFormat format,byte[] payload,WaveRecordingOptions options,IAudioRecordingStorage? storage = null)
    {
        var backend = new ManualBackend { ActualFormat = format }; var sink = new PcmWaveRecordingSink(options,storage);
        await using var session = new CaptureSession(backend,new("manual",format));
        await session.StartAsync(sink).WaitAsync(Timeout);
        if (payload.Length > 0) backend.Last!.Emit(payload,new(0,payload.Length/format.BlockAlign,0));
        backend.Last!.Finish(); return await session.Completion!.WaitAsync(Timeout);
    }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static int[] ReadPcm24(ReadOnlySpan<byte> data)
    {
        int[] result = new int[data.Length/3];
        for(int i=0;i<result.Length;i++) { int n = data[i*3] | data[i*3+1]<<8 | data[i*3+2]<<16; result[i] = n<<8>>8; }
        return result;
    }
    private static void ValidateHeader(byte[] file,AudioCaptureFormat format,long frames)
    {
        long bytes = frames*format.BlockAlign;
        Assert.Equal("RIFF"u8.ToArray(),file.AsSpan(0,4).ToArray()); Assert.Equal("WAVEfmt "u8.ToArray(),file.AsSpan(8,8).ToArray());
        Assert.Equal(1,BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(20))); Assert.Equal(format.Channels,BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(22)));
        Assert.Equal((long)format.SampleRate,(long)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24))); Assert.Equal(format.BitsPerSample,BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(34)));
        Assert.Equal(bytes,BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40))); Assert.Equal(44+bytes+(bytes&1),file.LongLength);
        Assert.Equal(file.LongLength-8,BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
    }
}

internal sealed class RecordingTestFolder : IDisposable
{
    internal string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"mztools-capture-test-"+Guid.NewGuid().ToString("N"));
    internal RecordingTestFolder() => Directory.CreateDirectory(DirectoryPath);
    internal string PathFor(string name) => System.IO.Path.Combine(DirectoryPath,name);
    public void Dispose() => Directory.Delete(DirectoryPath,recursive:true);
}

internal sealed class FaultStorage : IAudioRecordingStorage
{
    private readonly FileAudioRecordingStorage files = new();
    internal int? FailAfterPcmBytes;
    internal long ReadBytes;
    internal bool FailRepairWrites,FailSidecarRename,DenyCreate,CreateCollisionOnRename;
    public bool Exists(string path) => files.Exists(path);
    public Stream CreateNew(string path)
    {
        if (DenyCreate) throw new UnauthorizedAccessException("Injected permission denial.");
        var stream = files.CreateNew(path);
        return path.EndsWith(".partial.wav",StringComparison.Ordinal) && FailAfterPcmBytes.HasValue ? new FaultStream(stream,FailAfterPcmBytes,false) : stream;
    }
    public Stream OpenExisting(string path)
    {
        var stream = files.OpenExisting(path);
        return new FaultStream(stream,null,FailRepairWrites && path.EndsWith(".partial.wav",StringComparison.Ordinal),
            count => Interlocked.Add(ref ReadBytes,count));
    }
    public Stream OpenRead(string path) => new FaultStream(files.OpenRead(path),null,false,
        count => Interlocked.Add(ref ReadBytes,count));
    public void FlushToDisk(Stream stream) => files.FlushToDisk(stream);
    public void MoveNew(string source,string destination)
    {
        if (FailSidecarRename && destination.EndsWith(".json",StringComparison.Ordinal)) throw new IOException("Injected sidecar publish failure.");
        if (CreateCollisionOnRename && destination.EndsWith(".wav",StringComparison.Ordinal)) File.WriteAllBytes(destination,[0xA5]);
        files.MoveNew(source,destination);
    }

    private sealed class FaultStream(Stream inner,int? pcmLimit,bool rejectWrites,Action<int>? onRead = null) : Stream
    {
        private bool failed;
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer,int offset,int count) { int read = inner.Read(buffer,offset,count); onRead?.Invoke(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token = default)
        { int read = await inner.ReadAsync(buffer,token); onRead?.Invoke(read); return read; }
        public override long Seek(long offset,SeekOrigin origin) => inner.Seek(offset,origin);
        public override void SetLength(long length) => inner.SetLength(length);
        public override void Write(byte[] buffer,int offset,int count) => WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token = default)
        {
            if (rejectWrites) throw new IOException("Injected persistent finalize failure.");
            if (pcmLimit.HasValue && Position >= 44 && !failed)
            {
                int writable = (int)Math.Clamp(44+pcmLimit.Value-Position,0,buffer.Length);
                if (writable > 0) await inner.WriteAsync(buffer[..writable],token);
                if (writable < buffer.Length) { failed = true; throw new IOException("Injected disk full after partial write."); }
                return;
            }
            await inner.WriteAsync(buffer,token);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
