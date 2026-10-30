using System.Text;
using System.Text.Json;
using MZTools.Capture;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Tests;

public sealed class AudioDocumentTests : IDisposable
{
    internal string Root {get;}=Path.Combine(Path.GetTempPath(),"mztools-f6-test-"+Guid.NewGuid().ToString("N"));
    internal DiskTestDialogs Dialogs {get;}=new();
    internal DocumentCoordinator Coordinator {get;}
    internal ShellViewModel Shell {get;}
    internal const int Frames=8229;
    public AudioDocumentTests(){Directory.CreateDirectory(Root);Coordinator=new(new MediaDocumentLoader(Dialogs),Dialogs,new());Shell=new(Coordinator,_=>Task.CompletedTask,Dialogs);}
    internal string FilePath(string name)=>Path.Combine(Root,name);
    internal string Wave(string name="tone.wav",int channels=2,int frames=Frames)
    {
        string path=FilePath(name);using var stream=File.Create(path);using var w=new BinaryWriter(stream,Encoding.ASCII);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+frames*channels*2);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)channels);w.Write(48000);w.Write(48000*channels*2);w.Write((short)(channels*2));w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(frames*channels*2);
        for(int i=0;i<frames;i++)for(int c=0;c<channels;c++)w.Write((short)Math.Round(16384*Math.Sin(2*Math.PI*(c==0?1500:3000)*i/48000)));
        return path;
    }
    internal async Task<AudioSession> Open(string? path=null)
    {Dialogs.OpenPath=path??Wave();Assert.True(await Coordinator.OpenAsync(),Coordinator.Error);return Assert.IsType<AudioSession>(Coordinator.Active);}
    [Fact]public async Task OpenIsReadOnlyAndSaveCannotRebindOrRewrite()
    {
        var s=await Open();var before=File.ReadAllBytes(s.Path!);Assert.True(Shell.ShowAudio);Assert.False(Shell.ShowReadiness);Assert.True(s.IsReadOnly);Assert.False(s.CanSave);Assert.False(s.CanSaveAs);Assert.False(s.IsModified);Assert.False(await Coordinator.SaveAsync(true));Assert.Equal(before,File.ReadAllBytes(s.Path!));Assert.Empty(s.Programs);
    }
    [Fact]public async Task SelectionUnionsOverlapsAndKeepsExactHalfOpenFrameCoordinates()
    {
        var s=await Open();s.SetSelection([(100,200),(150,300),(500,600)]);Assert.Equal(new[]{(100L,300L),(500L,600L)},s.SelectedIntervals);Assert.Throws<ArgumentOutOfRangeException>(()=>s.SetSelection([(-1,3)]));Assert.Throws<ArgumentOutOfRangeException>(()=>s.SetSelection([(0,Frames+1)]));s.FitSelection();Assert.Equal(100,s.ViewStart);Assert.Equal(600,s.ViewEnd);Assert.Equal(350,s.FrameAt(50,100));s.Zoom(2);Assert.Equal(250,s.ViewEnd-s.ViewStart);s.Pan(long.MaxValue/2);Assert.Equal(Frames,s.ViewEnd);
    }
    [Theory][InlineData("Left / mono",1)][InlineData("Right",1)][InlineData("Stereo",2)][InlineData("Average",1)]
    public async Task ExportContainsActualSelectedPcmWithDeclaredChannels(string channels,int count)
    {
        var s=await Open();var original=File.ReadAllBytes(s.Path!);s.SetSelection([(100,300)]);
        string staged;using(var stage=await AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken,channels))
        {
            staged=stage.Path;Archive("wave-"+channels.Replace('/','_')+".wav",File.ReadAllBytes(staged));using var r=AudioSignalAnalysisService.OpenReader(staged);Assert.Equal(200,r.Format.FrameCount);Assert.Equal(count,r.Format.Channels);Assert.Equal(48000u,r.Format.SampleRate);Assert.Equal(16,r.Format.BitsPerSample);
            if(channels=="Stereo")Assert.Equal(original.AsSpan(44+100*4,200*4).ToArray(),File.ReadAllBytes(staged).AsSpan(44).ToArray());
            if(channels is "Left / mono" or "Right")
            {var actual=File.ReadAllBytes(staged).AsSpan(44).ToArray();int c=channels=="Right"?1:0;for(int i=0;i<200;i++)Assert.Equal(BitConverter.ToInt16(original,44+(100+i)*4+c*2),BitConverter.ToInt16(actual,i*2));}
        }
        Assert.False(File.Exists(staged));Assert.Equal(original,File.ReadAllBytes(s.Path!));
    }
    [Fact]public async Task DisjointExportRequiresExplicitContiguousSelection()
    {var s=await Open();s.SetSelection([(0,100),(200,300)]);await Assert.ThrowsAsync<InvalidOperationException>(()=>AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken));await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken));await Assert.ThrowsAsync<InvalidOperationException>(()=>s.RecoverAsync(new(),null,null,TestContext.Current.CancellationToken));}
    [Fact]public async Task SourceMutationBlocksExportsWithoutCreatingDestination()
    {var s=await Open();File.AppendAllText(s.Path!,"changed");await Assert.ThrowsAsync<IOException>(()=>AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken));}
    [Fact]public async Task ExportCoordinatorProtectsSourceEvenWhenOverrideWasConfirmed()
    {
        var s=await Open();var before=File.ReadAllBytes(s.Path!);Assert.False(await Coordinator.ExecuteAsync(async ct=>{await Coordinator.WriteExportAsync(new Dictionary<string,byte[]>{[s.Path!]=[1,2,3]},ct);return true;}));Assert.Equal(before,File.ReadAllBytes(s.Path!));Assert.Same(s,Coordinator.Active);
    }
    [Fact]public async Task OfflineSpectrumUsesExactHannHelperAndOriginalFinalWindow()
    {
        var s=await Open();Assert.True(await s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken));var result=s.SpectrumData!;Assert.Equal(Frames-4096,result.StartFrame);Assert.Equal(Frames,result.EndFrame);Assert.Equal(1500d,result.LeftPeak);Assert.Equal(3000d,result.RightPeak);
        var pcm=File.ReadAllBytes(s.Path!);var data=new[]{new double[4096],new double[4096]};for(int i=0;i<4096;i++)for(int c=0;c<2;c++)data[c][i]=BitConverter.ToInt16(pcm,44+(Frames-4096+i)*4+c*2)/32768.0;
        var expected=new SpectralAnalysis().Compute(data,4096,4096,48000,2,out _,out _);Assert.Equal(expected,result.Bins);Assert.InRange(result.Bins[128].LeftDbfs,-6.022,-6.019);Assert.Contains("not PSD",result.Method);
    }
    [Fact]public async Task SpectrogramIncludesEveryWindowAndDeclaresBoundedAggregationAndTail()
    {
        var s=await Open();var g=AudioSpectralAnalysis.Spectrogram(s.Model,0,Frames,new(),256,null,TestContext.Current.CancellationToken,3,8);
        Assert.Equal(63,g.Windows);Assert.Equal(37,g.UncoveredTail);Assert.Equal(3,g.Columns.Count);Assert.Equal(63,g.Columns.Sum(c=>c.Windows));Assert.All(g.Columns,c=>Assert.True(c.Left.Length<=8));Assert.Equal(0,g.Columns[0].StartFrame);Assert.Equal(Frames-37,g.Columns[^1].EndFrame);Assert.InRange(g.Columns[0].Left[8/g.FrequencyGroup],-6.023f,-6.018f);Assert.Contains("no interpolation",g.Method);
    }
    [Fact]public async Task ShortIntervalHasNoInventedFftSamples()
    {var s=await Open();s.SetSelection([(100,200)]);await s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken);Assert.Empty(s.SpectrumData!.Bins);Assert.Null(s.SpectrumData.LeftPeak);await s.ComputeSpectralAsync(true,null,TestContext.Current.CancellationToken);Assert.Empty(s.SpectrogramData!.Columns);Assert.Equal(100,s.SpectrogramData.UncoveredTail);}
    private sealed class GateProgress(TaskCompletionSource ready,Task release) : IProgress<WavAnalysisProgress>
    {public void Report(WavAnalysisProgress value){ready.TrySetResult();release.GetAwaiter().GetResult();}}
    [Fact]public async Task ChangedSelectionCannotReceiveOldSpectrogramCompletion()
    {
        var s=await Open();s.FftSize=256;var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work=s.ComputeSpectralAsync(true,new GateProgress(ready,release.Task),TestContext.Current.CancellationToken);
        try{await ready.Task.WaitAsync(TestContext.Current.CancellationToken);s.SetSelection([(100,200)]);}finally{release.TrySetResult();}
        Assert.False(await work);Assert.Null(s.SpectrogramData);
    }
    [Fact]public async Task SpectralCacheReusesImmutableSnapshotAndCancellationPublishesNothing()
    {
        var s=await Open();using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>s.ComputeSpectralAsync(true,null,cancel.Token));Assert.Null(s.SpectrogramData);Assert.Equal(0,s.SpectralCacheBytes);
        await s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken);var first=s.SpectrumData;await s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken);Assert.Same(first,s.SpectrumData);Assert.InRange(s.SpectralCacheBytes,1,24L*1024*1024);
    }
    [Fact]public async Task SampleZoomUsesActualPcmAndDisplayGainDoesNotAffectExport()
    {
        var s=await Open();s.ShowRange(100,110);await s.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);var buckets=s.DisplayWaveform.VisibleBuckets(100,110,100);Assert.Equal(10,buckets.Count);var pcm=File.ReadAllBytes(s.Path!);foreach(var bucket in buckets){Assert.Equal(1,bucket.EndSample-bucket.StartSample);Assert.Equal(BitConverter.ToInt16(pcm,44+(int)bucket.StartSample*4)/32768.0,bucket.LeftMin);}
        s.SetSelection([(100,110)]);using var a=await AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken);s.Gain=100;using var b=await AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken);Assert.Equal(File.ReadAllBytes(a.Path),File.ReadAllBytes(b.Path));
    }
    [Fact]public async Task OfflineTriggerCentersActualCrossingAndNoMatchRetainsViewport()
    {
        var s=await Open();s.ShowRange(1000,2000);Assert.True(await s.TriggerAsync(0,true,TestContext.Current.CancellationToken));Assert.Equal(1000,s.ViewEnd-s.ViewStart);long center=s.ViewStart+500;var bytes=File.ReadAllBytes(s.Path!);Assert.True(BitConverter.ToInt16(bytes,44+((int)center-1)*4)<0);Assert.True(BitConverter.ToInt16(bytes,44+(int)center*4)>=0);long first=s.ViewStart,last=s.ViewEnd;Assert.False(await s.TriggerAsync(2,true,TestContext.Current.CancellationToken));Assert.Equal(first,s.ViewStart);Assert.Equal(last,s.ViewEnd);
    }
    [Fact]public async Task ChannelAndTransformChangesClearOldSpectralResultsButGainDoesNot()
    {
        var s=await Open();await s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken);var spectrum=s.SpectrumData;s.Gain=8;Assert.Same(spectrum,s.SpectrumData);s.ChannelMode=1;Assert.Null(s.SpectrumData);s.RightShift="2";Assert.True(s.HasDraft);await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken));Assert.True(await s.ApplyTransformPreviewAsync(TestContext.Current.CancellationToken));Assert.False(s.HasTransformDraft);Assert.Equal(2,s.AppliedTransform.RightShiftSamples);s.ResetDraft();Assert.Equal(new AudioPcmTransform(),s.AppliedTransform);
    }
    [Fact]public async Task CanceledOpenLeavesExistingTabsAndSourceBytes()
    {var s=await Open();var bytes=File.ReadAllBytes(s.Path!);using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>AudioSession.LoadAsync(s.Path!,null,cancel.Token));Assert.Same(s,Coordinator.Active);Assert.Single(Coordinator.Documents);Assert.Equal(bytes,File.ReadAllBytes(s.Path!));}
    [Fact]public async Task CloseWaitsForPreviewAndCancelKeepsDraft()
    {
        var s=await Open();s.RightShift="4";Dialogs.Close=CloseDecision.Cancel;Assert.False(await Coordinator.CloseAsync());Assert.Same(s,Coordinator.Active);var task=s.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);Dialogs.Close=CloseDecision.Discard;Assert.True(await Coordinator.CloseAsync());Assert.True(task.IsCompleted);Assert.Empty(Coordinator.Documents);await s.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);
    }
    [Fact]public async Task ReferencesAreSessionLocalAndProvenanceRemainsExplicit()
    {var a=await Open();int builtIn=a.ReferenceNames.Count;a.AddReference("User",100,110,200,210);Assert.Equal(builtIn+1,a.ReferenceNames.Count);Assert.Contains("User",a.ReferenceEvidence);Assert.False(a.FollowReference);var b=await Open(Wave("second.wav"));Assert.Equal(builtIn,b.ReferenceNames.Count);}
    [Fact]public async Task InvalidFlacDoesNotReplaceActiveDocument()
    {var s=await Open();Dialogs.OpenPath=FilePath("unsupported.flac");File.WriteAllBytes(Dialogs.OpenPath,[1,2,3]);Assert.False(await Coordinator.OpenAsync());Assert.Same(s,Coordinator.Active);Assert.Contains("FLAC",Coordinator.Error,StringComparison.OrdinalIgnoreCase);}
    [Theory][InlineData("txt")][InlineData("csv")][InlineData("json")]
    public async Task ReportExportMatchesCoreExactly(string format)
    {var s=await Open();Assert.Equal(Encoding.UTF8.GetBytes(AudioAnalysisReport.Export(s.Model,format)),AudioOperations.Report(s,format));if(format=="json")using(JsonDocument.Parse(AudioOperations.Report(s,format))){} }
    internal string Manifest(bool complete=true)
    {
        string a=Wave("joined.wav",frames:4096),b=Wave("joined_002.wav",frames:4133),manifest=a+".json";
        object Segment(string p,long first,int frames)=>new{path=Path.GetFileName(p),published=true,headerFinalized=true,pcmMatchesWrittenBytes=true,firstFrame=first,frames,dataBytes=frames*4,fileBytes=new FileInfo(p).Length,fileSha256=AtomicFileTransaction.Hash(p)};
        File.WriteAllText(manifest,JsonSerializer.Serialize(new{schema="mztools-live-capture-v1",status=complete?"Complete":"Incomplete",recording=new{conversion=new{output=new{channels=2,sampleRate=48000,bitsPerSample=16}},segments=new[]{Segment(a,0,4096),Segment(b,4096,4133)},savedFrames=Frames}}));return manifest;
    }
    [Fact]public async Task CompleteManifestJoinsFramesAndProtectsEverySegmentFromExport()
    {
        var s=await Open(Manifest());Assert.Equal(Frames,s.Model.FrameCount);Assert.Equal(3,s.SourceHashes.Count);s.SetSelection([(4000,4200)]);using var stage=await AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken,"Stereo");
        var expected=File.ReadAllBytes(FilePath("joined.wav")).AsSpan(44+4000*4,96*4).ToArray().Concat(File.ReadAllBytes(FilePath("joined_002.wav")).Skip(44).Take(104*4)).ToArray();Assert.Equal(expected,File.ReadAllBytes(stage.Path).AsSpan(44).ToArray());
        string segment=FilePath("joined_002.wav");byte[] before=File.ReadAllBytes(segment);Assert.False(await Coordinator.ExecuteAsync(async ct=>{await Coordinator.WriteExportAsync(new Dictionary<string,byte[]>{[segment]=[0]},ct);return true;}));Assert.Equal(before,File.ReadAllBytes(segment));File.AppendAllText(segment,"changed");Assert.Throws<IOException>(s.VerifySources);
    }
    [Fact]public async Task IncompleteManifestIsRejectedWithoutAssumingMissingSamples()
    {Dialogs.OpenPath=Manifest(false);Assert.False(await Coordinator.OpenAsync());Assert.Empty(Coordinator.Documents);Assert.Contains("complete",Coordinator.Error,StringComparison.OrdinalIgnoreCase);}
    internal async Task<AudioSession> TapeWave()
    {
        string tape=FilePath("program.mzf");File.WriteAllBytes(tape,MediaDocumentTests.Mzf("RECOVERED",32));string wave=FilePath("program.wav");SharpTapeExporter.Export(wave,TapeDocumentIO.Read(tape).Records,SharpTapeOutputFormat.Wav,cancellationToken:TestContext.Current.CancellationToken);return await Open(wave);
    }
    [Fact]public async Task InitialProgramsAndConsumedPulseContextMatchCoreWithoutSecondDecode()
    {
        var s=await TapeWave();Assert.Single(s.Programs);Assert.NotEmpty(s.Blocks);var block=s.Blocks.First(b=>b.Channel==1);s.SelectedBlock=block;s.ChannelMode=0;Assert.True(await s.MeasureAsync(TestContext.Current.CancellationToken));Assert.Same(block.Value,s.MeasuredBlock);Assert.Equal(TapeDocumentIO.Read(FilePath("program.mzf")).Records[0].Body.MzfBody,s.ExportRecords(true)[0].Body.MzfBody);
    }
    [Fact]public async Task RecoveryPassesRetainCandidateValidityAndComparisonEvidence()
    {
        var s=await TapeWave();Assert.True(await s.RecoverAsync(new(){ZeroCrossing=true,Schmitt=true,ThresholdScales=[1]},null,null,TestContext.Current.CancellationToken));Assert.Single(s.Programs);Assert.True(s.HasDraft);Assert.NotEmpty(s.Candidates);Assert.NotEmpty(s.Diagnostics);s.SelectedPass=s.Passes[0];Assert.NotEmpty(s.RecoveryStreams);Assert.NotNull(s.MeasuredBlock);await s.ComparePassesAsync(TestContext.Current.CancellationToken);Assert.NotNull(s.PassComparison);
        s.SelectedCandidate=s.Candidates.First(c=>c.CanSynthesize);var synthetic=s.CreateSynthetic("USER",1,0x5000,0x5001,"Supplied");Assert.Equal(MetadataOrigin.CreatedOrModifiedInAdvanced,synthetic.MetadataOrigin);Assert.Equal(s.SelectedCandidate.Value.Data,synthetic.Body.MzfBody);s.ResetDraft();Assert.True(s.HasDraft);s.MarkExported();Assert.False(s.HasDraft);
    }
    [Theory][InlineData("Mzt")][InlineData("Mzf")][InlineData("M12")]
    public async Task RecoveredProgramExportUsesF4BytesAndRetainsSourceBinding(string format)
    {
        var s=await TapeWave();string original=s.Path!;var hash=AtomicFileTransaction.Hash(original);string path=FilePath("export."+format.ToLowerInvariant());using var plan=await AudioOperations.ProgramsAsync(s,path,format,true,TestContext.Current.CancellationToken);
        var tape=TapeSession.New(false);tape.ApplyRecords(s.ExportRecords(true));using var expected=await MediaOperations.ExportAsync(tape,new(path,format,SelectedOnly:false),TestContext.Current.CancellationToken);Assert.Equal(expected.Outputs.Keys.Order(),plan.Outputs.Keys.Order());foreach(var pair in expected.Outputs){Assert.Equal(pair.Value,plan.Outputs[pair.Key]);Archive(Path.GetFileName(pair.Key),plan.Outputs[pair.Key]);}Assert.Equal(original,s.Path);Assert.Equal(hash,AtomicFileTransaction.Hash(original));
    }
    private static void Archive(string name,byte[] bytes)
    {var root=Environment.GetEnvironmentVariable("MZTOOLS_F6_OUTPUT");if(root is null)return;var path=Path.Combine(root,"binary");Directory.CreateDirectory(path);File.WriteAllBytes(Path.Combine(path,name),bytes);}
    public void Dispose()
    {
        foreach(var d in Coordinator.Documents)d.Dispose();string path=Path.GetFullPath(Root),temp=Path.GetFullPath(Path.GetTempPath());if(!path.StartsWith(temp,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new IOException("Refusing cleanup outside owned fixture.");Directory.Delete(path,true);
    }
}
