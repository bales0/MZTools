using System.Globalization;

namespace MZTools.Desktop.Documents;

public sealed partial class AudioSession
{
    internal AudioBlockAnalysis? MeasuredBlock {get;private set;}
    public string MeasurementSummary=>MeasuredBlock is { } b?new AudioBlockRow(b).Detail:"Select a block or measure a frame interval. No pulse statistics have been invented.";
    public IReadOnlyList<AudioDistributionRow> Distributions=>MeasuredBlock?.Distributions.Where(d=>VisibleGroups.Contains(d.Group)).Select(d=>new AudioDistributionRow(d)).ToArray()??[];
    public IReadOnlyList<string> MeasurementModes {get;}=["Used for decoding","Compare Zero crossing","Compare Hysteresis","Compare Adaptive peak"];
    private int measurementMode;
    public int MeasurementMode {get=>measurementMode;set=>Set(ref measurementMode,value);}
    private readonly List<AudioPulseReference> references=[];
    public IReadOnlyList<string> ReferenceNames=>references.Select(r=>r.Name).ToArray();
    private int referenceIndex;
    private bool showReference=true,followReference=true;
    public int ReferenceIndex {get=>referenceIndex;set{if(value<0||value>=references.Count)return;if(Set(ref referenceIndex,value))UpdateReference();}}
    public bool ShowReference {get=>showReference;set{if(Set(ref showReference,value))UpdateReference();}}
    public bool FollowReference {get=>followReference;set{if(Set(ref followReference,value)&&value&&SelectedBlock is { } row)FollowBlockReference(row.Value);}}
    internal AudioPulseReference ReferenceAt(int index)=>references[index];
    internal AudioPulseReference? SelectedReference=>references.Count==0?null:references[Math.Clamp(ReferenceIndex,0,references.Count-1)];
    public string ReferenceEvidence=>SelectedReference is { } r?$"{r.Name} · {r.Kind}\n{r.Source}\n{r.Evidence}\n{r.Derivation}":"No reference.";
    private bool shortHigh=true,shortLow=true,longHigh=true,longLow=true;
    public bool ShortHigh {get=>shortHigh;set{if(Set(ref shortHigh,value))UpdateReference();}}
    public bool ShortLow {get=>shortLow;set{if(Set(ref shortLow,value))UpdateReference();}}
    public bool LongHigh {get=>longHigh;set{if(Set(ref longHigh,value))UpdateReference();}}
    public bool LongLow {get=>longLow;set{if(Set(ref longLow,value))UpdateReference();}}
    internal IReadOnlyList<string> VisibleGroups=>AudioPulseReference.Groups.Where((_,i)=>new[]{ShortHigh,ShortLow,LongHigh,LongLow}[i]).ToArray();
    public double? HistogramMin {get;private set;}
    public double? HistogramMax {get;private set;}
    internal AudioHistogramRange HistogramBounds()
    {
        var ranges=Distributions.Select(d=>AudioHistogramRange.Create(d.Value,Model.SampleRate)).ToArray();
        return ranges.Length==0?new(0,1):new(ranges.Min(r=>r.Min),ranges.Max(r=>r.Max));
    }
    public void FitHistogram(){HistogramMin=HistogramMax=null;Changed(nameof(HistogramMin));}
    public void ZoomHistogram(double factor,double anchor=.5)
    {
        var full=HistogramBounds();double first=HistogramMin??full.Min,last=HistogramMax??full.Max,width=(last-first)/factor;
        var next=full.Constrain(first+(last-first-width)*Math.Clamp(anchor,0,1),first+(last-first-width)*Math.Clamp(anchor,0,1)+width,1_000_000.0/Model.SampleRate/4);
        HistogramMin=next.Min;HistogramMax=next.Max;Changed(nameof(HistogramMin));
    }
    public void PanHistogram(double delta)
    {
        var full=HistogramBounds();double first=HistogramMin??full.Min,last=HistogramMax??full.Max;
        var next=full.Constrain(first+delta,last+delta,1_000_000.0/Model.SampleRate/4);HistogramMin=next.Min;HistogramMax=next.Max;Changed(nameof(HistogramMin));
    }
    internal void AddReference(string name,double sh,double sl,double lh,double ll)
    {references.Add(AudioPulseReference.User(name,sh,sl,lh,ll));FollowReference=false;Changed(nameof(ReferenceNames));ReferenceIndex=references.Count-1;UpdateReference();}
    private void FollowBlockReference(AudioBlockAnalysis block)
    {
        int index=references.FindIndex(r=>r.Name==block.ReferenceName||r.CatalogId==block.PulseSource?.ReferenceId);
        if(index>=0)ReferenceIndex=index;
    }
    private void UpdateReference()
    {
        baseReport=null;Model.ComparisonReference=SelectedReference;Model.ShowComparisonReference=ShowReference;Model.VisiblePulseGroups=VisibleGroups;
        foreach(var p in new[]{nameof(ReferenceEvidence),nameof(Distributions),nameof(Report)})Changed(p);
    }
    private void NotifyMeasurement()
    {foreach(var p in new[]{nameof(MeasurementSummary),nameof(Distributions),nameof(Report)})Changed(p);}
    public async Task<bool> ApplyTransformPreviewAsync(CancellationToken ct)
    {
        RequireSingleInterval();
        long revision=settingsGeneration,selection=selectionGeneration,first=SelectionStart,last=SelectionEnd;var transform=RequestedTransform();int channel=transform.Mix||ChannelMode!=1?0:1;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var result=await Task.Run(()=>AudioRangeInspection.Analyze(Model,first,last,transform,channel,WavPulseMode.ZeroCrossing,1,false,null,linked.Token,waveformOnly:true),linked.Token);
        if(disposed||revision!=settingsGeneration||selection!=selectionGeneration)return false;
        AppliedTransform=transform;ClearSpectral();DisplayWaveform=result.Analysis.Waveform;DisplayedTransform=transform;settingsDirty=false;Validation="";ShowRange(result.Start,result.End);
        foreach(var p in new[]{nameof(DisplayWaveform),nameof(HasDraft),nameof(Title),nameof(Validation),nameof(TransformDescription)})Changed(p);return true;
    }
    public async Task<bool> MeasureAsync(CancellationToken ct)
    {
        if(settingsDirty)throw new InvalidOperationException("Apply transform preview or Cancel edits before measuring.");
        var intervals=SelectedIntervals.ToArray();var reference=SelectedBlock?.Value;var transform=EffectiveTransform;int channel=transform.Mix||ChannelMode!=1?0:1;
        bool used=MeasurementMode==0&&reference?.PulseSource is not null;
        if(used&&transform==new AudioPcmTransform()&&intervals.Length==1&&intervals[0]==(reference!.StartSample,reference.EndSample)&&channel==reference.Channel-1)
        {MeasuredBlock=reference;NotifyMeasurement();return true;}
        var detector=used&&Enum.TryParse<WavPulseMode>(reference!.DecodeDetector,out var decoded)?decoded:MeasurementMode switch{2=>WavPulseMode.Schmitt,3=>WavPulseMode.AdaptiveZeroCrossing,_=>WavPulseMode.ZeroCrossing};
        double gate=used?reference!.PulseSource!.ThresholdScale:1;long? preprocessing=used?reference!.PulseSource!.PreprocessingStartSample:null;double deadband=used?reference!.PulseSource!.ZeroDeadbandPercent/100:0;
        long generation=selectionGeneration;using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var result=await Task.Run(()=>AudioRangeInspection.AnalyzeSelection(Model,intervals,channel,transform.Mix,reference,linked.Token,detector,gate,preprocessing,deadband,transform),linked.Token);
        if(disposed||generation!=selectionGeneration||transform!=EffectiveTransform)return false;MeasuredBlock=result;NotifyMeasurement();return true;
    }
    private readonly object cacheGate=new();
    private readonly Dictionary<string,(object Value,long Bytes)> spectralCache=[];
    private readonly LinkedList<string> spectralOrder=[];
    internal long SpectralCacheBytes {get{lock(cacheGate)return spectralCache.Values.Sum(x=>x.Bytes);}}
    private T Cache<T>(string key,Func<T> compute,Func<T,long> size)where T:class
    {
        AudioRangeInspection.VerifySource(Model);
        lock(cacheGate)
        {
            if(disposed)throw new OperationCanceledException();
            if(spectralCache.TryGetValue(key,out var cached)){spectralOrder.Remove(key);spectralOrder.AddLast(key);return (T)cached.Value;}
        }
        var value=compute();long bytes=size(value);if(bytes>24L*1024*1024)throw new InvalidDataException("Spectral snapshot exceeds the 24 MiB data cache budget.");
        lock(cacheGate)
        {
            if(disposed)throw new OperationCanceledException();
            if(spectralCache.TryGetValue(key,out var existing))return (T)existing.Value;
            while(SpectralCacheBytes+bytes>24L*1024*1024&&spectralOrder.Count>0){spectralCache.Remove(spectralOrder.First!.Value);spectralOrder.RemoveFirst();}
            spectralCache.Add(key,(value,bytes));spectralOrder.AddLast(key);return value;
        }
    }
    internal void ClearSpectral()
    {SpectrumData=null;SpectrogramData=null;Changed(nameof(SpectrumData));Changed(nameof(SpectrogramData));Changed(nameof(SpectralSummary));}
    private double frequencyMin,frequencyMax;
    public double FrequencyMin=>frequencyMin;
    public double FrequencyMax=>frequencyMax>frequencyMin?frequencyMax:Model.SampleRate/2.0;
    public void FitFrequency(){frequencyMin=frequencyMax=0;Changed(nameof(FrequencyMin));}
    public void ZoomFrequency(double factor,double anchor=.5)
    {
        double total=Model.SampleRate/2.0,old=FrequencyMax-FrequencyMin,width=Math.Clamp(old/factor,Model.SampleRate/(double)FftSize,total);
        frequencyMin=Math.Clamp(FrequencyMin+(old-width)*Math.Clamp(anchor,0,1),0,Math.Max(0,total-width));frequencyMax=frequencyMin+width;Changed(nameof(FrequencyMin));
    }
    public void PanFrequency(double delta)
    {double width=FrequencyMax-FrequencyMin;frequencyMin=Math.Clamp(FrequencyMin+delta,0,Math.Max(0,Model.SampleRate/2.0-width));frequencyMax=frequencyMin+width;Changed(nameof(FrequencyMin));}
    internal AudioSpectrum? SpectrumData {get;private set;}
    internal AudioSpectrogram? SpectrogramData {get;private set;}
    private int fftSize=4096;
    public IReadOnlyList<int> FftChoices {get;}=[256,512,1024,2048,4096,8192,16384,32768];
    public int FftSize {get=>fftSize;set{if(FftChoices.Contains(value))Set(ref fftSize,value);}}
    public string SpectralSummary=>SpectrogramData is { } g?$"{g.Method}\nFrames [{g.StartFrame:N0}, {g.EndFrame:N0}) · N={g.FftSize} · hop={g.Hop} frames · Δf={g.SampleRate/(double)g.FftSize:0.###} Hz\n{g.Windows:N0} exact FFT windows aggregated to {g.Columns.Count} columns · {g.FrequencyGroup} bins per displayed band · uncovered tail {g.UncoveredTail} frames\n"+SpectrumSummary:SpectrumSummary;
    private string SpectrumSummary=>SpectrumData is { } s?$"{s.Method}\nFrames [{s.StartFrame:N0}, {s.EndFrame:N0}) · N={s.FftSize} · Δf={s.SampleRate/(double)s.FftSize:0.###} Hz\nDominant L/R: {s.LeftPeak:0.###}/{s.RightPeak:0.###} Hz · {s.Bins.Count} bins":"Select a frame interval and Compute spectrum/spectrogram. PCM is not read during painting.";
    internal async Task<bool> ComputeSpectralAsync(bool spectrogram,IProgress<WavAnalysisProgress>? progress,CancellationToken ct)
    {
        RequireSingleInterval();
        if(settingsDirty)throw new InvalidOperationException("Apply transform preview or Cancel edits first.");
        long start=SelectionStart,end=SelectionEnd,generation=selectionGeneration;int n=FftSize;var transform=EffectiveTransform;string key=FormattableString.Invariant($"{spectrogram}:{start}:{end}:{n}:{transform}");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        object result=await Task.Run<object>(()=>spectrogram?Cache(key,()=>AudioSpectralAnalysis.Spectrogram(Model,start,end,transform,n,progress,linked.Token),x=>x.Bytes):Cache(key,()=>AudioSpectralAnalysis.Spectrum(Model,start,end,transform,n,linked.Token),x=>(long)x.Bins.Count*24),linked.Token);
        if(disposed||generation!=selectionGeneration||transform!=EffectiveTransform||n!=FftSize)return false;if(result is AudioSpectrogram g)SpectrogramData=g;else SpectrumData=(AudioSpectrum)result;
        Changed(nameof(SpectralSummary));Changed(nameof(SpectrumData));Changed(nameof(SpectrogramData));return true;
    }
    internal async Task<bool> TriggerAsync(double level,bool rising,CancellationToken ct)
    {
        if(settingsDirty)throw new InvalidOperationException("Apply transform preview or Cancel edits first.");
        long generation=viewGeneration,first=ViewStart,last=ViewEnd;var transform=EffectiveTransform;int channel=ChannelMode==1?1:0;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var range=await Task.Run(()=>AudioSpectralAnalysis.Trigger(Model,first,last,transform,transform.Mix?0:channel,level,rising,linked.Token),linked.Token);
        if(disposed||generation!=viewGeneration||transform!=EffectiveTransform)return false;Validation=range.Triggered?"Trigger found in actual source PCM (latest centered crossing, at most 2 seconds retained).":"No qualifying crossing found; viewport retained.";Changed(nameof(Validation));
        if(range.Triggered)ShowRange(range.Start,range.End);return range.Triggered;
    }
    public void CancelViewport()=>rangeWork?.Cancel();
}
