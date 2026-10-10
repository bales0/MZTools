using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace MZTools;

internal sealed record AudioAnalysisIssue(string Code, DskIssueSeverity Severity, string Description,
    long StartSample, long EndSample, int? Record = null, int? Channel = null, string? Block = null);
internal sealed record AudioSignalRegion(string Kind, long StartSample, long EndSample, int? Record, int? Channel, string Evidence)
{
    public uint SampleRate { get; init; }
    public double? StartSeconds => SampleRate > 0 ? StartSample / (double)SampleRate : null;
    public double? EndSeconds => SampleRate > 0 ? EndSample / (double)SampleRate : null;
}
internal sealed record AudioChannelLevel(int Channel, string Name, long Samples, double Peak, double Rms, double DcOffset,
    double PositivePeak, double NegativePeak, long ClippingCount, double ClippingPercent, double Asymmetry,
    double? SignalRms, double? EstimatedNoiseRms, double? EstimatedSeparationDb,
    int ValidRecords, int ValidChecksumCandidates, int ChecksumCandidates, double? TimingErrorPercent)
{
    public double? LeaderStabilityPercent { get; init; }
}
internal sealed record AudioHistogramBin(double DurationMicroseconds, long Count);
internal readonly record struct AudioHistogramRange(double Min, double Max)
{
    internal AudioHistogramRange Constrain(double min, double max, double minimumWidth)
    {
        double span = Max - Min;
        double width = Math.Clamp(max - min, Math.Min(minimumWidth, span), span);
        // Max - (Max - Min) can round below Min. Return the original bounds
        // for the full span and never pass reversed limits to Math.Clamp.
        if (width >= span) return this;
        double start = Math.Clamp(min, Min, Math.Max(Min, Max - width));
        return new(start, Math.Min(Max, start + width));
    }

    internal static AudioHistogramRange Create(AudioPulseDistribution distribution, uint sampleRate)
    {
        var values = distribution.Histogram.Where(b => b.Count > 0).Select(b => b.DurationMicroseconds)
            .Concat(distribution.Reference is double reference ? new[] { reference } : Array.Empty<double>())
            .Where(v => double.IsFinite(v) && v >= 0).ToArray();
        if (values.Length == 0) return new(0, 1);
        double min = values.Min(), max = values.Max();
        double pad = max > min ? (max - min) * .06 : Math.Max(sampleRate > 0 ? 1_000_000.0 / sampleRate : 1, max * .01);
        return new(Math.Max(0, min - pad), max + pad);
    }
}

internal static class AudioRegionCoverage
{
    internal static IReadOnlyList<AudioSignalRegion> Unassigned(long frames, IEnumerable<(long Start, long End)> spans)
    {
        var result = new List<AudioSignalRegion>();
        long cursor = 0;
        void Add(long end) => result.Add(new("Unassigned audio", cursor, end, null, null,
            "Not assigned to a selected decoded block. May contain repeated copies, undecodable data, music or silence; this is not a confirmed silent gap."));
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            long start = Math.Clamp(span.Start, 0, frames), end = Math.Clamp(span.End, 0, frames);
            if (end <= start) continue;
            if (start > cursor) Add(start);
            cursor = Math.Max(cursor, end);
        }
        if (cursor < frames) Add(frames);
        return result;
    }
}
internal sealed record AudioPulseDistribution(string Group, long Count, double? Mean, double? Median, double? StdDev,
    double? Min, double? Max, double? P5, double? P95, double? Reference, double? DeviationPercent,
    IReadOnlyList<AudioHistogramBin> Histogram);
internal sealed record AudioPulsePoint(long Sample, bool Long, double HighMicroseconds, double LowMicroseconds, bool Outlier);
internal sealed record AudioDriftWindow(long StartSample, long EndSample, int Pulses, double TimingScale,
    double? ShortMean, double? LongMean, double RelativeTimingErrorPercent, double LocalStdDevPercent);
internal sealed record AudioDetectorAgreement(long ZeroCrossPulses, long SchmittPulses, long MatchedPulses,
    double? PulseAgreementPercent, double? ClassificationAgreementPercent);
