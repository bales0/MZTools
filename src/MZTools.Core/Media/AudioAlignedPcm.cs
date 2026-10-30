using System;
using System.Threading;

namespace MZTools;

// Coordinates remain on the original LEFT time axis: R'(t) = R(t - shift).
// A bounded delay buffer retains PCM, never bucket extrema, before mixing.
internal sealed record AudioPcmTransform(int RightShiftSamples = 0, bool InvertRight = false, bool Mix = false)
{
    internal string Description => $"R shift {RightShiftSamples:+0;-0;0} samples; invert R {InvertRight}; {(Mix ? "Mix (L + R) / 2" : "separate channels")}";
    internal void Validate(PcmAudioFormat format)
    {
        if (Math.Abs((long)RightShiftSamples) > format.SampleRate * 2L) throw new ArgumentException("Right shift must be within ±2 seconds.");
        if (format.Channels != 2 && (RightShiftSamples != 0 || InvertRight || Mix)) throw new ArgumentException("Alignment, right polarity and mix require stereo audio.");
    }
    internal (long Start, long End) Bounds(PcmAudioFormat format, long start, long end)
    {
        Validate(format);
        if (start < 0 || end <= start || end > format.FrameCount) throw new ArgumentOutOfRangeException(nameof(start));
        long first = Math.Max(start, Math.Max(0, RightShiftSamples)), last = Math.Min(end, format.FrameCount + Math.Min(0, RightShiftSamples));
        if (last <= first) throw new ArgumentException("The channels have no common samples in this interval after alignment.");
        return (first, last);
    }
    internal void Visit(IPcmAudioStreamReader reader, long start, long end, Action<long, int, int> consume, CancellationToken token)
    {
        (start, end) = Bounds(reader.Format, start, end);
        int delay = Math.Abs(RightShiftSamples);
        var ring = new int[delay + 1];
        long firstRead = RightShiftSamples > 0 ? start - delay : start;
        long lastRead = RightShiftSamples < 0 ? end + delay : end;
        reader.ReadFrames(firstRead, lastRead - firstRead, (sample, left, right) =>
        {
            token.ThrowIfCancellationRequested();
            long time = RightShiftSamples < 0 ? sample - delay : sample;
            int slot = (int)(sample % ring.Length);
            ring[slot] = RightShiftSamples < 0 ? left : right;
            if (time < start || time >= end) return;
            if (delay > 0)
            {
                int past = ring[(int)((sample - delay) % ring.Length)];
                if (RightShiftSamples < 0) left = past; else right = past;
            }
            // Inverting -8388608 yields +8388608, so saturate to the PCM range.
            if (InvertRight) right = (int)Math.Clamp(-(long)right, -8388608, 8388607);
            if (Mix) left = right = (int)(((long)left + right) / 2);
            consume(time, left, right);
        });
    }
}
