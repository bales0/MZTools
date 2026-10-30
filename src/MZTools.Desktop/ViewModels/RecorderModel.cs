using System.Collections.ObjectModel;
using System.Globalization;
using MZTools.Capture;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public interface IRecorderBackendFactory
{
    IReadOnlyList<string> Choices {get;}
    IAudioCaptureBackend Create(int index,bool raw,bool engineConversion);
}

/// <summary>UI adapter only. CaptureSession owns archive priority, buffers and finalization.</summary>
public sealed class RecorderModel : Observable,IAsyncDisposable
{
    private readonly IRecorderBackendFactory factory;
    private readonly AudioRecorderViewModel recorder=new();
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? pending;
    private IAudioCaptureBackend? backend;
    private bool busy,disposed,refreshing;
    private TaskCompletionSource? operationFinished,refreshFinished;
    private Task? disposal;
    private readonly object disposalGate=new();
    private int backendIndex,channelMode,tabIndex;
    private AudioCaptureDevice? device;
    private AudioCaptureFormat? format;
    private LiveSignalSnapshot? live;
    private string status="Idle. Select an input explicitly; constructing this view never opens a device.",error="";
    public RecorderModel(IRecorderBackendFactory factory){this.factory=factory;}
    public IReadOnlyList<string> Backends=>factory.Choices;
    public int BackendIndex {get=>backendIndex;set{if(!CanConfigure||value<0||value>=Backends.Count)return;if(Set(ref backendIndex,value)){Devices.Clear();Device=null;backend=null;}}}
    public ObservableCollection<AudioCaptureDevice> Devices {get;}=[];
    public AudioCaptureDevice? Device {get=>device;set{if(!CanConfigure)return;if(Set(ref device,value)){Changed(nameof(Formats));Format=Formats.FirstOrDefault();}}}
    public IReadOnlyList<AudioCaptureFormat> Formats=>Device?.AdvertisedFormats??[];
    public AudioCaptureFormat? Format
    {
        get=>format;
        set
        {
            if(!CanConfigure||!Set(ref format,value))return;ChannelMode=0;PulseChannel=0;
            foreach(var name in new[]{nameof(CanStart),nameof(CanMonitor),nameof(Channels),nameof(PulseMaximumChannel),nameof(PulseChannel)})Changed(name);
        }
    }

