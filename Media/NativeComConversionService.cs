using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MZTools;

internal sealed record NativeComCompressionChoice(string Label, MzfCompressionOptions? Options)
{
    public override string ToString() => Label;
    internal static IReadOnlyList<NativeComCompressionChoice> Choices { get; } =
    [
        new("Keep source (no added compression)", null),
        new("None (decode known ZX0/ZX7)", new(MzfCompressionAlgorithm.None)),
        new("Auto (smallest safe COM)", new(MzfCompressionAlgorithm.Auto)),
        new("ZX0 forward (quick)", new(MzfCompressionAlgorithm.Zx0, Zx0Quick: true)),
        new("ZX0 backward (quick)", new(MzfCompressionAlgorithm.Zx0, CompressionDirection.Backward, Zx0Quick: true)),
        new("ZX7 forward", new(MzfCompressionAlgorithm.Zx7)),
        new("ZX7 backward", new(MzfCompressionAlgorithm.Zx7, CompressionDirection.Backward)),
        new("ZX0 forward (best compression; slower)", new(MzfCompressionAlgorithm.Zx0)),
        new("ZX0 backward (best compression; slower)", new(MzfCompressionAlgorithm.Zx0, CompressionDirection.Backward))
    ];
}

internal static class NativeComConversionService
{
    internal static string SuggestName(string decodedHeaderName)
    {
        string stem = new(decodedHeaderName.ToUpperInvariant().Where(c =>
            c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-').Take(8).ToArray());
        return (stem.Length == 0 ? "PROGRAM" : stem) + ".COM";
    }

    internal static MzNativeComResult Build(NativeProgramImage source, CpmTargetProfile profile,
        NativeMachineMode mode, MzfCompressionOptions? options, bool multipart = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options != null) MzfCompressionService.ValidateOptions(options, CompressionTarget.NativeCom, source.Segments.Sum(s => s.Data.Length));
        if (!NativeHeaderExecution.SupportedType(source)) throw new InvalidDataException("Unsupported MZF file type or executable-header loader.");
        if (options == null) return MzNativeComBuilder.Build(source, profile, mode, true, multipart);
        if (source.Segments.Count != 1 || source.Dependencies.Count > 0 || multipart)
            throw new InvalidDataException("Compression requires a standalone single-record program without unresolved dependencies.");
        using var reader = new BinaryReader(new MemoryStream(source.Header.Concat(source.Segments[0].Data).ToArray()));
        TapeRecord record = new MZTFileReader().ReadMzfRecord(reader);
        if (MzfLoaderBuilder.TryGetCompressionInfo(record, out _)) record = MzfDecompressionService.Decompress(record).Record;

        MzNativeComResult Candidate(MzfCompressionOptions policy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packed = MzfCompressionService.Compress(record, policy, CompressionTarget.MzfTape, cancellationToken);
            // Compression must retain executable header bytes as well as the program body.
            if (NativeHeaderExecution.TryGetJump(MzfNativeImageParser.Parse(record), out _) &&
                !packed.Record.GetSerializedHeader().AsSpan(24).SequenceEqual(record.GetSerializedHeader().AsSpan(24)))
                throw new InvalidDataException("Compression would overwrite the executable header loader. Choose a non-embedded loader.");
            if (policy.Algorithm != MzfCompressionAlgorithm.None)
            {
                var restored = MzfDecompressionService.Decompress(packed.Record).Record;
                if (!restored.Body.MzfBody.AsSpan().SequenceEqual(record.Body.MzfBody) ||
                    restored.Header.MzfStart != record.Header.MzfStart || restored.Header.MzfExec != record.Header.MzfExec)
                    throw new InvalidDataException("Compression round-trip verification failed.");
            }
            var image = MzfNativeImageParser.Parse(packed.Record, source.SourceType, source.InputSha256);
            image = image with { Warnings = source.Warnings.Where(w => !w.StartsWith("Input compression:", StringComparison.Ordinal))
                .Concat(image.Warnings.Select(w => w.StartsWith("Input compression:", StringComparison.Ordinal)
                    ? w.Replace("Input compression:", "Output payload compression:", StringComparison.Ordinal) : w))
                .Append($"COM compression policy: {policy.Algorithm} {policy.Direction}; native payload {record.Body.MzfBody.Length} B -> stored {packed.PackedSize} B. Source unchanged.").ToArray() };
            return MzNativeComBuilder.Build(image, profile, mode, true);
        }
        if (options.Algorithm != MzfCompressionAlgorithm.Auto) return Candidate(options);
        var successes = new List<MzNativeComResult>();
        var reasons = new List<string>();
        try { successes.Add(MzNativeComBuilder.Build(source, profile, mode, true)); }
        catch (InvalidDataException e) { reasons.Add(e.Message); }
        var candidates = NativeComCompressionChoice.Choices.Where(c => c.Options != null && c.Options.Algorithm != MzfCompressionAlgorithm.Auto);
        bool Best(NativeComCompressionChoice choice) => choice.Options is { Algorithm: MzfCompressionAlgorithm.Zx0, Zx0Quick: false };
        foreach (var choice in candidates.Where(c => !Best(c)))
        {
            try { successes.Add(Candidate(choice.Options!)); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { reasons.Add(choice.Label + ": " + e.Message); }
        }
        // Try the slower optimal ZX0 encoder when quick policies cannot fit (e.g. original HLIPA).
        if (successes.Count == 0)
            foreach (var choice in candidates.Where(Best))
            {
                try { successes.Add(Candidate(choice.Options!)); }
                catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { reasons.Add(choice.Label + ": " + e.Message); }
            }
        if (successes.Count == 0) throw new InvalidDataException("No safe compression/loader combination.\n" + string.Join("\n", reasons));
        var best = successes.MinBy(r => r.Bytes.Length)!;
        return best with { Report = best.Report + "\nAuto: smallest COM among successfully verified compression and loader placements." };
    }
}
