namespace MZTools.Capture;

/// <summary>Incremental analysis-worker consumer. Caller serializes Configure/Process/Snapshot; no archival or native input access.</summary>
public sealed class LivePulseAnalyzer
{
    private readonly AudioCaptureFormat format;
    private readonly LivePulseOptions options;
    private readonly TapeSignalPreprocessor[] filters;
    private readonly List<Stream> streams=[];
    private long nextFrame,epochStart,resets;
    private string resetReason="Enabled / new capture";
    private sealed class Stream
    {
        internal required LivePulseDetector Detector;
        internal required int Channel;
        internal required LiveMeasuredPulse[] Ring;
        internal StreamingAdaptivePeakCrossing? Adaptive;
        internal bool? Level;
        internal bool HasEdge;
        internal long Edge,Detected,Evictions;
        internal int Write,Count;
        internal double Gate;
        internal void Add(bool high,double samples,long end)
        {
            if(!double.IsFinite(samples)||samples<=0) return;
            if(Count==Ring.Length) Evictions++; else Count++;
            Ring[Write]=new(end,samples,high); Write=(Write+1)%Ring.Length; Detected++;
        }
        internal void EdgeAt(long frame,bool current)
        {
            if(Level==current) return;
            if(Level.HasValue&&HasEdge&&frame>Edge) Add(Level.Value,frame-Edge,frame);
            if(Level.HasValue) HasEdge=true; // discard startup's unknown partial lobe
            Level=current; Edge=frame;
        }
        internal void Reset()
        { Adaptive?.Reset(); Level=null; HasEdge=false; Edge=Detected=Evictions=0; Write=Count=0; Gate=0; }
        internal void Trim(long cutoff)
        {
            while(Count>0)
            {
                var pulse=Ring[(Write-Count+Ring.Length)%Ring.Length];
                if(pulse.EndFrame-pulse.Samples>=cutoff) break;
                Count--;
            }
        }
        internal LiveMeasuredPulse[] Copy()
        {
            var copy=new LiveMeasuredPulse[Count];
            for(int i=0;i<Count;i++) copy[i]=Ring[(Write-Count+i+Ring.Length)%Ring.Length];
            return copy;
        }
    }
    public LivePulseAnalyzer(AudioCaptureFormat format,LivePulseOptions options,long firstFrame=0)
    {
        options.Validate(); if(firstFrame<0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        this.format=format;
        // Freeze caller-owned guide lists. Historical numbers remain owned by the injected catalog.
        this.options=options with { Reference=options.Reference with { Guides=Array.AsReadOnly(options.Reference.Guides.ToArray()) } };
        filters=Enumerable.Range(0,format.Channels).Select(_=>new TapeSignalPreprocessor((uint)format.SampleRate)).ToArray();
        foreach(int ch in Enumerable.Range(0,format.Channels))
        foreach(var detector in Enum.GetValues<LivePulseDetector>())
        {
            if(detector==LivePulseDetector.ZeroCrossing&&!options.ZeroCrossing||detector==LivePulseDetector.Hysteresis&&!options.Hysteresis||detector==LivePulseDetector.Adaptive&&!options.Adaptive) continue;
            var stream=new Stream { Detector=detector,Channel=ch,Ring=new LiveMeasuredPulse[options.MaximumPulsesPerStream] };
            if(detector==LivePulseDetector.Adaptive) stream.Adaptive=new((high,samples,end,_)=>stream.Add(high,samples,end),options.AdaptiveDeadbandFs);
            streams.Add(stream);
        }
        nextFrame=epochStart=firstFrame;
    }
    public void Reset(long firstFrame,string reason)
    {
        if(firstFrame<0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        foreach(var filter in filters) filter.Reset(); foreach(var stream in streams) stream.Reset();
        nextFrame=epochStart=firstFrame; resets++; resetReason=reason;
    }
    public void Process(long frame,double left,double right)
    {
        if(frame<0||frame==long.MaxValue) throw new ArgumentOutOfRangeException(nameof(frame));
        if(frame!=nextFrame) Reset(frame,"Preview frame gap / discontinuity");
        nextFrame=frame+1;
        if(!double.IsFinite(left)||format.Channels==2&&!double.IsFinite(right)) { Reset(nextFrame,"Non-finite preview sample"); return; }
        for(int ch=0;ch<format.Channels;ch++)
        {
            var (value,threshold)=filters[ch].Process(ch==0?left:right); double gate=threshold*options.ThresholdScale;
            foreach(var stream in streams)
            {
                if(stream.Channel!=ch) continue;
                switch(stream.Detector)
                {
                    case LivePulseDetector.ZeroCrossing: stream.Gate=0; stream.EdgeAt(frame,value>=0); break;
                    case LivePulseDetector.Hysteresis:
                        stream.Gate=gate; bool current=stream.Level??value>=0;
                        if(!current&&value>=gate) current=true; else if(current&&value<=-gate) current=false;
                        stream.EdgeAt(frame,current); break;
                    default: stream.Gate=Math.Max(gate,options.AdaptiveDeadbandFs); stream.Adaptive!.Process(frame,value,gate); break;
                }
            }
        }
        if((frame&255)==0) Trim();
    }
    private long Cutoff=>Math.Max(epochStart,nextFrame-(long)Math.Ceiling(options.WindowSeconds*format.SampleRate));
    private void Trim() { long cutoff=Cutoff; foreach(var stream in streams) stream.Trim(cutoff); }
    /// <summary>Copies only one selected stream under the caller's short sample lock; builds charts later.</summary>
    public LivePulseWindow Capture(LivePulseViewOptions view)
    {
        view.Validate(format.Channels);
        Trim(); var stream=streams.FirstOrDefault(s=>s.Channel==view.Channel&&s.Detector==view.Detector);
        var statuses=streams.Select(s=>new LivePulseStreamStatus(s.Detector,format.Channels==1?"Mono":s.Channel==0?"Left":"Right",s.Detected,s.Count,s.Evictions,s.Gate,s.Adaptive?.OverlengthResets??0)).ToArray();
        return new(format,options,view,Cutoff,nextFrame,resets,stream==null?"Selected detector is disabled":resetReason,stream?.Evictions??0,stream?.Copy()??[],Array.AsReadOnly(statuses));
    }
}

/// <summary>Detached, bounded raw window; expensive classification/statistics/histogram construction occurs off the sample lock.</summary>
public sealed record LivePulseWindow(AudioCaptureFormat Format,LivePulseOptions Options,LivePulseViewOptions View,long StartFrame,long EndFrame,long Resets,string ResetReason,long CapacityEvictions,LiveMeasuredPulse[] Pulses,IReadOnlyList<LivePulseStreamStatus> Streams)
{
    public LivePulseSnapshot Measure()
    {
        Options.Validate(); View.Validate(Format.Channels);
        if(StartFrame<0||EndFrame<StartFrame||Pulses.Length>Options.MaximumPulsesPerStream||Pulses.Any(p=>!double.IsFinite(p.Samples)||p.Samples<=0||p.EndFrame>EndFrame||p.EndFrame-p.Samples<StartFrame))
            throw new ArgumentException("Invalid detached pulse window.");
        var targets=Enumerable.Range(0,4).Select(g=>Options.Reference.Guides.Where(v=>(int)v.Group==g).Select(v=>v.Microseconds).ToArray()).ToArray();
        var rows=new LivePulseComparison[Pulses.Length];
        for(int i=0;i<rows.Length;i++)
        {
            var pulse=Pulses[i]; double us=pulse.Samples*1e6/Format.SampleRate; bool high=pulse.PhysicalHigh!=Options.InvertPolarity;
            int shortGroup=high?0:1,longGroup=shortGroup+2;
            (double Error,double Target) Match(int group)
            {
                double best=double.PositiveInfinity,target=double.NaN;
                foreach(double candidate in targets[group]) { double error=Math.Abs(us/(candidate*Options.ReferenceScale)-1); if(error<best) { best=error; target=candidate; } }
                return(best,target);
            }
            var shortMatch=Match(shortGroup); var longMatch=Match(longGroup); bool isLong=longMatch.Error<shortMatch.Error;
            var best=isLong?longMatch:shortMatch; bool match=best.Error<=Options.Tolerance&&Math.Abs(shortMatch.Error-longMatch.Error)>1e-12;
            var group=match?(LivePulseGroup)(isLong?longGroup:shortGroup):high?LivePulseGroup.UnclassifiedHigh:LivePulseGroup.UnclassifiedLow;
            rows[i]=new(pulse.EndFrame,us,pulse.PhysicalHigh,group,best.Target,(us/(best.Target*Options.ReferenceScale)-1)*100,match);
        }
        var matched=rows.Where(r=>r.ReferenceMatch).ToArray();
        var distributions=Enum.GetValues<LivePulseGroup>().Select(group=>
        {
            var values=rows.Where(r=>r.Group==group).Select(r=>r.Microseconds).ToArray(); double mean=values.Length>0?values.Average():0;
            double sd=values.Length>0?Math.Sqrt(values.Average(v=>(v-mean)*(v-mean))):0;
            return new LivePulseDistribution(group,values.Length,values.Length>0?mean:null,values.Length>0?sd:null,mean>0?100*sd/mean:null);
        }).ToArray();
        var guides=Options.Reference.Guides.Select(g=>g.Microseconds*Options.ReferenceScale);
        double min=View.MinimumMicroseconds??Math.Min(rows.Length>0?rows.Min(r=>r.Microseconds):double.PositiveInfinity,guides.Min());
        double max=View.MaximumMicroseconds??Math.Max(rows.Length>0?rows.Max(r=>r.Microseconds):0,guides.Max());
        if(!View.MinimumMicroseconds.HasValue) { double padding=Math.Max(1,(max-min)*.05); min=Math.Max(0,min-padding); max+=padding; }
        if(max<=min) max=min+1;
        var counts=Enumerable.Range(0,View.Bins).Select(_=>new int[6]).ToArray(); long visible=0;
        foreach(var row in rows)
        {
            if(row.Microseconds<min||row.Microseconds>max) continue;
            int bin=Math.Clamp((int)((row.Microseconds-min)/(max-min)*View.Bins),0,View.Bins-1); counts[bin][(int)row.Group]++; visible++;
        }
        var histogram=counts.Select((c,i)=>new LivePulseHistogramBin(min+(max-min)*i/View.Bins,min+(max-min)*(i+1)/View.Bins,Array.AsReadOnly(c))).ToArray();
        var timing=new List<LivePulseTimingWindow>();
        for(int i=0;i<matched.Length;i+=256)
        {
            var window=matched.Skip(i).Take(256).ToArray(); double ratio=window.Average(r=>r.Microseconds/r.ReferenceMicroseconds!.Value);
            timing.Add(new(Math.Max(StartFrame,(long)Math.Floor(window[0].EndFrame-window[0].Microseconds*Format.SampleRate/1e6)),window[^1].EndFrame,window.Length,ratio,Math.Sqrt(window.Average(r=>r.ResidualPercent!.Value*r.ResidualPercent.Value))));
        }
        double? meanScale=null,jitter=null,drift=null;
        if(matched.Length>0)
        {
            var ratios=matched.Select(r=>r.Microseconds/r.ReferenceMicroseconds!.Value).ToArray(); double mean=ratios.Average();
            meanScale=mean; jitter=100*Math.Sqrt(ratios.Average(r=>(r-mean)*(r-mean)))/mean;
            if(ratios.Length>=32) { int half=ratios.Length/2; double a=ratios.Take(half).Average(),b=ratios.Skip(half).Average(); drift=(b/a-1)*100; }
        }
        return new(Format,Options,View.Detector,View.Channel,StartFrame,EndFrame,Resets,ResetReason,rows.Length,matched.Length,rows.Length-matched.Length,CapacityEvictions,Math.Max(0,rows.Length-256),visible,min,max,Array.AsReadOnly(histogram),Array.AsReadOnly(distributions),timing.AsReadOnly(),Array.AsReadOnly(rows.TakeLast(256).ToArray()),Streams,meanScale,jitter,drift);
    }
}
