using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace MZTools;

internal static class AudioSignalAnalysisService
{
    internal static IPcmAudioStreamReader OpenReader(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => new WavPcmStreamReader(path), ".flac" => new FlacPcmStreamReader(path),
        _ => throw new ArgumentException("Select a WAV or FLAC file.")
    };

    internal static AudioSignalAnalysis Analyze(string path, IProgress<WavAnalysisProgress>? progress = null, CancellationToken token = default)
    {
        var original = new FileInfo(path); long length = original.Length; var modified = original.LastWriteTimeUtc;
        var decoded = WavHeuristicAnalyzer.AnalyzeFile(path, progress, token, includeCandidates: true);
        using var reader = OpenReader(path);
        var f = reader.Format;
        var regions = new List<AudioSignalRegion>();
        var accumulators = new List<AudioBlockAccumulator>();
        for (int i = 0; i < decoded.Statistics.RecordRecoveries.Count; i++)
        {
            var recovery = decoded.Statistics.RecordRecoveries[i];
            string name = SharpMzEncoding.ConvertMzfNameToASCIIString(decoded.Records[i].Header.MzfFname);
            foreach (var candidate in new[] { recovery.Header.Candidate, recovery.Payload.Candidate })
            {
                var trace = candidate.SignalTrace;
                string block = candidate.Kind.ToString();
                if (trace is { IsOrdered: true })
                {
                    void Region(string kind, long start, long end) { if (end > start) regions.Add(new(kind, Math.Clamp(start, 0, f.FrameCount), Math.Clamp(end, 0, f.FrameCount), i + 1, candidate.Channel + 1, "Existing decoder transition; " + block)); }
                    Region(block + " leader", trace.LeaderStart, trace.SyncStart);
                    Region(block + " sync", trace.SyncStart, trace.DataStart);
                    Region(block, trace.DataStart, trace.ChecksumStart);
                    Region(block + " checksum", trace.ChecksumStart, trace.End);
                }
                else regions.Add(new(block + " (partial trace)", candidate.StartSample, candidate.EndSample, i + 1, candidate.Channel + 1, "Only decoder anchors available; leader/sync boundaries unknown."));
                if (recovery.ReconstructionUsed || recovery.SelectiveRecoveryUsed)
                    regions.Add(new("Recovered", trace?.DataStart ?? candidate.StartSample, candidate.EndSample, i + 1, candidate.Channel + 1, "Existing decoder reconstruction/selective recovery."));
                accumulators.Add(new(i + 1, name, candidate, recovery.FinalProfile, recovery.ReconstructionUsed || recovery.SelectiveRecoveryUsed, f.SampleRate));
            }
        }
        foreach (var failure in decoded.Failures) regions.Add(new("Unresolved", failure.StartSample, failure.EndSample, null, null, failure.Reason));
        var levels = Enumerable.Range(0, f.Channels).Select(_ => new AudioLevelAccumulator(f.BitsPerSample)).ToArray();
        var envelope = new WaveformEnvelopeBuilder(0, f.FrameCount, 65536);
        var quiet = new AudioQuietRegions(f.SampleRate);
        // Ordered intervals support a linear PCM pass, even for long captures.
        var spans = accumulators.Select(b => (b.Start, b.End)).OrderBy(b => b.Start).ToArray();
        regions.AddRange(AudioRegionCoverage.Unassigned(f.FrameCount, spans));
        int span = 0;
        WavHeuristicAnalyzer.VisitSignal(reader, (sample, left, right) =>
        {
            envelope.Add(sample, left, right);
            quiet.Add(sample, Math.Max(Math.Abs((long)left), f.Channels > 1 ? Math.Abs((long)right) : 0));
            while (span < spans.Length && spans[span].End <= sample) span++;
            bool signal = span < spans.Length && sample >= spans[span].Start;
            levels[0].Add(left, signal, sample); if (f.Channels > 1) levels[1].Add(right, signal, sample);
            if (sample % 32768 == 0) progress?.Report(new("Measuring PCM / pulses", f.FrameCount == 0 ? 1 : sample / (double)f.FrameCount, sample, f.FrameCount, accumulators.Count));
        }, (channel, mode, high, duration, end) =>
        {
            foreach (var block in accumulators)
                if (block.Channel == channel && end > block.Start && end <= block.End && end - duration >= block.Start)
                    block.Add(mode, high, duration, end);
        }, token);
        regions.AddRange(quiet.Finish(f.FrameCount));
        regions = regions.Select(r => r with { SampleRate = f.SampleRate }).OrderBy(r => r.StartSample).ToList();
        var blocks = accumulators.Select(b => b.Finish()).ToArray();
        var channels = levels.Select((l, c) =>
        {
            var candidates = decoded.Candidates.Where(b => b.Channel == c).ToArray();
            int records = WavHeuristicAnalyzer.AssembleCandidates(candidates, f.SampleRate, f.FrameCount).Records.Count;
            var errors = candidates.Where(b => double.IsFinite(b.TimingError)).Select(b => b.TimingError * 100).ToArray();
            var stability = candidates.Where(b => b.ChecksumValid && b.LeaderAverage > 0).Select(b => b.LeaderStdDev / b.LeaderAverage * 100).Where(double.IsFinite).ToArray();
            return l.Finish(c + 1, f.Channels == 1 ? "Mono" : c == 0 ? "Left" : "Right", records, candidates.Count(b => b.ChecksumValid), candidates.Length, errors.Length > 0 ? errors.Average() : null)
                with { LeaderStabilityPercent = stability.Length > 0 ? stability.Average() : null };
        }).ToArray();
        var agreements = Enumerable.Range(0, f.Channels).Select(c =>
        {
            HashSet<string> Keys(WavPulseMode mode) => decoded.Candidates.Where(b => b.Channel == c && b.PulseMode == mode && b.ChecksumValid)
                .Select(b => $"{b.Kind}:{Convert.ToHexString(SHA256.HashData(b.Data))}").ToHashSet();
            var z = Keys(WavPulseMode.ZeroCrossing); var s = Keys(WavPulseMode.Schmitt);
            int common = z.Intersect(s).Count(), union = z.Union(s).Count();
            IReadOnlyList<TapeRecord> Records(WavPulseMode mode) => WavHeuristicAnalyzer.AssembleCandidates(decoded.Candidates.Where(b => b.Channel == c && b.PulseMode == mode), f.SampleRate, f.FrameCount).Records;
            HashSet<string> RecordKeys(IReadOnlyList<TapeRecord> records) => records.Select(r => Convert.ToHexString(SHA256.HashData(r.GetSerializedHeader().Concat(r.Body.MzfBody).ToArray()))).ToHashSet();
            var zr = Records(WavPulseMode.ZeroCrossing); var sr = Records(WavPulseMode.Schmitt); var zk = RecordKeys(zr); var sk = RecordKeys(sr); int recordUnion = zk.Union(sk).Count();
            return new AudioDecodeAgreement(c + 1, z.Count, s.Count, common, union > 0 ? 100.0 * common / union : null,
                "Jaccard agreement of unique checksum-valid kind + byte-content candidates; repeated copies collapse. Includes selective-recovery candidates.")
                { ZeroCrossValidRecords = zr.Count, SchmittValidRecords = sr.Count, RecordAgreementPercent = recordUnion > 0 ? 100.0 * zk.Intersect(sk).Count() / recordUnion : null };
        }).ToArray();
        var issues = new List<AudioAnalysisIssue>();
        foreach (var ch in channels)
        {
            void Issue(string code, string message, long start = 0, long? end = null) => issues.Add(new(code, DskIssueSeverity.Warning, message, start, end ?? f.FrameCount, Channel: ch.Channel));
            if (ch.ClippingPercent > .1) Issue("AUDIO_CLIPPING", $"{ch.ClippingCount:N0} clipped samples ({ch.ClippingPercent:F3}%).", levels[ch.Channel - 1].FirstClip, levels[ch.Channel - 1].LastClip + 1);
            if (ch.Rms < .03) Issue("AUDIO_LOW_LEVEL", $"RMS {ch.Rms:F4} FS is below 0.03 FS.");
            if (Math.Abs(ch.DcOffset) > .05) Issue("AUDIO_DC_OFFSET", $"DC {ch.DcOffset:F4} FS exceeds ±0.05 FS.");
        }
        foreach (var b in blocks)
        {
            void Issue(string code, string message, long start, long end) => issues.Add(new(code, DskIssueSeverity.Warning, message, start, end, b.Record, b.Channel, b.Block));
            long count = b.Distributions.Sum(d => d.Count);
            if (count > 0 && b.Outliers > count * .01) Issue("AUDIO_PULSE_OUTLIERS", $"{b.Outliers:N0}/{count:N0} pulses differ by >35% from the scaled reference.", b.StartSample, b.EndSample);
            if (b.Drift.Count > 1 && b.Drift.Max(w => w.TimingScale) - b.Drift.Min(w => w.TimingScale) > .03 * (b.GlobalTimingScale ?? 1)) Issue("AUDIO_TIMING_DRIFT", "Timing scale varies by more than 3% across 256-pulse windows.", b.StartSample, b.EndSample);
            for (int j = 1; j < b.Drift.Count; j++) if (Math.Abs(b.Drift[j].TimingScale / b.Drift[j - 1].TimingScale - 1) > .1)
                Issue("AUDIO_TIMING_DISCONTINUITY", "Adjacent timing windows differ by more than 10%.", b.Drift[j - 1].StartSample, b.Drift[j].EndSample);
            if (b.Agreement.PulseAgreementPercent is < 95) Issue("AUDIO_THRESHOLD_SENSITIVE", "Zero-crossing and Schmitt pulses agree below 95%; inspect noise and threshold sensitivity.", b.StartSample, b.EndSample);
        }
        foreach (var a in agreements.Where(a => a.AgreementPercent is < 95 || a.RecordAgreementPercent is < 95)) issues.Add(new("AUDIO_THRESHOLD_SENSITIVE", DskIssueSeverity.Warning, a.Definition + $" Candidate agreement {a.AgreementPercent:F1}%; record agreement {a.RecordAgreementPercent:F1}%.", 0, f.FrameCount, Channel: a.Channel));
        foreach (var candidate in decoded.Candidates.Where(c => !c.ChecksumValid).GroupBy(c => (c.Channel, c.Kind, c.EndSample / Math.Max(1, f.SampleRate / 20))).Select(g => g.First()))
            issues.Add(new("AUDIO_CHECKSUM_FAILURE", DskIssueSeverity.Warning, $"{candidate.Kind} candidate checksum failed; another copy or detector may recover it.", candidate.SignalTrace?.DataStart ?? candidate.StartSample, candidate.EndSample, Channel: candidate.Channel + 1, Block: candidate.Kind.ToString()));
        foreach (var failure in decoded.Failures) issues.Add(new("AUDIO_UNRESOLVED_REGION", DskIssueSeverity.Error, failure.Reason, failure.StartSample, failure.EndSample));
        if (decoded.Records.Count == 0) issues.Add(new("AUDIO_NO_RECORDS", DskIssueSeverity.Error, "No complete SHARP tape program was recovered. Audio quality alone does not prove valid tape data.", 0, f.FrameCount));
        if (channels.Length == 2 && channels[0].ValidRecords != channels[1].ValidRecords) issues.Add(new("AUDIO_CHANNEL_MISMATCH", DskIssueSeverity.Warning, "The channels recover different numbers of complete records.", 0, f.FrameCount));
        var best = channels.OrderByDescending(c => c.ValidRecords).ThenByDescending(c => c.ChecksumCandidates == 0 ? 0 : c.ValidChecksumCandidates / (double)c.ChecksumCandidates)
            .ThenBy(c => c.ClippingPercent).ThenBy(c => c.TimingErrorPercent ?? double.MaxValue).ThenBy(c => c.LeaderStabilityPercent ?? double.MaxValue).ToArray();
        bool equal = best.Length == 2 && best[0].ValidRecords == best[1].ValidRecords && best[0].ValidChecksumCandidates == best[1].ValidChecksumCandidates && best[0].ChecksumCandidates == best[1].ChecksumCandidates && best[0].ClippingCount == best[1].ClippingCount && best[0].TimingErrorPercent == best[1].TimingErrorPercent && best[0].LeaderStabilityPercent == best[1].LeaderStabilityPercent;
        bool poor = issues.Any(i => i.Severity == DskIssueSeverity.Error) || channels.Any(c => c.ClippingPercent > 2);
        original.Refresh(); if (original.Length != length || original.LastWriteTimeUtc != modified) throw new IOException("The source changed during analysis. Analyze the file again.");
        return new()
        {
            Source = Path.GetFullPath(path), SourceLength = length, SourceModifiedUtc = modified, Format = reader.SourceFormat, SampleRate = f.SampleRate, BitsPerSample = f.BitsPerSample,
            ChannelCount = f.Channels, FrameCount = f.FrameCount, Channels = channels, Blocks = blocks, Regions = regions,
            DecodeAgreement = agreements, Issues = issues, Waveform = new(envelope.Finish(), f.FrameCount, f.Channels),
            Quality = poor ? "Poor" : issues.Count > 0 ? "Marginal" : "Good",
            QualityExplanation = $"Recovered {decoded.Records.Count} program(s); {issues.Count} issue(s). " + (issues.Count > 0 ? string.Join("; ", issues.Select(i => i.Description).Distinct().Take(3)) + (issues.Count > 3 ? " See Issues for all findings." : "") : "No configured threshold exceeded."),
            BestChannel = best[0].ValidRecords == 0 ? "Not assessable (no complete channel records)" : equal ? "Equivalent" : best[0].Name,
            BestChannelReasons = string.Join("; ", best.Select(c => $"{c.Name}: {c.ValidRecords} records, {c.ValidChecksumCandidates}/{c.ChecksumCandidates} valid candidate checksums, clipping {c.ClippingPercent:F3}%, timing error {c.TimingErrorPercent:F2}%, leader variation {c.LeaderStabilityPercent:F2}%, estimated noise RMS {c.EstimatedNoiseRms:F4} FS")),
            StereoRmsDifferenceDb = channels.Length == 2 && channels.All(c => c.Rms > 0) ? 20 * Math.Log10(channels[0].Rms / channels[1].Rms) : null
        };
    }
}

