using System.Globalization;
using System.Security.Cryptography;

namespace MZTools.Desktop.Documents;

/// <summary>Read-only audio source plus independent analysis/view state. No capture backend is created.</summary>
public sealed partial class AudioSession : DocumentSession,IFileSetDocument
{
    internal AudioSignalAnalysis Model {get;}
    private readonly Dictionary<string,string?> sources;
    IReadOnlyDictionary<string,string?> IFileSetDocument.SourceHashes=>sources;
    internal IReadOnlyDictionary<string,string?> SourceHashes=>sources;
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? rangeWork;
    private long viewGeneration,settingsGeneration,selectionGeneration;
    private bool disposed,settingsDirty,pendingRecovery;
    public override bool CanSave=>false;
    public override bool CanSaveAs=>false;
    public override bool HasUnsavedChanges=>HasDraft;
    public bool HasTransformDraft=>settingsDirty;
    public bool HasDraft=>settingsDirty||pendingRecovery;
    public override string SaveDisabledReason=>"Audio source is read-only. Export creates a separate WAV/report/program; Save never rewrites the recording.";
    public string Summary=>$"{Model.Format} · {Model.SampleRate:N0} Hz · {Model.BitsPerSample}-bit · {Model.ChannelCount} ch · {Model.FrameCount:N0} frames · {Model.DurationSeconds:0.###} s";
    public string Quality=>Model.Quality+" · "+Model.QualityExplanation;
    private string? baseReport;
    public string Report=>(baseReport??=AudioAnalysisReport.Export(Model,"txt"))+"\n\n"+MeasurementSummary+"\n"+SpectralSummary+"\n"+RecoverySummary;
    public string Limits=>Model.Limits;
    public IReadOnlyList<AudioBlockRow> Blocks {get;}
    public IReadOnlyList<AudioRegionRow> Regions {get;}
    public IReadOnlyList<AudioLevelRow> Levels {get;}
    public IReadOnlyList<AudioAgreementRow> Agreement {get;}
    private readonly IReadOnlyList<AudioIssueRow> issues;
    private string issueFilter="All";
    public IReadOnlyList<string> IssueFilters {get;}=["All","Unsafe","Error","Warning","Info"];
    public string IssueFilter {get=>issueFilter;set{if(Set(ref issueFilter,value))Changed(nameof(Issues));}}
    public IReadOnlyList<AudioIssueRow> Issues=>issues.Where(i=>IssueFilter=="All"||i.Severity==IssueFilter).ToArray();
    private int viewIndex,channelMode,pulseViewIndex,spectralViewIndex;
    public int PulseViewIndex {get=>pulseViewIndex;set=>Set(ref pulseViewIndex,value);}
    public int SpectralViewIndex {get=>spectralViewIndex;set=>Set(ref spectralViewIndex,value);}
    public int ViewIndex {get=>viewIndex;set=>Set(ref viewIndex,value);}
    public IReadOnlyList<string> Channels=>Model.ChannelCount==1?["Mono"]:["Left","Right","Overlay","Average"];
    public int ChannelMode
    {
        get=>channelMode;
        set{if(value<0||value>=Channels.Count)return;if(Set(ref channelMode,value)){ClearSpectral();InvalidateView();Changed(nameof(TransformDescription));}}
    }
    private long viewStart,viewEnd,selectionStart,selectionEnd;
    public long ViewStart=>viewStart;public long ViewEnd=>viewEnd;
    public long SelectionStart=>selectionStart;public long SelectionEnd=>selectionEnd;
    public string SelectionSummary=>$"Frames [{SelectionStart:N0}, {SelectionEnd:N0}) · {(SelectionEnd-SelectionStart)/(double)Model.SampleRate:0.######} s · {SelectedIntervals.Count} interval(s)";
    internal IReadOnlyList<(long Start,long End)> SelectedIntervals {get;private set;}=[];
    internal AudioPcmTransform DisplayedTransform {get;private set;}=new();
    internal WaveformRenderModel DisplayWaveform {get;private set;}
    private AudioBlockRow? selectedBlock;
    private AudioRegionRow? selectedRegion;
    private AudioIssueRow? selectedIssue;
    public AudioBlockRow? SelectedBlock
    {
        get=>selectedBlock;
        set
        {
            if(!Set(ref selectedBlock,value))return;
            if(value is not null){SetSelection([(value.Start,value.End)]);MeasuredBlock=value.Value;NotifyMeasurement();if(FollowReference)FollowBlockReference(value.Value);}
            Changed(nameof(Detail));
        }
    }
    public AudioRegionRow? SelectedRegion
    {get=>selectedRegion;set{if(Set(ref selectedRegion,value)&&value is not null){SelectEvidence(value.Start,value.End);Changed(nameof(Detail));}}}
    public AudioIssueRow? SelectedIssue
    {get=>selectedIssue;set{if(Set(ref selectedIssue,value)&&value is not null){SelectEvidence(value.Start,value.End);Changed(nameof(Detail));}}}
    public string Detail=>SelectedCandidate?.Hex??SelectedIssue?.Description??SelectedBlock?.Detail??SelectedRegion?.Evidence??Quality;
    private bool autoScale=true;
    private double gain=1;
    public bool AutoScale {get=>autoScale;set=>Set(ref autoScale,value);}
    public double Gain {get=>gain;set{if(double.IsFinite(value)&&value>0&&value<=1000&&Set(ref gain,value))AutoScale=false;}}
    public bool IsPreviewBusy {get;private set;}
    public string Validation {get;private set;}="";
    internal AudioPcmTransform AppliedTransform {get;private set;}=new();
    internal AudioPcmTransform EffectiveTransform=>AppliedTransform with{Mix=AppliedTransform.Mix||ChannelMode==3};
    public string TransformDescription=>"Measured PCM · "+EffectiveTransform.Description+". Gain/Auto are display-only.";
    private string shift="0";
    private bool invertRight,mix;
    public string RightShift {get=>shift;set{if(Set(ref shift,value))DraftChanged();}}
    public bool InvertRight {get=>invertRight;set{if(Set(ref invertRight,value))DraftChanged();}}
    public bool Mix {get=>mix;set{if(Set(ref mix,value))DraftChanged();}}
    public bool CanAlign=>Model.ChannelCount==2;
    internal AudioPcmTransform RequestedTransform()=>new(int.Parse(RightShift,CultureInfo.InvariantCulture),InvertRight,Mix);
    private void DraftChanged(){settingsDirty=true;settingsGeneration++;Changed(nameof(HasDraft));Changed(nameof(Title));}
    internal AudioSession(AudioSignalAnalysis model,Dictionary<string,string?> hashes)
    {
        Model=model;baseReport=AudioAnalysisReport.Export(model,"txt");sources=hashes;viewEnd=model.FrameCount;selectionEnd=model.FrameCount;SelectedIntervals=[(0,model.FrameCount)];DisplayWaveform=model.Waveform;
        Blocks=model.Blocks.Select(b=>new AudioBlockRow(b)).ToArray();Regions=model.Regions.Select(r=>new AudioRegionRow(r)).ToArray();issues=model.Issues.Select(i=>new AudioIssueRow(i)).ToArray();Levels=model.Channels.Select(c=>new AudioLevelRow(c)).ToArray();Agreement=model.DecodeAgreement.Select(a=>new AudioAgreementRow(a)).ToArray();
        _=model.Waveform.VisibleBuckets(0,model.FrameCount,2048);
        references.AddRange(AudioPulseReferences.Choices);
        RefreshRecoveryRows();
    }
    protected override void OnLoaded()=>IsReadOnly=true;
    internal static async Task<AudioSession> LoadAsync(string path,IProgress<WavAnalysisProgress>? progress,CancellationToken ct)
    {
        if(System.IO.Path.GetExtension(path).Equals(".flac",StringComparison.OrdinalIgnoreCase))MZTools.Platform.Audio.NativeAudioCodecs.Configure();
        return await Task.Run(()=>
        {
            using var reader=AudioSignalAnalysisService.OpenReader(path);
            if(reader.Format.FrameCount<=0)throw new InvalidDataException("The recording contains no audio frames.");
            var paths=new[]{System.IO.Path.GetFullPath(path)}.Concat(reader is SegmentedPcmStreamReader joined?joined.SourceFiles:[]).Distinct(AtomicFileTransaction.PathComparer).ToArray();
            var hashes=paths.ToDictionary(p=>p,p=>AtomicFileTransaction.Hash(p)??throw new FileNotFoundException("Audio source is missing.",p),AtomicFileTransaction.PathComparer);
            var model=AudioSignalAnalysisService.Analyze(path,progress,ct);ct.ThrowIfCancellationRequested();
            foreach(var pair in hashes)if(AtomicFileTransaction.Hash(pair.Key)!=pair.Value)throw new IOException("The source set changed while analyzing.");
            return new AudioSession(model,hashes.ToDictionary(p=>p.Key,p=>(string?)p.Value,AtomicFileTransaction.PathComparer));
        },ct);
    }
    internal void VerifySources()
    {foreach(var pair in sources)if(AtomicFileTransaction.Hash(pair.Key)!=pair.Value)throw new IOException("An audio source or recording segment changed. Reopen it before export.");}
    public void SetSelection(IEnumerable<(long Start,long End)> intervals)
    {
        var requested=intervals.ToArray();if(requested.Any(r=>r.Start<0||r.End>Model.FrameCount||r.End<=r.Start))throw new ArgumentOutOfRangeException(nameof(intervals),"Frame intervals must be nonempty and within the recording.");
        var spans=AudioRangeInspection.MergeRanges(requested);
        if(spans.Length==0)throw new ArgumentException("Select a nonempty frame interval.");
        selectionGeneration++;SelectedIntervals=spans;selectionStart=spans[0].Start;selectionEnd=spans[^1].End;
        foreach(var p in new[]{nameof(SelectionStart),nameof(SelectionEnd),nameof(SelectionSummary)})Changed(p);
    }
    internal void SelectEvidence(long first,long last)
    {first=Math.Clamp(first,0,Model.FrameCount-1);last=Math.Clamp(last,first+1,Model.FrameCount);SetSelection([(first,last)]);}
    internal void RequireSingleInterval()
    {if(SelectedIntervals.Count!=1)throw new InvalidOperationException("Choose one contiguous frame interval for this operation. Measure selection supports a union; analysis windows never concatenate disjoint PCM silently.");}
    public void ShowRange(long start,long end)
    {long count=Math.Clamp(end-start,1,Math.Max(1,Model.FrameCount));viewStart=Math.Clamp(start,0,Math.Max(0,Model.FrameCount-count));viewEnd=viewStart+count;InvalidateView();}
    public void Fit()=>ShowRange(0,Model.FrameCount);
    public void FitSelection()=>ShowRange(SelectionStart,SelectionEnd);
    public void Zoom(double factor,double anchor=.5)
    {
        if(!double.IsFinite(factor)||factor<=0)throw new ArgumentOutOfRangeException(nameof(factor));anchor=Math.Clamp(anchor,0,1);
        long count=Math.Clamp((long)Math.Round((ViewEnd-ViewStart)/factor),1,Math.Max(1,Model.FrameCount));long first=ViewStart+(long)Math.Round(((ViewEnd-ViewStart)-count)*anchor);ShowRange(first,first+count);
    }
    public void Pan(long frames){long count=ViewEnd-ViewStart;long first=Math.Clamp(ViewStart+frames,0,Math.Max(0,Model.FrameCount-count));ShowRange(first,first+count);}
    public long FrameAt(double position,double width)=>Math.Clamp(ViewStart+(long)Math.Round(Math.Clamp(position/Math.Max(1,width),0,1)*(ViewEnd-ViewStart)),ViewStart,ViewEnd);
    private void InvalidateView(){viewGeneration++;Changed(nameof(ViewStart));Changed(nameof(ViewEnd));Changed(nameof(TransformDescription));}
    private readonly HashSet<Task> viewportTasks=[];
    private bool stopping;
    public Task RefreshViewportAsync(int maximumBuckets=2048,CancellationToken cancellation=default)
    {
        if(disposed||stopping)return Task.CompletedTask;
        var task=RefreshViewportCoreAsync(maximumBuckets,cancellation);
        lock(viewportTasks)viewportTasks.Add(task);
        _=task.ContinueWith(t=>{lock(viewportTasks)viewportTasks.Remove(t);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        return task;
    }
    internal async Task StopPreviewAsync()
    {
        stopping=true;rangeWork?.Cancel();lifetime.Cancel();Task[] pending;lock(viewportTasks)pending=viewportTasks.ToArray();
        await Task.WhenAll(pending);
    }
    private async Task RefreshViewportCoreAsync(int maximumBuckets,CancellationToken cancellation)
    {
        if(disposed)return;rangeWork?.Cancel();var request=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);rangeWork=request;
        long generation=viewGeneration,start=ViewStart,end=ViewEnd;var transform=EffectiveTransform;IsPreviewBusy=true;Changed(nameof(IsPreviewBusy));
        try
        {
            var waveform=await Task.Run(()=>
            {
                AudioRangeInspection.VerifySource(Model);
                if(transform==new AudioPcmTransform() && end-start>Model.FrameCount/65536.0*maximumBuckets)return Model.Waveform;
                using var reader=AudioSignalAnalysisService.OpenReader(Model.Source);var bounds=transform.Bounds(reader.Format,start,end);var builder=new WaveformEnvelopeBuilder(bounds.Start,bounds.End,maximumBuckets);
                transform.Visit(reader,bounds.Start,bounds.End,(frame,l,r)=>builder.Add(frame,l,r),request.Token);
                AudioRangeInspection.VerifySource(Model);return new WaveformRenderModel(builder.Finish(),Model.FrameCount,(ushort)(transform.Mix?1:Model.ChannelCount));
            },request.Token);
            if(disposed||request.IsCancellationRequested||generation!=viewGeneration)return;
            DisplayWaveform=waveform;DisplayedTransform=transform;Validation="";Changed(nameof(DisplayWaveform));Changed(nameof(Validation));
        }
        catch(OperationCanceledException){}
        catch(Exception e)when(e is not OutOfMemoryException){if(!disposed&&generation==viewGeneration){Validation=e.Message;Changed(nameof(Validation));}}
        finally{if(rangeWork==request){rangeWork=null;IsPreviewBusy=false;Changed(nameof(IsPreviewBusy));}request.Dispose();}
    }
    public void ResetDraft()
    {
        shift="0";invertRight=mix=false;AppliedTransform=new();DisplayWaveform=Model.Waveform;DisplayedTransform=new();ClearSpectral();settingsDirty=false;settingsGeneration++;
        foreach(var p in new[]{nameof(RightShift),nameof(InvertRight),nameof(Mix),nameof(HasDraft),nameof(Title)})Changed(p);InvalidateView();
    }
    internal void SetError(string message){Validation=message;Changed(nameof(Validation));}
    public override void Dispose(){if(disposed)return;disposed=true;rangeWork?.Cancel();lifetime.Cancel();lock(cacheGate){spectralCache.Clear();spectralOrder.Clear();}}
    SaveChoice IFileSetDocument.CurrentSaveChoice(string path)=>throw new NotSupportedException(SaveDisabledReason);
    Task<DocumentSavePlan> IFileSetDocument.PrepareSaveAsync(SaveChoice choice,CancellationToken cancellation)=>throw new NotSupportedException(SaveDisabledReason);
    public override Task WriteAsync(Stream destination,CancellationToken cancellationToken)=>throw new NotSupportedException(SaveDisabledReason);
    public override Task ValidateSavedAsync(string temporaryPath,CancellationToken cancellationToken)=>throw new NotSupportedException(SaveDisabledReason);
}
