namespace MZTools.Capture;

public enum HeadEdgeConfidence { ResolutionLimited, LowConfidence, Candidate }
public sealed record HeadEdgeFamily(double CenterSamples,double ToleranceSamples);
public sealed record HeadEdgeGrouping(IReadOnlyList<HeadEdgeFamily> High,IReadOnlyList<HeadEdgeFamily> Low)
{
    public bool Equals(HeadEdgeGrouping? other)=>other!=null&&High.SequenceEqual(other.High)&&Low.SequenceEqual(other.Low);
    public override int GetHashCode() { var hash=new HashCode(); foreach(var f in High) hash.Add(f); hash.Add(-1); foreach(var f in Low) hash.Add(f); return hash.ToHashCode(); }
}
public sealed record HeadEdgeGroup(int Id,bool Rising,int Count,int CandidateCount,int LowConfidenceCount,
    int ResolutionLimitedCount,int RejectedCount,double Microseconds,double MadPercent,double LocalRange);
public sealed record HeadEdgeWindow(HeadEdgeGrouping? Grouping,Guid? ReferenceId,IReadOnlyList<HeadEdgeGroup> Groups,
    int Detected,int Complete,int Boundary,int Incomplete,int Ambiguous,int LocalRejected,int Backtracking,
    int ResolutionLimited,int LowConfidence,int Candidates,double Range,double OvershootFraction)
{
    public bool Equals(HeadEdgeWindow? other)=>other!=null&&Grouping==other.Grouping&&ReferenceId==other.ReferenceId&&Groups.SequenceEqual(other.Groups)&&
        Detected==other.Detected&&Complete==other.Complete&&Boundary==other.Boundary&&Incomplete==other.Incomplete&&Ambiguous==other.Ambiguous&&
        LocalRejected==other.LocalRejected&&Backtracking==other.Backtracking&&ResolutionLimited==other.ResolutionLimited&&LowConfidence==other.LowConfidence&&
        Candidates==other.Candidates&&Range.Equals(other.Range)&&OvershootFraction.Equals(other.OvershootFraction);
    public override int GetHashCode()
    {
        var hash=new HashCode(); hash.Add(Grouping);hash.Add(ReferenceId);foreach(var g in Groups)hash.Add(g);
        hash.Add(Detected);hash.Add(Complete);hash.Add(Boundary);hash.Add(Incomplete);hash.Add(Ambiguous);hash.Add(LocalRejected);hash.Add(Backtracking);
        hash.Add(ResolutionLimited);hash.Add(LowConfidence);hash.Add(Candidates);hash.Add(Range);hash.Add(OvershootFraction);return hash.ToHashCode();
    }
    public string Diagnostics=>$"Detected {Detected}; complete {Complete}; boundary {Boundary}; incomplete {Incomplete}; ambiguous {Ambiguous}; local rejection {LocalRejected}; backtracking {Backtracking}; resolution {ResolutionLimited}; low confidence {LowConfidence}; candidates {Candidates}.";
}
public sealed record HeadEdgeReference(Guid Id,HeadEdgeGrouping Grouping,IReadOnlyList<HeadEdgeGroup> Groups,
    double AcceptedFraction,long StartFrame,long EndFrame,bool Qualified,double WindowRange);

