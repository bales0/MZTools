namespace MZTools.Capture;

public enum HeadEdgeAdjustmentState { Waiting, WarmingUp, ReadyToZero, WorkingCopy, RepeatedPassage, Unstable, ContentChanged, ResolutionLimited, Unavailable, Paused, Stale, LowConfidence, Conflict, Uncertain }
public sealed record HeadEdgeSource(string DeviceId,string ProcessingSignature,long Discontinuities,string RecordingLabel="");
public sealed record HeadEdgeNeedle(double? CurrentMicroseconds,double? ZeroMicroseconds,double? ChangePercent,
    double? DisplayPercent,double WindowMadPercent,double TemporalMadPercent);
public sealed record HeadEdgeAdjustmentSnapshot(HeadEdgeAdjustmentState State,string Message,bool CanZero,bool HasZero,
    HeadEdgeNeedle Rise,HeadEdgeNeedle Fall,HeadEdgeNeedle Combined,int Windows,double SamplePeriodMicroseconds,long? ZeroStartFrame,long? ZeroEndFrame)
{
    public bool ZeroIsUncertain { get; init; }
    public string Diagnostics { get; init; } = "";
    public IReadOnlyList<HeadEdgeGroup> Groups { get; init; } = [];
}

/// <summary>Relative physical edge quality, with frozen context and reference weights. Never edits PCM or estimates an angle.</summary>
public sealed class HeadEdgeAdjustmentModel
{
    private sealed record Key(AudioCaptureFormat Format,HeadAlignmentOptions Options,long Frames,int Channel,HeadEdgeSource Source,bool Repeated);
    private sealed record Observation(long Start,long End,HeadEdgeWindow Window);
    private readonly List<Observation> history=[];
    private Key? key;
    private Observation? latest,zero;
    private HeadAlignmentChannel? channel;
    private long? lastEnd;
    private double lastDataTime,lastClock,damping=.5;
    private double? displayedRise,displayedFall,displayedCombined;
    private bool active,zeroUncertain;
    public HeadEdgeReference? Reference { get; private set; }
    public HeadEdgeAdjustmentSnapshot Current { get; private set; }=Empty(HeadEdgeAdjustmentState.Waiting,"Start monitoring and allow independent edge windows to settle.");
    private static HeadEdgeNeedle Blank=>new(null,null,null,null,0,0);
    private static HeadEdgeAdjustmentSnapshot Empty(HeadEdgeAdjustmentState state,string message)=>new(state,message,false,false,Blank,Blank,Blank,0,0,null,null);
    public void Reset(string reason="Measurement conditions changed — set a new zero.")
    {
        key=null; Reference=null; latest=zero=null; channel=null; lastEnd=null; history.Clear(); zeroUncertain=false;
        displayedRise=displayedFall=displayedCombined=null; Current=Empty(HeadEdgeAdjustmentState.Waiting,reason);
    }
    public void SetDamping(double seconds)
    { if(!double.IsFinite(seconds)||seconds is < .1 or > 3) throw new ArgumentOutOfRangeException(nameof(seconds)); damping=seconds; }
    public void SetActive(bool value,double now)
    { Advance(now); active=value; if(!value) { history.Clear(); Hold(HeadEdgeAdjustmentState.Paused,"HOLD — paused; retained pointer is not live evidence."); } }
    public void Update(HeadAlignmentMeasurement? measurement,HeadEdgeSource source,int selectedChannel,bool repeatedPassage,double now)
    {
        Advance(now); active=true;
        if(measurement==null) { history.Clear(); latest=null; Hold(HeadEdgeAdjustmentState.Unavailable,"HOLD — waiting for physical edge measurements."); return; }
        if(selectedChannel<0||selectedChannel>=measurement.Channels.Count) throw new ArgumentOutOfRangeException(nameof(selectedChannel));
        var next=new Key(measurement.Format,measurement.Options,measurement.EndFrame-measurement.StartFrame,selectedChannel,source,repeatedPassage);
        if(key!=next||measurement.EndFrame<lastEnd) { Reset(); key=next; }
        var selected=measurement.Channels[selectedChannel]; var w=selected.Edges;
        double sample=1e6/measurement.Format.SampleRate;
        if(lastEnd==measurement.EndFrame) { Advance(now); return; }
        bool raw=selected.ClippedSamples==0&&selected.AcRms>=measurement.Options.MinimumAcRms&&
            next.Frames>=1024&&w!=null&&w.Complete>=16&&selected.RiseCount>=8&&selected.FallCount>=8;
        // Fresh bad data overrides an overlapping good window; successful overlapping polls never count twice.
        if(!raw)
        {
            history.Clear(); latest=null; channel=selected; lastEnd=measurement.EndFrame; lastDataTime=now;
            Current=Build(HeadEdgeAdjustmentState.Unavailable,selected.ClippedSamples>0?"HOLD — digital clipping.":"HOLD — insufficient raw PCM edges; aggregate/FFT evidence cannot qualify the pointer.",false,sample); return;
        }
        string? reason=null; var state=HeadEdgeAdjustmentState.Unstable;
        if(w!.OvershootFraction>.2||w.Backtracking>Math.Max(1,.1*w.Complete)||w.LocalRejected>Math.Max(1,.1*w.Complete)) reason="HOLD — local level loss, ringing or backtracking; rejected edges remain counted.";
        else if(w.Grouping==null||w.Ambiguous>Math.Max(2,.05*w.Detected)||w.Incomplete>Math.Max(2,.05*w.Detected))
        { reason="HOLD — interval identity is ambiguous or transitions were lost; no regrouping against zero."; state=HeadEdgeAdjustmentState.ContentChanged; }
        else if(w.ResolutionLimited>Math.Max(1,.05*w.Complete)) { reason="HOLD — transitions shorter than two sample periods are unresolved, not worse edges."; state=HeadEdgeAdjustmentState.ResolutionLimited; }
        else if(w.LowConfidence>Math.Max(1,.05*w.Complete)) { reason="HOLD — 2–4 sample periods per edge: low confidence. A native 96 kHz capture may help."; state=HeadEdgeAdjustmentState.LowConfidence; }
        else if(w.Groups.Any(g=>g.CandidateCount<4||g.MadPercent>15)) reason="HOLD — too few or variable candidates in a context group.";
        if(reason!=null)
        {
            history.Clear(); channel=selected; latest=new(measurement.StartFrame,measurement.EndFrame,w); lastEnd=measurement.EndFrame; lastDataTime=now;
            Current=Build(state,reason,true,sample); return;
        }
        if(lastEnd.HasValue&&measurement.StartFrame<lastEnd.Value) return;
        channel=selected; lastEnd=measurement.EndFrame; lastDataTime=now; latest=new(measurement.StartFrame,measurement.EndFrame,w);
        if(Reference!=null&&w.ReferenceId!=Reference.Id)
        { history.Clear(); Current=Build(HeadEdgeAdjustmentState.WarmingUp,"HOLD — waiting for a worker window classified with the frozen zero.",true,sample); return; }
        history.Add(latest); if(history.Count>24) history.RemoveAt(0);
        double duration=next.Frames/(double)next.Format.SampleRate;
        long cutoff=measurement.EndFrame-(long)(next.Format.SampleRate*Math.Max(1.5,4*duration));
        history.RemoveAll(o=>o.End<cutoff);
        if(history.Count<3||(latest.End-history[0].Start)/(double)next.Format.SampleRate<.5)
        { Current=Build(HeadEdgeAdjustmentState.WarmingUp,"HOLD — collecting at least three independent windows over 0.5 s. Early zero remains provisional.",true,sample); return; }
        if(!Stable()) { Current=Build(HeadEdgeAdjustmentState.Unstable,"HOLD — context groups or their times changed across independent windows.",true,sample); return; }
        if(zero==null) { Current=Build(HeadEdgeAdjustmentState.ReadyToZero,"Ready for a manual zero on this tape. FFT is diagnostic only.",true,sample); return; }
        if(Reference is not { Qualified:true })
        { Current=Build(HeadEdgeAdjustmentState.Uncertain,"HOLD — provisional zero cannot certify a trend. Set a new zero after independent windows settle.",true,sample); return; }
        if(w.Range/Reference.WindowRange is < .75 or > 1.33)
        { Current=Build(HeadEdgeAdjustmentState.ContentChanged,"HOLD — signal range changed substantially; check gain, dropout or analog clipping before comparing edges.",true,sample); return; }
        var groups=Aggregated();
        if(groups.Count!=Reference.Groups.Count||Reference.Groups.Any(z=>!groups.Any(g=>g.Id==z.Id)))
        { Current=Build(HeadEdgeAdjustmentState.ContentChanged,"HOLD — a frozen reference group is missing; weights are never redistributed.",true,sample); return; }
        double fraction=w.Candidates/(double)Math.Max(1,w.Detected-w.Boundary);
        if(Math.Abs(fraction-Reference.AcceptedFraction)>.08||Reference.Groups.Any(z=>
            groups.First(g=>g.Id==z.Id) is var g&&(Math.Abs(g.LocalRange-z.LocalRange)>.15||Math.Abs(g.CandidateCount/(double)Math.Max(1,g.Count)-z.CandidateCount/(double)Math.Max(1,z.Count))>.1)))
        { Current=Build(HeadEdgeAdjustmentState.ContentChanged,"HOLD — accepted/rejected edge population or local range changed; survivor bias cannot count as improvement.",true,sample); return; }
        var logs=Reference.Groups.Select(z=>Math.Log(z.Microseconds/groups.First(g=>g.Id==z.Id).Microseconds)).ToArray();
        double uncertainty=Reference.Groups.Max(z=>sample/Math.Min(z.Microseconds,groups.First(g=>g.Id==z.Id).Microseconds));
        if(logs.Any(v=>!double.IsFinite(v)||Math.Abs(v)>Math.Log(2))||logs.Max()-logs.Min()>Math.Max(.15,uncertainty)||logs.Min()<-.03&&logs.Max()>.03)
        { Current=Build(HeadEdgeAdjustmentState.Conflict,"HOLD — rising/falling or context groups disagree, or change is implausibly large.",true,sample); return; }
        double combined=LogChange(groups,null);
        if(Math.Abs(combined)>1e-6&&Math.Abs(Math.Log(1+combined/100))<uncertainty)
        { Current=Build(HeadEdgeAdjustmentState.Uncertain,"HOLD — change is smaller than the sampling uncertainty allowance.",true,sample); return; }
        Current=Build(repeatedPassage?HeadEdgeAdjustmentState.RepeatedPassage:HeadEdgeAdjustmentState.WorkingCopy,
            "Relative physical edge quality from frozen context groups; verify a promising setting on the same passage. No calibrated mechanical angle.",true,sample);
    }
    private bool Stable()
    {
        var current=latest!.Window;
        foreach(var observation in history)
        {
            var old=observation.Window;
            if(!SameGrouping(old.Grouping,current.Grouping)||old.Groups.Count!=current.Groups.Count) return false;
            foreach(var g in current.Groups)
            {
                var prior=old.Groups.FirstOrDefault(v=>v.Id==g.Id);
                if(prior==null||!double.IsFinite(g.Microseconds)||Math.Abs(prior.Microseconds/g.Microseconds-1)>.15) return false;
            }
        }
        return true;
    }
    private static bool SameGrouping(HeadEdgeGrouping? a,HeadEdgeGrouping? b)
    {
        if(a==null||b==null) return false;
        bool Same(IReadOnlyList<HeadEdgeFamily> x,IReadOnlyList<HeadEdgeFamily> y)=>x.Count==y.Count&&x.Select((v,i)=>Math.Abs(v.CenterSamples/y[i].CenterSamples-1)<.08).All(v=>v);
        return Same(a.High,b.High)&&Same(a.Low,b.Low);
    }
    private IReadOnlyList<HeadEdgeGroup> Aggregated()
    {
        if(latest==null) return [];
        var result=new List<HeadEdgeGroup>();
        foreach(var g in latest.Window.Groups)
        {
            var matches=history.Select(o=>o.Window.Groups.FirstOrDefault(v=>v.Id==g.Id)).OfType<HeadEdgeGroup>().ToArray();
            if(matches.Length==0) { result.Add(g); continue; }
            result.Add(g with { Microseconds=HeadEdgeAnalysis.Median(matches.Select(v=>v.Microseconds)),LocalRange=HeadEdgeAnalysis.Median(matches.Select(v=>v.LocalRange)),
                Count=matches.Sum(v=>v.Count),CandidateCount=matches.Sum(v=>v.CandidateCount),MadPercent=matches.Max(v=>v.MadPercent) });
        }
        return result.AsReadOnly();
    }
    public bool ZeroHere(double now)
    {
        Advance(now); if(!Current.CanZero||latest==null||key==null) return false;
        bool qualified=Current.State is HeadEdgeAdjustmentState.ReadyToZero or HeadEdgeAdjustmentState.WorkingCopy or HeadEdgeAdjustmentState.RepeatedPassage;
        // A stable, currently held change may be explicitly re-zeroed, never automatically.
        if(history.Count>=3&&(latest.End-history[0].Start)/(double)key.Format.SampleRate>=.5&&Stable()&&latest.Window.Groups.All(g=>g.CandidateCount>=4)&&latest.Window.LowConfidence==0&&latest.Window.ResolutionLimited==0)
            qualified=true;
        zeroUncertain=!qualified; zero=new(history.Count>0?history[0].Start:latest.Start,latest.End,latest.Window);
        var groups=Aggregated();
        Reference=latest.Window.Grouping is {} grouping?new(Guid.NewGuid(),grouping,groups,latest.Window.Candidates/(double)Math.Max(1,latest.Window.Detected-latest.Window.Boundary),zero.Start,zero.End,qualified,latest.Window.Range):null;
        displayedRise=displayedFall=displayedCombined=0;
        Current=Build(qualified?(key.Repeated?HeadEdgeAdjustmentState.RepeatedPassage:HeadEdgeAdjustmentState.WorkingCopy):HeadEdgeAdjustmentState.Uncertain,
            qualified?"Qualified manual zero saved with fixed context identities and weights.":"Provisional manual zero saved; HOLD until a new explicit qualified zero.",true,1e6/key.Format.SampleRate);
        Current=Current with { Rise=Current.Rise with { DisplayPercent=0 },Fall=Current.Fall with { DisplayPercent=0 },Combined=Current.Combined with { DisplayPercent=0 } };
        history.Clear(); return true;
    }
    private double LogChange(IReadOnlyList<HeadEdgeGroup> groups,bool? rising)
    {
        if(Reference==null) return double.NaN;
        double Direction(bool direction)
        {
            var z=Reference.Groups.Where(g=>g.Rising==direction).ToArray(); double weight=z.Sum(g=>g.CandidateCount);
            if(weight<=0) return double.NaN;
            return z.Sum(g=>g.CandidateCount/weight*Math.Log(g.Microseconds/(groups.FirstOrDefault(v=>v.Id==g.Id)?.Microseconds??double.NaN)));
        }
        double log=rising.HasValue?Direction(rising.Value):(Direction(true)+Direction(false))*.5;
        return 100*(Math.Exp(log)-1);
    }
    public HeadEdgeAdjustmentSnapshot Advance(double now)
    {
        if(!double.IsFinite(now)||now<lastClock) throw new ArgumentOutOfRangeException(nameof(now),"Use a monotonic presentation clock.");
        double dt=lastClock==0?0:now-lastClock; lastClock=now;
        if(active&&lastEnd.HasValue&&now-lastDataTime>Math.Max(1,key==null?1:2.5*key.Frames/key.Format.SampleRate))
        { history.Clear(); Hold(HeadEdgeAdjustmentState.Stale,"HOLD — stale retained pointer; no fresh evidence."); }
        if(Current.State is HeadEdgeAdjustmentState.WorkingCopy or HeadEdgeAdjustmentState.RepeatedPassage)
        {
            double alpha=1-Math.Exp(-dt/damping);
            double? Smooth(double? old,double? target)=>target.HasValue?old.HasValue?old+(target-old)*alpha:target:old;
            displayedRise=Smooth(displayedRise,Current.Rise.ChangePercent); displayedFall=Smooth(displayedFall,Current.Fall.ChangePercent); displayedCombined=Smooth(displayedCombined,Current.Combined.ChangePercent);
            Current=Current with { Rise=Current.Rise with { DisplayPercent=displayedRise },Fall=Current.Fall with { DisplayPercent=displayedFall },Combined=Current.Combined with { DisplayPercent=displayedCombined } };
        }
        return Current;
    }
    private HeadEdgeAdjustmentSnapshot Build(HeadEdgeAdjustmentState state,string message,bool canZero,double sample)
    {
        var groups=Aggregated();
        bool show=state is HeadEdgeAdjustmentState.WorkingCopy or HeadEdgeAdjustmentState.RepeatedPassage;
        double? Time(IReadOnlyList<HeadEdgeGroup>? source,bool? direction)
        {
            var a=source?.Where(g=>!direction.HasValue||g.Rising==direction).Where(g=>double.IsFinite(g.Microseconds)).ToArray();
            return a is {Length:>0}?HeadEdgeAnalysis.Median(a.Select(g=>g.Microseconds)):null;
        }
        HeadEdgeNeedle Needle(bool? direction,double? displayed)
        {
            double? change=show?LogChange(groups,direction):null;
            if(change.HasValue&&!double.IsFinite(change.Value)) change=null;
            double temporal=groups.Where(g=>!direction.HasValue||g.Rising==direction).Select(g=>
            {
                var times=history.Select(o=>o.Window.Groups.FirstOrDefault(v=>v.Id==g.Id)?.Microseconds??double.NaN).Where(double.IsFinite).ToArray();
                double median=HeadEdgeAnalysis.Median(times);
                return times.Length<2||median<=0?0:100*HeadEdgeAnalysis.Median(times.Select(t=>Math.Abs(t-median)))/median;
            }).DefaultIfEmpty().Max();
            return new(Time(groups,direction),Time(Reference?.Groups,direction),change,displayed,
                groups.Where(g=>!direction.HasValue||g.Rising==direction).Select(g=>g.MadPercent).DefaultIfEmpty().Max(),temporal);
        }
        return new(state,message,canZero,zero!=null,Needle(true,displayedRise),Needle(false,displayedFall),Needle(null,displayedCombined),
            history.Count,sample,zero?.Start,zero?.End) { ZeroIsUncertain=zeroUncertain,Groups=groups,
            Diagnostics=(latest?.Window.Diagnostics??"No edge window.")+" Fixed reference groups: "+(Reference?.Groups.Count??0)+". "+
                string.Join("; ",groups.Select(g=>$"{(g.Rising?"Rise":"Fall")} {g.Id%4}: n={g.CandidateCount}/{g.Count}, {(double.IsFinite(g.Microseconds)?g.Microseconds.ToString("F2")+" µs":"unresolved")}, MAD {g.MadPercent:F1}%")) };
    }
    private void Hold(HeadEdgeAdjustmentState state,string message)
    {
        Current=Current with { State=state,Message=message,CanZero=false,
            Rise=Current.Rise with { ChangePercent=null },Fall=Current.Fall with { ChangePercent=null },Combined=Current.Combined with { ChangePercent=null } };
    }
}
