using System.Buffers.Binary;

namespace MZTools.Capture;

public sealed record AudioPcmConversion(AudioCaptureFormat Source, AudioCaptureFormat Output,
    bool ChangesContainer, bool MayLosePrecision, string Method);
public readonly record struct AudioPcmConversionCounts(long Samples, long SaturatedSamples);

/// <summary>No resampling, filtering, normalization, dither or channel mixing.</summary>
public static class AudioPcmQuantizer
{
    public static AudioPcmConversion Plan(AudioCaptureFormat source, bool allowPcm24Conversion,bool preserveNativeFormat=false)
    {
        if(preserveNativeFormat) return new(source,source,false,false,"Byte-exact delivered native stream; no format conversion or resampling. Driver format does not certify physical ADC resolution.");
        if (source.Representation is not (AudioSampleRepresentation.Float32 or AudioSampleRepresentation.Signed32))
            return new(source, source, false, false, "Byte-exact delivered integer PCM; no conversion");
        if (!allowPcm24Conversion)
            throw new NotSupportedException("The input supplies float32/PCM32. Explicitly enable conversion to PCM24 for the existing analyzer.");
        var output = new AudioCaptureFormat(source.SampleRate, source.Channels, AudioSampleRepresentation.Signed24,
            source.Representation == AudioSampleRepresentation.Float32 ? 24 : Math.Min(24, source.ValidBitsPerSample));
        return new(source, output, true, source.Representation == AudioSampleRepresentation.Float32 || source.ValidBitsPerSample > 24,
            "PCM24: normalized float * 8388608 or signed PCM32 / 256 (unused integer padding bits ignored); nearest, midpoint away from zero; saturate [-8388608,8388607]; no dither; non-finite floats rejected");
    }

    public static AudioPcmConversionCounts Convert(AudioPcmConversion conversion, ReadOnlySpan<byte> source, Span<byte> output)
    {
        if (source.Length % conversion.Source.BlockAlign != 0) throw new ArgumentException("Incomplete source PCM frame.");
        int frames = source.Length / conversion.Source.BlockAlign;
        int bytes = checked(frames * conversion.Output.BlockAlign);
        if (output.Length < bytes) throw new ArgumentException("Output PCM buffer is too small.");
        if (!conversion.ChangesContainer)
        { source.CopyTo(output); return new((long)frames * conversion.Source.Channels, 0); }
        long saturated = 0;
        for (int i = 0, offset = 0; i < source.Length; i += 4, offset += 3)
        {
            double scaled;
            if (conversion.Source.Representation == AudioSampleRepresentation.Float32)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(source[i..]);
                if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite float sample; recording stopped without substituting invented PCM.");
                scaled = value * 8388608.0;
            }
            else
            {
                int value = BinaryPrimitives.ReadInt32LittleEndian(source[i..]);
                int padding = 32-conversion.Source.ValidBitsPerSample;
                value = (value >> padding) << padding;
                scaled = value / 256.0;
            }
            double rounded = Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (rounded < -8388608 || rounded > 8388607) saturated++;
            int quantized = (int)Math.Clamp(rounded, -8388608, 8388607);
            output[offset] = (byte)quantized; output[offset + 1] = (byte)(quantized >> 8); output[offset + 2] = (byte)(quantized >> 16);
        }
        return new(source.Length / 4, saturated);
    }
}
