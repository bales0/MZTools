using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MZTools;

internal sealed record AudioRangePreview(AudioSignalAnalysis Analysis, AudioBlockAnalysis Block, long Start, long End,
    string Measurement, int OmittedIssueSpans);

internal static class AudioRangeInspection
{
    internal static (long Start, long End)[] MergeRanges(System.Collections.Generic.IEnumerable<(long Start, long End)> ranges)
    {
        var merged = new System.Collections.Generic.List<(long Start, long End)>();
        foreach (var range in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        return merged.ToArray();
    }

    internal static AudioBlockAnalysis AnalyzeSelection(AudioSignalAnalysis source,
        System.Collections.Generic.IEnumerable<(long Start, long End)> ranges, int channel, bool mix,
        AudioBlockAnalysis? referenceBlock, CancellationToken token = default)
    {
        var spans = MergeRanges(ranges);
        if (spans.Length == 0) throw new ArgumentException("Select a nonempty audio interval.");
        var counts = AudioPulseReference.Groups.Select(_ => new AudioDurationCounter()).ToArray();
        AudioBlockAnalysis? first = null;
        long outliers = 0;
        foreach (var span in spans)
        {
            token.ThrowIfCancellationRequested();
            var block = Analyze(source, span.Start, span.End, new(Mix: mix), channel,
                WavPulseMode.ZeroCrossing, 1, referenceBlock?.Inverted ?? false, referenceBlock, token).Block;
            first ??= block; outliers += block.Outliers;
            for (int i = 0; i < counts.Length; i++)
                foreach (var bin in block.Distributions[i].Histogram)
                    counts[i].Add(bin.DurationMicroseconds * source.SampleRate / 1e6, bin.Count);
        }
        return first! with { StartSample = spans[0].Start, EndSample = spans[^1].End, Outliers = outliers,
            PulseMeasurement = $"{spans.Length} selected interval(s), overlap counted once • " + first!.PulseMeasurement,
            Distributions = counts.Select((c, i) => c.Finish(AudioPulseReference.Groups[i], source.SampleRate, first.Distributions[i].Reference)).ToArray() };
    }

    internal static AudioRangePreview Analyze(AudioSignalAnalysis source, long start, long end, AudioPcmTransform transform,
        int channel, WavPulseMode detector, double thresholdScale, bool inverted, AudioBlockAnalysis? referenceBlock,
        CancellationToken token = default, bool waveformOnly = false)
    {
        if (!double.IsFinite(thresholdScale) || thresholdScale < .1 || thresholdScale > 4) throw new ArgumentException("Measurement Schmitt scale must be 0.1–4.");
        VerifySource(source);
        using var reader = AudioSignalAnalysisService.OpenReader(source.Source);
        (start, end) = transform.Bounds(reader.Format, start, end);
        int count = transform.Mix ? 1 : reader.Format.Channels;
        if (channel < 0 || channel >= count) throw new ArgumentException("Select an available histogram channel.");
        var counters = new[] { new AudioDurationCounter(), new AudioDurationCounter(), new AudioDurationCounter(), new AudioDurationCounter() };
        double[] references = referenceBlock?.Distributions.Select(d => d.Reference ?? double.NaN).ToArray() ??
            [SharpZ80TimingTable.Rom800Normal.ShortHighMicroseconds, SharpZ80TimingTable.Rom800Normal.ShortLowMicroseconds,
             SharpZ80TimingTable.Rom800Normal.LongHighMicroseconds, SharpZ80TimingTable.Rom800Normal.LongLowMicroseconds];
        if (references.Length != 4 || references.Any(v => !double.IsFinite(v) || v <= 0)) throw new ArgumentException("The selected classification reference has no four finite pulse durations.");
        double scale = referenceBlock?.ClassificationScale ?? 1;
        var envelope = new WaveformEnvelopeBuilder(start, end, 65536);
        int positiveFullScale = 8388608 - (1 << (24 - reader.Format.BitsPerSample));
        var findings = new AudioIssueSpans(4096);
        int windowCount = 0; long windowStart = start, previousStart = start, outliers = 0; double ratioSum = 0; double? previousMean = null;
        Action<long, int, int> frames = (sample, left, right) =>
        {
            envelope.Add(sample, left, right);
            int value = channel == 0 ? left : right;
            if (value >= positiveFullScale || value <= -8388608) findings.Add("AUDIO_CLIPPING", "PCM reaches full scale.", sample, sample + 1, channel + 1);
        };
        Action<int, WavPulseMode, bool, double, long> pulses = (ch, mode, high, duration, pulseEnd) =>
        {
            if (ch != channel || mode != detector || pulseEnd - duration < start || pulseEnd > end || duration <= 0) return;
            bool logicalHigh = high == inverted;
            int shortIndex = logicalHigh ? 0 : 1;
            double us = duration * 1e6 / reader.Format.SampleRate;
            bool isLong = Math.Abs(us - references[shortIndex + 2] * scale) < Math.Abs(us - references[shortIndex] * scale);
            int group = shortIndex + (isLong ? 2 : 0); counters[group].Add(duration);
            double ratio = us / references[group];
            if (Math.Abs(ratio / scale - 1) > .35)
            { outliers++; findings.Add("AUDIO_PULSE_OUTLIERS", "Measured pulse differs by more than 35% from the scaled classification reference; this does not prove a damaged byte.", (long)Math.Floor(pulseEnd - duration), pulseEnd, channel + 1); }
            if (windowCount == 0) windowStart = (long)Math.Floor(pulseEnd - duration);
            ratioSum += ratio; windowCount++;
            if (windowCount == 256)
            {
                double mean = ratioSum / windowCount;
                if (previousMean is > 0 && Math.Abs(mean / previousMean.Value - 1) > .1)
                    findings.Add("AUDIO_TIMING_DISCONTINUITY", "Adjacent 256-pulse windows differ by more than 10%.", previousStart, pulseEnd, channel + 1);
                previousMean = mean; previousStart = windowStart; ratioSum = 0; windowCount = 0;
            }
        };
        if (waveformOnly) transform.Visit(reader, start, end, frames, token);
        else WavHeuristicAnalyzer.VisitRangeSignal(reader, start, end, transform, thresholdScale, frames, pulses, token, measureAdaptive: detector == WavPulseMode.AdaptiveZeroCrossing);
        token.ThrowIfCancellationRequested(); VerifySource(source);
        string measurement = $"{(transform.Mix ? "Mix" : reader.Format.Channels == 1 ? "Mono" : channel == 0 ? "Left" : "Right")} • {detector}" +
            (detector != WavPulseMode.ZeroCrossing ? $" ×{thresholdScale:G}" : "") + $" • {(inverted ? "inverted" : "normal")} classification • {transform.Description}";
        var block = new AudioBlockAnalysis(1, "Selected audio", "Range", referenceBlock?.Profile ?? "NORMAL 1:1", referenceBlock?.ReferenceName ?? SharpZ80TimingTable.Rom800Normal.Name,
            channel + 1, inverted, detector.ToString(), measurement, start, end, false, 0, 0, false,
            counters.Select((c, i) => c.Finish(AudioPulseReference.Groups[i], reader.Format.SampleRate, references[i])).ToArray(), [], 0, [],
            new(0, 0, 0, null, null), null, null, outliers) { ClassificationScale = scale };
        var analysis = new AudioSignalAnalysis { Source = source.Source, SourceLength = source.SourceLength, SourceModifiedUtc = source.SourceModifiedUtc,
            Format = source.Format, SampleRate = source.SampleRate, BitsPerSample = source.BitsPerSample, ChannelCount = (ushort)count,
            FrameCount = source.FrameCount, Blocks = [block], Issues = findings.Items,
            Regions = [new AudioSignalRegion("Unassigned audio", start, end, null, null, "Selected PCM interval; no program association inferred.") { SampleRate = source.SampleRate }],
            Waveform = new(envelope.Finish(), source.FrameCount, (ushort)count) };
        return new(analysis, block, start, end, measurement, findings.Omitted);
    }
    internal static void VerifySource(AudioSignalAnalysis source)
    {
        var file = new FileInfo(source.Source);
        if (file.Length != source.SourceLength || file.LastWriteTimeUtc != source.SourceModifiedUtc) throw new IOException("The source changed since analysis. Analyze it again.");
    }
}

internal sealed class AudioIssueSpans(int maximum)
{
    private readonly List<AudioAnalysisIssue> items = [];
    private readonly Dictionary<(string Code, int Channel), int> previous = [];
    internal IReadOnlyList<AudioAnalysisIssue> Items => items;
    internal int Omitted { get; private set; }
    internal void Add(string code, string description, long start, long end, int channel)
    {
        var key = (code, channel);
        if (previous.TryGetValue(key, out int index) && items[index].EndSample >= start)
        { items[index] = items[index] with { EndSample = Math.Max(items[index].EndSample, end) }; return; }
        if (items.Count >= maximum) { Omitted++; return; }
        previous[key] = items.Count; items.Add(new(code, DskIssueSeverity.Warning, description, start, end, Channel: channel));
    }
}
