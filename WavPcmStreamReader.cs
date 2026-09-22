using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.IO;

namespace QDTool
{
    internal readonly record struct PcmAudioFormat(
        ushort Channels,
        uint SampleRate,
        ushort BitsPerSample,
        ushort BlockAlign,
        long DataOffset,
        long DataLength)
    {
        internal long FrameCount => DataLength / BlockAlign;
    }

    internal interface IPcmAudioStreamReader : IDisposable
    {
        PcmAudioFormat Format { get; }
        string SourceFormat { get; }
        void ReadFrames(Action<long, int, int> consume);
        void ReadFrames(long firstFrame, long frameCount, Action<long, int, int> consume);
    }

    internal sealed class WavPcmStreamReader : IPcmAudioStreamReader
    {
        private static readonly HashSet<uint> SupportedSampleRates =
            [22050, 44100, 88200, 96000];

        private readonly FileStream stream;
        public PcmAudioFormat Format { get; }
        public string SourceFormat => "WAV";

        internal WavPcmStreamReader(string filePath)
        {
            stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.SequentialScan);
            try
            {
                Format = ReadFormat(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void ReadFrames(Action<long, int, int> consume) =>
            ReadFrames(0, Format.FrameCount, consume);

        public void ReadFrames(long firstFrame, long frameCount, Action<long, int, int> consume)
        {
            ArgumentNullException.ThrowIfNull(consume);
            if (firstFrame < 0 || frameCount < 0 || firstFrame > Format.FrameCount - frameCount)
            {
                throw new ArgumentOutOfRangeException(nameof(firstFrame));
            }

            int blockAlign = Format.BlockAlign;
            int bufferLength = Math.Max(blockAlign, 65536 - (65536 % blockAlign));
            byte[] buffer = new byte[bufferLength];
            stream.Position = checked(Format.DataOffset + (firstFrame * blockAlign));
            long bytesRemaining = checked(frameCount * blockAlign);
            long frameIndex = firstFrame;

            while (bytesRemaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, bytesRemaining);
                ReadExactly(stream, buffer.AsSpan(0, requested), "The WAV data chunk is truncated.");
                for (int offset = 0; offset < requested; offset += blockAlign)
                {
                    int left = DecodeSample(buffer.AsSpan(offset), Format.BitsPerSample);
                    int right = Format.Channels == 2
                        ? DecodeSample(buffer.AsSpan(offset + (Format.BitsPerSample / 8)), Format.BitsPerSample)
                        : left;
                    consume(frameIndex++, left, right);
                }
                bytesRemaining -= requested;
            }
        }

        public void Dispose() => stream.Dispose();

        private static PcmAudioFormat ReadFormat(FileStream stream)
        {
            Span<byte> riff = stackalloc byte[12];
            ReadExactly(stream, riff, "The WAV header is truncated.");
            if (!riff[..4].SequenceEqual("RIFF"u8) || !riff[8..].SequenceEqual("WAVE"u8))
            {
                throw new InvalidDataException("WAV format unsupported: the file is not RIFF/WAVE.");
            }

            ushort audioFormat = 0;
            ushort channels = 0;
            ushort bits = 0;
            ushort blockAlign = 0;
            uint sampleRate = 0;
            long dataOffset = -1;
            long dataLength = 0;
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> format = stackalloc byte[16];

            while (stream.Position <= stream.Length - chunkHeader.Length)
            {
                ReadExactly(stream, chunkHeader, "The WAV chunk header is truncated.");
                uint unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
                long length = unsignedLength;
                long chunkStart = stream.Position;
                if (length > stream.Length - chunkStart)
                {
                    throw new InvalidDataException("WAV damaged: a chunk extends beyond the end of the file.");
                }

                if (chunkHeader[..4].SequenceEqual("fmt "u8))
                {
                    if (length < 16)
                    {
                        throw new InvalidDataException("WAV damaged: the fmt chunk is shorter than 16 bytes.");
                    }
                    ReadExactly(stream, format, "The WAV fmt chunk is truncated.");
                    audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(format);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                }
                else if (chunkHeader[..4].SequenceEqual("data"u8) && dataOffset < 0)
                {
                    dataOffset = chunkStart;
                    dataLength = length;
                }

                long nextChunk = chunkStart + length;
                // RIFF specifies word padding, but some otherwise valid writers
                // (including older QDTool output) omit the pad after the final
                // odd-sized data chunk. There is no following chunk to misalign.
                if ((length & 1) != 0 && nextChunk < stream.Length)
                {
                    nextChunk++;
                }
                stream.Position = nextChunk;
            }

            if (audioFormat != 1)
            {
                throw new InvalidDataException(
                    $"WAV format unsupported: compression format {audioFormat} is not PCM.");
            }
            if (channels is not 1 and not 2)
            {
                throw new InvalidDataException(
                    $"WAV format unsupported: {channels} channels; only mono and stereo are supported.");
            }
            if (bits is not 8 and not 16 and not 24)
            {
                throw new InvalidDataException(
                    $"Unsupported WAV bit depth: {bits}; expected 8, 16, or 24-bit PCM.");
            }
            if (!SupportedSampleRates.Contains(sampleRate))
            {
                throw new InvalidDataException(
                    $"Unsupported WAV sample rate: {sampleRate} Hz; expected 22050, 44100, 88200, or 96000 Hz.");
            }

            ushort expectedAlign = checked((ushort)(channels * (bits / 8)));
            if (blockAlign != expectedAlign)
            {
                throw new InvalidDataException(
                    $"WAV damaged: block alignment is {blockAlign}, expected {expectedAlign}.");
            }
            if (dataOffset < 0 || dataLength == 0)
            {
                throw new InvalidDataException("WAV damaged: a non-empty data chunk was not found.");
            }
            if ((dataLength % blockAlign) != 0)
            {
                throw new InvalidDataException("WAV damaged: the data chunk ends inside a PCM frame.");
            }

            return new PcmAudioFormat(
                channels,
                sampleRate,
                bits,
                blockAlign,
                dataOffset,
                dataLength);
        }

        private static int DecodeSample(ReadOnlySpan<byte> source, ushort bits) => bits switch
        {
            8 => (source[0] - 128) << 16,
            16 => BinaryPrimitives.ReadInt16LittleEndian(source) << 8,
            24 => SignExtend24(source),
            _ => throw new InvalidOperationException("Unsupported PCM sample width.")
        };

        private static int SignExtend24(ReadOnlySpan<byte> source)
        {
            int value = source[0] | (source[1] << 8) | (source[2] << 16);
            return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
        }

        private static void ReadExactly(Stream stream, Span<byte> destination, string error)
        {
            int readTotal = 0;
            while (readTotal < destination.Length)
            {
                int read = stream.Read(destination[readTotal..]);
                if (read == 0)
                {
                    throw new InvalidDataException(error);
                }
                readTotal += read;
            }
        }
    }
}
