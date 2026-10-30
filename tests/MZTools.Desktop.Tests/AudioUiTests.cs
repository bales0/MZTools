using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class AudioUiTests
{
    private sealed class Host : IDisposable
    {
        internal AudioDocumentTests Files {get;}=new();
        internal MainWindow Window {get;}
        internal Host(){Window=new(Files.Shell);Window.Show();Dispatcher.UIThread.RunJobs();}
        internal TabControl Tabs=>Window.GetVisualDescendants().OfType<TabControl>().Single(c=>c.Name=="AudioTabs");
        internal AudioChart Wave=>Window.GetVisualDescendants().OfType<AudioChart>().Single(c=>c.Name=="Waveform");
        public void Dispose(){foreach(var child in Window.OwnedWindows.ToArray())child.Close();Window.Close();Dispatcher.UIThread.RunJobs();Files.Dispose();}
    }
    private static void Capture(Window window,string name,double scale=1)
    {
        var output=Environment.GetEnvironmentVariable("MZTOOLS_F6_OUTPUT");if(output is null)return;Directory.CreateDirectory(output);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
        using var bitmap=new RenderTargetBitmap(new PixelSize((int)(window.Bounds.Width*scale),(int)(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
    }
    private static void Detail(Host host,string name)
    {
        var button=host.Window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Graph detail")&&b.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();var detail=Assert.Single(host.Window.OwnedWindows);Assert.Same(host.Files.Shell.ActiveAudio,detail.GetVisualDescendants().OfType<AudioChart>().Single().Session);Capture(detail,name);
        if(name=="audio-spectrogram-detail.png")
        {
            var chart=detail.GetVisualDescendants().OfType<AudioChart>().Single();
            using var image=new RenderTargetBitmap(new PixelSize(1100,680),new Vector(96,96));image.Render(detail);
            var address=System.Runtime.InteropServices.Marshal.AllocHGlobal(1100*680*4);
            try
            {
                image.CopyPixels(new PixelRect(0,0,1100,680),address,1100*680*4,1100*4);var bytes=new byte[1100*680*4];System.Runtime.InteropServices.Marshal.Copy(address,bytes,0,bytes.Length);int colored=0;
                for(int y=120;y<600;y++)for(int x=100;x<1000;x++){int at=(y*1100+x)*4;if(bytes[at]>bytes[at+2]+10)colored++;}
                Assert.True(colored>10000,$"Spectrogram must paint its actual color raster, got {colored} colored pixels.");
            }
            finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(address);}
        }
        detail.Close();
    }
    [AvaloniaFact]public async Task ActualRecordsAndRegionsTablesShareExactFrameSelection()
    {
        using var host=new Host();var s=await host.Files.TapeWave();Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();
        var table=host.Window.GetVisualDescendants().OfType<TableView>().Single(c=>c.Name=="AudioBlocks");Assert.Equal(s.Blocks.Count,table.ItemCount);table.SelectedItem=s.Blocks[0];Dispatcher.UIThread.RunJobs();Assert.Same(s.Blocks[0],s.SelectedBlock);Assert.Equal(s.Blocks[0].End,s.SelectionEnd);
        if(s.Blocks.Count>1){table.SelectedItems!.Add(s.Blocks[1]);Dispatcher.UIThread.RunJobs();Assert.Equal(AudioRangeInspection.MergeRanges(s.Blocks.Take(2).Select(b=>(b.Start,b.End))),s.SelectedIntervals);}
        table.SelectedItem=s.Blocks[0];await s.MeasureAsync(TestContext.Current.CancellationToken);host.Tabs.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Detail(host,"audio-histogram-detail.png");var pulse=host.Window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="PulseKind");pulse.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Detail(host,"audio-scatter-detail.png");pulse.SelectedIndex=2;Dispatcher.UIThread.RunJobs();Detail(host,"audio-drift-detail.png");
        host.Tabs.SelectedIndex=2;Dispatcher.UIThread.RunJobs();var regions=host.Window.GetVisualDescendants().OfType<TableView>().Single(c=>c.Name=="AudioRegions");regions.SelectedItem=s.Regions[0];Dispatcher.UIThread.RunJobs();Assert.Equal(s.Regions[0].Start,s.SelectionStart);Capture(host.Window,"audio-regions.png");
    }
    [AvaloniaFact]public async Task WaveKeyboardAndPointerSelectionKeepSourceFrameCoordinates()
    {
        using var host=new Host();var s=await host.Files.Open();Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();host.Wave.Focus();host.Window.KeyPress(Key.OemPlus,RawInputModifiers.None,PhysicalKey.Equal,null);host.Window.KeyRelease(Key.OemPlus,RawInputModifiers.None,PhysicalKey.Equal,null);Dispatcher.UIThread.RunJobs();Assert.True(s.ViewEnd-s.ViewStart<AudioDocumentTests.Frames);
        var a=host.Wave.TranslatePoint(new Point(80,70),host.Window)!.Value;var b=host.Wave.TranslatePoint(new Point(200,70),host.Window)!.Value;
        host.Window.MouseDown(a,MouseButton.Left,RawInputModifiers.Shift);host.Window.MouseMove(b,RawInputModifiers.LeftMouseButton|RawInputModifiers.Shift);host.Window.MouseUp(b,MouseButton.Left,RawInputModifiers.Shift);Dispatcher.UIThread.RunJobs();Assert.True(s.SelectionEnd>s.SelectionStart);Assert.InRange(s.SelectionEnd,1,AudioDocumentTests.Frames);Assert.True(s.SelectionEnd-s.SelectionStart<AudioDocumentTests.Frames);Capture(host.Window,"audio-wave-selection.png");
    }
    [AvaloniaFact]public async Task SpectralTabsRenderPreparedDataAndRetainDocumentZoomOnSwitch()
    {
        using var host=new Host();var s=await host.Files.Open();s.FftSize=256;await s.ComputeSpectralAsync(false,null,TestContext.Current.CancellationToken);await s.ComputeSpectralAsync(true,null,TestContext.Current.CancellationToken);host.Tabs.SelectedIndex=4;Dispatcher.UIThread.RunJobs();Capture(host.Window,"audio-spectrum.png");Detail(host,"audio-spectrum-detail.png");
        var kind=host.Window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="SpectralKind");kind.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Capture(host.Window,"audio-spectrogram.png");Detail(host,"audio-spectrogram-detail.png");s.Zoom(4);long first=s.ViewStart,last=s.ViewEnd;host.Files.Coordinator.Add(MZTools.Desktop.Documents.TapeSession.New(false));host.Files.Coordinator.Active=s;Dispatcher.UIThread.RunJobs();Assert.Equal(first,s.ViewStart);Assert.Equal(last,s.ViewEnd);Assert.Equal(4,s.ViewIndex);
    }
    [AvaloniaFact]public async Task RecoveryTablesShowCoreProgramsCandidatesAndConsumedPassStreams()
    {
        using var host=new Host();var s=await host.Files.TapeWave();await s.RecoverAsync(new(){ThresholdScales=[1],Schmitt=false},null,null,TestContext.Current.CancellationToken);s.ViewIndex=5;Dispatcher.UIThread.RunJobs();Assert.NotNull(s.SelectedProgram);Capture(host.Window,"audio-recovery.png");
        var tabs=host.Window.GetVisualDescendants().OfType<TabControl>().Single(c=>c.Name=="RecoveryTabs");tabs.SelectedIndex=1;Dispatcher.UIThread.RunJobs();var candidates=host.Window.GetVisualDescendants().OfType<TableView>().Single(c=>c.Name=="RecoveryCandidates");candidates.SelectedItem=s.Candidates.First(c=>c.CanSynthesize);Dispatcher.UIThread.RunJobs();Assert.NotNull(s.SelectedCandidate);Assert.Contains("4096",s.CandidateHex);Capture(host.Window,"audio-candidate.png");var marker=s.Candidates.FirstOrDefault(c=>c.End<=c.Start);if(marker is not null){candidates.SelectedItem=marker;Dispatcher.UIThread.RunJobs();Assert.Equal(1,s.SelectionEnd-s.SelectionStart);}s.MarkExported();
    }
    [AvaloniaTheory][InlineData(1920,1080,1.0)][InlineData(1366,768,1.25)][InlineData(1440,960,1.0)][InlineData(1366,768,1.5)][InlineData(920,640,2.0)][InlineData(640,480,1.0)]
    public async Task ResponsiveAudioLayoutHasReachableControlsAndCompactInspector(int width,int height,double scale)
    {
        using var host=new Host();await host.Files.Open();host.Window.Width=width;host.Window.Height=height;Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();Assert.True(host.Wave.IsEffectivelyVisible);Assert.True(host.Wave.Bounds.Width>50);Capture(host.Window,$"audio-layout-{width}x{height}-{(scale==1.25?"1.25":scale.ToString("0.0"))}.png",scale);
        if(width<1100){host.Window.FindControl<AudioWorkspace>("AudioEditor")!.ShowInspector(host.Window);Dispatcher.UIThread.RunJobs();Assert.Single(host.Window.OwnedWindows);Capture(host.Window.OwnedWindows.Single(),"audio-inspector-compact-"+width+".png");}
    }
}
