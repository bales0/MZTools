namespace MZTools.Desktop.Documents;

public sealed partial class AudioSession
{
    internal Dictionary<string,string> RecoveryFormValues {get;set;}=[];
    internal AudioRegionRecoveryResult? Recovery {get;private set;}
    internal IReadOnlyList<TapeRecord> AvailableRecords=>Recovery?.Records??Model.RecoveredRecords;
    public IReadOnlyList<RecordRow> Programs {get;private set;}=[];
    public IReadOnlyList<AudioCandidateRow> Candidates {get;private set;}=[];
    public IReadOnlyList<AudioPassRow> Passes {get;private set;}=[];
    public IReadOnlyList<AudioDiagnosticRow> Diagnostics=>Recovery?.Diagnostics.Select(d=>new AudioDiagnosticRow(d)).ToArray()??[];
    public IReadOnlyList<AudioRejectedHeaderRow> RejectedHeaders=>Recovery?.RejectedHeaders.Select(d=>new AudioRejectedHeaderRow(d)).ToArray()??[];
    public IReadOnlyList<AudioFailureRow> Failures=>Recovery?.Failures.Select(f=>new AudioFailureRow(f.Reason,f.StartSample,f.EndSample)).ToArray()??[];
    private bool refreshingRecoveryRows;
    private RecordRow? selectedProgram;
    private AudioCandidateRow? selectedCandidate;
    private AudioPassRow? selectedPass;
    public RecordRow? SelectedProgram {get=>selectedProgram;set{if(refreshingRecoveryRows)return;if(Set(ref selectedProgram,value))Changed(nameof(Detail));}}
    public AudioCandidateRow? SelectedCandidate
    {
        get=>selectedCandidate;
        set{if(Set(ref selectedCandidate,value)){if(value is not null)SelectEvidence(value.Start,value.End);Changed(nameof(Detail));Changed(nameof(CandidateHex));Changed(nameof(CandidateEvidence));}}
    }
    public string CandidateHex=>SelectedCandidate?.Hex??"Select a recovery candidate. Hex inspection does not change validity or invent a header.";
    public string CandidateEvidence=>SelectedCandidate is{} c?c.Status+"\n"+c.Value.BoundaryEvidence:"Select a recovery candidate. No RAM address is assumed for unpaired raw bytes.";
    public AudioPassRow? SelectedPass
    {
        get=>selectedPass;
        set
        {
            if(!Set(ref selectedPass,value))return;recoveryStreamIndex=0;
            if(value is not null)
            {
                string detector=Recovery is null?"":AudioRecoveryHistogram.Detector(Recovery,value.Value);
                int index=value.Value.Measurements.ToList().FindIndex(m=>m.Block.DecodeDetector==detector&&m.Block.Channel==(ChannelMode==1?2:1));if(index>=0)recoveryStreamIndex=index;
            }
            Changed(nameof(RecoveryStreams));Changed(nameof(RecoveryStreamIndex));UseRecoveryStream();
        }
    }
    private int recoveryStreamIndex;
    public IReadOnlyList<string> RecoveryStreams=>SelectedPass?.Value.Measurements.Select((m,i)=>$"{i+1}: ch {m.Block.Channel} · {m.Block.DecodeDetector} · inverted {m.Block.Inverted} · {m.Block.PulseMeasurement}").ToArray()??[];
    public int RecoveryStreamIndex {get=>recoveryStreamIndex;set{if(Set(ref recoveryStreamIndex,value))UseRecoveryStream();}}
    private void UseRecoveryStream()
    {
        if(!IsRecoveryCurrent||SelectedPass?.Value.Measurements is not { } measured||RecoveryStreamIndex<0||RecoveryStreamIndex>=measured.Count)return;
        MeasuredBlock=measured[RecoveryStreamIndex].Block;NotifyMeasurement();
    }
    public string RecoverySummary=>AnalysisInputStatus+"\n"+(Recovery is { } result?$"Frames [{result.StartSample:N0}, {result.EndSample:N0}) · {result.Records.Count} recovered program(s) · {result.Candidates.Count} candidates · {result.Passes.Count} passes\nDiagnostics omitted {result.OmittedDiagnostics} · rejected headers omitted {result.OmittedHeaderRejections}. Candidate validity is separate from synthetic user metadata.":$"Initial analysis recovered {Programs.Count} program(s). Select an interval and Run deeper analysis for candidate/pass diagnostics.");
    public IReadOnlyList<string> PassNames=>Passes.Select(p=>$"{p.Index+1}: {p.Test} · {p.Detectors} · H{p.Headers}/P{p.Payloads}").ToArray();
    private int compareLeft,compareRight;
    public int CompareLeft {get=>compareLeft;set=>Set(ref compareLeft,value);}
    public int CompareRight {get=>compareRight;set=>Set(ref compareRight,value);}
    internal AudioPassComparisonResult? PassComparison {get;private set;}
    public IReadOnlyList<AudioDifferenceRow> PassDifferences=>PassComparison?.Rows.Select(r=>new AudioDifferenceRow(r)).ToArray()??[];
    public string PassComparisonSummary=>PassComparison is { } comparison?$"{comparison.Status} · {comparison.OmittedDiagnostics} omitted diagnostic events. Bounded evidence does not establish an absolute first bad byte.":"Choose retained passes from the latest deeper analysis.";
    internal async Task<bool> RecoverAsync(AudioRecoveryOptions options,int? expectedLength,IProgress<WavAnalysisProgress>? progress,CancellationToken ct)
    {
        RequireSingleInterval();
        if(!IsDeepAnalysis)
        {
            EnableDeepAnalysis();channelMode=options.Transform.Mix?3:options.Channel??(CanAlign?2:0);
            shift=options.Transform.RightShiftSamples.ToString(System.Globalization.CultureInfo.InvariantCulture);invertRight=options.Transform.InvertRight;mix=options.Transform.Mix;
        }
        previewBounds??=(SelectionStart,SelectionEnd);
        var input=RequestedAnalysisInput();options=options with{Transform=input.Transform,Channel=input.Signal==DeepSignal.Right?1:0};
        long selection=selectionGeneration,settings=settingsGeneration;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var runs=await Task.Run(()=>
        {
            VerifySources(linked.Token);
            var list=new List<(string Source,AudioRegionRecoveryResult Result)>();
            foreach(int ch in input.Signal==DeepSignal.Both&&CanAlign?new[]{0,1}:new[]{input.Signal==DeepSignal.Right?1:0})
            {
                linked.Token.ThrowIfCancellationRequested();
                string label=input.Signal==DeepSignal.Average?"Average":!CanAlign?"Mono":ch==0?"Left":"Right";
                var found=WavHeuristicAnalyzer.AnalyzeRange(Model.Source,input.Start,input.End,expectedLength,progress,linked.Token,options with{Channel=ch});
                // Core measures both streams; expose only the effective channel of this run.
                found=found with{Passes=found.Passes.Select(p=>p with{Channels=input.Signal==DeepSignal.Average?"Mix":label,
                    AudioTransform=label+" · "+input.Label,Measurements=p.Measurements.Where(m=>m.Block.Channel==ch+1).ToArray()}).ToArray()};
                list.Add((label,found));
                if(list.Sum(r=>r.Item2.Candidates.Count)>65536||list.Sum(r=>r.Item2.Diagnostics.Count)>200000)throw new InvalidDataException("Combined Deep evidence exceeds the bounded result budget; narrow the interval/tests.");
            }
            VerifySources(linked.Token);return list;
        },linked.Token);
        if(disposed||linked.IsCancellationRequested||selection!=selectionGeneration||settings!=settingsGeneration||input!=RequestedAnalysisInput())return false;
        var first=runs[0].Result;
        var result=runs.Count==1?first:new AudioRegionRecoveryResult(first.Source,first.SampleRate,first.StartSample,first.EndSample,
            runs.SelectMany(r=>r.Result.Records).ToArray(),runs.SelectMany(r=>r.Result.Recoveries).ToArray(),runs.SelectMany(r=>r.Result.Candidates).ToArray(),
            runs.SelectMany(r=>r.Result.Failures.Select(f=>f with{Reason=r.Source+": "+f.Reason})).ToArray(),runs.SelectMany(r=>r.Result.Passes).ToArray())
        {Diagnostics=runs.SelectMany(r=>r.Result.Diagnostics).ToArray(),RejectedHeaders=runs.SelectMany(r=>r.Result.RejectedHeaders).ToArray(),
         OmittedDiagnostics=runs.Sum(r=>r.Result.OmittedDiagnostics),OmittedHeaderRejections=runs.Sum(r=>r.Result.OmittedHeaderRejections)};
        ChannelResults=runs;programSources=runs.SelectMany(r=>r.Result.Records.Select(_=>r.Source)).ToArray();CompletedAnalysisInput=input;completedRun++;
        DeepMeasurements=[];Recovery=result;selectedRecoveryTransform=input.Transform;selectedRecoveryMeasurement=null;AppliedTransform=input.Transform;ClearSpectral();settingsDirty=false;pendingRecovery=result.Records.Count>0;RefreshRecoveryRows();InvalidateView();
        foreach(var p in new[]{nameof(HasDraft),nameof(HasTransformDraft),nameof(Title),nameof(TransformDescription),nameof(AnalysisInputStatus),nameof(RecoverySummary),nameof(Diagnostics),nameof(RejectedHeaders),nameof(Failures)})Changed(p);return true;
    }

