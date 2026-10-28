using System.Buffers.Binary;

namespace MZTools.Capture.Tests;

public class HeadEdgeAdjustmentTests
{
    private static readonly AudioCaptureFormat Format=new(48000,2,AudioSampleRepresentation.Float32);
    private static readonly HeadEdgeSource Source=new("input","same processing",0,"A copied cassette");
    private static HeadAlignmentMeasurement Measurement(double rise=100,double fall=100,HeadEdgeEvidence? evidence=null)
    {
        evidence??=new(2,2,0,0,.5,.8,1000);
        var channel=new HeadAlignmentChannel("Left",true,"usable",.3,0,30,1,rise,fall,40,40,0) { EdgeEvidence=evidence };
        return new(Format,new(),0,4096,[channel,channel with { Channel="Right" }],new(false,"Not required for copy matching",null,null,null,null,null,null,null,null,null,null,false,0));
    }
    private static double Feed(HeadEdgeAdjustmentModel model,HeadAlignmentMeasurement measurement,int first=0,int count=6,int channel=0,HeadEdgeSource? source=null,bool repeated=false,double timeBase=1)
    {
        double time=0; long frames=measurement.EndFrame-measurement.StartFrame;
        for(int i=first;i<first+count;i++)
        {
            long end=(i+1)*frames; time=timeBase+end/(double)measurement.Format.SampleRate;
            model.Update(measurement with { StartFrame=i*frames,EndFrame=end },source??Source,channel,repeated,time);
        }
        return time;
    }
    [Fact]
    public void ZeroWithoutStudioReference_CentersOneChannelForBothEdges()
    {
        var model=new HeadEdgeAdjustmentModel(); double now=Feed(model,Measurement(100,200));
        Assert.True(model.Current.CanZero); Assert.True(model.ZeroHere(now));
        Assert.Equal(HeadEdgeAdjustmentState.WorkingCopy,model.Current.State);
        Assert.Equal(0,model.Current.Combined.ChangePercent); Assert.Equal(0,model.Current.Combined.DisplayPercent);
        Assert.Equal(150,model.Current.Combined.ZeroMicroseconds); Assert.Equal(5*4096,model.Current.ZeroStartFrame);
    }
    [Fact]
    public void CombinedNeedle_UsesEqualEdgeTimes_NotAverageOfPercentages()
    {
        var model=new HeadEdgeAdjustmentModel(); double now=Feed(model,Measurement(100,200)); model.ZeroHere(now);
        now=Feed(model,Measurement(80,180),6,8); var measured=model.Current;
        Assert.Equal(100*(150.0/130-1),measured.Combined.ChangePercent!.Value,8);
        Assert.Equal(80,measured.Rise.CurrentMicroseconds); Assert.Equal(180,measured.Fall.CurrentMicroseconds);
        model.Advance(now+.1); Assert.Equal(measured.Combined.ChangePercent,model.Current.Combined.ChangePercent);
        Assert.InRange(model.Current.Combined.DisplayPercent!.Value,0,measured.Combined.ChangePercent!.Value);
    }
    [Fact]
    public void OppositeEdgeChanges_CanCancelButAreExplicitlyWarned()
    {
        var model=new HeadEdgeAdjustmentModel(); double now=Feed(model,Measurement()); model.ZeroHere(now);
        Feed(model,Measurement(70,130),6,10);
        Assert.Equal(0,model.Current.Combined.ChangePercent); Assert.Contains("disagree",model.Current.Message);
    }
    [Fact]
    public void LeftAndRight_QualifyAndMoveIndependently()
    {
        var left=new HeadEdgeAdjustmentModel(); var right=new HeadEdgeAdjustmentModel(); var baseline=Measurement();
        double now=Feed(left,baseline); Feed(right,baseline,channel:1); left.ZeroHere(now); right.ZeroHere(now);
        var changed=Measurement(80,80); changed=changed with { Channels=[changed.Channels[0],changed.Channels[1] with { RiseMicroseconds=120,FallMicroseconds=120 }] };
        Feed(left,changed,6,10); Feed(right,changed,6,10,channel:1);
        Assert.Equal(25,left.Current.Combined.ChangePercent!.Value,5); Assert.Equal(-100.0/6,right.Current.Combined.ChangePercent!.Value,5);
        Assert.Equal(100,right.Current.Combined.ZeroMicroseconds);
    }
    [Fact]
    public void BadRightChannel_DoesNotHideGoodLeft()
    {
        var m=Measurement(); m=m with { Channels=[m.Channels[0],m.Channels[1] with { Assessable=false,ClippedSamples=12,Quality="Digital clipping" }] };
        var left=new HeadEdgeAdjustmentModel(); var right=new HeadEdgeAdjustmentModel(); Feed(left,m); Feed(right,m,channel:1);
        Assert.True(left.Current.CanZero); Assert.False(right.Current.CanZero); Assert.Contains("clipping",right.Current.Message);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ChangedConditions_InvalidateManualZero(int condition)
    {
        var model=new HeadEdgeAdjustmentModel(); var m=Measurement(); double now=Feed(model,m); model.ZeroHere(now);
        var source=Source; int channel=0;
        if(condition==0) source=source with { DeviceId="other" };
        if(condition==1) source=source with { ProcessingSignature="new EQ" };
        if(condition==2) source=source with { Discontinuities=1 };
        if(condition==3) channel=1;
        if(condition==4) m=m with { Options=m.Options with { BandSplitHz=4000 } };
        Feed(model,m,6,1,channel,source); Assert.False(model.Current.HasZero);
    }
    [Fact]
    public void FormatWindowAndModeChanges_AlsoInvalidateZero()
    {
        foreach(int change in Enumerable.Range(0,3))
        {
            var model=new HeadEdgeAdjustmentModel(); var m=Measurement(); double now=Feed(model,m); model.ZeroHere(now);
            if(change==0) m=m with { Format=new(96000,2,AudioSampleRepresentation.Float32) };
            if(change==1) m=m with { EndFrame=8192 };
            model.Update(m with { StartFrame=30000,EndFrame=30000+(m.EndFrame-m.StartFrame) },Source,0,change==2,now+.1);
            Assert.False(model.Current.HasZero);
        }
    }
    [Fact]
    public void OverlappingPolls_DoNotCertifyStabilityOrFreezeFreshClipping()
    {
        var model=new HeadEdgeAdjustmentModel(); var m=Measurement();
        for(int i=0;i<20;i++) model.Update(m,Source,0,false,1+i*.01);
        Assert.Equal(1,model.Current.Windows); Assert.Equal(HeadEdgeAdjustmentState.WarmingUp,model.Current.State);
        Assert.True(model.ZeroHere(1.19)); Assert.True(model.Current.ZeroIsUncertain);
        double now=Feed(model,m,1,timeBase:2); model.ZeroHere(now);
        var clipped=m with { StartFrame=7*4096-2048,EndFrame=8*4096-2048,Channels=[m.Channels[0] with { Assessable=false,Quality="clipping" },m.Channels[1]] };
        model.Update(clipped,Source,0,false,now+.01);
        Assert.Equal(HeadEdgeAdjustmentState.Unavailable,model.Current.State); Assert.False(model.ZeroHere(now+.01));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NoiseOvershootDispersionAndSamplingLimit_CannotBecomeAnImprovement(int problem)
    {
        var model=new HeadEdgeAdjustmentModel(); var m=Measurement(); double now=Feed(model,m); model.ZeroHere(now);
        var e=m.Channels[0].EdgeEvidence!;
        e=problem switch { 0=>e with { BacktrackingFraction=.3 },1=>e with { OvershootFraction=.3 },2=>e with { RiseMadPercent=40 },_=>e };
        var bad=m with { Channels=[m.Channels[0] with { RiseMicroseconds=problem==3?20:80,EdgeEvidence=e },m.Channels[1]] };
        now=Feed(model,bad,6,1); Assert.Null(model.Current.Combined.ChangePercent); Assert.True(model.Current.CanZero); Assert.True(model.Current.HasZero);
        Assert.Equal(problem==3?HeadEdgeAdjustmentState.ResolutionLimited:HeadEdgeAdjustmentState.Unstable,model.Current.State);
        Assert.True(model.ZeroHere(now)); Assert.True(model.Current.ZeroIsUncertain); Assert.Null(model.Current.Combined.ChangePercent);
        Assert.Equal(0,model.Current.Combined.DisplayPercent);
    }
    [Fact]
    public void GrossContentChange_HoldsComparisonButAllowsExplicitNewZero()
    {
        var model=new HeadEdgeAdjustmentModel(); var m=Measurement(); double now=Feed(model,m); model.ZeroHere(now);
        var changed=m with { Channels=[m.Channels[0] with { EdgeEvidence=m.Channels[0].EdgeEvidence! with { TransitionsPerSecond=2500 } },m.Channels[1]] };
        now=Feed(model,changed,6,6); Assert.Equal(HeadEdgeAdjustmentState.ContentChanged,model.Current.State); Assert.Null(model.Current.Combined.ChangePercent);
        Assert.True(model.ZeroHere(now)); Assert.Equal(0,model.Current.Combined.ChangePercent);
    }
    [Fact]
    public void StaleAndPausedMeasurements_CannotBeZeroedOrShowFreshImprovement()
    {
        var model=new HeadEdgeAdjustmentModel(); var m=Measurement(); double now=Feed(model,m); model.ZeroHere(now);
        model.Advance(now+1.1); Assert.Equal(HeadEdgeAdjustmentState.Stale,model.Current.State); Assert.False(model.ZeroHere(now+1.1));
        Assert.Null(model.Current.Combined.ChangePercent);
        model.SetActive(false,now+1.2); Assert.Equal(HeadEdgeAdjustmentState.Paused,model.Current.State); Assert.True(model.Current.HasZero);
    }
    [Fact]
    public void LargeWindows_StillSettleWithoutFalseStalenessOrUnboundedHistory()
    {
        var m=Measurement() with { Format=new(22050,2,AudioSampleRepresentation.Float32),EndFrame=32768 };
        var model=new HeadEdgeAdjustmentModel(); Feed(model,m,count:60);
        Assert.True(model.Current.CanZero); Assert.InRange(model.Current.Windows,3,24);
    }
    private static async Task<HeadAlignmentMeasurement> Ramped(bool variable=false,double noise=0)
    {
        const int n=4096; await using var analyzer=new LiveSignalAnalyzer();
        await analyzer.StartAsync(new(new("test","test","test",[]),Format,Format,"test"));
        var bytes=new byte[n*8]; int period=200;
        for(int i=0;i<n;i++)
        {
            int p=i%period; double value=p<40?-.4:p<60?-.4+(p-40)*.04:p<140?.4:p<160?.4-(p-140)*.04:-.4;
            if(variable) { int q=i%240; value=q<60?-.4:q<80?-.4+(q-60)*.04:q<180?.4:q<200?.4-(q-180)*.04:-.4; }
            value+=noise*Math.Sin(i*2.2);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*8),(float)value); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*8+4),(float)(value*.7));
        }
        analyzer.Process(bytes,new(0,n,0)); return analyzer.CreateSnapshot(new() { Alignment=new() })!.Alignment!;
    }
    [Fact]
    public async Task CopiedPulseTrainWithVaryingDwellLengths_UsesEdgesNotAnIdealStudioTarget()
    {
        var normal=await Ramped(noise:.005); var variable=await Ramped(true,.005);
        var model=new HeadEdgeAdjustmentModel(); double now=Feed(model,normal); Assert.True(model.ZeroHere(now));
        Feed(model,variable,6,8); Assert.Equal(HeadEdgeAdjustmentState.WorkingCopy,model.Current.State);
        Assert.InRange(Math.Abs(model.Current.Combined.ChangePercent!.Value),0,2);
        Assert.InRange(variable.Channels[0].EdgeEvidence!.BacktrackingFraction,0,.2);
    }
    [Fact]
    public async Task ActualNoisyEdges_ProduceEvidenceRatherThanFalselySharperNeedles()
    {
        var noisy=await Ramped(noise:.12); var model=new HeadEdgeAdjustmentModel(); double now=Feed(model,noisy);
        Assert.True(model.Current.CanZero); Assert.Null(model.Current.Combined.ChangePercent);
        Assert.True(model.ZeroHere(now)); Assert.True(model.Current.ZeroIsUncertain);
        Assert.True(noisy.Channels[0].EdgeEvidence!.BacktrackingFraction>.2||noisy.Channels[0].EdgeEvidence!.RiseMadPercent>25||noisy.Channels[0].EdgeEvidence!.OvershootFraction>.2);
    }
    [Fact]
    public void VariableRealisticTiming_KeepsZeroAvailableAndTracksCurrentQuality()
    {
        var model=new HeadEdgeAdjustmentModel();
        for(int i=0;i<30;i++)
        {
            double now=Feed(model,Measurement(i%2==0?80:160,i%3==0?180:90),i,1);
            Assert.True(model.Current.CanZero);
            bool uncertain=model.Current.State is HeadEdgeAdjustmentState.WarmingUp or HeadEdgeAdjustmentState.Unstable;
            Assert.True(model.ZeroHere(now)); Assert.Equal(uncertain,model.Current.ZeroIsUncertain);
            if(uncertain) Assert.Null(model.Current.Combined.ChangePercent);
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void UnmeasurableSignal_NeverReusesEarlierGoodWindowForZero(int problem)
    {
        var model=new HeadEdgeAdjustmentModel(); var m=Measurement(); double now=Feed(model,m);
        var channel=m.Channels[0];
        channel=problem switch { 0=>channel with { ClippedSamples=1 },1=>channel with { Assessable=false,Quality="Silence" },2=>channel with { RiseCount=2 },_=>channel with { RiseMicroseconds=double.NaN } };
        now=Feed(model,m with { Channels=[channel,m.Channels[1]] },6,1);
        Assert.False(model.Current.CanZero); Assert.False(model.ZeroHere(now));
    }
    [Fact]
    public void RepeatedNoisyWindow_ExpiresAndCannotBePresentedAsFreshManualZero()
    {
        var m=Measurement(evidence:new(40,40,.3,.3,.5,.8,1000)); var model=new HeadEdgeAdjustmentModel();
        model.Update(m,Source,0,false,1); Assert.True(model.Current.CanZero);
        model.Update(m,Source,0,false,2.1); Assert.False(model.Current.CanZero); Assert.False(model.ZeroHere(2.1));
        Assert.Equal(HeadEdgeAdjustmentState.Stale,model.Current.State);
    }
}
