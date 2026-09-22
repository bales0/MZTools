using NAudio.SoundFile;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace QDTool
{
    internal sealed class FlacPcmStreamReader : IPcmAudioStreamReader
    {
        private static readonly HashSet<uint> SupportedSampleRates =
            [22050, 44100, 88200, 96000];

        private readonly SoundFileReader reader;
        public PcmAudioFormat Format { get; }
        public string SourceFormat => "FLAC";

        internal FlacPcmStreamReader(string filePath)
        {
            PcmAudioFormat streamInfo = ReadStreamInfo(filePath);
            try
            {
                reader = new SoundFileReader(filePath);
            }
            catch (DllNotFoundException exception)
            {
                throw new InvalidDataException(
                    "FLAC decoder is unavailable: the bundled libsndfile runtime could not be loaded.",
                    exception);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"FLAC could not be opened: {exception.Message}", exception);
            }

            try
            {
                if (reader.WaveFormat.Channels != streamInfo.Channels ||
                    reader.WaveFormat.SampleRate != streamInfo.SampleRate)
                {
                    throw new InvalidDataException(
                        "FLAC decoder output does not match the STREAMINFO metadata.");
                }
                if (!reader.CanSeek)
                {
                    throw new InvalidDataException(
                        "FLAC source is not seekable; selective recovery requires a seekable file.");
                }
                Format = streamInfo;
            }
            catch
            {
                reader.Dispose();
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

            int channels = Format.Channels;
            reader.Position = checked(firstFrame * channels * sizeof(float));
            float[] buffer = new float[8192 * channels];
            long framesRemaining = frameCount;
            long frameIndex = firstFrame;
            while (framesRemaining > 0)
            {
                int requestedSamples = checked((int)Math.Min(
                    buffer.Length,
                    framesRemaining * channels));
                int samplesRead = reader.Read(buffer.AsSpan(0, requestedSamples));
                if (samplesRead == 0)
                {
                    throw new InvalidDataException("FLAC is truncated before its declared sample count.");
                }
                if ((samplesRead % channels) != 0)
                {
                    throw new InvalidDataException("FLAC decoder returned an incomplete PCM frame.");
                }

                int framesRead = samplesRead / channels;
                for (int frame = 0; frame < framesRead; frame++)
                {
                    int offset = frame * channels;
                    int left = ToInt24(buffer[offset]);
                    int right = channels == 2 ? ToInt24(buffer[offset + 1]) : left;
                    consume(frameIndex++, left, right);
                }
                framesRemaining -= framesRead;
            }
        }

        public void Dispose() => reader.Dispose();

        private static PcmAudioFormat ReadStreamInfo(string filePath)
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[4];
            ReadExactly(stream, signature, "FLAC header is truncated.");
            if (!signature.SequenceEqual("fLaC"u8))
            {
                throw new InvalidDataException("FLAC format unsupported: native fLaC signature was not found.");
            }

            Span<byte> metadataHeader = stackalloc byte[4];
            Span<byte> streamInfo = stackalloc byte[34];
            while (true)
            {
                ReadExactly(stream, metadataHeader, "FLAC metadata header is truncated.");
                bool isLast = (metadataHeader[0] & 0x80) != 0;
                int type = metadataHeader[0] & 0x7F;
                int length = (metadataHeader[1] << 16) |
                    (metadataHeader[2] << 8) |
                    metadataHeader[3];
                if (type == 0)
                {
                    if (length != streamInfo.Length)
                    {
                        throw new InvalidDataException("FLAC STREAMINFO metadata has an invalid length.");
                    }
                    ReadExactly(stream, streamInfo, "FLAC STREAMINFO metadata is truncated.");
                    break;
                }
                if (length > stream.Length - stream.Position)
                {
                    throw new InvalidDataException("FLAC metadata block extends beyond the end of the file.");
                }
                stream.Position += length;
                if (isLast)
                {
                    throw new InvalidDataException("FLAC STREAMINFO metadata was not found.");
                }
            }

            ulong packed = BinaryPrimitives.ReadUInt64BigEndian(streamInfo[10..18]);
            uint sampleRate = (uint)((packed >> 44) & 0xFFFFF);
            ushort channels = (ushort)(((packed >> 41) & 0x07) + 1);
            ushort bits = (ushort)(((packed >> 36) & 0x1F) + 1);
            long frames = checked((long)(packed & 0xFFFFFFFFF));
            if (channels is not 1 and not 2)
            {
                throw new InvalidDataException(
                    $"FLAC format unsupported: {channels} channels; only mono and stereo are supported.");
            }
            if (bits is not 8 and not 16 and not 24)
            {
                throw new InvalidDataException(
                    $"Unsupported FLAC bit depth: {bits}; expected 8, 16, or 24 bit.");
            }
            if (!SupportedSampleRates.Contains(sampleRate))
            {
                throw new InvalidDataException(
                    $"Unsupported FLAC sample rate: {sampleRate} Hz; expected 22050, 44100, 88200, or 96000 Hz.");
            }
            if (frames <= 0)
            {
                throw new InvalidDataException("FLAC STREAMINFO does not declare a positive sample count.");
            }

            ushort blockAlign = checked((ushort)(channels * ((bits + 7) / 8)));
            return new PcmAudioFormat(
                channels,
                sampleRate,
                bits,
                blockAlign,
                0,
                checked(frames * blockAlign));
        }

        private static int ToInt24(float sample)
        {
            if (float.IsNaN(sample))
            {
                return 0;
            }
            double scaled = Math.Clamp((double)sample, -1.0, 1.0) * 8388608.0;
            return (int)Math.Clamp(
                Math.Round(scaled, MidpointRounding.AwayFromZero),
                -8388608,
                8388607);
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
