using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Capture;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class RecorderTests
{
    private sealed class Factory(SyntheticCaptureOptions? options=null,Func<long,int,double>? sample=null):IRecorderBackendFactory
    {
        internal int Created,Enumerated,Opened;
        public IReadOnlyList<string> Choices {get;}=["Test only synthetic"];
        public IAudioCaptureBackend Create(int index,bool raw,bool conversion){Created++;return new Spy(new SyntheticCaptureBackend(options??new(){FrameCount=long.MaxValue,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(10)},sample),this);}
        private sealed class Spy(IAudioCaptureBackend inner,Factory owner):IAudioCaptureBackend
        {
            public ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken=default){owner.Enumerated++;return inner.EnumerateDevicesAsync(cancellationToken);}
            public ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request,CancellationToken cancellationToken=default){owner.Opened++;return inner.OpenAsync(request,cancellationToken);}
        }
    }
    private sealed class Navigation:IRecorderNavigation
    {public Task OpenAudioAsync(string path,RecordedAudioAction action,CancellationToken cancellationToken=default)=>Task.CompletedTask;public void RevealFile(string path){}}
    private static async Task Select(RecorderModel model){await model.EnumerateAsync();model.Device=model.Devices[0];model.Format=new(48000,2,AudioSampleRepresentation.Signed16);}
    private static async Task Until(Func<bool> condition)
    {for(int i=0;i<1000&&!condition();i++)await Task.Delay(10,TestContext.Current.CancellationToken);Assert.True(condition());}
    [AvaloniaFact]public async Task FrozenEdgeReferenceFlowsThroughWorkerAndDiagnosticsStayCollapsed()
    {
        double width=140;
        double Signal(long frame,int channel)
        {
            double samples=System.Threading.Volatile.Read(ref width)*96000/1e6/.8,p=frame%100;
            double value=p<10?-.4:p<10+samples?-.4+.8*(p-10)/samples:p<50?.4:p<50+samples?.4-.8*(p-50)/samples:-.4;
            return value*(channel==0?1:.7);
        }
        var factory=new Factory(new(){FrameCount=long.MaxValue,ChunkFramePattern=[8192],ChunkInterval=TimeSpan.FromMilliseconds(25)},Signal);
        await using var model=new RecorderModel(factory);await Select(model);model.Format=new(96000,2,AudioSampleRepresentation.Signed24);model.FftSize=8192;
        var window=new RecorderWindow(model,new Navigation());window.Show();
        try
        {
            await model.StartAsync();
            for(int i=0;i<100&&model.LeftEdge.State!=HeadEdgeAdjustmentState.ReadyToZero;i++){await Task.Delay(35,TestContext.Current.CancellationToken);await model.RefreshAsync();}
            Assert.Equal(HeadEdgeAdjustmentState.ReadyToZero,model.LeftEdge.State);model.ZeroEdges();
            var left=model.EdgeModels[0].Reference;var right=model.EdgeModels[1].Reference;
            Assert.NotNull(left);Assert.NotNull(right);Assert.NotEqual(left.Id,right.Id);
            System.Threading.Volatile.Write(ref width,95);
            for(int i=0;i<120&&model.LeftEdge.Combined.ChangePercent is not >30;i++){await Task.Delay(35,TestContext.Current.CancellationToken);await model.RefreshAsync();}
            Assert.Same(left,model.EdgeModels[0].Reference);Assert.Same(right,model.EdgeModels[1].Reference);
            Assert.InRange(model.LeftEdge.Combined.ChangePercent!.Value,45,50);Assert.InRange(model.RightEdge!.Combined.ChangePercent!.Value,45,50);
            Assert.Equal(left.Id,model.Live!.Alignment!.Channels[0].Edges!.ReferenceId);
            Assert.Equal(right.Id,model.Live.Alignment.Channels[1].Edges!.ReferenceId);
            var tabs=window.GetVisualDescendants().OfType<TabControl>().First();tabs.SelectedItem=tabs.Items.OfType<TabItem>().Single(t=>Equals(t.Header,"Head alignment"));Dispatcher.UIThread.RunJobs();
            var detail=window.GetVisualDescendants().OfType<Expander>().Single(e=>Equals(e.Header,"Edge qualification / diagnostic details"));Assert.False(detail.IsExpanded);
            detail.IsExpanded=true;Dispatcher.UIThread.RunJobs();Assert.Contains("Detected",model.LeftEdge.Diagnostics);
            model.AlignmentEnabled=false;await model.RefreshAsync();Assert.Null(model.LeftEdge.Combined.ChangePercent);
            Assert.DoesNotContain(model.LeftEdge.State,new[]{HeadEdgeAdjustmentState.WorkingCopy,HeadEdgeAdjustmentState.RepeatedPassage});
            await model.StopAsync();Assert.Empty(model.Recordings);Assert.Empty(model.Error);
        }
        finally{await model.DisposeAsync();window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [Fact]public async Task ConstructorEnumerationAndExplicitSelectionNeverStartInput()
    {
        var f=new Factory();await using var m=new RecorderModel(f);Assert.Equal(0,f.Created);Assert.False(m.CanStart);await m.EnumerateAsync();Assert.Equal(0,f.Opened);Assert.Null(m.Device);m.Device=m.Devices[0];Assert.True(m.CanStart);Assert.False(m.IsActive);
    }
    [Fact]public async Task ExactFormatProbeClosesWithoutStartingInputOrCreatingAnArchive()
    {
        var factory=new Factory();await using var model=new RecorderModel(factory);await Select(model);
        var requested=new AudioCaptureFormat(192000,2,AudioSampleRepresentation.Signed24,20);
        Assert.DoesNotContain(requested,model.Formats);await model.ProbeFormatAsync(requested);
        Assert.Empty(model.Error);Assert.Equal(1,factory.Opened);Assert.False(model.IsActive);Assert.Null(model.Live);Assert.Empty(model.Recordings);
        Assert.Contains(requested,model.Formats);Assert.Equal(requested,model.Format);Assert.Contains("has not been started",model.Status);
        var device=model.Device;model.Device=null;model.Device=device;Assert.DoesNotContain(requested,model.Formats);Assert.Equal(1,factory.Opened);
    }
    [AvaloniaFact]public async Task WpfDestinationFieldProtectsOpenSourcesAndStartsOnlyAnExplicitRecordAction()
    {
        using var files=new MZTools.Desktop.Tests.MediaDocumentTests();string blocked=files.FilePath("open-source.wav"),output=files.FilePath("new-recording.wav");File.WriteAllBytes(blocked,[1,2,3]);
        var factory=new Factory(new(){FrameCount=4800,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(5)});await using var model=new RecorderModel(factory);await Select(model);
        var window=new RecorderWindow(model,new Navigation(),path=>path!=blocked);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();var destination=window.FindControl<TextBox>("RecorderDestination")!;var record=window.FindControl<Button>("RecordInput")!;
            destination.Text=blocked;record.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await Until(()=>model.Error.Length>0);Assert.Equal(0,factory.Opened);Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(blocked));
            destination.Text=output;record.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await Until(()=>model.Recordings.Count>0);await model.StopAsync();
            Assert.Equal(1,factory.Opened);Assert.True(File.Exists(output));Assert.Empty(model.Error);Assert.Single(model.Recordings);Assert.True(model.Recordings[0].SinkFinalized);
        }finally{await model.DisposeAsync();window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [Fact]public async Task MonitorUsesSharedSnapshotPipelineAndNoWriter()
    {
        var f=new Factory();await using var m=new RecorderModel(f);await Select(m);await m.StartAsync();await Until(()=>m.IsActive);await Task.Delay(250,TestContext.Current.CancellationToken);await m.RefreshAsync();Assert.NotNull(m.Live);Assert.NotEmpty(m.Live.Spectrum);Assert.Empty(m.Recordings);m.FitWave();await m.RefreshAsync();Assert.Equal(m.Live!.HistoryStartFrame,m.Live.WaveStartFrame);Assert.True(m.FollowLive);m.ZoomWave(2);double zoom=m.WaveSeconds;m.ChannelMode=2;await m.RefreshAsync();Assert.Equal(zoom,m.WaveSeconds);await m.StopAsync();Assert.False(m.IsActive);Assert.Empty(m.Recordings);Assert.Equal(1,f.Opened);
    }
    [Theory][InlineData(false)][InlineData(true)]public async Task FinalizedRecordingKeepsBitIdentityAndExplicitNativePolicy(bool native)
    {
        using var files=new AudioDocumentTests();var f=new Factory(new(){FrameCount=9600,ChunkFramePattern=[127,511,1024]});await using var m=new RecorderModel(f);await Select(m);m.PreserveNative=native;string path=files.FilePath("capture.wav");await m.StartAsync(path);await Until(()=>!m.IsActive);await m.RefreshAsync();var report=Assert.Single(m.Recordings);Assert.Equal(CaptureIntegrity.Complete,report.Integrity);Assert.True(report.SinkFinalized);Assert.NotNull(report.Recording);Assert.Equal(9600,report.Recording.SavedFrames);Assert.True(report.Recording.SourceHashCoversSavedFramesExactly);Assert.True(report.Recording.SidecarFinalized);Assert.All(report.Recording.Segments,s=>Assert.True(s.PcmMatchesWrittenBytes));using var reader=new WavPcmStreamReader(path);Assert.Equal(9600,reader.Format.FrameCount);Assert.Equal(48000u,reader.Format.SampleRate);Assert.Equal(16,reader.Format.BitsPerSample);
        var bytes=File.ReadAllBytes(path);Assert.Equal(report.Recording.SourcePcmSha256,Convert.ToHexString(SHA256.HashData(bytes.AsSpan(44))));
    }
    [Fact]public async Task StopDuringSlowStartFinalizesAndDisposesWithoutLateState()
    {
        var f=new Factory(new(){FrameCount=long.MaxValue,OpenDelay=TimeSpan.FromMilliseconds(200),ChunkInterval=TimeSpan.FromMilliseconds(10)});var m=new RecorderModel(f);await Select(m);var start=m.StartAsync();await Task.Delay(20,TestContext.Current.CancellationToken);m.Cancel();await m.DisposeAsync();await start;Assert.Null(m.Live);Assert.False(m.IsActive);Assert.Contains("Cancelled",m.Status);
    }
    [Fact]public async Task DisconnectIsIncompleteAndNeverBecomesSuccessfulArchive()
    {
        using var files=new AudioDocumentTests();var f=new Factory(new(){FrameCount=9600,DisconnectAtFrame=2000});await using var m=new RecorderModel(f);await Select(m);await m.StartAsync(files.FilePath("disconnect.wav"));await Until(()=>!m.IsActive);await m.RefreshAsync();var report=Assert.Single(m.Recordings);Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);Assert.Equal(CaptureEndReason.DeviceLost,report.Reason);Assert.NotEmpty(m.Error);
    }
    [Fact]public async Task LivePulsesAndAlignmentUseOneCatalogAndRetainQualification()
    {
        var f=new Factory();await using var m=new RecorderModel(f);await Select(m);m.PulsesEnabled=true;m.ApplyPulses();await m.StartAsync();await Task.Delay(500,TestContext.Current.CancellationToken);await m.RefreshAsync();Assert.NotNull(m.Live?.Pulses);Assert.NotEmpty(m.PulseStreams);Assert.Contains(PulseReferenceCatalog.Version,m.Live.Pulses.Options.Reference.CatalogVersion);Assert.NotNull(m.Live.Alignment);m.SaveAlignment(true);m.SaveAlignment(false);Assert.False(m.Comparison.Assessable);Assert.Contains("passage",m.ComparisonSummary);m.Passage="Same synthetic passage";m.SamePassage=true;m.SaveAlignment(true);m.SaveAlignment(false);Assert.NotNull(m.AlignmentComparison.A);Assert.NotNull(m.AlignmentComparison.B);await m.StopAsync();
    }
    [AvaloniaFact]public async Task IndependentRecorderRendersAndMainBrowserCloseDoesNotStopIt()
    {
        using var files=new AudioDocumentTests();var browser=new MainWindow(files.Shell);browser.Show();var factory=new Factory();var model=new RecorderModel(factory);var window=new RecorderWindow(model,new Navigation());window.Show();Dispatcher.UIThread.RunJobs();Assert.Equal(0,factory.Opened);await Select(model);model.Format=model.Formats.First(f=>f.Channels==2);await model.StartAsync();Assert.True(model.IsActive,model.Error);await Task.Delay(300,TestContext.Current.CancellationToken);await model.RefreshAsync();browser.Close();Dispatcher.UIThread.RunJobs();Assert.True(window.IsVisible,"Independent recorder was closed with the browser");Assert.True(model.IsActive,model.Error+" / "+model.Statistics);
        var output=Environment.GetEnvironmentVariable("MZTOOLS_COMPLETION_OUTPUT");if(output is not null){Directory.CreateDirectory(output);window.UpdateLayout();using var image=new RenderTargetBitmap(new PixelSize(1440,960),new Vector(96,96));image.Render(window);image.Save(Path.Combine(output,"recorder-signal.png"),PngBitmapEncoderOptions.Default);}
        Assert.NotEmpty(window.GetVisualDescendants().OfType<MZTools.Desktop.Controls.AudioChart>());await model.StopAsync();await model.DisposeAsync();window.Close();Dispatcher.UIThread.RunJobs();
    }
}
