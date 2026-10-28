using System.Threading.Channels;

namespace MZTools.Capture;

/// <summary>Separate preallocated pools prevent analysis from starving the archival path.</summary>
internal sealed class CapturePipeline
{
    private readonly AudioCaptureFormat format;
    private readonly CapturePipelineOptions options;
    private readonly IAudioRecordingSink? sink;
    private readonly IAudioChunkConsumer? analysis;
    private readonly CaptureDiagnostics diagnostics;
    private readonly Action<CaptureEndReason> stop;
    private readonly Channel<CapturedAudioChunk>? archiveQueue, analysisQueue;
    private readonly CaptureBufferPool? archivePool, analysisPool;
    private readonly Task archiveWorker, analysisWorker;
    private readonly TaskCompletionSource callbackIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int accepting = 1, callbackBusy, timestampWarning;
    private long received, accepted, written, analyzed, analysisDropped, missing;
    private long nextFrame, lastTimestamp = -1;
    private int unknownLoss;

    internal CapturePipeline(AudioCaptureFormat format, CapturePipelineOptions options, IAudioRecordingSink? sink,
        IAudioChunkConsumer? analysis, CaptureDiagnostics diagnostics, Action<CaptureEndReason> stop)
    {
        this.format = format; this.options = options; this.sink = sink; this.analysis = analysis;
        this.diagnostics = diagnostics; this.stop = stop;
        int bytes = checked(options.MaximumChunkFrames * format.BlockAlign);
        long total = checked((long)bytes * ((sink != null ? options.ArchiveQueueCapacity + 2L : 0) +
            (analysis != null ? options.AnalysisQueueCapacity + 2L : 0)));
        if (total > options.MaximumBufferBytes) throw new ArgumentException("Capture buffers exceed the configured memory budget.");
        if (sink != null)
        {
            archivePool = new(options.ArchiveQueueCapacity + 2, bytes);
            archiveQueue = CreateQueue(options.ArchiveQueueCapacity, singleReader: true);
        }
        if (analysis != null)
        {
            analysisPool = new(options.AnalysisQueueCapacity + 2, bytes);
            analysisQueue = CreateQueue(options.AnalysisQueueCapacity, singleReader: false);
        }
        archiveWorker = archiveQueue == null ? Task.CompletedTask : Task.Run(WriteArchiveAsync);
        analysisWorker = analysisQueue == null ? Task.CompletedTask : Task.Run(AnalyzeAsync);
    }

