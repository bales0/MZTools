using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MZTools;

internal sealed record NativeSegment(int Address, byte[] Data)
{
    internal int End => checked(Address + Data.Length);
}
internal enum NativeMachineMode { Mz800Monitor, Mz700Monitor, Mz800AllRam }
internal sealed record NativeProgramImage(string SourceType, string Name, byte Type,
    IReadOnlyList<NativeSegment> Segments, int EntryPoint, byte[] Header,
    IReadOnlyList<string> Dependencies, IReadOnlyList<string> Warnings, string InputSha256)
{
    internal MzfCompressionInfo? Compression { get; init; }
}

internal static class NativeHeaderExecution
{
    internal const int HeaderAddress = 0x10F0;

    // Support the executable-header trampoline used by HLIPA, not arbitrary unknown file types.
    // A recognized packed record must restore the same entry and a payload containing its target.
    internal static bool TryGetJump(NativeProgramImage image, out int target)
    {
        target = 0;
        int entry = image.Compression?.RestoredExec ?? image.EntryPoint;
        int offset = entry - HeaderAddress;
        if (image.Header.Length != 128 || offset < 24 || offset > 125 || image.Header[offset] != 0xC3 || image.Segments.Count != 1)
            return false;
        target = image.Header[offset + 1] | image.Header[offset + 2] << 8;
        var segment = image.Segments[0];
        int load = image.Compression?.RestoredLoad ?? segment.Address;
        int size = image.Compression?.RestoredSize ?? segment.Data.Length;
        return load >= HeaderAddress + 128 && load + size <= 0x10000 && target >= load && target < load + size &&
            !(segment.Address < entry + 3 && entry < segment.End);
    }

    internal static bool SupportedType(NativeProgramImage image) => image.Type == 1 || image.Type == 0x4D && TryGetJump(image, out _);
}

internal static class MzfNativeImageParser
{
    internal static NativeProgramImage Read(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".mzf" or ".m12" or ".mz0" or ".mz7")) throw new InvalidDataException("Select an MZF or structurally compatible single-record M12.");
        byte[] bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var record = new MZTFileReader().ReadMzfRecord(reader);
        var body = record.Body;
        body.TrailingData = reader.ReadBytes(checked((int)(stream.Length - stream.Position)));
        record.Body = body;
        return Parse(record, extension == ".m12" ? "M12" : "MZF", ImageVerificationService.Hash(bytes));
    }

    internal static NativeProgramImage Parse(TapeRecord record, string sourceType = "MZF", string? inputHash = null)
    {
        byte[] body = record.Body.MzfBody;
        if (body == null || body.Length != record.Header.MzfSize || body.Length != record.Body.DataSize)
            throw new InvalidDataException("Declared body size does not match the actual payload.");
        var dependencies = new List<string>(); var warnings = new List<string>();
        if (MzfLoaderBuilder.TryGetCompressionInfo(record, out var compression) && compression != null)
            warnings.Add($"Input compression: {compression.DisplayName}; the original self-extracting loader and payload are preserved unchanged. No compression is added by COM conversion. Expanded LOAD={compression.RestoredLoad:X4}, EXEC={compression.RestoredExec:X4}, size={compression.RestoredSize} B.");
        byte[] trailing = record.Body.TrailingData ?? [];
        if (trailing.Length > 0)
        {
            if (trailing.All(b => b is 0 or 0xFF)) warnings.Add($"{trailing.Length} trailing padding bytes excluded from COM payload.");
            else dependencies.Add($"{trailing.Length} non-padding trailing bytes: unresolved extra record/multipart dependency.");
        }
        byte[] header = record.GetSerializedHeader();
        return new(sourceType, SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname), record.Header.MzfFtype,
            [new(record.Header.MzfStart, (byte[])body.Clone())], record.Header.MzfExec, header,
            dependencies, warnings, inputHash ?? Convert.ToHexString(SHA256.HashData(header.Concat(body).Concat(trailing).ToArray())))
            { Compression = compression };
    }
}

internal sealed record NativeLoaderRange(string Name, int Start, int Length)
{
    internal int End => checked(Start + Length);
    internal bool Overlaps(int start, int end) => Start < end && start < End;
}

internal sealed record CpmTargetProfile(string Name, int ComLoadBase, int LoadCeiling,
    IReadOnlyList<NativeLoaderRange> Candidates, IReadOnlyList<NativeLoaderRange> ProtectedBeforeTakeover)
{
    // LOW occupies reserved padding in this COM, not an assumed OS-free address.
    // HIGH must additionally lie below the chosen ceiling and outside source data.
    internal static CpmTargetProfile Create(string name, int ceiling = 0xC000) => new(name, 0x100, ceiling,
        [new("LOW", 0x1000, 0xF0), new("HIGH", 0xA000, 0x100)],
        [new("CP/M zero page", 0, 0x100), new("outside declared TPA", ceiling, 0x10000 - ceiling)]);
    internal static IReadOnlyList<CpmTargetProfile> BuiltIns { get; } =
        [Create("P-CP/M80"), Create("CP/M 4.1"), Create("CP/M 4.2"), Create("Custom / conservative")];
    public override string ToString() => Name;
}

internal sealed record NativeLoaderPlan(bool Supported, NativeLoaderRange? Loader, bool Backward,
    int SourceStart, int SourceEnd, string Report, string? UnsupportedReason = null);

internal static class NativeLoaderPlacementAnalyzer
{
    internal const int Stage0End = 0x140, Stage1Source = 0x200, Stage1Capacity = 0xC0;
    internal const int HeaderSource = 0x300, MetadataSource = 0x380, PayloadSource = 0x1200;