internal sealed record AudioBlockAnalysis(int Record, string Name, string Block, string Profile, string ReferenceName,
    int Channel, bool Inverted, string DecodeDetector, string PulseMeasurement, long StartSample, long EndSample,
    bool ChecksumValid, ushort RecordedChecksum, ushort CalculatedChecksum, bool Recovered,
    IReadOnlyList<AudioPulseDistribution> Distributions, IReadOnlyList<AudioPulsePoint> Scatter,
    long ScatterPairs, IReadOnlyList<AudioDriftWindow> Drift, AudioDetectorAgreement Agreement,
    double? GlobalTimingScale, double? SpeedOffsetPercent, long Outliers)
{
    public double ClassificationScale { get; init; } = 1;
}
internal sealed record AudioDecodeAgreement(int Channel, int ZeroCrossValidBlocks, int SchmittValidBlocks,
    int CommonValidBlocks, double? AgreementPercent, string Definition)
{
    public int ZeroCrossValidRecords { get; init; }
    public int SchmittValidRecords { get; init; }
    public double? RecordAgreementPercent { get; init; }
}

internal sealed class AudioSignalAnalysis
{
    public string Source { get; init; } = "";
    public long SourceLength { get; init; }
    public DateTime SourceModifiedUtc { get; init; }
    public string Format { get; init; } = "";
    public string Layer => "Captured / measured PCM";
    public uint SampleRate { get; init; }
    public ushort BitsPerSample { get; init; }
    public ushort ChannelCount { get; init; }
    public long FrameCount { get; init; }
    public double DurationSeconds => SampleRate == 0 ? 0 : FrameCount / (double)SampleRate;
    public string Quality { get; init; } = "";
    public string QualityExplanation { get; init; } = "";
    public string BestChannel { get; init; } = "";
    public string BestChannelReasons { get; init; } = "";
    public double? StereoRmsDifferenceDb { get; init; }
    public string Limits => "PCM levels use full-scale units. Pulse durations use the existing DC/high-pass preprocessor and ZeroCrossing/Schmitt detectors; the waveform is raw PCM. High/Low timing labels follow selected decoder polarity and SharpZ80TimingTable conventions. Timing scale > 1 means longer/slower pulses; speed offset = (1/scale - 1) × 100. Noise is unassigned audio, so separation is Estimated, not laboratory SNR. No automatic filtering or normalization is applied. Quality thresholds: clipping > 0.1%, RMS < 0.03, |DC| > 0.05, pulse outliers > 1% at > 35% residual, drift > 3%, window discontinuity > 10%, detector agreement < 95%. Poor means decode failures or clipping > 2%; other warnings mean Marginal. Program execution is not tested.";
    public IReadOnlyList<AudioChannelLevel> Channels { get; init; } = [];
    public IReadOnlyList<AudioBlockAnalysis> Blocks { get; init; } = [];
    public IReadOnlyList<AudioDecodeAgreement> DecodeAgreement { get; init; } = [];
    public IReadOnlyList<AudioSignalRegion> Regions { get; init; } = [];
    public IReadOnlyList<AudioAnalysisIssue> Issues { get; init; } = [];
    public AudioPulseReference? ComparisonReference { get; set; }
    public bool ShowComparisonReference { get; set; }
    public IReadOnlyList<string> VisiblePulseGroups { get; set; } = AudioPulseReference.Groups;
    [JsonIgnore] public WaveformRenderModel Waveform { get; init; } = new([], 0, 1);
}

