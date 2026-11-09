namespace MZTools.Desktop.Documents;

internal enum DeepSignal { Left, Right, Both, Average }
// All intervals and results remain on the original left/reference time axis.
internal sealed record DeepAnalysisInput(DeepSignal Signal,int RightOffset,bool InvertRight,long Start,long End)
{
    internal AudioPcmTransform Transform => Signal==DeepSignal.Left ? new() : new(RightOffset,InvertRight,Signal==DeepSignal.Average);
    internal string Label => $"{Signal} · {Transform.Description} · frames [{Start}, {End}) · R[n-offset], + delays R";
}
public sealed partial class RecordRow { public string AnalysisSource {get;internal set;}=""; }

public sealed partial class AudioSession
{
    public bool IsDeepAnalysis {get;private set;}
    internal DeepAnalysisInput? CompletedAnalysisInput {get;private set;}
    internal IReadOnlyList<(string Source,AudioRegionRecoveryResult Result)> ChannelResults {get;private set;}=[];
    private string[] programSources=[];
    private long completedRun;
    internal IReadOnlyList<AudioBlockAnalysis> DeepMeasurements {get;private set;}=[];
    public bool CanAdjustRight=>CanAlign&&(!IsDeepAnalysis||ChannelMode!=0);
    public IReadOnlyList<string> DisplayChannels=>Model.ChannelCount==1?["Mono"]:IsDeepAnalysis?["Left (L)","Right (R)","Both / Overlay","Average (L+R)/2"]:["Overlay"];
    public int DisplayChannelMode
    {
        get=>IsDeepAnalysis?ChannelMode:0;
        set{if(IsDeepAnalysis)ChannelMode=value;}
    }
    public string AnalysisInputStatus=>!IsDeepAnalysis?$"Normal · {(CanAlign?"Overlay L/R":"Mono")} · original PCM; Deep settings are independent."
        : (CompletedAnalysisInput is null?"Deep input: ":IsRecoveryCurrent?$"Completed run #{completedRun}: ":"OUTDATED — run Deep analysis again. Current: ")+DescribeRequestedInput();
    private string DescribeRequestedInput(){try{return RequestedAnalysisInput().Label;}catch(Exception e) when(e is FormatException or OverflowException){return "Enter an integer R offset in source samples.";}}
    internal DeepAnalysisInput RequestedAnalysisInput()=>new(Model.ChannelCount==1?DeepSignal.Left:(DeepSignal)ChannelMode,int.Parse(RightShift,System.Globalization.CultureInfo.InvariantCulture),InvertRight,previewBounds?.Start??SelectionStart,previewBounds?.End??SelectionEnd);
    internal bool IsRecoveryCurrent
    {
        get{try{return !IsDeepAnalysis||CompletedAnalysisInput==RequestedAnalysisInput();}catch(Exception e) when(e is FormatException or OverflowException){return false;}}
    }
    internal void EnableDeepAnalysis(){if(IsDeepAnalysis)return;IsDeepAnalysis=true;Changed(nameof(IsDeepAnalysis));Changed(nameof(DisplayChannels));Changed(nameof(DisplayChannelMode));Changed(nameof(AnalysisInputStatus));}
    internal void UseDeepMeasurement(AudioBlockAnalysis block){MeasuredBlock=block;NotifyMeasurement();}
    private void InputChanged()
    {
        ClearSpectral();DeepMeasurements=[];MeasuredBlock=null;NotifyMeasurement();selectedRecoveryMeasurement=null;
        RefreshSelectedOverlays();Changed(nameof(CanAdjustRight));Changed(nameof(AnalysisInputStatus));Changed(nameof(RecoverySummary));
    }
}