    public bool Raw {get;set;}=true;
    public bool EngineConversion {get;set;}
    public bool AllowFallback {get;set;}
    public bool PreserveNative {get;set;}
    public bool AllowPcm24 {get;set;}
    public string Note {get;set;}="";
    public string TapeSide {get;set;}="";
    public bool IsBusy {get=>busy;private set{if(Set(ref busy,value))NotifyState();}}
    public bool IsActive=>recorder.IsActive;
    public bool CanConfigure=>!IsActive&&!IsBusy&&!disposed;
    public bool CanStart=>!IsBusy&&!disposed&&Device is not null&&Format is not null&&(!IsActive||recorder.State==CaptureSessionState.Monitoring);
    public bool CanMonitor=>CanStart&&!IsActive;
    public bool CanDiscard=>!IsBusy&&recorder.CanDiscard&&!disposed;
    public bool CanStop=>IsActive&&!IsBusy;
    public string Status {get=>status;private set=>Set(ref status,value);}
    public string Error {get=>error;private set{Set(ref error,value);Changed(nameof(HasError));}}
    public bool HasError=>Error.Length>0;
    public void ReportError(string value)=>Error=value;
    public LiveSignalSnapshot? Live {get=>live;private set=>Set(ref live,value);}
    private IReadOnlyList<CaptureSessionReport> recordings=[];
    public IReadOnlyList<CaptureSessionReport> Recordings=>recordings;
    private CaptureSessionReport? selectedRecording;
    public CaptureSessionReport? SelectedRecording {get=>selectedRecording;set{if(Set(ref selectedRecording,value))Changed(nameof(RecordingDetails));}}
    public string RecordingDetails=>SelectedRecording is { } r?$"{r.Integrity} · {r.Reason} · {r.Recording?.SavedFrames:N0} saved frames\n{r.Recording?.SidecarPath}\n"+string.Join("\n",r.Diagnostics.Select(d=>$"{d.Code}: {d.Message}")):"Select a finalized recording. Incomplete output is never labelled complete.";
    public string Statistics=>recorder.Capture is { } c?$"{c.State} · received {c.Statistics.ReceivedFrames:N0} · archived {c.Statistics.WrittenFrames:N0} · archive missing {c.Statistics.KnownMissingFrames:N0} / unknown loss {c.Statistics.HasUnknownLoss}\nPreview dropped {c.Statistics.AnalysisDroppedFrames:N0} frames · buffers {c.Statistics.AllocatedBufferBytes:N0} B · archive/preview queues {c.Statistics.ArchiveQueueDepth}/{c.Statistics.AnalysisQueueDepth}":"No capture session.";
    public string Processing=>recorder.Capture?.Stream is { } s?$"Requested {FormatLabel(s.RequestedFormat)} → delivered {FormatLabel(s.ActualFormat)}\n{s.ProcessingDescription}\nAccess {s.Processing?.AccessMode??"unspecified"} · RAW {(s.Processing?.RawActive is { } raw?raw.ToString():"unknown")} · {s.Processing?.EffectsStatus}":"Device processing and ADC precision are unknown until the backend reports them.";
    private static string FormatLabel(AudioCaptureFormat f)=>$"{f.SampleRate:N0} Hz · {f.Channels} ch · {f.Representation} ({f.ValidBitsPerSample} valid bits)";
    public long? SelectionStart {get;private set;}
    public long? SelectionEnd {get;private set;}
    public string SelectionSummary=>SelectionStart is { } first&&SelectionEnd is { } last?$"Selected source frames [{first:N0}, {last:N0}) · preview only":"Shift+drag selects original frame positions. Drag pans; wheel zooms. Display changes never modify the archive.";
    public void SelectFrames(long first,long last){SelectionStart=Math.Min(first,last);SelectionEnd=Math.Max(first,last);FollowLive=false;WaveEndFrame=Live?.WaveEndFrame;foreach(var name in new[]{nameof(SelectionStart),nameof(SelectionEnd),nameof(SelectionSummary),nameof(FollowLive)})Changed(name);}
    public int TabIndex {get=>tabIndex;set=>Set(ref tabIndex,value);}
    public int ChannelMode {get=>channelMode;set=>Set(ref channelMode,Math.Clamp(value,0,DisplayChannels==1?0:2));}
    public IReadOnlyList<string> Channels=>DisplayChannels==1?["Mono"]:["Left","Right","Overlay"];
    private int DisplayChannels=>recorder.Capture?.Stream?.ActualFormat.Channels??Format?.Channels??1;
    private int lastDisplayChannels;
    private CaptureSessionState lastState;
    private bool autoScale=true;private double gain=1;
    public bool AutoScale {get=>autoScale;set=>Set(ref autoScale,value);}
    public double Gain {get=>gain;set{if(double.IsFinite(value)&&value>0&&value<=1000){Set(ref gain,value);AutoScale=false;}}}
    public bool FollowLive {get;set;}=true;
    public double WaveSeconds {get;private set;}=.1;
    public long? WaveEndFrame {get;private set;}
    public bool TriggerEnabled {get;set;}
    public double TriggerLevel {get;set;}
    public bool RisingTrigger {get;set;}=true;
    public int FftSize {get;set;}=4096;
    public IReadOnlyList<int> FftChoices {get;}=[256,512,1024,2048,4096,8192,16384,32768];
    public bool AlignmentEnabled {get;set;}=true;
    public double BandLow {get;set;}=300;
    public double BandSplit {get;set;}=3000;
    public double BandHigh {get;set;}=12000;
    public string KnownTone {get;set;}="";
    public int MaximumLag {get;set;}=48;
    public string Passage {get;set;}="";
    public bool SamePassage {get;set;}
    public HeadAlignmentComparisonModel AlignmentComparison {get;}=new();
    public bool CanSaveAlignment=>Live?.Alignment is { } m&&m.Channels.All(c=>c.Assessable)&&recorder.Capture?.Stream is not null;
    public int PulseMaximumChannel=>DisplayChannels-1;
    public HeadAlignmentComparison Comparison=>AlignmentComparison.Compare();
    public string AlignmentDetails=>Live?.Alignment is { } a?$"{a.Method}\nFrames [{a.StartFrame:N0}, {a.EndFrame:N0})\n{a.Stereo.Status}\n{HeadAlignmentMeasurement.Limitation}":HeadAlignmentMeasurement.Limitation;
    public IReadOnlyList<HeadAlignmentChannel> AlignmentChannels=>Live?.Alignment?.Channels??[];
    public HeadAlignmentStereo? Stereo=>Live?.Alignment?.Stereo;
    public string ComparisonSummary=>Comparison.Status;
    public IReadOnlyList<HeadAlignmentDifference> Differences=>Comparison.Differences;
    public HeadEdgeAdjustmentModel[] EdgeModels {get;}=[new(),new()];
    public HeadEdgeAdjustmentSnapshot LeftEdge=>EdgeModels[0].Current;
    public HeadEdgeAdjustmentSnapshot? RightEdge=>Format?.Channels==1?null:EdgeModels[1].Current;
    public bool CanZeroEdges=>EdgeModels.Any(m=>m.Current.CanZero);
    private bool sameSection;private string copyLabel="";
    public bool SameSection {get=>sameSection;set{if(Set(ref sameSection,value))ClearEdges();}}
    public string CopyLabel {get=>copyLabel;set{if(Set(ref copyLabel,value))ClearEdges();}}
    public double EdgeDamping {get;set;}=.5;
    private double edgeRange=50;
    public double EdgeRange {get=>edgeRange;set{if(double.IsFinite(value)&&value>=1&&value<=1000)Set(ref edgeRange,value);}}
    private static double PresentationClock=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
    public void ZeroEdges(){foreach(var m in EdgeModels)m.ZeroHere(PresentationClock);NotifyEdges();}
    public void ClearEdges(){foreach(var m in EdgeModels)m.Reset("Manual zero cleared; wait for fresh qualified measurements.");NotifyEdges();}
    public void ClearComparison(){AlignmentComparison.Clear();Changed(nameof(Comparison));Changed(nameof(Differences));Changed(nameof(ComparisonSummary));}
    private void NotifyEdges(){Changed(nameof(LeftEdge));Changed(nameof(RightEdge));Changed(nameof(CanZeroEdges));}
    private void UpdateEdges()
    {
        double now=PresentationClock;var stream=recorder.Capture?.Stream;var measurement=Live?.Alignment;
        for(int channel=0;channel<EdgeModels.Length;channel++)
        {
            var model=EdgeModels[channel];model.SetDamping(EdgeDamping);
            if(!IsActive||stream is null||channel>=stream.ActualFormat.Channels){model.SetActive(false,now);continue;}
            model.Update(measurement,new(stream.Device.Id,stream.ProcessingDescription+stream.Processing,Live?.PreviewDiscontinuities??0,CopyLabel),channel,SameSection,now);
        }
        NotifyEdges();
    }
    public IReadOnlyList<StereoMetricRow> StereoRows=>Stereo is { } s?[
        new("R−L phase",s.SignalPhaseDegrees?.ToString("0.###")+" °","Known tone only, modulo one cycle; not mechanical head angle"),
        new("Right delay",s.DelayMicroseconds?.ToString("0.###")+" µs","Positive means Right lags Left; suppressed for ambiguous/unstable evidence"),
        new("Coherence",s.Coherence?.ToString("0.###")??"Unavailable","Four non-overlapping Hann segments; not a single FFT-bin score"),
        new("Zero-lag correlation",s.ZeroLagCorrelation?.ToString("0.###")??"Unavailable","DC-removed comparison of actual channels"),
        new("Best correlation",s.BestCorrelation?.ToString("0.###")??"Unavailable","Lag-limited search; periodic signal may be ambiguous"),
        new("R−L level difference",s.LevelDifferenceDb?.ToString("0.###")+" dB","Level difference is not azimuth"),
        new("Tone purity / phase stability",$"{s.TonePurity:0.###} / {s.PhaseStability:0.###}","Qualification of the explicitly declared reference tone"),
        new("Lag spread",s.LagSpreadSamples?.ToString("0.###")+" samples","Segment agreement; peak uniqueness: "+s.DelayPeakUnique)]:[];
    public bool PulsesEnabled {get;set;}
    private readonly List<AudioPulseReference> pulseReferences=AudioPulseReferences.Choices.ToList();
    public int PulseReferenceIndex {get;set;}
    public void AddPulseReference(string name,double sh,double sl,double lh,double ll){pulseReferences.Add(AudioPulseReference.User(name,sh,sl,lh,ll));Changed(nameof(PulseReferences));PulseReferenceIndex=pulseReferences.Count-1;Changed(nameof(PulseReferenceIndex));}
    private bool showPulseReference=true;
    public bool ShowPulseReference {get=>showPulseReference;set=>Set(ref showPulseReference,value);}
    public double PulseWindowSeconds {get;set;}=2;
    public int PulseDetector {get;set;}
    public int PulseChannel {get;set;}
    public bool PulseZero {get;set;}=true;
    public bool PulseSchmitt {get;set;}=true;
    public bool PulseAdaptive {get;set;}=true;
    public bool PulseInvert {get;set;}=true;
    public double PulseThreshold {get;set;}=1;
    public double PulseDeadband {get;set;}
    public double PulseTolerance {get;set;}=20;
    public double PulseScale {get;set;}=1;
    private double? pulseMin,pulseMax;
    public IReadOnlyList<string> PulseReferences=>pulseReferences.Select(r=>r.Name).ToArray();
    public IReadOnlyList<string> PulseDetectors {get;}=["Zero crossing","Hysteresis","Adaptive midpoint"];
    public IReadOnlyList<LivePulseDistribution> PulseDistributions=>Live?.Pulses?.Distributions??[];
    public IReadOnlyList<LivePulseStreamStatus> PulseStreams=>Live?.Pulses?.Streams??[];
    public string PulseDetails=>Live?.Pulses is { } p?$"{p.Options.Reference.Name} · {p.Options.Reference.Kind}\n{p.Options.Reference.Source}\n{p.Options.Reference.Evidence}\n{p.Meaning}\n{p.Preprocessing}\nMeasured {p.MeasuredPulses:N0}; reference matches {p.ReferenceMatches:N0}; unclassified {p.Unclassified:N0}; continuity resets {p.ContinuityResets:N0}: {p.ResetReason}":"Pulse analysis is optional. Apply settings explicitly; independent streams are never pooled.";
    public void ApplyPulses()
    {
        try
        {
            LivePulseReference? reference=PulsesEnabled?ProjectReference(pulseReferences[Math.Clamp(PulseReferenceIndex,0,PulseReferences.Count-1)]):null;
            recorder.ConfigurePulses(reference is null?null:new(){Reference=reference,ZeroCrossing=PulseZero,Hysteresis=PulseSchmitt,Adaptive=PulseAdaptive,InvertPolarity=PulseInvert,ThresholdScale=PulseThreshold,AdaptiveDeadbandFs=PulseDeadband/100,Tolerance=PulseTolerance/100,ReferenceScale=PulseScale,WindowSeconds=PulseWindowSeconds});Error="";
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){Error=ex.Message;}
    }
    private static LivePulseReference ProjectReference(AudioPulseReference r)=>new(r.CatalogId??"USER:"+r.Name,r.Name,r.CatalogId is null?"User":PulseReferenceCatalog.Version,r.Source,r.Evidence+" · "+r.Derivation,r.Kind.ToString(),r.Guides.Select(g=>new LivePulseGuide((LivePulseGroup)Array.IndexOf(AudioPulseReference.Groups,g.Group),g.Microseconds,g.Context)).ToArray());
    public Task EnumerateAsync()=>RunAsync(async ct=>
    {
        if(IsActive)throw new InvalidOperationException("Stop capture before changing inputs.");
        backend=factory.Create(BackendIndex,Raw,EngineConversion);var found=await backend.EnumerateDevicesAsync(ct);ct.ThrowIfCancellationRequested();
        Devices.Clear();foreach(var d in found)Devices.Add(d);device=null;format=null;Changed(nameof(Device));Changed(nameof(Formats));Changed(nameof(Format));Status=$"{Devices.Count} inputs. Select a device and delivered format; enumeration does not start capture.";
    });
    public Task StartAsync(string? destination=null,bool overwrite=false)=>RunAsync(async ct=>
    {
        if(backend is null||Device is null||Format is null)throw new InvalidOperationException("Enumerate and explicitly select input/format first.");
        WaveRecordingOptions? recording=destination is null?null:new(destination){PreserveNativeFormat=PreserveNative,AllowPcm24Conversion=AllowPcm24,RequireAnalyzerCompatibleRate=!PreserveNative,ApplicationVersion="0.9.9",TapeNote=Note,TapeSide=TapeSide};
        backend=factory.Create(BackendIndex,Raw,EngineConversion);Live=null;foreach(var model in EdgeModels)model.Reset("New capture — set a fresh session-local zero.");NotifyEdges();SelectionStart=SelectionEnd=null;Changed(nameof(SelectionStart));Changed(nameof(SelectionEnd));Changed(nameof(SelectionSummary));
        await recorder.StartAsync(backend,new(Device.Id,Format,AllowFallback),recording,ct,overwrite);Status=recording is null?"Monitoring · no archive writer":"Recording · archive has priority over preview";
    });
    public Task StopAsync()=>RunAsync(async _=>{await recorder.StopAsync();Status="Stopped and finalized. Inspect integrity and diagnostics before using output.";});
    public Task DiscardAsync()=>RunAsync(async _=>{await recorder.DiscardAsync();Status="Current run discarded; earlier recordings retained.";});
    public void Cancel()=>pending?.Cancel();
    public void ResetClipping()=>recorder.ResetClippingHold();
    public void FitWave(){FollowLive=true;WaveEndFrame=null;WaveSeconds=.1;Changed(nameof(FollowLive));}
    public void ZoomWave(double factor,double anchor=.5)
    {double old=WaveSeconds;WaveSeconds=Math.Clamp(WaveSeconds/factor,1.0/Math.Max(1,Live?.Format.SampleRate??48000),2);if(!FollowLive&&WaveEndFrame is { } end)WaveEndFrame=end-(long)((old-WaveSeconds)*(1-anchor)*(Live?.Format.SampleRate??48000));Changed(nameof(WaveSeconds));}
    public void PanWave(long frames)
    {if(Live is not { } s)return;FollowLive=false;WaveEndFrame=Math.Clamp((WaveEndFrame??s.WaveEndFrame)+frames,s.HistoryStartFrame+1,s.HistoryEndFrame);Changed(nameof(FollowLive));}
    public void ZoomPulse(double factor,double anchor=.5)
    {if(Live?.Pulses is not { } p)return;double first=pulseMin??p.HistogramMinimumMicroseconds,last=pulseMax??p.HistogramMaximumMicroseconds,width=Math.Clamp((last-first)/factor,.01,1e9);pulseMin=Math.Max(0,first+(last-first-width)*anchor);pulseMax=Math.Min(1e9,pulseMin.Value+width);}
    public void PanPulse(double microseconds){if(Live?.Pulses is { } p){double width=(pulseMax??p.HistogramMaximumMicroseconds)-(pulseMin??p.HistogramMinimumMicroseconds);pulseMin=Math.Clamp((pulseMin??p.HistogramMinimumMicroseconds)+microseconds,0,1e9-width);pulseMax=pulseMin+width;}}
    public void FitPulse(){pulseMin=pulseMax=null;}
    public void FitPulseReference(){var active=Live?.Pulses?.Options;var guides=active is null?pulseReferences[Math.Clamp(PulseReferenceIndex,0,pulseReferences.Count-1)].Guides.Select(g=>g.Microseconds*PulseScale).ToArray():active.Reference.Guides.Select(g=>g.Microseconds*active.ReferenceScale).ToArray();double min=guides.Min(),max=guides.Max(),pad=Math.Max(1,(max-min)*.15);pulseMin=Math.Max(0,min-pad);pulseMax=max+pad;}
    public IReadOnlyList<LivePulseTimingWindow> PulseTiming=>Live?.Pulses?.Timing??[];
    public IReadOnlyList<LivePulseComparison> RecentPulses=>Live?.Pulses?.RecentPulses??[];
    public void SaveAlignment(bool a)
    {
        if(!CanSaveAlignment||Live?.Alignment is not { } m||recorder.Capture?.Stream is not { } stream){Error="No qualified alignment measurement is available yet.";return;}
        AlignmentComparison.Save(a,m,new(stream.Device.Id,stream.ProcessingDescription+stream.Processing,Passage,SamePassage));Changed(nameof(Comparison));Changed(nameof(Differences));Changed(nameof(ComparisonSummary));
    }
    public async Task RefreshAsync()
    {
        if(disposed||refreshing||IsBusy)return;refreshing=true;var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);refreshFinished=finished;
        try
        {
            var view=new LiveSignalViewOptions{WaveSeconds=WaveSeconds,WaveEndFrame=FollowLive?null:WaveEndFrame,WaveBuckets=1200,FftSize=FftSize,TriggerEnabled=TriggerEnabled,TriggerLevel=TriggerLevel,TriggerChannel=ChannelMode==1?1:0,RisingTrigger=RisingTrigger,
                Alignment=AlignmentEnabled?new(){BandLowHz=BandLow,BandSplitHz=BandSplit,BandHighHz=BandHigh,KnownToneHz=string.IsNullOrWhiteSpace(KnownTone)?null:double.Parse(KnownTone,CultureInfo.InvariantCulture),MaximumLagSamples=MaximumLag}:null,
                Pulses=PulsesEnabled?new(){Detector=(LivePulseDetector)PulseDetector,Channel=PulseChannel,MinimumMicroseconds=pulseMin,MaximumMicroseconds=pulseMax}:null};
            var snapshot=await recorder.RefreshAsync(view,lifetime.Token);if(disposed)return;Live=snapshot;UpdateEdges();Error=recorder.Error??"";NotifyState();
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is not OutOfMemoryException){if(!disposed){Error=ex.Message;foreach(var model in EdgeModels)model.Reset("Invalid or unavailable analysis settings; waiting for a fresh measurement.");NotifyEdges();}}
        finally{refreshing=false;finished.TrySetResult();}
    }
    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if(IsBusy||disposed)return;var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);operationFinished=finished;IsBusy=true;pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);Error="";
        try{await action(pending.Token);}catch(OperationCanceledException){Status="Cancelled. Accepted archive data is retained and finalized by the session.";}catch(Exception ex)when(ex is not OutOfMemoryException){Error=ex.Message;}
        finally{pending.Dispose();pending=null;IsBusy=false;NotifyState();finished.TrySetResult();}
    }
    private void NotifyState()
    {
        if(lastDisplayChannels!=DisplayChannels){lastDisplayChannels=DisplayChannels;ChannelMode=0;PulseChannel=0;Changed(nameof(Channels));Changed(nameof(PulseChannel));Changed(nameof(PulseMaximumChannel));}
        var history=recorder.Recordings;
        if(lastState!=recorder.State){lastState=recorder.State;if(lastState is CaptureSessionState.Completed or CaptureSessionState.Failed){var report=recorder.LatestReport;Status=$"{lastState} · {report?.Integrity} · {report?.Reason}. Inspect report and loss counters.";}}if(history.Count!=recordings.Count||history.LastOrDefault()!=recordings.LastOrDefault()){recordings=history;Changed(nameof(Recordings));}
        foreach(var name in new[]{nameof(IsActive),nameof(CanConfigure),nameof(CanStart),nameof(CanMonitor),nameof(CanDiscard),nameof(CanStop),nameof(Statistics),nameof(Processing),nameof(PulseDistributions),nameof(PulseStreams),nameof(PulseDetails),nameof(AlignmentDetails),nameof(AlignmentChannels),nameof(CanSaveAlignment),nameof(PulseMaximumChannel),nameof(Stereo),nameof(StereoRows),nameof(PulseTiming),nameof(RecentPulses)})Changed(name);
        if(SelectedRecording is null&&Recordings.Count>0)SelectedRecording=Recordings[^1];
    }
    public ValueTask DisposeAsync(){lock(disposalGate)return new(disposal??=DisposeCoreAsync());}
    private async Task DisposeCoreAsync()
    {
        disposed=true;var operation=operationFinished?.Task;var preview=refreshFinished?.Task;lifetime.Cancel();pending?.Cancel();
        await recorder.DisposeAsync();if(operation is not null)await operation;if(preview is not null)await preview;Live=null;lifetime.Dispose();
    }
}

public sealed record StereoMetricRow(string Metric,string Value,string Meaning);
