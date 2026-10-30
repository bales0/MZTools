using System.Runtime.InteropServices;
using MZTools.Capture;

namespace MZTools.Platform.Audio;

/// <summary>PortAudio v19 input adapter. Native callbacks only forward borrowed PCM into the existing bounded pipeline.</summary>
public sealed class PortAudioBackend : IAudioCaptureBackend
{
    private static readonly SemaphoreSlim api=new(1,1);
    private static bool initialized;
    private static void Initialize(){NativeLibraries.Initialize();if(!initialized){Check(Pa_Initialize());initialized=true;}}
    private static void Check(int result){if(result<0)throw new IOException("PortAudio: "+Marshal.PtrToStringUTF8(Pa_GetErrorText(result)));}
    public async ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken=default)
    {
        if(!OperatingSystem.IsLinux()&&!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("Use WASAPI on Windows.");
        return await Task.Run(async()=>
        {
            await api.WaitAsync(cancellationToken);try
            {
                Initialize();int count=Pa_GetDeviceCount();Check(count);var devices=new List<AudioCaptureDevice>();
                for(int i=0;i<count;i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();var d=Device(i);if(d.MaxInputChannels<1)continue;var formats=new List<AudioCaptureFormat>();
                    foreach(int rate in new[]{22050,44100,48000,88200,96000,192000}.Append((int)d.DefaultSampleRate).Distinct())foreach(int channels in new[]{1,2}.Where(c=>c<=d.MaxInputChannels))foreach(var representation in new[]{AudioSampleRepresentation.Signed16,AudioSampleRepresentation.Signed24,AudioSampleRepresentation.Signed32,AudioSampleRepresentation.Float32})
                    {var format=new AudioCaptureFormat(rate,channels,representation);var input=Parameters(i,format,d);if(Pa_IsFormatSupported(ref input,0,rate)==0)formats.Add(format);}
                    devices.Add(new($"portaudio:{i}",Marshal.PtrToStringUTF8(d.Name)??$"Input {i}","PortAudio v19 · host API "+d.HostApi,formats.AsReadOnly()));
                }
                return (IReadOnlyList<AudioCaptureDevice>)devices.AsReadOnly();
            }finally{api.Release();}
        },cancellationToken);
    }
    public async ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request,CancellationToken cancellationToken=default)
    {
        if(!OperatingSystem.IsLinux()&&!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("Use WASAPI on Windows.");
        return await Task.Run(async()=>
        {
            await api.WaitAsync(cancellationToken);try
            {
                Initialize();if(!request.DeviceId.StartsWith("portaudio:",StringComparison.Ordinal)||!int.TryParse(request.DeviceId.AsSpan(10),out int index))throw new ArgumentException("Select an explicitly enumerated PortAudio input.");
                int count=Pa_GetDeviceCount();Check(count);if(index<0||index>=count)throw new IOException("Selected input is unavailable. Enumerate again.");
                var device=Device(index);if(request.Format.Channels>device.MaxInputChannels)throw new IOException("Selected channel count is unavailable.");
                var parameters=Parameters(index,request.Format,device);Check(Pa_IsFormatSupported(ref parameters,0,request.Format.SampleRate));
                cancellationToken.ThrowIfCancellationRequested();var session=new Session(request,new(request.DeviceId,Marshal.PtrToStringUTF8(device.Name)??request.DeviceId,"PortAudio v19",[request.Format]));
                try{session.Open(ref parameters);cancellationToken.ThrowIfCancellationRequested();return (IAudioCaptureSession)session;}catch{await session.DisposeAsync();throw;}
            }finally{api.Release();}
        },cancellationToken);
    }
    private static DeviceInfo Device(int index){nint pointer=Pa_GetDeviceInfo(index);if(pointer==0)throw new IOException("Input disappeared.");return Marshal.PtrToStructure<DeviceInfo>(pointer);}
    private static StreamParameters Parameters(int index,AudioCaptureFormat f,DeviceInfo d)=>new(){Device=index,Channels=f.Channels,SampleFormat=f.Representation switch{AudioSampleRepresentation.Float32=>1u,AudioSampleRepresentation.Signed32=>2u,AudioSampleRepresentation.Signed24=>4u,AudioSampleRepresentation.Signed16=>8u,AudioSampleRepresentation.Unsigned8=>32u,_=>throw new ArgumentException("Unsupported delivered sample representation.")},Latency=d.LowInputLatency};
    [StructLayout(LayoutKind.Sequential)]private struct DeviceInfo{public int Version;public nint Name;public int HostApi,MaxInputChannels,MaxOutputChannels;public double LowInputLatency,LowOutputLatency,HighInputLatency,HighOutputLatency,DefaultSampleRate;}
    [StructLayout(LayoutKind.Sequential)]private struct StreamParameters{public int Device,Channels;public nuint SampleFormat;public double Latency;public nint HostInfo;}
    [StructLayout(LayoutKind.Sequential)]private struct StreamTimes{public double InputAdc,Current,OutputDac;}
    [StructLayout(LayoutKind.Sequential)]private struct StreamInfo{public int Version;public double InputLatency,OutputLatency,SampleRate;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Callback(nint input,nint output,nuint frames,nint times,nuint flags,nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void Finished(nint user);
    private sealed class Session : IAudioCaptureSession
    {
        private readonly AudioCaptureRequest request;
        private readonly Callback callback;
        private readonly Finished finished;
        private int notified;
        private readonly SemaphoreSlim lifecycle=new(1,1);
        private nint stream;
        private long firstFrame;
        private int stopped,disposed;
        private bool started;
        private CaptureEndReason? failure;
        public AudioCaptureStreamInfo Info {get;private set;}
        public event AudioCaptureDataAvailable? DataAvailable;
        public event Action<AudioCaptureStopped>? Stopped;
        internal Session(AudioCaptureRequest request,AudioCaptureDevice device)
        {
            this.request=request;callback=OnData;finished=OnFinished;Info=new(device,request.Format,request.Format,"PortAudio delivered PCM. Host processing/conversion may occur; RAW and ADC precision are not certified. Timestamp origin: PortAudio inputBufferAdcTime in stream-clock seconds."){Processing=new("PortAudio v19","Host API input",false,null,true,null,"Unknown host/device effects; no RAW claim")};
        }
        internal void Open(ref StreamParameters input)
        {
            // Disable PortAudio's optional clipping/dither. Host conversion can still exist and is declared above.
            Check(Pa_OpenStream(out stream,ref input,0,request.Format.SampleRate,0,3,callback,0));
            Check(Pa_SetStreamFinishedCallback(stream,finished));
            var pointer=Pa_GetStreamInfo(stream);if(pointer==0)throw new IOException("PortAudio did not report delivered stream format.");var actual=Marshal.PtrToStructure<StreamInfo>(pointer);
            if(actual.SampleRate!=request.Format.SampleRate)throw new IOException("PortAudio reported a different delivered sample rate. No silent fallback is permitted.");
        }
        public async ValueTask StartAsync(CancellationToken cancellationToken=default)
        {
            await lifecycle.WaitAsync(cancellationToken);try{ObjectDisposedException.ThrowIf(disposed!=0,this);if(started||stopped!=0)throw new InvalidOperationException("Capture session cannot restart.");cancellationToken.ThrowIfCancellationRequested();Check(Pa_StartStream(stream));started=true;}finally{lifecycle.Release();}
        }
        private unsafe int OnData(nint input,nint output,nuint frames,nint times,nuint flags,nint user)
        {
            if(Volatile.Read(ref stopped)!=0||Volatile.Read(ref disposed)!=0)return 2;
            if(frames==0)return 0;
            if(input==0||frames>int.MaxValue/(uint)request.Format.BlockAlign){failure=CaptureEndReason.InvalidPacket;return 2;}
            int count=(int)frames;var timing=Marshal.PtrToStructure<StreamTimes>(times);AudioCapturePacketFlags packetFlags=0;
            if((flags&3)!=0)packetFlags|=AudioCapturePacketFlags.Discontinuity;
            long ticks=0;if(!double.IsFinite(timing.InputAdc)||timing.InputAdc<0||timing.InputAdc>long.MaxValue/1e7)packetFlags|=AudioCapturePacketFlags.TimestampUnreliable;else ticks=(long)(timing.InputAdc*1e7);
            try{DataAvailable?.Invoke(new ReadOnlySpan<byte>((void*)input,count*request.Format.BlockAlign),new(firstFrame,count,ticks,packetFlags));firstFrame+=count;return packetFlags.HasFlag(AudioCapturePacketFlags.Discontinuity)?2:0;}
            catch{failure=CaptureEndReason.BackendError;return 2;}
        }
        private void OnFinished(nint user)
        {if(Volatile.Read(ref stopped)==0&&failure is null)failure=CaptureEndReason.BackendError;PublishStopped();}
        private void PublishStopped()
        {if(Interlocked.Exchange(ref notified,1)==0)Stopped?.Invoke(new(failure??CaptureEndReason.Stopped,failure is null?null:"PortAudio stopped after a native error, disconnect or invalid packet."));}
        public async ValueTask StopAsync(CancellationToken cancellationToken=default)
        {
            await lifecycle.WaitAsync(cancellationToken);try
            {
                if(Interlocked.Exchange(ref stopped,1)!=0)return;
                if(started){int error=await Task.Run(()=>Pa_StopStream(stream),CancellationToken.None);if(error<0&&error!=-9983)failure=CaptureEndReason.BackendError;}
                PublishStopped();
            }finally{lifecycle.Release();}
        }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();await lifecycle.WaitAsync();try{if(Interlocked.Exchange(ref disposed,1)!=0)return;if(stream!=0){Check(Pa_CloseStream(stream));stream=0;}GC.KeepAlive(callback);GC.KeepAlive(finished);}finally{lifecycle.Release();}
        }
    }
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_Initialize();
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_GetDeviceCount();
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern nint Pa_GetDeviceInfo(int index);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern nint Pa_GetErrorText(int error);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_IsFormatSupported(ref StreamParameters input,nint output,double rate);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_OpenStream(out nint stream,ref StreamParameters input,nint output,double rate,nuint frames,nuint flags,Callback callback,nint user);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern nint Pa_GetStreamInfo(nint stream);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_SetStreamFinishedCallback(nint stream,Finished callback);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_StartStream(nint stream);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_StopStream(nint stream);
    [DllImport("mz-portaudio",CallingConvention=CallingConvention.Cdecl)]private static extern int Pa_CloseStream(nint stream);
}
