using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal sealed record AudioRecoveryMeasurement(AudioBlockAnalysis Block, IReadOnlyList<AudioAnalysisIssue> Issues, int Omitted);

// Aggregated while the decoding pass consumes pulses, never by a second PCM scan.
// SHORT/LONG colours compare measured durations with this pass's reference; they
// are not a claim that the decoder accepted a bit. Even rejected pulses are kept.
internal sealed class AudioRecoveryMeasurements(uint sampleRate, long start, long end,
    AudioRecoveryOptions options, AudioPulseReference? reference, double? shortMicroseconds)
{
    private sealed class Series
    {
        internal readonly AudioDurationCounter[] Counts = [new(), new(), new(), new()];
        internal readonly AudioIssueSpans Issues = new(4096);
        internal long Outliers;
        internal int WindowCount;
        internal long WindowStart, PreviousStart;
        internal double RatioSum;
        internal double? PreviousMean;
    }
    private readonly Dictionary<(int Channel, WavPulseMode Detector, bool Inverted), Series> series = [];
    private readonly double[][] targets = AudioPulseReference.Groups.Select((group, i) => reference != null
        ? reference.Guides.Where(g => g.Group == group).Select(g => g.Microseconds).ToArray()
        : new[] { shortMicroseconds.HasValue ? shortMicroseconds.Value * (i >= 2 ? 2 : 1) : i switch
            { 0 => SharpZ80TimingTable.Rom800Normal.ShortHighMicroseconds, 1 => SharpZ80TimingTable.Rom800Normal.ShortLowMicroseconds,
              2 => SharpZ80TimingTable.Rom800Normal.LongHighMicroseconds, _ => SharpZ80TimingTable.Rom800Normal.LongLowMicroseconds } }).ToArray();

    internal void IncludeChannel(int channel)
    {
        if (options.Channel.HasValue && options.Channel != channel) return;
        foreach (var (detector, enabled) in new[] { (WavPulseMode.ZeroCrossing, options.ZeroCrossing), (WavPulseMode.Schmitt, options.Schmitt), (WavPulseMode.AdaptiveZeroCrossing, options.AdaptiveZeroCrossing) })
        {
            if (!enabled) continue;
            if (options.Normal) series.TryAdd((channel, detector, false), new());
            if (options.Inverted) series.TryAdd((channel, detector, true), new());
        }
    }

    internal void Observe(int channel, WavPulseMode detector, bool high, double duration, long pulseEnd)
    {
        if (options.Channel.HasValue && options.Channel != channel || duration <= 0 || pulseEnd - duration < start || pulseEnd > end) return;
        if (detector == WavPulseMode.ZeroCrossing && !options.ZeroCrossing || detector == WavPulseMode.Schmitt && !options.Schmitt ||
            detector == WavPulseMode.AdaptiveZeroCrossing && !options.AdaptiveZeroCrossing) return;
        if (options.Normal) Add(false);
        if (options.Inverted) Add(true);
        void Add(bool inverted)
        {
            var key = (channel, detector, inverted);
            if (!series.TryGetValue(key, out var s)) series[key] = s = new();
            int shortGroup = high == inverted ? 0 : 1;
            double us = duration * 1e6 / sampleRate;
            double Error(int group)
            {
                double best = double.PositiveInfinity;
                foreach (double target in targets[group]) best = Math.Min(best, Math.Abs(us / target - 1));
                return best;
            }
            int group = Error(shortGroup + 2) < Error(shortGroup) ? shortGroup + 2 : shortGroup;
            s.Counts[group].Add(duration);
            if (Error(group) > .35)
            {
                s.Outliers++;
                s.Issues.Add("AUDIO_PULSE_OUTLIERS", "Pulse measured in Deep analysis differs by more than 35% from its comparison reference; exact damaged byte unknown.",
                    (long)Math.Floor(pulseEnd - duration), pulseEnd, channel + 1);
            }
            if (s.WindowCount == 0) s.WindowStart = (long)Math.Floor(pulseEnd - duration);
            s.RatioSum += us / targets[group][0]; s.WindowCount++;
            if (s.WindowCount == 256)
            {
                double mean = s.RatioSum / s.WindowCount;
                if (s.PreviousMean is > 0 && Math.Abs(mean / s.PreviousMean.Value - 1) > .1)
                    s.Issues.Add("AUDIO_TIMING_DISCONTINUITY", "Adjacent 256-pulse windows in Deep analysis differ by more than 10%.", s.PreviousStart, pulseEnd, channel + 1);
                s.PreviousMean = mean; s.PreviousStart = s.WindowStart; s.RatioSum = 0; s.WindowCount = 0;
            }
        }
    }

    internal IReadOnlyList<AudioRecoveryMeasurement> Finish() => series.OrderBy(p => p.Key.Channel).ThenBy(p => p.Key.Detector).ThenBy(p => p.Key.Inverted).Select(p =>
        new AudioRecoveryMeasurement(new AudioBlockAnalysis(1, "Deep analysis", "Range", "Measured pulses",
            reference?.Name ?? (shortMicroseconds.HasValue ? "Fixed timing comparison" : SharpZ80TimingTable.Rom800Normal.Name),
            p.Key.Channel + 1, p.Key.Inverted, p.Key.Detector.ToString(), options.Transform.Description, start, end, false, 0, 0, false,
            p.Value.Counts.Select((c, i) => c.Finish(AudioPulseReference.Groups[i], sampleRate, targets[i][0])).ToArray(), [], 0, [],
            new(0, 0, 0, null, null), null, null, p.Value.Outliers), p.Value.Issues.Items.ToArray(), p.Value.Issues.Omitted)).ToArray();
}
