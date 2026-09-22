using System;
using System.Buffers.Binary;

namespace QDTool
{
    // The decoder byte sequences and address formulas below are ported from
    // bales0/mz0 and bales0/mz7. Their original BSD notices are reproduced in
    // THIRD_PARTY_NOTICES.md.
    internal static class MzfLoaderBuilder
    {
        private static readonly byte[] Zx0ForwardDecoder =
        [
            0x0D, 0x21, 0x00, 0x00, 0x11, 0x00, 0x00, 0x01, 0xAD, 0x00, 0xC5, 0x01, 0xFF, 0xFF, 0xC5, 0x03,
            0x3E, 0x80, 0xCD, 0xE4, 0x11, 0xED, 0xB0, 0x87, 0x38, 0x0D, 0xCD, 0xE4, 0x11, 0xE3, 0xE5, 0x19,
            0xED, 0xB0, 0xE1, 0xE3, 0x87, 0x30, 0xEB, 0xCD, 0xE4, 0x11, 0x08, 0xF1, 0xAF, 0x91, 0xC8, 0x47,
            0x08, 0x4E, 0x23, 0xCB, 0x18, 0xCB, 0x19, 0xC5, 0x01, 0x01, 0x00, 0xD4, 0xEC, 0x11, 0x03, 0x18,
            0xDC, 0x0C, 0x87, 0x20, 0x03, 0x7E, 0x23, 0x17, 0xD8, 0x87, 0xCB, 0x11, 0xCB, 0x10, 0x18, 0xF2
        ];

        private static readonly byte[] Zx0BackwardDecoder =
        [
            0x0D, 0x21, 0x00, 0x00, 0x11, 0x00, 0x00, 0x01, 0xAD, 0x00, 0xC5, 0x01, 0x01, 0x00, 0xC5, 0x0D,
            0x3E, 0x80, 0xCD, 0xE4, 0x11, 0xED, 0xB8, 0x87, 0x38, 0x0D, 0xCD, 0xE4, 0x11, 0xE3, 0xE5, 0x19,
            0xED, 0xB8, 0xE1, 0xE3, 0x87, 0x30, 0xEB, 0x33, 0x33, 0xCD, 0xE4, 0x11, 0x05, 0xC8, 0x0D, 0x41,
            0x4E, 0x2B, 0xCB, 0x38, 0xCB, 0x19, 0x03, 0xC5, 0x01, 0x01, 0x00, 0xDC, 0xEC, 0x11, 0x03, 0x18,
            0xDC, 0x0C, 0x87, 0x20, 0x03, 0x7E, 0x2B, 0x17, 0xD0, 0x87, 0xCB, 0x11, 0xCB, 0x10, 0x18, 0xF2
        ];

        private static readonly byte[] Zx7ForwardDecoder =
        [
            0xCB, 0x21, 0x00, 0x00, 0x11, 0x00, 0x00, 0x01, 0xAD, 0x00, 0xC5, 0x3E, 0x80, 0xED, 0xA0, 0xCD,
            0xED, 0x11, 0x30, 0xF9, 0xD5, 0x01, 0x00, 0x00, 0x50, 0x14, 0xCD, 0xED, 0x11, 0x30, 0xFA, 0xD4,
            0xED, 0x11, 0xCB, 0x11, 0xCB, 0x10, 0x38, 0x1F, 0x15, 0x20, 0xF4, 0x03, 0x5E, 0x23, 0xCB, 0x33,
            0x30, 0x0C, 0x16, 0x10, 0xCD, 0xED, 0x11, 0xCB, 0x12, 0x30, 0xF9, 0x14, 0xCB, 0x3A, 0xCB, 0x1B,
            0xE3, 0xE5, 0xED, 0x52, 0xD1, 0xED, 0xB0, 0xE1, 0x30, 0xC5, 0x87, 0xC0, 0x7E, 0x23, 0x17, 0xC9
        ];