internal readonly record struct WaveformBucket(long StartSample, long EndSample, float LeftMin, float LeftMax, float RightMin, float RightMax);
internal sealed record WaveformRenderModel(IReadOnlyList<WaveformBucket> Buckets, long FrameCount, ushort Channels)
{
    private IReadOnlyList<IReadOnlyList<WaveformBucket>>? levels;
    // Each coarser level retains extrema, including isolated spikes, on both channels.
    internal IReadOnlyList<WaveformBucket> VisibleBuckets(long start, long end, int maximumBuckets)
    {
        if (maximumBuckets < 1) throw new ArgumentOutOfRangeException(nameof(maximumBuckets));
        if (end <= start || Buckets.Count == 0) return Array.Empty<WaveformBucket>();
        if (levels == null)
        {
            var pyramid = new List<IReadOnlyList<WaveformBucket>> { Buckets };
            while (pyramid[^1].Count > 1)
            {
                var previous = pyramid[^1]; var next = new List<WaveformBucket>((previous.Count + 1) / 2);
                for (int i = 0; i < previous.Count; i += 2)
                {
                    var a = previous[i];
                    if (i + 1 == previous.Count) { next.Add(a); continue; }
                    var b = previous[i + 1];
                    next.Add(new(a.StartSample, b.EndSample, Math.Min(a.LeftMin, b.LeftMin), Math.Max(a.LeftMax, b.LeftMax), Math.Min(a.RightMin, b.RightMin), Math.Max(a.RightMax, b.RightMax)));
                }
                pyramid.Add(next);
            }
            levels = pyramid;
        }
        int level = 0; double targetWidth = (end - start) / (double)maximumBuckets;
        while (level + 1 < levels.Count && (levels[level][^1].EndSample - levels[level][0].StartSample) / (double)levels[level].Count < targetWidth) level++;
        var selected = levels[level];
        int lo = 0, hi = selected.Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (selected[mid].EndSample <= start) lo = mid + 1; else hi = mid; }
        var visible = new List<WaveformBucket>();
        for (int i = lo; i < selected.Count && selected[i].StartSample < end; i++) visible.Add(selected[i]);
        return visible;
    }
    internal static WaveformRenderModel ReadRange(string path, long start, long end, int maximumBuckets, CancellationToken token)
    {
        using var reader = AudioSignalAnalysisService.OpenReader(path);
        start = Math.Clamp(start, 0, reader.Format.FrameCount);
        end = Math.Clamp(end, start, reader.Format.FrameCount);
        var builder = new WaveformEnvelopeBuilder(start, end, maximumBuckets);
        reader.ReadFrames(start, end - start, (sample, left, right) => { token.ThrowIfCancellationRequested(); builder.Add(sample, left, right); });
        return new(builder.Finish(), reader.Format.FrameCount, reader.Format.Channels);
    }
}

internal sealed class WaveformEnvelopeBuilder
{
    private readonly long start, end, stride;
    private readonly List<WaveformBucket> buckets = [];
    private long bucketStart = -1, bucketEnd;
    private float lmin, lmax, rmin, rmax;
    internal WaveformEnvelopeBuilder(long start, long end, int maximumBuckets)
    {
        if (start < 0 || end < start || maximumBuckets < 1) throw new ArgumentOutOfRangeException(nameof(start));
        this.start = start; this.end = end;
        stride = Math.Max(1, (end - start + maximumBuckets - 1) / maximumBuckets);
    }
    internal void Add(long sample, int left, int right)
    {
        if (sample < start || sample >= end) throw new ArgumentOutOfRangeException(nameof(sample));
        long next = start + (sample - start) / stride * stride;
        float l = left / 8388608f, r = right / 8388608f;
        if (bucketStart != next)
        {
            Flush(); bucketStart = next; bucketEnd = sample + 1; lmin = lmax = l; rmin = rmax = r;
        }
        else { bucketEnd = sample + 1; lmin = Math.Min(lmin, l); lmax = Math.Max(lmax, l); rmin = Math.Min(rmin, r); rmax = Math.Max(rmax, r); }
    }
    private void Flush() { if (bucketStart >= 0) { buckets.Add(new(bucketStart, bucketEnd, lmin, lmax, rmin, rmax)); bucketStart = -1; } }
    internal IReadOnlyList<WaveformBucket> Finish() { Flush(); return buckets; }
}

