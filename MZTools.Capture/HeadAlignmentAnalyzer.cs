using System.Numerics;

namespace MZTools.Capture;

/// <summary>Bounded diagnostic measurements on a copied contiguous preview window, never on archive data.</summary>
public static class HeadAlignmentAnalyzer
{
    public static HeadAlignmentMeasurement Measure(ReadOnlySpan<double> left,ReadOnlySpan<double> right,AudioCaptureFormat format,
        long firstFrame,IReadOnlyList<LiveSpectrumBin> spectrum,HeadAlignmentOptions options)
    {
        options.Validate(format.SampleRate);
        if(left.Length>32768||firstFrame<0||firstFrame>long.MaxValue-left.Length||format.Channels==2&&right.Length!=left.Length)
            throw new ArgumentException("Invalid bounded alignment window.");
        double limit=format.Representation==AudioSampleRepresentation.Float32?float.MaxValue:1;
        foreach(double x in left) if(!double.IsFinite(x)||Math.Abs(x)>limit) throw new ArgumentException("Invalid normalized alignment sample.");
        if(format.Channels==2) foreach(double x in right) if(!double.IsFinite(x)||Math.Abs(x)>limit) throw new ArgumentException("Invalid normalized alignment sample.");
        if(spectrum.Count!=0&&spectrum.Count!=left.Length/2+1) throw new ArgumentException("Spectrum must describe this exact alignment window.");
        double maxDb=20*Math.Log10(2*limit)+1e-6;
        for(int i=0;i<spectrum.Count;i++)
        {
            var bin=spectrum[i];
            if(!double.IsFinite(bin.FrequencyHz)||Math.Abs(bin.FrequencyHz-i*format.SampleRate/(double)left.Length)>1e-6||
               !double.IsFinite(bin.LeftDbfs)||!double.IsFinite(bin.RightDbfs)||bin.LeftDbfs < -160||bin.RightDbfs < -160||bin.LeftDbfs>maxDb||bin.RightDbfs>maxDb)
                throw new ArgumentException("Spectrum must use the matching finite, normalized peak-amplitude grid.");
        }
        var channels=new List<HeadAlignmentChannel> { Channel(left,format.Channels==1?"Mono":"Left",0,format,spectrum,options) };
        if(format.Channels==2) channels.Add(Channel(right,"Right",1,format,spectrum,options));
        var stereo=format.Channels==2 ? Stereo(left,right,format.SampleRate,options,channels) : EmptyStereo("Not assessable: mono input; use the individual indicators.");
        return new(format,options,firstFrame,firstFrame+left.Length,channels.AsReadOnly(),stereo);
    }
    private static HeadAlignmentChannel Channel(ReadOnlySpan<double> x,string name,int ch,AudioCaptureFormat f,IReadOnlyList<LiveSpectrumBin> spectrum,HeadAlignmentOptions options)
    {
        double mean=0,squares=0; long clipped=0;
        double positive=f.Representation==AudioSampleRepresentation.Float32?1:1-Math.Pow(2,1-f.ValidBitsPerSample);
        foreach(double value in x) { mean+=value; if(value<=-1||value>=positive) clipped++; }
        if(x.Length>0) mean/=x.Length;
        foreach(double value in x) squares+=(value-mean)*(value-mean);
        double rms=x.Length>0?Math.Sqrt(squares/x.Length):0;
        bool full=x.Length>=1024&&(x.Length&(x.Length-1))==0&&spectrum.Count>0;
        bool assessable=full&&rms>=options.MinimumAcRms&&clipped==0;
        string quality=!full?"Not assessable: need a complete contiguous FFT window (≥1024 samples).":rms<options.MinimumAcRms?"Not assessable: insufficient AC energy.":clipped>0?"Not assessable: digital clipping.":"Individual indicators only; comparable reference passage required for A/B.";
        double lowEnergy=0,highEnergy=0;
        foreach(var bin in spectrum)
        {
            if(!double.IsFinite(bin.FrequencyHz)||!double.IsFinite(bin.LeftDbfs)||!double.IsFinite(bin.RightDbfs)) throw new ArgumentException("Invalid spectrum.");
            if(bin.FrequencyHz<options.BandLowHz||bin.FrequencyHz>Math.Min(options.BandHighHz,f.SampleRate*.5)) continue;
            double power=Math.Pow(10,(ch==0?bin.LeftDbfs:bin.RightDbfs)/10);
            if(bin.FrequencyHz==f.SampleRate*.5) power*=2; // Nyquist was not doubled in the peak-amplitude spectrum.
            if(bin.FrequencyHz<options.BandSplitHz) lowEnergy+=power; else highEnergy+=power;
        }
        double? hf=spectrum.Count>0&&lowEnergy+highEnergy>1e-12?100*highEnergy/(lowEnergy+highEnergy):null;
        if(x.Length<2) return new(name,assessable,quality,rms,mean,hf,null,null,null,0,0,clipped);
        var sorted=x.ToArray(); Array.Sort(sorted);
        double lo=sorted[(int)((sorted.Length-1)*.05)],hi=sorted[(int)((sorted.Length-1)*.95)],range=hi-lo;
        double slope=0; for(int i=1;i<x.Length;i++) slope+=Math.Abs(x[i]-x[i-1]);
        double lower=lo+range*.1,upper=lo+range*.9,rising=double.NaN,falling=double.NaN;
        var rises=new List<double>(); var falls=new List<double>();
        double riseTravel=0,fallTravel=0; int roughTransitions=0,positiveSamples=0,plateauSamples=0;
        foreach(double value in x) { if(value>=(lo+hi)*.5) positiveSamples++; if(value<=lower||value>=upper) plateauSamples++; }
        if(range>1e-9)
        for(int i=1;i<x.Length;i++)
        {
            double previous=x[i-1],current=x[i];
            double Crossing(double threshold)=>i-1+(threshold-previous)/(current-previous);
            double travel=Math.Abs(Math.Clamp(current,lower,upper)-Math.Clamp(previous,lower,upper));
            if(double.IsFinite(rising)) riseTravel+=travel;
            if(double.IsFinite(falling)) fallTravel+=travel;
            if(current<=lower) rising=double.NaN;
            if(current>=upper) falling=double.NaN;
            if(previous<=lower&&current>lower) { rising=Crossing(lower); riseTravel=travel; }
            if(previous<upper&&current>=upper&&double.IsFinite(rising)) { rises.Add((Crossing(upper)-rising)*1e6/f.SampleRate); if(riseTravel>(upper-lower)*1.2) roughTransitions++; rising=double.NaN; }
            if(previous>=upper&&current<upper) { falling=Crossing(upper); fallTravel=travel; }
            if(previous>lower&&current<=lower&&double.IsFinite(falling)) { falls.Add((Crossing(lower)-falling)*1e6/f.SampleRate); if(fallTravel>(upper-lower)*1.2) roughTransitions++; falling=double.NaN; }
        }
        static double? Median(List<double> values) { if(values.Count==0) return null; values.Sort(); return (values[(values.Count-1)/2]+values[values.Count/2])*.5; }
        double? rise=Median(rises),fall=Median(falls);
        double Mad(List<double> values,double? median)=>median is >0?100*(Median(values.Select(v=>Math.Abs(v-median.Value)).ToList())??0)/median.Value:0;
        double overshoot=range>1e-9?Math.Max(0,Math.Max(lo-sorted[(int)((sorted.Length-1)*.001)],sorted[(int)((sorted.Length-1)*.999)]-hi))/range:0;
        var evidence=new HeadEdgeEvidence(Mad(rises,rise),Mad(falls,fall),
            rises.Count+falls.Count>0?roughTransitions/(double)(rises.Count+falls.Count):0,overshoot,
            positiveSamples/(double)x.Length,plateauSamples/(double)x.Length,(rises.Count+falls.Count)*f.SampleRate/(double)x.Length);
        return new(name,assessable,quality,rms,mean,hf,range>1e-9?slope/(x.Length-1)/range*f.SampleRate/1000:null,rise,fall,rises.Count,falls.Count,clipped) { EdgeEvidence=evidence };
    }
    private static HeadAlignmentStereo EmptyStereo(string status)=>new(false,status,null,null,null,null,null,null,null,null,null,null,false,0);
    private static HeadAlignmentStereo Stereo(ReadOnlySpan<double> left,ReadOnlySpan<double> right,int rate,HeadAlignmentOptions options,List<HeadAlignmentChannel> channels)
    {
        if(channels.Any(c=>!c.Assessable)) return EmptyStereo("Not assessable: both channels need sufficient unclipped AC energy and ≥1024 contiguous samples.");
        int n=left.Length,segmentLength=1; while(segmentLength*8<=n) segmentLength*=2;
        // Qualified inputs are a complete power-of-two window; four segments cover that exact interval.
        int total=segmentLength*4; int offset=n-total;
        left=left[offset..]; right=right[offset..];
        double zero=Correlation(left,right,0),level=20*Math.Log10(channels[1].AcRms/channels[0].AcRms);
        double coherence=BandCoherence(left,right,segmentLength,rate,options);
        if(options.KnownToneHz is double hz)
        {
            if(hz*segmentLength/rate<4) return new(false,"Not assessable: need at least four known-tone cycles per coherence segment; increase FFT/window length.",zero,null,coherence,null,null,null,null,null,level,null,false,4);
            Complex cross=Complex.Zero; double xx=0,yy=0,purity=1; Complex phases=Complex.Zero;
            double firstPhase=0,lastPhase=0,firstLeft=0,lastLeft=0,firstRight=0,lastRight=0;
            for(int s=0;s<4;s++)
            {
                var a=FitTone(left.Slice(s*segmentLength,segmentLength),hz,rate);
                var b=FitTone(right.Slice(s*segmentLength,segmentLength),hz,rate);
                var product=Complex.Conjugate(a.Value)*b.Value;
                cross+=product; xx+=a.Value.Magnitude*a.Value.Magnitude; yy+=b.Value.Magnitude*b.Value.Magnitude;
                purity=Math.Min(purity,Math.Min(a.Purity,b.Purity));
                double phase=product.Phase; if(s==0) firstPhase=phase; lastPhase=phase;
                double referencePhase=2*Math.PI*hz*s*segmentLength/rate;
                double lphase=a.Value.Phase-referencePhase,rphase=b.Value.Phase-referencePhase;
                if(s==0) { firstLeft=lphase; firstRight=rphase; } lastLeft=lphase; lastRight=rphase;
                phases+=Complex.FromPolarCoordinates(1,phase);
            }
            double toneCoherence=xx*yy>0?Math.Clamp(cross.Magnitude*cross.Magnitude/(xx*yy),0,1):0,stability=phases.Magnitude/4;
            double drift=Math.Abs(Math.Atan2(Math.Sin(lastPhase-firstPhase),Math.Cos(lastPhase-firstPhase)));
            double leftDrift=Math.Abs(Math.Atan2(Math.Sin(lastLeft-firstLeft),Math.Cos(lastLeft-firstLeft)));
            double rightDrift=Math.Abs(Math.Atan2(Math.Sin(lastRight-firstRight),Math.Cos(lastRight-firstRight)));
            bool qualified=purity>=.9&&toneCoherence>=.95&&stability>=.995&&Math.Max(drift,Math.Max(leftDrift,rightDrift))<=5*Math.PI/180;
            return new(qualified,qualified?"Qualified known tone. Phase is modulo one cycle; periodic signal has ambiguous absolute delay. This is signal phase, not head angle.":"Not assessable: declared tone frequency, purity, cross-channel coherence or phase stability check failed.",zero,null,toneCoherence,null,null,qualified?cross.Phase*180/Math.PI:null,purity,stability,level,null,false,4);
        }
        int lagLimit=Math.Min(options.MaximumLagSamples,segmentLength/8); var averaged=new double[lagLimit*2+1]; var peaks=new int[4];
        var perSegment=new double[4][];
        for(int s=0;s<4;s++)
        {
            var a=left.Slice(s*segmentLength,segmentLength); var b=right.Slice(s*segmentLength,segmentLength);
            var values=perSegment[s]=new double[averaged.Length];
            for(int lag=-lagLimit;lag<=lagLimit;lag++) { double c=Correlation(a,b,lag); values[lag+lagLimit]=c; averaged[lag+lagLimit]+=c/4; }
            peaks[s]=BestPeak(values)-lagLimit;
        }
        int best=BestPeak(averaged),sampleLag=best-lagLimit; double magnitude=Math.Abs(averaged[best]),other=0;
        for(int i=1;i<averaged.Length-1;i++) if(Math.Abs(i-best)>2&&Math.Abs(averaged[i])>=Math.Abs(averaged[i-1])&&Math.Abs(averaged[i])>=Math.Abs(averaged[i+1])) other=Math.Max(other,Math.Abs(averaged[i]));
        // A boundary maximum cannot prove a unique peak inside this finite search range.
        other=Math.Max(other,Math.Abs(averaged[0])); other=Math.Max(other,Math.Abs(averaged[^1]));
        bool unique=magnitude-other>=.05&&best>0&&best<averaged.Length-1;
        double spread=peaks.Max()-peaks.Min(); bool stable=spread<=1&&peaks.All(p=>Math.Abs(p-sampleLag)<=1);
        bool assessable=magnitude>=.9&&coherence>=.8&&unique&&stable;
        double sub=0;
        if(best>0&&best<averaged.Length-1)
        {
            double a=Math.Abs(averaged[best-1]),b=Math.Abs(averaged[best]),c=Math.Abs(averaged[best+1]),denom=a-2*b+c;
            if(Math.Abs(denom)>1e-12) sub=Math.Clamp(.5*(a-c)/denom,-.5,.5);
        }
        double lagEstimate=sampleLag+sub;
        return new(assessable,assessable?"Related broadband signal: stable unique lag and band coherence; relative channel timing only.":"Not assessable: correlation/coherence is weak, lag is unstable, periodic or at the search boundary.",zero,averaged[best],coherence,assessable?lagEstimate:null,assessable?lagEstimate*1e6/rate:null,null,null,null,level,spread,unique,4);
    }
    private static int BestPeak(double[] values)
    {
        int best=values.Length/2;
        for(int i=0;i<values.Length;i++) if(Math.Abs(values[i])>Math.Abs(values[best])+1e-9) best=i;
        return best;
    }
    private static double Correlation(ReadOnlySpan<double> left,ReadOnlySpan<double> right,int lag)
    {
        int start=Math.Max(0,-lag),end=Math.Min(left.Length,right.Length-lag),n=end-start;
        double x=0,y=0,xx=0,yy=0,xy=0;
        for(int i=start;i<end;i++) { double a=left[i],b=right[i+lag]; x+=a; y+=b; xx+=a*a; yy+=b*b; xy+=a*b; }
        double denominator=Math.Sqrt(Math.Max(0,xx-x*x/n)*Math.Max(0,yy-y*y/n));
        return denominator>1e-20?Math.Clamp((xy-x*y/n)/denominator,-1,1):0;
    }
    private static double BandCoherence(ReadOnlySpan<double> left,ReadOnlySpan<double> right,int length,int rate,HeadAlignmentOptions options)
    {
        var x=new Complex[length]; var y=new Complex[length]; var xx=new double[length/2+1]; var yy=new double[xx.Length]; var xy=new Complex[xx.Length];
        var window=Enumerable.Range(0,length).Select(i=>.5-.5*Math.Cos(2*Math.PI*i/length)).ToArray();
        for(int s=0;s<4;s++)
        {
            double mx=0,my=0; for(int i=0;i<length;i++) { mx+=left[s*length+i]; my+=right[s*length+i]; } mx/=length; my/=length;
            for(int i=0;i<length;i++) { x[i]=new((left[s*length+i]-mx)*window[i],0); y[i]=new((right[s*length+i]-my)*window[i],0); }
            LiveSignalAnalyzer.Transform(x); LiveSignalAnalyzer.Transform(y);
            for(int i=1;i<xx.Length;i++) { xx[i]+=x[i].Magnitude*x[i].Magnitude; yy[i]+=y[i].Magnitude*y[i].Magnitude; xy[i]+=Complex.Conjugate(x[i])*y[i]; }
        }
        double weighted=0,weight=0,peakX=xx.Max(),peakY=yy.Max();
        for(int i=1;i<xx.Length;i++)
        {
            double hz=i*rate/(double)length;
            if(hz<options.BandLowHz||hz>Math.Min(options.BandHighHz,rate*.5)||xx[i]<peakX*1e-4||yy[i]<peakY*1e-4||xx[i]*yy[i]<=1e-30) continue;
            double w=Math.Sqrt(xx[i]*yy[i]); weighted+=Math.Clamp(xy[i].Magnitude*xy[i].Magnitude/(xx[i]*yy[i]),0,1)*w; weight+=w;
        }
        return weight>0?weighted/weight:0;
    }
    private static (Complex Value,double Purity) FitTone(ReadOnlySpan<double> data,double hz,int rate)
    {
        double wsum=0,mx=0,ms=0,mc=0; int length=data.Length;
        double W(int i)=>.5-.5*Math.Cos(2*Math.PI*i/length);
        for(int i=0;i<data.Length;i++) { double w=W(i),phase=2*Math.PI*hz*i/rate; wsum+=w; mx+=w*data[i]; ms+=w*Math.Sin(phase); mc+=w*Math.Cos(phase); }
        mx/=wsum; ms/=wsum; mc/=wsum;
        double ss=0,cc=0,sc=0,xs=0,xc=0,energy=0;
        for(int i=0;i<data.Length;i++) { double w=W(i),phase=2*Math.PI*hz*i/rate,s=Math.Sin(phase)-ms,c=Math.Cos(phase)-mc,x=data[i]-mx; ss+=w*s*s; cc+=w*c*c; sc+=w*s*c; xs+=w*x*s; xc+=w*x*c; energy+=w*x*x; }
        double determinant=ss*cc-sc*sc;
        if(determinant<=1e-20||energy<=1e-20) return(Complex.Zero,0);
        double a=(xs*cc-xc*sc)/determinant,b=(xc*ss-xs*sc)/determinant;
        return(new(b,-a),Math.Clamp((a*xs+b*xc)/energy,0,1));
    }
}