        private static readonly byte[] Zx7BackwardDecoder =
        [
            0xCB, 0x21, 0x00, 0x00, 0x11, 0x00, 0x00, 0x01, 0xAD, 0x00, 0xC5, 0x3E, 0x80, 0xED, 0xA8, 0xCD,
            0xED, 0x11, 0x30, 0xF9, 0xD5, 0x01, 0x00, 0x00, 0x50, 0x14, 0xCD, 0xED, 0x11, 0x30, 0xFA, 0xD4,
            0xED, 0x11, 0xCB, 0x11, 0xCB, 0x10, 0x38, 0x1F, 0x15, 0x20, 0xF4, 0x03, 0x5E, 0x2B, 0xCB, 0x33,
            0x30, 0x0C, 0x16, 0x10, 0xCD, 0xED, 0x11, 0xCB, 0x12, 0x30, 0xF9, 0x14, 0xCB, 0x3A, 0xCB, 0x1B,
            0xE3, 0xE5, 0xED, 0x5A, 0xD1, 0xED, 0xB8, 0xE1, 0x30, 0xC5, 0x87, 0xC0, 0x7E, 0x2B, 0x17, 0xC9
        ];

        private static readonly byte[] Zx7ForwardEmbeddedDecoder =
        [
            0x21, 0x00, 0x00, 0x11, 0x00, 0x00, 0x01, 0xAD, 0x00, 0xC5, 0x3E, 0x80, 0xED, 0xA0, 0x87, 0xCC,
            0x66, 0x11, 0x30, 0xF8, 0xD5, 0x01, 0x01, 0x00, 0x50, 0x14, 0x87, 0xCC, 0x66, 0x11, 0x30, 0xF9,
            0xC3, 0x35, 0x11, 0x87, 0xCC, 0x66, 0x11, 0xCB, 0x11, 0xCB, 0x10, 0x38, 0x2D, 0x15, 0x20, 0xF3,
            0x03, 0x5E, 0x23, 0xCB, 0x33, 0x30, 0x1A, 0x87, 0xCC, 0x66, 0x11, 0xCB, 0x12, 0x87, 0xCC, 0x66,
            0x11, 0xCB, 0x12, 0x87, 0xCC, 0x66, 0x11, 0xCB, 0x12, 0x87, 0xCC, 0x66, 0x11, 0x3F, 0x38, 0x01,
            0x14, 0xCB, 0x1B, 0xE3, 0xE5, 0xED, 0x52, 0xD1, 0xED, 0xB0, 0xE1, 0xD2, 0x16, 0x11, 0x7E, 0x23,
            0x17, 0xC9
        ];

        private static readonly byte[] Zx7BackwardEmbeddedDecoder =
        [
            0x21, 0x00, 0x00, 0x11, 0x00, 0x00, 0x01, 0xAD, 0x00, 0xC5, 0x3E, 0x80, 0xED, 0xA8, 0x87, 0xCC,
            0x66, 0x11, 0x30, 0xF8, 0xD5, 0x01, 0x01, 0x00, 0x50, 0x14, 0x87, 0xCC, 0x66, 0x11, 0x30, 0xF9,
            0xC3, 0x35, 0x11, 0x87, 0xCC, 0x66, 0x11, 0xCB, 0x11, 0xCB, 0x10, 0x38, 0x2D, 0x15, 0x20, 0xF3,
            0x03, 0x5E, 0x2B, 0xCB, 0x33, 0x30, 0x1A, 0x87, 0xCC, 0x66, 0x11, 0xCB, 0x12, 0x87, 0xCC, 0x66,
            0x11, 0xCB, 0x12, 0x87, 0xCC, 0x66, 0x11, 0xCB, 0x12, 0x87, 0xCC, 0x66, 0x11, 0x3F, 0x38, 0x01,
            0x14, 0xCB, 0x1B, 0xE3, 0xE5, 0xED, 0x5A, 0xD1, 0xED, 0xB8, 0xE1, 0xD2, 0x16, 0x11, 0x7E, 0x2B,
            0x17, 0xC9
        ];

