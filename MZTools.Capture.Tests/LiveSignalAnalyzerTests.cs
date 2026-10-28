using System.Buffers.Binary;

namespace MZTools.Capture.Tests;

public class LiveSignalAnalyzerTests
{
    private static async Task<LiveSignalAnalyzer> Create(AudioCaptureFormat? format=null,LiveSignalAnalyzerOptions? options=null)
    {
        format??=new(48000,2,AudioSampleRepresentation.Float32);
        var analyzer=new LiveSignalAnalyzer(options);
        await analyzer.StartAsync(new(new("test","test","test",[]),format,format,"test")); return analyzer;
    }
    private static byte[] FloatPcm(int count,int channels,Func<int,int,float> sample)
    {
        var data=new byte[count*channels*4];
        for(int i=0;i<count;i++) for(int ch=0;ch<channels;ch++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan((i*channels+ch)*4),sample(i,ch));
        return data;
    }
    [Fact]
    public async Task CalibratedStereoTone_HannAmplitudeDcRmsAndExactIntervals()
    {
        await using var analyzer=await Create();
        analyzer.Process(FloatPcm(4096,2,(i,ch)=>(float)((ch==0?.1:-.05)+(ch==0?.5:.25)*Math.Sin(2*Math.PI*128*i/4096))),new(0,4096,0));
        var snapshot=analyzer.CreateSnapshot(new() { LevelWindowSeconds=4096/48000.0 })!;
        Assert.Equal(1500,snapshot.LeftDominantHz); Assert.Equal(1500,snapshot.RightDominantHz);
        Assert.Equal(-6.0205999,snapshot.Spectrum[128].LeftDbfs,5); Assert.Equal(-12.0411998,snapshot.Spectrum[128].RightDbfs,5);
        Assert.Equal(.1,snapshot.Levels[0].Dc,7); Assert.Equal(-.05,snapshot.Levels[1].Dc,7);
        Assert.Equal(Math.Sqrt(.1*.1+.5*.5/2),snapshot.Levels[0].Rms,7);
        Assert.Equal(.6,snapshot.Levels[0].Peak,6); Assert.Equal(4096,snapshot.Levels[0].Samples);
        Assert.Equal(0,snapshot.SpectrumStartFrame); Assert.Equal(4096,snapshot.SpectrumEndFrame);
        Assert.Equal(0,snapshot.LevelStartFrame); Assert.Equal(4096,snapshot.LevelEndFrame);
        Assert.Equal(-20,snapshot.Spectrum[0].LeftDbfs,5);
        Assert.All(snapshot.Spectrum,b=>Assert.True(double.IsFinite(b.LeftDbfs)&&double.IsFinite(b.RightDbfs)));
    }
    [Fact]
    public async Task Silence_FiniteSpectrumButNoInventedDominantFrequencyOrDbForZero()
    {
        await using var analyzer=await Create(); analyzer.Process(new byte[4096*8],new(0,4096,0));
        var s=analyzer.CreateSnapshot()!; Assert.Null(s.LeftDominantHz); Assert.Null(s.RightDominantHz);
        Assert.All(s.Levels,l=> { Assert.Null(l.RmsDbfs); Assert.Null(l.PeakDbfs); Assert.Equal(0,l.ClippedSamples); });
        Assert.All(s.Spectrum,b=>Assert.Equal(-160,b.LeftDbfs));
    }
    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,8)]
    [InlineData(AudioSampleRepresentation.Signed16,16)]
    [InlineData(AudioSampleRepresentation.Signed24,24)]
    [InlineData(AudioSampleRepresentation.Signed32,32)]
    [InlineData(AudioSampleRepresentation.Signed32,24)]
    public async Task IntegerPrecision_FullScaleAndPositiveNeighbourAreDistinguished(AudioSampleRepresentation representation,int valid)
    {
        var f=new AudioCaptureFormat(48000,1,representation,valid); await using var analyzer=await Create(f);
        int bytes=f.BlockAlign; var data=new byte[bytes*3]; long step=1L<<(f.BitsPerSample-valid),max=(1L<<(f.BitsPerSample-1))-step,min=-(1L<<(f.BitsPerSample-1));
        void Write(int index,long value)
        {
            if(bytes==1) data[index]=(byte)(value+128);
            else for(int b=0;b<bytes;b++) data[index*bytes+b]=(byte)(value>>(8*b));
        }
        Write(0,max-step); Write(1,max); Write(2,min); analyzer.Process(data,new(0,3,0));
        var s=analyzer.CreateSnapshot()!; Assert.Equal(2,s.Levels[0].ClippedSamples); Assert.Equal(2,s.Levels[0].ClippingHold);
        Assert.Equal((max-step)/Math.Pow(2,f.BitsPerSample-1),s.Waveform[0].LeftMax,12);
        analyzer.ResetClippingHold(.5); s=analyzer.CreateSnapshot()!; Assert.Equal(0,s.Levels[0].ClippingHold); Assert.Equal(3,s.Levels[0].ClippedSamples);
    }
    [Fact]
    public async Task BoundedHistory_WrapsAndEnvelopeRetainsSingleFrameSpikes()
    {
        await using var analyzer=await Create(new(1000,1,AudioSampleRepresentation.Float32),new() { HistorySeconds=.05 });
        analyzer.Process(FloatPcm(1000,1,(i,_)=>i==978?1:0),new(0,1000,0));
        var s=analyzer.CreateSnapshot(new() { WaveSeconds=.05,WaveBuckets=5 })!;
        Assert.Equal(950,s.HistoryStartFrame); Assert.Equal(1000,s.HistoryEndFrame); Assert.Equal(400,s.HistoryBufferBytes);
        Assert.Equal(1,s.Waveform.Max(b=>b.LeftMax)); Assert.Equal(5,s.Waveform.Count); Assert.Empty(s.Spectrum);
        Assert.Equal(950,s.Waveform[0].FirstFrame); Assert.Equal(1000,s.Waveform[^1].EndFrame);
        s=analyzer.CreateSnapshot(new() { WaveSeconds=.001,WaveEndFrame=979 })!;
        Assert.Single(s.Waveform); Assert.Equal(1,s.Waveform[0].LeftMax);
    }
    [Fact]
    public async Task GapsAndNonFiniteSamples_ResetContiguousPreviewWithoutInventingSilence()
    {
        await using var analyzer=await Create(new(1000,1,AudioSampleRepresentation.Float32));
        analyzer.Process(FloatPcm(20,1,(_,_)=>.5f),new(0,20,0));
        analyzer.Process(FloatPcm(20,1,(_,_)=>-.5f),new(30,20,0));
        var s=analyzer.CreateSnapshot()!; Assert.Equal(30,s.HistoryStartFrame); Assert.Equal(1,s.PreviewDiscontinuities); Assert.Equal(-.5,s.Levels[0].Dc);
        analyzer.Process(FloatPcm(3,1,(i,_)=>i==1?float.NaN:.25f),new(50,3,0));
        s=analyzer.CreateSnapshot()!; Assert.Equal(52,s.HistoryStartFrame); Assert.Equal(1,s.InvalidFrames); Assert.Equal(1,s.Levels[0].Samples); Assert.Equal(.25,s.Levels[0].Dc);
    }
    [Fact]
    public async Task PacketPartitioning_DoesNotChangeWaveOrFft()
    {
        var data=FloatPcm(8192,2,(i,ch)=>(float)(.2*Math.Sin(i*.07+ch)));
        await using var a=await Create(); await using var b=await Create();
        a.Process(data,new(0,8192,0));
        for(int first=0;first<8192;first+=137) { int count=Math.Min(137,8192-first); b.Process(data.AsSpan(first*8,count*8),new(first,count,first)); }
        Assert.Equal(a.CreateSnapshot()!.Waveform,b.CreateSnapshot()!.Waveform); Assert.Equal(a.CreateSnapshot()!.Spectrum,b.CreateSnapshot()!.Spectrum);
    }
    [Theory]
    [InlineData(.00002)]
    [InlineData(.001)]
    [InlineData(.05)]
    public async Task Trigger_TinyWindowsStayValidAndCrossingsAreCentered(double seconds)
    {
        await using var analyzer=await Create();
        analyzer.Process(FloatPcm(10000,2,(i,_)=>i%400<200?-1:1),new(0,10000,0));
        var s=analyzer.CreateSnapshot(new() { TriggerEnabled=true,WaveSeconds=seconds,WaveBuckets=4096 })!;
        Assert.InRange(s.WaveEndFrame-s.WaveStartFrame,1,Math.Max(1,Math.Round(seconds*48000)));
        Assert.All(s.Waveform,b=>Assert.True(b.FirstFrame>=s.HistoryStartFrame&&b.EndFrame<=s.HistoryEndFrame));
        if(seconds==.05) Assert.True(s.Triggered);
    }
    [Fact]
    public async Task TriggerWithoutCrossing_KeepsRequestedLatestSpan()
    {
        await using var analyzer=await Create(); analyzer.Process(FloatPcm(10000,2,(_,_)=>.2f),new(0,10000,0));
        var s=analyzer.CreateSnapshot(new() { TriggerEnabled=true,WaveSeconds=.05 })!;
        Assert.False(s.Triggered); Assert.Equal(7600,s.WaveStartFrame); Assert.Equal(10000,s.WaveEndFrame);
    }
    [Fact]
    public async Task InvalidOptions_AreRejectedBeforeAllocationOrDrawing()
    {
        var f=new AudioCaptureFormat(96000,2,AudioSampleRepresentation.Signed24);
        await Assert.ThrowsAsync<ArgumentException>(()=>Create(f,new() { MaximumHistoryBytes=10 }));
        await using var analyzer=await Create();
        Assert.Throws<ArgumentOutOfRangeException>(()=>analyzer.CreateSnapshot(new() { FftSize=123 }));
        Assert.Throws<ArgumentOutOfRangeException>(()=>analyzer.CreateSnapshot(new() { WaveSeconds=double.NaN }));
        Assert.Throws<InvalidDataException>(()=>analyzer.Process(new byte[1],new(0,1,0)));
    }
}
