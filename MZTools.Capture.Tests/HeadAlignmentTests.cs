using System.Buffers.Binary;
using System.Diagnostics;

namespace MZTools.Capture.Tests;

public class HeadAlignmentTests
{
    private static async Task<HeadAlignmentMeasurement> Measure(Func<int,int,double> signal,HeadAlignmentOptions? options=null,int channels=2,int n=4096,int rate=48000,int packet=4096)
    {
        var format=new AudioCaptureFormat(rate,channels,AudioSampleRepresentation.Float32); await using var analyzer=new LiveSignalAnalyzer();
        await analyzer.StartAsync(new(new("test","test","test",[]),format,format,"test"));
        for(int first=0;first<n;first+=packet)
        {
            int count=Math.Min(packet,n-first); var bytes=new byte[count*channels*4];
            for(int i=0;i<count;i++) for(int ch=0;ch<channels;ch++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i*channels+ch)*4),(float)signal(first+i,ch));
            analyzer.Process(bytes,new(first,count,first));
        }
        return analyzer.CreateSnapshot(new() { FftSize=n,Alignment=options??new() })!.Alignment!;
    }
    private static double Noise(int i,int seed=1)
    {
        uint value=unchecked((uint)i*747796405u+(uint)seed*2891336453u); value=((value>>((int)(value>>28)+4))^value)*277803737u; value=(value>>22)^value;
        return (value/(double)uint.MaxValue*2-1)*.4;
    }
    [Fact]
    public async Task MonoIndicators_AreScopedWithoutStereoOrAbsoluteAngleClaim()
    {
        var m=await Measure((i,_)=>.4*Math.Sin(2*Math.PI*1500*i/48000),channels:1);
        Assert.Equal(0,m.StartFrame); Assert.Equal(4096,m.EndFrame); var channel=Assert.Single(m.Channels);
        Assert.True(channel.Assessable); Assert.Equal(.4/Math.Sqrt(2),channel.AcRms,5); Assert.True(channel.RiseCount>100); Assert.True(channel.FallCount>100);
        Assert.InRange(channel.HighBandPercent!.Value,0,.001); Assert.NotNull(channel.RiseMicroseconds);
        Assert.False(m.Stereo.Assessable); Assert.Null(m.Stereo.SignalPhaseDegrees); Assert.Null(m.Stereo.DelaySamples);
    }
    [Fact]
    public async Task SpectralRatio_SeparatesKnownLowAndHighBandPower()
    {
        var m=await Measure((i,_)=>.4*Math.Sin(2*Math.PI*1500*i/48000)+.2*Math.Sin(2*Math.PI*6000*i/48000),channels:1);
        Assert.Equal(20,m.Channels[0].HighBandPercent!.Value,3);
    }
    [Theory]
    [InlineData(48000)]
    [InlineData(96000)]
    public async Task RampedTransitions_HaveCorrectTimeUnitsAndGainInvariantSlope(int rate)
    {
        double Signal(int i) { int phase=i%200; return phase<60?-.4:phase<80?-.4+(phase-60)*.04:phase<160?.4:phase<180?.4-(phase-160)*.04:-.4; }
        var m=await Measure((i,ch)=>Signal(i)*(ch==0?1:.5),rate:rate);
        Assert.Equal(16e6/rate,m.Channels[0].RiseMicroseconds!.Value,2); Assert.Equal(16e6/rate,m.Channels[0].FallMicroseconds!.Value,2);
        Assert.Equal(m.Channels[0].NormalizedSlopePerMs!.Value,m.Channels[1].NormalizedSlopePerMs!.Value,6);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SilentClippedOrDcOnly_AreNotAssessable(int kind)
    {
        var m=await Measure((i,_)=>kind==0?0:kind==1?1.1*Math.Sin(i*.2):.4,new() { KnownToneHz=1000 });
        Assert.All(m.Channels,c=>Assert.False(c.Assessable)); Assert.False(m.Stereo.Assessable);
        Assert.Null(m.Stereo.SignalPhaseDegrees); Assert.Null(m.Stereo.DelaySamples);
    }
    [Theory]
    [InlineData(30)]
    [InlineData(-70)]
    [InlineData(180)]
    public async Task KnownTone_ReportsSignalPhaseButNeverAmbiguousAbsoluteDelay(double degrees)
    {
        var m=await Measure((i,ch)=>(ch==0?.4:.2)*Math.Sin(2*Math.PI*1000*i/48000+(ch==0?0:degrees*Math.PI/180)),new() { KnownToneHz=1000 });
        var s=m.Stereo; Assert.True(s.Assessable,s.Status); Assert.InRange(s.Coherence!.Value,.9999,1); Assert.InRange(s.TonePurity!.Value,.9999,1); Assert.InRange(s.PhaseStability!.Value,.9999,1);
        double difference=(s.SignalPhaseDegrees!.Value-degrees)*Math.PI/180;
        Assert.InRange(Math.Abs(Math.Atan2(Math.Sin(difference),Math.Cos(difference))),0,1e-5);
        double Rms(int ch) { var values=Enumerable.Range(0,4096).Select(i=>(ch==0?.4:.2)*Math.Sin(2*Math.PI*1000*i/48000+(ch==0?0:degrees*Math.PI/180))).ToArray(); double mean=values.Average(); return Math.Sqrt(values.Average(v=>(v-mean)*(v-mean))); }
        Assert.Equal(20*Math.Log10(Rms(1)/Rms(0)),s.LevelDifferenceDb!.Value,5); Assert.Null(s.DelaySamples); Assert.Contains("ambiguous",s.Status);
    }
    [Fact]
    public async Task WrongToneFrequencyAndDifferentChannelContents_DoNotClaimPhase()
    {
        var wrong=await Measure((i,ch)=>.4*Math.Sin(2*Math.PI*(ch==0?1000:1040)*i/48000),new() { KnownToneHz=1000 });
        Assert.False(wrong.Stereo.Assessable); Assert.Null(wrong.Stereo.SignalPhaseDegrees);
        var noise=await Measure((i,ch)=>Noise(i,ch+1),new() { KnownToneHz=1000 });
        Assert.False(noise.Stereo.Assessable); Assert.Null(noise.Stereo.SignalPhaseDegrees);
        var commonWrong=await Measure((i,_)=>.4*Math.Sin(2*Math.PI*1005*i/48000),new() { KnownToneHz=1000 });
        Assert.False(commonWrong.Stereo.Assessable); Assert.Null(commonWrong.Stereo.SignalPhaseDegrees);
    }
    [Fact]
    public async Task TooFewToneCycles_AreExplicitlyRejected()
    {
        var m=await Measure((i,_)=>.4*Math.Sin(2*Math.PI*500*i/48000),new() { KnownToneHz=500 },n:1024);
        Assert.False(m.Stereo.Assessable); Assert.Contains("four",m.Stereo.Status); Assert.Null(m.Stereo.SignalPhaseDegrees);
    }
    [Theory]
    [InlineData(7,1)]
    [InlineData(-9,1)]
    [InlineData(0,-1)]
    public async Task RelatedBroadband_HasSignedStableUniqueDelay(int delay,int polarity)
    {
        var m=await Measure((i,ch)=>ch==0?Noise(i):polarity*.6*Noise(i-delay));
        var s=m.Stereo; Assert.True(s.Assessable,s.Status); Assert.True(s.DelayPeakUnique); Assert.Equal(delay,s.DelaySamples!.Value,2);
        Assert.Equal(delay*1e6/48000,s.DelayMicroseconds!.Value,0); Assert.InRange(s.Coherence!.Value,.98,1); Assert.InRange(s.LagSpreadSamples!.Value,0,1);
        if(polarity==-1) Assert.InRange(s.BestCorrelation!.Value,-1,-.99);
    }
    [Fact]
    public async Task UnrelatedBroadband_IsNotAValidChannelDelay()
    {
        var m=await Measure((i,ch)=>Noise(i,ch+1)); Assert.False(m.Stereo.Assessable); Assert.Null(m.Stereo.DelaySamples); Assert.InRange(m.Stereo.Coherence!.Value,0,.6);
    }
    [Fact]
    public async Task PeriodicSignalAndSearchBoundary_NeverClaimUniqueDelay()
    {
        var periodic=await Measure((i,ch)=>.4*Math.Sin(2*Math.PI*1500*(i-ch*3)/48000)); Assert.False(periodic.Stereo.Assessable); Assert.Null(periodic.Stereo.DelaySamples);
        var boundary=await Measure((i,ch)=>Noise(i-ch*48)); Assert.False(boundary.Stereo.Assessable); Assert.Null(boundary.Stereo.DelaySamples); Assert.False(boundary.Stereo.DelayPeakUnique);
    }
    [Fact]
    public async Task ChangingDelayAcrossSegments_IsRejected()
    {
        var m=await Measure((i,ch)=>ch==0?Noise(i):Noise(i-(i/1024)*4)); Assert.False(m.Stereo.Assessable); Assert.Null(m.Stereo.DelaySamples);
    }
    [Fact]
    public async Task PacketPartitioning_DoesNotAlterAlignment()
    {
        var a=await Measure((i,ch)=>Noise(i-ch*7)); var b=await Measure((i,ch)=>Noise(i-ch*7),packet:137);
        Assert.Equal(a.Stereo,b.Stereo); Assert.Equal(a.Channels,b.Channels);
    }
    [Fact]
    public async Task StartupAndGap_RequireNewFullWindow_NoCrossGapMeasurement()
    {
        var format=new AudioCaptureFormat(48000,1,AudioSampleRepresentation.Signed16); await using var analyzer=new LiveSignalAnalyzer(); await analyzer.StartAsync(new(new("x","x","x",[]),format,format,"x"));
        var bytes=new byte[2048*2]; for(int i=0;i<2048;i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i*2),(short)(Math.Sin(i*.2)*10000));
        analyzer.Process(bytes,new(0,2048,0));
        Assert.False(analyzer.CreateSnapshot(new() { Alignment=new() })!.Alignment!.Channels[0].Assessable);
        analyzer.Process(bytes,new(3000,2048,0));
        var m=analyzer.CreateSnapshot(new() { Alignment=new() })!.Alignment!; Assert.Equal(3000,m.StartFrame); Assert.False(m.Channels[0].Assessable);
    }
    [Fact]
    public async Task AlignmentIsOptIn_AndInvalidOptionsCannotMakeNonFiniteResults()
    {
        var format=new AudioCaptureFormat(48000,1,AudioSampleRepresentation.Signed16); await using var analyzer=new LiveSignalAnalyzer(); await analyzer.StartAsync(new(new("x","x","x",[]),format,format,"x"));
        Assert.Null(analyzer.CreateSnapshot()!.Alignment);
        Assert.Throws<ArgumentOutOfRangeException>(()=>analyzer.CreateSnapshot(new() { Alignment=new() { KnownToneHz=double.NaN } }));
        Assert.Throws<ArgumentOutOfRangeException>(()=>analyzer.CreateSnapshot(new() { Alignment=new() { BandSplitHz=30000 } }));
        Assert.Throws<ArgumentException>(()=>HeadAlignmentAnalyzer.Measure([double.NaN],[],format,0,[],new()));
        Assert.Throws<ArgumentException>(()=>HeadAlignmentAnalyzer.Measure([.2,.2],[],format,0,[new(0,double.NaN,0),new(24000,0,0)],new()));
    }
    [Fact]
    public async Task MaximumWindowAndLag_StillRecoverExactBroadbandTiming()
    {
        var watch=Stopwatch.StartNew();
        var result=await Measure((i,ch)=>Noise(i-ch*110),new() { MaximumLagSamples=128 },n:32768,rate:96000);
        Assert.True(result.Stereo.Assessable,result.Stereo.Status); Assert.Equal(110,result.Stereo.DelaySamples!.Value,2);
        Assert.Equal(32768,result.EndFrame-result.StartFrame);
        // Informational only: no machine-dependent latency assertion or physical real-time claim.
        Console.WriteLine($"Maximum alignment window, including PCM ingest and FFT: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }
    [Fact]
    public async Task AB_NeedsConfirmedPassageAndMatchingSettings_ReportsIndividualDeltas()
    {
        var a=await Measure((i,_)=>.3*Math.Sin(2*Math.PI*1500*i/48000)+.2*Math.Sin(2*Math.PI*6000*i/48000),channels:1);
        var b=await Measure((i,_)=>.3*Math.Sin(2*Math.PI*1500*i/48000)+.1*Math.Sin(2*Math.PI*6000*i/48000),channels:1);
        var model=new HeadAlignmentComparisonModel(); Assert.False(model.Compare().Assessable);
        var context=new HeadAlignmentContext("device","raw","passage",false); model.Save(true,a,context); model.Save(false,b,context); Assert.False(model.Compare().Assessable);
        context=context with { SamePassageConfirmed=true }; model.Save(true,a,context); model.Save(false,b,context); var comparison=model.Compare(); Assert.True(comparison.Assessable); Assert.Equal(-20.76923,comparison.Differences.Single(d=>d.Metric=="High-band fraction").Delta,3);
        model.Save(false,b,context with { PassageId="other" }); Assert.False(model.Compare().Assessable); Assert.Empty(model.Compare().Differences);
        model.Save(false,b,context with { DeviceId="other" }); Assert.False(model.Compare().Assessable);
        model.Save(false,b,context with { ProcessingSignature="processed" }); Assert.False(model.Compare().Assessable);
        model.Save(false,b with { Format=new(96000,1,AudioSampleRepresentation.Float32) },context); Assert.False(model.Compare().Assessable);
        model.Save(false,b with { Options=b.Options with { BandSplitHz=4000 } },context); Assert.False(model.Compare().Assessable);
        model.Save(false,b with { EndFrame=b.EndFrame-1 },context); Assert.False(model.Compare().Assessable);
        model.Clear(); Assert.Null(model.A); Assert.Null(model.B);
    }
    [Fact]
    public async Task AB_InvalidDeclaredStereoTone_RemainsNotAssessableDespiteUserConfirmation()
    {
        var measurement=await Measure((i,_)=>.4*Math.Sin(2*Math.PI*1005*i/48000),new() { KnownToneHz=1000 });
        var comparison=new HeadAlignmentComparisonModel(); var context=new HeadAlignmentContext("input","raw","tone",true);
        comparison.Save(true,measurement,context); comparison.Save(false,measurement,context);
        Assert.False(comparison.Compare().Assessable); Assert.Empty(comparison.Compare().Differences);
    }
    [Fact]
    public async Task PhaseComparison_UsesNearestCycleAcrossWrap()
    {
        var a=await Measure((i,ch)=>.4*Math.Sin(2*Math.PI*1000*i/48000+(ch==0?0:179*Math.PI/180)),new() { KnownToneHz=1000 });
        var b=await Measure((i,ch)=>.4*Math.Sin(2*Math.PI*1000*i/48000+(ch==0?0:-179*Math.PI/180)),new() { KnownToneHz=1000 });
        var model=new HeadAlignmentComparisonModel(); var context=new HeadAlignmentContext("device","raw","tone",true); model.Save(true,a,context); model.Save(false,b,context);
        Assert.True(model.Compare().Assessable); Assert.Equal(2,model.Compare().Differences.Single(d=>d.Metric=="R−L signal phase").Delta,3);
    }
}