internal sealed class AudioQuietRegions(uint sampleRate)
{
    private readonly List<AudioSignalRegion> regions = [];
    private long windowStart, quietStart = -1, peak;
    internal void Add(long sample, long amplitude)
    {
        peak = Math.Max(peak, amplitude);
        if (sample + 1 - windowStart >= Math.Max(1, sampleRate / 10)) Flush(sample + 1);
    }
    private void Flush(long end)
    {
        if (peak / 8388608.0 < .005) { if (quietStart < 0) quietStart = windowStart; }
        else if (quietStart >= 0) { regions.Add(new("Gap (estimated quiet)", quietStart, windowStart, null, null, "Both channels peak below 0.005 FS in 100 ms windows; quiet audio, not proof of tape structure.")); quietStart = -1; }
        windowStart = end; peak = 0;
    }
    internal IReadOnlyList<AudioSignalRegion> Finish(long end)
    {
        if (windowStart < end) Flush(end);
        if (quietStart >= 0) { regions.Add(new("Gap (estimated quiet)", quietStart, end, null, null, "PCM peak below 0.005 FS; estimated quiet region.")); quietStart = -1; }
        return regions;
    }
}

internal sealed class AudioLevelAccumulator(ushort bits)
{
    private long count, clipped, signalCount, noiseCount;
    private double sum, squares, signalSquares, noiseSquares, positive, negative;
    internal long FirstClip { get; private set; } = -1;
    internal long LastClip { get; private set; }
    internal void Add(int value, bool signal, long sample)
    {
        double v = value / 8388608.0; count++; sum += v; squares += v * v;
        positive = Math.Max(positive, v); negative = Math.Min(negative, v);
        if (value <= -8388608 || value >= 8388608 - (1 << (24 - bits))) { clipped++; if (FirstClip < 0) FirstClip = sample; LastClip = sample; }
        if (signal) { signalCount++; signalSquares += v * v; } else { noiseCount++; noiseSquares += v * v; }
    }
    internal AudioChannelLevel Finish(int channel, string name, int records, int valid, int candidates, double? timing)
    {
        double? signal = signalCount > 0 ? Math.Sqrt(signalSquares / signalCount) : null;
        double? noise = noiseCount > 0 ? Math.Sqrt(noiseSquares / noiseCount) : null;
        return new(channel, name, count, Math.Max(positive, -negative), count > 0 ? Math.Sqrt(squares / count) : 0,
            count > 0 ? sum / count : 0, positive, negative, clipped, count > 0 ? 100.0 * clipped / count : 0, positive + negative,
            signal, noise, signal is > 0 && noise is > 0 ? 20 * Math.Log10(signal.Value / noise.Value) : null, records, valid, candidates, timing);
    }
}