        public static string DetectCompression(TapeRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (MatchesLoader(record, Zx0ForwardDecoder, backwards: false, embedded: false))
            {
                return "ZX0 (forward)";
            }
            if (MatchesLoader(record, Zx0BackwardDecoder, backwards: true, embedded: false))
            {
                return "ZX0 (backward)";
            }
            if (MatchesLoader(record, Zx7ForwardDecoder, backwards: false, embedded: false))
            {
                return "ZX7 (forward)";
            }
            if (MatchesLoader(record, Zx7BackwardDecoder, backwards: true, embedded: false))
            {
                return "ZX7 (backward)";
            }
            if (MatchesLoader(record, Zx7ForwardEmbeddedDecoder, backwards: false, embedded: true))
            {
                return "ZX7 (forward, embedded)";
            }
            if (MatchesLoader(record, Zx7BackwardEmbeddedDecoder, backwards: true, embedded: true))
            {
                return "ZX7 (backward, embedded)";
            }
            return "None / unknown";
        }

        private static bool MatchesLoader(
            TapeRecord record,
            ReadOnlySpan<byte> decoderTemplate,
            bool backwards,
            bool embedded)
        {
            byte[]? body = record.Body.MzfBody;
            int loaderSize = embedded ? 0x15 : 0x20 + decoderTemplate.Length;
            if (body == null || body.Length < loaderSize)
            {
                return false;
            }

            int prefixOffset = backwards ? body.Length - loaderSize : 0;
            int expectedExec = backwards
                ? record.Header.MzfStart + prefixOffset
                : record.Header.MzfStart;
            if (expectedExec > ushort.MaxValue || record.Header.MzfExec != expectedExec ||
                !MatchesPrefix(body.AsSpan(prefixOffset), decoderTemplate.Length, embedded))
            {
                return false;
            }

            ReadOnlySpan<byte> decoder = embedded
                ? record.DescriptionRaw.AsSpan(0, decoderTemplate.Length)
                : body.AsSpan(prefixOffset + 0x20, decoderTemplate.Length);
            return MatchesPatchedDecoder(decoder, decoderTemplate, embedded ? 1 : 2);
        }

        private static bool MatchesPrefix(ReadOnlySpan<byte> prefix, int decoderLength, bool embedded)
        {
            if (prefix.Length < (embedded ? 0x15 : 0x20) ||
                prefix[0] != 0x21 || prefix[3] != 0x22 ||
                prefix[4] != 0x02 || prefix[5] != 0x11 ||
                prefix[6] != 0x21 || prefix[9] != 0x22 ||
                prefix[10] != 0x04 || prefix[11] != 0x11 ||
                prefix[12] != 0x21 || prefix[15] != 0x22 ||
                prefix[16] != 0x06 || prefix[17] != 0x11)
            {
                return false;
            }

            if (embedded)
            {
                return prefix[18] == 0xC3 && prefix[19] == 0x08 && prefix[20] == 0x11;
            }

            return prefix[18] == 0x21 &&
                prefix[21] == 0x11 && prefix[22] == 0xA3 && prefix[23] == 0x11 &&
                prefix[24] == 0x01 &&
                BinaryPrimitives.ReadUInt16LittleEndian(prefix[25..27]) == decoderLength &&
                prefix[27] == 0xED && prefix[28] == 0xB0 &&
                prefix[29] == 0xC3 && prefix[30] == 0xA4 && prefix[31] == 0x11;
        }

        private static bool MatchesPatchedDecoder(
            ReadOnlySpan<byte> decoder,
            ReadOnlySpan<byte> template,
            int firstPatchedOperand)
        {
            if (decoder.Length != template.Length)
            {
                return false;
            }

            for (int index = 0; index < template.Length; index++)
            {
                bool patched = index >= firstPatchedOperand && index < firstPatchedOperand + 2 ||
                    index >= firstPatchedOperand + 3 && index < firstPatchedOperand + 5 ||
                    index >= firstPatchedOperand + 6 && index < firstPatchedOperand + 8;
                if (!patched && decoder[index] != template[index])
                {
                    return false;
                }
            }
            return true;
        }

