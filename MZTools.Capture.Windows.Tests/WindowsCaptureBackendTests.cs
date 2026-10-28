using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MZTools.Capture.Windows.Tests;

public class WindowsCaptureBackendTests
{
    private static readonly AudioCaptureFormat Format = new(48000,1,AudioSampleRepresentation.Signed16);
    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReplacementRollback_RestoresOriginalWaveIfSidecarCannotBeMoved()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mztools-replace-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"original.wav"); var platform=new FakeWindowsPlatform(); await using var model=new AudioRecorderViewModel();
            var backend=new WindowsAudioCaptureBackend(platform); var request=new AudioCaptureRequest("input",Format);
            await model.StartAsync(backend,request,new(path)); platform.Last!.Emit([1,2,3,4],AudioClientBufferFlags.None,0,0); await model.StopAsync();
            byte[] wave=File.ReadAllBytes(path),json=File.ReadAllBytes(path+".json");
            await model.StartAsync(backend,request,new(path),overwrite:true); platform.Last!.Emit([5,6,7,8],AudioClientBufferFlags.None,0,0);
            using(var locked=new FileStream(path+".json",FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var report=await model.StopAsync(); Assert.Equal(CaptureIntegrity.Incomplete,report!.Integrity);
                Assert.Equal(wave,File.ReadAllBytes(path)); Assert.Equal(json,File.ReadAllBytes(path+".json"));
                Assert.All(report.Recording!.Segments,s=>Assert.True(File.Exists(s.Path)));
            }
        }
        finally { foreach(string path in Directory.GetFiles(directory)) File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task DiscardLockedSegment_RemainsRetryableAndCannotStartOverPendingDeletion()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mztools-discard-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"locked.wav"); var platform=new FakeWindowsPlatform();
            await using var model=new AudioRecorderViewModel();
            await model.StartAsync(new WindowsAudioCaptureBackend(platform),new("input",Format),new(path) { SegmentDataLimit=4 });
            platform.Last!.Emit([1,2,3,4,5,6,7,8],AudioClientBufferFlags.None,0,0);
            var deadline=DateTime.UtcNow.Add(Timeout);
            while(!File.Exists(path)) { if(DateTime.UtcNow>deadline) throw new TimeoutException(); await Task.Delay(10); }
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                await Assert.ThrowsAsync<IOException>(()=>model.DiscardAsync());
                Assert.True(model.HasPendingDiscard); Assert.True(model.CanDiscard); Assert.False(model.IsBusy);
                Assert.True(File.Exists(path)); Assert.Contains(path,model.Error!);
                await Assert.ThrowsAsync<InvalidOperationException>(()=>model.StartAsync(new WindowsAudioCaptureBackend(platform),new("input",Format)));
            }
            await model.DiscardAsync();
            Assert.False(model.HasPendingDiscard); Assert.False(model.CanDiscard); Assert.Null(model.Error);
            Assert.Empty(model.Recordings); Assert.Empty(Directory.GetFiles(directory));
        }
        finally { foreach(string path in Directory.GetFiles(directory)) File.Delete(path); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Signed16,1)]
    [InlineData(AudioSampleRepresentation.Signed24,2)]
    [InlineData(AudioSampleRepresentation.Float32,2)]
    public async Task EmptyCallbacks_DoNotStopCaptureRebasePositionsOrAlterPcm(AudioSampleRepresentation representation,int channels)
    {
        var format = new AudioCaptureFormat(48000,channels,representation);
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        var consumer = new PacketConsumer();
        await using var session = new CaptureSession(backend,new("input",format),new() { AnalysisQueueCapacity = 8 });
        await session.StartAsync(analysis:consumer).WaitAsync(Timeout);
        var transport = platform.Last!;
        transport.Emit([],AudioClientBufferFlags.None,-1,-1);
        byte[] first = Enumerable.Range(0,480*format.BlockAlign).Select(i => (byte)i).ToArray();
        byte[] second = Enumerable.Repeat((byte)0xA5,480*format.BlockAlign).ToArray();
        transport.Emit(first,AudioClientBufferFlags.None,1200,50000);
        transport.Emit([],AudioClientBufferFlags.Silent,-1,-1);
        transport.Emit(second,AudioClientBufferFlags.None,1680,150000);
        transport.Emit([],AudioClientBufferFlags.None,2160,250000);
        var report = await session.StopAsync().WaitAsync(Timeout);
        Assert.Equal(CaptureSessionState.Completed,session.State);
        Assert.Equal(960,report!.Statistics.AnalyzedFrames);
        Assert.Equal(0,report.Statistics.KnownMissingFrames);
        Assert.Equal(1200,report.Stream!.DeviceFrameOrigin);
        Assert.Equal(50000,report.Stream.FirstPacketTimestampTicks);
        Assert.Equal(first.Concat(second),consumer.Data);
        Assert.Equal(new long[] { 0,480 },consumer.Packets.Select(p => p.FirstFrame));
    }

    [Fact]
    public async Task EmptyCallback_DoesNotConcealGapBeforeNextRealPacket()
    {
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        await using var session = new CaptureSession(backend,new("input",Format));
        await session.StartAsync().WaitAsync(Timeout);
        platform.Last!.Emit([1,2],AudioClientBufferFlags.None,100,10000);
        platform.Last.Emit([],AudioClientBufferFlags.None,101,10208);
        platform.Last.Emit([3,4],AudioClientBufferFlags.None,104,11000);
        var report = await session.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.Discontinuity,report.Reason);
        Assert.Equal(3,report.Statistics.KnownMissingFrames);
    }

    [Theory]
    [InlineData(AudioClientBufferFlags.None)]
    [InlineData(AudioClientBufferFlags.Silent)]
    public async Task NonemptyPartialFrame_IsRejectedWithBufferAndFormatDiagnostics(AudioClientBufferFlags flags)
    {
        var format = new AudioCaptureFormat(96000,2,AudioSampleRepresentation.Signed24);
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        var consumer = new PacketConsumer();
        await using var session = new CaptureSession(backend,new("input",format));
        await session.StartAsync(analysis:consumer).WaitAsync(Timeout);
        platform.Last!.Emit([1,2,3,4,5,6,7],flags,100,10000);
        var report = await session.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.InvalidPacket,report.Reason);
        Assert.Empty(consumer.Data); Assert.Null(report.Stream!.DeviceFrameOrigin);
        Assert.Contains(report.Diagnostics,d => d.Message.Contains("buffer 7 bytes") &&
            d.Message.Contains("alignment 6 bytes") && d.Message.Contains("96000 Hz, 2 channel(s)") &&
            d.Message.Contains("Signed24, 24 valid bits") && d.Message.Contains($"flags {flags}"));
    }

    [Fact]
    public async Task EnumerationAndOpen_DoNotStartTheInputOrChangeDefaultDevices()
    {
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        var devices = await backend.EnumerateDevicesAsync(); Assert.Single(devices);
        await using var device = await backend.OpenAsync(new("input",Format));
        Assert.Equal(0,platform.Last!.Starts); Assert.True(platform.Options!.RequestRaw);
        Assert.False(platform.Options.AllowEngineFormatConversion); Assert.Equal("input",device.Info.Device.Id);
        Assert.Null(device.Info.Processing!.RawActive); Assert.Equal(WindowsAudioCaptureMode.Shared,platform.Options.Mode);
    }

    [Fact]
    public async Task PacketPositions_AreRebasedAndFirstDiscontinuityRemainsVisibleWithoutInventedLoss()
    {
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        var sink = new PacketConsumer(); await using var session = new CaptureSession(backend,new("input",Format),
            new() { MaximumChunkFrames = 1,AnalysisQueueCapacity = 8 });
        await session.StartAsync(analysis:sink).WaitAsync(Timeout);
        platform.Last!.Emit([1,2,3,4],AudioClientBufferFlags.DataDiscontinuity,1234,50000);
        platform.Last.Emit([5,6],AudioClientBufferFlags.None,1236,50416);
        var report = await session.StopAsync().WaitAsync(Timeout);
        Assert.Equal(CaptureIntegrity.NotRecorded,report!.Integrity); Assert.Equal(CaptureSessionState.Completed,session.State);
        Assert.Equal(3,report.Statistics.AnalyzedFrames); Assert.Equal(0,report.Statistics.KnownMissingFrames);
        Assert.Equal(1234,report.Stream!.DeviceFrameOrigin); Assert.Equal(50000,report.Stream.FirstPacketTimestampTicks);
        Assert.NotNull(report.Stream.FirstPacketReceivedUtc); Assert.True(report.Stream.Processing!.RawActive);
        Assert.Contains(report.Diagnostics,d => d.Code == "INITIAL_DISCONTINUITY");
        Assert.Equal(new long[] {0,1,2},sink.Packets.Select(p => p.FirstFrame));
        Assert.True(sink.Packets[0].Flags.HasFlag(AudioCapturePacketFlags.InitialDiscontinuity));
        Assert.All(sink.Packets.Skip(1),p => Assert.Equal(AudioCapturePacketFlags.None,p.Flags));
        Assert.Contains(report.Diagnostics,d => d.Code == "INITIAL_POSITION_OFFSET");
        Assert.Equal(1,platform.Last.Disposes);
    }

    [Fact]
    public async Task MidstreamDiscontinuity_IsNotHiddenByPositionRebasing()
    {
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        await using var session = new CaptureSession(backend,new("input",Format));
        await session.StartAsync().WaitAsync(Timeout);
        platform.Last!.Emit([1,2],AudioClientBufferFlags.None,100,10000);
        platform.Last.Emit([3,4],AudioClientBufferFlags.DataDiscontinuity,104,11000);
        var report = await session.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.Discontinuity,report.Reason); Assert.Equal(3,report.Statistics.KnownMissingFrames);
        Assert.Equal(CaptureSessionState.Failed,session.State); Assert.Contains(report.Diagnostics,d => d.Code == "DROPOUT");
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,1)]
    [InlineData(AudioSampleRepresentation.Unsigned8,2)]
    [InlineData(AudioSampleRepresentation.Signed16,2)]
    [InlineData(AudioSampleRepresentation.Signed24,2)]
    [InlineData(AudioSampleRepresentation.Float32,2)]
    public async Task NativeSilenceFlag_UsesCorrectPcmSilenceAndSplitsWithoutFrameLoss(AudioSampleRepresentation representation,int channels)
    {
        var format = new AudioCaptureFormat(48000,channels,representation);
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform,new() { SilenceBufferFrames = 2 });
        var consumer = new PacketConsumer(); await using var session = new CaptureSession(backend,new("input",format),new() { AnalysisQueueCapacity = 8 });
        await session.StartAsync(analysis:consumer).WaitAsync(Timeout);
        platform.Last!.Emit(Enumerable.Repeat((byte)0x55,5*format.BlockAlign).ToArray(),AudioClientBufferFlags.Silent,100,5000);
        var report = await session.StopAsync().WaitAsync(Timeout);
        Assert.Equal(5,report!.Statistics.AnalyzedFrames); Assert.Equal(new long[] {0,2,4},consumer.Packets.Select(p => p.FirstFrame));
        Assert.All(consumer.Data,b => Assert.Equal(representation == AudioSampleRepresentation.Unsigned8 ? 128 : 0,b));
        Assert.All(consumer.Packets,p => Assert.True(p.Flags.HasFlag(AudioCapturePacketFlags.Silence)));
    }

    [Fact]
    public async Task TimestampError_IsPreservedAndDoesNotTurnUndefinedTimeIntoTrustedEvidence()
    {
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        var consumer = new PacketConsumer(); await using var session = new CaptureSession(backend,new("input",Format));
        await session.StartAsync(analysis:consumer).WaitAsync(Timeout);
        platform.Last!.Emit([1,2],AudioClientBufferFlags.TimestampError,100,-1);
        var report = await session.StopAsync().WaitAsync(Timeout);
        Assert.Contains(report!.Diagnostics,d => d.Code == "TIMESTAMP_UNRELIABLE");
        Assert.True(Assert.Single(consumer.Packets).Flags.HasFlag(AudioCapturePacketFlags.TimestampUnreliable));
        Assert.Equal(0,consumer.Packets[0].TimestampTicks);
    }

    [Fact]
    public async Task DeviceInvalidated_StopsOnceAndReportsTheNativeFailure()
    {
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        await using var session = new CaptureSession(backend,new("input",Format)); await session.StartAsync().WaitAsync(Timeout);
        platform.Last!.End(new COMException("Input unplugged",unchecked((int)0x88890004)));
        var report = await session.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.DeviceLost,report.Reason); Assert.Equal(1,platform.Last.Disposes);
        Assert.Contains(report.Diagnostics,d => d.Code == "DEVICE_LOST" && d.Message.Contains("88890004",StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedRawStart_DoesNotSilentlyRetryInProcessedMode()
    {
        var platform = new FakeWindowsPlatform { StartFailure = new NotSupportedException("RAW unavailable") };
        var backend = new WindowsAudioCaptureBackend(platform); await using var session = new CaptureSession(backend,new("input",Format));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.StartAsync().WaitAsync(Timeout));
        var report = await session.Completion!.WaitAsync(Timeout);
        Assert.Equal(CaptureEndReason.StartError,report.Reason); Assert.Equal(1,platform.Opens); Assert.Equal(1,platform.Last!.Disposes);
        Assert.Null(report.Stream!.Processing!.RawActive);
    }

    [Fact]
    public async Task StopDuringNativeStart_WaitsForNativeCallAndThenCleansUp()
    {
        var platform = new FakeWindowsPlatform { BlockStart = true }; var backend = new WindowsAudioCaptureBackend(platform);
        await using var session = new CaptureSession(backend,new("input",Format));
        var start = session.StartAsync(); var transport = await platform.Opened.Task.WaitAsync(Timeout);
        await transport.StartEntered.Task.WaitAsync(Timeout); var stop = session.StopAsync();
        try { Assert.False(stop.IsCompleted); }
        finally { transport.StartRelease.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Timeout));
        var report = await stop.WaitAsync(Timeout); Assert.Equal(CaptureEndReason.Stopped,report!.Reason); Assert.Equal(1,transport.Disposes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedFormatChange_RequiresExplicitFallbackOrEngineConversion(bool engineConversion)
    {
        var mix = new AudioCaptureFormat(48000,2,AudioSampleRepresentation.Float32);
        var request = new AudioCaptureRequest("input",Format);
        var options = new WindowsAudioCaptureOptions { AllowEngineFormatConversion = engineConversion };
        if (engineConversion) Assert.Equal(Format,WasapiCapturePlatform.Negotiate(request,options,mix));
        else
        {
            Assert.Throws<NotSupportedException>(() => WasapiCapturePlatform.Negotiate(request,options,mix));
            Assert.Equal(mix,WasapiCapturePlatform.Negotiate(request with { AllowFormatFallback = true },options,mix));
        }
    }

    [Fact]
    public async Task ExclusiveMode_RequiresExactFormatAndNeverStartsDuringProbe()
    {
        var request = new AudioCaptureRequest("input",Format); var options = new WindowsAudioCaptureOptions { Mode = WindowsAudioCaptureMode.Exclusive };
        Assert.Contains(WindowsAudioCaptureMode.Exclusive,WindowsAudioCaptureBackend.SupportedModes);
        var fake = new FakeWindowsPlatform();
        var backend=new WindowsAudioCaptureBackend(fake,options); await using var session=await backend.OpenAsync(request);
        Assert.Equal(0,fake.Last!.Starts); Assert.Equal(WindowsAudioCaptureMode.Exclusive,fake.Options!.Mode);
        Assert.Equal(Format,WasapiCapturePlatform.Negotiate(request,options,null));
        Assert.Throws<ArgumentException>(()=>WasapiCapturePlatform.Negotiate(request with { AllowFormatFallback=true },options,null));
        Assert.Throws<ArgumentException>(()=>new WindowsAudioCaptureBackend(options with { AllowEngineFormatConversion=true }));
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,8)]
    [InlineData(AudioSampleRepresentation.Signed16,16)]
    [InlineData(AudioSampleRepresentation.Signed24,24)]
    [InlineData(AudioSampleRepresentation.Signed32,24)]
    [InlineData(AudioSampleRepresentation.Float32,32)]
    public void NAudioFormatMapping_PreservesActualContainerAndValidBits(AudioSampleRepresentation representation,int validBits)
    {
        var format = new AudioCaptureFormat(96000,2,representation,validBits);
        Assert.Equal(format,WasapiWaveFormats.FromNative(WasapiWaveFormats.ToNative(format)));
        Assert.Throws<NotSupportedException>(() => WasapiWaveFormats.FromNative(new WaveFormat(48000,16,6)));
    }
}