    internal static NativeLoaderPlan Analyze(NativeProgramImage image, CpmTargetProfile profile, NativeMachineMode mode,
        bool independent, bool knownMultipart = false)
    {
        var errors = new List<string>();
        int length = image.Segments.Sum(s => s.Data.Length);
        int sourceEnd = checked(PayloadSource + length);
        if (!Enum.IsDefined(mode)) errors.Add("Unknown native machine state.");
        if (profile.ComLoadBase != 0x100 || profile.LoadCeiling is < PayloadSource or > 0xE000)
            errors.Add("CP/M profile must use COM origin 0100 and a declared ceiling between 1200 and E000.");
        if (!independent || knownMultipart || image.Dependencies.Count > 0) errors.Add("Unresolved multipart/tape/extra-file dependency; only standalone single-record programs are supported.");
        errors.AddRange(image.Dependencies);
        if (!NativeHeaderExecution.SupportedType(image)) errors.Add("Only type 1 machine-code programs or a validated type 4D header jump into the program are supported.");
        if (image.Segments.Count != 1) errors.Add("First version supports one segment; multi-segment copy ordering is not established.");
        if (image.Header.Length != 128 || image.Segments.Any(s => s.Address < 0 || s.Data.Length == 0 || s.End > 0x10000))
            errors.Add("Empty, wrapping or invalid segment/header.");
        if (!image.Segments.Any(s => image.EntryPoint >= s.Address && image.EntryPoint < s.End) &&
            !(image.Compression == null && NativeHeaderExecution.TryGetJump(image, out _)))
            errors.Add("EntryPoint is outside the supplied segments and is not a validated executable-header jump.");
        if (sourceEnd > profile.LoadCeiling) errors.Add("Program too large for selected CP/M profile (no compression).");
        if (mode != NativeMachineMode.Mz800AllRam && image.Segments.Any(s => s.Address < 0x1200 || s.End > (mode == NativeMachineMode.Mz700Monitor ? 0xC000 : 0xE000)))
            errors.Add("Selected monitor map requires RAM targets from 1200 up to C000 (700 mode) or E000 (800 mode). Select all-RAM only for a program which initializes its own environment.");
        NativeLoaderRange? selected = null;
        foreach (var candidate in profile.Candidates)
        {
            if (candidate.Start < 0x100 || candidate.Length < Stage1Capacity + 0x30 || candidate.End > profile.LoadCeiling) continue;
            if (image.Segments.Any(s => candidate.Overlaps(s.Address, s.End))) continue;
            if (candidate.Overlaps(PayloadSource, sourceEnd) || candidate.Overlaps(profile.ComLoadBase, Stage0End) ||
                candidate.Overlaps(Stage1Source, Stage1Source + Stage1Capacity) || candidate.Overlaps(HeaderSource, MetadataSource + 32) ||
                candidate.Overlaps(0x10F0, 0x1170)) continue;
            if (profile.ProtectedBeforeTakeover.Any(p => candidate.Overlaps(p.Start, p.End))) continue;
            if (mode != NativeMachineMode.Mz800AllRam && (candidate.Start < 0x1000 || candidate.End > 0xC000)) continue;
            if (mode == NativeMachineMode.Mz700Monitor && candidate.Overlaps(0x1000, 0x2000)) continue; // CG-ROM during font copy
            selected = candidate; break;
        }
        if (selected == null) errors.Add("No safe LOW/HIGH loader placement; candidate conflicts with target, source, mapped ROM or protected area.");
        bool backward = image.Segments.Count == 1 && image.Segments[0].Address > PayloadSource && image.Segments[0].Address < sourceEnd;
        string report = $"Source: {image.SourceType}; name: {image.Name}; type: {image.Type}\nInput SHA-256: {image.InputSha256}\n" +
            string.Join("\n", image.Segments.Select(s => $"Segment [{s.Address:X4},{s.End:X}), {s.Data.Length} B")) +
            $"\nEntryPoint: {image.EntryPoint:X4}\nTarget CP/M: {profile.Name}; declared COM area [0100,{profile.LoadCeiling:X4})\n" +
            $"Bootstrap: {selected?.Name ?? "Unsupported"}; range [{selected?.Start:X4},{selected?.End:X4}) including private stack\nSource payload inside COM: [{PayloadSource:X4},{sourceEnd:X})\nOverlap strategy: {(backward ? "LDDR" : "LDIR")}\n" +
            $"Native state: {mode}; DI, IM1; private SP; PPI reset, external PIO interrupts disabled, PSG muted.\nNative takeover: YES\nCP/M may be overwritten after takeover: YES\nReturn to CP/M: NO\nExit: RESET\nBootstrap compression: NONE (payload compression is reported separately)\n" +
            (NativeHeaderExecution.TryGetJump(image, out int headerTarget) ? $"Executable header preserved at 10F0: entry {image.Compression?.RestoredExec ?? image.EntryPoint:X4}, JP {headerTarget:X4} into restored payload.\n" : "") +
            "TPA ceiling is an explicit profile bound, not a measured BDOS address. Stage 0 checks the live BDOS vector before takeover. CP/M variant/hardware certification is separate.\nNative 8253 timers and the PPI timer IRQ gate are initialized; final CPU IRQ state is DI until the program enables it. Pending floppy INTRQ is acknowledged. Monitor work areas are not universally reconstructed; game-specific dependencies must be verified.\n" +
            string.Join("\n", image.Warnings.Concat(errors)) + $"\nResult: {(errors.Count == 0 ? "Convertible with selected explicit native state; hardware unverified" : "Unsupported")}";
        return new(errors.Count == 0, selected, backward, PayloadSource, sourceEnd, report,
            errors.Count == 0 ? null : string.Join("\n", errors));
    }
}