        public static TapeRecord Build(
            TapeRecord source,
            CompressionPayload payload,
            MzfCompressionOptions options)
        {
            if (options.Algorithm is not (MzfCompressionAlgorithm.Zx0 or MzfCompressionAlgorithm.Zx7))
            {
                throw new ArgumentException("A concrete ZX0 or ZX7 algorithm is required.", nameof(options));
            }

            TapeRecord clone = source.DeepClone();
            int originalSize = clone.Header.MzfSize;
            int originalLoad = clone.Header.MzfStart;
            int originalExec = clone.Header.MzfExec;
            int adjustedSize = originalSize - options.SkipBytes;
            bool backwards = options.Direction == CompressionDirection.Backward;
            bool embedded = options.Algorithm == MzfCompressionAlgorithm.Zx7 && options.Zx7EmbeddedLoader;

            byte[] decoder = GetDecoder(options.Algorithm, backwards, embedded);
            int loaderSize = embedded ? 0x15 : 0x20 + decoder.Length;
            int delta = backwards ? payload.Delta : Math.Max(payload.Delta, loaderSize);
            int adjustedLoad = backwards ? originalLoad : originalLoad + options.SkipBytes;
            int packedSize = loaderSize + payload.Data.Length;
            int packedLoad = backwards
                ? originalLoad - delta
                : adjustedLoad + adjustedSize + delta - packedSize;
            int packedExec = backwards ? packedLoad + payload.Data.Length : packedLoad;

            int sourceAddress = backwards
                ? originalLoad - delta + payload.Data.Length - 1
                : adjustedLoad + adjustedSize + delta - payload.Data.Length;
            int targetAddress = backwards
                ? originalLoad + adjustedSize - 1
                : adjustedLoad;

            ValidateAddressLayout(
                originalLoad,
                originalSize,
                packedLoad,
                packedSize,
                packedExec,
                sourceAddress,
                targetAddress,
                originalExec);

            byte[] prefix = BuildPrefix(
                adjustedSize,
                adjustedLoad,
                originalExec,
                packedExec,
                decoder.Length,
                embedded);
            byte[] rawHeader = clone.GetSerializedHeader();

            if (embedded)
            {
                byte[] embeddedDecoder = (byte[])decoder.Clone();
                PatchDecoder(embeddedDecoder, sourceAddress, targetAddress, originalExec, embedded: true);
                embeddedDecoder.CopyTo(rawHeader, 24);
            }
            else
            {
                decoder = (byte[])decoder.Clone();
                PatchDecoder(decoder, sourceAddress, targetAddress, originalExec, embedded: false);
            }

            byte[] packedBody = new byte[packedSize];
            if (backwards)
            {
                payload.Data.CopyTo(packedBody, 0);
                prefix.CopyTo(packedBody, payload.Data.Length);
                if (!embedded)
                {
                    decoder.CopyTo(packedBody, payload.Data.Length + prefix.Length);
                }
            }
            else
            {
                prefix.CopyTo(packedBody, 0);
                if (!embedded)
                {
                    decoder.CopyTo(packedBody, prefix.Length);
                }
                payload.Data.CopyTo(packedBody, loaderSize);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(18, 2), (ushort)packedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(20, 2), (ushort)packedLoad);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(22, 2), (ushort)packedExec);

            MZQFileHeader header = clone.Header;
            header.MzfSize = (ushort)packedSize;
            header.MzfStart = (ushort)packedLoad;
            header.MzfExec = (ushort)packedExec;
            header.MzfHeaderDescription = rawHeader[24..128];

            MZQFileBody body = clone.Body;
            body.DataSize = (ushort)packedSize;
            body.MzfBody = packedBody;
            return new TapeRecord(rawHeader, header, body, clone.Profile, clone.MetadataOrigin);
        }

