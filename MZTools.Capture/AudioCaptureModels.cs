using System.Collections.Concurrent;

namespace MZTools.Capture;

public enum AudioSampleRepresentation { Unsigned8, Signed16, Signed24, Signed32, Float32 }
public enum AudioSampleLayout { InterleavedLittleEndian }

/// <summary>Delivered container format, not a claim about an ADC's physical resolution.</summary>
public sealed record AudioCaptureFormat
{
    public int SampleRate { get; }
    public int Channels { get; }
    public AudioSampleRepresentation Representation { get; }
    public int ValidBitsPerSample { get; }
    public AudioSampleLayout Layout => AudioSampleLayout.InterleavedLittleEndian;
    public int BitsPerSample => Representation switch
    {
        AudioSampleRepresentation.Unsigned8 => 8,
        AudioSampleRepresentation.Signed16 => 16,
        AudioSampleRepresentation.Signed24 => 24,
        _ => 32
    };
    public int BlockAlign => Channels * (BitsPerSample / 8);

    public AudioCaptureFormat(int sampleRate, int channels, AudioSampleRepresentation representation,
        int? validBitsPerSample = null)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        if (!Enum.IsDefined(representation)) throw new ArgumentOutOfRangeException(nameof(representation));
        SampleRate = sampleRate; Channels = channels; Representation = representation;
        ValidBitsPerSample = validBitsPerSample ?? BitsPerSample;
        if (ValidBitsPerSample <= 0 || ValidBitsPerSample > BitsPerSample ||
            ((representation is AudioSampleRepresentation.Float32 or AudioSampleRepresentation.Unsigned8) && ValidBitsPerSample != BitsPerSample))
            throw new ArgumentOutOfRangeException(nameof(validBitsPerSample));
    }
}

public sealed record AudioCaptureDevice(string Id, string Name, string Backend,
    IReadOnlyList<AudioCaptureFormat> AdvertisedFormats);
public sealed record AudioCaptureRequest(string DeviceId, AudioCaptureFormat Format, bool AllowFormatFallback = false);

/// <summary>Backend must state unknown processing explicitly; RAW never certifies hardware transparency.</summary>
public sealed record AudioCaptureStreamInfo(AudioCaptureDevice Device, AudioCaptureFormat RequestedFormat,
    AudioCaptureFormat ActualFormat, string ProcessingDescription)
{
    public AudioCaptureProcessing? Processing { get; init; }
    public long? DeviceFrameOrigin { get; init; }
    public long? FirstPacketTimestampTicks { get; init; }
    public DateTimeOffset? FirstPacketReceivedUtc { get; init; }
}
public sealed record AudioCaptureProcessing(string BackendVersion, string AccessMode, bool RawRequested,
    bool? RawActive, bool EngineConversionPossible, AudioCaptureFormat? EngineMixFormat, string EffectsStatus);

[Flags]
public enum AudioCapturePacketFlags
{ None = 0, Silence = 1, Discontinuity = 2, TimestampUnreliable = 4, InitialDiscontinuity = 8, InitialPositionOffset = 16 }

/// <summary>FirstFrame is relative to this stream (first packet starts at zero).
/// TimestampTicks is a monotonic timestamp in 100 ns units with a backend-defined origin.</summary>
public readonly record struct AudioCapturePacket(long FirstFrame, int FrameCount, long TimestampTicks,
    AudioCapturePacketFlags Flags = AudioCapturePacketFlags.None);

public enum CaptureSessionState { Idle, Starting, Monitoring, Recording, Stopping, Completed, Failed, Disposed }
public enum CaptureEndReason
{
    Stopped, SourceCompleted, Cancelled, DeviceLost, BackendError, StartError,
    ArchiveOverflow, SinkError, InvalidPacket, Discontinuity, FormatMismatch, AnalysisError
}
public enum CaptureIntegrity { NotRecorded, Complete, Incomplete }
public enum CaptureDiagnosticSeverity { Warning, Error }
public sealed record CaptureDiagnostic(string Code, CaptureDiagnosticSeverity Severity, string Message,
    long? FirstFrame = null, long? FrameCount = null);

public sealed record CapturePipelineOptions
{
    public int ArchiveQueueCapacity { get; init; } = 64;
    public int AnalysisQueueCapacity { get; init; } = 2;
    public int MaximumChunkFrames { get; init; } = 4096;
    public int MaximumDiagnostics { get; init; } = 128;
    public long MaximumBufferBytes { get; init; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        if (ArchiveQueueCapacity is < 1 or > 65536 || AnalysisQueueCapacity is < 1 or > 65536 ||
            MaximumChunkFrames <= 0 || MaximumDiagnostics is < 1 or > 65536 || MaximumBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(CapturePipelineOptions));
    }
}

public sealed record CaptureStatistics(long ReceivedFrames, long AcceptedArchiveFrames, long WrittenFrames,
    long AnalyzedFrames, long AnalysisDroppedFrames, long KnownMissingFrames, bool HasUnknownLoss,
    int ArchiveQueueDepth, int AnalysisQueueDepth, long AllocatedBufferBytes, int LeasedBuffers, int PeakLeasedBuffersUpperBound);
public sealed record CaptureCompletion(CaptureEndReason Reason, CaptureIntegrity Integrity, CaptureStatistics Statistics);
public sealed record CaptureSessionReport(DateTimeOffset StartUtc, DateTimeOffset EndUtc, TimeSpan Elapsed,
    AudioCaptureStreamInfo? Stream, CaptureEndReason Reason, CaptureIntegrity Integrity, bool SinkFinalized,
    CaptureStatistics Statistics, IReadOnlyList<CaptureDiagnostic> Diagnostics, long OmittedDiagnostics)
{
    public AudioRecordingResult? Recording { get; init; }
}
public sealed record CaptureSessionSnapshot(CaptureSessionState State, AudioCaptureStreamInfo? Stream,
    CaptureStatistics Statistics, IReadOnlyList<CaptureDiagnostic> Diagnostics, long OmittedDiagnostics);

internal sealed class CaptureDiagnostics(int capacity)
{
    private readonly ConcurrentQueue<CaptureDiagnostic> items = new();
    private int count;
    private long omitted;
    internal void Add(CaptureDiagnostic item)
    {
        if (Interlocked.Increment(ref count) <= capacity) items.Enqueue(item);
        else Interlocked.Increment(ref omitted);
    }
    internal IReadOnlyList<CaptureDiagnostic> Snapshot() => Array.AsReadOnly(items.ToArray());
    internal long Omitted => Interlocked.Read(ref omitted);
}
