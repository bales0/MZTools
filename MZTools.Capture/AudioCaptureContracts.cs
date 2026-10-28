using System.Collections.Concurrent;

namespace MZTools.Capture;

/// <summary>The data span is borrowed only for this synchronous callback. Callbacks must be serialized.</summary>
public delegate void AudioCaptureDataAvailable(ReadOnlySpan<byte> data, AudioCapturePacket packet);
public sealed record AudioCaptureStopped(CaptureEndReason Reason, string? Error = null);

public interface IAudioCaptureBackend
{
    ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default);
    ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default);
}

public interface IAudioCaptureSession : IAsyncDisposable
{
    AudioCaptureStreamInfo Info { get; }
    event AudioCaptureDataAvailable? DataAvailable;
    event Action<AudioCaptureStopped>? Stopped;
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    /// <summary>Must quiesce callbacks before returning; repeat calls must be safe.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IAudioChunkConsumer : IAsyncDisposable
{
    ValueTask StartAsync(AudioCaptureStreamInfo stream, CancellationToken cancellationToken = default);
    /// <summary>Borrowed read-only chunk until the returned operation completes. Do not retain its memory.</summary>
    ValueTask ConsumeAsync(CapturedAudioChunk chunk, CancellationToken cancellationToken = default);
}

public interface IAudioRecordingSink : IAudioChunkConsumer
{
    /// <summary>Called after draining, including failure. Incomplete integrity must never be reported as clean.</summary>
    ValueTask CompleteAsync(CaptureCompletion completion, CancellationToken cancellationToken = default);
}

/// <summary>Pipeline-owned PCM lease. Consumers borrow it; only the pipeline returns it to its pool.</summary>
public sealed class CapturedAudioChunk
{
    private readonly CaptureBufferPool pool;
    private readonly byte[] buffer;
    private int leased;
    private int length;
    public AudioCapturePacket Packet { get; private set; }
    public ReadOnlyMemory<byte> Data => Volatile.Read(ref leased) == 1
        ? buffer.AsMemory(0, length) : throw new ObjectDisposedException(nameof(CapturedAudioChunk));

    internal CapturedAudioChunk(CaptureBufferPool pool, int bytes) { this.pool = pool; buffer = new byte[bytes]; }
    internal void Assign(ReadOnlySpan<byte> data, AudioCapturePacket packet)
    {
        data.CopyTo(buffer); length = data.Length; Packet = packet; Volatile.Write(ref leased, 1);
    }
    internal void Release()
    {
        if (Interlocked.Exchange(ref leased, 0) != 1) throw new InvalidOperationException("PCM lease returned twice.");
        pool.Return(this);
    }
}

internal sealed class CaptureBufferPool
{
    private readonly ConcurrentQueue<CapturedAudioChunk> available = new();
    private int leased, peak;
    internal int Leased => Volatile.Read(ref leased);
    internal int Peak => Volatile.Read(ref peak);
    internal long Bytes { get; }
    internal CaptureBufferPool(int capacity, int bytes)
    {
        Bytes = checked((long)capacity * bytes);
        for (int i = 0; i < capacity; i++) available.Enqueue(new(this, bytes));
    }
    internal CapturedAudioChunk? Rent(ReadOnlySpan<byte> data, AudioCapturePacket packet)
    {
        if (!available.TryDequeue(out var chunk)) return null;
        int current = Interlocked.Increment(ref leased);
        int previous;
        do { previous = Volatile.Read(ref peak); if (current <= previous) break; }
        while (Interlocked.CompareExchange(ref peak, current, previous) != previous);
        chunk.Assign(data, packet); return chunk;
    }
    internal void Return(CapturedAudioChunk chunk) { Interlocked.Decrement(ref leased); available.Enqueue(chunk); }
    internal void Clear() => available.Clear();
}
