using System.Collections.Generic;
using System;
using System.Linq;

namespace MZTools;

internal sealed record AudioRecoveryPass(double ThresholdScale, int ValidHeaders, int ValidPayloads, int Candidates)
{
    public string Test { get; init; } = "Adaptive";
    public double? TimeScale { get; init; }
    public double? ShortMicroseconds { get; init; }
    public double? FuzzyTolerance { get; init; }
    public string Detectors { get; init; } = "ZeroCrossing + Schmitt";
    public string Channels { get; init; } = "All";
    public string Polarity { get; init; } = "Both";
    public string AudioTransform { get; init; } = "Original PCM";
    public string? Reference { get; init; }
    public string? ReferenceSource { get; init; }
    public string? ReferenceEvidence { get; init; }
    public double? ReferenceTolerance { get; init; }
    public bool HalfSample { get; init; }
    public double ZeroDeadbandPercent { get; init; }
    internal IReadOnlyList<AudioRecoveryMeasurement> Measurements { get; init; } = [];
}

internal sealed record AudioRecoveryOptions
{
    internal bool PayloadOnly { get; init; }
    internal bool UnknownLength { get; init; }
    internal bool Diagnostics { get; init; } = true;
    internal bool ZeroCrossing { get; init; } = true;
    internal bool Schmitt { get; init; } = true;
    internal bool AdaptiveZeroCrossing { get; init; }
    internal double ZeroDeadband { get; init; }
    internal bool ReferenceTests { get; init; }
    internal IReadOnlyList<AudioPulseReference> References { get; init; } = [];
    internal double ReferenceTolerance { get; init; } = .20;
    internal bool HalfSample { get; init; }
    internal bool Normal { get; init; } = true;
    internal bool Inverted { get; init; } = true;
    internal int? Channel { get; init; }
    internal double[] ThresholdScales { get; init; } = [.4, .65, 1, 1.4];
    internal bool Adaptive { get; init; } = true;
    internal bool FixedTiming { get; init; }
    internal bool Fuzzy { get; init; }
    internal double ShortMicroseconds { get; init; } = 238;
    internal double[] TimeScales { get; init; } = [.9, 1, 1.1];
    internal double FuzzyTolerance { get; init; } = .35;
    internal AudioPcmTransform Transform { get; init; } = new();
    internal void Validate(int channels)
    {
        if (!double.IsFinite(ZeroDeadband) || ZeroDeadband < 0 || ZeroDeadband > 1)
            throw new ArgumentException("Adaptive zero deadband must be between 0% and 100% FS.");
        if ((!ZeroCrossing && !Schmitt && !AdaptiveZeroCrossing) || (!Normal && !Inverted) || (!Adaptive && !FixedTiming && !Fuzzy && !ReferenceTests))
            throw new ArgumentException("Select at least one detector, polarity and test.");
        if (Channel.HasValue && (Channel < 0 || Channel >= channels)) throw new ArgumentException("The selected channel is unavailable.");
        if (ThresholdScales.Length is < 1 or > 8 || ThresholdScales.Any(v => !double.IsFinite(v) || v < .1 || v > 4))
            throw new ArgumentException("Enter 1–8 threshold scales between 0.1 and 4.");
        if (FixedTiming && (!double.IsFinite(ShortMicroseconds) || ShortMicroseconds < 10 || ShortMicroseconds > 2000 || TimeScales.Length is < 1 or > 8 || TimeScales.Any(v => !double.IsFinite(v) || v < .25 || v > 4)))
            throw new ArgumentException("Fixed timing requires a SHORT duration of 10–2000 µs and 1–8 time scales between 0.25 and 4.");
        if (Fuzzy && (!double.IsFinite(FuzzyTolerance) || FuzzyTolerance < .05 || FuzzyTolerance > .6))
            throw new ArgumentException("Fuzzy tolerance must be between 5% and 60%.");
        if (ReferenceTests && (References.Count is < 1 or > 64 || !double.IsFinite(ReferenceTolerance) || ReferenceTolerance < .01 || ReferenceTolerance > .6 ||
            References.Any(r => AudioPulseReference.Groups.Any(group => !r.Guides.Any(g => g.Group == group)) || r.Guides.Any(g => !double.IsFinite(g.Microseconds) || g.Microseconds <= 0))))
            throw new ArgumentException("Select 1–64 reference profiles with all four positive pulse groups and a tolerance of 1–60%.");
    }
}
internal sealed record AudioRegionRecoveryResult(string Source, uint SampleRate, long StartSample, long EndSample,
    IReadOnlyList<TapeRecord> Records, IReadOnlyList<WavRecoveryInfo> Recoveries,
    IReadOnlyList<SharpBlockCandidate> Candidates, IReadOnlyList<WavAnalysisFailure> Failures, IReadOnlyList<AudioRecoveryPass> Passes)
{
    public IReadOnlyList<AudioHeaderRejection> RejectedHeaders { get; init; } = [];
    public int OmittedHeaderRejections { get; init; }
    internal IReadOnlyList<AudioRecoveryEvent> Diagnostics { get; init; } = [];
    internal int OmittedDiagnostics { get; init; }
}
