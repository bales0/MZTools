using System;
using System.Collections.Generic;
using System.Threading;

namespace QDTool
{
    internal static class Zx0Compressor
    {
        private const int InitialOffset = 1;
        private const int MaxOffsetZx0 = 32640;
        private const int MaxOffsetQuick = 2176;

        private sealed class Block
        {
            public Block? Chain;
            public int Bits;
            public int Index;
            public int Offset;
            public int Length;
            public int References;
        }

        private sealed class BlockPool
        {
            private readonly Stack<Block> free = new();

            public Block Allocate(int bits, int index, int offset, int length, Block? chain)
            {
                Block block;
                if (free.Count > 0)
                {
                    block = free.Pop();
                    if (block.Chain != null)
                    {
                        Release(block.Chain);
                    }
                }
                else
                {
                    block = new Block();
                }

                block.Bits = bits;
                block.Index = index;
                block.Offset = offset;
                block.Length = length;
                block.Chain = chain;
                block.References = 0;
                if (chain != null)
                {
                    chain.References++;
                }
                return block;
            }

            public void Assign(ref Block? destination, Block value)
            {
                value.References++;
                if (destination != null)
                {
                    Release(destination);
                }
                destination = value;
            }

            private void Release(Block block)
            {
                block.References--;
                if (block.References == 0)
                {
                    free.Push(block);
                }
            }
        }

        public static CompressionPayload Compress(
            byte[] source,
            int skip,
            bool backwards,
            bool quick,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Length == 0)
            {
                throw new ArgumentException("ZX0 cannot compress an empty input.", nameof(source));
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

            Block optimal = Optimize(
                input,
                skip,
                quick ? MaxOffsetQuick : MaxOffsetZx0,
                cancellationToken);
            CompressionPayload result = Encode(optimal, input, skip, backwards);
            if (backwards)
            {
                Array.Reverse(result.Data);
            }
            return result;
        }

        private static Block Optimize(
            byte[] input,
            int skip,
            int offsetLimit,
            CancellationToken cancellationToken)
        {
            int maxOffset = OffsetCeiling(input.Length - 1, offsetLimit);
            var lastLiteral = new Block?[maxOffset + 1];
            var lastMatch = new Block?[maxOffset + 1];
            var optimal = new Block?[input.Length + 1];
            var matchLength = new int[maxOffset + 1];
            var bestLength = new int[Math.Max(input.Length + 1, 3)];
            bestLength[2] = 2;
            var pool = new BlockPool();

            Block fake = pool.Allocate(-1, skip - 1, InitialOffset, 0, null);
            pool.Assign(ref lastMatch[InitialOffset], fake);

            for (int index = skip; index < input.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bestLengthSize = 2;
                maxOffset = OffsetCeiling(index, offsetLimit);
                for (int offset = 1; offset <= maxOffset; offset++)
                {
                    if (index >= offset && input[index] == input[index - offset])
                    {
                        if (lastLiteral[offset] != null)
                        {
                            int length = index - lastLiteral[offset]!.Index;
                            int bits = lastLiteral[offset]!.Bits + 1 + EliasGammaBits(length);
                            Block block = pool.Allocate(bits, index, offset, length, lastLiteral[offset]);
                            pool.Assign(ref lastMatch[offset], block);
                            if (optimal[index] == null || optimal[index]!.Bits > bits)
                            {
                                pool.Assign(ref optimal[index], lastMatch[offset]!);
                            }
                        }

                        matchLength[offset]++;
                        if (matchLength[offset] > 1)
                        {
                            if (bestLengthSize < matchLength[offset])
                            {
                                int bestBits = optimal[index - bestLength[bestLengthSize]]!.Bits +
                                    EliasGammaBits(bestLength[bestLengthSize] - 1);
                                do
                                {
                                    bestLengthSize++;
                                    int bits2 = optimal[index - bestLengthSize]!.Bits +
                                        EliasGammaBits(bestLengthSize - 1);
                                    if (bits2 <= bestBits)
                                    {
                                        bestLength[bestLengthSize] = bestLengthSize;
                                        bestBits = bits2;
                                    }
                                    else
                                    {
                                        bestLength[bestLengthSize] = bestLength[bestLengthSize - 1];
                                    }
                                }
                                while (bestLengthSize < matchLength[offset]);
                            }

                            int length = bestLength[matchLength[offset]];
                            int bits = optimal[index - length]!.Bits + 8 +
                                EliasGammaBits((offset - 1) / 128 + 1) +
                                EliasGammaBits(length - 1);
                            if (lastMatch[offset] == null ||
                                lastMatch[offset]!.Index != index ||
                                lastMatch[offset]!.Bits > bits)
                            {
                                Block block = pool.Allocate(bits, index, offset, length, optimal[index - length]);
                                pool.Assign(ref lastMatch[offset], block);
                                if (optimal[index] == null || optimal[index]!.Bits > bits)
                                {
                                    pool.Assign(ref optimal[index], lastMatch[offset]!);
                                }
                            }
                        }
                    }
                    else
                    {
                        matchLength[offset] = 0;
                        if (lastMatch[offset] != null)
                        {
                            int length = index - lastMatch[offset]!.Index;
                            int bits = lastMatch[offset]!.Bits + 1 + EliasGammaBits(length) + length * 8;
                            Block block = pool.Allocate(bits, index, 0, length, lastMatch[offset]);
                            pool.Assign(ref lastLiteral[offset], block);
                            if (optimal[index] == null || optimal[index]!.Bits > bits)
                            {
                                pool.Assign(ref optimal[index], lastLiteral[offset]!);
                            }
                        }
                    }
                }
            }

            return optimal[input.Length - 1]
                ?? throw new InvalidOperationException("ZX0 optimizer did not produce an output chain.");
        }

