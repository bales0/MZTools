using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MZTools.Capture.Windows;

internal sealed class WasapiCapturePlatform : IWindowsCapturePlatform
{
    internal const string BackendVersion = "NAudio.Wasapi 3.3.0";

    public IReadOnlyList<AudioCaptureDevice> Enumerate(WindowsAudioCaptureOptions options,CancellationToken token)
    {
        options.ValidateSupported(); token.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active);
        var result = new List<AudioCaptureDevice>();
        foreach(var device in devices)
        {
            using (device)
            {
                token.ThrowIfCancellationRequested();
                using var client = device.CreateAudioClient();
                var formats = new List<AudioCaptureFormat>();
                try { formats.Add(WasapiWaveFormats.FromNative(client.MixFormat)); }
                catch (NotSupportedException) { /* Multichannel/non-PCM mix is not advertised as mono/stereo PCM. */ }
                if (options.AllowEngineFormatConversion)
                {
                    foreach(int rate in new[] {22050,44100,48000,88200,96000})
                        foreach(int channels in new[] {1,2})
                            foreach(var (representation,validBits) in new[] {
                                (AudioSampleRepresentation.Unsigned8,8),(AudioSampleRepresentation.Signed16,16),
                                (AudioSampleRepresentation.Signed24,24),(AudioSampleRepresentation.Signed32,24),
                                (AudioSampleRepresentation.Signed32,32),(AudioSampleRepresentation.Float32,32) })
                            {
                                token.ThrowIfCancellationRequested();
                                var format = new AudioCaptureFormat(rate,channels,representation,validBits);
                                if (client.IsFormatSupported(AudioClientShareMode.Shared,WasapiWaveFormats.ToNative(format))) formats.Add(format);
                            }
                }
                result.Add(new(device.ID,device.FriendlyName,"Windows WASAPI input",Array.AsReadOnly(formats.Distinct().ToArray())));
            }
        }
        return result.AsReadOnly();
    }

    public IWindowsCaptureTransport Open(AudioCaptureRequest request,WindowsAudioCaptureOptions options,CancellationToken token)
    {
        options.ValidateSupported();
        token.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = enumerator.GetDevice(request.DeviceId);
        WasapiRecorder? recorder = null;
        try
        {
            if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
                throw new InvalidOperationException("Select an active input endpoint. Output/loopback devices are not recording inputs.");
            AudioCaptureFormat? mix;
            AudioCaptureFormat actual;
            WaveFormat nativeMix;
            using (var client = device.CreateAudioClient())
            {
                nativeMix = client.MixFormat;
                try { mix = WasapiWaveFormats.FromNative(nativeMix); }
                catch (NotSupportedException) { mix = null; }
                actual = Negotiate(request,options,mix);
            }
            token.ThrowIfCancellationRequested();
            // Preserve the exact native mix descriptor/channel mask when shared capture requests that format.
            var requestedNative = actual == mix ? nativeMix : WasapiWaveFormats.ToNative(actual);
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithFormat(requestedNative)
                .WithBufferLength(options.BufferMilliseconds).WithEventSync().WithMmcssThreadPriority("Pro Audio");
            if (options.RequestRaw) builder.WithRawMode();
            recorder = builder.Build();
            AudioCaptureFormat delivered = WasapiWaveFormats.FromNative(recorder.WaveFormat);
            if (actual != delivered) throw new InvalidDataException("WASAPI recorder changed the negotiated container format.");
            bool conversion = actual != mix;
            var processing = new AudioCaptureProcessing(BackendVersion,options.Mode.ToString(),options.RequestRaw,null,conversion,mix,
                options.RequestRaw ? "RAW requested; pending Start. Endpoint/driver/hardware processing is not certified absent."
                    : "RAW disabled by caller. Windows/driver may apply enhancements, AGC, noise suppression or echo cancellation; status unknown.");
            var info = new AudioCaptureStreamInfo(new(device.ID,device.FriendlyName,"Windows WASAPI input",Array.AsReadOnly(new[] {actual})),
                request.Format,actual,Describe(processing)) { Processing = processing };
            return new Transport(recorder,device,info);
        }
        catch { recorder?.Dispose(); device.Dispose(); throw; }
    }

    internal static AudioCaptureFormat Negotiate(AudioCaptureRequest request,WindowsAudioCaptureOptions options,
        AudioCaptureFormat? mix)
    {
        options.ValidateSupported();
        if (request.Format == mix || options.AllowEngineFormatConversion) return request.Format;
        if (request.AllowFormatFallback && mix != null) return mix;
        throw new NotSupportedException("The requested format differs from the endpoint mix format. Choose the actual mix format, explicitly allow format fallback, or explicitly permit shared-engine conversion.");
    }

    private static string Describe(AudioCaptureProcessing p) => $"{p.BackendVersion}; {p.AccessMode}; {p.EffectsStatus} " +
        (p.EngineConversionPossible ? "Shared-engine rate/sample-format/channel conversion is explicitly allowed and may occur. " : "No application-requested engine format conversion. ") +
        "Delivered container/valid bits do not establish physical ADC resolution.";

    private sealed class Transport : IWindowsCaptureTransport
    {
        private readonly WasapiRecorder recorder;
        private readonly MMDevice device;
        private AudioCaptureStreamInfo info;
        private bool disposed;
        public AudioCaptureStreamInfo Info => info;
        public event CaptureDataAvailableHandler? DataAvailable;
        public event Action<Exception?>? Stopped;
        internal Transport(WasapiRecorder recorder,MMDevice device,AudioCaptureStreamInfo info)
        {
            this.recorder = recorder; this.device = device; this.info = info;
            recorder.DataAvailable += Data; recorder.RecordingStopped += End;
        }
        private void Data(ReadOnlySpan<byte> data,AudioClientBufferFlags flags,long frame,long ticks) => DataAvailable?.Invoke(data,flags,frame,ticks);
        private void End(object? sender,StoppedEventArgs ended) => Stopped?.Invoke(ended.Exception);
        public void Start()
        {
            recorder.StartRecording();
            var processing = info.Processing! with { RawActive = info.Processing!.RawRequested,
                EffectsStatus = info.Processing!.RawRequested
                    ? "RAW stream initialized successfully; system capture effects bypassed. Always-on endpoint/driver/hardware processing remains unknown."
                    : info.Processing!.EffectsStatus };
            info = info with { Processing = processing, ProcessingDescription = Describe(processing) };
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { await recorder.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                recorder.DataAvailable -= Data; recorder.RecordingStopped -= End;
                DataAvailable = null; Stopped = null; device.Dispose();
            }
        }
    }
}

