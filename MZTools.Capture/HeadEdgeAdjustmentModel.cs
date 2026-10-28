namespace MZTools.Capture;

public enum HeadEdgeAdjustmentState { Waiting, WarmingUp, ReadyToZero, WorkingCopy, RepeatedPassage, Unstable, ContentChanged, ResolutionLimited, Unavailable, Paused, Stale }
public sealed record HeadEdgeSource(string DeviceId,string ProcessingSignature,long Discontinuities,string RecordingLabel="");
public sealed record HeadEdgeNeedle(double? CurrentMicroseconds,double? ZeroMicroseconds,double? ChangePercent,
    double? DisplayPercent,double WindowMadPercent,double TemporalMadPercent);
public sealed record HeadEdgeAdjustmentSnapshot(HeadEdgeAdjustmentState State,string Message,bool CanZero,bool HasZero,
    HeadEdgeNeedle Rise,HeadEdgeNeedle Fall,HeadEdgeNeedle Combined,int Windows,double SamplePeriodMicroseconds,long? ZeroStartFrame,long? ZeroEndFrame)
{
    public bool ZeroIsUncertain { get; init; }
}

/// <summary>Two relative edge indicators for matching a particular tape copy. Never estimates a mechanical angle or edits PCM.</summary>
public sealed class HeadEdgeAdjustmentModel
{
    private sealed record Key(AudioCaptureFormat Format,HeadAlignmentOptions Options,long Frames,int Channel,HeadEdgeSource Source,bool Repeated);
    private sealed record Observation(long Start,long End,double Rise,double Fall,HeadEdgeEvidence Evidence);
    private readonly List<Observation> history=[];
    private Key? key;
    private Observation? zero;
    private Observation? latest;
    private long? lastEnd;
    private HeadAlignmentChannel? channel;
    private double lastDataTime,lastClock;
    private double? displayedRise,displayedFall,displayedCombined;
    private bool active;
    private bool zeroUncertain;
    private double damping=.5;
    public HeadEdgeAdjustmentSnapshot Current { get; private set; }=Empty(HeadEdgeAdjustmentState.Waiting,"Start monitoring and allow the edge statistics to settle.");
    private static HeadEdgeNeedle Blank=>new(null,null,null,null,0,0);
    private static HeadEdgeAdjustmentSnapshot Empty(HeadEdgeAdjustmentState state,string message)=>new(state,message,false,false,Blank,Blank,Blank,0,0,null,null);

