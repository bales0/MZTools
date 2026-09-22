using System;
using System.Threading;

namespace QDTool
{
    internal static class Zx7Compressor
    {
        private const int MaxOffset = 2176;
        private const int MaxLength = 65536;

        private sealed class Optimal
        {
            public int Bits;
            public int Offset;
            public int Length;
        }

        public static CompressionPayload Compress(
            byte[] source,
            int skip,
            bool backwards,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Length == 0)
            {
                throw new ArgumentException("ZX7 cannot compress an empty input.", nameof(source));
            }
            if (skip < 0 || skip >= source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(skip), "Skip must leave at least one byte to compress.");
            }

            byte[] input = (byte[])source.Clone();
            if (backwards)
            {
                Array.Reverse(input);
            }

            Optimal[] optimal = Optimize(input, skip, cancellationToken);
            CompressionPayload result = Encode(optimal, input, skip);
            if (backwards)
            {
                Array.Reverse(result.Data);
            }
            return result;
        }

        private static Optimal[] Optimize(byte[] input, int skip, CancellationToken cancellationToken)
        {
            var minimum = new int[MaxOffset + 1];
            var maximum = new int[MaxOffset + 1];
            var matches = new int[256 * 256];
            var matchSlots = new int[input.Length];
            var optimal = new Optimal[input.Length];
            for (int index = 0; index < optimal.Length; index++)
            {
                optimal[index] = new Optimal();
            }

            int i;
            for (i = 1; i <= skip; i++)
            {
                int matchIndex = input[i - 1] << 8 | input[i];
                matchSlots[i] = matches[matchIndex];
                matches[matchIndex] = i;
            }

            optimal[skip].Bits = 8;
            for (; i < input.Length; i++)
            {
                if ((i & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                optimal[i].Bits = optimal[i - 1].Bits + 9;
                int matchIndex = input[i - 1] << 8 | input[i];
                int bestLength = 1;
                int match = matches[matchIndex];
                int matchLinkOwner = -1;
                while (match != 0 && bestLength < MaxLength)
                {
                    int offset = i - match;
                    if (offset > MaxOffset)
                    {
                        if (matchLinkOwner < 0)
                        {
                            matches[matchIndex] = 0;
                        }
                        else
                        {
                            matchSlots[matchLinkOwner] = 0;
                        }
                        break;
                    }

                    int length;
                    for (length = 2; length <= MaxLength && i >= skip + length; length++)
                    {
                        if (length > bestLength)
                        {
                            bestLength = length;
                            int bits = optimal[i - length].Bits + CountBits(offset, length);
                            if (optimal[i].Bits > bits)
                            {
                                optimal[i].Bits = bits;
                                optimal[i].Offset = offset;
                                optimal[i].Length = length;
                            }
                        }
                        else if (maximum[offset] != 0 && i + 1 == maximum[offset] + length)
                        {
                            length = i - minimum[offset];
                            if (length > bestLength)
                            {
                                length = bestLength;
                            }
                        }

                        if (i < offset + length || input[i - length] != input[i - length - offset])
                        {
                            break;
                        }
                    }
                    minimum[offset] = i + 1 - length;
                    maximum[offset] = i;
                    matchLinkOwner = match;
                    match = matchSlots[match];
                }
                matchSlots[i] = matches[matchIndex];
                matches[matchIndex] = i;
            }
            return optimal;
        }

        private static int CountBits(int offset, int length) =>
            1 + (offset > 128 ? 12 : 8) + EliasGammaBits(length - 1);

        private static int EliasGammaBits(int value)
        {
            int bits = 1;
            while (value > 1)
            {
                bits += 2;
                value >>= 1;
            }
            return bits;
        }

        private static CompressionPayload Encode(Optimal[] optimal, byte[] input, int skip)
        {
            int inputIndex = input.Length - 1;
            int outputSize = (optimal[inputIndex].Bits + 18 + 7) / 8;
            var writer = new BitWriter(outputSize, outputSize - input.Length + skip);

            optimal[inputIndex].Bits = 0;
            while (inputIndex != skip)
            {
                int inputPrevious = inputIndex -
                    (optimal[inputIndex].Length > 0 ? optimal[inputIndex].Length : 1);
                optimal[inputPrevious].Bits = inputIndex;
                inputIndex = inputPrevious;
            }

            writer.WriteByte(input[inputIndex]);
            writer.ReadBytes(1);
            while ((inputIndex = optimal[inputIndex].Bits) > 0)
            {
                if (optimal[inputIndex].Length == 0)
                {
                    writer.WriteBit(0);
                    writer.WriteByte(input[inputIndex]);
                    writer.ReadBytes(1);
                }
                else
                {
                    writer.WriteBit(1);
                    writer.WriteEliasGamma(optimal[inputIndex].Length - 1);
                    int offset = optimal[inputIndex].Offset - 1;
                    if (offset < 128)
                    {
                        writer.WriteByte(offset);
                    }
                    else
                    {
                        offset -= 128;
                        writer.WriteByte((offset & 127) | 128);
                        for (int mask = 1024; mask > 127; mask >>= 1)
                        {
                            writer.WriteBit(offset & mask);
                        }
                    }
                    writer.ReadBytes(optimal[inputIndex].Length);
                }
            }

            writer.WriteBit(1);
            for (int i = 0; i < 16; i++)
            {
                writer.WriteBit(0);
            }
            writer.WriteBit(1);
            return new CompressionPayload(writer.Output, writer.Delta);
        }

        private sealed class BitWriter
        {
            private int outputIndex;
            private int bitIndex;
            private int bitMask;
            private int difference;

            public BitWriter(int outputSize, int initialDifference)
            {
                Output = new byte[outputSize];
                difference = initialDifference;
            }

            public byte[] Output { get; }
            public int Delta { get; private set; }

            public void ReadBytes(int count)
            {
                difference += count;
                if (difference > Delta)
                {
                    Delta = difference;
                }
            }

            public void WriteByte(int value)
            {
                Output[outputIndex++] = (byte)value;
                difference--;
            }

            public void WriteBit(int value)
            {
                if (bitMask == 0)
                {
                    bitMask = 128;
                    bitIndex = outputIndex;
                    WriteByte(0);
                }
                if (value != 0)
                {
                    Output[bitIndex] |= (byte)bitMask;
                }
                bitMask >>= 1;
            }

            public void WriteEliasGamma(int value)
            {
                int i;
                for (i = 2; i <= value; i <<= 1)
                {
                    WriteBit(0);
                }
                while ((i >>= 1) > 0)
                {
                    WriteBit(value & i);
                }
            }
        }
    }
}
