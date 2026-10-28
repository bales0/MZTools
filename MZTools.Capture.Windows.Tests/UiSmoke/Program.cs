using MZTools;
using MZTools.Capture;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static void Main()
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.Resources.Add("TopToolbarButton",new Style(typeof(Button)));
        app.Resources.Add("TopToolbarSeparator",new Style(typeof(Border)));
        app.Startup+=async(_,_)=>
        {
            AudioRecorderWindow? window=null;
            try
            {
                string output=Path.Combine(AppContext.BaseDirectory,"smoke-artifacts",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
                bool closeApproved=false; int hardwareRequests=0; var navigation=new Navigation();
                window=new(navigation,_=>{ hardwareRequests++; throw new Exception("Hardware access is forbidden in this test."); },true,()=>closeApproved);
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null);
                Check(hardwareRequests==0,"Demo enumeration does not touch Windows input");
                Check(window.Model.State==CaptureSessionState.Idle,"Opening the recorder does not start capture");
                window.Format.SelectedIndex=1; window.Destination.Text=Path.Combine(output,"recording.wav");
                window.Width=1100; window.Height=800; await Idle(); Save(window,output,"inputs");
                await window.Start(false); await Wait(()=>window.Model.Live?.Spectrum.Count>0);
                Check(window.Model.State==CaptureSessionState.Monitoring,"Monitor lifecycle");
                Check(!File.Exists(window.Destination.Text),"Monitor creates no WAV");
                Check(Math.Abs(window.Model.Live!.LeftDominantHz!.Value-1000)<12,"Live calibrated FFT on synthetic 1 kHz");
                var mainTabs=(TabControl)Field(window,"tabs")!; mainTabs.SelectedIndex=3;
                var alignment=(HeadAlignmentPanel)Field(window,"alignment")!;
                await Wait(()=>window.Model.Live?.Alignment!=null);
                Check(!window.Model.Live!.Alignment!.Stereo.Assessable&&window.Model.Live.Alignment.Stereo.DelaySamples==null,"Unqualified periodic signal cannot claim a channel delay");
                alignment.KnownTone.IsChecked=true; alignment.KnownTone.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>window.Model.Live?.Alignment?.Stereo.SignalPhaseDegrees!=null);
                Check(window.Model.Live!.Alignment!.Stereo.Assessable&&Math.Abs(window.Model.Live.Alignment.Stereo.SignalPhaseDegrees!.Value)<.01,"Known demo tone has qualified relative phase");
                window.Width=1100; window.Height=800; await Idle(); Save(window,output,"head-indicators");
                alignment.Passage.Text="demo 1 kHz"; alignment.Confirm.IsChecked=true; alignment.Save(true); alignment.Save(false);
                Check(alignment.Comparison.Compare().Assessable,"Confirmed same-passage A/B is available");
                Descendants<TabControl>(alignment).Single().SelectedIndex=1; await Idle(); Save(window,output,"head-comparison");
                alignment.Passage.Text="different passage"; alignment.Save(false);
                Check(!alignment.Comparison.Compare().Assessable,"Different A/B passage IDs are rejected");
                alignment.Tone.Text="-1"; await Wait(()=>window.Model.Live?.Alignment==null);
                Check(window.Model.IsActive&&!alignment.SaveA.IsEnabled,"Invalid alignment settings cannot stop capture or save stale results");
                alignment.Tone.Text="1000"; await Wait(()=>window.Model.Live?.Alignment?.Stereo.SignalPhaseDegrees!=null);
                alignment.Passage.Text="demo 1 kHz"; alignment.WindowSize.SelectedIndex=0;
                await Wait(()=>window.Model.Live?.Alignment is { } m&&m.EndFrame-m.StartFrame==1024&&m.Stereo.Assessable);
                alignment.Save(false); Check(!alignment.Comparison.Compare().Assessable,"A/B rejects differing measurement window lengths");
                alignment.WindowSize.SelectedIndex=1; await Wait(()=>window.Model.Live?.Alignment is { } m&&m.EndFrame-m.StartFrame==4096&&m.Stereo.Assessable);
                Descendants<TabControl>(alignment).Single().SelectedIndex=0;
                window.Width=920; window.Height=640; await Idle(); Save(window,output,"head-small");
                Check(Descendants<DataGrid>(alignment).All(g=>g.ActualHeight>=80),"Head indicators and stereo tables fit the small window with scrolling");
                ((FrameworkElement)window.Content).LayoutTransform=new ScaleTransform(1.5,1.5); window.Width=1450; window.Height=1000; await Idle(); Save(window,output,"head-scale150");
                Check(Descendants<DataGrid>(alignment).All(g=>g.ActualHeight>=80),"Head indicators remain usable at 150 percent layout scale");
                ((FrameworkElement)window.Content).LayoutTransform=null;
                mainTabs.SelectedIndex=4; var pulses=(LivePulsePanel)Field(window,"pulses")!;
                await Task.Delay(150); Check(window.Model.Live?.Pulses==null,"Pulse detection is opt-in");
                var pulseLatency=System.Diagnostics.Stopwatch.StartNew();
                pulses.Enabled.IsChecked=true; pulses.Enabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>pulses.Current?.MeasuredPulses>20); Check(pulses.Current!.Streams.Count==6,"Three detectors run independently on both channels");
                Console.WriteLine($"INFO demo pulse enable → first >20 displayed durations: {pulseLatency.Elapsed.TotalMilliseconds:F1} ms; preview dropped frames: {window.Model.Capture!.Statistics.AnalysisDroppedFrames}. Includes acquisition and UI polling; not hardware latency.");
                pulses.Detector.SelectedIndex=2; pulses.Channel.SelectedIndex=1;
                await Wait(()=>pulses.Current is { Detector:LivePulseDetector.Adaptive,Channel:1,MeasuredPulses:>20 });
                Check(pulses.Current!.ReferenceMatches>0,"Adaptive live durations compare with catalog references");
                pulses.Reference.AddUser("Demo 1 kHz",["500","500","1000","1000"]);
                await Wait(()=>pulses.Current?.Options.Reference.Id=="USER:Demo 1 kHz"&&pulses.Current.ReferenceMatches>20);
                Check(pulses.Current!.Options.Reference.CatalogVersion=="Session-local user values","User reference provenance stays explicit");
                double pulseSpan=pulses.Current.HistogramMaximumMicroseconds-pulses.Current.HistogramMinimumMicroseconds;
                typeof(LivePulsePanel).GetMethod("ZoomData",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(pulses,[.5]);
                await Wait(()=>pulses.Current!=null&&pulses.Current.HistogramMaximumMicroseconds-pulses.Current.HistogramMinimumMicroseconds<pulseSpan*.8);
                double zoomed=pulses.Current!.HistogramMaximumMicroseconds-pulses.Current.HistogramMinimumMicroseconds;
                pulses.Channel.SelectedIndex=0; await Wait(()=>pulses.Current?.Channel==0);
                Check(Math.Abs(pulses.Current!.HistogramMaximumMicroseconds-pulses.Current.HistogramMinimumMicroseconds-zoomed)<1e-6,"Live histogram zoom survives channel changes");
                window.Width=920; window.Height=640;
                Check(pulses.Current.VisiblePulses>0,"Zoom buttons retain the dominant measured pulse cluster");
                await Idle(); Save(window,output,"live-pulses-small");
                Check(((FrameworkElement)Field(pulses,"chart")!).ActualHeight>=90,"Live pulse histogram fits the small window");
                ((FrameworkElement)window.Content).LayoutTransform=new ScaleTransform(1.5,1.5); window.Width=1450; window.Height=1000; await Idle(); Save(window,output,"live-pulses-scale150");
                Check(((FrameworkElement)Field(pulses,"chart")!).ActualHeight>=90,"Live pulse histogram fits 150 percent layout scale");
                ((FrameworkElement)window.Content).LayoutTransform=null;
                pulses.Zero.IsChecked=false; pulses.Zero.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>pulses.Current?.Streams.Count==4&&pulses.Current.MeasuredPulses>20);
                Check(pulses.Current!.Streams.All(s=>s.Detector!=LivePulseDetector.ZeroCrossing),"Detector checkboxes control independent streaming passes");
                ((TextBox)Field(pulses,"threshold")!).Text="-1"; await Wait(()=>window.Model.Live?.Pulses==null);
                Check(window.Model.IsActive&&pulses.Current==null,"Invalid pulse settings clear stale results without stopping capture");
                ((TextBox)Field(pulses,"threshold")!).Text="1"; await Wait(()=>pulses.Current?.MeasuredPulses>20);
                Check(pulses.Current!.ContinuityResets>0,"Changing pulse settings starts an explicit new measurement epoch");
                pulses.Enabled.IsChecked=false; pulses.Enabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                mainTabs.SelectedIndex=0; await Idle();
                window.Width=1100; window.Height=800; await Idle(); Save(window,output,"monitor");
                var wave=(LiveSignalCanvas)Field(window,"waveform")!;
                Check(wave.ActualHeight>=100,"Waveform receives useful vertical space");
                typeof(AudioRecorderWindow).GetMethod("Zoom",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[.5,.5]);
                double span=(double)Field(window,"seconds")!;
                ((ComboBox)Field(window,"channel")!).SelectedIndex=1; ((ComboBox)Field(window,"gain")!).SelectedIndex=3;
                await Task.Delay(160); Check((double)Field(window,"seconds")! == span,"Channel and gain preserve zoom");
                await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>5000);
                Check(window.Model.State==CaptureSessionState.Recording,"Monitor → recording restart");
                window.Close(); await Task.Delay(150); Check(window.IsVisible&&window.Model.IsActive,"Declining close preserves capture");
                await window.StopCapture();
                var report=window.Model.LatestReport!; var segment=report.Recording!.Segments.Single();
                Check(report.Integrity==CaptureIntegrity.Complete&&segment.PcmMatchesWrittenBytes==true,"Finalized WAV verified against written PCM");
                using(var reader=new WavPcmStreamReader(segment.Path)) Check(reader.Format.SampleRate==48000&&reader.Format.BitsPerSample==24&&reader.Format.Channels==2&&reader.Format.FrameCount==segment.Frames,"Existing reader accepts captured native WAV");
                Check(report.Recording.SidecarFinalized&&File.Exists(report.Recording.SidecarPath),"SHA-256 sidecar finalized");
                foreach(var action in Enum.GetValues<RecordedAudioAction>()) await window.OpenSelected(action);
                Check(navigation.Actions.Count==3&&navigation.Actions.All(a=>a.Path==segment.Path),"All existing analyzer navigation actions use saved WAV");
                var tabs=Descendants<TabControl>(window).First(); tabs.SelectedIndex=1; await Idle();
                var savedTable=(DataGrid)Field(window,"segments")!;
                savedTable.Columns.First(column=>column.SortMemberPath=="Path").Width=280;
                await Idle(); Save(window,output,"recordings");
                Check(((DataGrid)Field(window,"segments")!).Items.Count==1,"Finalized segment appears without starting another capture");
                tabs.SelectedIndex=0; window.Width=920; window.Height=640; await Idle(); Save(window,output,"small");
                Check(wave.ActualHeight>=100,"Small window still has a visible waveform");
                ((FrameworkElement)window.Content).LayoutTransform=new ScaleTransform(1.5,1.5); window.Width=1450; window.Height=1000; await Idle(); Save(window,output,"scale150");
                Check(wave.ActualHeight>=100,"150 percent layout scale preserves plotting space");
                ((FrameworkElement)window.Content).LayoutTransform=null;
                window.Destination.Text=Path.Combine(output,"close-recording.wav");
                await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>1000);
                closeApproved=true; window.Close(); await Wait(()=>!window.IsVisible);
                Check(!window.Model.IsActive,"Approved close stops and releases input");
                Check(window.Model.LatestReport?.Integrity==CaptureIntegrity.Complete&&File.Exists(window.Destination.Text),"Approved close finalizes active recording");
                window=new(navigation,_=>new EmptyBackend()); window.Show(); await Task.Delay(250);
                Check(!window.Monitor.IsEnabled&&!window.Record.IsEnabled,"No-device state is usable and cannot start capture");
                window.Source.SelectedIndex=1; await Wait(()=>window.Device.SelectedItem!=null); Check(window.Monitor.IsEnabled,"Demo works without an audio device"); window.Close(); await Wait(()=>!window.IsVisible);
                var main=new MainWindow(); main.Show(); await Idle();
                var tools=Descendants<MenuItem>(main).Where(m=>m.Header?.ToString()?.Contains("Record Audio")==true).ToArray();
                // Context menus are separate visual trees; inspect XAML-created tool menu directly.
                var menu=(MenuItem?)null;
                foreach(var b in Descendants<Button>(main)) if(b.ContextMenu!=null) menu=b.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(m=>m.Header?.ToString()?.Contains("Record Audio")==true)??menu;
                Check(menu!=null||tools.Length>0,"Recorder is available in Tools"); main.Close();
                Console.WriteLine($"PASS: {checks} recorder UI checks. Hardware input NOT tested. Screenshots: {output}");
                app.Shutdown(0);
            }
            catch(Exception error) { Console.Error.WriteLine(error); if(window!=null) await window.Model.DisposeAsync(); app.Shutdown(1); }
        };
        app.Run();
    }
    private static object? Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value);
    private static void Check(bool result,string name) { if(!result) throw new Exception(name); checks++; Console.WriteLine("PASS "+name); }
    private static async Task Wait(Func<bool> done) { var timeout=DateTime.UtcNow.AddSeconds(12); while(!done()) { if(DateTime.UtcNow>timeout) throw new TimeoutException(); await Task.Delay(30); } await Idle(); }
    private static async Task Idle()=>await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    { for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i); if(child is T match) yield return match; foreach(var nested in Descendants<T>(child)) yield return nested; } }
    private static void Save(Window window,string folder,string name)
    {
        var content=(FrameworkElement)window.Content;
        var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        var capture=new DrawingVisual();
        using(var drawing=capture.RenderOpen())
        {
            var bounds=new Rect(0,0,content.ActualWidth,content.ActualHeight);
            drawing.DrawRectangle(window.Background??Brushes.White,null,bounds);
            drawing.DrawRectangle(new VisualBrush(content),null,bounds);
        }
        image.Render(capture); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file=File.Create(Path.Combine(folder,name+".png")); png.Save(file);
    }
    private sealed class Navigation:IRecorderNavigation
    {
        internal readonly List<(string Path,RecordedAudioAction Action)> Actions=[];
        public Task OpenAudioAsync(string path,RecordedAudioAction action,CancellationToken cancellationToken=default) { Actions.Add((path,action)); return Task.CompletedTask; }
        public void RevealFile(string path) { }
    }
    private sealed class EmptyBackend:IAudioCaptureBackend
    {
        public ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken=default)=>ValueTask.FromResult<IReadOnlyList<AudioCaptureDevice>>([]);
        public ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request,CancellationToken cancellationToken=default)=>throw new Exception("An empty backend cannot open input.");
    }
}
