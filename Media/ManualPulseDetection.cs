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

// Alternating lobes are gated by Schmitt thresholds. A crossing is measured at
// the midpoint of adjacent opposite peaks and interpolated to 1/8 sample.
// The bounded ring discards gaps instead of inventing edges across missing data.
internal sealed class AdaptivePeakCrossing(Action<bool, double, long, double> emit, double zeroDeadband = 0)
{
    private readonly double[] samples = new double[4096];
    private bool? positive;
    private long peakIndex, previousPeakIndex;
    private double peak, previousPeak;
    private bool hasPrevious;
    private double? lastEdge;
    internal void Process(long index, double value, double threshold)
    {
        samples[index % samples.Length] = value;
        // A fixed minimum gate supplements the envelope gate. Keep the original
        // samples for midpoint interpolation; the deadband never shifts PCM.
        threshold = Math.Max(threshold, zeroDeadband);
        if (!positive.HasValue)
        {
            if (Math.Abs(value) < threshold) return;
            positive = value > 0; peak = value; peakIndex = index; return;
        }
        bool transition = positive.Value ? value <= -threshold : value >= threshold;
        if (transition)
        {
            CompletePeak(); positive = !positive.Value; peak = value; peakIndex = index;
        }
        else if (positive.Value ? value > peak : value < peak) { peak = value; peakIndex = index; }
        // A long flat lobe cannot retain a usable opposite peak in the ring.
        if (hasPrevious && index - previousPeakIndex >= samples.Length) { hasPrevious = false; lastEdge = null; }
    }
    internal void Flush() { if (positive.HasValue) CompletePeak(); }
    private void CompletePeak()
    {
        if (hasPrevious && peakIndex > previousPeakIndex && peakIndex - previousPeakIndex < samples.Length)
        {
            double middle = (peak + previousPeak) / 2, steepest = 0;
            double? crossing = null;
            bool rising = peak > previousPeak;
            for (long i = previousPeakIndex + 1; i <= peakIndex; i++)
            {
                double a = samples[(i - 1) % samples.Length], b = samples[i % samples.Length];
                if ((rising ? a <= middle && b > middle : a >= middle && b < middle) && Math.Abs(b - a) > steepest)
                { steepest = Math.Abs(b - a); crossing = Math.Round((i - 1 + (middle - a) / (b - a)) * 8) / 8; }
            }
            if (crossing.HasValue)
            {
                if (lastEdge.HasValue && crossing > lastEdge) emit(!rising, crossing.Value - lastEdge.Value, (long)Math.Round(crossing.Value), 1);
                lastEdge = crossing;
            }
            else lastEdge = null;
        }
        previousPeak = peak; previousPeakIndex = peakIndex; hasPrevious = true;
    }
}
