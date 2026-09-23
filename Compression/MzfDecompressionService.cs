using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace QDTool
{
    internal sealed record MzfDecompressionResult(
        TapeRecord Record,
        MzfCompressionInfo Compression,
        bool HeaderDescriptionFullyRestored);

    internal static class MzfDecompressionService
    {
        internal static MzfDecompressionResult Decompress(TapeRecord packed)
        {
            ArgumentNullException.ThrowIfNull(packed);
            if (!MzfLoaderBuilder.TryGetCompressionInfo(packed, out MzfCompressionInfo? detected) ||
                detected == null)
            {
                throw new InvalidOperationException("The record does not contain a recognized MZTools ZX0/ZX7 loader.");
            }

            MzfCompressionInfo info = detected;
            byte[] packedBody = packed.Body.MzfBody;
            if (info.PayloadLength <= 0 || info.PayloadOffset < 0 ||
                info.PayloadOffset + info.PayloadLength > packedBody.Length)
            {
                throw new InvalidDataException("The compressed payload boundaries are invalid.");
            }
            byte[] payload = packedBody.AsSpan(info.PayloadOffset, info.PayloadLength).ToArray();
            bool backwards = info.Direction == CompressionDirection.Backward;
            if (backwards)
            {
                Array.Reverse(payload);
            }

            byte[] restored = info.Algorithm switch
            {
                MzfCompressionAlgorithm.Zx0 => DecompressZx0(payload, info.RestoredSize, backwards),
                MzfCompressionAlgorithm.Zx7 => DecompressZx7(payload, info.RestoredSize),
                _ => throw new InvalidDataException("Unsupported compressed payload algorithm.")
            };
            if (backwards)
            {
                Array.Reverse(restored);
            }
            if (restored.Length != info.RestoredSize)
            {
                throw new InvalidDataException(
                    $"Decompression produced {restored.Length} bytes, expected {info.RestoredSize} bytes.");
            }

            byte[] rawHeader = packed.GetSerializedHeader();
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(18, 2), info.RestoredSize);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(20, 2), info.RestoredLoad);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(22, 2), info.RestoredExec);
            if (info.EmbeddedLoader)
            {
                rawHeader.AsSpan(24, Math.Min(info.DecoderLength, 104)).Clear();
            }

            MZQFileHeader header = packed.Header;
            header.MzfSize = info.RestoredSize;
            header.MzfStart = info.RestoredLoad;
            header.MzfExec = info.RestoredExec;
            header.MzfHeaderDescription = rawHeader[24..128];
            MZQFileBody body = packed.Body;
            body.DataSize = info.RestoredSize;
            body.MzfBody = restored;
            TapeRecord record = new(
                rawHeader,
                header,
                body,
                packed.Profile,
                packed.MetadataOrigin);
            return new MzfDecompressionResult(record, info, !info.EmbeddedLoader);
        }

        private static byte[] DecompressZx7(byte[] input, int expectedSize)
        {
            var reader = new BitReader(input);
            var output = new List<byte>(expectedSize);
            output.Add(reader.ReadByte());
            while (true)
            {
                if (reader.ReadBit() == 0)
                {
                    output.Add(reader.ReadByte());
                    EnsureOutputLimit(output, expectedSize);
                    continue;
                }

                int encodedLength = reader.ReadStandardEliasGamma();
                if (encodedLength == 0x10000)
                {
                    break;
                }
                int length = checked(encodedLength + 1);
                int encodedOffset = reader.ReadByte();
                int offsetMinusOne;
                if ((encodedOffset & 0x80) == 0)
                {
                    offsetMinusOne = encodedOffset;
                }
                else
                {
                    offsetMinusOne = encodedOffset & 0x7F;
                    for (int mask = 1024; mask > 127; mask >>= 1)
                    {
                        if (reader.ReadBit() != 0)
                        {
                            offsetMinusOne |= mask;
                        }
                    }
                    offsetMinusOne += 128;
                }
                CopyMatch(output, offsetMinusOne + 1, length, expectedSize);
            }
            return ValidateFinished(reader, output, expectedSize, "ZX7");
        }

        private static byte[] DecompressZx0(byte[] input, int expectedSize, bool backwards)
        {
            var reader = new BitReader(input);
            var output = new List<byte>(expectedSize);
            int lastOffset = 1;
            ReadLiterals(reader, output, reader.ReadInterlacedEliasGamma(backwards), expectedSize);
            bool afterMatch = false;
            while (true)
            {
                int selector = reader.ReadBit();
                if (selector == 0 && afterMatch)
                {
                    ReadLiterals(reader, output, reader.ReadInterlacedEliasGamma(backwards), expectedSize);
                    afterMatch = false;
                    continue;
                }
                if (selector == 0)
                {
                    CopyMatch(
                        output,
                        lastOffset,
                        reader.ReadInterlacedEliasGamma(backwards),
                        expectedSize);
                    afterMatch = true;
                    continue;
                }

                int offsetHigh = reader.ReadInterlacedEliasGamma(backwards);
                if (offsetHigh == 256)
                {
                    break;
                }
                int offsetByte = reader.ReadByte();
                int offsetLow = backwards
                    ? offsetByte >> 1
                    : 127 - (offsetByte >> 1);
                lastOffset = checked((offsetHigh - 1) * 128 + offsetLow + 1);
                reader.SetPendingBit(offsetByte & 1);
                int length = checked(reader.ReadInterlacedEliasGamma(backwards) + 1);
                CopyMatch(output, lastOffset, length, expectedSize);
                afterMatch = true;
            }
            return ValidateFinished(reader, output, expectedSize, "ZX0");
        }

        private static void ReadLiterals(
            BitReader reader,
            List<byte> output,
            int count,
            int expectedSize)
        {
            if (count <= 0)
            {
                throw new InvalidDataException("Compressed stream contains an invalid literal length.");
            }
            for (int index = 0; index < count; index++)
            {
                output.Add(reader.ReadByte());
                EnsureOutputLimit(output, expectedSize);
            }
        }

        private static void CopyMatch(List<byte> output, int offset, int length, int expectedSize)
        {
            if (offset <= 0 || offset > output.Count || length <= 0)
            {
                throw new InvalidDataException(
                    "Compressed stream requires data outside the stored payload. " +
                    "It may use expert partial/skip compression or be damaged; it cannot be decompressed safely.");
            }
            for (int index = 0; index < length; index++)
            {
                output.Add(output[^offset]);
                EnsureOutputLimit(output, expectedSize);
            }
        }

        private static void EnsureOutputLimit(List<byte> output, int expectedSize)
        {
            if (output.Count > expectedSize)
            {
                throw new InvalidDataException("Decompressed data exceeds the size stored in the loader.");
            }
        }

        private static byte[] ValidateFinished(
            BitReader reader,
            List<byte> output,
            int expectedSize,
            string algorithm)
        {
            if (output.Count != expectedSize)
            {
                throw new InvalidDataException(
                    $"{algorithm} stream ended after {output.Count} bytes; expected {expectedSize} bytes.");
            }
            return output.ToArray();
        }

        private sealed class BitReader
        {
            private readonly byte[] input;
            private int inputIndex;
            private int bitMask;
            private int bitValue;
            private int pendingBit = -1;

            internal BitReader(byte[] input)
            {
                this.input = input;
            }

            internal byte ReadByte()
            {
                if (inputIndex >= input.Length)
                {
                    throw new InvalidDataException("Compressed stream ended unexpectedly.");
                }
                return input[inputIndex++];
            }

            internal int ReadBit()
            {
                if (pendingBit >= 0)
                {
                    int result = pendingBit;
                    pendingBit = -1;
                    return result;
                }
                if (bitMask == 0)
                {
                    bitValue = ReadByte();
                    bitMask = 0x80;
                }
                int bit = (bitValue & bitMask) != 0 ? 1 : 0;
                bitMask >>= 1;
                return bit;
            }

            internal void SetPendingBit(int bit)
            {
                if (pendingBit >= 0)
                {
                    throw new InvalidDataException("Compressed stream has overlapping backtrack bits.");
                }
                pendingBit = bit & 1;
            }

            internal int ReadStandardEliasGamma()
            {
                int leadingZeros = 0;
                while (ReadBit() == 0)
                {
                    if (++leadingZeros > 16)
                    {
                        throw new InvalidDataException("Compressed stream contains an oversized Elias value.");
                    }
                }
                // ZX7 uses 16 zero bits followed by one as a dedicated end marker.
                // Unlike an ordinary gamma-coded 65536, it has no trailing value bits.
                if (leadingZeros == 16)
                {
                    return 0x10000;
                }
                int value = 1;
                for (int index = 0; index < leadingZeros; index++)
                {
                    value = checked((value << 1) | ReadBit());
                }
                return value;
            }

            internal int ReadInterlacedEliasGamma(bool backwards)
            {
                int terminator = backwards ? 0 : 1;
                int value = 1;
                while (ReadBit() != terminator)
                {
                    value = checked((value << 1) | ReadBit());
                    if (value > 0x10000)
                    {
                        throw new InvalidDataException("Compressed stream contains an oversized interlaced Elias value.");
                    }
                }
                return value;
            }
        }
    }
}
