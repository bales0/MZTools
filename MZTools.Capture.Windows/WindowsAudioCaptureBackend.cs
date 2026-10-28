using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MZTools.Capture.Windows;

public enum WindowsAudioCaptureMode { Shared, Exclusive }
public sealed record WindowsAudioCaptureOptions
{
    public WindowsAudioCaptureMode Mode { get; init; } = WindowsAudioCaptureMode.Shared;
    public bool RequestRaw { get; init; } = true;
    public bool AllowEngineFormatConversion { get; init; }
    public int BufferMilliseconds { get; init; } = 100;
    public int SilenceBufferFrames { get; init; } = 65536;

    internal void ValidateSupported()
    {
        if (!Enum.IsDefined(Mode) || BufferMilliseconds is < 10 or > 2000 ||
            SilenceBufferFrames is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(WindowsAudioCaptureOptions));
        if (Mode != WindowsAudioCaptureMode.Shared)
            throw new NotSupportedException("Exclusive input is unavailable with NAudio WasapiRecorder 3.3.0. Use shared input at the endpoint mix format; RAW can be requested separately. No fallback or recording was started.");
    }
}

/// <summary>Only explicit input endpoints. No loopback, default-device routing, device-volume changes or automatic microphone start.</summary>
public sealed class WindowsAudioCaptureBackend : IAudioCaptureBackend
{
    public static IReadOnlyList<WindowsAudioCaptureMode> SupportedModes { get; } =
        Array.AsReadOnly(new[] { WindowsAudioCaptureMode.Shared });
    private readonly IWindowsCapturePlatform platform;
    private readonly WindowsAudioCaptureOptions options;
    public WindowsAudioCaptureBackend(WindowsAudioCaptureOptions? options = null) : this(new WasapiCapturePlatform(),options) { }
    internal WindowsAudioCaptureBackend(IWindowsCapturePlatform platform, WindowsAudioCaptureOptions? options = null)
    {
        this.platform = platform; this.options = options ?? new();
        this.options.ValidateSupported();
    }
    public async ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default) =>
        await Task.Run(() => platform.Enumerate(options,cancellationToken),cancellationToken).ConfigureAwait(false);

    public async ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request,CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transport = await Task.Run(() => platform.Open(request,options,cancellationToken),cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        { await transport.DisposeAsync().ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
        return new Session(transport,options);
    }

    private sealed class Session : IAudioCaptureSession
    {
        private readonly IWindowsCaptureTransport transport;
        private readonly SemaphoreSlim lifecycle = new(1,1);
        private readonly byte[] silence;
        private AudioCaptureStreamInfo info;
        private long? origin;
        private int started, stopping, disposed;
        private bool transportDisposed;
        public AudioCaptureStreamInfo Info => Volatile.Read(ref info);
        public event AudioCaptureDataAvailable? DataAvailable;
        public event Action<AudioCaptureStopped>? Stopped;

        internal Session(IWindowsCaptureTransport transport,WindowsAudioCaptureOptions options)
        {
            this.transport = transport; info = transport.Info;
            silence = new byte[checked(options.SilenceBufferFrames * info.ActualFormat.BlockAlign)];
            if (info.ActualFormat.Representation == AudioSampleRepresentation.Unsigned8) Array.Fill(silence,(byte)128);
            transport.DataAvailable += OnData; transport.Stopped += OnStopped;
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed != 0,this);
                if (Volatile.Read(ref stopping) != 0) throw new InvalidOperationException("Input session has already stopped.");
                if (Interlocked.Exchange(ref started,1) != 0) throw new InvalidOperationException("Input session already started.");
                cancellationToken.ThrowIfCancellationRequested();
                // Native initialization is synchronous; the UI thread is never used and resources are not freed underneath it.
                await Task.Run(transport.Start,cancellationToken).ConfigureAwait(false);
                var updated = transport.Info;
                AudioCaptureStreamInfo old;
                do
                {
                    old = Info;
                } while (!ReferenceEquals(Interlocked.CompareExchange(ref info,updated with { DeviceFrameOrigin = old.DeviceFrameOrigin,
                    FirstPacketTimestampTicks = old.FirstPacketTimestampTicks, FirstPacketReceivedUtc = old.FirstPacketReceivedUtc },old),old));
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { lifecycle.Release(); }
        }

        private void OnData(ReadOnlySpan<byte> data,AudioClientBufferFlags rawFlags,long deviceFrame,long timestamp)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (deviceFrame < 0 || timestamp < 0 && !rawFlags.HasFlag(AudioClientBufferFlags.TimestampError))
            { Stopped?.Invoke(new(CaptureEndReason.InvalidPacket,"WASAPI returned an invalid packet position.")); return; }
            bool first = !origin.HasValue;
            if (first)
            {
                origin = deviceFrame;
                var receivedUtc = DateTimeOffset.UtcNow;
                AudioCaptureStreamInfo old;
                do
                {
                    old = Info;
                } while (!ReferenceEquals(Interlocked.CompareExchange(ref info,old with { DeviceFrameOrigin = deviceFrame,
                    FirstPacketTimestampTicks = timestamp, FirstPacketReceivedUtc = receivedUtc },old),old));
            }
            long frame = deviceFrame-origin!.Value;
            var flags = AudioCapturePacketFlags.None;
            if (first && deviceFrame > 0) flags |= AudioCapturePacketFlags.InitialPositionOffset;
            if (rawFlags.HasFlag(AudioClientBufferFlags.DataDiscontinuity))
                flags |= first ? AudioCapturePacketFlags.InitialDiscontinuity : AudioCapturePacketFlags.Discontinuity;
            if (rawFlags.HasFlag(AudioClientBufferFlags.TimestampError)) flags |= AudioCapturePacketFlags.TimestampUnreliable;
            if (rawFlags.HasFlag(AudioClientBufferFlags.Silent)) flags |= AudioCapturePacketFlags.Silence;
            timestamp = Math.Max(0,timestamp);
            int align = Info.ActualFormat.BlockAlign;
            if (data.Length == 0 || data.Length % align != 0)
            { Stopped?.Invoke(new(CaptureEndReason.InvalidPacket,"WASAPI returned an incomplete PCM frame.")); return; }
            if (rawFlags.HasFlag(AudioClientBufferFlags.Silent))
            {
                // WASAPI silence has no valid payload; NAudio's zero buffer is not the unsigned PCM8 silence value.
                for(int offset=0;offset<data.Length;)
                {
                    int bytes = Math.Min(silence.Length,data.Length-offset);
                    DataAvailable?.Invoke(silence.AsSpan(0,bytes),new(frame+offset/align,bytes/align,
                        checked(timestamp+(long)(offset/align)*TimeSpan.TicksPerSecond/Info.ActualFormat.SampleRate),flags));
                    offset += bytes; flags &= ~(AudioCapturePacketFlags.InitialDiscontinuity | AudioCapturePacketFlags.InitialPositionOffset);
                }
            }
            else DataAvailable?.Invoke(data,new(frame,data.Length/align,timestamp,flags));
        }

        private void OnStopped(Exception? error)
        {
            var reason = error == null ? CaptureEndReason.Stopped : error.HResult == unchecked((int)0x88890004)
                ? CaptureEndReason.DeviceLost : CaptureEndReason.BackendError;
            Stopped?.Invoke(new(reason,error == null ? null : $"{error.Message} (HRESULT 0x{error.HResult:X8})"));
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref stopping,1);
                if (!transportDisposed)
                {
                    // DisposeAsync joins the NAudio capture thread; StopRecording alone does not quiesce callbacks.
                    await transport.DisposeAsync().ConfigureAwait(false); transportDisposed = true;
                    transport.DataAvailable -= OnData; transport.Stopped -= OnStopped;
                }
            }
            finally { lifecycle.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref disposed,1); DataAvailable = null; Stopped = null;
        }
    }
}

internal interface IWindowsCapturePlatform
{
    IReadOnlyList<AudioCaptureDevice> Enumerate(WindowsAudioCaptureOptions options,CancellationToken token);
    IWindowsCaptureTransport Open(AudioCaptureRequest request,WindowsAudioCaptureOptions options,CancellationToken token);
}
internal interface IWindowsCaptureTransport : IAsyncDisposable
{
    AudioCaptureStreamInfo Info { get; }
    event CaptureDataAvailableHandler? DataAvailable;
    event Action<Exception?>? Stopped;
    void Start();
}
