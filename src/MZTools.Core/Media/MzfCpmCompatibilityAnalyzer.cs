using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal enum CompatibilityEvidence { Proven, StrongEvidence, Heuristic, Unknown }
internal sealed record MzfCpmAnalysis(string Name, byte Type, int BodySize, ushort Load, ushort Exec,
    int EndExclusive, bool EntryWithinBody, bool LoadAtComOrigin, bool LowMemoryOverlap,
    bool TpaOverlap, bool PossibleMultipart, string ConversionMode, string Profile,
    CompatibilityEvidence Evidence, IReadOnlyList<string> Warnings)
{
    // Header addresses and instruction-like byte patterns never establish a
    // native CP/M program or the required execution environment.
    internal bool CanConvert => false;
    internal string Report => $"MZF / CP/M compatibility — read-only\nName: {Name}; type: {Type}; body: {BodySize} B\nLOAD={Load:X4}; EXEC={Exec:X4}; range [{Load:X4}, {EndExclusive:X})\nLOAD at COM 0100: {LoadAtComOrigin}; entry inside body: {EntryWithinBody}\nLow-memory overlap: {LowMemoryOverlap}; conservative TPA ceiling overlap: {TpaOverlap}\nPossible multipart / trailing dependency: {PossibleMultipart} (not automatically resolved)\nConversion mode: {ConversionMode}\nHistorical profile: {Profile}\nEvidence: {Evidence}\n\n" + string.Join("\n", Warnings) + "\n\nConvert disabled: no independently proven native COM or validated historical launcher profile matches. LOAD=0100 / EXEC=0100 alone are insufficient. See docs/MZF_CPM_INTEROP_RESEARCH.md.";
}

internal static class MzfCpmCompatibilityAnalyzer
{
    // E000 is a conservative inspection ceiling, not a measurement of a running
    // system's BDOS/BIOS address. Report exact runtime TPA as unknown.
    internal static MzfCpmAnalysis Analyze(TapeRecord record, bool knownMultipart = false)
    {
        var header = record.Header; byte[] body = record.Body.MzfBody;
        if (body == null || body.Length != header.MzfSize || body.Length != record.Body.DataSize)
            throw new InvalidDataException("MZF declared size/body length mismatch.");
        int end = header.MzfStart + body.Length;
        bool multipart = knownMultipart || record.Body.TrailingData is { Length: > 0 };
        var warnings = new List<string> { "Actual CP/M generation, TPA/BDOS/BIOS boundaries, ROM services and resident runtime dependencies are unknown." };
        if (body.Length == 0) warnings.Add("Empty body.");
        if (end > 0x10000) warnings.Add("LOAD range wraps/exceeds the 16-bit address space.");
        bool entry = header.MzfExec >= header.MzfStart && header.MzfExec < end && end <= 0x10000;
        if (!entry) warnings.Add("EXEC lies outside the non-wrapping body range.");
        if (header.MzfStart < 0x100) warnings.Add("Body overlaps CP/M low memory / vectors / command tail.");
        if (end > 0xE000) warnings.Add("Body exceeds conservative E000 inspection ceiling; actual TPA must be established for the target system.");
        if (multipart) warnings.Add("Additional bytes or known multiple records prevent a single-file independence claim.");
        for (int i = 0; i < body.Length; i++)
        {
            if (i + 2 < body.Length && body[i] is 0xCD or 0xC3)
            {
                int destination = body[i + 1] | body[i + 2] << 8;
                if (destination < 0x1000) warnings.Add($"Heuristic at body +{i:X}: CALL/JP-like bytes to {destination:X4}; may be ROM/low memory or data, not proven executable code.");
            }
            if (i + 1 < body.Length && body[i] is 0xD3 or 0xDB)
                warnings.Add($"Heuristic at body +{i:X}: IN/OUT-like bytes, port {body[i + 1]:X2}; hardware/paging behavior requires disassembly.");
            if (warnings.Count >= 40) { warnings.Add("Additional heuristic patterns omitted; this is not control-flow disassembly."); break; }
        }
        return new(SharpMzEncoding.ConvertMzfNameToASCIIString(header.MzfFname), header.MzfFtype, body.Length,
            header.MzfStart, header.MzfExec, end, entry, header.MzfStart == 0x100,
            body.Length > 0 && header.MzfStart < 0x100, end > 0xE000, multipart,
            "Unsupported / conversion not proven safe", "No validated profile", CompatibilityEvidence.Unknown, warnings);
    }
}