        private static int OffsetCeiling(int index, int offsetLimit) =>
            index > offsetLimit ? offsetLimit : index < InitialOffset ? InitialOffset : index;

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

        private static CompressionPayload Encode(
            Block optimal,
            byte[] input,
            int skip,
            bool backwards)
        {
            int outputSize = (optimal.Bits + 18 + 7) / 8;
            var writer = new BitWriter(outputSize, outputSize - input.Length + skip);

            Block? next = null;
            while (optimal != null)
            {
                Block? previous = optimal.Chain;
                optimal.Chain = next;
                next = optimal;
                optimal = previous!;
            }

            int inputIndex = skip;
            bool first = true;
            int lastOffset = InitialOffset;
            for (Block? block = next!.Chain; block != null; block = block.Chain)
            {
                if (block.Offset == 0)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        writer.WriteBit(0);
                    }
                    writer.WriteInterlacedEliasGamma(block.Length, backwards);
                    for (int i = 0; i < block.Length; i++)
                    {
                        writer.WriteByte(input[inputIndex]);
                        inputIndex++;
                        writer.ReadBytes(1);
                    }
                }
                else if (block.Offset == lastOffset)
                {
                    writer.WriteBit(0);
                    writer.WriteInterlacedEliasGamma(block.Length, backwards);
                    inputIndex += block.Length;
                    writer.ReadBytes(block.Length);
                }
                else
                {
                    writer.WriteBit(1);
                    writer.WriteInterlacedEliasGamma((block.Offset - 1) / 128 + 1, backwards);
                    writer.WriteByte(backwards
                        ? ((block.Offset - 1) % 128) << 1
                        : (255 - ((block.Offset - 1) % 128)) << 1);
                    writer.Backtrack = true;
                    writer.WriteInterlacedEliasGamma(block.Length - 1, backwards);
                    inputIndex += block.Length;
                    writer.ReadBytes(block.Length);
                    lastOffset = block.Offset;
                }
            }

            writer.WriteBit(1);
            writer.WriteInterlacedEliasGamma(256, backwards);
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
            public bool Backtrack { get; set; }

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
                if (Backtrack)
                {
                    if (value != 0)
                    {
                        Output[outputIndex - 1] |= 1;
                    }
                    Backtrack = false;
                    return;
                }

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

            public void WriteInterlacedEliasGamma(int value, bool backwards)
            {
                int i;
                for (i = 2; i <= value; i <<= 1)
                {
                }
                i >>= 1;
                while ((i >>= 1) > 0)
                {
                    WriteBit(backwards ? 1 : 0);
                    WriteBit(value & i);
                }
                WriteBit(backwards ? 0 : 1);
            }
        }
    }
}
