using System;
using System.IO;
using System.Threading;

namespace MZTools;

internal sealed record AudioSelectionWaveExport(long StartSample, long EndSample, int Channels, uint SampleRate, ushort BitsPerSample);

internal static class AudioSelectionWaveExporter
{
    // Export measured PCM, never a min/max envelope or reconstructed tape bytes.
    internal static AudioSelectionWaveExport Export(AudioSignalAnalysis source, string destination, long start, long end,
        AudioPcmTransform transform, int channelMode, IProgress<WavAnalysisProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); AudioRangeInspection.VerifySource(source);
        string target = Path.GetFullPath(destination);
        if (string.Equals(target, Path.GetFullPath(source.Source), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different output file; the source recording cannot be overwritten.");
        using var reader = AudioSignalAnalysisService.OpenReader(source.Source);
        if (channelMode is < 0 or > 2) throw new ArgumentException("Select Left, Right, stereo or Average.");
        if (reader.Format.Channels == 1 && channelMode == 1) throw new ArgumentException("This recording has no right channel.");
        (start, end) = transform.Bounds(reader.Format, start, end);
        int channels = reader.Format.Channels == 2 && !transform.Mix && channelMode == 2 ? 2 : 1;
        ushort bits = reader.Format.BitsPerSample;
        int bytesPerSample = bits / 8, blockAlign = channels * bytesPerSample;
        long frames = end - start, dataBytes = checked(frames * blockAlign), padding = dataBytes & 1;
        if (dataBytes + padding > uint.MaxValue - 36L) throw new ArgumentException("The selection exceeds the WAV RIFF size limit. Export a shorter interval.");
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".mztools-selection-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            progress?.Report(new("Export selection to WAV", 0, 0, frames, 0));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write("RIFF"u8); writer.Write((uint)(36 + dataBytes + padding)); writer.Write("WAVEfmt "u8);
                writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)channels); writer.Write(reader.Format.SampleRate);
                writer.Write(checked(reader.Format.SampleRate * (uint)blockAlign)); writer.Write((ushort)blockAlign); writer.Write(bits);
                writer.Write("data"u8); writer.Write((uint)dataBytes); writer.Flush();
                var buffer = new byte[65536 - 65536 % blockAlign]; int used = 0; long written = 0;
                void Sample(int value)
                {
                    // Readers normalize PCM to signed 24 bit. Round transformed averages
                    // back to the source resolution, saturating at its signed limits.
                    int scale = 1 << (24 - bits), limit = 1 << (bits - 1);
                    int quantized = (int)Math.Clamp(Math.Round(value / (double)scale, MidpointRounding.AwayFromZero), -limit, limit - 1);
                    if (bits == 8) buffer[used++] = (byte)(quantized + 128);
                    else { buffer[used++] = (byte)quantized; buffer[used++] = (byte)(quantized >> 8); if (bits == 24) buffer[used++] = (byte)(quantized >> 16); }
                }
                transform.Visit(reader, start, end, (_, left, right) =>
                {
                    if (channels == 2) { Sample(left); Sample(right); } else Sample(channelMode == 1 && !transform.Mix ? right : left);
                    if (used == buffer.Length) { stream.Write(buffer, 0, used); used = 0; }
                    written++;
                    if (written % 16384 == 0) progress?.Report(new("Export selection to WAV", written / (double)frames, written, frames, 0));
                }, token);
                if (written != frames) throw new IOException("The source did not supply the complete selected interval.");
                stream.Write(buffer, 0, used); if (padding != 0) stream.WriteByte(0);
            }
            token.ThrowIfCancellationRequested(); AudioRangeInspection.VerifySource(source);
            File.Move(temporary, target, overwrite: true);
            progress?.Report(new("WAV export complete", 1, frames, frames, 0));
            return new(start, end, channels, reader.Format.SampleRate, bits);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
