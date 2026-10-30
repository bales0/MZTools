using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal sealed record AudioMeasurementInterval(long StartSample, long EndSample);
internal sealed record AudioPulseMeasurementSource(string Pcm, uint SampleRate, long StartSample, long EndSample,
    int Channel, string Detector, bool Inverted, double ThresholdScale, double ZeroDeadbandPercent,
    string ReferenceId, double Scale, string TimingContext, string PassId)
{
    public long PreprocessingStartSample { get; init; } = StartSample;
    public IReadOnlyList<AudioMeasurementInterval> Intervals { get; init; } = [];
    public string Meaning => "Measured pulse durations; reference comparison labels; decoder acceptance is reported separately by candidates/checksums";
}

internal sealed record AudioPulseMetrics(IReadOnlyList<AudioPulseDistribution> Distributions, IReadOnlyList<AudioPulsePoint> Scatter,
    long ScatterPairs, IReadOnlyList<AudioDriftWindow> Drift, long Outliers, double? GlobalScale,
    IReadOnlyList<AudioAnalysisIssue> Issues, int Omitted);

// Sole implementation of measured pulse grouping, moments, drift, scatter and
// localized residuals. It never changes decoder classification or acceptance.
internal sealed class AudioPulseMeasurementEngine(AudioPulseMeasurementSource source, double[][] targets)
{
    private readonly AudioDurationCounter[] counts = [new(),new(),new(),new()];
    private readonly List<AudioPulsePoint> scatter = [];
    private readonly List<AudioDriftWindow> drift = [];
    private readonly AudioIssueSpans issues = new(4096);
    private long pairs, stride = 1, outliers, totalCount, windowStart, windowEnd, pendingStart;
    private double totalRatio, ratioSum, ratioSquares, residualSquares, shortSum, longSum;
    private double? pendingHigh;
    private bool pendingLong, pendingOutlier;
    private int windowCount, shortCount, longCount, omittedWindows;
    private AudioDriftWindow? previousWindow;
    internal AudioPulseMeasurementSource Source => source;

    internal void Add(bool physicalHigh, double samples, long end)
    {
        if (!double.IsFinite(samples) || samples <= 0 || end - samples < source.StartSample || end > source.EndSample) return;
        bool high = physicalHigh == source.Inverted;
        int shortGroup = high ? 0 : 1;
        double us = samples * 1e6 / source.SampleRate;
        (double Error, double Reference) Best(int group)
        {
            double error = double.PositiveInfinity, reference = double.NaN;
            foreach (double value in targets[group])
            {
                if (!double.IsFinite(value) || value <= 0) continue;
                double candidate = Math.Abs(us / (value * source.Scale) - 1);
                if (candidate < error) { error = candidate; reference = value; }
            }
            return (error, reference);
        }
        var shortMatch = Best(shortGroup); var longMatch = Best(shortGroup + 2);
        bool isLong = longMatch.Error < shortMatch.Error;
        int group = shortGroup + (isLong ? 2 : 0);
        var match = isLong ? longMatch : shortMatch;
        if (!double.IsFinite(match.Reference)) return;
        counts[group].Add(samples);
        long start = (long)Math.Floor(end - samples);
        bool outlier = match.Error > .35;
        if (outlier) { outliers++; issues.Add("AUDIO_PULSE_OUTLIERS", "Measured pulse differs by more than 35% from the scaled comparison reference; no damaged byte inferred.", start, end, source.Channel); }
        if (high) { pendingHigh = us; pendingLong = isLong; pendingOutlier = outlier; pendingStart = start; }
        else if (pendingHigh.HasValue)
        {
            pairs++;
            if (pairs % stride == 0) scatter.Add(new(pendingStart, pendingLong, pendingHigh.Value, us, pendingOutlier || outlier));
            if (scatter.Count > 20000) { int kept = 0; for (int i = 0; i < scatter.Count; i += 2) scatter[kept++] = scatter[i]; scatter.RemoveRange(kept, scatter.Count - kept); stride *= 2; }
            pendingHigh = null;
        }
        double ratio = us / match.Reference;
        if (windowCount == 0) windowStart = start;
        windowEnd = end; windowCount++; ratioSum += ratio; ratioSquares += ratio * ratio; residualSquares += Math.Pow(ratio / source.Scale - 1, 2);
        totalRatio += ratio; totalCount++;
        if (isLong) { longSum += us; longCount++; } else { shortSum += us; shortCount++; }
        if (windowCount == 256) Flush();
    }
    private void Flush()
    {
        if (windowCount == 0) return;
        double mean = ratioSum / windowCount;
        var window = new AudioDriftWindow(windowStart, windowEnd, windowCount, mean, shortCount > 0 ? shortSum / shortCount : null,
            longCount > 0 ? longSum / longCount : null, 100 * Math.Sqrt(residualSquares / windowCount),
            100 * Math.Sqrt(Math.Max(0, ratioSquares / windowCount - mean * mean)) / mean);
        if (previousWindow != null && Math.Abs(mean / previousWindow.TimingScale - 1) > .1)
            issues.Add("AUDIO_TIMING_DISCONTINUITY", "Adjacent 256-pulse windows differ by more than 10%.", previousWindow.StartSample, windowEnd, source.Channel);
        previousWindow = window;
        if (drift.Count < 4096) drift.Add(window); else omittedWindows++;
        windowCount = shortCount = longCount = 0; ratioSum = ratioSquares = residualSquares = shortSum = longSum = 0;
    }
    internal AudioPulseMetrics Finish()
    {
        Flush();
        return new(counts.Select((c,i) => c.Finish(AudioPulseReference.Groups[i],source.SampleRate,
            targets[i].FirstOrDefault(double.IsFinite) is double v && v > 0 ? v : null)).ToArray(), scatter.ToArray(),pairs,drift.ToArray(),outliers,
            totalCount > 0 ? totalRatio / totalCount : null,issues.Items.ToArray(),issues.Omitted + omittedWindows);
    }
    internal static double[][] Targets(SharpZ80TimingReference reference) => new[] {
        new[] {reference.ShortHighMicroseconds},new[] {reference.ShortLowMicroseconds},new[] {reference.LongHighMicroseconds},new[] {reference.LongLowMicroseconds} };
}
