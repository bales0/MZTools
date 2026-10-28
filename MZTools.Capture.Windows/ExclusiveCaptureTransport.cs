using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MZTools.Capture.Windows;

internal static class NativeWasapiFormats
{
    internal static IEnumerable<AudioCaptureFormat> Candidates()
    {
        foreach(int rate in new[]{8000,11025,16000,22050,24000,32000,44100,48000,64000,88200,96000,128000,176400,192000,352800,384000})
        foreach(int channels in new[]{1,2})
        foreach(var (representation,valid) in new[]{(AudioSampleRepresentation.Unsigned8,8),(AudioSampleRepresentation.Signed16,16),(AudioSampleRepresentation.Signed24,24),(AudioSampleRepresentation.Signed32,24),(AudioSampleRepresentation.Signed32,32),(AudioSampleRepresentation.Float32,32)})
            yield return new(rate,channels,representation,valid);
    }
    internal static WaveFormat? Supported(AudioClient client,AudioCaptureFormat format)
    {
        var extended=WasapiWaveFormats.ToNative(format);
        if(client.IsFormatSupported(AudioClientShareMode.Exclusive,extended)) return extended;
        // Some drivers accept the equivalent full-width WAVEFORMATEX descriptor only.
        if(format.ValidBitsPerSample!=format.BitsPerSample) return null;
        var classic=format.Representation==AudioSampleRepresentation.Float32?WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate,format.Channels):new WaveFormat(format.SampleRate,format.BitsPerSample,format.Channels);
        return client.IsFormatSupported(AudioClientShareMode.Exclusive,classic)?classic:null;
    }
}

internal readonly record struct ExclusivePacket(IntPtr Data,int Frames,AudioClientBufferFlags Flags,long Position,long Timestamp);
internal interface IExclusiveCaptureSource:IDisposable
{
    int Initialize(EventWaitHandle signal);
    void Start();
    void Stop();
    ExclusivePacket Read();
    void Release(int frames);
    long PeriodTicks { get; }
}

/// <summary>Own capture thread, GetBuffer/ReleaseBuffer loop; never uses shared-only GetNextPacketSize.</summary>
internal sealed class ExclusiveCaptureTransport:IWindowsCaptureTransport
{
    private readonly AudioCaptureRequest request;
    private readonly Func<IExclusiveCaptureSource> factory;
    private readonly AutoResetEvent signal=new(false);
    private readonly ManualResetEvent stop=new(false);
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously),done=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate=new();
    private bool started,disposed;
    private Task? disposal;
    private AudioCaptureStreamInfo info;
    public AudioCaptureStreamInfo Info=>Volatile.Read(ref info);
    public event CaptureDataAvailableHandler? DataAvailable;
    public event Action<Exception?>? Stopped;
    internal ExclusiveCaptureTransport(AudioCaptureRequest request,WindowsAudioCaptureOptions options,Func<IExclusiveCaptureSource>? factory=null,string? friendlyName=null)
    {
        this.request=request; this.factory=factory??(()=>new WasapiExclusiveSource(request,options));
        var processing=new AudioCaptureProcessing("NAudio.AudioClient 3.3.0 / MZTools exclusive packet transport","Exclusive",options.RequestRaw,null,false,null,
            "Exact driver-accepted format; Windows shared mix/conversion bypassed. RAW checkbox is shared-only. Endpoint/firmware/physical ADC processing and resolution remain unknown.");
        info=new(new(request.DeviceId,friendlyName??request.DeviceId,"Windows WASAPI exclusive",Array.AsReadOnly(new[]{request.Format})),request.Format,request.Format,processing.EffectsStatus) { Processing=processing };
    }
    public void Start()
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this); if(started) throw new InvalidOperationException("Exclusive transport already started."); started=true;
            var worker=new Thread(Run) { IsBackground=true,Name="MZTools native WASAPI input" }; worker.SetApartmentState(ApartmentState.MTA); worker.Start();
        }
        ready.Task.GetAwaiter().GetResult();
    }
    private void Run()
    {
        Exception? error=null;
        try
        {
            using var source=factory();
            int frames=source.Initialize(signal);
            if(frames<=0||(long)frames*request.Format.BlockAlign>64*1024*1024) throw new InvalidDataException("Native buffer exceeds the capture memory bound.");
            var buffer=new byte[checked(frames*request.Format.BlockAlign)];
            using var mmcss=new CaptureMmcss();
            info=Info with { ProcessingDescription=Info.ProcessingDescription+$" Native buffer {frames} frames; event period {source.PeriodTicks} ×100 ns; MMCSS Pro Audio {mmcss.Active}." };
            if(stop.WaitOne(0)) throw new OperationCanceledException("Exclusive start cancelled before streaming.");
            source.Start(); ready.TrySetResult();
            try
            {
                WaitHandle[] events=[stop,signal];
                while(WaitHandle.WaitAny(events,500)!=0)
                {
                    if(stop.WaitOne(0)) break;
                    Pump(source,buffer,frames,128); // bounded even if a broken driver never reports empty
                }
            }
            finally { source.Stop(); }
            Pump(source,buffer,frames,128); // consume already available tail after Stop; no fabricated frames
        }
        catch(Exception failure) { error=failure; ready.TrySetException(failure); }
        finally
        {
            try { Stopped?.Invoke(error); } catch { /* Consumer shutdown cannot strand the joining caller. */ }
            done.TrySetResult();
        }
    }
    private void Pump(IExclusiveCaptureSource source,byte[] buffer,int maximumFrames,int maximumPackets)
    {
        for(int i=0;i<maximumPackets;i++)
        {
            var packet=source.Read(); if(packet.Frames==0) return;
            try
            {
                if(packet.Frames<0||packet.Frames>maximumFrames) throw new InvalidDataException("Driver packet exceeds the initialized buffer.");
                int bytes=checked(packet.Frames*request.Format.BlockAlign);
                if(packet.Flags.HasFlag(AudioClientBufferFlags.Silent)) buffer.AsSpan(0,bytes).Fill(request.Format.Representation==AudioSampleRepresentation.Unsigned8?(byte)128:(byte)0);
                else { if(packet.Data==IntPtr.Zero) throw new InvalidDataException("Null native capture packet."); Marshal.Copy(packet.Data,buffer,0,bytes); }
                DataAvailable?.Invoke(buffer.AsSpan(0,bytes),packet.Flags,packet.Position,packet.Timestamp);
            }
            finally { source.Release(packet.Frames); }
        }
        throw new IOException("Native capture did not drain within its bounded packet budget.");
    }
    public ValueTask DisposeAsync()
    {
        lock(gate)
        {
            if(disposal!=null) return new(disposal);
            disposed=true; stop.Set();
            if(!started) { ready.TrySetCanceled(); done.TrySetResult(); }
            disposal=FinishDispose(); return new(disposal);
        }
    }
    private async Task FinishDispose()
    {
        await done.Task.ConfigureAwait(false);
        DataAvailable=null; Stopped=null;
        signal.Dispose(); stop.Dispose();
    }
}