    private static Channel<CapturedAudioChunk> CreateQueue(int capacity, bool singleReader) =>
        Channel.CreateBounded<CapturedAudioChunk>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = singleReader, AllowSynchronousContinuations = false });

    // Called on the backend's capture thread. No awaits, UI, DSP, disk I/O, logging or user callbacks.
    internal void Offer(ReadOnlySpan<byte> data, AudioCapturePacket packet)
    {
        if (Volatile.Read(ref accepting) == 0) return;
        if (Interlocked.CompareExchange(ref callbackBusy, 1, 0) != 0)
        {
            Fail(CaptureEndReason.InvalidPacket, "CONCURRENT_CALLBACK", "Backend delivered overlapping capture callbacks."); return;
        }
        try
        {
            if (Volatile.Read(ref accepting) == 0) return;
            if (packet.FrameCount <= 0 || packet.FirstFrame < 0 || packet.TimestampTicks < 0 ||
                (long)packet.FrameCount * format.BlockAlign != data.Length ||
                packet.FirstFrame > long.MaxValue - packet.FrameCount)
            {
                Fail(CaptureEndReason.InvalidPacket, "INVALID_PACKET", "PCM packet has invalid bounds or an incomplete frame.", packet.FirstFrame); return;
            }
            Interlocked.Add(ref received, packet.FrameCount);
            if (nextFrame == 0 && packet.Flags.HasFlag(AudioCapturePacketFlags.InitialPositionOffset))
                diagnostics.Add(new("INITIAL_POSITION_OFFSET",CaptureDiagnosticSeverity.Warning,
                    "The first available packet has a nonzero native frame origin. The archive begins at this packet; see DeviceFrameOrigin. Earlier device frames were not supplied and are not invented.",0));
            if (nextFrame == 0 && packet.Flags.HasFlag(AudioCapturePacketFlags.InitialDiscontinuity))
                diagnostics.Add(new("INITIAL_DISCONTINUITY",CaptureDiagnosticSeverity.Warning,
                    "Device signaled a transition at capture start. There is no preceding captured interval from which to infer a lost frame count.",0));
            if (packet.FirstFrame != nextFrame || packet.Flags.HasFlag(AudioCapturePacketFlags.Discontinuity))
            {
                if (packet.FirstFrame < nextFrame)
                    Fail(CaptureEndReason.InvalidPacket, "OVERLAPPING_FRAMES", "PCM frame indices moved backwards.", packet.FirstFrame);
                else
                {
                    long lost = packet.FirstFrame - nextFrame;
                    if (lost > 0) Interlocked.Add(ref missing, lost); else Volatile.Write(ref unknownLoss, 1);
                    Fail(CaptureEndReason.Discontinuity, "DROPOUT", "Capture discontinuity; recording stopped without inventing replacement samples.",
                        nextFrame, lost > 0 ? lost : null);
                }
                return;
            }
            bool unreliable = packet.Flags.HasFlag(AudioCapturePacketFlags.TimestampUnreliable);
            if (!unreliable && packet.TimestampTicks < lastTimestamp)
            { Fail(CaptureEndReason.InvalidPacket, "TIMESTAMP_REVERSED", "Reliable capture timestamps moved backwards.", packet.FirstFrame); return; }
            if (unreliable && Interlocked.Exchange(ref timestampWarning, 1) == 0)
                diagnostics.Add(new("TIMESTAMP_UNRELIABLE", CaptureDiagnosticSeverity.Warning, "Device reported an unreliable timestamp.", packet.FirstFrame));
            if (!unreliable) lastTimestamp = packet.TimestampTicks;
            nextFrame = packet.FirstFrame + packet.FrameCount;
            for (int offset = 0; offset < packet.FrameCount;)
            {
                if (Volatile.Read(ref accepting) == 0) break;
                int frames = Math.Min(options.MaximumChunkFrames, packet.FrameCount - offset);
                var flags = offset == 0 ? packet.Flags : packet.Flags & ~(AudioCapturePacketFlags.InitialDiscontinuity | AudioCapturePacketFlags.InitialPositionOffset);
                var part = new AudioCapturePacket(packet.FirstFrame + offset, frames,
                    checked(packet.TimestampTicks + (long)offset * TimeSpan.TicksPerSecond / format.SampleRate), flags);
                var bytes = data.Slice(offset * format.BlockAlign, frames * format.BlockAlign);
                if (archiveQueue != null)
                {
                    var chunk = archivePool!.Rent(bytes, part);
                    if (chunk == null || !archiveQueue.Writer.TryWrite(chunk))
                    {
                        chunk?.Release();
                        Fail(CaptureEndReason.ArchiveOverflow, "ARCHIVE_OVERRUN", "Archive queue is full; retained audio will be drained and marked incomplete.",
                            part.FirstFrame, packet.FrameCount - offset);
                        break;
                    }
                    Interlocked.Add(ref accepted, frames);
                }
                OfferAnalysis(bytes, part);
                offset += frames;
            }
        }
        catch (OverflowException)
        { Fail(CaptureEndReason.InvalidPacket, "PACKET_OVERFLOW", "Capture packet time or size overflowed.", packet.FirstFrame); }
        finally
        {
            Volatile.Write(ref callbackBusy, 0);
            if (Volatile.Read(ref accepting) == 0) callbackIdle.TrySetResult();
        }
    }

    private void OfferAnalysis(ReadOnlySpan<byte> data, AudioCapturePacket packet)
    {
        if (analysisQueue == null) return;
        var chunk = analysisPool!.Rent(data, packet);
        if (chunk == null) { Interlocked.Add(ref analysisDropped, packet.FrameCount); return; }
        if (analysisQueue.Writer.TryWrite(chunk)) return;
        if (analysisQueue.Reader.TryRead(out var old))
        { Interlocked.Add(ref analysisDropped, old.Packet.FrameCount); old.Release(); }
        if (!analysisQueue.Writer.TryWrite(chunk))
        { Interlocked.Add(ref analysisDropped, packet.FrameCount); chunk.Release(); }
    }

    private async Task WriteArchiveAsync()
    {
        bool failed = false;
        await foreach (var chunk in archiveQueue!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (!failed)
                {
                    await sink!.ConsumeAsync(chunk, CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Add(ref written, chunk.Packet.FrameCount);
                }
            }
            catch (Exception error)
            { failed = true; Fail(CaptureEndReason.SinkError, "SINK_WRITE_FAILED", error.Message, chunk.Packet.FirstFrame); }
            finally { chunk.Release(); }
        }
    }

    private async Task AnalyzeAsync()
    {
        bool failed = false;
        await foreach (var chunk in analysisQueue!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (!failed)
                {
                    await analysis!.ConsumeAsync(chunk, CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Add(ref analyzed, chunk.Packet.FrameCount);
                }
                else Interlocked.Add(ref analysisDropped, chunk.Packet.FrameCount);
            }
            catch (Exception error)
            {
                failed = true; Interlocked.Add(ref analysisDropped, chunk.Packet.FrameCount);
                diagnostics.Add(new("ANALYSIS_FAILED", sink == null ? CaptureDiagnosticSeverity.Error : CaptureDiagnosticSeverity.Warning,
                    error.Message, chunk.Packet.FirstFrame));
                if (sink == null) Fail(CaptureEndReason.AnalysisError, "MONITOR_FAILED", "Monitor consumer failed.");
            }
            finally { chunk.Release(); }
        }
    }

    private void Fail(CaptureEndReason reason, string code, string message, long? frame = null, long? count = null)
    {
        Volatile.Write(ref accepting, 0);
        diagnostics.Add(new(code, CaptureDiagnosticSeverity.Error, message, frame, count));
        stop(reason); // Signals a TaskCompletionSource only; never invokes backend Stop on this thread.
    }

    internal async Task DrainAsync()
    {
        Volatile.Write(ref accepting, 0);
        if (Volatile.Read(ref callbackBusy) != 0) await callbackIdle.Task.ConfigureAwait(false);
        archiveQueue?.Writer.TryComplete(); analysisQueue?.Writer.TryComplete();
        await Task.WhenAll(archiveWorker, analysisWorker).ConfigureAwait(false);
        archivePool?.Clear(); analysisPool?.Clear();
    }

    internal CaptureStatistics Snapshot() => new(Interlocked.Read(ref received), Interlocked.Read(ref accepted),
        Interlocked.Read(ref written), Interlocked.Read(ref analyzed), Interlocked.Read(ref analysisDropped),
        Interlocked.Read(ref missing), Volatile.Read(ref unknownLoss) != 0,
        archiveQueue?.Reader.Count ?? 0, analysisQueue?.Reader.Count ?? 0,
        (archivePool?.Bytes ?? 0) + (analysisPool?.Bytes ?? 0),
        (archivePool?.Leased ?? 0) + (analysisPool?.Leased ?? 0), (archivePool?.Peak ?? 0) + (analysisPool?.Peak ?? 0));
}