internal static class WasapiWaveFormats
{
    internal static WaveFormat ToNative(AudioCaptureFormat format) => new WaveFormatExtensible(format.SampleRate,
        format.BitsPerSample,format.Channels,format.Representation == AudioSampleRepresentation.Float32,
        format.ValidBitsPerSample,format.Channels == 1 ? 4 : 3);

    internal static AudioCaptureFormat FromNative(WaveFormat format)
    {
        int valid = format.BitsPerSample;
        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible)
        {
            Guid subFormat;
            if (format is WaveFormatExtensible extensible) { subFormat = extensible.SubFormat; valid = extensible.ValidBitsPerSample; }
            else if (format is WaveFormatExtraData extra && extra.ExtraData.Length >= 22)
            { valid = BinaryPrimitives.ReadUInt16LittleEndian(extra.ExtraData); subFormat = new Guid(extra.ExtraData.AsSpan(6,16)); }
            else throw new NotSupportedException("Unrecognized extensible capture format.");
            encoding = subFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM ? WaveFormatEncoding.Pcm :
                subFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? WaveFormatEncoding.IeeeFloat : throw new NotSupportedException("Compressed input format is unsupported.");
        }
        AudioSampleRepresentation representation = (encoding,format.BitsPerSample) switch
        {
            (WaveFormatEncoding.Pcm,8) => AudioSampleRepresentation.Unsigned8,
            (WaveFormatEncoding.Pcm,16) => AudioSampleRepresentation.Signed16,
            (WaveFormatEncoding.Pcm,24) => AudioSampleRepresentation.Signed24,
            (WaveFormatEncoding.Pcm,32) => AudioSampleRepresentation.Signed32,
            (WaveFormatEncoding.IeeeFloat,32) => AudioSampleRepresentation.Float32,
            _ => throw new NotSupportedException("Only PCM8/16/24/32 or IEEE float32 capture is supported.")
        };
        if (format.Channels is not (1 or 2)) throw new NotSupportedException("Choose a mono or stereo input format.");
        var result = new AudioCaptureFormat(format.SampleRate,format.Channels,representation,valid);
        if (format.BlockAlign != result.BlockAlign) throw new InvalidDataException("Invalid input block alignment.");
        return result;
    }
}
