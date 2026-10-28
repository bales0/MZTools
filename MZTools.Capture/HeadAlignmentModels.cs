namespace MZTools.Capture;

/// <summary>Diagnostic thresholds, not calibrated hardware acceptance limits or historical tape timings.</summary>
public sealed record HeadAlignmentOptions
{
    public double BandLowHz { get; init; } = 300;
    public double BandSplitHz { get; init; } = 3000;
    public double BandHighHz { get; init; } = 12000;
    public double? KnownToneHz { get; init; }
    public int MaximumLagSamples { get; init; } = 48;
    public double MinimumAcRms { get; init; } = .001;
    internal void Validate(int rate)
    {
        if(!double.IsFinite(BandLowHz)||!double.IsFinite(BandSplitHz)||!double.IsFinite(BandHighHz)||
           BandLowHz<=0||BandSplitHz<=BandLowHz||Math.Min(BandHighHz,rate*.5)<=BandSplitHz||BandHighHz<=BandSplitHz||
           KnownToneHz.HasValue&&(!double.IsFinite(KnownToneHz.Value)||KnownToneHz<=0||KnownToneHz>=rate*.5)||
           MaximumLagSamples is < 1 or > 128||!double.IsFinite(MinimumAcRms)||MinimumAcRms is < .000001 or > .1)
            throw new ArgumentOutOfRangeException(nameof(HeadAlignmentOptions),"Check band boundaries, known tone and lag limits.");
    }
}
public sealed record HeadAlignmentChannel(string Channel,bool Assessable,string Quality,double AcRms,double Dc,
    double? HighBandPercent,double? NormalizedSlopePerMs,double? RiseMicroseconds,double? FallMicroseconds,
    int RiseCount,int FallCount,long ClippedSamples)
{
    public HeadEdgeEvidence? EdgeEvidence { get; init; }
}
public sealed record HeadEdgeEvidence(double RiseMadPercent,double FallMadPercent,double BacktrackingFraction,
    double OvershootFraction,double PositiveFraction,double PlateauFraction,double TransitionsPerSecond);
public sealed record HeadAlignmentStereo(bool Assessable,string Status,double? ZeroLagCorrelation,
    double? BestCorrelation,double? Coherence,double? DelaySamples,double? DelayMicroseconds,
    double? SignalPhaseDegrees,double? TonePurity,double? PhaseStability,double? LevelDifferenceDb,
    double? LagSpreadSamples,bool DelayPeakUnique,int CoherenceSegments);
public sealed record HeadAlignmentMeasurement(AudioCaptureFormat Format,HeadAlignmentOptions Options,long StartFrame,long EndFrame,
    IReadOnlyList<HeadAlignmentChannel> Channels,HeadAlignmentStereo Stereo)
{
    public const string Limitation="Match playback to this tape recording — relative assistance, not a calibrated head angle. Recorded alignment, content, EQ and gain affect the result.";
    public string Method=>"Hann band energy; P5–P95 10–90% transitions; DC-removed correlation; four non-overlapping Hann coherence segments. Positive delay means Right lags Left; signal phase is R−L modulo one cycle.";
}
public sealed record HeadAlignmentContext(string DeviceId,string ProcessingSignature,string PassageId,bool SamePassageConfirmed);
public sealed record HeadAlignmentSaved(string Label,DateTimeOffset SavedUtc,HeadAlignmentContext Context,HeadAlignmentMeasurement Measurement);
public sealed record HeadAlignmentDifference(string Channel,string Metric,string Unit,double A,double B)
{
    public double Delta=>B-A;
}
public sealed record HeadAlignmentComparison(bool Assessable,string Status,IReadOnlyList<HeadAlignmentDifference> Differences);

/// <summary>Two explicit user-saved measurements only. Never ranks unrelated live content.</summary>
public sealed class HeadAlignmentComparisonModel
{
    public HeadAlignmentSaved? A { get; private set; }
    public HeadAlignmentSaved? B { get; private set; }
    public void Save(bool reference,HeadAlignmentMeasurement measurement,HeadAlignmentContext context)
    {
        var saved=new HeadAlignmentSaved(reference?"A":"B",DateTimeOffset.UtcNow,context,measurement);
        if(reference) A=saved; else B=saved;
    }
    public void Clear() { A=B=null; }
    public HeadAlignmentComparison Compare()
    {
        HeadAlignmentComparison Reject(string reason)=>new(false,"Not assessable: "+reason,Array.Empty<HeadAlignmentDifference>());
        if(A==null||B==null) return Reject("save both A and B.");
        var a=A.Measurement; var b=B.Measurement;
        if(!A.Context.SamePassageConfirmed||!B.Context.SamePassageConfirmed||string.IsNullOrWhiteSpace(A.Context.PassageId)||
           !string.Equals(A.Context.PassageId.Trim(),B.Context.PassageId.Trim(),StringComparison.Ordinal)) return Reject("confirm replay of the same named reference passage for both measurements.");
        if(A.Context.DeviceId!=B.Context.DeviceId||A.Context.ProcessingSignature!=B.Context.ProcessingSignature||a.Format!=b.Format)
            return Reject("device, processing or delivered PCM format differs.");
        if(a.Options!=b.Options||a.EndFrame-a.StartFrame!=b.EndFrame-b.StartFrame) return Reject("measurement settings or window length differs.");
        if(a.Channels.Any(c=>!c.Assessable)||b.Channels.Any(c=>!c.Assessable)) return Reject("one measurement is too short, silent or clipped.");
        if(a.Format.Channels==2&&a.Options.KnownToneHz.HasValue&&(!a.Stereo.Assessable||!b.Stereo.Assessable)) return Reject("the declared stereo reference tone did not qualify.");
        var differences=new List<HeadAlignmentDifference>();
        for(int i=0;i<a.Channels.Count;i++)
        {
            var left=a.Channels[i]; var right=b.Channels[i];
            void Add(string metric,string unit,double? x,double? y) { if(x.HasValue&&y.HasValue) differences.Add(new(left.Channel,metric,unit,x.Value,y.Value)); }
            Add("AC RMS","FS",left.AcRms,right.AcRms); Add("High-band fraction","%",left.HighBandPercent,right.HighBandPercent);
            Add("Normalized slope","1/ms",left.NormalizedSlopePerMs,right.NormalizedSlopePerMs);
            Add("Rise 10–90%","µs",left.RiseMicroseconds,right.RiseMicroseconds); Add("Fall 90–10%","µs",left.FallMicroseconds,right.FallMicroseconds);
        }
        if(a.Stereo.Assessable&&b.Stereo.Assessable)
        {
            void Add(string metric,string unit,double? x,double? y) { if(x.HasValue&&y.HasValue) differences.Add(new("Stereo",metric,unit,x.Value,y.Value)); }
            Add("Coherence","0–1",a.Stereo.Coherence,b.Stereo.Coherence);
            Add("Right delay","µs",a.Stereo.DelayMicroseconds,b.Stereo.DelayMicroseconds);
            // Phase comparison uses a circular difference; represent B in the nearest equivalent cycle to A.
            if(a.Stereo.SignalPhaseDegrees is double phaseA&&b.Stereo.SignalPhaseDegrees is double phaseB)
                Add("R−L signal phase","° modulo cycle",phaseA,phaseA+Math.Atan2(Math.Sin((phaseB-phaseA)*Math.PI/180),Math.Cos((phaseB-phaseA)*Math.PI/180))*180/Math.PI);
        }
        return new(true,"User-confirmed comparable passage; content identity is not automatically verified. Δ = B − A; no combined quality score.",differences.AsReadOnly());
    }
}