internal sealed class AudioDurationCounter
{
    private readonly SortedDictionary<double, long> counts = [];
    public long Count { get; private set; }
    private double mean, m2;
    internal void Add(double samples, long repetitions = 1)
    {
        if (samples <= 0 || repetitions <= 0) return;
        counts[samples] = counts.GetValueOrDefault(samples) + repetitions; Count += repetitions;
        double delta = samples - mean; mean += delta * repetitions / Count; m2 += repetitions * delta * (samples - mean);
    }
    internal AudioPulseDistribution Finish(string group, uint sampleRate, double? reference)
    {
        if (Count == 0) return new(group, 0, null, null, null, null, null, null, null, reference, null, []);
        double unit = 1_000_000.0 / sampleRate;
        double At(long rank) { long total = 0; foreach (var pair in counts) { total += pair.Value; if (rank < total) return pair.Key; } return counts.Last().Key; }
        double Quantile(double percentile) { double rank = percentile * (Count - 1); long lo = (long)Math.Floor(rank); return (At(lo) + (At((long)Math.Ceiling(rank)) - At(lo)) * (rank - lo)) * unit; }
        double value = mean * unit;
        return new(group, Count, value, Quantile(.5), Math.Sqrt(m2 / Count) * unit, counts.First().Key * unit,
            counts.Last().Key * unit, Quantile(.05), Quantile(.95), reference,
            reference is > 0 ? (value / reference.Value - 1) * 100 : null,
            counts.Select(pair => new AudioHistogramBin(pair.Key * unit, pair.Value)).ToArray());
    }
}

