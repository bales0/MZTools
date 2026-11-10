using System.Text;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

public sealed class DeepAnalysisInputTests
{
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    private static AudioRecoveryOptions Options=>new(){ZeroCrossing=true,Schmitt=false,Normal=true,Inverted=false,ThresholdScales=[1]};
    private static string Stereo(AudioDocumentTests files,string name,Func<int,(short L,short R)> samples,int frames,int rate=48000)
    {
        string path=files.FilePath(name);using var w=new BinaryWriter(File.Create(path),Encoding.ASCII);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+frames*4);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)2);w.Write(rate);w.Write(rate*4);w.Write((short)4);w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(frames*4);
        for(int i=0;i<frames;i++){var (l,r)=samples(i);w.Write(l);w.Write(r);}return path;
    }
    [Theory][InlineData(0,1)][InlineData(1,1)][InlineData(2,2)][InlineData(3,1)]
    public async Task MeasurementUsesChosenChannelsAndKeepsNormalIndependent(int mode,int count)
    {
        using var files=new AudioDocumentTests();var normal=await files.Open();using var deep=normal.CreateSelectedPreview(100,1000);
        deep.ChannelMode=mode;deep.RightShift=mode==0?"0":"3";
        Assert.True(await deep.ApplyTransformKeepingViewportAsync(Token));Assert.True(await deep.MeasureAsync(Token));
        Assert.Equal(count,deep.DeepMeasurements.Count);
        Assert.Equal(mode==1?2:1,deep.DeepMeasurements[0].Channel);
        if(mode==2)Assert.Equal(2,deep.DeepMeasurements[1].Channel);
        Assert.Equal(mode==3,deep.EffectiveTransform.Mix);Assert.Equal("0",normal.RightShift);Assert.False(normal.IsDeepAnalysis);Assert.Null(normal.Recovery);
        Assert.Equal(new[]{"Overlay"},normal.DisplayChannels);normal.DisplayChannelMode=1;Assert.Equal(2,normal.ChannelMode);Assert.Contains("Overlay L/R",normal.AnalysisInputStatus);
    }
    [Theory][InlineData(-3)][InlineData(3)]
    public async Task AverageUsesActualAlignedSamplesAndCropsWithoutWrap(int offset)
    {
        using var files=new AudioDocumentTests();string path=Stereo(files,"edges.wav",i=>((short)(32000-i*3),(short)(31000-i*7)),40);
        var normal=await files.Open(path);using var deep=normal.CreateSelectedPreview(0,40);deep.ChannelMode=3;deep.RightShift=offset.ToString();
        Assert.True(await deep.ApplyTransformKeepingViewportAsync(Token));
        long start=Math.Max(0,offset),end=Math.Min(40,40+offset);deep.ShowRange(start,end);await deep.RefreshViewportAsync(cancellation:Token);
        var buckets=deep.DisplayWaveform.VisibleBuckets(start,end,100);Assert.Equal(end-start,buckets.Count);
        foreach(var b in buckets){int n=(int)b.StartSample;double expected=((32000-n*3)+(31000-(n-offset)*7))/2d/32768d;Assert.Equal(expected,b.LeftMin);Assert.Equal(b.LeftMin,b.RightMin);}
        Assert.Equal(start,buckets[0].StartSample);Assert.Equal(end,buckets[^1].EndSample);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task DecoderUsesRealChosenSignalWithoutCrossChannelAssembly(int mode)
    {
        using var files=new AudioDocumentTests();var mono=await files.TapeWave();
        using var reader=AudioSignalAnalysisService.OpenReader(mono.Path!,cancellation:Token);var pcm=new List<short>();reader.ReadFrames(0,reader.Format.FrameCount,(_,l,r)=>{Token.ThrowIfCancellationRequested();pcm.Add((short)(l>>8));});
        string path=Stereo(files,"opposite.wav",i=>(pcm[i],(short)-pcm[i]),pcm.Count,(int)reader.Format.SampleRate);
        var normal=await files.Open(path);byte[] bytes=File.ReadAllBytes(path);using var deep=normal.CreateSelectedPreview(0,pcm.Count);deep.ChannelMode=mode;
        // Inverted polarity decoding is allowed here, so either physical stream can recover.
        Assert.True(await deep.RecoverAsync(Options with{Inverted=true},null,null,Token));
        Assert.Equal(mode==2?2:1,deep.ChannelResults.Count);Assert.True(deep.IsRecoveryCurrent);
        if(mode==3){Assert.Empty(deep.Programs);Assert.Equal("Average",deep.ChannelResults[0].Source);}
        else{Assert.NotEmpty(deep.Programs);if(mode!=2)Assert.All(deep.Programs,p=>Assert.Equal(mode==0?"Left":"Right",p.AnalysisSource));}
        if(mode==2){Assert.Equal(new[]{"Left","Right"},deep.ChannelResults.Select(r=>r.Source));Assert.Contains(deep.Programs,p=>p.AnalysisSource=="Left");Assert.Contains(deep.Programs,p=>p.AnalysisSource=="Right");Assert.All(deep.ChannelResults,r=>Assert.All(r.Result.Passes,p=>Assert.All(p.Measurements,m=>Assert.Equal(r.Source=="Left"?1:2,m.Block.Channel))));}
        Assert.Equal(bytes,File.ReadAllBytes(path));Assert.Null(normal.Recovery);
        deep.ChannelMode=mode==0?1:0;Assert.False(deep.IsRecoveryCurrent);Assert.Contains("OUTDATED",deep.AnalysisInputStatus);Assert.Throws<InvalidOperationException>(()=>deep.ExportRecords(true));Assert.Empty(deep.DeepMeasurements);
    }
    [Fact]public async Task ChangedSettingsDiscardInFlightSpectralWorkAndMonoHasNoAlignment()
    {
        using var files=new AudioDocumentTests();var normal=await files.Open();using var deep=normal.CreateSelectedPreview(0,AudioDocumentTests.Frames);
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deep.FftSize=256;var work=deep.ComputeSpectralAsync(true,new Gate(ready,release.Task),Token);
        try{await ready.Task.WaitAsync(TimeSpan.FromSeconds(10),Token);deep.ChannelMode=3;}finally{release.TrySetResult();}
        Assert.False(await work);Assert.Null(deep.SpectrogramData);
        var mono=await files.Open(files.Wave("mono.wav",channels:1));using var child=mono.CreateSelectedPreview(0,1000);Assert.False(child.CanAdjustRight);Assert.Equal(new[]{"Mono"},child.DisplayChannels);Assert.Equal(DeepSignal.Left,child.RequestedAnalysisInput().Signal);
        Assert.True(await child.RecoverAsync(Options,null,null,Token));Assert.Equal("Mono",Assert.Single(child.ChannelResults).Source);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task BothPreservesDifferentProgramsAndDoesNotRecoverFromSilentChannel(int scenario)
    {
        using var files=new AudioDocumentTests();var mono=await files.TapeWave();
        using var a=AudioSignalAnalysisService.OpenReader(mono.Path!,cancellation:Token);var left=new List<short>();a.ReadFrames(0,a.Format.FrameCount,(_,l,r)=>left.Add((short)(l>>8)));
        string mzf=files.FilePath("other.mzf");File.WriteAllBytes(mzf,MediaDocumentTests.Mzf("DIFFERENT",32));string wav=files.FilePath("other.wav");SharpTapeExporter.Export(wav,TapeDocumentIO.Read(mzf).Records,SharpTapeOutputFormat.Wav,cancellationToken:Token);
        using var b=AudioSignalAnalysisService.OpenReader(wav,cancellation:Token);var right=new List<short>();b.ReadFrames(0,b.Format.FrameCount,(_,l,r)=>right.Add((short)(l>>8)));
        int frames=Math.Max(left.Count,right.Count);
        string path=Stereo(files,"two.wav",i=>(scenario==0||(i>=left.Count)?(short)0:left[i],scenario==1||(i>=right.Count)?(short)0:right[i]),frames,(int)a.Format.SampleRate);
        var normal=await files.Open(path);using var deep=normal.CreateSelectedPreview(0,frames);deep.ChannelMode=2;
        Assert.True(await deep.RecoverAsync(Options,null,null,Token));
        var l=deep.ChannelResults.Single(r=>r.Source=="Left").Result;var r=deep.ChannelResults.Single(r=>r.Source=="Right").Result;
        if(scenario==0)Assert.Empty(l.Records);else Assert.Equal("RECOVERED",Assert.Single(deep.Programs,p=>p.AnalysisSource=="Left").Name);
        if(scenario==1)Assert.Empty(r.Records);else Assert.Equal("DIFFERENT",Assert.Single(deep.Programs,p=>p.AnalysisSource=="Right").Name);
        var completed=deep.Recovery;using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>deep.RecoverAsync(Options,null,null,cancel.Token));Assert.Same(completed,deep.Recovery);Assert.True(deep.IsRecoveryCurrent);
    }
    [Fact]public async Task ExplicitWaveExportChannelRemainsIndependentOfDeepAverage()
    {
        using var files=new AudioDocumentTests();var normal=await files.Open();using var deep=normal.CreateSelectedPreview(100,300);deep.ChannelMode=3;Assert.True(await deep.ApplyTransformKeepingViewportAsync(Token));
        using var stereo=await AudioOperations.StageWaveAsync(deep,null,Token,"Stereo");using var reader=AudioSignalAnalysisService.OpenReader(stereo.Path,cancellation:Token);Assert.Equal(2,reader.Format.Channels);
        byte[] original=File.ReadAllBytes(normal.Path!);int seen=0;reader.ReadFrames(0,200,(n,l,r)=>{Assert.Equal((int)BitConverter.ToInt16(original,44+((int)n+100)*4)<<8,l);Assert.Equal((int)BitConverter.ToInt16(original,46+((int)n+100)*4)<<8,r);seen++;});Assert.Equal(200,seen);
    }
    [Fact]public async Task NegativeOffsetAlignsDelayedRightBeforeAverageAndChangesActualDecodeInput()
    {
        using var files=new AudioDocumentTests();var mono=await files.TapeWave();using var reader=AudioSignalAnalysisService.OpenReader(mono.Path!,cancellation:Token);
        var pcm=new List<short>();reader.ReadFrames(0,reader.Format.FrameCount,(_,l,r)=>pcm.Add((short)(l>>8)));
        string path=Stereo(files,"delayed.wav",i=>(pcm[i],i<4?(short)0:pcm[i-4]),pcm.Count,(int)reader.Format.SampleRate);
        var normal=await files.Open(path);using var deep=normal.CreateSelectedPreview(0,pcm.Count);deep.ChannelMode=3;deep.RightShift="-4";
        Assert.True(await deep.ApplyTransformKeepingViewportAsync(Token));deep.ShowRange(100,200);await deep.RefreshViewportAsync(cancellation:Token);
        foreach(var bucket in deep.DisplayWaveform.VisibleBuckets(100,200,200))Assert.Equal(pcm[(int)bucket.StartSample]/32768d,bucket.LeftMin);
        Assert.True(await deep.RecoverAsync(Options,null,null,Token));Assert.NotEmpty(deep.Programs);Assert.Equal(pcm.Count-4,deep.Recovery!.EndSample);
        deep.RightShift="0";Assert.False(deep.IsRecoveryCurrent);Assert.Null(deep.MeasuredBlock);Assert.True(await deep.ApplyTransformKeepingViewportAsync(Token));await deep.RefreshViewportAsync(cancellation:Token);
        Assert.Contains(deep.DisplayWaveform.VisibleBuckets(100,200,200),bucket=>bucket.LeftMin!=pcm[(int)bucket.StartSample]/32768d);
        deep.ChannelMode=0;Assert.Equal("0",deep.RightShift);Assert.False(deep.CanAdjustRight);
        deep.ChannelMode=1;deep.RightShift="99999999999999999999999";Assert.Contains("integer",deep.AnalysisInputStatus);Assert.False(deep.IsRecoveryCurrent);
    }
    [Fact]public async Task ChangedDeepRangeClearsPulseAndSpectrumAndNotifiesTheCurrentInput()
    {
        using var files=new AudioDocumentTests();var normal=await files.Open();using var deep=normal.CreateSelectedPreview(0,AudioDocumentTests.Frames);
        Assert.True(await deep.MeasureAsync(Token));Assert.True(await deep.ComputeSpectralAsync(false,null,Token));Assert.NotNull(deep.MeasuredBlock);Assert.NotNull(deep.SpectrumData);
        var changed=new List<string?>();deep.PropertyChanged+=(_,e)=>changed.Add(e.PropertyName);
        deep.SetPreviewBounds(100,1000);Assert.Null(deep.MeasuredBlock);Assert.Empty(deep.DeepMeasurements);Assert.Null(deep.SpectrumData);Assert.Contains(nameof(AudioSession.AnalysisInputStatus),changed);Assert.Contains("frames [100, 1000)",deep.AnalysisInputStatus);
        Assert.True(await deep.MeasureAsync(Token));var block=deep.MeasuredBlock;deep.SetPreviewBounds(100,1000);Assert.Same(block,deep.MeasuredBlock);
        Assert.Equal(0,normal.SelectionStart);Assert.Equal(AudioDocumentTests.Frames,normal.SelectionEnd);
    }
    [Theory][InlineData("range")][InlineData("channel")][InlineData("offset")]
    public async Task ChangedInputClearsComparisonAndPreventsComparingOrExportingOldPasses(string change)
    {
        using var files=new AudioDocumentTests();var normal=await files.Open();using var deep=normal.CreateSelectedPreview(0,AudioDocumentTests.Frames);
        Assert.True(await deep.RecoverAsync(Options,null,null,Token));Assert.True(deep.Passes.Count>=2);await deep.ComparePassesAsync(Token);Assert.NotNull(deep.PassComparison);Assert.NotEmpty(AudioOperations.Report(deep,"comparison"));
        if(change=="range")deep.SetPreviewBounds(100,AudioDocumentTests.Frames-100);else if(change=="channel")deep.ChannelMode=1;else deep.RightShift="2";
        Assert.False(deep.IsRecoveryCurrent);Assert.Null(deep.PassComparison);Assert.Empty(deep.PassDifferences);Assert.Contains("OUTDATED",deep.PassComparisonSummary);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>deep.ComparePassesAsync(Token));Assert.Throws<InvalidOperationException>(()=>AudioOperations.Report(deep,"comparison"));
        Assert.True(await deep.RecoverAsync(Options,null,null,Token));await deep.ComparePassesAsync(Token);Assert.NotNull(deep.PassComparison);Assert.True(deep.IsRecoveryCurrent);
    }
    private sealed class Gate(TaskCompletionSource ready,Task release):IProgress<WavAnalysisProgress>
    {public void Report(WavAnalysisProgress value){ready.TrySetResult();release.GetAwaiter().GetResult();}}
}
