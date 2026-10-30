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
        AudioBlockAnalysis? referenceBlock, CancellationToken token = default, WavPulseMode detector = WavPulseMode.ZeroCrossing,
        double thresholdScale = 1, long? preprocessingStart = null, double zeroDeadband = 0, AudioPcmTransform? transform = null)
    {
        var spans = MergeRanges(ranges);
        if (spans.Length == 0) throw new ArgumentException("Select a nonempty audio interval.");
        var counts = AudioPulseReference.Groups.Select(_ => new AudioDurationCounter()).ToArray();
        AudioBlockAnalysis? first = null;
        long outliers = 0, pairs = 0, measuredCount = 0, scatterStride = 1, seenScatter = 0;
        double ratioSum = 0;
        int omitted = 0;
        var scatter = new System.Collections.Generic.List<AudioPulsePoint>();
        var drift = new System.Collections.Generic.List<AudioDriftWindow>();
        foreach (var span in spans)
        {
            token.ThrowIfCancellationRequested();
            var block = Analyze(source, span.Start, span.End, transform ?? new(Mix: mix), channel,
                detector, thresholdScale, referenceBlock?.Inverted ?? false, referenceBlock, token,
                preprocessingStart: preprocessingStart, zeroDeadband: zeroDeadband).Block;
            first ??= block; outliers += block.Outliers;
            long count = block.Distributions.Sum(d => d.Count);
            if (block.GlobalTimingScale is double timing) { ratioSum += timing * count; measuredCount += count; }
            pairs += block.ScatterPairs; omitted += block.OmittedMeasurementSpans;
            foreach (var point in block.Scatter) {
                if (++seenScatter % scatterStride == 0) scatter.Add(point);
                if (scatter.Count > 20000) {
                    int kept = 0; for(int i=0;i<scatter.Count;i+=2) scatter[kept++] = scatter[i];
                    scatter.RemoveRange(kept,scatter.Count-kept); scatterStride *= 2;
                }
            }
            foreach (var window in block.Drift) { if (drift.Count < 4096) drift.Add(window); else omitted++; }
            for (int i = 0; i < counts.Length; i++)
                foreach (var bin in block.Distributions[i].Histogram)
                    counts[i].Add(bin.DurationMicroseconds * source.SampleRate / 1e6, bin.Count);
        }
        double? global = measuredCount > 0 ? ratioSum / measuredCount : null;
        return first! with { StartSample = spans[0].Start, EndSample = spans[^1].End, Outliers = outliers,
            Scatter = scatter.ToArray(), ScatterPairs = pairs, Drift = drift.ToArray(), GlobalTimingScale = global,
            SpeedOffsetPercent = global is > 0 ? (1/global.Value-1)*100 : null, OmittedMeasurementSpans = omitted,
            PulseSource = first!.PulseSource! with { StartSample = spans[0].Start, EndSample = spans[^1].End,
                Intervals = spans.Select(s => new AudioMeasurementInterval(s.Start,s.End)).ToArray() },
            PulseMeasurement = $"{spans.Length} selected interval(s), overlap counted once • " + first!.PulseMeasurement,
            Distributions = counts.Select((c, i) => c.Finish(AudioPulseReference.Groups[i], source.SampleRate, first.Distributions[i].Reference)).ToArray() };
    }

    internal static AudioRangePreview Analyze(AudioSignalAnalysis source, long start, long end, AudioPcmTransform transform,
        int channel, WavPulseMode detector, double thresholdScale, bool inverted, AudioBlockAnalysis? referenceBlock,
        CancellationToken token = default, bool waveformOnly = false, long? preprocessingStart = null, double zeroDeadband = 0)
    {
        if (!double.IsFinite(thresholdScale) || thresholdScale < .1 || thresholdScale > 4) throw new ArgumentException("Measurement Schmitt scale must be 0.1–4.");
        VerifySource(source);
        using var reader = AudioSignalAnalysisService.OpenReader(source.Source);
        (start, end) = transform.Bounds(reader.Format, start, end);
        preprocessingStart = Math.Min(preprocessingStart ?? start, start);
        int count = transform.Mix ? 1 : reader.Format.Channels;
        if (channel < 0 || channel >= count) throw new ArgumentException("Select an available histogram channel.");
        double[] references = referenceBlock?.Distributions.Select(d => d.Reference ?? double.NaN).ToArray() ??
            [SharpZ80TimingTable.Rom800Normal.ShortHighMicroseconds, SharpZ80TimingTable.Rom800Normal.ShortLowMicroseconds,
             SharpZ80TimingTable.Rom800Normal.LongHighMicroseconds, SharpZ80TimingTable.Rom800Normal.LongLowMicroseconds];
        if (references.Length != 4 || references.Any(v => !double.IsFinite(v) || v <= 0)) throw new ArgumentException("The selected classification reference has no four finite pulse durations.");
        double scale = referenceBlock?.ClassificationScale ?? 1;
        var envelope = new WaveformEnvelopeBuilder(start, end, 65536);
        int positiveFullScale = 8388608 - (1 << (24 - reader.Format.BitsPerSample));
        var findings = new AudioIssueSpans(4096);
        var engine = new AudioPulseMeasurementEngine(new(source.Source, source.SampleRate, start, end, channel+1, detector.ToString(), inverted,
            thresholdScale, zeroDeadband*100, referenceBlock?.PulseSource?.ReferenceId ?? "rom800", scale, "Selected interval / comparison", "Visual range")
            { PreprocessingStartSample = preprocessingStart ?? start }, references.Select(r => new[] {r}).ToArray());
        Action<long, int, int> frames = (sample, left, right) =>
        {
            if (sample < start) return;
            envelope.Add(sample, left, right);
            int value = channel == 0 ? left : right;
            if (value >= positiveFullScale || value <= -8388608) findings.Add("AUDIO_CLIPPING", "PCM reaches full scale.", sample, sample + 1, channel + 1);
        };
        Action<int, WavPulseMode, bool, double, long> pulses = (ch, mode, high, duration, pulseEnd) =>
        {
            if (ch != channel || mode != detector || pulseEnd - duration < start || pulseEnd > end || duration <= 0) return;
            engine.Add(high, duration, pulseEnd);
        };
        if (waveformOnly) transform.Visit(reader, start, end, frames, token);
        else WavHeuristicAnalyzer.VisitRangeSignal(reader, preprocessingStart ?? start, end, transform, thresholdScale, frames, pulses, token,
            measureAdaptive: detector == WavPulseMode.AdaptiveZeroCrossing, zeroDeadband: zeroDeadband);
        token.ThrowIfCancellationRequested(); VerifySource(source);
        string measurement = $"{(transform.Mix ? "Mix" : reader.Format.Channels == 1 ? "Mono" : channel == 0 ? "Left" : "Right")} • {detector}" +
            (detector != WavPulseMode.ZeroCrossing ? $" ×{thresholdScale:G}" : "") + $" • {(inverted ? "inverted" : "normal")} classification • {transform.Description}";
        var metrics = engine.Finish();
        var block = new AudioBlockAnalysis(1, "Selected audio", "Range", referenceBlock?.Profile ?? "NORMAL 1:1", referenceBlock?.ReferenceName ?? SharpZ80TimingTable.Rom800Normal.Name,
            channel + 1, inverted, detector.ToString(), measurement, start, end, false, 0, 0, false,
            metrics.Distributions, metrics.Scatter, metrics.ScatterPairs, metrics.Drift,
            new(0, 0, 0, null, null), metrics.GlobalScale, metrics.GlobalScale is > 0 ? (1 / metrics.GlobalScale.Value - 1) * 100 : null, metrics.Outliers) { ClassificationScale = scale, PulseSource = engine.Source, OmittedMeasurementSpans = metrics.Omitted };
        var analysis = new AudioSignalAnalysis { Source = source.Source, SourceLength = source.SourceLength, SourceModifiedUtc = source.SourceModifiedUtc,
            Format = source.Format, SampleRate = source.SampleRate, BitsPerSample = source.BitsPerSample, ChannelCount = (ushort)count,
            FrameCount = source.FrameCount, Blocks = [block], Issues = findings.Items.Concat(metrics.Issues).ToArray(),
            Regions = [new AudioSignalRegion("Unassigned audio", start, end, null, null, "Selected PCM interval; no program association inferred.") { SampleRate = source.SampleRate }],
            Waveform = new(envelope.Finish(), source.FrameCount, (ushort)count) };
        return new(analysis, block, start, end, measurement, findings.Omitted + metrics.Omitted);
    }
    internal static void VerifySource(AudioSignalAnalysis source)
    {
        var file = new FileInfo(source.Source);
        if (file.Length != source.SourceLength || file.LastWriteTimeUtc != source.SourceModifiedUtc) throw new IOException("The source changed since analysis. Analyze it again.");
        if(SegmentedPcmStreamReader.IsManifest(source.Source)) { using var verified=new SegmentedPcmStreamReader(source.Source); }
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
