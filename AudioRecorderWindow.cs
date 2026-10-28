using Microsoft.Win32;
using MZTools.Capture;
using MZTools.Capture.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace MZTools;

internal sealed class AudioRecorderWindow : Window
{
    internal readonly AudioRecorderViewModel Model = new();
    internal readonly ComboBox Source = Choice("Windows input (WASAPI)","Synthetic demo — no hardware"), Device = Choice(), Format = Choice();
    internal readonly ComboBox AccessMode = Choice("Shared — Windows mix", "Exclusive — driver-accepted format");
    private readonly TextBox nativeRate=new() { Text="192000",Width=80 },nativeBits=new() { Text="24",Width=45 };
    private readonly ComboBox nativeEncoding=Choice("PCM8","PCM16","PCM24","PCM32","Float32"),nativeChannels=Choice("Mono","Stereo");
    private readonly Button probeNative=Button("Check exact format");
    private bool NativeInput=>Source.SelectedIndex==0&&AccessMode.SelectedIndex==1;
    internal readonly Button Monitor = Button("Monitor only"), Record = Button("Record"), Stop = Button("Stop / finalize");
    internal readonly Button Cancel = Button("Cancel");
    private readonly Button fitHistory=Button("Fit history");
    private bool fitHistoryActive;
    internal readonly TextBox Destination = new() { MinWidth=280,Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Tape_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".wav") };
    private readonly CheckBox raw=Check("Request RAW (shared only)",true),engine=Check("Allow Windows format conversion"),pcm24=Check("Allow float32 / PCM32 → PCM24 (shared only)"),follow=Check("Follow live",true),auto=Check("Auto scale"),trigger=Check("Trigger");
    private readonly ComboBox channel=Choice("Left","Right","Overlay L/R"),gain=Choice("0.5","1","2","4","8"),fft=Choice("1024","4096","16384","32768"),slope=Choice("Rising","Falling");
    private readonly TextBox triggerLevel=new() { Text="0",Width=55 },clipLevel=new() { Text="100",Width=50 },note=new() { MinWidth=200 },side=new() { Width=65 };
    private readonly TextBlock state=Text("Idle — choose an input; Monitor or Record starts capture."),actual=Text("Input not started."),scope=Text(""),spectrumInfo=Text(""),output=Text(""),failure=Text("");
    private readonly Expander settings=new() { Header="Input / recording settings",IsExpanded=true };
    private readonly DataGrid levels=Table(),segments=Table(),diagnostics=Table();
    private readonly LiveSignalCanvas waveform=new(),spectrum=new() { Spectrum=true };
    private readonly HeadAlignmentPanel alignment=new();
    private readonly LivePulsePanel pulses=new();
    private readonly TabControl tabs=new();
    private readonly TabItem alignmentTab=new() { Header="Head alignment" };
    private readonly TabItem pulsesTab=new() { Header="Live pulses" };
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(75) };
    private readonly Func<WindowsAudioCaptureOptions,IAudioCaptureBackend> windowsFactory;
    private readonly IRecorderNavigation navigation;
    private readonly Func<bool> confirmClose;
    private readonly Func<bool> confirmDiscard;
    private readonly Func<string,bool> confirmOverwrite;
    private bool endingCapture;
    private readonly TextBlock completionNotice=Text("");
    private readonly CancellationTokenSource lifetime=new();
    private readonly List<FrameworkElement> inputControls=[];
    private readonly List<FrameworkElement> recordingControls=[];
    private IAudioCaptureBackend? backend;
    private IReadOnlyList<AudioCaptureDevice> devices=[];
    private IReadOnlyList<SegmentRow> rows=[];
    private CaptureSessionReport? shownReport;
    private CancellationTokenSource? enumeration;
    private bool ready,ticking,closing,allowClose,starting,viewDirty=true;
    private Task<bool>? closeAttempt;
    private bool recordingRun,startingRecording;
    private double seconds=.1;
    private long? end;
    private long lastFrame=-1;
    private sealed record DeviceRow(AudioCaptureDevice Device) { public override string ToString()=>Device.Name; }
    private sealed record FormatRow(AudioCaptureFormat Format) { public override string ToString()=>Describe(Format); }
    internal sealed record SegmentRow(string Path,long Frames,long Bytes,bool HeaderFinalized,bool Published,bool? PcmVerified,string Integrity,string? Error,bool AnalyzerCompatible);

    internal AudioRecorderWindow(IRecorderNavigation? navigation=null,Func<WindowsAudioCaptureOptions,IAudioCaptureBackend>? windowsFactory=null,bool demo=false,Func<bool>? confirmClose=null,Func<bool>? confirmDiscard=null,Func<string,bool>? confirmOverwrite=null)
    {
        Title="Record Audio / Live Tape Analysis"; Width=1220; Height=900; MinWidth=920; MinHeight=640;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        this.navigation=navigation??new AudioRecorderNavigation(this);
        this.windowsFactory=windowsFactory??(options=>new WindowsAudioCaptureBackend(options));
        this.confirmClose=confirmClose??(()=>MessageBox.Show(this,"Stop recording and finalize the WAV before closing? Recorded audio will be retained.","Audio capture",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes);
        this.confirmDiscard=confirmDiscard??(()=>MessageBox.Show(this,"Discard the current recording? All WAV segments and its report from this run will be deleted. Earlier saved recordings will be kept.","Cancel recording",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes);
        this.confirmOverwrite=confirmOverwrite??(path=>MessageBox.Show(this,"Replace the existing recording after successful Stop / finalize?\n"+path+"\nIts JSON report and segments identified by that report are included. Cancel or a capture error keeps the original.","Replace recording",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes);
        var root=new DockPanel { Margin=new(10) }; Content=root;
        var header=new StackPanel(); DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
        var config=new StackPanel();
        var input=Row(); Add(input,"Source",Source); Add(input,"Device",Device); Add(input,"Input format",Format);
        Device.MinWidth=230; Device.MaxWidth=400; Format.MinWidth=250;
        var refresh=Button("Refresh inputs"); input.Children.Add(refresh); config.Children.Add(input);
        var options=Row(); options.Children.Add(raw); options.Children.Add(engine); options.Children.Add(pcm24); config.Children.Add(options);
        Add(options,"Access",AccessMode);
        var custom=Row(); Add(custom,"Custom native Hz",nativeRate); Add(custom,"Container",nativeEncoding); Add(custom,"Valid bits",nativeBits); Add(custom,"Channels",nativeChannels); custom.Children.Add(probeNative);
        config.Children.Add(new Expander { Header="Probe an exact exclusive format (does not start recording)",Content=custom });
        nativeEncoding.SelectedIndex=2; nativeChannels.SelectedIndex=1;
        var file=Row(); Add(file,"WAV destination",Destination); var browse=Button("Browse…"); file.Children.Add(browse); config.Children.Add(file);
        recordingControls.AddRange([Destination,browse,pcm24,note,side]);
        var metadata=Row(); Add(metadata,"Tape note",note); Add(metadata,"Side",side); config.Children.Add(metadata); config.Children.Add(output);
        settings.Content=new ScrollViewer { Content=config,MaxHeight=230,VerticalScrollBarVisibility=ScrollBarVisibility.Auto }; header.Children.Add(settings);
        inputControls.AddRange([Source,Device,Format,raw,engine,refresh,AccessMode,nativeRate,nativeBits,nativeEncoding,nativeChannels,probeNative]);
        var actions=Row(); actions.Children.Add(Monitor); actions.Children.Add(Record); actions.Children.Add(Stop); actions.Children.Add(Cancel); header.Children.Add(actions);
        header.Children.Add(state); failure.Foreground=Brushes.Firebrick; header.Children.Add(failure);
        completionNotice.Foreground=Brushes.DarkGreen; completionNotice.MaxHeight=60; completionNotice.TextTrimming=TextTrimming.CharacterEllipsis; completionNotice.Visibility=Visibility.Collapsed; header.Children.Add(completionNotice);
        header.Children.Add(new Expander { Header="Actual input / processing",Content=actual });
        root.Children.Add(tabs);
        var live=new DockPanel(); tabs.Items.Add(new TabItem { Header="Live signal",Content=live });
        var controls=new StackPanel(); DockPanel.SetDock(controls,Dock.Top); live.Children.Add(controls);
        levels.Height=83; ConfigureLevels(); controls.Children.Add(levels);
        var view=Row(); Add(view,"View",channel); Add(view,"Gain",gain); view.Children.Add(auto); view.Children.Add(follow);
        var fit=fitHistory; var zoomIn=Button("Zoom +"); var zoomOut=Button("Zoom −"); view.Children.Add(fit); view.Children.Add(zoomIn); view.Children.Add(zoomOut); controls.Children.Add(view);
        var measurement=Row(); measurement.Children.Add(trigger); Add(measurement,"Level (FS)",triggerLevel); measurement.Children.Add(slope); Add(measurement,"FFT",fft); Add(measurement,"Clip ≥ % FS",clipLevel); var reset=Button("Apply / reset clip hold"); measurement.Children.Add(reset); controls.Children.Add(measurement);
        controls.Children.Add(scope);
        var plots=new Grid(); plots.RowDefinitions.Add(new() { Height=new(3,GridUnitType.Star) }); plots.RowDefinitions.Add(new() { Height=new(5) }); plots.RowDefinitions.Add(new() { Height=new(2,GridUnitType.Star) });
        plots.Children.Add(waveform); var splitter=new GridSplitter { Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,Background=Brushes.LightGray }; Grid.SetRow(splitter,1); plots.Children.Add(splitter);
        var bottom=new DockPanel(); Grid.SetRow(bottom,2); var caption=spectrumInfo; DockPanel.SetDock(caption,Dock.Bottom); bottom.Children.Add(caption); bottom.Children.Add(spectrum); plots.Children.Add(bottom); live.Children.Add(plots);
        var recordings=new DockPanel(); tabs.Items.Add(new TabItem { Header="Recordings",Content=recordings });
        var savedActions=Row(); DockPanel.SetDock(savedActions,Dock.Bottom); recordings.Children.Add(savedActions);
        foreach(var action in Enum.GetValues<RecordedAudioAction>())
        { var button=Button(action==RecordedAudioAction.VisualAnalysis?"Visual analysis…":action==RecordedAudioAction.HeuristicImport?"Heuristic import…":"Manual recovery…"); savedActions.Children.Add(button); button.Click+=async(_,_)=>await OpenSelected(action); }
        var folder=Button("Show in folder"); savedActions.Children.Add(folder); folder.Click+=(_,_)=>Guard(()=>{ if(segments.SelectedItem is SegmentRow selected) this.navigation.RevealFile(selected.Path); });
        recordings.Children.Add(segments); tabs.Items.Add(new TabItem { Header="Diagnostics",Content=diagnostics });
        alignmentTab.Content=alignment; tabs.Items.Add(alignmentTab);
        pulsesTab.Content=pulses; tabs.Items.Add(pulsesTab);
        pulses.ConfigurationChanged+=()=>{ if(!closing) Guard(()=>Model.ConfigurePulses(pulses.Options())); Dirty(); };
        pulses.ViewChanged+=Dirty;
        alignment.Changed+=Dirty; tabs.SelectionChanged+=(_,e)=>{ if(ReferenceEquals(e.OriginalSource,tabs)) Dirty(); };
        channel.SelectedIndex=2; gain.SelectedIndex=1; fft.SelectedIndex=1; Source.SelectedIndex=demo?1:0;
        Source.SelectionChanged+=async(_,_)=>{ if(ready) await Enumerate(); };
        AccessMode.SelectionChanged+=async(_,_)=>{ if(ready) await Enumerate(); };
        probeNative.Click+=async(_,_)=>await ProbeNative();
        Device.SelectionChanged+=(_,_)=> { Format.ItemsSource=(Device.SelectedItem as DeviceRow)?.Device.AdvertisedFormats.Select(f=>new FormatRow(f)); Format.SelectedIndex=0; UpdateOutput(); };
        Format.SelectionChanged+=(_,_)=>UpdateOutput(); pcm24.Click+=(_,_)=>UpdateOutput();
        refresh.Click+=async(_,_)=>await Enumerate(); raw.Click+=async(_,_)=>await Enumerate(); engine.Click+=async(_,_)=>await Enumerate();
        browse.Click+=(_,_)=>{ var dialog=new SaveFileDialog { Filter="PCM WAV (*.wav)|*.wav",FileName=Path.GetFileName(Destination.Text),OverwritePrompt=true }; if(dialog.ShowDialog(this)==true) { Destination.Text=dialog.FileName; UpdateOutput(); } };
        Monitor.Click+=async(_,_)=>await Start(false); Record.Click+=async(_,_)=>await Start(true); Stop.Click+=async(_,_)=>await StopCapture();
        Cancel.Click+=async(_,_)=>await CancelCapture();
        fit.Click+=(_,_)=>{ fitHistoryActive=true; end=null; follow.IsChecked=true; Dirty(); }; zoomIn.Click+=(_,_)=>Zoom(.5,.5); zoomOut.Click+=(_,_)=>Zoom(2,.5);
        waveform.ZoomRequested+=Zoom; waveform.PanRequested+=fraction=>{ var snap=Model.Live; if(snap==null||snap.HistoryEndFrame<=snap.HistoryStartFrame) return; fitHistoryActive=false; end=(long)Math.Clamp((end??snap.HistoryEndFrame)+fraction*seconds*snap.Format.SampleRate,snap.HistoryStartFrame+1,snap.HistoryEndFrame); follow.IsChecked=false; Dirty(); };
        foreach(var choice in new[]{channel,gain,fft,slope}) choice.SelectionChanged+=(_,_)=>Dirty();
        foreach(var check in new[]{auto,follow,trigger}) check.Click+=(_,_)=>{ if(check==follow && follow.IsChecked!=true) fitHistoryActive=false; gain.IsEnabled=auto.IsChecked!=true; end=follow.IsChecked==true?null:end??Model.Live?.HistoryEndFrame; Dirty(); };
        triggerLevel.TextChanged+=(_,_)=>Dirty(); reset.Click+=(_,_)=>Guard(()=>{ if(!double.TryParse(clipLevel.Text,out double threshold)) throw new ArgumentException("Enter a clipping threshold between 1 and 100 percent FS."); Model.ResetClippingHold(threshold/100); Dirty(); });
        timer.Tick+=async(_,_)=>await Tick();
        Loaded+=async(_,_)=>{ ready=true; await Enumerate(); if(!closing) timer.Start(); };
        Closing+=OnClosing;
        Closed+=(_,_)=> { timer.Stop(); enumeration?.Cancel(); lifetime.Cancel(); };
        UpdateButtons();
    }

    private async Task Enumerate()
    {
        if(Model.IsActive||starting||closing) return;
        string? previousDevice=(Device.SelectedItem as DeviceRow)?.Device.Id;
        var previousFormat=(Format.SelectedItem as FormatRow)?.Format;
        enumeration?.Cancel(); var work=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); enumeration=work;
        backend=null; devices=[]; Device.ItemsSource=null; Format.ItemsSource=null; Monitor.IsEnabled=Record.IsEnabled=false;
        try
        {
            var candidate=Source.SelectedIndex==1 ? new SyntheticCaptureBackend(new() { FrameCount=long.MaxValue,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(10) }) : windowsFactory(new() { Mode=NativeInput?WindowsAudioCaptureMode.Exclusive:WindowsAudioCaptureMode.Shared,RequestRaw=!NativeInput&&raw.IsChecked==true,AllowEngineFormatConversion=!NativeInput&&engine.IsChecked==true });
            var found=await candidate.EnumerateDevicesAsync(work.Token);
            if(work.IsCancellationRequested||closing) return;
            backend=candidate; devices=found; Device.ItemsSource=found.Select(d=>new DeviceRow(d)).ToArray(); Device.SelectedIndex=found.Count>0?0:-1;
            if(previousDevice!=null) Device.SelectedItem=Device.Items.Cast<DeviceRow>().FirstOrDefault(row=>row.Device.Id==previousDevice)??Device.SelectedItem;
            if(previousFormat!=null&&Format.Items.Cast<FormatRow>().FirstOrDefault(row=>row.Format==previousFormat) is {} retained) Format.SelectedItem=retained;
            state.Text=found.Count==0?"No supported input. Connect a mono/stereo input and refresh, or select Synthetic demo.":"Ready — input starts only with Monitor or Record."; failure.Text="";
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { failure.Text="Cannot enumerate inputs: "+error.Message; }
        finally { if(ReferenceEquals(enumeration,work)) enumeration=null; work.Dispose(); if(!closing) { UpdateButtons(); UpdateOutput(); } }
    }
    internal async Task Start(bool recording)
    {
        if(starting||backend==null||Device.SelectedItem is not DeviceRow device||Format.SelectedItem is not FormatRow format) return;
        bool previousRecordingRun=recordingRun,attemptedStart=false;
        starting=true; startingRecording=recording; recordingRun=recording; failure.Text=""; completionNotice.Visibility=Visibility.Collapsed; UpdateButtons();
        try
        {
            WaveRecordingOptions? options=null; bool overwrite=false;
            if(recording)
            {
                string path=Path.GetFullPath(Destination.Text);
                if(!string.Equals(Path.GetExtension(path),".wav",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a .wav destination.");
                AudioPcmQuantizer.Plan(format.Format,pcm24.IsChecked==true,NativeInput);
                if(File.Exists(path)||File.Exists(path+".json")) { if(!confirmOverwrite(path)) return; overwrite=true; }
                options=new(path) { PreserveNativeFormat=NativeInput,RequireAnalyzerCompatibleRate=!NativeInput,AllowPcm24Conversion=!NativeInput&&pcm24.IsChecked==true,ApplicationVersion=typeof(App).Assembly.GetName().Version?.ToString()??"unknown",TapeNote=note.Text,TapeSide=side.Text };
            }
            end=null; follow.IsChecked=true; lastFrame=-1; shownReport=null;
            alignment.Update(null,null);
            pulses.Clear();
            attemptedStart=true; await Model.StartAsync(backend,new(device.Device.Id,format.Format),options,lifetime.Token,overwrite); settings.IsExpanded=false; Dirty();
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { failure.Text=error.Message; }
        finally { if(!attemptedStart) recordingRun=previousRecordingRun; starting=false; startingRecording=false; UpdateButtons(); await Tick(); }
    }
    internal async Task StopCapture()
    {
        if(starting||endingCapture||Model.IsBusy||closing) return;
        endingCapture=true;
        try
        {
            var stop=Model.StopAsync(); UpdateButtons(); var report=await stop;
            if(report?.Recording is {} recording)
            {
                if(!recording.SidecarFinalized||recording.Segments.Any(s=>!s.HeaderFinalized||!s.Published))
                { failure.Text="Could not finalize the recording. Review retained files and diagnostics."; return; }
                completionNotice.Text=$"Saved {recording.Segments.Count} WAV file(s): "+recording.Segments.FirstOrDefault()?.Path;
                completionNotice.ToolTip=string.Join("\n",recording.Segments.Select(s=>s.Path));
            }
            else { completionNotice.Text="Monitor stopped. No recording file was created."; completionNotice.ToolTip=null; }
            completionNotice.Visibility=Visibility.Visible; settings.IsExpanded=true; settings.BringIntoView();
        }
        catch(Exception error) { failure.Text=error.Message; }
        finally { endingCapture=false; Dirty(); await Tick(); UpdateButtons(); }
    }
    internal async Task CancelCapture()
    {
        if(starting||endingCapture||Model.IsBusy||closing||!Model.CanDiscard) return;
        if((recordingRun||Model.HasPendingDiscard)&&!confirmDiscard()) return;
        try
        {
            var discard=Model.DiscardAsync(); UpdateButtons(); await discard;
            shownReport=null; rows=Model.Recordings.Reverse().SelectMany(r=>r.Recording!.Segments.Select(segment=>new SegmentRow(segment.Path,segment.Frames,segment.DataBytes,segment.HeaderFinalized,segment.Published,segment.PcmMatchesWrittenBytes,r.Integrity.ToString(),segment.Error,r.Recording.AnalyzerCompatible))).ToArray(); segments.ItemsSource=rows;
            waveform.Snapshot=spectrum.Snapshot=null; waveform.InvalidateVisual(); spectrum.InvalidateVisual();
            pulses.Clear(); alignment.Update(null,null); failure.Text="";
            state.Text="Idle — choose an input; Monitor or Record starts capture."; scope.Text=""; levels.ItemsSource=null; diagnostics.ItemsSource=null;
            completionNotice.Text=recordingRun?"Recording discarded.":"Monitor cancelled. No recording file was created.";
            completionNotice.ToolTip=null;
            completionNotice.Visibility=Visibility.Visible; recordingRun=false;
            settings.IsExpanded=true; settings.BringIntoView(); Device.Focus();
        }
        catch(Exception error) { failure.Text="Could not discard recording: "+error.Message; }
        finally { Dirty(); await Tick(); UpdateButtons(); }
    }
    private void Zoom(double factor,double anchor)
    {
        fitHistoryActive=false; double old=seconds; seconds=Math.Clamp(seconds*factor,.00002,2);
        if(end.HasValue && Model.Live is {} snap && snap.HistoryEndFrame>snap.HistoryStartFrame) end=(long)Math.Clamp(end.Value+(seconds-old)*(1-anchor)*snap.Format.SampleRate,snap.HistoryStartFrame+1,snap.HistoryEndFrame);
        Dirty();
    }
    private void Dirty() { viewDirty=true; AudioSignalChart.MarkFit(fitHistory,fitHistoryActive); }
    private async Task Tick()
    {
        if(ticking||allowClose) return; ticking=true;
        try
        {
            var capture=Model.Capture;
            if(viewDirty || capture?.Statistics.AnalyzedFrames!=lastFrame)
            {
                if(fitHistoryActive) seconds=2;
                if(!double.TryParse(triggerLevel.Text,out double triggerValue)||!double.IsFinite(triggerValue)) triggerValue=0;
                var view=new LiveSignalViewOptions { WaveSeconds=seconds,WaveEndFrame=follow.IsChecked==true?null:end,WaveBuckets=Math.Clamp((int)Math.Max(1,waveform.ActualWidth-78),1,4096),FftSize=int.Parse((string)(tabs.SelectedItem==alignmentTab?alignment.WindowSize.SelectedItem:fft.SelectedItem)),TriggerEnabled=trigger.IsChecked==true,TriggerChannel=channel.SelectedIndex==1?1:0,TriggerLevel=triggerValue,RisingTrigger=slope.SelectedIndex==0,
                    Alignment=tabs.SelectedItem==alignmentTab?alignment.Options(capture?.Stream?.ActualFormat.SampleRate??48000):null,
                    Pulses=tabs.SelectedItem==pulsesTab?pulses.View(capture?.Stream?.ActualFormat):null };
                viewDirty=false; lastFrame=capture?.Statistics.AnalyzedFrames??-1;
                var live=await Model.RefreshAsync(view,lifetime.Token);
                if(allowClose) return;
                alignment.Update(live?.Alignment,Model.Capture?.Stream,live?.PreviewDiscontinuities??0,Model.IsActive);
                if(tabs.SelectedItem==pulsesTab) pulses.Update(live?.Pulses);
                if(live!=null)
                {
                    UpdateChannelLabels(live.Format.Channels);
                    levels.ItemsSource=live.Levels;
                    waveform.Snapshot=spectrum.Snapshot=live; waveform.Channel=spectrum.Channel=channel.SelectedIndex;
                    waveform.Gain=double.Parse((string)gain.SelectedItem,System.Globalization.CultureInfo.InvariantCulture); waveform.AutoScale=auto.IsChecked==true;
                    waveform.InvalidateVisual(); spectrum.InvalidateVisual();
                    scope.Text=$"Wave [{live.WaveStartFrame:N0}, {live.WaveEndFrame:N0}) • levels [{live.LevelStartFrame:N0}, {live.LevelEndFrame:N0}) • clip ±{live.ClipThreshold*100:G3}% FS • {live.PreviewDiscontinuities:N0} preview gaps • {live.InvalidFrames:N0} invalid frames";
                    spectrumInfo.Text=$"FFT [{live.SpectrumStartFrame:N0}, {live.SpectrumEndFrame:N0}) • {(live.Spectrum.Count==0?"waiting for a full window":$"dominant L/mono {live.LeftDominantHz:F1} Hz / R {live.RightDominantHz:F1} Hz")} • Hann peak amplitude dBFS, not PSD";
                }
            }
            capture=Model.Capture;
            if(capture!=null)
            {
                var s=capture.Statistics; var report=Model.LatestReport;
                state.Text=$"{capture.State} • received {s.ReceivedFrames:N0} • archived {s.WrittenFrames:N0} frames • preview dropped {s.AnalysisDroppedFrames:N0} • missing {s.KnownMissingFrames:N0} • unknown loss {s.HasUnknownLoss}";
                if(capture.Stream is {} stream) actual.Text=$"Requested: {Describe(stream.RequestedFormat)}\nDelivered: {Describe(stream.ActualFormat)}\n{stream.ProcessingDescription}\nDriver / hardware processing cannot be certified by RAW. Preview gain / trigger / FFT never change the archive.";
                diagnostics.ItemsSource=capture.Diagnostics;
                if(report!=null&&!ReferenceEquals(report,shownReport))
                {
                    shownReport=report;
                    rows=Model.Recordings.Reverse().SelectMany(r=>r.Recording!.Segments.Select(segment=>new SegmentRow(segment.Path,segment.Frames,segment.DataBytes,segment.HeaderFinalized,segment.Published,segment.PcmMatchesWrittenBytes,r.Integrity.ToString(),segment.Error,r.Recording.AnalyzerCompatible))).ToArray();
                    segments.ItemsSource=rows; if(rows.Count>0) segments.SelectedIndex=0;
                    if(Model.Error!=null) failure.Text=Model.Error;
                }
                if(report?.Recording is {} saved) state.Text+=$" • {report.Integrity} • {saved.SavedDurationSeconds:F2} s • sidecar {(saved.SidecarFinalized?"saved":"not finalized")}";
            }
            alignment.SetCaptureActive(Model.IsActive);
            UpdateButtons();
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { failure.Text="Preview: "+error.Message; }
        finally { ticking=false; }
    }
    private void UpdateButtons()
    {
        bool busy=starting||endingCapture||Model.IsBusy,active=Model.IsActive;
        Cancel.IsEnabled=Model.CanDiscard&&!busy&&!starting&&!closing;
        gain.IsEnabled=auto.IsChecked!=true;
        AudioSignalChart.MarkFit(fitHistory,fitHistoryActive);
        foreach(var control in inputControls) control.IsEnabled=!active&&!busy;
        bool available=backend!=null&&Device.SelectedItem!=null&&Format.SelectedItem!=null&&enumeration==null;
        Monitor.IsEnabled=available&&!active&&!busy&&!Model.HasPendingDiscard;
        Record.IsEnabled=available&&!busy&&!Model.HasPendingDiscard&&(!active||Model.State==CaptureSessionState.Monitoring);
        Stop.IsEnabled=active&&!starting&&!busy&&Model.State!=CaptureSessionState.Stopping;
        foreach(var control in recordingControls) control.IsEnabled=!busy&&Model.State!=CaptureSessionState.Recording&&Model.State!=CaptureSessionState.Stopping;
        raw.IsEnabled=engine.IsEnabled=!NativeInput&&!active&&!busy;
        pcm24.IsEnabled=pcm24.IsEnabled&&!NativeInput;
        probeNative.IsEnabled=NativeInput&&!active&&!busy&&backend!=null&&enumeration==null&&Device.SelectedItem!=null;
    }
    private void UpdateOutput()
    {
        if(Format.SelectedItem is not FormatRow format) { output.Text="No input format."; return; }
        UpdateChannelLabels(format.Format.Channels);
        try { var conversion=AudioPcmQuantizer.Plan(format.Format,pcm24.IsChecked==true,NativeInput); output.Text=$"WAV: {Describe(conversion.Output)} • {(conversion.ChangesContainer?"explicit PCM24 conversion":"byte-exact delivered samples")} • automatic RIFF splitting / SHA-256"+(NativeInput?" • driver-accepted, no resampling; physical ADC resolution unknown":"")+(RiffWaveLayout.AnalyzerSupports(conversion.Output,NativeInput)?"":" • archive supported; current offline analyzers cannot read this format"); }
        catch(Exception error) { output.Text=error.Message; }
    }
    private void UpdateChannelLabels(int count)
    {
        string first=count==1?"Mono":"Left";
        if(channel.Items.Count>0&&Equals(channel.Items[0],first)) return;
        int selected=channel.SelectedIndex;
        channel.ItemsSource=count==1?new[]{"Mono"}:new[]{"Left","Right","Overlay L/R"};
        channel.SelectedIndex=count==1?0:Math.Clamp(selected,0,2);
    }
    private async Task ProbeNative()
    {
        if(!NativeInput||backend==null||Device.SelectedItem is not DeviceRow device||Model.IsActive||starting) return;
        starting=true; UpdateButtons();
        try
        {
            if(!int.TryParse(nativeRate.Text,out int rate)||!int.TryParse(nativeBits.Text,out int valid)) throw new ArgumentException("Enter integer Hz and valid bits.");
            var representations=new[]{AudioSampleRepresentation.Unsigned8,AudioSampleRepresentation.Signed16,AudioSampleRepresentation.Signed24,AudioSampleRepresentation.Signed32,AudioSampleRepresentation.Float32};
            var format=new AudioCaptureFormat(rate,nativeChannels.SelectedIndex+1,representations[nativeEncoding.SelectedIndex],valid);
            await using var session=await backend.OpenAsync(new(device.Device.Id,format),lifetime.Token);
            if(session.Info.ActualFormat!=format) throw new InvalidOperationException("Exact format was not accepted.");
            var formats=Format.Items.Cast<FormatRow>().ToList(); if(!formats.Any(row=>row.Format==format)) formats.Add(new(format));
            Format.ItemsSource=formats; Format.SelectedItem=formats.First(row=>row.Format==format);
            failure.Text=""; state.Text="Exact format accepted by driver. Input has not been started; availability is checked again at Start.";
        }
        catch(Exception error) { failure.Text="Native format: "+error.Message; }
        finally { starting=false; UpdateButtons(); UpdateOutput(); }
    }
    internal async Task OpenSelected(RecordedAudioAction action)
    {
        try
        {
            if(segments.SelectedItem is not SegmentRow selected) return;
            if(!selected.HeaderFinalized||selected.Frames==0||!selected.AnalyzerCompatible) throw new InvalidOperationException("Select a finalized, non-empty segment in an analyzer-compatible format.");
            var recording=Model.Recordings.Select(r=>r.Recording).FirstOrDefault(r=>r?.Segments.Any(s=>s.Path==selected.Path)==true);
            string path=selected.Path;
            if(recording is { Segments.Count:>1 })
            {
                if(!recording.SidecarFinalized||!recording.AnalyzerCompatible) throw new InvalidOperationException("A complete compatible recording report is required to join segments.");
                path=recording.SidecarPath!;
            }
            await navigation.OpenAudioAsync(path,action,lifetime.Token);
        }
        catch(Exception error) { failure.Text=error.Message; }
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(allowClose) return;
        e.Cancel=true;
        if(await PrepareToCloseAsync()) _ = Dispatcher.BeginInvoke(new Action(()=>{ if(IsVisible) Close(); }));
    }
    internal Task<bool> PrepareToCloseAsync()
    {
        if(allowClose) return Task.FromResult(true);
        if(closeAttempt is { IsCompletedSuccessfully:true }&&!closeAttempt.Result) closeAttempt=null;
        return closeAttempt??=PrepareCloseCoreAsync();
    }
    private async Task<bool> PrepareCloseCoreAsync()
    {
        bool archiveActive=startingRecording||Model.State==CaptureSessionState.Recording||recordingRun&&(Model.State is CaptureSessionState.Starting or CaptureSessionState.Stopping);
        if(archiveActive&&!confirmClose()) return false;
        closing=true; timer.Stop(); enumeration?.Cancel();
        try
        {
            var previousReport=Model.LatestReport;
            if(!starting&&!Model.IsBusy&&Model.State==CaptureSessionState.Recording) await Model.StopAsync();
            lifetime.Cancel();
            await Model.DisposeAsync();
            if(!ReferenceEquals(previousReport,Model.LatestReport)&&Model.LatestReport?.Integrity==CaptureIntegrity.Incomplete)
            { failure.Text="Capture ended with incomplete data. Review the retained recording and diagnostics before closing again."; closing=false; timer.Start(); return false; }
            allowClose=true; return true;
        }
        catch(Exception error) { failure.Text="Cannot finish closing: "+error.Message; closing=false; timer.Start(); return false; }
    }
    private void Guard(Action action) { try { action(); } catch(Exception error) { failure.Text=error.Message; } }
    private void ConfigureLevels()
    {
        levels.AutoGenerateColumns=false;
        void Column(string label,string property,string? format=null,int width=85) => levels.Columns.Add(new DataGridTextColumn { Header=label,Binding=new Binding(property) { StringFormat=format,TargetNullValue="−∞" },Width=width });
        Column("Channel","Channel",width:70); Column("Peak FS","Peak","F4"); Column("RMS FS","Rms","F4"); Column("DC FS","Dc","F4");
        Column("Peak dBFS","PeakDbfs","F2",90); Column("RMS dBFS","RmsDbfs","F2",90); Column("Clip count","ClippedSamples","N0"); Column("Clip hold","ClippingHold","N0"); Column("Clipped %","ClippingPercent","F3");
        var style=new Style(typeof(DataGridRow));
        var clipped=new DataTrigger { Binding=new Binding("ClippingHold"),Value=0L };
        style.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Firebrick)); clipped.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black)); style.Triggers.Add(clipped); levels.RowStyle=style;
    }
    private static string Describe(AudioCaptureFormat f)=>$"{f.SampleRate:N0} Hz / {f.BitsPerSample} bit ({f.ValidBitsPerSample} valid) / {f.Channels} ch / {f.Representation}";
    private static WrapPanel Row()=>new() { Margin=new(0,3,0,3) };
    private static Button Button(string text)=>new() { Content=text,Padding=new(9,4,9,4),Margin=new(3) };
    private static TextBlock Text(string text)=>new() { Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(3) };
    private static CheckBox Check(string text,bool value=false)=>new() { Content=text,IsChecked=value,Margin=new(7,5,7,5),VerticalAlignment=VerticalAlignment.Center };
    private static ComboBox Choice(params string[] choices)=>new() { ItemsSource=choices,SelectedIndex=choices.Length>0?0:-1,MinWidth=85,Margin=new(3) };
    private static void Add(Panel row,string text,FrameworkElement control) { row.Children.Add(new TextBlock { Text=text,Margin=new(5),VerticalAlignment=VerticalAlignment.Center }); row.Children.Add(control); }
    private static DataGrid Table()
    {
        var table=new DataGrid { IsReadOnly=true,AutoGenerateColumns=true,CanUserAddRows=false,CanUserResizeColumns=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,Margin=new(3),EnableRowVirtualization=true,EnableColumnVirtualization=true };
        var style=new Style(typeof(DataGridCell)); var selected=new Trigger { Property=DataGridCell.IsSelectedProperty,Value=true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.RoyalBlue)); selected.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White)); style.Triggers.Add(selected); table.CellStyle=style;
        return table;
    }
}
