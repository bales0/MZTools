using System.Threading.Channels;

namespace MZTools.Capture.Tests;

public class CaptureSessionTests
{
    internal static readonly AudioCaptureFormat Format = new(48000, 1, AudioSampleRepresentation.Signed16);
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    internal static CaptureSession Create(ManualBackend backend, CapturePipelineOptions? options = null, bool fallback = false) =>
        new(backend, new("manual", Format, fallback), options);

    [Fact]
    public async Task Lifecycle_RestartsWithFreshConsumersAndRepeatedStopReturnsSameReport()
    {
        var backend = new ManualBackend(); await using var controller = Create(backend);
        Assert.Equal(CaptureSessionState.Idle, controller.State);
        Assert.Null(await controller.StopAsync());
        for (int run = 0; run < 3; run++)
        {
            var sink = new MemorySink(); await controller.StartAsync(sink).WaitAsync(Timeout);
            Assert.Equal(CaptureSessionState.Recording, controller.State);
            var device = backend.Last!; device.Emit([1,2,3,4], new(0,2,0));
            var firstStop = controller.StopAsync(); var secondStop = controller.StopAsync();
            var report = await firstStop.WaitAsync(Timeout);
            Assert.Same(report, await secondStop.WaitAsync(Timeout));
            Assert.Equal(CaptureSessionState.Completed, controller.State);
            Assert.Equal(CaptureIntegrity.Complete, report!.Integrity);
            Assert.Equal(2, report.Statistics.WrittenFrames); Assert.True(report.SinkFinalized);
            Assert.Equal(new byte[] {1,2,3,4}, sink.Bytes);
            Assert.Equal(1, sink.FinalizeCalls); Assert.Equal(1, sink.DisposeCalls);
            Assert.Equal(1, device.DisposeCalls); Assert.Equal(0, device.DataSubscribers);
            Assert.Equal(0, report.Statistics.LeasedBuffers);
            Assert.True(report.EndUtc >= report.StartUtc); Assert.True(report.Elapsed >= TimeSpan.Zero);
        }
        Assert.Equal(3, backend.OpenCalls);
    }