/// <summary>Raw physical PCM crossings. Runs only on a copied worker window, at most 32768 samples.
/// Group identities are learned without a pulse profile and frozen by an explicit reference.</summary>
internal static class HeadEdgeAnalysis
{
    private struct Edge
    {
        internal double Time,Width,Travel;
        internal bool Rising;
    }
    internal static double Median(IEnumerable<double> values)
    {
        var a=values.Order().ToArray(); return a.Length==0?double.NaN:(a[(a.Length-1)/2]+a[a.Length/2])*.5;
    }
    private static double Quantile(double[] sorted,double q)
    {
        double at=(sorted.Length-1)*q; int i=(int)at;
        return sorted[i]+(sorted[Math.Min(i+1,sorted.Length-1)]-sorted[i])*(at-i);
    }
    internal static HeadEdgeWindow Measure(ReadOnlySpan<double> x,int rate,HeadEdgeReference? reference)
    {
        if(x.Length<3) return new(null,reference?.Id,[],0,0,0,0,0,0,0,0,0,0,0,0);
        var sorted=x.ToArray(); Array.Sort(sorted);
        double lo=Quantile(sorted,.05),hi=Quantile(sorted,.95),range=hi-lo;
        double lower=lo+.1*range,upper=lo+.9*range,mid=(lo+hi)*.5;
        double overshoot=range>1e-9?Math.Max(0,Math.Max(lo-Quantile(sorted,.001),Quantile(sorted,.999)-hi))/range:0;
        var edges=System.Buffers.ArrayPool<Edge>.Shared.Rent(x.Length);
        try
        {
        int count=0,riseIndex=-1,fallIndex=-1;
        double riseStart=double.NaN,fallStart=double.NaN,riseTravel=0,fallTravel=0;
        if(range>1e-9) for(int i=1;i<x.Length;i++)
        {
            double a=x[i-1],b=x[i];
            double Cross(double threshold)=>i-1+(threshold-a)/(b-a);
            double travel=Math.Abs(Math.Clamp(b,lower,upper)-Math.Clamp(a,lower,upper));
            if(double.IsFinite(riseStart)) riseTravel+=travel;
            if(double.IsFinite(fallStart)) fallTravel+=travel;
            if(b<=lower) { riseStart=double.NaN; riseIndex=-1; }
            if(b>=upper) { fallStart=double.NaN; fallIndex=-1; }
            if(a<=lower&&b>lower) { riseStart=Cross(lower); riseTravel=travel; riseIndex=-1; }
            if(a>=upper&&b<upper) { fallStart=Cross(upper); fallTravel=travel; fallIndex=-1; }
            if(a<=mid&&b>mid) { riseIndex=count; edges[count++]=new(){Time=Cross(mid),Rising=true}; }
            if(a>=mid&&b<mid) { fallIndex=count; edges[count++]=new(){Time=Cross(mid),Rising=false}; }
            if(a<upper&&b>=upper&&double.IsFinite(riseStart)&&riseIndex>=0)
            { edges[riseIndex].Width=Cross(upper)-riseStart; edges[riseIndex].Travel=riseTravel/(upper-lower); riseStart=double.NaN; }
            if(a>lower&&b<=lower&&double.IsFinite(fallStart)&&fallIndex>=0)
            { edges[fallIndex].Width=Cross(lower)-fallStart; edges[fallIndex].Travel=fallTravel/(upper-lower); fallStart=double.NaN; }
        }
        var high=new List<double>(); var low=new List<double>();
        for(int i=1;i<count;i++) if(edges[i-1].Rising!=edges[i].Rising)
            (edges[i-1].Rising?high:low).Add(edges[i].Time-edges[i-1].Time);
        var grouping=reference?.Grouping;
        if(grouping==null)
        {
            var h=Learn(high); var l=Learn(low);
            if(h!=null&&l!=null) grouping=new(h,l);
        }
        var values=new List<double>[8]; var ranges=new List<double>[8];
        var candidates=new int[8]; var limited=new int[8]; var lowConfidence=new int[8]; var rejected=new int[8]; var total=new int[8];
        for(int i=0;i<8;i++) { values[i]=[]; ranges[i]=[]; }
        int complete=0,incomplete=0,ambiguous=0,localRejected=0,backtracking=0,resolution=0,lc=0,accepted=0;
        for(int i=1;i<count-1;i++)
        {
            var edge=edges[i];
            if(edge.Width>0) complete++; else incomplete++;
            int before=grouping==null?-1:Assign(edge.Time-edges[i-1].Time,edge.Rising?grouping.Low:grouping.High);
            int after=grouping==null?-1:Assign(edges[i+1].Time-edge.Time,edge.Rising?grouping.High:grouping.Low);
            if(before<0||after<0||edges[i-1].Rising==edge.Rising||edges[i+1].Rising==edge.Rising) { ambiguous++; continue; }
            int id=(edge.Rising?0:4)+before*2+after; total[id]++;
            if(edge.Width<=0) { rejected[id]++; continue; }
            int start=Math.Max(0,(int)((edges[i-1].Time+edge.Time)*.5));
            int end=Math.Min(x.Length-1,(int)Math.Ceiling((edge.Time+edges[i+1].Time)*.5));
            double min=double.MaxValue,max=double.MinValue;
            for(int j=start;j<=end;j++) { min=Math.Min(min,x[j]); max=Math.Max(max,x[j]); }
            double local=(max-min)/range,center=Math.Abs((max+min)*.5-mid)/range;
            if(local<.65||local>1.35||center>.2) { localRejected++; rejected[id]++; continue; }
            if(edge.Travel>1.2) { backtracking++; rejected[id]++; continue; }
            if(edge.Width<2) { resolution++; limited[id]++; continue; }
            if(edge.Width<4) { lc++; lowConfidence[id]++; continue; }
            accepted++; candidates[id]++; values[id].Add(edge.Width*1e6/rate); ranges[id].Add(local);
        }
        var groups=new List<HeadEdgeGroup>();
        for(int id=0;id<8;id++) if(total[id]>0)
        {
            double median=Median(values[id]);
            groups.Add(new(id,id<4,total[id],candidates[id],lowConfidence[id],limited[id],rejected[id],median,
                median>0?100*Median(values[id].Select(v=>Math.Abs(v-median)))/median:0,Median(ranges[id])));
        }
        return new(grouping,reference?.Id,groups.AsReadOnly(),count,complete,Math.Min(2,count),incomplete,ambiguous,localRejected,backtracking,resolution,lc,accepted,range,overshoot);
        }
        finally { System.Buffers.ArrayPool<Edge>.Shared.Return(edges); }
    }
    private static int Assign(double value,IReadOnlyList<HeadEdgeFamily> families)
    {
        int result=-1;
        for(int i=0;i<families.Count;i++) if(Math.Abs(value-families[i].CenterSamples)<=families[i].ToleranceSamples)
        { if(result>=0) return -1; result=i; }
        return result;
    }
    private static IReadOnlyList<HeadEdgeFamily>? Learn(List<double> values)
    {
        if(values.Count<8) return null;
        var a=values.Order().ToArray();
        HeadEdgeFamily Family(IEnumerable<double> v)
        {
            var list=v.ToArray(); double median=Median(list),mad=Median(list.Select(x=>Math.Abs(x-median)));
            return new(median,Math.Max(1.5,Math.Max(.12*median,4*mad)));
        }
        double p10=Quantile(a,.1),p90=Quantile(a,.9);
        if(p90/p10<1.25) return Array.AsReadOnly(new[]{Family(a)});
        double c0=p10,c1=p90;
        for(int iteration=0;iteration<12;iteration++)
        {
            double split=Math.Sqrt(c0*c1); var left=a.Where(v=>v<split).ToArray(); var right=a.Where(v=>v>=split).ToArray();
            if(left.Length<8||right.Length<8) return null;
            c0=Median(left); c1=Median(right);
        }
        var f0=Family(a.Where(v=>v<Math.Sqrt(c0*c1))); var f1=Family(a.Where(v=>v>=Math.Sqrt(c0*c1)));
        if(c1/c0<1.45||f0.CenterSamples+f0.ToleranceSamples>=f1.CenterSamples-f1.ToleranceSamples||f0.ToleranceSamples>.3*c0||f1.ToleranceSamples>.3*c1) return null;
        return Array.AsReadOnly(new[]{f0,f1});
    }
}