internal static class AudioAnalysisReport
{
    internal static string Export(AudioSignalAnalysis analysis, string format)
    {
        if (format == "json") return JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        var rows = new List<string[]>();
        void Row(string section, string scope, string metric, object? value, string unit = "", long? start = null, long? end = null) =>
            rows.Add([section, scope, metric, value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "Not available", unit, start?.ToString() ?? "", end?.ToString() ?? ""]);
        Row("source", "", "source", analysis.Source); Row("source", "", "layer", analysis.Layer); Row("source", "", "format", analysis.Format);
        Row("source", "", "sample rate", analysis.SampleRate, "Hz"); Row("source", "", "channels", analysis.ChannelCount);
        Row("source", "", "bit depth", analysis.BitsPerSample, "bits"); Row("source", "", "frames", analysis.FrameCount);
        Row("source", "", "file length", analysis.SourceLength, "B"); Row("source", "", "modified UTC", analysis.SourceModifiedUtc.ToString("O", CultureInfo.InvariantCulture));
        Row("source", "", "duration", analysis.DurationSeconds, "s"); Row("source", "", "quality", analysis.Quality);
        Row("source", "", "quality reasons", analysis.QualityExplanation); Row("source", "", "best channel", analysis.BestChannel);
        Row("source", "", "best channel reasons", analysis.BestChannelReasons); Row("source", "", "L/R RMS difference", analysis.StereoRmsDifferenceDb, "dB");
        Row("source", "", "limits / definitions", analysis.Limits);
        Row("comparison", "", "reference grid visible", analysis.ShowComparisonReference);
        Row("comparison", "", "visible pulse groups", string.Join(", ", analysis.VisiblePulseGroups));
        if (analysis.ComparisonReference is { } comparison)
        {
            Row("comparison", comparison.Name, "source", comparison.Source); Row("comparison", comparison.Name, "evidence", comparison.Evidence);
            foreach (var guide in comparison.Guides) Row("comparison", comparison.Name, guide.Group + " / " + guide.Context, guide.Microseconds, "µs");
        }
        foreach (var channel in analysis.Channels)
        {
            foreach (var (name, value, unit) in new (string, object?, string)[] {
                ("Peak",channel.Peak,"FS"),("RMS",channel.Rms,"FS"),("DC",channel.DcOffset,"FS"),
                ("Positive peak",channel.PositivePeak,"FS"),("Negative peak",channel.NegativePeak,"FS"),
                ("Clipping count",channel.ClippingCount,"samples"),("Clipping",channel.ClippingPercent,"%"),("Asymmetry",channel.Asymmetry,"FS"),
                ("Signal RMS",channel.SignalRms,"FS"),("Estimated noise RMS",channel.EstimatedNoiseRms,"FS"),("Estimated separation",channel.EstimatedSeparationDb,"dB"),
                ("Valid records",channel.ValidRecords,""),("Checksum valid candidates",channel.ValidChecksumCandidates,""),("Checksum candidates",channel.ChecksumCandidates,""),("Timing error",channel.TimingErrorPercent,"%"),("Leader variation",channel.LeaderStabilityPercent,"%") }) Row("channel",channel.Name,name,value,unit);
        }
        foreach (var block in analysis.Blocks)
        {
            string scope = $"{block.Record}: {block.Name} / {block.Block}";
            Row("block",scope,"profile",block.Profile); Row("block",scope,"reference",block.ReferenceName);
            Row("block",scope,"selected channel",block.Channel); Row("block",scope,"inverted",block.Inverted);
            Row("block",scope,"decode detector",block.DecodeDetector); Row("block",scope,"pulse measurement",block.PulseMeasurement);
            Row("block",scope,"checksum valid",block.ChecksumValid); Row("block",scope,"recorded checksum",block.RecordedChecksum);
            Row("block",scope,"calculated checksum",block.CalculatedChecksum); Row("block",scope,"recovered",block.Recovered);
            Row("block",scope,"global timing scale",block.GlobalTimingScale,"",block.StartSample,block.EndSample);
            Row("block",scope,"speed offset",block.SpeedOffsetPercent,"%"); Row("block",scope,"outliers",block.Outliers);
            Row("agreement",scope,"pulse agreement",block.Agreement.PulseAgreementPercent,"%"); Row("agreement",scope,"classification agreement",block.Agreement.ClassificationAgreementPercent,"%");
            foreach (var distribution in block.Distributions)
            {
                foreach (var (name,value) in new (string,object?)[] { ("Count",distribution.Count),("Mean",distribution.Mean),("Median",distribution.Median),("StdDev",distribution.StdDev),("Min",distribution.Min),("Max",distribution.Max),("P5",distribution.P5),("P95",distribution.P95),("Reference",distribution.Reference),("Deviation %",distribution.DeviationPercent) })
                    Row("pulse statistics",scope+" / "+distribution.Group,name,value,name=="Count"?"":name=="Deviation %"?"%":"µs");
                foreach (var bin in distribution.Histogram) Row("histogram",scope+" / "+distribution.Group,bin.DurationMicroseconds.ToString(CultureInfo.InvariantCulture),bin.Count,"µs / count");
            }
            foreach (var window in block.Drift)
            {
                Row("drift",scope,"scale",window.TimingScale,"",window.StartSample,window.EndSample);
                Row("drift",scope,"SHORT mean",window.ShortMean,"µs",window.StartSample,window.EndSample);
                Row("drift",scope,"LONG mean",window.LongMean,"µs",window.StartSample,window.EndSample);
                Row("drift",scope,"relative timing error",window.RelativeTimingErrorPercent,"%",window.StartSample,window.EndSample);
                Row("drift",scope,"local stddev",window.LocalStdDevPercent,"%",window.StartSample,window.EndSample);
            }
        }
        foreach (var agreement in analysis.DecodeAgreement)
        {
            Row("candidate agreement",$"Channel {agreement.Channel}",agreement.Definition,agreement.AgreementPercent,"%");
            Row("decode agreement",$"Channel {agreement.Channel}","ZeroCrossing valid records",agreement.ZeroCrossValidRecords);
            Row("decode agreement",$"Channel {agreement.Channel}","Schmitt valid records",agreement.SchmittValidRecords);
            Row("decode agreement",$"Channel {agreement.Channel}","Record content agreement",agreement.RecordAgreementPercent,"%");
        }
        foreach (var region in analysis.Regions) Row("region",$"Record {region.Record} / channel {region.Channel}",region.Kind,region.Evidence,"",region.StartSample,region.EndSample);
        foreach (var issue in analysis.Issues) Row("issue",$"Record {issue.Record} / channel {issue.Channel} / {issue.Block}",issue.Code,$"{issue.Severity}: {issue.Description}","",issue.StartSample,issue.EndSample);
        if (format == "csv") return "section,scope,metric,value,unit,startSample,endSample\r\n" + string.Join("\r\n", rows.Select(row => string.Join(',',row.Select(value=>'"'+value.Replace("\"","\"\"")+'"'))));
        return "Visual Media Analysis — " + analysis.Layer + "\n" + string.Join('\n',rows.Select(row=>$"{row[0]} | {row[1]} | {row[2]}: {row[3]} {row[4]}"+(row[5].Length>0?$" | samples [{row[5]}, {row[6]})":"")));
    }
}