    [Fact]
    public async Task CancelDuringOpen_ReleasesTransferredConsumersWithoutStartingThem()
    {
        var backend = new ManualBackend { HoldOpen = true }; var sink = new MemorySink();
        await using var controller = Create(backend); using var cancellation = new CancellationTokenSource();
        Task start = controller.StartAsync(sink, cancellationToken: cancellation.Token);
        await backend.OpenEntered.Task.WaitAsync(Timeout); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Timeout));
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.Cancelled, report.Reason); Assert.Equal(CaptureSessionState.Completed, controller.State);
        Assert.Equal(CaptureIntegrity.NotRecorded, report.Integrity);
        Assert.Equal(0, sink.StartCalls); Assert.Equal(0, sink.FinalizeCalls); Assert.Equal(1, sink.DisposeCalls);
    }

    [Fact]
    public async Task StopDuringStart_CancelsDeviceStartAndFinalizesOnce()
    {
        var backend = new ManualBackend { HoldStart = true }; var sink = new MemorySink();
        await using var controller = Create(backend); Task start = controller.StartAsync(sink);
        await backend.OpenEntered.Task.WaitAsync(Timeout);
        var device = await backend.Created.Task.WaitAsync(Timeout);
        await device.StartEntered.Task.WaitAsync(Timeout);
        var stopping = controller.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Timeout));
        var report = await stopping.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.Stopped, report!.Reason); Assert.Equal(1, sink.FinalizeCalls);
        Assert.Equal(1, device.StopCalls); Assert.Equal(1, device.DisposeCalls);
    }

    [Fact]
    public async Task CancellationAfterStart_StopsWithoutDiscardingAcceptedFrames()
    {
        var backend = new ManualBackend(); var sink = new MemorySink();
        using var cancellation = new CancellationTokenSource(); await using var controller = Create(backend);
        await controller.StartAsync(sink, cancellationToken: cancellation.Token).WaitAsync(Timeout);
        backend.Last!.Emit([7,8], new(0,1,0)); cancellation.Cancel();
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.Cancelled, report.Reason); Assert.Equal(1, report.Statistics.WrittenFrames);
        Assert.Equal(CaptureIntegrity.Complete, report.Integrity);
    }

    [Fact]
    public async Task RejectedConcurrentStart_DoesNotTakeOwnershipOfAnotherSink()
    {
        var backend = new ManualBackend(); await using var controller = Create(backend);
        await controller.StartAsync(new MemorySink()).WaitAsync(Timeout); var rejected = new MemorySink();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(rejected));
        Assert.Equal(0, rejected.DisposeCalls); await controller.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task DisposeWhileStarting_IsAwaitableAndIdempotent()
    {
        var backend = new ManualBackend { HoldOpen = true }; var controller = Create(backend);
        Task start = controller.StartAsync(); await backend.OpenEntered.Task.WaitAsync(Timeout);
        var a = controller.DisposeAsync().AsTask(); var b = controller.DisposeAsync().AsTask();
        await Task.WhenAll(a,b).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Timeout));
        Assert.Equal(CaptureSessionState.Disposed, controller.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.StartAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegotiatedFormat_RequiresExplicitFallbackPermission(bool allowed)
    {
        var backend = new ManualBackend { ActualFormat = new(96000,2,AudioSampleRepresentation.Float32) };
        var sink = new MemorySink(); await using var controller = Create(backend, fallback: allowed);
        if (allowed)
        {
            await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Finish();
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(sink).WaitAsync(Timeout));
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(backend.ActualFormat, report.Stream!.ActualFormat);
        Assert.Equal(allowed ? CaptureEndReason.SourceCompleted : CaptureEndReason.FormatMismatch, report.Reason);
        Assert.Equal(allowed ? 1 : 0, sink.StartCalls);
    }

    [Theory]
    [InlineData("start", "CAPTURE_START_FAILED")]
    [InlineData("finalize", "SINK_FINALIZE_FAILED")]
    [InlineData("dispose", "SINK_DISPOSE_FAILED")]
    public async Task SinkLifecycleFailure_IsReportedAndAllResourcesReleased(string failure, string diagnostic)
    {
        var backend = new ManualBackend(); var sink = new MemorySink { Failure = failure };
        await using var controller = Create(backend);
        if (failure == "start")
            await Assert.ThrowsAsync<IOException>(() => controller.StartAsync(sink).WaitAsync(Timeout));
        else { await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Finish(); }
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureSessionState.Failed, controller.State); Assert.Equal(CaptureIntegrity.Incomplete, report.Integrity);
        Assert.Contains(report.Diagnostics, d => d.Code == diagnostic);
        Assert.Equal(1, sink.DisposeCalls); Assert.Equal(1, backend.Last!.DisposeCalls);
        Assert.Equal(0, report.Statistics.LeasedBuffers);
    }

    [Fact]
    public async Task DeviceDisconnect_IsDistinctFromSuccessfulCompletion()
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); await using var controller = Create(backend);
        await controller.StartAsync(sink).WaitAsync(Timeout); backend.Last!.Emit([1,2],new(0,1,0));
        backend.Last.Finish(CaptureEndReason.DeviceLost);
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.DeviceLost, report.Reason); Assert.Equal(CaptureIntegrity.Incomplete, report.Integrity);
        Assert.Contains(report.Diagnostics,d => d.Code == "DEVICE_LOST"); Assert.True(report.SinkFinalized);
    }

    [Fact]
    public async Task StopWaitCancellation_DoesNotAbortFinalization()
    {
        var backend = new ManualBackend(); var sink = new MemorySink { BlockWrites = true };
        await using var controller = Create(backend); await controller.StartAsync(sink).WaitAsync(Timeout);
        backend.Last!.Emit([1,2],new(0,1,0)); await sink.Entered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        Task stopping = controller.StopAsync(cancellation.Token); cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(Timeout)); }
        finally { sink.Release.TrySetResult(); }
        var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.True(report.SinkFinalized); Assert.Equal(1, report.Statistics.WrittenFrames);
    }

    [Fact]
    public async Task BackendLateCallback_AfterDisposeCannotTouchReleasedPcm()
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); var controller = Create(backend);
        await controller.StartAsync(sink).WaitAsync(Timeout);
        var lateCallback = backend.Last!.CallbackSnapshot();
        backend.Last.Emit([1,2],new(0,1,0)); await controller.DisposeAsync().AsTask().WaitAsync(Timeout);
        lateCallback!([3,4],new(1,1,100));
        Assert.Equal(1, controller.Statistics!.ReceivedFrames); Assert.Equal(0, controller.Statistics.LeasedBuffers);
        Assert.Throws<ObjectDisposedException>(() => sink.LastChunk!.Data);
    }

    [Fact]
    public async Task DiagnosticStorage_IsBoundedAndReportsOmissions()
    {
        var backend = new ManualBackend { ErrorsOnStop = 10 }; await using var controller = Create(backend, new() { MaximumDiagnostics = 2 });
        await controller.StartAsync().WaitAsync(Timeout); var report = await controller.StopAsync().WaitAsync(Timeout);
        Assert.Equal(2, report!.Diagnostics.Count); Assert.Equal(8, report.OmittedDiagnostics);
        Assert.Equal(CaptureEndReason.BackendError, report.Reason);
    }

    [Fact]
    public void PortableAssembly_HasNoApplicationWindowsOrAudioPackageDependency()
    {
        var assembly = typeof(CaptureSession).Assembly;
        Assert.Equal(".NETCoreApp,Version=v10.0", assembly.GetCustomAttributesData()
            .Single(a => a.AttributeType.Name == "TargetFrameworkAttribute").ConstructorArguments[0].Value);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name is "MZTools" ||
            a.Name!.StartsWith("NAudio",StringComparison.Ordinal) || a.Name.StartsWith("Presentation",StringComparison.Ordinal) ||
            a.Name.StartsWith("Microsoft.Win32",StringComparison.Ordinal) || a.Name == "WindowsBase");
    }

    [Fact]
    public async Task StartupFailure_ReleasesConsumersAndControllerCanRecover()
    {
        var backend = new ManualBackend { OpenFailure = true }; var first = new MemorySink();
        await using var controller = Create(backend);
        await Assert.ThrowsAsync<IOException>(() => controller.StartAsync(first).WaitAsync(Timeout));
        var failed = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.StartError, failed.Reason); Assert.Equal(1,first.DisposeCalls);
        backend.OpenFailure = false; var second = new MemorySink();
        await controller.StartAsync(second).WaitAsync(Timeout); backend.Last!.Finish();
        var recovered = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureSessionState.Completed,controller.State); Assert.Empty(recovered.Diagnostics);
    }

    [Fact]
    public async Task PreCancelledStart_DoesNotOpenDeviceOrTakeSinkOwnership()
    {
        var backend = new ManualBackend(); var sink = new MemorySink(); await using var controller = Create(backend);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.StartAsync(sink,cancellationToken:cancellation.Token));
        Assert.Equal(CaptureSessionState.Idle,controller.State); Assert.Equal(0,backend.OpenCalls); Assert.Equal(0,sink.DisposeCalls);
    }

    [Fact]
    public async Task Snapshots_ExposeActualFormatAndWarningsBeforeStopWithoutChangingPriorSnapshots()
    {
        var backend = new ManualBackend(); await using var controller = Create(backend);
        var idle = controller.Snapshot; Assert.Equal(CaptureSessionState.Idle,idle.State); Assert.Null(idle.Stream);
        await controller.StartAsync().WaitAsync(Timeout);
        backend.Last!.Emit([1,2],new(0,1,0,AudioCapturePacketFlags.TimestampUnreliable));
        var live = controller.Snapshot;
        Assert.Equal(CaptureSessionState.Monitoring,live.State); Assert.Equal(Format,live.Stream!.ActualFormat);
        Assert.Single(live.Diagnostics); Assert.Equal(1,live.Statistics.ReceivedFrames);
        backend.Last.Emit([3,4],new(1,1,200)); await controller.StopAsync().WaitAsync(Timeout);
        Assert.Equal(1,live.Statistics.ReceivedFrames); Assert.Equal(2,controller.Snapshot.Statistics.ReceivedFrames);
    }

    [Fact]
    public async Task SyntheticBackend_IsExplicitAndCanBeStoppedWhileStarting()
    {
        var backend = new SyntheticCaptureBackend(new() { StartDelay = TimeSpan.FromMinutes(1) });
        Assert.Single(await backend.EnumerateDevicesAsync());
        await using var device = await backend.OpenAsync(new(SyntheticCaptureBackend.DeviceId,Format));
        int packets = 0; device.DataAvailable += (_,_) => Interlocked.Increment(ref packets);
        Assert.Equal(0,packets);
        var starting = device.StartAsync().AsTask();
        await device.StopAsync().AsTask().WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Timeout));
        Assert.Equal(0,packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticBackend_FaultInjectionHasExactFrameEvidence(bool disconnect)
    {
        var options = new SyntheticCaptureOptions { FrameCount = 100, ChunkFramePattern = [32],
            DisconnectAtFrame = disconnect ? 17 : null, DiscontinuityAtFrame = disconnect ? null : 17, MissingFrames = 9 };
        var backend = new SyntheticCaptureBackend(options); var sink = new MemorySink();
        await using var controller = new CaptureSession(backend,new(SyntheticCaptureBackend.DeviceId,Format));
        await controller.StartAsync(sink).WaitAsync(Timeout); var report = await controller.Completion!.WaitAsync(Timeout);
        Assert.Equal(disconnect ? CaptureEndReason.DeviceLost : CaptureEndReason.Discontinuity,report.Reason);
        Assert.Equal(17,report.Statistics.WrittenFrames); Assert.Equal(disconnect ? 0 : 9,report.Statistics.KnownMissingFrames);
        Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);
    }
}