    private void RefreshRecoveryRows()
    {
        refreshingRecoveryRows=true;try
        {
        Programs=AvailableRecords.Select((r,i)=>new RecordRow(r,i){AnalysisSource=i<programSources.Length?programSources[i]:"Original analysis"}).ToArray();Candidates=Recovery?.Candidates.Select((c,i)=>new AudioCandidateRow(c,i){AnalysisSource=CompletedAnalysisInput?.Signal==DeepSignal.Average?"Average":!CanAlign?"Mono":c.Channel==1?"Right":"Left"}).ToArray()??[];Passes=Recovery?.Passes.Select((p,i)=>new AudioPassRow(p,i)).ToArray()??[];
        selectedProgram=Programs.FirstOrDefault();selectedCandidate=null;selectedPass=null;PassComparison=null;compareLeft=0;compareRight=Passes.Count>1?1:0;
        foreach(var p in new[]{nameof(Programs),nameof(Candidates),nameof(Passes),nameof(SelectedProgram),nameof(SelectedCandidate),nameof(SelectedPass),nameof(CandidateHex),nameof(CandidateEvidence),nameof(PassNames),nameof(CompareLeft),nameof(CompareRight),nameof(PassDifferences),nameof(PassComparisonSummary)})Changed(p);
        }finally{refreshingRecoveryRows=false;}Changed(nameof(SelectedProgram));
    }
    internal async Task ComparePassesAsync(CancellationToken ct)
    {
        var result=Recovery??throw new InvalidOperationException("Run deeper analysis first.");int left=CompareLeft,right=CompareRight;
        var comparison=await Task.Run(()=>AudioDecodePassComparison.Compare(result,left,right,ct),ct);
        if(disposed||result!=Recovery)return;PassComparison=comparison;Changed(nameof(PassDifferences));Changed(nameof(PassComparisonSummary));
    }
    internal TapeRecord[] ExportRecords(bool all)=>!IsRecoveryCurrent?throw new InvalidOperationException("Deep results are OUTDATED; analyze the current input before export."):all?AvailableRecords.Select(r=>r.DeepClone()).ToArray():SelectedProgram is { } row?[row.Record.DeepClone()]:throw new InvalidOperationException("Select a recovered program first.");
    internal TapeRecord CreateSynthetic(string name,byte type,ushort load,ushort exec,string description)
    {
        if(!IsRecoveryCurrent)throw new InvalidOperationException("Deep results are OUTDATED.");
        if(SelectedCandidate is not {CanSynthesize:true} row)throw new InvalidDataException("Synthetic metadata requires a payload with an available matching checksum.");
        var record=AudioSyntheticHeader.Create(row.Value.Data,name,type,load,exec,description);record.MetadataOrigin=MetadataOrigin.CreatedOrModifiedInAdvanced;return record;
    }
    internal void MarkExported(){pendingRecovery=false;Changed(nameof(HasDraft));Changed(nameof(Title));}
}
public sealed record AudioFailureRow(string Reason,long Start,long End);
public sealed class AudioRejectedHeaderRow
{
    internal AudioHeaderRejection Value {get;}internal AudioRejectedHeaderRow(AudioHeaderRejection value)=>Value=value;
    public long Frame=>Value.Sample;public int Channel=>Value.Channel;public string Detector=>Value.Detector;public string Reason=>Value.Reason;public string Hash=>Value.HeaderSha256;
}
