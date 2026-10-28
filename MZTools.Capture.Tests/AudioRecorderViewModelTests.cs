namespace MZTools.Capture.Tests;

public class AudioRecorderViewModelTests
{
    private static readonly AudioCaptureRequest Request=new("manual",CaptureSessionTests.Format);
    [Fact]
    public async Task MonitorThenRecordThenMonitor_PreservesFinalizedRecordingAndExactPcm()
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        await model.StartAsync(backend,Request); Assert.Equal(CaptureSessionState.Monitoring,model.State);
        backend.Last!.Emit([1,2,3,4],new(0,2,0));
        string path=folder.PathFor("record.wav"); await model.StartAsync(backend,Request,new(path));
        Assert.Equal(CaptureSessionState.Recording,model.State); backend.Last!.Emit([5,6,7,8],new(0,2,0));
        var report=await model.StopAsync(); Assert.Equal(CaptureIntegrity.Complete,report!.Integrity);
        Assert.Equal(new byte[]{5,6,7,8},File.ReadAllBytes(path)[44..]); Assert.Single(model.Recordings);
        Assert.NotNull(await model.RefreshAsync(new()));
        await model.StartAsync(backend,Request); await model.StopAsync(); Assert.Single(model.Recordings); Assert.Equal(path,model.Recordings[0].Recording!.Segments[0].Path);
    }
    [Fact]
    public async Task PreviewOptionsFftAndClipReset_CannotChangeArchivedSamples()
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        string path=folder.PathFor("unchanged.wav"); await model.StartAsync(backend,Request,new(path));
        model.ConfigurePulses(new() { Reference=LivePulseTests.Reference });
        var expected=new List<byte>();
        for(int chunk=0;chunk<12;chunk++)
        {
            byte[] bytes=Enumerable.Range(0,1024).Select(i=>(byte)(i*37+chunk)).ToArray(); expected.AddRange(bytes);
            backend.Last!.Emit(bytes,new(chunk*512,512,chunk*10000));
            model.ResetClippingHold(.7);
            await model.RefreshAsync(new() { FftSize=1024,WaveSeconds=.001,TriggerEnabled=true,WaveBuckets=13,Alignment=new(),Pulses=new() });
        }
        var report=await model.StopAsync(); Assert.Equal(CaptureIntegrity.Complete,report!.Integrity);
        Assert.Equal(expected,File.ReadAllBytes(path)[44..]); Assert.True(report.Recording!.SourceHashCoversSavedFramesExactly);
        Assert.True(report.Recording.Segments.Single().PcmMatchesWrittenBytes);
    }
    [Fact]
    public async Task NewMonitor_DoesNotPresentPreviousRecordingAsCurrentCompletion()
    {
        var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        await model.StartAsync(backend,Request); await model.StopAsync(); Assert.NotNull(model.LatestReport);
        await model.StartAsync(backend,Request); Assert.Null(model.LatestReport); await model.StopAsync();
    }
    [Fact]
    public async Task DisposeDuringPendingOpen_CancelsAndCannotRestartOrLeakBackend()
    {
        var backend=new ManualBackend { HoldOpen=true }; var model=new AudioRecorderViewModel();
        var start=model.StartAsync(backend,Request); await backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first=model.DisposeAsync().AsTask(); var second=model.DisposeAsync().AsTask();
        await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>start); Assert.False(model.IsActive); Assert.False(model.IsBusy);
        await Assert.ThrowsAsync<ObjectDisposedException>(()=>model.StartAsync(backend,Request)); Assert.Null(backend.Last);
    }
    [Fact]
    public async Task ConcurrentStartIsRejected_StopDuringStartIsSafe()
    {
        var backend=new ManualBackend { HoldStart=true }; await using var model=new AudioRecorderViewModel();
        var start=model.StartAsync(backend,Request); var device=await backend.Created.Task; await device.StartEntered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>model.StartAsync(backend,Request));
        await model.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>start);
        Assert.Equal(1,device.DisposeCalls); Assert.False(model.IsActive);
    }
    [Fact]
    public async Task FailedStart_HasDiagnosticAndAllowsRetry()
    {
        var backend=new ManualBackend { OpenFailure=true }; await using var model=new AudioRecorderViewModel();
        await Assert.ThrowsAsync<IOException>(()=>model.StartAsync(backend,Request)); Assert.NotNull(model.Error);
        backend.OpenFailure=false; await model.StartAsync(backend,Request); Assert.Null(model.Error); await model.StopAsync();
    }
}