    public void Reset(string reason="Measurement conditions changed — set a new zero.")
    {
        key=null; zero=latest=null; zeroUncertain=false; channel=null; history.Clear(); lastEnd=null; displayedRise=displayedFall=displayedCombined=null;
        Current=Empty(HeadEdgeAdjustmentState.Waiting,reason);
    }
    public void SetDamping(double seconds)
    { if(!double.IsFinite(seconds)||seconds is < .1 or > 3) throw new ArgumentOutOfRangeException(nameof(seconds)); damping=seconds; }
    public void SetActive(bool value,double now)
    {
        Advance(now); active=value;
        if(!value) { history.Clear(); Hold(HeadEdgeAdjustmentState.Paused,"Paused — retained measurements are not live. Zeroing is disabled."); }
    }
    public void Update(HeadAlignmentMeasurement? measurement,HeadEdgeSource source,int selectedChannel,bool repeatedPassage,double now)
    {
        Advance(now); active=true;
        if(measurement==null) { history.Clear(); Hold(HeadEdgeAdjustmentState.Unavailable,"No current measurement; waiting for usable edges."); return; }
        if(selectedChannel<0||selectedChannel>=measurement.Channels.Count) throw new ArgumentOutOfRangeException(nameof(selectedChannel));
        var nextKey=new Key(measurement.Format,measurement.Options,measurement.EndFrame-measurement.StartFrame,selectedChannel,source,repeatedPassage);
        if(key!=nextKey) { Reset(); key=nextKey; }
        var selected=measurement.Channels[selectedChannel];
        if(lastEnd.HasValue&&measurement.EndFrame<lastEnd) { Reset("Source position moved backwards — set a new zero."); key=nextKey; }
        string? invalid=null; var state=HeadEdgeAdjustmentState.Unavailable; var evidence=selected.EdgeEvidence;
        double sample=1e6/measurement.Format.SampleRate;
        if(latest?.End==measurement.EndFrame&&now-lastDataTime>Math.Max(1,2.5*nextKey.Frames/measurement.Format.SampleRate)) { Advance(now); return; }
        bool measurable=selected.Assessable&&selected.ClippedSamples==0&&selected.RiseCount>=8&&selected.FallCount>=8&&
            selected.RiseMicroseconds is >0&&double.IsFinite(selected.RiseMicroseconds.Value)&&selected.FallMicroseconds is >0&&double.IsFinite(selected.FallMicroseconds.Value)&&evidence!=null;
        if(!selected.Assessable||selected.ClippedSamples>0) invalid=selected.ClippedSamples>0?"Digital clipping — manual zero is unavailable.":selected.Quality;
        else if(!measurable) invalid="Need at least eight complete rising and falling edges in a full window.";
        else if(selected.RiseMicroseconds<2*sample||selected.FallMicroseconds<2*sample)
        { invalid="Sampling limit: fewer than two sample periods across an edge; a relative improvement is not resolved."; state=HeadEdgeAdjustmentState.ResolutionLimited; }
        else if(evidence!.RiseMadPercent>25||evidence.FallMadPercent>25||evidence.BacktrackingFraction>.20||evidence.OvershootFraction>.20)
        { invalid="Edges are variable, noisy or ringing — wait for a steadier section; a shorter spike is not an improvement."; state=HeadEdgeAdjustmentState.Unstable; }
        if(invalid!=null)
        {
            channel=selected; history.Clear();
            if(latest?.End!=measurement.EndFrame) lastDataTime=now;
            latest=measurable?new(measurement.StartFrame,measurement.EndFrame,selected.RiseMicroseconds!.Value,selected.FallMicroseconds!.Value,evidence!):null;
            lastEnd=measurement.EndFrame;
            Current=Build(state,invalid+(measurable?" Manual zero is available from the current robust edge medians, with uncertainty.":""),measurable,sample,0,0); return;
        }
        // Reject bad signal immediately, but do not count overlapping valid windows as new evidence.
        if(lastEnd.HasValue&&measurement.StartFrame<lastEnd.Value) { Advance(now); return; }
        channel=selected; lastEnd=measurement.EndFrame; lastDataTime=now;
        latest=new(measurement.StartFrame,measurement.EndFrame,channel.RiseMicroseconds!.Value,channel.FallMicroseconds!.Value,evidence!);
        history.Add(latest);
        double duration=(measurement.EndFrame-measurement.StartFrame)/(double)measurement.Format.SampleRate;
        long cutoff=measurement.EndFrame-(long)(measurement.Format.SampleRate*Math.Max(1.5,3.5*duration));
        history.RemoveAll(o=>o.End<cutoff); if(history.Count>24) history.RemoveAt(0);
        double medianRise=Median(history.Select(o=>o.Rise)),medianFall=Median(history.Select(o=>o.Fall));
        double riseMad=Mad(history.Select(o=>o.Rise),medianRise),fallMad=Mad(history.Select(o=>o.Fall),medianFall);
        bool settled=history.Count>=3&&(latest.End-history[0].Start)/(double)measurement.Format.SampleRate>=.3;
        if(!settled) { Current=Build(HeadEdgeAdjustmentState.WarmingUp,"Collecting distinct windows. Manual zero is available now; its stability is not yet established.",true,sample,riseMad,fallMad); return; }
        if(riseMad>20||fallMad>20||Math.Abs(latest.Rise/medianRise-1)>.20||Math.Abs(latest.Fall/medianFall-1)>.20) { Current=Build(HeadEdgeAdjustmentState.Unstable,"Timing is changing across windows — content, tape speed or adjustment may be responsible. Manual zero is available with uncertainty.",true,sample,riseMad,fallMad); return; }
        if(zero==null) { Current=Build(HeadEdgeAdjustmentState.ReadyToZero,"Ready: zero on a usable part of this recording. A studio reference is not required.",true,sample,riseMad,fallMad); return; }
        var old=zero.Evidence; var current=evidence!;
        double density=current.TransitionsPerSecond/Math.Max(1e-9,old.TransitionsPerSecond);
        if(density is < .65 or > 1.55||Math.Abs(current.PositiveFraction-old.PositiveFraction)>.15||Math.Abs(current.PlateauFraction-old.PlateauFraction)>.25)
        { Current=Build(HeadEdgeAdjustmentState.ContentChanged,"Possible content/shape change — comparison held. Replay the earlier section, or zero on this stable section. This cannot be separated from adjustment automatically.",true,sample,riseMad,fallMad); return; }
        Current=Build(repeatedPassage?HeadEdgeAdjustmentState.RepeatedPassage:HeadEdgeAdjustmentState.WorkingCopy,
            repeatedPassage?"Repeated passage declared — relative edge change for this copy; no calibrated head angle.":"Working comparison for this tape copy. Content varies: verify a promising setting by replaying the same passage.",true,sample,riseMad,fallMad);
        Advance(now);
    }
    public bool ZeroHere(double now)
    {
        Advance(now);
        if(!Current.CanZero||latest==null||key==null) return false;
        var previousState=Current.State;
        zeroUncertain=previousState is HeadEdgeAdjustmentState.WarmingUp or HeadEdgeAdjustmentState.Unstable or HeadEdgeAdjustmentState.ResolutionLimited;
        zero=latest; // current window already uses robust edge medians; manual zero means exactly "here"
        displayedRise=displayedFall=displayedCombined=0;
        Current=Build(zeroUncertain?previousState:key.Repeated?HeadEdgeAdjustmentState.RepeatedPassage:HeadEdgeAdjustmentState.WorkingCopy,
            zeroUncertain?"Manual zero saved from current edge medians. Live comparison remains held until measurement quality permits it.":"Zero saved for this recording. Positive = shorter edge than this zero; verify with repeated playback.",true,1e6/key.Format.SampleRate,Current.Rise.TemporalMadPercent,Current.Fall.TemporalMadPercent);
        Current=Current with { Rise=Current.Rise with { DisplayPercent=0 },Fall=Current.Fall with { DisplayPercent=0 },Combined=Current.Combined with { DisplayPercent=0 } };
        return true;
    }
    public HeadEdgeAdjustmentSnapshot Advance(double now)
    {
        double dt=CheckClock(now);
        double staleAfter=key==null?1:Math.Max(1,2.5*key.Frames/key.Format.SampleRate);
        if(active&&lastEnd.HasValue&&now-lastDataTime>staleAfter)
        { history.Clear(); Hold(HeadEdgeAdjustmentState.Stale,"No fresh measurement — comparison held until new windows arrive."); }
        if(Current.State is HeadEdgeAdjustmentState.WorkingCopy or HeadEdgeAdjustmentState.RepeatedPassage)
        {
            double alpha=1-Math.Exp(-dt/damping);
            if(Current.Rise.ChangePercent is double r) displayedRise=displayedRise.HasValue?displayedRise+(r-displayedRise)*alpha:r;
            if(Current.Fall.ChangePercent is double f) displayedFall=displayedFall.HasValue?displayedFall+(f-displayedFall)*alpha:f;
            if(Current.Combined.ChangePercent is double c) displayedCombined=displayedCombined.HasValue?displayedCombined+(c-displayedCombined)*alpha:c;
            Current=Current with { Rise=Current.Rise with { DisplayPercent=displayedRise },Fall=Current.Fall with { DisplayPercent=displayedFall },Combined=Current.Combined with { DisplayPercent=displayedCombined } };
        }
        return Current;
    }
    private HeadEdgeAdjustmentSnapshot Build(HeadEdgeAdjustmentState state,string message,bool canZero,double sample,double riseMad,double fallMad)
    {
        double? rise=channel?.RiseMicroseconds,fall=channel?.FallMicroseconds;
        bool show=state is HeadEdgeAdjustmentState.WorkingCopy or HeadEdgeAdjustmentState.RepeatedPassage;
        if(zeroUncertain) message+=" Manual zero has uncertain stability/resolution; it is not a qualified reference.";
        double? combined=rise.HasValue&&fall.HasValue?(rise+fall)*.5:null;
        double? zeroCombined=zero==null?null:(zero.Rise+zero.Fall)*.5;
        if(show&&Math.Abs((zero!.Rise/rise!.Value-1)-(zero.Fall/fall!.Value-1))>.25)
            message+=" Rising/falling changes disagree; inspect the separate times before treating the combined movement as useful.";
        return new(state,message,canZero,zero!=null,
            new(rise,zero?.Rise,show&&rise is >0?(zero!.Rise/rise-1)*100:null,displayedRise,channel?.EdgeEvidence?.RiseMadPercent??0,riseMad),
            new(fall,zero?.Fall,show&&fall is >0?(zero!.Fall/fall-1)*100:null,displayedFall,channel?.EdgeEvidence?.FallMadPercent??0,fallMad),
            new(combined,zeroCombined,show&&combined is >0?(zeroCombined/combined-1)*100:null,displayedCombined,Math.Max(channel?.EdgeEvidence?.RiseMadPercent??0,channel?.EdgeEvidence?.FallMadPercent??0),Math.Max(riseMad,fallMad)),
            history.Count,sample,zero?.Start,zero?.End) { ZeroIsUncertain=zeroUncertain };
    }
    private void Hold(HeadEdgeAdjustmentState state,string message)
    {
        Current=Current with { State=state,Message=message,CanZero=false,
            Rise=Current.Rise with { ChangePercent=null },Fall=Current.Fall with { ChangePercent=null },Combined=Current.Combined with { ChangePercent=null } };
    }
    private double CheckClock(double now)
    {
        if(!double.IsFinite(now)||now<lastClock) throw new ArgumentOutOfRangeException(nameof(now),"Use a monotonic presentation clock.");
        double dt=lastClock==0?0:now-lastClock; lastClock=now; return dt;
    }
    private static double Median(IEnumerable<double> values) { var sorted=values.Order().ToArray(); return(sorted[(sorted.Length-1)/2]+sorted[sorted.Length/2])*.5; }
    private static double Mad(IEnumerable<double> values,double median)=>100*Median(values.Select(v=>Math.Abs(v-median)))/median;
}