        private static byte[] BuildPrefix(
            int originalSize,
            int originalLoad,
            int originalExec,
            int loaderAddress,
            int decoderLength,
            bool embedded)
        {
            byte[] loader = new byte[embedded ? 0x15 : 0x20];
            WriteStoreWord(loader, 0x00, originalSize, 0x1102);
            WriteStoreWord(loader, 0x06, originalLoad, 0x1104);
            WriteStoreWord(loader, 0x0C, originalExec, 0x1106);
            if (embedded)
            {
                loader[0x12] = 0xC3;
                loader[0x13] = 0x08;
                loader[0x14] = 0x11;
            }
            else
            {
                loader[0x12] = 0x21;
                WriteWord(loader, 0x13, loaderAddress + 0x20);
                loader[0x15] = 0x11;
                loader[0x16] = 0xA3;
                loader[0x17] = 0x11;
                loader[0x18] = 0x01;
                WriteWord(loader, 0x19, decoderLength);
                loader[0x1B] = 0xED;
                loader[0x1C] = 0xB0;
                loader[0x1D] = 0xC3;
                loader[0x1E] = 0xA4;
                loader[0x1F] = 0x11;
            }
            return loader;
        }

        private static void WriteStoreWord(byte[] target, int offset, int value, int address)
        {
            target[offset] = 0x21;
            WriteWord(target, offset + 1, value);
            target[offset + 3] = 0x22;
            WriteWord(target, offset + 4, address);
        }

        private static void PatchDecoder(
            byte[] decoder,
            int sourceAddress,
            int targetAddress,
            int originalExec,
            bool embedded)
        {
            int firstOperand = embedded ? 1 : 2;
            WriteWord(decoder, firstOperand, sourceAddress);
            WriteWord(decoder, firstOperand + 3, targetAddress);
            WriteWord(decoder, firstOperand + 6, originalExec);
        }

        private static byte[] GetDecoder(
            MzfCompressionAlgorithm algorithm,
            bool backwards,
            bool embedded) => (algorithm, backwards, embedded) switch
        {
            (MzfCompressionAlgorithm.Zx0, false, false) => Zx0ForwardDecoder,
            (MzfCompressionAlgorithm.Zx0, true, false) => Zx0BackwardDecoder,
            (MzfCompressionAlgorithm.Zx7, false, false) => Zx7ForwardDecoder,
            (MzfCompressionAlgorithm.Zx7, true, false) => Zx7BackwardDecoder,
            (MzfCompressionAlgorithm.Zx7, false, true) => Zx7ForwardEmbeddedDecoder,
            (MzfCompressionAlgorithm.Zx7, true, true) => Zx7BackwardEmbeddedDecoder,
            _ => throw new InvalidOperationException("Unsupported compressor/loader combination.")
        };

        private static void ValidateAddressLayout(
            int originalLoad,
            int originalSize,
            int packedLoad,
            int packedSize,
            int packedExec,
            int sourceAddress,
            int targetAddress,
            int originalExec)
        {
            if (originalLoad + originalSize > 0x10000)
            {
                throw new InvalidOperationException("Compression failed: the original LOAD and SIZE exceed the 16-bit MZ address space.");
            }
            if (packedSize <= 0 || packedSize > ushort.MaxValue ||
                packedLoad < 0 || packedLoad > ushort.MaxValue ||
                packedExec < 0 || packedExec > ushort.MaxValue ||
                packedLoad + packedSize > 0x10000 ||
                sourceAddress < 0 || sourceAddress > ushort.MaxValue ||
                targetAddress < 0 || targetAddress > ushort.MaxValue ||
                originalExec < 0 || originalExec > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    "Compression failed: resulting address layout would overflow 16-bit MZ memory.");
            }
        }

        private static void WriteWord(byte[] target, int offset, int value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), checked((ushort)value));
    }
}
