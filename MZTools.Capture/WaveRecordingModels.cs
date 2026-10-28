using System.Buffers.Binary;

namespace MZTools.Capture;

public sealed record WaveRecordingOptions(string DestinationPath)
{
    public bool AllowPcm24Conversion { get; init; }
    public bool PreserveNativeFormat { get; init; }
    public bool RequireAnalyzerCompatibleRate { get; init; } = true;
    public long SegmentDataLimit { get; init; } = uint.MaxValue - 36L;
    public int ConversionBufferBytes { get; init; } = 65536;
    public string ApplicationName { get; init; } = "MZTools";
    public string ApplicationVersion { get; init; } = "unspecified";
    public string? TapeNote { get; init; }
    public string? TapeSide { get; init; }
}

public sealed record AudioRecordingSegment(string Path, long FirstFrame, long Frames, long DataBytes,
    long FileBytes, bool HeaderFinalized, bool Published, string? FileSha256, string? PcmSha256, string? Error,
    string? TrailingPartialFrameHex = null, bool? PcmMatchesWrittenBytes = null);
public sealed record AudioRecordingResult(AudioPcmConversion Conversion, long SavedFrames, long SourceFramesHashed,
    string? SourcePcmSha256, bool SourceHashCoversSavedFramesExactly, long SaturatedSamples,
    IReadOnlyList<AudioRecordingSegment> Segments, string? SidecarPath, bool SidecarFinalized,
    bool AnalyzerCompatible, IReadOnlyList<string> Errors)
{
    public long SourceBytesHashed => checked(SourceFramesHashed*Conversion.Source.BlockAlign);
    public long SavedPcmBytes => Segments.Sum(s => s.DataBytes);
    public double SavedDurationSeconds => SavedFrames/(double)Conversion.Output.SampleRate;
}

/// <summary>Optional post-disposal sidecar hook. Called after the final capture and consumer cleanup outcome is known.</summary>
public interface ICaptureSessionReportSink
{
    AudioRecordingResult? RecordingResult { get; }
    ValueTask WriteSessionReportAsync(CaptureSessionReport report, CancellationToken cancellationToken = default);
}

/// <summary>Storage seam for deterministic disk-full, rename, permission and finalize failure tests.</summary>
public interface IAudioRecordingStorage
{
    bool Exists(string path);
    Stream CreateNew(string path);
    Stream OpenExisting(string path);
    Stream OpenRead(string path);
    void FlushToDisk(Stream stream);
    void MoveNew(string source, string destination);
}

public sealed class FileAudioRecordingStorage : IAudioRecordingStorage
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
    public Stream OpenExisting(string path) => new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
        FileShare.None, 1, FileOptions.Asynchronous);
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
    public void FlushToDisk(Stream stream)
    {
        if (stream is FileStream file) file.Flush(flushToDisk: true); else stream.Flush();
    }
    public void MoveNew(string source, string destination) => File.Move(source, destination, overwrite: false);
}

/// <summary>64-bit frame accounting; the RIFF/data sizes are checked before narrowing to UInt32.</summary>
public static class RiffWaveLayout
{
    public static int DataOffset(AudioCaptureFormat format,bool preserveNativeFormat=false)=>!preserveNativeFormat?44:
        format.Representation==AudioSampleRepresentation.Float32?80:format.BitsPerSample==32||format.ValidBitsPerSample!=format.BitsPerSample?68:44;
    public static bool AnalyzerSupports(AudioCaptureFormat format,bool preserveNativeFormat=false)=>AnalyzerSupportsRate(format.SampleRate)&&
        format.Representation!=AudioSampleRepresentation.Float32&&format.BitsPerSample<=24&&DataOffset(format,preserveNativeFormat)==44;
    public static long FrameCapacity(AudioCaptureFormat format, long requestedDataLimit = uint.MaxValue - 36L,bool preserveNativeFormat=false)
    {
        if (requestedDataLimit <= 0) throw new ArgumentOutOfRangeException(nameof(requestedDataLimit));
        long overhead=DataOffset(format,preserveNativeFormat)-8L;
        long bytes = Math.Min(requestedDataLimit, uint.MaxValue - overhead);
        bytes -= bytes % format.BlockAlign;
        if (bytes + (bytes & 1) > uint.MaxValue - overhead) bytes -= format.BlockAlign;
        if (bytes <= 0) throw new ArgumentException("The segment limit must accommodate a whole PCM frame.");
        return bytes / format.BlockAlign;
    }

    public static byte[] Header(AudioCaptureFormat format, long frames,bool preserveNativeFormat=false)
    {
        if (!preserveNativeFormat&&(format.BitsPerSample > 24 || format.Representation == AudioSampleRepresentation.Float32))
            throw new ArgumentException("Compatible WAV output requires integer PCM8/16/24.");
        if (frames < 0 || frames > FrameCapacity(format,preserveNativeFormat:preserveNativeFormat)) throw new ArgumentOutOfRangeException(nameof(frames));
        long data = checked(frames * format.BlockAlign);
        int offset=DataOffset(format,preserveNativeFormat); bool extended=offset!=44;
        byte[] header = new byte[offset];
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4),checked((uint)(offset-8L + data + (data & 1))));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16),extended?40u:16u);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20),extended?(ushort)0xFFFE:(ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22),(ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24),(uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28),checked((uint)((long)format.SampleRate * format.BlockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32),(ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34),(ushort)format.BitsPerSample);
        if(extended)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36),22);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(38),(ushort)format.ValidBitsPerSample);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40),format.Channels==1?4u:3u);
            new Guid(format.Representation==AudioSampleRepresentation.Float32?"00000003-0000-0010-8000-00aa00389b71":"00000001-0000-0010-8000-00aa00389b71").ToByteArray().CopyTo(header,44);
            if(format.Representation==AudioSampleRepresentation.Float32)
            { "fact"u8.CopyTo(header.AsSpan(60)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64),4); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68),checked((uint)frames)); }
        }
        "data"u8.CopyTo(header.AsSpan(offset-8)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset-4),checked((uint)data));
        return header;
    }

    public static bool AnalyzerSupportsRate(int rate) => rate is 22050 or 44100 or 48000 or 88200 or 96000;
}
