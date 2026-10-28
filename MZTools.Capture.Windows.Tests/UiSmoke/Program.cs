using MZTools;
using MZTools.Capture;
using MZTools.Capture.Windows;
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
                bool closeApproved=false,discardApproved=false,overwriteApproved=false; int hardwareRequests=0,discardPrompts=0,overwritePrompts=0; var navigation=new Navigation();
                window=new(navigation,_=>{ hardwareRequests++; throw new Exception("Hardware access is forbidden in this test."); },true,()=>closeApproved,()=>{ discardPrompts++; return discardApproved; },_=>{ overwritePrompts++; return overwriteApproved; });
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null);
                Check(hardwareRequests==0,"Demo enumeration does not touch Windows input");
                Check(window.Model.State==CaptureSessionState.Idle,"Opening the recorder does not start capture");
                var viewChannel=(ComboBox)Field(window,"channel")!;
                Check(viewChannel.Items.Count==1&&Equals(viewChannel.Items[0],"Mono"),"Mono input has a Mono view, without stereo-only choices");
                window.Format.SelectedIndex=1; window.Destination.Text=Path.Combine(output,"recording.wav");
                Check(viewChannel.Items.Count==3&&Equals(viewChannel.Items[0],"Left"),"Stereo input says Left without Mono");
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
                Check(Math.Abs(alignment.StereoIndicator.Phase!.Value)<.01&&alignment.StereoIndicator.Delay==null&&alignment.StereoIndicator.Correlation is >.99,"Stereo graph exposes qualified phase/correlation without inventing tone delay");
                var measured=window.Model.Live!.Alignment!; var graph=new HeadStereoIndicator();
                graph.Update(measured with { Stereo=measured.Stereo with { Assessable=false,SignalPhaseDegrees=45,DelayMicroseconds=7 } },true);
                Check(graph.Phase==null&&graph.Delay==null,"Unqualified stereo results cannot place phase/delay markers");
                graph.Update(measured with { Format=new(48000,1,AudioSampleRepresentation.Signed24) },true);
                Check(graph.Phase==null&&graph.Delay==null&&graph.Correlation==null,"Mono has no L/R graph values");
                graph.Update(measured,false); Check(graph.Phase==null&&graph.Correlation==null,"Paused stereo graph clears live markers");
                graph.Update(measured with { Options=measured.Options with { KnownToneHz=null },Stereo=measured.Stereo with { DelayMicroseconds=7,SignalPhaseDegrees=45 } },true);
                Check(graph.Delay==7&&graph.Phase==null,"Broadband delay is shown in microseconds without inventing a universal phase");
                Descendants<TabControl>(alignment).Single().SelectedIndex=3; await Idle(); Save(window,output,"stereo-indicators");
                window.Width=920; window.Height=640; await Idle(); Save(window,output,"stereo-small");
                Check(alignment.StereoIndicator.ActualHeight>=220,"Three stereo graphs fit the small recorder window");
                window.Width=1100; window.Height=800; Descendants<TabControl>(alignment).Single().SelectedIndex=0;
                await Wait(()=>alignment.Adjustment.ZeroHere.IsEnabled);
                alignment.Adjustment.ZeroHere.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(alignment.Adjustment.Models.All(m=>m.Current.HasZero&&m.Current.Combined.DisplayPercent==0),"One combined-edge needle per channel zeros independently");
                var headStatus=alignment.Adjustment.status;
                var dial=(FrameworkElement)Field(alignment.Adjustment,"left")!;
                await Idle(); double dialTop=dial.TranslatePoint(new Point(),alignment).Y,dialHeight=dial.ActualHeight;
                var statusModels=new[]{new HeadEdgeAdjustmentModel(),new HeadEdgeAdjustmentModel()}; headStatus.Update(statusModels,2); window.UpdateLayout();
                Check(Math.Abs(dial.TranslatePoint(new Point(),alignment).Y-dialTop)<.01&&Math.Abs(dial.ActualHeight-dialHeight)<.01,"Graphic status keeps dial position and height");
                foreach(var model in statusModels) model.Reset(string.Concat(Enumerable.Repeat("Variable real tape signal — uncertain manual reference. ",12)));
                headStatus.Update(statusModels,2); window.UpdateLayout();
                Check(Math.Abs(dial.TranslatePoint(new Point(),alignment).Y-dialTop)<.01&&Math.Abs(dial.ActualHeight-dialHeight)<.01&&headStatus.ToolTip is ToolTip,"Long status details stay in tooltip without moving dials");
                headStatus.Update(alignment.Adjustment.Models,2);
                await Idle(); Save(window,output,"head-tape-matching");
                alignment.Adjustment.Repeated.IsChecked=true; alignment.Adjustment.Repeated.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(alignment.Adjustment.Models.All(m=>!m.Current.HasZero),"Changing comparison conditions invalidates manual zero");
                alignment.Adjustment.Repeated.IsChecked=false; alignment.Adjustment.Repeated.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width=920; window.Height=640; await Idle(); Save(window,output,"head-tape-small");
                Check(((FrameworkElement)Field(alignment.Adjustment,"left")!).ActualHeight>=130&&((FrameworkElement)Field(alignment.Adjustment,"right")!).ActualHeight>=130,"Left/right dials fit the small window");
                ((FrameworkElement)window.Content).LayoutTransform=new ScaleTransform(1.5,1.5); window.Width=1450; window.Height=1000; await Idle(); Save(window,output,"head-tape-scale150");
                Check(((FrameworkElement)Field(alignment.Adjustment,"left")!).ActualHeight>=130,"Copy-matching dials fit 150 percent layout scale");
                ((FrameworkElement)window.Content).LayoutTransform=null;
                Descendants<TabControl>(alignment).Single().SelectedIndex=1;
                window.Width=1100; window.Height=800; await Idle(); Save(window,output,"head-indicators");
                alignment.Passage.Text="demo 1 kHz"; alignment.Confirm.IsChecked=true; alignment.Save(true); alignment.Save(false);
                Check(alignment.Comparison.Compare().Assessable,"Confirmed same-passage A/B is available");
                Descendants<TabControl>(alignment).Single().SelectedIndex=2; await Idle(); Save(window,output,"head-comparison");
                alignment.Passage.Text="different passage"; alignment.Save(false);
                Check(!alignment.Comparison.Compare().Assessable,"Different A/B passage IDs are rejected");
                alignment.Tone.Text="-1"; await Wait(()=>window.Model.Live?.Alignment==null);
                Check(window.Model.IsActive&&!alignment.SaveA.IsEnabled,"Invalid alignment settings cannot stop capture or save stale results");
                alignment.Tone.Text="1000"; await Wait(()=>window.Model.Live?.Alignment?.Stereo.SignalPhaseDegrees!=null);
                alignment.Passage.Text="demo 1 kHz"; alignment.WindowSize.SelectedIndex=0;
                await Wait(()=>window.Model.Live?.Alignment is { } m&&m.EndFrame-m.StartFrame==1024&&m.Stereo.Assessable);
                alignment.Save(false); Check(!alignment.Comparison.Compare().Assessable,"A/B rejects differing measurement window lengths");
                alignment.WindowSize.SelectedIndex=1; await Wait(()=>window.Model.Live?.Alignment is { } m&&m.EndFrame-m.StartFrame==4096&&m.Stereo.Assessable);
                Descendants<TabControl>(alignment).Single().SelectedIndex=1;
                window.Width=920; window.Height=640; await Idle(); Save(window,output,"head-small");
                Check(Descendants<DataGrid>(alignment).All(g=>g.ActualHeight>=80),"Head indicators and stereo tables fit the small window with scrolling");
                ((FrameworkElement)window.Content).LayoutTransform=new ScaleTransform(1.5,1.5); window.Width=1450; window.Height=1000; await Idle(); Save(window,output,"head-scale150");
                Check(Descendants<DataGrid>(alignment).All(g=>g.ActualHeight>=80),"Head indicators remain usable at 150 percent layout scale");
                ((FrameworkElement)window.Content).LayoutTransform=null;
                mainTabs.SelectedIndex=4; var pulses=(LivePulsePanel)Field(window,"pulses")!;
                var referenceChoice=(ComboBox)Field(pulses.Reference,"choice")!;
                var icChoices=referenceChoice.Items.Cast<AudioPulseReference>().Where(r=>r.Name.StartsWith("IC 1:")).ToArray();
                Check(icChoices.Length==3&&icChoices.All(r=>r.Kind==PulseReferenceKind.WriterExact)&&referenceChoice.Items.Count==12,"Default references offer simple profile names and no technical variants");
                Check(!pulses.Reference.References.Any(r=>r.CatalogId?.StartsWith("iccompat")==true)&&!Descendants<CheckBox>(pulses.Reference).Any(c=>c.Content?.ToString()?.Contains("legacy IC")==true),"No legacy IC references or toggle remain in the active reference collection");
                pulses.Reference.UseReference(icChoices[0],true);
                await Task.Delay(150); Check(window.Model.Live?.Pulses==null,"Pulse detection is opt-in");
                var pulseLatency=System.Diagnostics.Stopwatch.StartNew();
                pulses.Enabled.IsChecked=true; pulses.Enabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>pulses.Current?.MeasuredPulses>20); Check(pulses.Current!.Streams.Count==6,"Three detectors run independently on both channels");
                double firstPulseMilliseconds=pulseLatency.Elapsed.TotalMilliseconds; long firstPulseDrops=window.Model.Capture!.Statistics.AnalysisDroppedFrames;
                var pulseChart=(FrameworkElement)Field(pulses,"chart")!;
                int GuideCount()=>(int)pulseChart.GetType().GetProperty("RenderedReferenceGuides",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pulseChart)!;
                var markerVisibility=(CheckBox)Field(pulses.Reference,"visible")!;
                var fitReferences=Descendants<Button>(pulses).Single(b=>Equals(b.Content,"Fit references"));
                fitReferences.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(()=>GuideCount()==4);
                Check(fitReferences.FontWeight==FontWeights.Bold,"Fit references is visibly active");
                var originalPulseView=pulses.View(pulses.Current!.Format);
                double panStart=originalPulseView.MinimumMicroseconds!.Value;
                double panSpan=originalPulseView.MaximumMicroseconds!.Value-panStart;
                void BeginPulsePan()=>typeof(LivePulsePanel).GetMethod("BeginPan",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(pulses,null);
                void PanPulse(double fraction)=>typeof(LivePulsePanel).GetMethod("Pan",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(pulses,[fraction]);
                BeginPulsePan();
                var beforePanSnapshot=pulses.Current;
                PanPulse(.1); PanPulse(.2); PanPulse(.3);
                Check(fitReferences.FontWeight!=FontWeights.Bold,"Manual pulse drag ends highlighted reference Fit");
                var panned=pulses.View(pulses.Current.Format);
                Check(ReferenceEquals(beforePanSnapshot,pulses.Current)&&Math.Abs(panned.MinimumMicroseconds!.Value-(panStart+.3*panSpan))<1e-6,"Rapid drag positions track total mouse displacement without awaiting a measurement refresh");
                Check(Math.Abs((double)Field(pulseChart,"ViewMinimum")!-panned.MinimumMicroseconds!.Value)<1e-6,"Histogram drawing viewport follows drag immediately");
                await Wait(()=>pulses.Current!=null&&Math.Abs(pulses.Current.HistogramMinimumMicroseconds-(panStart+.3*panSpan))<1e-6);
                PanPulse(.15);
                Check(Math.Abs(pulses.View(pulses.Current!.Format).MinimumMicroseconds!.Value-(panStart+.15*panSpan))<1e-6,"Reversing drag after a refresh retains its original mouse anchor");
                PanPulse(-100);
                var clampedPan=pulses.View(pulses.Current.Format);
                Check(clampedPan.MinimumMicroseconds==0&&Math.Abs(clampedPan.MaximumMicroseconds!.Value-panSpan)<1e-6,"Dragging past zero clamps the viewport and preserves its width");
                PanPulse(.1);
                Check(Math.Abs(pulses.View(pulses.Current.Format).MinimumMicroseconds!.Value-(panStart+.1*panSpan))<1e-6,"Dragging back from the zero boundary has no accumulated offset");
                fitReferences.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                int referenceChanges=0; pulses.ConfigurationChanged+=()=>referenceChanges++;
                long resets=pulses.Current!.ContinuityResets; var referenceInstance=pulses.Current.Options.Reference;
                markerVisibility.IsChecked=false; await Idle();
                Check(GuideCount()==0&&pulses.Current!=null&&referenceChanges==0,"Hiding reference guides only redraws and keeps measured pulses");
                markerVisibility.IsChecked=true; await Wait(()=>GuideCount()==4);
                Check(pulses.Current!.ContinuityResets==resets&&ReferenceEquals(referenceInstance,pulses.Current.Options.Reference)&&referenceChanges==0,"Showing guides does not reset live continuity or classifier");
                var requestedReference=pulses.Options()!.Reference;
                pulses.Reference.UseReference(pulses.Reference.References.Single(r=>r.Name=="NORMAL 1:2"),true); await Idle();
                Check(referenceChanges==0&&pulses.Current!=null&&ReferenceEquals(referenceInstance,pulses.Current.Options.Reference)&&ReferenceEquals(requestedReference,pulses.Options()!.Reference),"IC and NORMAL alias switches share a classifier without restarting measurement");
                pulses.Reference.UseReference(pulses.Reference.References.Single(r=>r.Name=="MZ-800 ROM"),true);
                var romView=pulses.View(null); var romValues=pulses.Reference.Selected!.Guides.Select(g=>g.Microseconds).ToArray();
                double romPad=Math.Max(1,(romValues.Max()-romValues.Min())*.2);
                Check(Math.Abs(romView.MinimumMicroseconds!.Value-Math.Max(0,romValues.Min()-romPad))<1e-6&&Math.Abs(romView.MaximumMicroseconds!.Value-(romValues.Max()+romPad))<1e-6,"Changing reference fits its exact durations immediately, before a new measurement arrives");
                await Wait(()=>pulses.Current?.Options.Reference.Id=="rom800"); await Wait(()=>GuideCount()==9);
                var guideHits=((IEnumerable<(double X,string Text)>)Field(pulseChart,"referenceHits")!).ToArray();
                Check(guideHits.Count(h=>h.Text.Contains("SHORT Low:"))==4&&guideHits.Any(h=>h.Text.Contains("Leader"))&&guideHits.Any(h=>h.Text.Contains("Data S→S")),"ROM markers preserve separate LOW contexts and explain them in hover details");
                var thresholds=Descendants<Expander>(pulses).Single(e=>Equals(e.Header,"Thresholds / polarity / time window"));
                Check(Math.Abs(pulses.Reference.TranslatePoint(new Point(),pulses).X-thresholds.TranslatePoint(new Point(),pulses).X)<1,"Pulse reference expander aligns with the neighboring expanders");
                pulses.Reference.IsExpanded=true; window.Height=950; await Idle(); Save(window,output,"reference-guides");
                pulses.Reference.IsExpanded=false; window.Height=800;
                Descendants<Button>(pulses).Single(b=>Equals(b.Content,"Fit all")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Console.WriteLine($"INFO demo pulse enable → first >20 displayed durations: {firstPulseMilliseconds:F1} ms; preview dropped frames: {firstPulseDrops}. Includes acquisition and UI polling; not hardware latency.");
                pulses.Detector.SelectedIndex=2; pulses.Channel.SelectedIndex=1;
                await Wait(()=>pulses.Current is { Detector:LivePulseDetector.Adaptive,Channel:1,MeasuredPulses:>20 });
                Check(pulses.Current!.ReferenceMatches>0,"Adaptive live durations compare with catalog references");
                pulses.Reference.AddUser("Demo 1 kHz",["500","500","1000","1000"]);
                var userPulseView=pulses.View(null);
                Check(userPulseView.MinimumMicroseconds==400&&userPulseView.MaximumMicroseconds==1100,"New user pulse references fit automatically too");
                var pulseScale=(TextBox)Field(pulses,"scale")!; pulseScale.Text="2";
                Check(pulses.View(null).MinimumMicroseconds==800&&pulses.View(null).MaximumMicroseconds==2200&&fitReferences.FontWeight==FontWeights.Bold,"Active reference Fit follows reference scale changes");
                pulseScale.Text="1";
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
                var liveGain=(ComboBox)Field(window,"gain")!; var liveAuto=(CheckBox)Field(window,"auto")!;
                liveGain.SelectedItem="8"; liveAuto.IsChecked=true; liveAuto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>wave.AutoScale);
                Check(!liveGain.IsEnabled&&wave.ChannelLegend=="Blue Left • orange Right","Auto scale disables manual gain and stereo waveform names both channel colors");
                wave.Gain=2; var autoPixels=Pixels(wave); wave.Gain=8;
                Check(autoPixels.SequenceEqual(Pixels(wave)),"Automatic live waveform rendering ignores manual gain entirely");
                liveAuto.IsChecked=false; liveAuto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(()=>!wave.AutoScale);
                Check(liveGain.IsEnabled&&liveGain.SelectedItem?.ToString()=="8","Leaving Auto scale restores the retained manual gain");
                var historyFit=Descendants<Button>(window).Single(b=>Equals(b.Content,"Fit history"));
                historyFit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(historyFit.FontWeight==FontWeights.Bold,"Continuous history Fit is visibly active");
                typeof(AudioRecorderWindow).GetMethod("Zoom",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[.5,.5]);
                await Task.Delay(100); Check(historyFit.FontWeight!=FontWeights.Bold,"Manual waveform zoom ends highlighted history Fit");
                double span=(double)Field(window,"seconds")!;
                ((ComboBox)Field(window,"channel")!).SelectedIndex=1; ((ComboBox)Field(window,"gain")!).SelectedIndex=3;
                await Task.Delay(160); Check((double)Field(window,"seconds")! == span,"Channel and gain preserve zoom");
                await window.StopCapture();
                Check(!window.Model.IsActive&&window.Model.LatestReport?.Recording==null&&((Expander)Field(window,"settings")!).IsExpanded&&window.Format.IsEnabled&&window.Destination.IsEnabled,"Returning from Monitor safely releases input and restores device/file settings without an archive");
                await window.Start(false);
                await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>5000);
                Check(window.Model.State==CaptureSessionState.Recording,"Monitor → recording restart");
                window.Close(); await Task.Delay(150); Check(window.IsVisible&&window.Model.IsActive,"Declining close preserves capture");
                await window.StopCapture();
                Check(!window.Model.IsActive&&((Expander)Field(window,"settings")!).IsExpanded&&window.Device.IsEnabled&&window.Destination.IsEnabled,"Returning from Record finalizes before enabling input/file settings");
                var report=window.Model.LatestReport!; var segment=report.Recording!.Segments.Single();
                Check(report.Integrity==CaptureIntegrity.Complete&&segment.PcmMatchesWrittenBytes==true,"Finalized WAV verified against written PCM");
                using(var reader=new WavPcmStreamReader(segment.Path)) Check(reader.Format.SampleRate==48000&&reader.Format.BitsPerSample==24&&reader.Format.Channels==2&&reader.Format.FrameCount==segment.Frames,"Existing reader accepts captured native WAV");
                Check(report.Recording.SidecarFinalized&&File.Exists(report.Recording.SidecarPath),"SHA-256 sidecar finalized");
                Check(((TextBlock)Field(window,"completionNotice")!).Text.Contains(segment.Path),"Stop returns to settings with the saved WAV path");
                Check(!Descendants<Button>(window).Any(b=>Equals(b.Content,"Input / file settings…")),"Redundant input/file settings action is removed");
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
                window.Destination.Text=Path.Combine(output,"cancel-recording.wav");
                await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>1000);
                await window.CancelCapture();
                Check(discardPrompts==1&&window.Model.IsActive&&window.Model.State==CaptureSessionState.Recording,"Declining Cancel preserves the active recording");
                discardApproved=true; await window.CancelCapture();
                Check(discardPrompts==2&&!window.Model.IsActive&&window.Model.State==CaptureSessionState.Idle&&!File.Exists(window.Destination.Text)&&!File.Exists(window.Destination.Text+".json"),"Confirmed Cancel discards the current WAV and sidecar");
                Check(File.Exists(segment.Path)&&window.Model.Recordings.Count==1,"Cancel preserves earlier saved recordings");
                Check(((Expander)Field(window,"settings")!).IsExpanded&&window.Device.IsEnabled&&window.Destination.IsEnabled&&((TextBlock)Field(window,"completionNotice")!).Text=="Recording discarded.","Cancel returns to editable input settings with clear discard status");
                await window.Start(false); await window.CancelCapture();
                Check(discardPrompts==2&&window.Model.State==CaptureSessionState.Idle,"Monitor Cancel returns without a discard confirmation or WAV");
                byte[] savedBeforeOverwrite=File.ReadAllBytes(segment.Path); window.Destination.Text=segment.Path;
                await window.Start(true);
                Check(overwritePrompts==1&&!window.Model.IsActive&&savedBeforeOverwrite.SequenceEqual(File.ReadAllBytes(segment.Path)),"Declining overwrite leaves the original recording and input stopped");
                await window.Start(false); int promptsBeforeDecline=discardPrompts; await window.Start(true);
                Check(overwritePrompts==2&&window.Model.State==CaptureSessionState.Monitoring,"Declining overwrite while monitoring keeps Monitor running");
                await window.CancelCapture(); Check(discardPrompts==promptsBeforeDecline,"Declined overwrite cannot turn Monitor Cancel into a recording discard prompt");
                overwriteApproved=true; await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>1000);
                Check(overwritePrompts==3&&savedBeforeOverwrite.SequenceEqual(File.ReadAllBytes(segment.Path)),"Confirmed overwrite stages new PCM while preserving the original");
                await window.CancelCapture();
                Check(savedBeforeOverwrite.SequenceEqual(File.ReadAllBytes(segment.Path)),"Cancel during replacement preserves the original WAV");
                await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>1000); await window.StopCapture();
                Check(overwritePrompts==4&&window.Model.LatestReport?.Integrity==CaptureIntegrity.Complete&&window.Model.LatestReport.Recording!.Segments.Single().Path==segment.Path&&!savedBeforeOverwrite.SequenceEqual(File.ReadAllBytes(segment.Path)),"Stop finalizes and publishes the approved replacement at the original name");
                Check(window.Model.Recordings.Count==1,"Replacement history contains only the current version of a reused path");
                window.Destination.Text=Path.Combine(output,"close-recording.wav");
                await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>1000);
                closeApproved=true; window.Close(); await Wait(()=>!window.IsVisible);
                Check(!window.Model.IsActive,"Approved close stops and releases input");
                Check(window.Model.LatestReport?.Integrity==CaptureIntegrity.Complete&&File.Exists(window.Destination.Text),"Approved close finalizes active recording");
                window=new(navigation,_=>new EmptyBackend()); window.Show(); await Task.Delay(250);
                Check(!window.Monitor.IsEnabled&&!window.Record.IsEnabled,"No-device state is usable and cannot start capture");
                window.Source.SelectedIndex=1; await Wait(()=>window.Device.SelectedItem!=null); Check(window.Monitor.IsEnabled,"Demo works without an audio device"); window.Close(); await Wait(()=>!window.IsVisible);
                WindowsAudioCaptureOptions? selectedOptions=null; var nativeBackend=new NativeDemoBackend();
                window=new(navigation,options=>{ selectedOptions=options; return nativeBackend; }); window.Show(); await Wait(()=>window.Device.SelectedItem!=null);
                window.AccessMode.SelectedIndex=1; await Wait(()=>selectedOptions?.Mode==WindowsAudioCaptureMode.Exclusive&&window.Record.IsEnabled);
                Check(!selectedOptions!.AllowEngineFormatConversion&&!selectedOptions.RequestRaw,"Native mode disables shared processing and conversion");
                Check(!((CheckBox)Field(window,"pcm24")!).IsEnabled&&!((CheckBox)Field(window,"raw")!).IsEnabled,"Native mode cannot silently enable PCM24 or RAW");
                ((TextBox)Field(window,"nativeRate")!).Text="176400";
                ((Button)Field(window,"probeNative")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>nativeBackend.Opens==1&&window.Record.IsEnabled);
                Check(window.Model.State==CaptureSessionState.Idle&&window.Format.Items.Count==3,"Custom native format is checked without starting capture");
                window.Format.SelectedIndex=1; window.Destination.Text=Path.Combine(output,"native-float.wav");
                await Idle(); Save(window,output,"native-test-input");
                await window.Start(true); await Wait(()=>window.Model.Capture?.Statistics.WrittenFrames>2000); await window.StopCapture();
                var nativeResult=window.Model.LatestReport!.Recording!; var nativeSegment=nativeResult.Segments.Single();
                Check(nativeResult.Conversion.Output==new AudioCaptureFormat(192000,2,AudioSampleRepresentation.Float32)&&!nativeResult.Conversion.ChangesContainer,"Native UI records exact rate and bit depth without forced PCM24");
                Check(nativeSegment.PcmMatchesWrittenBytes==true&&!nativeResult.AnalyzerCompatible,"Native archive verifies PCM and reports offline analyzer incompatibility");
                int previousActions=navigation.Actions.Count; await window.OpenSelected(RecordedAudioAction.VisualAnalysis);
                Check(navigation.Actions.Count==previousActions,"Unsupported native WAV is not sent to an incompatible offline reader");
                window.Close(); await Wait(()=>!window.IsVisible);
                var main=new MainWindow(); main.Show(); await Idle();
                var tools=Descendants<MenuItem>(main).Where(m=>m.Header?.ToString()?.Contains("Record Audio")==true).ToArray();
                // Context menus are separate visual trees; inspect XAML-created tool menu directly.
                var menu=(MenuItem?)null;
                foreach(var b in Descendants<Button>(main)) if(b.ContextMenu!=null) menu=b.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(m=>m.Header?.ToString()?.Contains("Record Audio")==true)??menu;
                Check(menu!=null||tools.Length>0,"Recorder is available in Tools");
                string profilePath=Path.Combine(output,"profile.mzf"); byte[] profileBytes=new byte[129];
                profileBytes[0]=1; profileBytes[1]=(byte)'P'; profileBytes[2]=13; profileBytes[18]=1; profileBytes[21]=0x12; profileBytes[23]=0x12; profileBytes[128]=0xc9;
                File.WriteAllBytes(profilePath,profileBytes); File.WriteAllText(Path.ChangeExtension(profilePath,".mfi"),"TYPE=IC\nSPEED=1:3\n");
                main.GetType().GetMethod("AddFile",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,[profilePath,true,false,null,null,null,false,0,0]);
                var profileRows=(System.Collections.ObjectModel.ObservableCollection<MzfDisplayData>)((DataGrid)main.FindName("MzfDataGrid")).ItemsSource;
                var profileRow=profileRows.Single();
                main.UpdateLayout(); await Idle();
                var loaderChoice=Descendants<ComboBox>(main).First(c=>ReferenceEquals(c.DataContext,profileRow)&&c.Items.Contains("IC"));
                Check(Equals(loaderChoice.SelectedItem,"IC")&&Descendants<TextBlock>(loaderChoice).Any(t=>t.Text=="IC FAST IPL"),"Main profile combo renders IC FAST IPL while preserving selected key IC");
                Check(profileRow.ProfileDescription.Contains("asymmetric")&&profileRow.Speed=="1:3","Profile tooltip explains physical timing without reinterpreting speed");
                Save(main,output,"ic-profile-choice");
                int closePrompts=0; bool permitOwnerClose=false;
                window=new(navigation,_=>throw new Exception("Hardware forbidden"),true,()=>{ closePrompts++; return permitOwnerClose; }) { Owner=main };
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null); window.Format.SelectedIndex=1;
                window.Destination.Text=Path.Combine(output,"owner-close.wav"); await window.Start(true);
                await Wait(()=>window.Model.Capture?.Statistics.WrittenFrames>2000);
                main.Close(); await Wait(()=>closePrompts==1&&main.IsEnabled);
                Check(main.IsVisible&&window.IsVisible&&window.Model.IsActive,"Declining recorder shutdown keeps its owner open and capture running");
                var idleRecorder=new AudioRecorderWindow(navigation,_=>throw new Exception("Hardware forbidden"),true) { Owner=main };
                idleRecorder.Show(); await Wait(()=>idleRecorder.Device.SelectedItem!=null);
                permitOwnerClose=true; main.Close(); await Wait(()=>!main.IsVisible);
                Check(!window.IsVisible&&!idleRecorder.IsVisible&&!window.Model.IsActive,"Owner shutdown closes both active and idle recorders");
                Check(window.Model.LatestReport?.Integrity==CaptureIntegrity.Complete&&window.Model.LatestReport.Recording?.Segments.Single().PcmMatchesWrittenBytes==true,"Owner waits for WAV finalization and verification");
                var idleOwner=new MainWindow(); idleOwner.Show();
                window=new(navigation,_=>throw new Exception("Hardware forbidden"),true) { Owner=idleOwner }; window.Show(); await Wait(()=>window.Device.SelectedItem!=null);
                idleOwner.Close(); await Wait(()=>!idleOwner.IsVisible); Check(!window.IsVisible,"Idle owner shutdown does not re-enter Closing synchronously");
                var failedOwner=new MainWindow(); failedOwner.Show();
                window=new(navigation,_=>throw new Exception("Hardware forbidden"),true,()=>true) { Owner=failedOwner };
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null); window.Format.SelectedIndex=1;
                window.Destination.Text=Path.Combine(output,"owner-publish-collision.wav"); await window.Start(true);
                await Wait(()=>window.Model.Capture?.Statistics.WrittenFrames>2000);
                File.WriteAllBytes(window.Destination.Text,[0xa5]); // deliberate publish race in the isolated test directory
                failedOwner.Close(); await Wait(()=>failedOwner.IsEnabled&&!window.Model.IsActive);
                Check(failedOwner.IsVisible&&window.IsVisible&&window.Model.LatestReport?.Integrity==CaptureIntegrity.Incomplete,"Finalize failure keeps owner and recorder open for review");
                Check(window.Model.LatestReport!.Recording!.Segments.Any(s=>!s.Published&&File.Exists(s.Path)),"Failed owner shutdown retains acquired partial WAV");
                failedOwner.Close(); await Wait(()=>!failedOwner.IsVisible); Check(!window.IsVisible,"Reviewed failed capture can be explicitly closed again");
                int monitorPrompts=0; window=new(navigation,_=>throw new Exception("Hardware forbidden"),true,()=>{ monitorPrompts++; return false; });
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null); await window.Start(false);
                window.Close(); await Wait(()=>!window.IsVisible);
                Check(monitorPrompts==0&&!window.Model.IsActive&&window.Model.LatestReport?.Recording==null,"Monitor closes without an archive confirmation or WAV");
                var monitorOwner=new MainWindow(); monitorOwner.Show();
                window=new(navigation,_=>throw new Exception("Hardware forbidden"),true,()=>{ monitorPrompts++; return false; }) { Owner=monitorOwner };
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null); await window.Start(false);
                monitorOwner.Close(); await Wait(()=>!monitorOwner.IsVisible);
                Check(monitorPrompts==0&&!window.IsVisible&&!window.Model.IsActive,"Owner also stops Monitor without archive confirmation");
                window=new(navigation,_=>new SyntheticCaptureBackend(new() { StartDelay=TimeSpan.FromMilliseconds(500),FrameCount=long.MaxValue,ChunkInterval=TimeSpan.FromMilliseconds(10) }),false,()=>{ monitorPrompts++; return false; });
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null);
                var pendingMonitor=window.Start(false); await Wait(()=>window.Model.State==CaptureSessionState.Starting);
                window.Close(); await pendingMonitor; await Wait(()=>!window.IsVisible);
                Check(monitorPrompts==0&&!window.Model.IsActive,"Pending Monitor start can close without an archive prompt");
                window=new(navigation,_=>throw new Exception("Hardware forbidden"),true,()=>true);
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null);
                var segmentedBackend=new SyntheticCaptureBackend(new() { FrameCount=2400,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(10) });
                var segmentedDevice=(await segmentedBackend.EnumerateDevicesAsync()).Single();
                var segmentedFormat=segmentedDevice.AdvertisedFormats.First(f=>f.SampleRate==48000&&f.Representation==AudioSampleRepresentation.Signed24&&f.Channels==2);
                await window.Model.StartAsync(segmentedBackend,new(segmentedDevice.Id,segmentedFormat),new(Path.Combine(output,"segmented.wav")) { SegmentDataLimit=800*segmentedFormat.BlockAlign });
                await Wait(()=>window.Model.LatestReport?.Recording is { Segments.Count:3 });
                var wholeRecording=window.Model.LatestReport!.Recording!;
                var segmentedTable=(DataGrid)Field(window,"segments")!; await Wait(()=>segmentedTable.Items.Count==3); segmentedTable.SelectedIndex=1;
                int actionsBeforeJoined=navigation.Actions.Count;
                foreach(var action in Enum.GetValues<RecordedAudioAction>()) await window.OpenSelected(action);
                Check(navigation.Actions.Skip(actionsBeforeJoined).Count()==3&&navigation.Actions.Skip(actionsBeforeJoined).All(a=>a.Path==wholeRecording.SidecarPath),"Every recorder analysis action opens all segments through the recording report");
                using(var joined=new SegmentedPcmStreamReader(wholeRecording.SidecarPath!)) Check(joined.Format.FrameCount==2400&&joined.Format.Channels==2,"Actual recorder JSON and PCM24 stereo segments form a continuous source");
                window.Close(); await Wait(()=>!window.IsVisible);
                byte[] beforeReplacementClose=File.ReadAllBytes(segment.Path);
                window=new(navigation,_=>throw new Exception("Hardware forbidden"),true,()=>true,()=>true,_=>true);
                window.Show(); await Wait(()=>window.Device.SelectedItem!=null); window.Destination.Text=segment.Path;
                await window.Start(true); await Wait(()=>window.Model.Capture!.Statistics.WrittenFrames>1000);
                Check(beforeReplacementClose.SequenceEqual(File.ReadAllBytes(segment.Path)),"Replacement keeps the old file until approved close finalizes it");
                window.Close(); await Wait(()=>!window.IsVisible);
                Check(window.Model.LatestReport?.Integrity==CaptureIntegrity.Complete&&window.Model.LatestReport.Recording!.Segments.Single().Path==segment.Path&&!beforeReplacementClose.SequenceEqual(File.ReadAllBytes(segment.Path)),"Approved recording close publishes the completed replacement");
                Console.WriteLine($"PASS: {checks} recorder UI checks. Hardware input NOT tested. Screenshots: {output}");
                app.Shutdown(0);
            }
            catch(Exception error) { Console.Error.WriteLine(error); if(window!=null) await window.Model.DisposeAsync(); app.Shutdown(1); }
        };
        app.Run();
    }
    private static object? Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value);
    private static byte[] Pixels(FrameworkElement element)
    {
        element.InvalidateVisual(); element.UpdateLayout();
        int width=Math.Max(1,(int)element.ActualWidth),height=Math.Max(1,(int)element.ActualHeight);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(element);
        var bytes=new byte[width*height*4]; bitmap.CopyPixels(bytes,width*4,0); return bytes;
    }
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
    private sealed class NativeDemoBackend:IAudioCaptureBackend
    {
        private readonly SyntheticCaptureBackend synthetic=new(new() { FrameCount=long.MaxValue,ChunkFramePattern=[1920],ChunkInterval=TimeSpan.FromMilliseconds(10) });
        internal int Opens;
        public ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken=default)=>ValueTask.FromResult<IReadOnlyList<AudioCaptureDevice>>(
            [new(SyntheticCaptureBackend.DeviceId,"TEST ONLY — synthetic native format, no hardware","UI test",[new(192000,2,AudioSampleRepresentation.Signed32,24),new(192000,2,AudioSampleRepresentation.Float32)])]);
        public ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request,CancellationToken cancellationToken=default) { Opens++; return synthetic.OpenAsync(request,cancellationToken); }
    }
}
