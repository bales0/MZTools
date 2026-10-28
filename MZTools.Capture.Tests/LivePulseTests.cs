namespace MZTools.Capture.Tests;

public class LivePulseTests
{
    internal static LivePulseReference Reference=new("test","Test-only symmetric reference","TEST","Synthetic vector","TEST ONLY","User",Array.AsReadOnly(Enumerable.Range(0,4).Select(g=>new LivePulseGuide((LivePulseGroup)g,g<2?250:500,"Test")).ToArray()));
    private static AudioCaptureFormat Format=new(48000,2,AudioSampleRepresentation.Signed16);
    private static double Square(int frame)
    { int phase=frame%72; return phase<12?.5:phase<24?-.5:phase<48?.5:-.5; }
    private static LivePulseSnapshot Analyze(int chunk,LivePulseDetector detector,LivePulseOptions? options=null,int frames=7200)
    {
        var engine=new LivePulseAnalyzer(Format,options??new() { Reference=Reference });
        for(int start=0;start<frames;start+=chunk) for(int i=start;i<Math.Min(frames,start+chunk);i++) engine.Process(i,Square(i),-Square(i));
        return engine.Capture(new() { Detector=detector }).Measure();
    }
    [Theory]
    [InlineData(LivePulseDetector.ZeroCrossing)]
    [InlineData(LivePulseDetector.Hysteresis)]
    [InlineData(LivePulseDetector.Adaptive)]
    public void PacketPartitioning_IsIdenticalAndStreamsStaySeparate(LivePulseDetector detector)
    {
        var a=Analyze(7200,detector); var b=Analyze(137,detector);
        Assert.Equal(a.MeasuredPulses,b.MeasuredPulses); Assert.Equal(a.RecentPulses,b.RecentPulses); Assert.Equal(a.Distributions,b.Distributions);
        Assert.Equal(6,a.Streams.Count); Assert.Equal(a.MeasuredPulses,a.Streams.Single(s=>s.Channel=="Left"&&s.Detector==detector).Retained);
        Assert.All(a.RecentPulses,p=>Assert.True(p.Microseconds>0&&double.IsFinite(p.Microseconds)));
    }
    [Fact]
    public void StartupAndStop_DoNotInventPartialPulses()
    {
        var options=new LivePulseOptions { Reference=Reference,Adaptive=false,Hysteresis=false };
        var engine=new LivePulseAnalyzer(Format,options);
        for(int i=0;i<20;i++) engine.Process(i,.5,.5); Assert.Equal(0,engine.Capture(new()).Measure().MeasuredPulses);
        for(int i=20;i<30;i++) engine.Process(i,-.5,-.5); Assert.Equal(0,engine.Capture(new()).Measure().MeasuredPulses);
        engine.Process(30,.5,.5); var s=engine.Capture(new()).Measure();
        Assert.Equal(1,s.MeasuredPulses); Assert.Equal(10e6/48000,s.RecentPulses.Single().Microseconds,8);
        Assert.Equal(s.MeasuredPulses,engine.Capture(new()).Measure().MeasuredPulses); // snapshot never flushes an unfinished lobe
    }
    [Theory]
    [InlineData(LivePulseDetector.ZeroCrossing)]
    [InlineData(LivePulseDetector.Hysteresis)]
    [InlineData(LivePulseDetector.Adaptive)]
    public void GapAndNonFiniteSample_ResetContinuityAndDiscardCrossGapEdges(LivePulseDetector detector)
    {
        var engine=new LivePulseAnalyzer(Format,new() { Reference=Reference }); for(int i=0;i<2000;i++) engine.Process(i,Square(i),Square(i));
        engine.Process(3000,.5,.5); var gap=engine.Capture(new() { Detector=detector }).Measure();
        Assert.Equal(1,gap.ContinuityResets); Assert.Equal(3000,gap.StartFrame); Assert.Equal(0,gap.MeasuredPulses);
        engine.Process(3001,double.NaN,0); var bad=engine.Capture(new() { Detector=detector }).Measure(); Assert.Equal(2,bad.ContinuityResets); Assert.Equal(0,bad.MeasuredPulses);
        for(int i=3002;i<5000;i++) engine.Process(i,Square(i),Square(i)); var fresh=engine.Capture(new() { Detector=detector }).Measure();
        Assert.True(fresh.MeasuredPulses>0); Assert.All(fresh.RecentPulses,p=>Assert.True(p.EndFrame-p.Microseconds*48000/1e6>=3002));
    }
    [Fact]
    public void CapacityAndTimeWindow_AreBoundedAndRetentionIsReported()
    {
        var s=Analyze(137,LivePulseDetector.ZeroCrossing,new() { Reference=Reference,MaximumPulsesPerStream=64,WindowSeconds=.05 });
        Assert.InRange(s.MeasuredPulses,1,64); Assert.True(s.CapacityEvictions>0); Assert.Equal(4800,s.StartFrame);
        Assert.All(s.RecentPulses,p=>Assert.True(p.EndFrame-p.Microseconds*48000/1e6>=s.StartFrame)); Assert.InRange(s.Timing.Count,0,1);
    }
    [Fact]
    public void Settings_SelectOnlyRequestedDetectorsAndPolarity()
    {
        var a=Analyze(137,LivePulseDetector.Hysteresis,new() { Reference=Reference,ZeroCrossing=false,Adaptive=false,InvertPolarity=false });
        Assert.Equal(2,a.Streams.Count); Assert.All(a.Streams,s=>Assert.Equal(LivePulseDetector.Hysteresis,s.Detector));
        Assert.All(a.RecentPulses.Where(p=>p.ReferenceMatch),p=>Assert.Equal(p.PhysicalHigh,p.Group is LivePulseGroup.ShortHigh or LivePulseGroup.LongHigh));
        var disabled=Analyze(137,LivePulseDetector.ZeroCrossing,new() { Reference=Reference,ZeroCrossing=false }); Assert.Equal(0,disabled.MeasuredPulses); Assert.Contains("disabled",disabled.ResetReason);
    }
    [Fact]
    public void UnknownDurations_AreRetainedAndNeverBecomeDecodedBits()
    {
        var options=new LivePulseOptions { Reference=Reference,Tolerance=.01 };
        var window=new LivePulseWindow(Format,options,new(),0,1000,0,"test",0,[new(100,2,true),new(300,100,false)],[]);
        var s=window.Measure(); Assert.Equal(2,s.MeasuredPulses); Assert.Equal(2,s.Unclassified); Assert.Equal(0,s.ReferenceMatches);
        Assert.Equal(2,s.Histogram.Sum(b=>b.Counts.Sum())); Assert.Null(s.MeanTimingScale); Assert.Contains("NOT RUN",s.Meaning);
    }
    [Fact]
    public void ReferenceComparison_TimingScaleJitterAndDriftHaveKnownUnits()
    {
        var pulses=new List<LiveMeasuredPulse>(); long end=0;
        for(int i=0;i<64;i++) { int duration=i<32?12:13; end+=duration; pulses.Add(new(end,duration,i%2==0)); }
        var s=new LivePulseWindow(Format,new() { Reference=Reference,InvertPolarity=false },new(),0,end,0,"test",0,pulses.ToArray(),[]).Measure();
        Assert.Equal(64,s.ReferenceMatches); Assert.Equal(1+1.0/24,s.MeanTimingScale!.Value,8); Assert.Equal(100.0/12,s.DriftPercent!.Value,8);
        Assert.Equal(4,s.ResidualJitterPercent!.Value,8); Assert.Equal(64,s.Timing.Single().Matches);
        Assert.Equal(32,s.Distributions.Single(d=>d.Group==LivePulseGroup.ShortHigh).Count);
    }
    [Fact]
    public void HistogramViewport_DoesNotChangeClassificationOrDiscardOffscreenCounts()
    {
        var engine=new LivePulseAnalyzer(Format,new() { Reference=Reference }); for(int i=0;i<7200;i++) engine.Process(i,Square(i),Square(i));
        var full=engine.Capture(new()).Measure(); var zoom=engine.Capture(new() { MinimumMicroseconds=240,MaximumMicroseconds=270,Bins=64 }).Measure();
        Assert.Equal(full.MeasuredPulses,zoom.MeasuredPulses); Assert.Equal(full.ReferenceMatches,zoom.ReferenceMatches); Assert.True(zoom.VisiblePulses<zoom.MeasuredPulses);
        Assert.Equal(zoom.VisiblePulses,zoom.Histogram.Sum(b=>b.Counts.Sum()));
    }
    [Fact]
    public void AdaptiveDeadband_RejectsSmallOscillationsWithoutChangingOtherDetectors()
    {
        var engine=new LivePulseAnalyzer(Format,new() { Reference=Reference,AdaptiveDeadbandFs=.1 });
        for(int i=0;i<2000;i++) engine.Process(i,.02*Math.Sin(i*.2),.02*Math.Sin(i*.2));
        Assert.Equal(0,engine.Capture(new() { Detector=LivePulseDetector.Adaptive }).Measure().MeasuredPulses);
        Assert.True(engine.Capture(new()).Measure().MeasuredPulses>0); Assert.True(engine.Capture(new() { Detector=LivePulseDetector.Hysteresis }).Measure().MeasuredPulses>0);
    }
    [Fact]
    public void InvalidOptionsAndViews_AreRejectedBeforeProcessing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>new LivePulseAnalyzer(Format,new() { Reference=Reference,ZeroCrossing=false,Hysteresis=false,Adaptive=false }));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new LivePulseAnalyzer(Format,new() { Reference=Reference,MaximumPulsesPerStream=int.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new LivePulseAnalyzer(Format,new() { Reference=Reference,ThresholdScale=double.NaN }));
        Assert.Throws<ArgumentException>(()=>new LivePulseAnalyzer(Format,new() { Reference=Reference with { Guides=[] } }));
        var engine=new LivePulseAnalyzer(Format,new() { Reference=Reference });
        Assert.Throws<ArgumentOutOfRangeException>(()=>engine.Capture(new() { MinimumMicroseconds=10,MaximumMicroseconds=10 }));
        Assert.Throws<ArgumentOutOfRangeException>(()=>engine.Capture(new() { Channel=2 }));
        Assert.Throws<ArgumentOutOfRangeException>(()=>engine.Capture(new() { MinimumMicroseconds=0,MaximumMicroseconds=double.MaxValue }));
    }
    [Fact]
    public async Task ConfigurationEpochAndFrozenReferences_DoNotReplayOrChangeWaveform()
    {
        var guides=Reference.Guides.ToArray(); var options=new LivePulseOptions { Reference=Reference with { Guides=guides } };
        await using var analyzer=new LiveSignalAnalyzer(); analyzer.ConfigurePulses(options);
        guides[0]=guides[0] with { Microseconds=9999 }; // caller-owned metadata cannot alter an installed configuration
        await analyzer.StartAsync(new(new("test","test","test",[]),Format,Format,"test"));
        var pcm=new byte[7200*4];
        for(int i=0;i<7200;i++) { short value=(short)(Square(i)*32768); System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i*4),value); System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i*4+2),value); }
        analyzer.Process(pcm,new(0,7200,0)); var before=analyzer.CreateSnapshot(new() { Pulses=new() })!;
        Assert.Equal(250,before.Pulses!.Options.Reference.Guides[0].Microseconds);
        analyzer.ConfigurePulses(options with {}); var same=analyzer.CreateSnapshot(new() { Pulses=new() })!;
        Assert.Equal(before.Pulses.MeasuredPulses,same.Pulses!.MeasuredPulses); Assert.Equal(before.Pulses.ContinuityResets,same.Pulses.ContinuityResets);
        analyzer.ConfigurePulses(options with { ThresholdScale=2 }); var changed=analyzer.CreateSnapshot(new() { Pulses=new() })!;
        Assert.Equal(0,changed.Pulses!.MeasuredPulses); Assert.Equal(7200,changed.Pulses.StartFrame); Assert.Equal(before.Waveform,changed.Waveform);
        analyzer.ConfigurePulses(null); var disabled=analyzer.CreateSnapshot(new() { Pulses=new() })!; Assert.Null(disabled.Pulses); Assert.Equal(before.Waveform,disabled.Waveform);
    }
    [Fact]
    public void AdaptiveOverlengthHistory_IsCountedWithoutInventingLongPulses()
    {
        var engine=new LivePulseAnalyzer(Format,new() { Reference=Reference });
        for(int i=0;i<200;i++) engine.Process(i,Square(i),Square(i));
        for(int i=200;i<7000;i++) engine.Process(i,.5,.5);
        var s=engine.Capture(new() { Detector=LivePulseDetector.Adaptive }).Measure();
        Assert.True(s.Streams.Single(stream=>stream.Channel=="Left"&&stream.Detector==LivePulseDetector.Adaptive).AdaptiveHistoryResets>0);
        Assert.All(s.RecentPulses,pulse=>Assert.True(pulse.Microseconds<4096*1e6/48000));
    }
}
