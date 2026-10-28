using System.Buffers.Binary;

namespace MZTools.Capture;

public sealed record SyntheticCaptureOptions
{
    public long FrameCount { get; init; } = 48000;
    public int[] ChunkFramePattern { get; init; } = [128, 511, 1024];
    public TimeSpan OpenDelay { get; init; }
    public TimeSpan StartDelay { get; init; }
    public TimeSpan ChunkInterval { get; init; }
    public long? DisconnectAtFrame { get; init; }
    public long? DiscontinuityAtFrame { get; init; }
    public int MissingFrames { get; init; } = 17;
}

/// <summary>Explicit demo/test source. Never opens hardware or generates data until StartAsync.</summary>
public sealed class SyntheticCaptureBackend : IAudioCaptureBackend
{
    public const string DeviceId = "mztools-synthetic-input";
    private readonly SyntheticCaptureOptions options;
    private readonly int[] pattern;
    private readonly Func<long, int, double>? sample;
    private static readonly AudioCaptureDevice device = new(DeviceId, "Synthetic audio input", "Synthetic",
        Array.AsReadOnly(new[] { new AudioCaptureFormat(44100,1,AudioSampleRepresentation.Signed16),
            new AudioCaptureFormat(48000,2,AudioSampleRepresentation.Signed24),
            new AudioCaptureFormat(96000,2,AudioSampleRepresentation.Signed24) }));

    /// <param name="sample">Normalized sample by absolute frame and zero-based channel; default is a 1 kHz sine.</param>
    public SyntheticCaptureBackend(SyntheticCaptureOptions? options = null, Func<long, int, double>? sample = null)
    {
        this.options = options ?? new(); this.sample = sample;
        pattern = (int[])this.options.ChunkFramePattern.Clone();
        if (this.options.FrameCount < 0 || pattern.Length == 0 || pattern.Any(n => n is < 1 or > 1048576) ||
            this.options.OpenDelay < TimeSpan.Zero || this.options.StartDelay < TimeSpan.Zero || this.options.ChunkInterval < TimeSpan.Zero ||
            this.options.MissingFrames < 0 || this.options.DisconnectAtFrame < 0 || this.options.DiscontinuityAtFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AudioCaptureDevice>>(Array.AsReadOnly(new[] { device }));
    }

    public async ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId != DeviceId) throw new ArgumentException("Unknown synthetic input device.");
        if (options.OpenDelay > TimeSpan.Zero) await Task.Delay(options.OpenDelay, cancellationToken).ConfigureAwait(false);
        return new Session(request, options, pattern, sample);
    }

    private sealed class Session(AudioCaptureRequest request, SyntheticCaptureOptions options, int[] pattern,
        Func<long, int, double>? sample) : IAudioCaptureSession
    {
        public AudioCaptureStreamInfo Info { get; } = new(device, request.Format, request.Format, "Synthetic; no hardware, processing or resampling");
        public event AudioCaptureDataAvailable? DataAvailable;
        public event Action<AudioCaptureStopped>? Stopped;
        private readonly CancellationTokenSource stop = new();
        private readonly TaskCompletionSource startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? worker;
        private int started, disposed;

        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Backend stream already started.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (options.StartDelay > TimeSpan.Zero) await Task.Delay(options.StartDelay, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                worker = Task.Run(ProduceAsync);
            }
            finally { startFinished.TrySetResult(); }
        }

        private async Task ProduceAsync()
        {
            var format = request.Format;
            byte[] buffer = new byte[checked(pattern.Max() * format.BlockAlign)];
            long frame = 0; int chunkIndex = 0;
            CaptureEndReason reason = CaptureEndReason.SourceCompleted; string? error = null;
            try
            {
                while (frame < options.FrameCount)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (options.DisconnectAtFrame is long disconnect && frame >= disconnect)
                    { reason = CaptureEndReason.DeviceLost; error = "Synthetic input disconnected."; break; }
                    if (options.DiscontinuityAtFrame is long gap && frame >= gap)
                    {
                        int frames = (int)Math.Min(pattern[chunkIndex % pattern.Length], options.FrameCount - frame);
                        DataAvailable?.Invoke(buffer.AsSpan(0, frames * format.BlockAlign), new(frame + options.MissingFrames,
                            frames, Timestamp(frame, format.SampleRate), AudioCapturePacketFlags.Discontinuity));
                        break;
                    }
                    int count = (int)Math.Min(pattern[chunkIndex++ % pattern.Length], options.FrameCount - frame);
                    if (options.DisconnectAtFrame is long boundary && frame < boundary) count = (int)Math.Min(count, boundary - frame);
                    if (options.DiscontinuityAtFrame is long gapBoundary && frame < gapBoundary) count = (int)Math.Min(count, gapBoundary - frame);
                    for (int i = 0; i < count; i++)
                        for (int channel = 0; channel < format.Channels; channel++)
                        {
                            double value = sample?.Invoke(frame + i, channel) ?? .5 * Math.Sin(2 * Math.PI * 1000 * (frame + i) / format.SampleRate);
                            WriteSample(buffer.AsSpan((i * format.Channels + channel) * (format.BitsPerSample / 8)), format, value);
                        }
                    DataAvailable?.Invoke(buffer.AsSpan(0, count * format.BlockAlign), new(frame, count, Timestamp(frame, format.SampleRate)));
                    frame += count;
                    if (options.ChunkInterval > TimeSpan.Zero) await Task.Delay(options.ChunkInterval, stop.Token).ConfigureAwait(false);
                    else await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { reason = CaptureEndReason.Stopped; }
            catch (Exception exception) { reason = CaptureEndReason.BackendError; error = exception.Message; }
            Stopped?.Invoke(new(reason, error));
        }

        private static long Timestamp(long frame, int rate) => checked(frame / rate * TimeSpan.TicksPerSecond +
            frame % rate * TimeSpan.TicksPerSecond / rate);

        private static void WriteSample(Span<byte> destination, AudioCaptureFormat format, double value)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Synthetic samples must be finite.");
            if (format.Representation == AudioSampleRepresentation.Float32)
            { BinaryPrimitives.WriteSingleLittleEndian(destination, (float)value); return; }
            double scale = Math.Pow(2, format.ValidBitsPerSample - 1);
            long quantized = (long)Math.Clamp(Math.Round(value * scale, MidpointRounding.AwayFromZero), -scale, scale - 1);
            // Valid integer bits occupy the most significant bits of their container (WAVE extensible convention).
            quantized <<= format.BitsPerSample - format.ValidBitsPerSample;
            if (format.Representation == AudioSampleRepresentation.Unsigned8) destination[0] = (byte)(quantized + 128);
            else for (int b = 0; b < format.BitsPerSample / 8; b++) destination[b] = (byte)(quantized >> (8 * b));
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            await stop.CancelAsync().ConfigureAwait(false);
            if (Volatile.Read(ref started) != 0) await startFinished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (worker != null) await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await stop.CancelAsync().ConfigureAwait(false);
            if (Volatile.Read(ref started) != 0) await startFinished.Task.ConfigureAwait(false);
            if (worker != null) await worker.ConfigureAwait(false);
            DataAvailable = null; Stopped = null; stop.Dispose();
        }
    }
}
