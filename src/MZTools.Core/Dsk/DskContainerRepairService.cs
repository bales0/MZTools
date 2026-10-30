using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal enum DskRepairRisk { SafeDeterministic, UnsafeAmbiguous, NotRepairable }
internal sealed record DskRepairPlan(string IssueCode, string Description, DskRepairRisk Risk,
    bool RequiresTruncateConfirmation, DskOperationPreview? Preview);

internal static class DskContainerRepairService
{
    internal static IReadOnlyList<DskRepairPlan> Inspect(byte[] source)
    {
        var plans = new List<DskRepairPlan>();
        DskImage? parsed = null;
        try { parsed = DskImage.Parse(source); } catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) { }
        if (source.Length < 256 || !source.AsSpan(0, 34).SequenceEqual("EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8) || source[0x31] is not (1 or 2))
            return [new("DSK_PARSE_FAILED", "Header/signature or side count is invalid; no deterministic repair.", DskRepairRisk.NotRepairable, false, null)];

        // Conservative recovery: complete, non-sparse, canonical C/H sequence with
        // one aligned Track-Info boundary per track. Embedded signatures make it ambiguous.
        var starts = new List<int>();
        for (int p = 256; p <= source.Length - 256; p += 256)
            if (source.AsSpan(p, 12).SequenceEqual("Track-Info\r\n"u8)) starts.Add(p);
        int sides = source[0x31];
        if (starts.Count > 0 && starts[0] == 256 && starts.Count <= DskImage.MaximumAbsoluteTracks && starts.Count % sides == 0 && source.Length % 256 == 0)
        {
            byte[] candidate = (byte[])source.Clone();
            bool valid = !(parsed != null && parsed.ContainerTrailingData.Length > 0 && parsed.Tracks.Count == starts.Count);
            int declaredCount = source[0x30] * sides;
            if (declaredCount <= DskImage.MaximumAbsoluteTracks && Enumerable.Range(0, declaredCount).Any(i => source[0x34 + i] == 0)) valid = false;
            // The final boundary cannot be inferred from EOF: trailing data must
            // never be absorbed as track padding. Require the final tsize as evidence.
            if (starts[^1] + (source[0x34 + starts.Count - 1] << 8) != source.Length) valid = false;
            for (int i = 0; i < starts.Count; i++)
            {
                int p = starts[i], end = i + 1 < starts.Count ? starts[i + 1] : source.Length;
                int size = end - p;
                if (source[p + 0x10] != i / sides || source[p + 0x11] != i % sides || size / 256 > 255) { valid = false; break; }
                candidate[0x34 + i] = (byte)(size / 256);
            }
            candidate[0x30] = checked((byte)(starts.Count / sides));
            // Stale entries beyond the declared geometry are not interpreted or modified.
            if (valid && !source.AsSpan().SequenceEqual(candidate))
            {
                try
                {
                    var recovered = DskImage.Parse(candidate);
                    if (recovered.ContainerTrailingData.Length != 0 || !recovered.Serialize().AsSpan().SequenceEqual(candidate)) valid = false;
                    // Extra boundaries inside valid declared sector data are not evidence of tracks.
                    if (parsed != null && starts.Any(p => parsed.Tracks.Where(t => t != null).Any(t => p > t!.FileOffset && p < t.FileOffset + t.BlockSize))) valid = false;
                    if (valid)
                    {
                        string code = source[0x30] != candidate[0x30] ? "DSK_TRACK_COUNT" : "DSK_TSIZE";
                        string report = code + "\nAffected structures: disk header geometry / tsize table. File payloads: unchanged.\n" + Changes(source, candidate) + "\nAnalyzer before:\n" + AnalyzeSource(source) + "\nAnalyzer after:\n" + DskAnalyzer.Analyze(recovered).Report();
                        plans.Add(new(code, "Restore header geometry/track-size table from complete canonical track boundaries.", DskRepairRisk.SafeDeterministic, false, new(source, candidate, report)));
                    }
                }
                catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) { valid = false; }
            }
        }
        if (parsed?.ContainerTrailingData.Length > 0)
        {
            int end = parsed.DeclaredImageSize;
            // A complete track after the declared data could indicate a wrong track count.
            bool looksLikeTrack = starts.Any(p => p >= end);
            if (!looksLikeTrack)
            {
                byte[] output = source[..end];
                var check = DskImage.Parse(output);
                plans.Add(new("DSK_TRAILING_DATA", $"Truncate {source.Length - end} bytes beyond all declared tracks (possible unrelated data).", DskRepairRisk.UnsafeAmbiguous, true,
                    new(source, output, $"DSK_TRAILING_DATA\nAffected structure: container trailing bytes. File payloads: unchanged.\nExplicit destructive truncate: remove bytes 0x{end:X}..0x{source.Length - 1:X}.\nAll declared tracks remain byte-identical.\nAnalyzer before:\n" + AnalyzeSource(source) + "\nAnalyzer after:\n" + DskAnalyzer.Analyze(check).Report())));
            }
        }
        if (plans.Count == 0)
            plans.Add(new("DSK_CONTAINER", parsed == null ? "Track boundaries, padding or geometry are ambiguous; no safe automatic repair." : "No deterministic container repair is needed or available.", parsed == null ? DskRepairRisk.UnsafeAmbiguous : DskRepairRisk.NotRepairable, false, null));
        return plans;
    }

    private static string AnalyzeSource(byte[] bytes)
    {
        try { return DskAnalyzer.Analyze(DskImage.Parse(bytes)).Report(); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        { return "DSK_PARSE_FAILED: original container cannot be analyzed: " + e.Message; }
    }

    private static string Changes(byte[] before, byte[] after)
    {
        var report = new StringBuilder("Deterministic DSK container repair preview\nOnly geometry/tsize header bytes change; track bytes are unchanged.\n");
        for (int i = 0; i < 256; i++) if (before[i] != after[i]) report.AppendLine($"Header 0x{i:X2}: {before[i]:X2} → {after[i]:X2}");
        return report.ToString();
    }
}
