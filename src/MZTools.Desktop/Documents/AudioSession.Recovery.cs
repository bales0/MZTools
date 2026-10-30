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
        set{if(Set(ref selectedCandidate,value)){if(value is not null)SelectEvidence(value.Start,value.End);Changed(nameof(Detail));Changed(nameof(CandidateHex));}}
    }
    public string CandidateHex=>SelectedCandidate?.Hex??"Select a recovery candidate. Hex inspection does not change validity or invent a header.";
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
        if(SelectedPass?.Value.Measurements is not { } measured||RecoveryStreamIndex<0||RecoveryStreamIndex>=measured.Count)return;
        MeasuredBlock=measured[RecoveryStreamIndex].Block;NotifyMeasurement();
    }
    public string RecoverySummary=>Recovery is { } result?$"Frames [{result.StartSample:N0}, {result.EndSample:N0}) · {result.Records.Count} recovered program(s) · {result.Candidates.Count} candidates · {result.Passes.Count} passes\nDiagnostics omitted {result.OmittedDiagnostics} · rejected headers omitted {result.OmittedHeaderRejections}. Candidate validity is separate from synthetic user metadata.":$"Initial analysis recovered {Programs.Count} program(s). Select an interval and Run deeper analysis for candidate/pass diagnostics.";
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
        long selection=selectionGeneration,settings=settingsGeneration;long first=SelectionStart,last=SelectionEnd;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var result=await Task.Run(()=>{VerifySources();var recovered=WavHeuristicAnalyzer.AnalyzeRange(Model.Source,first,last,expectedLength,progress,linked.Token,options);VerifySources();return recovered;},linked.Token);
        if(disposed||selection!=selectionGeneration||settings!=settingsGeneration)return false;
        Recovery=result;AppliedTransform=options.Transform;ClearSpectral();settingsDirty=false;pendingRecovery=result.Records.Count>0;RefreshRecoveryRows();InvalidateView();
        foreach(var p in new[]{nameof(HasDraft),nameof(Title),nameof(TransformDescription),nameof(RecoverySummary),nameof(Diagnostics),nameof(RejectedHeaders),nameof(Failures)})Changed(p);return true;
    }
    private void RefreshRecoveryRows()
    {
        refreshingRecoveryRows=true;try
        {
        Programs=AvailableRecords.Select((r,i)=>new RecordRow(r,i)).ToArray();Candidates=Recovery?.Candidates.Select((c,i)=>new AudioCandidateRow(c,i)).ToArray()??[];Passes=Recovery?.Passes.Select((p,i)=>new AudioPassRow(p,i)).ToArray()??[];
        selectedProgram=Programs.FirstOrDefault();selectedCandidate=null;selectedPass=null;PassComparison=null;compareLeft=0;compareRight=Passes.Count>1?1:0;
        foreach(var p in new[]{nameof(Programs),nameof(Candidates),nameof(Passes),nameof(SelectedProgram),nameof(SelectedCandidate),nameof(SelectedPass),nameof(CandidateHex),nameof(PassNames),nameof(CompareLeft),nameof(CompareRight),nameof(PassDifferences),nameof(PassComparisonSummary)})Changed(p);
        }finally{refreshingRecoveryRows=false;}Changed(nameof(SelectedProgram));
    }
    internal async Task ComparePassesAsync(CancellationToken ct)
    {
        var result=Recovery??throw new InvalidOperationException("Run deeper analysis first.");int left=CompareLeft,right=CompareRight;
        var comparison=await Task.Run(()=>AudioDecodePassComparison.Compare(result,left,right,ct),ct);
        if(disposed||result!=Recovery)return;PassComparison=comparison;Changed(nameof(PassDifferences));Changed(nameof(PassComparisonSummary));
    }
    internal TapeRecord[] ExportRecords(bool all)=>all?AvailableRecords.Select(r=>r.DeepClone()).ToArray():SelectedProgram is { } row?[row.Record.DeepClone()]:throw new InvalidOperationException("Select a recovered program first.");
    internal TapeRecord CreateSynthetic(string name,byte type,ushort load,ushort exec,string description)
    {
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
