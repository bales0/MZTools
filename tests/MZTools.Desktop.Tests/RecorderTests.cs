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
    private sealed class Factory(SyntheticCaptureOptions? options=null):IRecorderBackendFactory
    {
        internal int Created,Enumerated,Opened;
        public IReadOnlyList<string> Choices {get;}=["Test only synthetic"];
        public IAudioCaptureBackend Create(int index,bool raw,bool conversion){Created++;return new Spy(new SyntheticCaptureBackend(options??new(){FrameCount=long.MaxValue,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(10)}),this);}
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
    [Fact]public async Task ConstructorEnumerationAndExplicitSelectionNeverStartInput()
    {
        var f=new Factory();await using var m=new RecorderModel(f);Assert.Equal(0,f.Created);Assert.False(m.CanStart);await m.EnumerateAsync();Assert.Equal(0,f.Opened);Assert.Null(m.Device);m.Device=m.Devices[0];Assert.True(m.CanStart);Assert.False(m.IsActive);
    }
    [Fact]public async Task MonitorUsesSharedSnapshotPipelineAndNoWriter()
    {
        var f=new Factory();await using var m=new RecorderModel(f);await Select(m);await m.StartAsync();await Until(()=>m.IsActive);await Task.Delay(250,TestContext.Current.CancellationToken);await m.RefreshAsync();Assert.NotNull(m.Live);Assert.NotEmpty(m.Live.Spectrum);Assert.Empty(m.Recordings);m.ZoomWave(2);double zoom=m.WaveSeconds;m.ChannelMode=2;await m.RefreshAsync();Assert.Equal(zoom,m.WaveSeconds);await m.StopAsync();Assert.False(m.IsActive);Assert.Empty(m.Recordings);Assert.Equal(1,f.Opened);
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