internal sealed record RecordedPacket(AudioCapturePacket Packet, byte[] Data);

internal sealed class MemorySink : IAudioRecordingSink
{
    private readonly List<byte> bytes = [];
    internal byte[] Bytes => bytes.ToArray();
    internal readonly Channel<RecordedPacket> Writes = Channel.CreateUnbounded<RecordedPacket>();
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool BlockWrites;
    internal string? Failure;
    internal int StartCalls, FinalizeCalls, DisposeCalls;
    internal CaptureCompletion? Completion;
    internal CapturedAudioChunk? LastChunk;
    public ValueTask StartAsync(AudioCaptureStreamInfo stream, CancellationToken cancellationToken = default)
    { StartCalls++; if (Failure == "start") throw new IOException("Injected sink start failure."); return ValueTask.CompletedTask; }
    public async ValueTask ConsumeAsync(CapturedAudioChunk chunk, CancellationToken cancellationToken = default)
    {
        LastChunk = chunk; Entered.TrySetResult();
        if (BlockWrites) await Release.Task.WaitAsync(cancellationToken);
        if (Failure == "write") throw new IOException("Injected sink write failure.");
        var copy = chunk.Data.ToArray(); bytes.AddRange(copy); Writes.Writer.TryWrite(new(chunk.Packet,copy));
    }
    internal async Task<RecordedPacket> NextWrite() => await Writes.Reader.ReadAsync().AsTask().WaitAsync(CaptureSessionTests.Timeout);
    public ValueTask CompleteAsync(CaptureCompletion completion, CancellationToken cancellationToken = default)
    { FinalizeCalls++; Completion = completion; if (Failure == "finalize") throw new IOException("Injected finalize failure."); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync()
    { DisposeCalls++; if (Failure == "dispose") throw new IOException("Injected sink dispose failure."); return ValueTask.CompletedTask; }
}

internal sealed class ManualBackend : IAudioCaptureBackend
{
    internal readonly TaskCompletionSource OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<ManualStream> Created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HoldOpen, HoldStart, OpenFailure;
    internal int OpenCalls, ErrorsOnStop;
    internal AudioCaptureFormat? ActualFormat;
    internal ManualStream? Last;
    public ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AudioCaptureDevice>>([]);
    public async ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
    {
        OpenCalls++; OpenEntered.TrySetResult();
        if (OpenFailure) throw new IOException("Injected input open failure.");
        if (HoldOpen) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan,cancellationToken);
        Last = new(request, this); Created.TrySetResult(Last); return Last;
    }
    internal sealed class ManualStream(AudioCaptureRequest request, ManualBackend backend) : IAudioCaptureSession
    {
        public AudioCaptureStreamInfo Info { get; } = new(new("manual","Manual test input","Test",[]), request.Format,
            backend.ActualFormat ?? request.Format, "Test source");
        public event AudioCaptureDataAvailable? DataAvailable;
        public event Action<AudioCaptureStopped>? Stopped;
        internal readonly TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StopCalls, DisposeCalls;
        internal int DataSubscribers => DataAvailable?.GetInvocationList().Length ?? 0;
        internal AudioCaptureDataAvailable? CallbackSnapshot() => DataAvailable;
        internal void Emit(byte[] data, AudioCapturePacket packet) => DataAvailable?.Invoke(data,packet);
        internal void Finish(CaptureEndReason reason = CaptureEndReason.SourceCompleted) => Stopped?.Invoke(new(reason));
        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        { StartEntered.TrySetResult(); if (backend.HoldStart) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan,cancellationToken); }
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        { StopCalls++; for(int i=0;i<backend.ErrorsOnStop;i++) Finish(CaptureEndReason.BackendError); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
