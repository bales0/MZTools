using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MZTools.Capture.Windows.Tests;

public class ExclusiveCaptureTests
{
    private static readonly AudioCaptureFormat Format=new(192000,2,AudioSampleRepresentation.Signed32,24);
    private static readonly TimeSpan Timeout=TimeSpan.FromSeconds(10);
    private static ExclusiveCaptureTransport Transport(FakeSource source,AudioCaptureFormat? format=null)=>new(new("test-only",format??Format),new() { Mode=WindowsAudioCaptureMode.Exclusive },()=>source);

    [Fact]
    public async Task NativePackets_KeepBitsPositionsAndOrderOnOneWorkerIncludingStopTail()
    {
        var source=new FakeSource(); var transport=Transport(source); var received=new List<byte>(); var positions=new List<long>();
        var got=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.DataAvailable+=(data,flags,position,time)=>{ received.AddRange(data.ToArray()); positions.Add(position); Assert.Equal(position*10,time); if(positions.Count==3) got.TrySetResult(); };
        Exception? failure=null; transport.Stopped+=error=>failure=error;
        await Task.Run(transport.Start).WaitAsync(Timeout);
        byte[][] chunks=[Enumerable.Repeat((byte)0xa5,8).ToArray(),Enumerable.Repeat((byte)0x17,16).ToArray(),Enumerable.Repeat((byte)0xe2,8).ToArray()];
        source.Enqueue(chunks[0],100); source.Enqueue(chunks[1],101); source.Enqueue(chunks[2],103);
        await got.Task.WaitAsync(Timeout); source.Tail=Enumerable.Repeat((byte)0x33,8).ToArray();
        await Task.WhenAll(transport.DisposeAsync().AsTask(),transport.DisposeAsync().AsTask()).WaitAsync(Timeout);
        Assert.Null(failure); Assert.Equal(chunks.SelectMany(c=>c).Concat(source.Tail),received);
        Assert.Equal(new long[]{100,101,103,104},positions); Assert.Equal(4,source.Released);
        Assert.Equal(1,source.Disposed); Assert.Equal(1,source.Stops);
        Assert.Single(source.Threads.Distinct()); Assert.NotEqual(Environment.CurrentManagedThreadId,source.Threads.First());
        Assert.False(transport.Info.Processing!.EngineConversionPossible); Assert.Equal(Format,transport.Info.ActualFormat);
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,128)]
    [InlineData(AudioSampleRepresentation.Float32,0)]
    public async Task SilentPackets_AreCanonicalWithoutReadingNullPointer(AudioSampleRepresentation representation,int silence)
    {
        var format=new AudioCaptureFormat(48000,1,representation); var source=new FakeSource(format.BlockAlign); await using var transport=Transport(source,format);
        var got=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.DataAvailable+=(data,flags,p,t)=>got.TrySetResult(data.ToArray());
        await Task.Run(transport.Start).WaitAsync(Timeout); source.Enqueue(new byte[format.BlockAlign*2],0,AudioClientBufferFlags.Silent);
        Assert.All(await got.Task.WaitAsync(Timeout),value=>Assert.Equal(silence,value));
        await transport.DisposeAsync(); Assert.Equal(1,source.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackOrOversizeFailure_ReleasesBorrowedPacketAndStops(bool oversize)
    {
        var source=new FakeSource(); await using var transport=Transport(source);
        var stopped=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Stopped+=e=>stopped.TrySetResult(e);
        transport.DataAvailable+=(d,f,p,t)=>throw new IOException("consumer failed");
        await Task.Run(transport.Start).WaitAsync(Timeout); source.Enqueue(new byte[oversize?40:8],0);
        var failure=await stopped.Task.WaitAsync(Timeout); Assert.NotNull(failure);
        Assert.Equal(1,source.Released); await transport.DisposeAsync(); Assert.Equal(1,source.Disposed); Assert.Equal(1,source.Stops);
    }

    [Fact]
    public async Task StopWhileInitializing_JoinsWithoutStartingOrDisposingUnderNativeCall()
    {
        var source=new FakeSource { BlockInitialize=true }; var transport=Transport(source);
        var start=Task.Run(transport.Start); await source.Entered.Task.WaitAsync(Timeout);
        var dispose=transport.DisposeAsync().AsTask(); Assert.False(dispose.IsCompleted); Assert.Equal(0,source.Disposed);
        source.Resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>start.WaitAsync(Timeout));
        await dispose.WaitAsync(Timeout); Assert.Equal(0,source.Starts); Assert.Equal(1,source.Disposed);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task InitializationFailure_IsExplicitAndDoesNotRetryOtherFormat()
    {
        var source=new FakeSource { InitializationError=new NotSupportedException("driver rejected") }; await using var transport=Transport(source);
        await Assert.ThrowsAsync<NotSupportedException>(()=>Task.Run(transport.Start).WaitAsync(Timeout));
        await transport.DisposeAsync(); Assert.Equal(0,source.Starts); Assert.Equal(1,source.Disposed);
    }

    [Fact]
    public async Task DisposingUnstartedTransport_DoesNotCreateSource()
    {
        int calls=0; var transport=new ExclusiveCaptureTransport(new("test",Format),new(),()=>{ calls++; return new FakeSource(); });
        await transport.DisposeAsync(); await transport.DisposeAsync(); Assert.Equal(0,calls);
        Assert.Throws<ObjectDisposedException>(transport.Start);
    }

    private sealed class FakeSource(int align=8):IExclusiveCaptureSource
    {
        private readonly ConcurrentQueue<(byte[] Bytes,long Position,AudioClientBufferFlags Flags)> queue=new();
        private EventWaitHandle? signal;
        private IntPtr borrowed;
        public readonly ConcurrentQueue<int> Threads=new();
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Resume=new(false);
        public bool BlockInitialize; public Exception? InitializationError; public byte[]? Tail;
        public int Released,Disposed,Starts,Stops;
        public long PeriodTicks=>10000;
        private void ThreadCheck()=>Threads.Enqueue(Environment.CurrentManagedThreadId);
        public int Initialize(EventWaitHandle value) { ThreadCheck(); signal=value; Entered.TrySetResult(); if(BlockInitialize) Resume.Wait(); if(InitializationError!=null) throw InitializationError; return 4; }
        public void Start() { ThreadCheck(); Starts++; }
        public void Stop() { ThreadCheck(); Stops++; if(Tail!=null) queue.Enqueue((Tail,104,AudioClientBufferFlags.None)); }
        public void Enqueue(byte[] bytes,long position,AudioClientBufferFlags flags=AudioClientBufferFlags.None) { queue.Enqueue((bytes,position,flags)); signal!.Set(); }
        public ExclusivePacket Read()
        {
            ThreadCheck(); Assert.Equal(IntPtr.Zero,borrowed);
            if(!queue.TryDequeue(out var packet)) return new(IntPtr.Zero,0,0,0,0);
            if(!packet.Flags.HasFlag(AudioClientBufferFlags.Silent)) { borrowed=Marshal.AllocHGlobal(packet.Bytes.Length); Marshal.Copy(packet.Bytes,0,borrowed,packet.Bytes.Length); }
            return new(borrowed,packet.Bytes.Length/align,packet.Flags,packet.Position,packet.Position*10);
        }
        public void Release(int frames) { ThreadCheck(); Released++; if(borrowed!=IntPtr.Zero) { Marshal.FreeHGlobal(borrowed); borrowed=IntPtr.Zero; } }
        public void Dispose() { ThreadCheck(); Disposed++; Assert.Equal(IntPtr.Zero,borrowed); Resume.Dispose(); }
    }
}
