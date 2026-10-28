namespace MZTools.Capture;

public enum LivePulseDetector { ZeroCrossing, Hysteresis, Adaptive }
public enum LivePulseGroup { ShortHigh, ShortLow, LongHigh, LongLow, UnclassifiedHigh, UnclassifiedLow }
public sealed record LivePulseGuide(LivePulseGroup Group,double Microseconds,string Context);
/// <summary>Injected projection of the application's one reference catalog, or explicitly entered user values.</summary>
public sealed record LivePulseReference(string Id,string Name,string CatalogVersion,string Source,string Evidence,string Kind,IReadOnlyList<LivePulseGuide> Guides);
public sealed record LivePulseOptions
{
    public bool ZeroCrossing { get; init; } = true;
    public bool Hysteresis { get; init; } = true;
    public bool Adaptive { get; init; } = true;
    public double ThresholdScale { get; init; } = 1;
    public double AdaptiveDeadbandFs { get; init; }
    public bool InvertPolarity { get; init; } = true;
    public double ReferenceScale { get; init; } = 1;
    public double Tolerance { get; init; } = .20;
    public double WindowSeconds { get; init; } = 2;
    public int MaximumPulsesPerStream { get; init; } = 16384;
    public required LivePulseReference Reference { get; init; }
    internal void Validate()
    {
        if(!ZeroCrossing&&!Hysteresis&&!Adaptive||!double.IsFinite(ThresholdScale)||ThresholdScale is < .1 or > 4||
           !double.IsFinite(AdaptiveDeadbandFs)||AdaptiveDeadbandFs is < 0 or > 1||!double.IsFinite(ReferenceScale)||ReferenceScale is < .1 or > 10||
           !double.IsFinite(Tolerance)||Tolerance is < 0 or > 1||!double.IsFinite(WindowSeconds)||WindowSeconds is < .05 or > 10||MaximumPulsesPerStream is < 64 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(LivePulseOptions));
        if(Reference==null||string.IsNullOrWhiteSpace(Reference.Id)||Reference.Guides.Count>256||Reference.Guides.Any(g=>!Enum.IsDefined(g.Group)||g.Group>LivePulseGroup.LongLow||!double.IsFinite(g.Microseconds)||g.Microseconds is <= 0 or > 1_000_000)||
           Enumerable.Range(0,4).Any(group=>!Reference.Guides.Any(g=>(int)g.Group==group))) throw new ArgumentException("Select a duration reference with all four SHORT/LONG HIGH/LOW groups.");
    }
}
public sealed record LivePulseViewOptions
{
    public LivePulseDetector Detector { get; init; }
    public int Channel { get; init; }
    public double? MinimumMicroseconds { get; init; }
    public double? MaximumMicroseconds { get; init; }
    public int Bins { get; init; } = 320;
    internal void Validate(int channels)
    {
        if(!Enum.IsDefined(Detector)||Channel<0||Channel>=channels||Bins is < 8 or > 1024||
           MinimumMicroseconds.HasValue&&(!double.IsFinite(MinimumMicroseconds.Value)||MinimumMicroseconds<0)||
           MaximumMicroseconds.HasValue&&(!double.IsFinite(MaximumMicroseconds.Value)||MaximumMicroseconds<=0||MaximumMicroseconds>1e9)||
           MinimumMicroseconds.HasValue!=MaximumMicroseconds.HasValue||MaximumMicroseconds<=MinimumMicroseconds)
            throw new ArgumentOutOfRangeException(nameof(LivePulseViewOptions));
    }
}
public readonly record struct LiveMeasuredPulse(long EndFrame,double Samples,bool PhysicalHigh);
public sealed record LivePulseComparison(long EndFrame,double Microseconds,bool PhysicalHigh,LivePulseGroup Group,double? ReferenceMicroseconds,double? ResidualPercent,bool ReferenceMatch);
public sealed record LivePulseDistribution(LivePulseGroup Group,long Count,double? MeanMicroseconds,double? StandardDeviationMicroseconds,double? CvPercent);
public sealed record LivePulseHistogramBin(double MinimumMicroseconds,double MaximumMicroseconds,IReadOnlyList<int> Counts);
public sealed record LivePulseTimingWindow(long StartFrame,long EndFrame,int Matches,double MeanTimingScale,double ResidualRmsPercent);
public sealed record LivePulseStreamStatus(LivePulseDetector Detector,string Channel,long DetectedSinceReset,int Retained,long CapacityEvictions,double CurrentGateFs,long AdaptiveHistoryResets=0);
public sealed record LivePulseSnapshot(AudioCaptureFormat Format,LivePulseOptions Options,LivePulseDetector Detector,int Channel,long StartFrame,long EndFrame,long ContinuityResets,string ResetReason,
    long MeasuredPulses,long ReferenceMatches,long Unclassified,long CapacityEvictions,long TraceRowsOmitted,long VisiblePulses,
    double HistogramMinimumMicroseconds,double HistogramMaximumMicroseconds,IReadOnlyList<LivePulseHistogramBin> Histogram,
    IReadOnlyList<LivePulseDistribution> Distributions,IReadOnlyList<LivePulseTimingWindow> Timing,IReadOnlyList<LivePulseComparison> RecentPulses,
    IReadOnlyList<LivePulseStreamStatus> Streams,double? MeanTimingScale,double? ResidualJitterPercent,double? DriftPercent)
{
    public string Preprocessing=>"Shared offline DC tracker 0.75 s; 20 Hz high-pass; envelope attack 5 ms / release 250 ms; base gate max(0.0025 FS, envelope ×0.22). Diagnostic branch only.";
    public string Meaning=>"Measured durations → explicit reference comparison; decoder bits / checksums / loader acceptance NOT RUN. Independent detector streams are not pooled.";
}
