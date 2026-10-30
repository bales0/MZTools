using NAudio.SoundFile;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MZTools
{
    internal sealed class FlacPcmStreamReader : IPcmAudioStreamReader
    {
        private static readonly HashSet<uint> SupportedSampleRates =
            [22050, 44100, 48000, 88200, 96000];

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

        private static PcmAudioFormat ReadStreamInfo(string filePath)=>FlacStreamInfo.Read(filePath);


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
