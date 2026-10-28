using System;
using System.Linq;

namespace MZTools;

// Opt-in rules for manual recovery. Durations are never written back to PCM or traces.
internal sealed class ReferencePulseRules
{
    private readonly double[][] targets;
    private readonly double tolerance;
    private readonly bool halfSample;
    internal ReferencePulseRules(AudioPulseReference reference, uint sampleRate, double tolerance, bool halfSample)
    {
        targets = AudioPulseReference.Groups.Select(group => reference.Guides.Where(g => g.Group == group).Select(g => g.Microseconds * sampleRate / 1e6).ToArray()).ToArray();
        this.tolerance = tolerance; this.halfSample = halfSample;
    }
    internal double ShortSamples => targets[0][0];
    internal int Classify(double samples, bool logicalHigh)
    {
        double shortError = Error(samples, logicalHigh ? 0 : 1);
        double longError = Error(samples, logicalHigh ? 2 : 3);
        if (Math.Min(shortError, longError) > tolerance || Math.Abs(shortError - longError) < 1e-12) return -1;
        return shortError < longError ? 0 : 1;
    }
    private double Error(double samples, int group)
    {
        double best = double.PositiveInfinity;
        foreach (double target in targets[group])
        {
            best = Math.Min(best, Math.Abs(samples / target - 1));
            if (halfSample)
            {
                if (samples > .5) best = Math.Min(best, Math.Abs((samples - .5) / target - 1));
                best = Math.Min(best, Math.Abs((samples + .5) / target - 1));
            }
        }
        return best;
    }
}

// Compatibility facade: offline recovery and live analysis share the exact detector.
internal sealed class AdaptivePeakCrossing(Action<bool, double, long, double> emit, double zeroDeadband = 0)
{
    private readonly MZTools.Capture.StreamingAdaptivePeakCrossing detector = new(emit, zeroDeadband);
    internal void Process(long index, double value, double threshold) => detector.Process(index, value, threshold);
    internal void Flush() => detector.Flush();
}
