using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QDTool
{
    internal static class MzfCompressionService
    {
        private static readonly MzfCompressionOptions[] AutoCandidates =
        [
            new(MzfCompressionAlgorithm.None),
            new(MzfCompressionAlgorithm.Zx0, CompressionDirection.Forward),
            new(MzfCompressionAlgorithm.Zx0, CompressionDirection.Backward),
            new(MzfCompressionAlgorithm.Zx7, CompressionDirection.Forward),
            new(MzfCompressionAlgorithm.Zx7, CompressionDirection.Backward),
            new(MzfCompressionAlgorithm.Zx0, CompressionDirection.Forward, Zx0Quick: true),
            new(MzfCompressionAlgorithm.Zx0, CompressionDirection.Backward, Zx0Quick: true)
        ];

        public static Task<MzfCompressionResult> CompressAsync(
            TapeRecord source,
            MzfCompressionOptions options,
            CompressionTarget target,
            CancellationToken cancellationToken = default) =>
            Task.Run(() => Compress(source, options, target, cancellationToken), cancellationToken);

        public static MzfCompressionResult Compress(
            TapeRecord source,
            MzfCompressionOptions options,
            CompressionTarget target,
            CancellationToken cancellationToken = default)
        {
            if (target == CompressionTarget.IplDsk)
            {
                source = Mz800IplDskWriter.PrepareRecordForIpl(source);
            }
            ValidateSource(source);
            ValidateOptions(options, target, source.Body.MzfBody.Length);
            if (options.Algorithm == MzfCompressionAlgorithm.Auto)
            {
                return CompressAuto(source, target, cancellationToken);
            }

            MzfCompressionResult result = CompressConcrete(source, options, cancellationToken);
            ValidateTargetResult(result.Record, target);
            return result;
        }

        public static void ValidateOptions(
            MzfCompressionOptions options,
            CompressionTarget target,
            int inputSize)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.SkipBytes < 0 || (options.SkipBytes > 0 && options.SkipBytes >= inputSize))
            {
                throw new InvalidOperationException("Skip must be smaller than the MZF program body.");
            }
            if (options.Algorithm is MzfCompressionAlgorithm.None or MzfCompressionAlgorithm.Auto)
            {
                if (options.SkipBytes != 0 || options.Zx0Quick || options.Zx7EmbeddedLoader)
                {
                    throw new InvalidOperationException("None and Auto do not accept algorithm-specific compression options.");
                }
            }
            if (options.Algorithm == MzfCompressionAlgorithm.Zx0 && options.Zx7EmbeddedLoader)
            {
                throw new InvalidOperationException("Embedded loader is a ZX7 option.");
            }
            if (options.Algorithm == MzfCompressionAlgorithm.Zx7 && options.Zx0Quick)
            {
                throw new InvalidOperationException("Quick mode is a ZX0 option.");
            }
            if (target == CompressionTarget.IplDsk && options.Zx7EmbeddedLoader)
            {
                throw new InvalidOperationException(
                    "ZX7 embedded loader is not compatible with direct IPL DSK because the MZF header is not loaded with the program body.");
            }
            if (target == CompressionTarget.IplDsk && options.SkipBytes != 0)
            {
                throw new InvalidOperationException(
                    "Partial/skip compression is not supported for a self-contained direct IPL DSK.");
            }
        }

        private static MzfCompressionResult CompressAuto(
            TapeRecord source,
            CompressionTarget target,
            CancellationToken cancellationToken)
        {
            var successes = new List<(MzfCompressionResult Result, int Rank, int Order)>();
            int order = 0;
            foreach (MzfCompressionOptions candidate in AutoCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    MzfCompressionResult result = CompressConcrete(source, candidate, cancellationToken);
                    successes.Add((result, GetAutoRank(candidate), order));
                }
                catch (InvalidOperationException)
                {
                    // Address-layout failures invalidate only this candidate.
                }
                order++;
            }

            if (successes.Count == 0)
            {
                throw new InvalidOperationException("Compression failed: no candidate has a valid 16-bit MZF address layout.");
            }

            IEnumerable<(MzfCompressionResult Result, int Rank, int Order)> eligible = successes;
            if (target == CompressionTarget.IplDsk)
            {
                eligible = eligible.Where(value =>
                    value.Result.PackedSize <= Mz800IplDskWriter.MaxIplStagedSize &&
                    (long)value.Result.Record.Header.MzfStart + value.Result.PackedSize <= 0x10000L);
            }

            List<(MzfCompressionResult Result, int Rank, int Order)> ordered = eligible
                .OrderBy(value => value.Result.PackedSize)
                .ThenBy(value => value.Rank)
                .ThenBy(value => value.Order)
                .ToList();
            if (ordered.Count == 0)
            {
                int smallest = successes.Min(value => value.Result.PackedSize);
                throw new InvalidOperationException(
                    $"Cannot create IPL DSK: original size is {source.Body.MzfBody.Length} bytes and the smallest packed size is {smallest} bytes, but the safe direct-IPL limit is {Mz800IplDskWriter.MaxIplStagedSize} bytes ($BCE9).");
            }
            MzfCompressionResult selected = ordered[0].Result;
            ValidateTargetResult(selected.Record, target);
            return selected;
        }

        private static int GetAutoRank(MzfCompressionOptions options) => options.Algorithm switch
        {
            MzfCompressionAlgorithm.None => 0,
            MzfCompressionAlgorithm.Zx0 when !options.Zx0Quick => 1,
            MzfCompressionAlgorithm.Zx7 => 2,
            MzfCompressionAlgorithm.Zx0 => 3,
            _ => 4
        };

        private static MzfCompressionResult CompressConcrete(
            TapeRecord source,
            MzfCompressionOptions options,
            CancellationToken cancellationToken)
        {
            if (options.Algorithm == MzfCompressionAlgorithm.None)
            {
                return new MzfCompressionResult(source.DeepClone(), options, source.Body.MzfBody.Length);
            }

            bool backwards = options.Direction == CompressionDirection.Backward;
            CompressionPayload payload = options.Algorithm switch
            {
                MzfCompressionAlgorithm.Zx0 => Zx0Compressor.Compress(
                    source.Body.MzfBody,
                    options.SkipBytes,
                    backwards,
                    options.Zx0Quick,
                    cancellationToken),
                MzfCompressionAlgorithm.Zx7 => Zx7Compressor.Compress(
                    source.Body.MzfBody,
                    options.SkipBytes,
                    backwards,
                    cancellationToken),
                _ => throw new InvalidOperationException("A concrete compression algorithm is required.")
            };

            TapeRecord packed = MzfLoaderBuilder.Build(source, payload, options);
            return new MzfCompressionResult(packed, options, source.Body.MzfBody.Length);
        }

        private static void ValidateSource(TapeRecord source)
        {
            ArgumentNullException.ThrowIfNull(source);
            byte[]? body = source.Body.MzfBody;
            if (body == null || body.Length == 0)
            {
                throw new InvalidOperationException("Compression requires a non-empty MZF program body.");
            }
            if (body.Length != source.Body.DataSize || body.Length != source.Header.MzfSize)
            {
                throw new InvalidOperationException(
                    $"MZF sizes are inconsistent (header {source.Header.MzfSize} B, body declaration {source.Body.DataSize} B, actual body {body.Length} B).");
            }
        }

        private static void ValidateTargetResult(TapeRecord record, CompressionTarget target)
        {
            if ((long)record.Header.MzfStart + record.Header.MzfSize > 0x10000L)
            {
                throw new InvalidOperationException(
                    "Compression failed: resulting address layout would overflow 16-bit MZ memory.");
            }
            if (target == CompressionTarget.IplDsk &&
                record.Body.MzfBody.Length > Mz800IplDskWriter.MaxIplStagedSize)
            {
                throw new InvalidOperationException(
                    $"Cannot create IPL DSK: staged program is {record.Body.MzfBody.Length} bytes, but the safe direct-IPL limit is {Mz800IplDskWriter.MaxIplStagedSize} bytes ($BCE9). Try ZX0/ZX7 compression or Auto.");
            }
        }
    }
}