internal sealed class WasapiExclusiveSource(AudioCaptureRequest request,WindowsAudioCaptureOptions options):IExclusiveCaptureSource
{
    private MMDeviceEnumerator? enumerator;
    private MMDevice? device;
    private AudioClient? client;
    private AudioCaptureClient? capture;
    public long PeriodTicks { get; private set; }
    public int Initialize(EventWaitHandle signal)
    {
        enumerator=new(); device=enumerator.GetDevice(request.DeviceId);
        if(device.DataFlow!=DataFlow.Capture||device.State!=DeviceState.Active) throw new InvalidOperationException("Capture endpoint became unavailable.");
        client=device.CreateAudioClient();
        var format=NativeWasapiFormats.Supported(client,request.Format)??throw new NotSupportedException("Driver rejected the exact native format at Start.");
        PeriodTicks=Math.Max(client.MinimumDevicePeriod,options.BufferMilliseconds*10000L);
        try { client.Initialize(AudioClientShareMode.Exclusive,AudioClientStreamFlags.EventCallback,PeriodTicks,PeriodTicks,format,Guid.Empty); }
        catch(COMException e) when(unchecked((uint)e.HResult)==0x88890019) // AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED
        {
            int aligned=client.BufferSize; client.Dispose(); client=device.CreateAudioClient();
            if(aligned<=0) throw;
            PeriodTicks=(long)Math.Round(10_000_000.0*aligned/request.Format.SampleRate,MidpointRounding.AwayFromZero);
            format=NativeWasapiFormats.Supported(client,request.Format)??throw new NotSupportedException("Native format changed during buffer alignment retry.");
            client.Initialize(AudioClientShareMode.Exclusive,AudioClientStreamFlags.EventCallback,PeriodTicks,PeriodTicks,format,Guid.Empty);
        }
        client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()); capture=client.AudioCaptureClient;
        return client.BufferSize;
    }
    public void Start()=>client!.Start();
    public void Stop()=>client!.Stop();
    public ExclusivePacket Read()
    {
        // BUFFER_EMPTY yields zero frames. A native failure is explicit, rather than being hidden as silence.
        IntPtr data=capture!.GetBuffer(out int frames,out var flags,out long position,out long ticks);
        return new(data,frames,flags,position,ticks);
    }
    public void Release(int frames)=>capture!.ReleaseBuffer(frames);
    public void Dispose()
    {
        try { capture?.Dispose(); }
        finally { try { client?.Dispose(); } finally { try { device?.Dispose(); } finally { enumerator?.Dispose(); } } }
    }
}

internal sealed class CaptureMmcss:IDisposable
{
    private readonly IntPtr handle;
    internal bool Active=>handle!=IntPtr.Zero;
    internal CaptureMmcss() { handle=AvSetMmThreadCharacteristics("Pro Audio",out _); }
    public void Dispose() { if(handle!=IntPtr.Zero) AvRevertMmThreadCharacteristics(handle); }
    [DllImport("avrt.dll",EntryPoint="AvSetMmThreadCharacteristicsW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string task,out uint index);
    [DllImport("avrt.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