internal sealed class AudioBlockAccumulator
{
    private readonly int record; private readonly string name; private readonly SharpBlockCandidate candidate;
    private readonly TapeProfile profile; private readonly bool recovered; private readonly uint rate;
    private readonly SharpZ80TimingReference reference;
    private readonly AudioDurationCounter[] counters = [new(), new(), new(), new()];
    private readonly List<AudioPulsePoint> scatter = []; private readonly List<AudioDriftWindow> drift = [];
    private readonly AudioPulseAgreementCounter agreement = new();
    private long pairs, outliers, pendingSample; private double? pendingHigh; private bool pendingLong, pendingOutlier;
    private int scatterStride = 1, windowCount; private long windowStart, windowEnd;
    private double ratioSum, ratioSquares, residualSquares, shortSum, longSum, totalRatio; private int shortCount, longCount; private long totalCount;
    internal int Channel => candidate.Channel;
    internal long Start => candidate.SignalTrace?.LeaderStart ?? candidate.StartSample;
    internal long End => candidate.EndSample;
    internal AudioBlockAccumulator(int record, string name, SharpBlockCandidate candidate, TapeProfile profile, bool recovered, uint rate)
    {
        this.record = record; this.name = name; this.candidate = candidate; this.profile = profile; this.recovered = recovered; this.rate = rate;
        reference = Reference(candidate.Kind == SharpBlockKind.Header ? candidate.Profile : profile, candidate.Kind);
    }
    internal static SharpZ80TimingReference Reference(TapeProfile p, SharpBlockKind kind) => p switch
    {
        TapeProfile.Normal1_2 => SharpZ80TimingTable.Normal1_2, TapeProfile.Normal1_3 => SharpZ80TimingTable.Normal1_3, TapeProfile.Normal1_4 => SharpZ80TimingTable.Normal1_4,
        TapeProfile.Mz700_1_1 => SharpZ80TimingTable.Mz700Normal, TapeProfile.Mz700_1_3 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Mz700Normal : SharpZ80TimingTable.Mz700Fast3,
        TapeProfile.Ic1_1 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Rom800Normal : SharpZ80TimingTable.Ic1_1,
        TapeProfile.Ic1_2 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Rom800Normal : SharpZ80TimingTable.Ic1_2,
        TapeProfile.Ic1_3 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Rom800Normal : SharpZ80TimingTable.Ic1_3,
        TapeProfile.Ic1_4 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Rom800Normal : SharpZ80TimingTable.Ic1_4,
        TapeProfile.Tc1_1 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Rom800Normal : SharpZ80TimingTable.Tc1_1,
        TapeProfile.Tc1_2 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Rom800Normal : SharpZ80TimingTable.Tc1_2,
        TapeProfile.Tc1_3 => kind == SharpBlockKind.Header ? SharpZ80TimingTable.Rom800Normal : SharpZ80TimingTable.Tc1_3,
        TapeProfile.Ultra => SharpZ80TimingTable.UltraPreTransferRom800,
        TapeProfile.UltraMz700 => SharpZ80TimingTable.Ultra700Leader, TapeProfile.UltraMz800 => SharpZ80TimingTable.Ultra800Leader,
        _ => SharpZ80TimingTable.Rom800Normal
    };
    private double Ref(bool high, bool isLong) => isLong ? high ? reference.LongHighMicroseconds : reference.LongLowMicroseconds : high ? reference.ShortHighMicroseconds : reference.ShortLowMicroseconds;
    internal void Add(WavPulseMode mode, bool physicalHigh, long duration, long end)
    {
        bool high = physicalHigh == candidate.Inverted;
        double us = duration * 1_000_000.0 / rate, scale = candidate.TimingScale > 0 && double.IsFinite(candidate.TimingScale) ? candidate.TimingScale : 1;
        bool isLong = double.IsFinite(Ref(high, true)) && Math.Abs(us - Ref(high, true) * scale) < Math.Abs(us - Ref(high, false) * scale);
        agreement.Add(mode, physicalHigh, duration, end, isLong);
        if (mode != WavPulseMode.ZeroCrossing) return;
        counters[(isLong ? 2 : 0) + (high ? 0 : 1)].Add(duration);
        double expected = Ref(high, isLong), ratio = us / expected;
        bool outlier = Math.Abs(ratio / scale - 1) > .35;
        if (outlier) outliers++;
        if (high) { pendingHigh = us; pendingLong = isLong; pendingOutlier = outlier; pendingSample = end - duration; }
        else if (pendingHigh.HasValue)
        {
            pairs++;
            if (pairs % scatterStride == 0) scatter.Add(new(pendingSample, pendingLong, pendingHigh.Value, us, pendingOutlier || outlier));
            if (scatter.Count > 20000) { int kept = 0; for (int j = 0; j < scatter.Count; j += 2) scatter[kept++] = scatter[j]; scatter.RemoveRange(kept, scatter.Count - kept); scatterStride *= 2; }
            pendingHigh = null;
        }
        if (windowCount == 0) windowStart = end - duration;
        windowEnd = end; windowCount++; ratioSum += ratio; ratioSquares += ratio * ratio; residualSquares += Math.Pow(ratio / scale - 1, 2);
        totalRatio += ratio; totalCount++;
        if (isLong) { longSum += us; longCount++; } else { shortSum += us; shortCount++; }
        if (windowCount == 256) FlushWindow();
    }
    private void FlushWindow()
    {
        if (windowCount == 0) return;
        double mean = ratioSum / windowCount;
        drift.Add(new(windowStart, windowEnd, windowCount, mean, shortCount > 0 ? shortSum / shortCount : null, longCount > 0 ? longSum / longCount : null,
            100 * Math.Sqrt(residualSquares / windowCount), 100 * Math.Sqrt(Math.Max(0, ratioSquares / windowCount - mean * mean)) / mean));
        windowCount = shortCount = longCount = 0; ratioSum = ratioSquares = residualSquares = shortSum = longSum = 0;
    }
    internal AudioBlockAnalysis Finish()
    {
        FlushWindow(); double? scale = totalCount > 0 ? totalRatio / totalCount : null;
        var groups = new[] { "SHORT High", "SHORT Low", "LONG High", "LONG Low" };
        return new(record, name, candidate.Kind.ToString(), TapeProfileNames.ToDisplayName(profile), reference.Name, candidate.Channel + 1,
            candidate.Inverted, candidate.PulseMode.ToString(), "ZeroCrossing, standard preprocessing; 256-pulse drift windows; scatter downsampled to ≤20,000 pairs", Start, End,
            candidate.ChecksumValid, candidate.RecordedChecksum, candidate.CalculatedChecksum, recovered,
            counters.Select((c, i) => c.Finish(groups[i], rate, double.IsFinite(Ref(i % 2 == 0, i >= 2)) ? Ref(i % 2 == 0, i >= 2) : null)).ToArray(),
            scatter, pairs, drift, agreement.Finish(), scale, scale is > 0 ? (1 / scale.Value - 1) * 100 : null, outliers)
            { ClassificationScale = candidate.TimingScale > 0 && double.IsFinite(candidate.TimingScale) ? candidate.TimingScale : 1 };
    }
}

internal sealed class AudioPulseAgreementCounter
{
    private readonly Queue<(bool High, long Duration, long End, bool Long)>[] pending = [new(), new()];
    private long z, s, matched, classified;
    internal void Add(WavPulseMode mode, bool high, long duration, long end, bool isLong)
    {
        int index = mode == WavPulseMode.ZeroCrossing ? 0 : 1; if (index == 0) z++; else s++;
        var other = pending[1 - index]; long tolerance = Math.Max(2, duration / 10);
        while (other.Count > 0 && other.Peek().End < end - tolerance) other.Dequeue();
        if (other.Count > 0 && other.Peek().High == high && Math.Abs(other.Peek().End - end) <= tolerance)
        { var pulse = other.Dequeue(); matched++; if (pulse.Long == isLong) classified++; }
        else { pending[index].Enqueue((high, duration, end, isLong)); if (pending[index].Count > 64) pending[index].Dequeue(); }
    }
    internal AudioDetectorAgreement Finish() => new(z, s, matched, z + s > 0 ? 200.0 * matched / (z + s) : null, matched > 0 ? 100.0 * classified / matched : null);
}
