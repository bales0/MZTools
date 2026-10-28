namespace MZTools.Capture.Tests;

public class AudioRecorderViewModelTests
{
    private static readonly AudioCaptureRequest Request=new("manual",CaptureSessionTests.Format);
    [Theory]
    [InlineData(4)]
    [InlineData(20)]
    public async Task ReplacementPreservesOriginalUntilStopAndPublishesWholeNewSegmentFamily(int newBytes)
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        string path=folder.PathFor("replace.wav"); var options=new WaveRecordingOptions(path) { SegmentDataLimit=4 };
        await model.StartAsync(backend,Request,options); backend.Last!.Emit(new byte[12],new(0,6,0)); var old=await model.StopAsync();
        var originals=old!.Recording!.Segments.Select(s=>s.Path).Append(path+".json").ToDictionary(p=>p,File.ReadAllBytes);
        await model.StartAsync(backend,Request,options,overwrite:true);
        byte[] payload=Enumerable.Range(1,newBytes).Select(i=>(byte)i).ToArray(); backend.Last!.Emit(payload,new(0,newBytes/2,0));
        foreach(var original in originals) Assert.Equal(original.Value,File.ReadAllBytes(original.Key));
        var report=await model.StopAsync(); Assert.Equal(CaptureIntegrity.Complete,report!.Integrity);
        Assert.Equal(payload,report.Recording!.Segments.SelectMany(s=>File.ReadAllBytes(s.Path)[44..]).ToArray());
        Assert.Equal(path,report.Recording.Segments[0].Path); Assert.Equal(path+".json",report.Recording.SidecarPath);
        using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path+".json"));
        Assert.Equal(path,json.RootElement.GetProperty("recording").GetProperty("segments")[0].GetProperty("path").GetString());
        Assert.Single(model.Recordings);
        Assert.Equal(report.Recording.Segments.Count+1,Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }
    [Theory]
    [InlineData("cancel")]
    [InlineData("open-error")]
    [InlineData("device-loss")]
    [InlineData("changed-original")]
    [InlineData("new-collision")]
    public async Task AbortedReplacementNeverOverwritesOriginal(string action)
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        string path=folder.PathFor("replace.wav"); var options=new WaveRecordingOptions(path) { SegmentDataLimit=4 };
        await model.StartAsync(backend,Request,options); backend.Last!.Emit([1,2,3,4],new(0,2,0)); await model.StopAsync();
        byte[] original=File.ReadAllBytes(path),sidecar=File.ReadAllBytes(path+".json");
        if(action=="open-error")
        {
            backend.OpenFailure=true; await Assert.ThrowsAsync<IOException>(()=>model.StartAsync(backend,Request,options,overwrite:true));
        }
        else
        {
            await model.StartAsync(backend,Request,options,overwrite:true); backend.Last!.Emit(new byte[8],new(0,4,0));
            if(action=="cancel") await model.DiscardAsync();
            else
            {
                if(action=="changed-original") { original=[0xA5]; File.WriteAllBytes(path,original); }
                if(action=="new-collision") File.WriteAllBytes(folder.PathFor("replace_002.wav"),[0x5A]);
                if(action=="device-loss") backend.Last!.Finish(CaptureEndReason.DeviceLost);
                var failed=await model.StopAsync(); Assert.Equal(CaptureIntegrity.Incomplete,failed!.Integrity);
                Assert.All(failed.Recording!.Segments,s=>Assert.True(File.Exists(s.Path)));
                if(action=="new-collision") Assert.Equal(new byte[]{0x5A},File.ReadAllBytes(folder.PathFor("replace_002.wav")));
            }
        }
        Assert.Equal(original,File.ReadAllBytes(path)); Assert.Equal(sidecar,File.ReadAllBytes(path+".json"));
        if(action=="cancel") Assert.Equal(2,Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }
    [Fact]
    public async Task DiscardDeletesOnlyCurrentSegmentsAndSidecarAndKeepsEarlierRecording()
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        string saved=folder.PathFor("saved.wav"),cancelled=folder.PathFor("cancelled.wav");
        await model.StartAsync(backend,Request,new(saved)); backend.Last!.Emit([1,2,3,4],new(0,2,0));
        await model.StopAsync(); byte[] original=File.ReadAllBytes(saved),sidecar=File.ReadAllBytes(saved+".json");
        File.WriteAllText(folder.PathFor("cancelled-unrelated.wav"),"unrelated");
        await model.StartAsync(backend,Request,new(cancelled) { SegmentDataLimit=8 });
        backend.Last!.Emit(Enumerable.Range(0,20).Select(i=>(byte)i).ToArray(),new(0,10,0));
        var directory=Path.GetDirectoryName(cancelled)!;
        await model.DiscardAsync();
        Assert.Equal(CaptureSessionState.Idle,model.State); Assert.False(model.CanDiscard); Assert.False(model.IsBusy);
        Assert.Null(model.Live); Assert.Null(model.LatestReport); Assert.Null(model.Capture);
        Assert.Single(model.Recordings); Assert.Equal(saved,model.Recordings[0].Recording!.Segments.Single().Path);
        Assert.Equal(original,File.ReadAllBytes(saved)); Assert.Equal(sidecar,File.ReadAllBytes(saved+".json"));
        Assert.Equal(new[]{"cancelled-unrelated.wav","saved.wav","saved.wav.json"},Directory.GetFiles(directory).Select(Path.GetFileName).Order());
        await model.StartAsync(backend,Request); await model.StopAsync(); Assert.Single(model.Recordings);
    }
    [Fact]
    public async Task MonitorDiscardCreatesNoFilesAndCannotDiscardAnAlreadySavedRun()
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        await model.StartAsync(backend,Request); await model.DiscardAsync();
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(folder.PathFor("unused.wav"))!));
        Assert.Equal(1,backend.Last!.DisposeCalls); Assert.Null(model.LatestReport);
        string saved=folder.PathFor("saved.wav"); await model.StartAsync(backend,Request,new(saved));
        backend.Last!.Emit([1,2],new(0,1,0)); await model.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>model.DiscardAsync()); Assert.True(File.Exists(saved));
    }
    [Fact]
    public async Task DiscardDoesNotEvictEarlierEntriesFromFullRecordingHistory()
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        for(int i=0;i<16;i++)
        {
            await model.StartAsync(backend,Request,new(folder.PathFor($"saved-{i}.wav")));
            backend.Last!.Emit([1,2],new(0,1,0)); await model.StopAsync();
        }
        var previous=model.Recordings.ToArray();
        await model.StartAsync(backend,Request,new(folder.PathFor("discard.wav")));
        backend.Last!.Emit([3,4],new(0,1,0)); await model.DiscardAsync();
        Assert.Equal(previous,model.Recordings);
        Assert.All(previous,r=>Assert.True(File.Exists(r.Recording!.Segments.Single().Path)));
    }
    [Fact]
    public async Task DiscardPublicationCollisionDeletesOwnedPartialButKeepsForeignDestination()
    {
        using var folder=new RecordingTestFolder(); var backend=new ManualBackend(); await using var model=new AudioRecorderViewModel();
        string target=folder.PathFor("collision.wav"); await model.StartAsync(backend,Request,new(target));
        backend.Last!.Emit([1,2,3,4],new(0,2,0)); File.WriteAllBytes(target,[0xA5]);
        await model.DiscardAsync();
        Assert.Equal(new byte[]{0xA5},File.ReadAllBytes(target)); Assert.Empty(model.Recordings);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }
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
