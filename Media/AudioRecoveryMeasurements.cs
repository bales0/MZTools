using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal sealed record AudioRecoveryMeasurement(AudioBlockAnalysis Block, IReadOnlyList<AudioAnalysisIssue> Issues, int Omitted);

// Aggregated while the decoding pass consumes pulses, never by a second PCM scan.
// SHORT/LONG colours compare measured durations with this pass's reference; they
// are not a claim that the decoder accepted a bit. Even rejected pulses are kept.
internal sealed class AudioRecoveryMeasurements(uint sampleRate, long start, long end,
    AudioRecoveryOptions options, AudioPulseReference? reference, double? shortMicroseconds, string pcm = "", string passId = "Deep analysis")
{
    private readonly Dictionary<(int Channel, WavPulseMode Detector, bool Inverted), AudioPulseMeasurementEngine> series = [];
    private AudioPulseMeasurementEngine Create(int channel, WavPulseMode detector, bool inverted) => new(new(pcm, sampleRate, start, end, channel + 1,
        detector.ToString(), inverted, detector == WavPulseMode.ZeroCrossing ? 1 : options.ThresholdScales[0], options.ZeroDeadband * 100, reference?.CatalogId ?? reference?.Name ?? "rom800", 1, "Deep comparison", passId), targets);
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
            if (options.Normal) series.TryAdd((channel, detector, false), Create(channel,detector,false));
            if (options.Inverted) series.TryAdd((channel, detector, true), Create(channel,detector,true));
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
            if (!series.TryGetValue(key, out var s)) series[key] = s = Create(channel,detector,inverted);
            s.Add(high, duration, pulseEnd);
        }
    }

    internal IReadOnlyList<AudioRecoveryMeasurement> Finish() => series.OrderBy(p => p.Key.Channel).ThenBy(p => p.Key.Detector).ThenBy(p => p.Key.Inverted).Select(p =>
    {
        var metrics = p.Value.Finish();
        return new AudioRecoveryMeasurement(new AudioBlockAnalysis(1, "Deep analysis", "Range", "Measured pulses",
            reference?.Name ?? (shortMicroseconds.HasValue ? "Fixed timing comparison" : SharpZ80TimingTable.Rom800Normal.Name),
            p.Key.Channel + 1, p.Key.Inverted, p.Key.Detector.ToString(), options.Transform.Description, start, end, false, 0, 0, false,
            metrics.Distributions, metrics.Scatter, metrics.ScatterPairs, metrics.Drift, new(0,0,0,null,null),metrics.GlobalScale,
            metrics.GlobalScale is > 0 ? (1 / metrics.GlobalScale.Value - 1) * 100 : null, metrics.Outliers)
            { PulseSource = p.Value.Source, OmittedMeasurementSpans = metrics.Omitted }, metrics.Issues, metrics.Omitted);
    }).ToArray();
}
