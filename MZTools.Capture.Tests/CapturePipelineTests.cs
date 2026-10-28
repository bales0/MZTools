using System.Security.Cryptography;
using System.Threading.Channels;

namespace MZTools.Capture.Tests;

public class CapturePipelineTests
{
    private static TimeSpan Timeout => CaptureSessionTests.Timeout;

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,1)]
    [InlineData(AudioSampleRepresentation.Unsigned8,2)]
    [InlineData(AudioSampleRepresentation.Signed16,1)]
    [InlineData(AudioSampleRepresentation.Signed16,2)]
    [InlineData(AudioSampleRepresentation.Signed24,1)]
    [InlineData(AudioSampleRepresentation.Signed24,2)]
    [InlineData(AudioSampleRepresentation.Signed32,1)]
    [InlineData(AudioSampleRepresentation.Signed32,2)]
    [InlineData(AudioSampleRepresentation.Float32,1)]
    [InlineData(AudioSampleRepresentation.Float32,2)]
    public async Task OwnedPcm_IsByteExactForEveryFormatAndSplitsOnlyOnFrameBoundaries(AudioSampleRepresentation representation, int channels)
    {
        var format = new AudioCaptureFormat(48000,channels,representation);
        var backend = new ManualBackend { ActualFormat = format }; var sink = new MemorySink();
        await using var controller = new CaptureSession(backend,new("manual",format),new() { MaximumChunkFrames = 8, ArchiveQueueCapacity = 64 });
        await controller.StartAsync(sink).WaitAsync(Timeout);
        byte[] expected = Enumerable.Range(0,71 * format.BlockAlign).Select(n => (byte)(n*37)).ToArray();
        int first = 0;
        foreach (int count in new[] {1,17,53})
        {
            var borrowed = expected.AsSpan(first*format.BlockAlign,count*format.BlockAlign).ToArray();
            backend.Last!.Emit(borrowed,new(first,count,(long)first*TimeSpan.TicksPerSecond/format.SampleRate));
            Array.Fill(borrowed,(byte)0xCC); first += count;
        }
        backend.Last!.Finish(); var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.Complete,report.Integrity); Assert.Equal(expected,sink.Bytes);
        Assert.Equal(71,report.Statistics.ReceivedFrames); Assert.Equal(71,report.Statistics.WrittenFrames);
        long position = 0;
        while (sink.Writes.Reader.TryRead(out var chunk))
        {
            Assert.Equal(position,chunk.Packet.FirstFrame); Assert.InRange(chunk.Packet.FrameCount,1,8);
            Assert.Equal(chunk.Packet.FrameCount*format.BlockAlign,chunk.Data.Length); position += chunk.Packet.FrameCount;
        }
        Assert.Equal(71,position); Assert.Equal(0,report.Statistics.LeasedBuffers);
    }

    [Fact]
    public async Task ArchiveOverflow_StopsAndDrainsAlreadyAcceptedPcmWithExplicitLoss()
    {
        var backend = new ManualBackend(); var sink = new MemorySink { BlockWrites = true };
        await using var controller = CaptureSessionTests.Create(backend,new() { ArchiveQueueCapacity = 1, MaximumChunkFrames = 1 });
        await controller.StartAsync(sink).WaitAsync(Timeout);
        backend.Last!.Emit([1,2],new(0,1,0)); await sink.Entered.Task.WaitAsync(Timeout);
        try
        {
            backend.Last.Emit([3,4],new(1,1,200)); backend.Last.Emit([5,6],new(2,1,400));
            Assert.False(controller.Completion!.IsCompleted);
        }
        finally { sink.Release.TrySetResult(); }
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.ArchiveOverflow,report.Reason); Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);
        Assert.Equal(new byte[] {1,2,3,4},sink.Bytes); Assert.True(report.SinkFinalized);
        Assert.Equal(3,report.Statistics.ReceivedFrames); Assert.Equal(2,report.Statistics.AcceptedArchiveFrames);
        Assert.Equal(2,report.Statistics.WrittenFrames); Assert.Equal(0,report.Statistics.LeasedBuffers);
        var issue = Assert.Single(report.Diagnostics,d => d.Code == "ARCHIVE_OVERRUN");
        Assert.Equal(2,issue.FirstFrame); Assert.Equal(1,issue.FrameCount);
    }

    [Fact]
    public async Task SlowAnalysis_DropsOnlyDisplayDataAndCannotConsumeArchiveBuffers()
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); var analysis = new MemorySink { BlockWrites = true };
        await using var controller = CaptureSessionTests.Create(backend,new() { ArchiveQueueCapacity = 2, AnalysisQueueCapacity = 1, MaximumChunkFrames = 1 });
        await controller.StartAsync(sink,analysis).WaitAsync(Timeout);
        backend.Last!.Emit([0,1],new(0,1,0)); await analysis.Entered.Task.WaitAsync(Timeout); await sink.NextWrite();
        try
        {
            for(int i=1;i<=100;i++) { backend.Last.Emit([(byte)i,1],new(i,1,i*250)); await sink.NextWrite(); }
            Assert.True(controller.Statistics!.AnalysisDroppedFrames > 0);
        }
        finally { analysis.Release.TrySetResult(); }
        var report = await controller.StopAsync().WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.Complete,report!.Integrity); Assert.Equal(101,report.Statistics.WrittenFrames);
        Assert.Equal(101,report.Statistics.AnalyzedFrames+report.Statistics.AnalysisDroppedFrames);
        Assert.Equal((2+2+1+2)*2,report.Statistics.AllocatedBufferBytes);
        Assert.Equal(0,report.Statistics.LeasedBuffers);
        Assert.InRange(report.Statistics.PeakLeasedBuffersUpperBound,1,7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisFailure_IsVisibleAndDoesNotCorruptArchive(bool recording)
    {
        var backend = new ManualBackend(); var sink = recording ? new MemorySink() : null;
        var analysis = new MemorySink { Failure = "write" }; await using var controller = CaptureSessionTests.Create(backend);
        await controller.StartAsync(sink,analysis).WaitAsync(Timeout);
        backend.Last!.Emit([1,2],new(0,1,0)); await analysis.Entered.Task.WaitAsync(Timeout);
        if (recording) backend.Last.Finish();
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Contains(report.Diagnostics,d => d.Code == "ANALYSIS_FAILED");
        Assert.Equal(recording ? CaptureIntegrity.Complete : CaptureIntegrity.NotRecorded,report.Integrity);
        Assert.Equal(recording ? CaptureSessionState.Completed : CaptureSessionState.Failed,controller.State);
        Assert.Equal(recording ? 1 : 0,report.Statistics.WrittenFrames);
    }

    [Fact]
    public async Task SinkWriteFailure_FinalizesPartialResultAndReleasesQueuedBuffers()
    {
        var backend = new ManualBackend(); var sink = new MemorySink { Failure = "write" };
        await using var controller = CaptureSessionTests.Create(backend);
        await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Emit([1,2,3,4],new(0,2,0));
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.SinkError,report.Reason); Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);
        Assert.Equal(CaptureIntegrity.Incomplete,sink.Completion!.Integrity); Assert.Equal(0,report.Statistics.WrittenFrames);
        Assert.Contains(report.Diagnostics,d => d.Code == "SINK_WRITE_FAILED"); Assert.Equal(0,report.Statistics.LeasedBuffers);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task DeviceDiscontinuity_DistinguishesKnownAndUnknownMissingFrameCount(int lost)
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); await using var controller = CaptureSessionTests.Create(backend);
        await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Emit([1,2],new(0,1,0));
        backend.Last.Emit([3,4],new(1+lost,1,500,AudioCapturePacketFlags.Discontinuity));
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.Discontinuity,report.Reason); Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);
        Assert.Equal(lost,report.Statistics.KnownMissingFrames); Assert.Equal(lost == 0,report.Statistics.HasUnknownLoss);
        Assert.Equal(new byte[] {1,2},sink.Bytes); Assert.Equal(1,report.Statistics.WrittenFrames);
        Assert.Equal(lost == 0 ? (long?)null : lost,Assert.Single(report.Diagnostics,d => d.Code == "DROPOUT").FrameCount);
    }

    [Theory]
    [InlineData(1,0,1,0)]
    [InlineData(2,0,0,0)]
    [InlineData(2,-1,1,0)]
    [InlineData(2,0,1,-1)]
    public async Task InvalidPacket_IsRejectedBeforeArchiving(int bytes, long frame, int count, long timestamp)
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); await using var controller = CaptureSessionTests.Create(backend);
        await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Emit(new byte[bytes],new(frame,count,timestamp));
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.InvalidPacket,report.Reason); Assert.Equal(0,report.Statistics.WrittenFrames);
    }

    [Fact]
    public async Task UnreliableTimestamps_AreReportedOnceWithoutInventingLostSamples()
    {
        var backend = new ManualBackend(); var analysis = new MemorySink(); await using var controller = CaptureSessionTests.Create(backend);
        await controller.StartAsync(analysis: analysis).WaitAsync(Timeout); Assert.Equal(CaptureSessionState.Monitoring,controller.State);
        backend.Last!.Emit([1,2],new(0,1,500,AudioCapturePacketFlags.TimestampUnreliable));
        backend.Last.Emit([3,4],new(1,1,100,AudioCapturePacketFlags.TimestampUnreliable)); backend.Last.Finish();
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.NotRecorded,report.Integrity); Assert.Equal(2,report.Statistics.AnalyzedFrames);
        Assert.Single(report.Diagnostics,d => d.Code == "TIMESTAMP_UNRELIABLE"); Assert.False(report.Statistics.HasUnknownLoss);
    }

    [Fact]
    public async Task UnreliableTimestamp_DoesNotReplaceTheLastReliableClockAnchor()
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); await using var controller = CaptureSessionTests.Create(backend);
        await controller.StartAsync(sink).WaitAsync(Timeout);
        backend.Last!.Emit([1,2],new(0,1,500));
        backend.Last.Emit([3,4],new(1,1,100,AudioCapturePacketFlags.TimestampUnreliable));
        backend.Last.Emit([5,6],new(2,1,200));
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.InvalidPacket,report.Reason);
        Assert.Contains(report.Diagnostics,d => d.Code == "TIMESTAMP_REVERSED");
        Assert.Equal(new byte[] {1,2,3,4},sink.Bytes);
    }

    [Fact]
    public async Task MemoryBudget_IsCheckedBeforeAllocatingPoolsAndDisposesConsumers()
    {
        var backend = new ManualBackend(); var sink = new MemorySink();
        await using var controller = CaptureSessionTests.Create(backend,new() { MaximumBufferBytes = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => controller.StartAsync(sink).WaitAsync(Timeout));
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(0,report.Statistics.AllocatedBufferBytes); Assert.Equal(0,sink.StartCalls); Assert.Equal(1,sink.DisposeCalls);
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8)]
    [InlineData(AudioSampleRepresentation.Signed16)]
    [InlineData(AudioSampleRepresentation.Signed24)]
    [InlineData(AudioSampleRepresentation.Signed32)]
    [InlineData(AudioSampleRepresentation.Float32)]
    public async Task SyntheticSource_IsIndependentOfCallbackPartitionAndHasKnownStereoVector(AudioSampleRepresentation representation)
    {
        byte[]? baseline = null;
        foreach (int[] pattern in new[] { new[] {1,7,13}, new[] {257} })
        {
            var backend = new SyntheticCaptureBackend(new() { FrameCount = 257, ChunkFramePattern = pattern },
                (_,channel) => channel == 0 ? -.5 : .25);
            var format = new AudioCaptureFormat(48000,2,representation); var sink = new MemorySink();
            await using var controller = new CaptureSession(backend,new(SyntheticCaptureBackend.DeviceId,format),
                new() { ArchiveQueueCapacity = 512, MaximumChunkFrames = 16 });
            await controller.StartAsync(sink).WaitAsync(Timeout); var report = await controller.Completion!.WaitAsync(Timeout);
            Assert.Equal(CaptureIntegrity.Complete,report.Integrity); Assert.Equal(257,report.Statistics.WrittenFrames);
            if (baseline == null) baseline = sink.Bytes; else Assert.Equal(baseline,sink.Bytes);
            byte[] vector = representation switch
            {
                AudioSampleRepresentation.Unsigned8 => [64,160],
                AudioSampleRepresentation.Signed16 => [0,192,0,32],
                AudioSampleRepresentation.Signed24 => [0,0,192,0,0,32],
                AudioSampleRepresentation.Signed32 => [0,0,0,192,0,0,0,32],
                _ => [0,0,0,191,0,0,128,62]
            };
            Assert.Equal(vector,sink.Bytes.AsSpan(0,format.BlockAlign).ToArray());
        }
    }

    [Fact]
    public async Task SyntheticInput_CanGenerateDeterministicPulsesDcNoiseAndChannelDelay()
    {
        const int frames = 4096;
        double Value(long frame, int channel)
        {
            long shifted = Math.Max(0,frame-channel*3);
            double noise = ((shifted*1103515245+12345)&0xFFFF)/65535.0-.5;
            return (shifted%48 < 24 ? .6 : -.6) + .1 + .01*noise;
        }
        var backend = new SyntheticCaptureBackend(new() { FrameCount = frames, ChunkFramePattern = [512] },Value);
        var sink = new MemorySink(); var format = new AudioCaptureFormat(96000,2,AudioSampleRepresentation.Signed24);
        await using var controller = new CaptureSession(backend,new(SyntheticCaptureBackend.DeviceId,format));
        await controller.StartAsync(sink).WaitAsync(Timeout); var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.Complete,report.Integrity);
        var data = sink.Bytes;
        for(int frame=3;frame<frames;frame++)
            Assert.Equal(data.AsSpan((frame-3)*6,3).ToArray(),data.AsSpan(frame*6+3,3).ToArray());
    }

    [Fact]
    public async Task ThirtyMinuteSyntheticTransfer_HasFixedBufferFootprintAndMatchingPcmHash()
    {
        // Accelerated 30 minutes at 48 kHz: 86,400,000 frames / 172,800,000 PCM bytes.
        const int packets = 30000, frames = 2880;
        var backend = new ManualBackend(); var sink = new HashSink();
        await using var controller = CaptureSessionTests.Create(backend,new() { ArchiveQueueCapacity = 2, MaximumChunkFrames = frames });
        await controller.StartAsync(sink).WaitAsync(Timeout);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] data = new byte[frames*2];
        for(int packet=0;packet<packets;packet++)
        {
            for(int i=0;i<data.Length;i++) data[i] = (byte)(packet+i);
            expected.AppendData(data); backend.Last!.Emit(data,new((long)packet*frames,frames,(long)packet*frames*TimeSpan.TicksPerSecond/48000));
            await sink.Written.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        }
        var report = await controller.StopAsync().WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.Complete,report!.Integrity); Assert.Equal((long)packets*frames,report.Statistics.WrittenFrames);
        Assert.Equal(expected.GetHashAndReset(),sink.Digest); Assert.Equal((2+2)*frames*2,report.Statistics.AllocatedBufferBytes);
        Assert.Equal(0,report.Statistics.LeasedBuffers); Assert.InRange(report.Statistics.PeakLeasedBuffersUpperBound,1,4);
    }

    [Fact]
    public async Task TimestampReversalAndOverlappingFrames_AreNeverSilentlyAccepted()
    {
        foreach(bool timestamp in new[] {true,false})
        {
            var backend = new ManualBackend(); var sink = new MemorySink(); await using var controller = CaptureSessionTests.Create(backend);
            await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Emit([1,2],new(0,1,500));
            backend.Last.Emit([3,4],timestamp ? new(1,1,100) : new(0,1,600));
            var report = await controller.Completion!.WaitAsync(Timeout);
            Assert.Equal(CaptureEndReason.InvalidPacket,report.Reason); Assert.Equal(new byte[] {1,2},sink.Bytes);
        }
    }

    [Fact]
    public async Task ExplicitSilence_IsPreservedWithSourceFlagAndIsNotInventedDropoutRepair()
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); await using var controller = CaptureSessionTests.Create(backend);
        await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Emit(new byte[4],new(0,2,0,AudioCapturePacketFlags.Silence));
        var report = await controller.StopAsync().WaitAsync(Timeout); var packet = await sink.NextWrite();
        Assert.Equal(AudioCapturePacketFlags.Silence,packet.Packet.Flags); Assert.Equal(new byte[4],sink.Bytes);
        Assert.Equal(CaptureIntegrity.Complete,report!.Integrity); Assert.False(report.Statistics.HasUnknownLoss);
    }

    private sealed class HashSink : IAudioRecordingSink
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal readonly Channel<bool> Written = Channel.CreateBounded<bool>(1);
        internal byte[]? Digest;
        public ValueTask StartAsync(AudioCaptureStreamInfo stream,CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ConsumeAsync(CapturedAudioChunk chunk,CancellationToken cancellationToken = default)
        { hash.AppendData(chunk.Data.Span); Written.Writer.TryWrite(true); return ValueTask.CompletedTask; }
        public ValueTask CompleteAsync(CaptureCompletion completion,CancellationToken cancellationToken = default)
        { Digest = hash.GetHashAndReset(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { hash.Dispose(); return ValueTask.CompletedTask; }
    }
}