internal sealed class FakeWindowsPlatform : IWindowsCapturePlatform
{
    internal FakeWindowsTransport? Last;
    internal int Opens;
    internal bool BlockStart;
    internal Exception? StartFailure;
    internal WindowsAudioCaptureOptions? Options;
    internal readonly TaskCompletionSource<FakeWindowsTransport> Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<AudioCaptureDevice> Enumerate(WindowsAudioCaptureOptions options,CancellationToken token)
    { token.ThrowIfCancellationRequested(); return [new("input","Fake input","Fake WASAPI",[])]; }
    public IWindowsCaptureTransport Open(AudioCaptureRequest request,WindowsAudioCaptureOptions options,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Options = options; Opens++;
        Last = new(new(new("input","Fake input","Fake WASAPI",[]),request.Format,request.Format,"Test-only transport")
        { Processing = new("Fake",options.Mode.ToString(),options.RequestRaw,null,false,request.Format,"Unknown until Start") },this);
        Opened.TrySetResult(Last); return Last;
    }
}

internal sealed class FakeWindowsTransport(AudioCaptureStreamInfo info,FakeWindowsPlatform platform) : IWindowsCaptureTransport
{
    public AudioCaptureStreamInfo Info { get; private set; } = info;
    public event CaptureDataAvailableHandler? DataAvailable;
    public event Action<Exception?>? Stopped;
    internal readonly TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly ManualResetEventSlim StartRelease = new();
    internal int Starts,Disposes;
    public void Start()
    {
        Starts++; StartEntered.TrySetResult(); if (platform.BlockStart) StartRelease.Wait(TimeSpan.FromSeconds(10));
        if (platform.StartFailure != null) throw platform.StartFailure;
        Info = Info with { Processing = Info.Processing! with { RawActive = Info.Processing.RawRequested } };
    }
    internal void Emit(byte[] data,AudioClientBufferFlags flags,long frame,long timestamp) => DataAvailable?.Invoke(data,flags,frame,timestamp);
    internal void End(Exception? error) => Stopped?.Invoke(error);
    public ValueTask DisposeAsync()
    { Disposes++; Stopped?.Invoke(null); DataAvailable = null; Stopped = null; StartRelease.Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class PacketConsumer : IAudioChunkConsumer
{
    internal readonly List<AudioCapturePacket> Packets = [];
    internal readonly List<byte> Data = [];
    public ValueTask StartAsync(AudioCaptureStreamInfo stream,CancellationToken token = default) => ValueTask.CompletedTask;
    public ValueTask ConsumeAsync(CapturedAudioChunk chunk,CancellationToken token = default)
    { Packets.Add(chunk.Packet); Data.AddRange(chunk.Data.ToArray()); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
