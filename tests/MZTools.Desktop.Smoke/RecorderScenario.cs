using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MZTools.Capture;
using MZTools.Desktop.Controls;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Smoke;

internal static class RecorderScenario
{
    private sealed class Factory:IRecorderBackendFactory
    {
        internal int Opens;
        public IReadOnlyList<string> Choices {get;}=["Native UI smoke · synthetic only"];
        public IAudioCaptureBackend Create(int index,bool raw,bool conversion)=>new Backend(this);
        private sealed class Backend(Factory owner):IAudioCaptureBackend
        {
            private readonly SyntheticCaptureBackend source=new(new(){FrameCount=48000,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(5)});
            public ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken=default)=>source.EnumerateDevicesAsync(cancellationToken);
            public ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request,CancellationToken cancellationToken=default){owner.Opens++;return source.OpenAsync(request,cancellationToken);}
        }
    }
    private sealed class Navigation:IRecorderNavigation
    {internal List<(string,RecordedAudioAction)> Calls=[];public Task OpenAudioAsync(string path,RecordedAudioAction action,CancellationToken cancellationToken=default){Calls.Add((path,action));return Task.CompletedTask;}public void RevealFile(string path){}}
    internal static async Task Run(string output)
    {
        string root=Path.Combine(Path.GetTempPath(),"mztools-recorder-native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var checks=new List<object>();var frames=new List<object>();var factory=new Factory();var navigation=new Navigation();var model=new RecorderModel(factory);var window=new RecorderWindow(model,navigation);window.Show();
        void Check(bool ok,string name){checks.Add(new{name,pass=ok});if(!ok)throw new InvalidOperationException(name);}
        void Capture(string name)
        {
            window.UpdateLayout();double scale=window.RenderScaling;using var image=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width*scale),(int)Math.Ceiling(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));image.Render(window);image.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);frames.Add(new{file=name,logicalWidth=window.Bounds.Width,logicalHeight=window.Bounds.Height,renderScaling=scale,origin="Skia render of actual native window; not desktop capture"});
        }
        try
        {
            await Task.Delay(100);Check(factory.Opens==0,"Recorder construction does not open input");await model.EnumerateAsync();Check(factory.Opens==0&&model.Device is null,"Enumeration requires explicit device selection and does not start input");model.Device=model.Devices[0];model.Format=model.Formats.First(f=>f.Channels==2);model.PulsesEnabled=true;model.ApplyPulses();string path=Path.Combine(root,"recording.wav");await model.StartAsync(path);Check(factory.Opens==1,"Record explicitly opens exactly one selected synthetic input");
            var charts=window.GetVisualDescendants().OfType<AudioChart>().ToArray();Check(charts.Length>=2,"Live/offline graph family is reused by recorder views");
            for(int i=0;i<200&&model.IsActive;i++){await model.RefreshAsync();model.ChannelMode=i%3;model.ZoomWave(i%2==0?1.5:1/1.5);window.UpdateLayout();await Task.Delay(10);}
            await model.StopAsync();await model.RefreshAsync();var recording=model.Recordings.Single();Check(recording.Integrity==CaptureIntegrity.Complete&&recording.SinkFinalized,"Archive remains complete through live UI refresh/zoom load");Check(recording.Recording is {SavedFrames:48000,SourceHashCoversSavedFramesExactly:true,SidecarFinalized:true},"WAV and manifest account for every source frame");Check(recording.Recording!.Segments.All(s=>s.PcmMatchesWrittenBytes==true),"Recorded segment bytes match delivered PCM hashes");Check(recording.Statistics.KnownMissingFrames==0&&!recording.Statistics.HasUnknownLoss,"Archive loss counters remain separate from preview drops");Check(model.Live is {Spectrum.Count:>0,Alignment:not null,Pulses:not null},"Bounded snapshots contain spectrum, alignment and optional pulse measurements");Capture("native-recorder-signal.png");
            var tabs=window.GetVisualDescendants().OfType<TabControl>().Single(c=>c.Name=="RecorderTabs");tabs.SelectedIndex=1;await Task.Delay(80);Check(model.PulseStreams.Count>0,"Live pulse streams remain individually identifiable");Capture("native-recorder-pulses.png");tabs.SelectedIndex=2;model.Passage="Same explicit synthetic passage";model.SamePassage=true;model.SaveAlignment(true);model.SaveAlignment(false);await Task.Delay(80);Check(model.AlignmentComparison.A is not null&&model.AlignmentComparison.B is not null,"A/B stores two explicit user measurements with passage provenance");Check(!model.ComparisonSummary.Contains("head angle",StringComparison.OrdinalIgnoreCase),"Comparison does not invent a calibrated mechanical angle");Capture("native-recorder-alignment.png");
            tabs.SelectedIndex=3;model.SelectedRecording=recording;await Task.Delay(80);foreach(string label in new[]{"Visual analysis","Heuristic import","Manual recovery"})window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(80);Check(navigation.Calls.Count==3&&navigation.Calls.All(c=>c.Item1==recording.Recording.SidecarPath),"Explicit saved-audio actions navigate through the complete recording manifest");Capture("native-recorder-history.png");
            window.Width=640;window.Height=480;await Task.Delay(80);Capture("native-recorder-compact.png");Check(window.FindControl<Button>("RecordInput")!.IsEffectivelyVisible,"Compact recorder keeps archive controls reachable");await model.DisposeAsync();window.Close();await Task.Delay(80);Check(!window.IsVisible,"Recorder close waits for session disposal and leaves no live window");
        }
        finally
        {
            await model.DisposeAsync();if(window.IsVisible)window.Close();File.WriteAllText(Path.Combine(output,"native-recorder.json"),JsonSerializer.Serialize(new{os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,checks,frames,hardwareTested=false,pickerMode="Private fixture path; root smoke separately records picker coverage"},new JsonSerializerOptions{WriteIndented=true}));
            string resolved=Path.GetFullPath(root);if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new IOException("Refusing cleanup outside owned fixture.");Directory.Delete(resolved,true);
        }
    }
}
