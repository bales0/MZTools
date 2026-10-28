namespace MZTools.Capture;

/// <summary>The existing heuristic's diagnostic DC/high-pass/envelope preprocessing, shared unchanged.</summary>
public sealed class TapeSignalPreprocessor
{
    private readonly double dcAlpha,highPassAlpha,envelopeAttack,envelopeRelease;
    private double dc,previousCentered,highPassed,envelope;
    public TapeSignalPreprocessor(uint sampleRate)
    {
        if(sampleRate==0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        dcAlpha=1-Math.Exp(-1.0/(sampleRate*.75)); highPassAlpha=Math.Exp(-2*Math.PI*20/sampleRate);
        envelopeAttack=1-Math.Exp(-1.0/(sampleRate*.005)); envelopeRelease=1-Math.Exp(-1.0/(sampleRate*.250));
    }
    public (double Filtered,double Threshold) Process(double sample)
    {
        dc+=(sample-dc)*dcAlpha; double centered=sample-dc;
        highPassed=highPassAlpha*(highPassed+centered-previousCentered); previousCentered=centered;
        double magnitude=Math.Abs(highPassed); envelope+=(magnitude-envelope)*(magnitude>envelope?envelopeAttack:envelopeRelease);
        return(highPassed,Math.Max(.0025,envelope*.22));
    }
    public void Reset() { dc=previousCentered=highPassed=envelope=0; }
}

/// <summary>Existing opposite-peak midpoint crossing, shared with offline recovery; bounded to 4096 samples.</summary>
public sealed class StreamingAdaptivePeakCrossing(Action<bool,double,long,double> emit,double zeroDeadband=0)
{
    private readonly double[] samples=new double[4096];
    private bool? positive;
    private long peakIndex,previousPeakIndex;
    private double peak,previousPeak;
    private bool hasPrevious;
    private double? lastEdge;
    public long OverlengthResets { get; private set; }
    public void Process(long index,double value,double threshold)
    {
        samples[index%samples.Length]=value;
        threshold=Math.Max(threshold,zeroDeadband);
        if(!positive.HasValue)
        {
            if(Math.Abs(value)<threshold) return;
            positive=value>0; peak=value; peakIndex=index; return;
        }
        bool transition=positive.Value?value<=-threshold:value>=threshold;
        if(transition) { CompletePeak(); positive=!positive.Value; peak=value; peakIndex=index; }
        else if(positive.Value?value>peak:value<peak) { peak=value; peakIndex=index; }
        if(hasPrevious&&index-previousPeakIndex>=samples.Length) { hasPrevious=false; lastEdge=null; OverlengthResets++; }
    }
    public void Flush() { if(positive.HasValue) CompletePeak(); }
    public void Reset() { positive=null; hasPrevious=false; lastEdge=null; peakIndex=previousPeakIndex=0; peak=previousPeak=0; OverlengthResets=0; }
    private void CompletePeak()
    {
        if(hasPrevious&&peakIndex>previousPeakIndex&&peakIndex-previousPeakIndex<samples.Length)
        {
            double middle=(peak+previousPeak)/2,steepest=0; double? crossing=null; bool rising=peak>previousPeak;
            for(long i=previousPeakIndex+1;i<=peakIndex;i++)
            {
                double a=samples[(i-1)%samples.Length],b=samples[i%samples.Length];
                if((rising?a<=middle&&b>middle:a>=middle&&b<middle)&&Math.Abs(b-a)>steepest)
                { steepest=Math.Abs(b-a); crossing=Math.Round((i-1+(middle-a)/(b-a))*8)/8; }
            }
            if(crossing.HasValue)
            {
                if(lastEdge.HasValue&&crossing>lastEdge) emit(!rising,crossing.Value-lastEdge.Value,(long)Math.Round(crossing.Value),1);
                lastEdge=crossing;
            }
            else lastEdge=null;
        }
        previousPeak=peak; previousPeakIndex=peakIndex; hasPrevious=true;
    }
}
