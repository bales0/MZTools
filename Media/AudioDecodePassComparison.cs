using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MZTools;

internal sealed record AudioPassDifference(string Stage, string Status, long? Sample, double? Seconds, string Left, string Right, string Evidence);
internal sealed record AudioPassComparisonResult(string Status, IReadOnlyList<AudioPassDifference> Rows, int OmittedDiagnostics);

internal static class AudioDecodePassComparison
{
    internal static AudioPassComparisonResult Compare(AudioRegionRecoveryResult result, int left, int right, CancellationToken token = default)
    {
        if (left < 0 || right < 0 || left >= result.Passes.Count || right >= result.Passes.Count) throw new ArgumentOutOfRangeException(nameof(left));
        token.ThrowIfCancellationRequested();
        var a = result.Passes[left]; var b = result.Passes[right];
        var rows = new List<AudioPassDifference>();
        rows.Add(new("Preprocessing", "Parameters", result.StartSample, result.StartSample / (double)result.SampleRate,
            Describe(a), Describe(b), "Same selected source/sample interval; different gates/transforms are explicit. Parameter differences alone do not prove a damaged byte."));
        if (a.AudioTransform != b.AudioTransform || a.Channels != b.Channels || a.Polarity != b.Polarity)
            return new("Unable to align", rows.Append(new("Alignment", "Unable to align", null, null, Describe(a), Describe(b), "Transforms, channels or polarity selections differ; no implicit mixing/alignment." )).ToArray(),result.OmittedDiagnostics);
        AudioRecoveryEvent[] Events(int index) => result.Diagnostics.Where(e => e.Context.StartsWith($"Pass {index+1}:",StringComparison.Ordinal)).OrderBy(e => e.Sample).ToArray();
        var eventsA = Events(left); var eventsB = Events(right);
        if (eventsA.Length == 0 || eventsB.Length == 0)
            return new("Unable to align", rows.Append(new("Alignment", "Unable to align", null, null, $"{eventsA.Length} stored events",$"{eventsB.Length} stored events","Diagnostics unavailable or capped. A missing event is not a proven decoder failure.")).ToArray(), result.OmittedDiagnostics);
        var used = new HashSet<int>();
        var unaligned = new List<AudioRecoveryEvent>();
        foreach (var x in eventsA)
        {
            token.ThrowIfCancellationRequested();
            int match = -1; long best = long.MaxValue;
            for (int j = 0; j < eventsB.Length; j++)
            {
                var y = eventsB[j];
                if (used.Contains(j) || x.Channel != y.Channel || x.Inverted != y.Inverted || x.Stage != y.Stage) continue;
                long delta = Math.Abs(x.Sample-y.Sample);
                if (delta <= Math.Max(2, result.SampleRate / 1000) && delta < best) { best = delta; match = j; }
            }
            if (match < 0) { unaligned.Add(x); continue; }
            used.Add(match); var aligned = eventsB[match];
            bool same = x.Code == aligned.Code && x.ByteIndex == aligned.ByteIndex && x.ExpectedLength == aligned.ExpectedLength && x.Reason == aligned.Reason;
            if (!same || x.PulseSamples != aligned.PulseSamples)
                rows.Add(new(x.Stage, "First observed mismatch", Math.Min(x.Sample, aligned.Sample), Math.Min(x.Seconds,aligned.Seconds), Event(x),Event(aligned),
                    "One-to-one event alignment by stage/channel/polarity within 1 ms (minimum 2 samples). This is the first retained mismatch, not proof of the absolute first error. Pulse widths are measured; classes/states are only those explicitly logged."));
            if (rows.Count > 1) break;
        }
        bool mismatch = rows.Any(r => r.Status == "First observed mismatch");
        if (!mismatch)
        {
            unaligned.AddRange(eventsB.Where((_,index) => !used.Contains(index)));
            var first = unaligned.OrderBy(e => e.Sample).FirstOrDefault();
            if (first != null) rows.Add(new(first.Stage,"Unable to align",first.Sample,first.Seconds,Event(first),"No corresponding retained event",
                "An unpaired observation is not proof of the first failure. Inspect this location and omitted-event counts."));
        }
        bool InStage(AudioRecoveryEvent e, string stage) => stage switch {
            "Leader / mark" => e.Code.Contains("LEADER") || e.Code.Contains("MARK"),
            "Header acceptance" => e.Stage == "Header acceptance" || e.Stage == "Header discovery",
            "Length" => e.Code.Contains("LENGTH") || e.Code.Contains("TRUNCATED") || e.Code == "SCANNER_CREATED",
            "Checksum" => e.Code.Contains("CHECKSUM"),
            "Candidate assembly" => e.Stage == "Assembly",
            _ => false };
        foreach (string stage in new[] { "Edge detection", "Pulse classification", "Leader / mark", "Header acceptance", "Length", "Checksum", "Candidate assembly" }) {
            int countA = eventsA.Count(e => InStage(e,stage)), countB = eventsB.Count(e => InStage(e,stage));
            rows.Add(new(stage,countA + countB == 0 ? "Unobserved" : "Retained evidence",null,null,$"{countA} retained events",$"{countB} retained events",
                "Retained decoder events are bounded. Stage-specific state, byte index, pulse neighborhoods, reset reasons and checksums appear in event evidence; absent stages are unobserved, not successful."));
        }
        return new(mismatch ? "First observed mismatch" : used.Count == 0 || unaligned.Count > 0 ? "Unable to align" : "No mismatch in aligned retained events",rows,result.OmittedDiagnostics);
    }
    private static string Describe(AudioRecoveryPass pass) => $"{pass.Test}; {pass.Detectors}; gate ×{pass.ThresholdScale:G}; deadband ±{pass.ZeroDeadbandPercent:G}% FS; {pass.Channels}/{pass.Polarity}; {pass.AudioTransform}; reference {pass.Reference ?? "adaptive/fixed comparison"}";
    private static string Event(AudioRecoveryEvent e) => $"sample {e.Sample}; {e.Detector}; channel {e.Channel}; inverted {e.Inverted}; {e.Code}; byte {e.ByteIndex}; expected {e.ExpectedLength}; pulse {e.PulseSamples:G} samples; {e.Reason}; {e.Context}";
}
