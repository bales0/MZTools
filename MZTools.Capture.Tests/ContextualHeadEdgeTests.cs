using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MZTools.Platform.Audio;

namespace MZTools.Capture.Tests;

public class ContextualHeadEdgeTests
{
    private static readonly HeadEdgeSource Source=new("synthetic","raw",0);
    private sealed class Run : IAsyncDisposable
    {
        internal readonly LiveSignalAnalyzer Analyzer=new();
        internal readonly HeadEdgeAdjustmentModel Model=new();
        internal readonly BaselineHeadEdgeAdjustmentModel Old=new();
        internal readonly AudioCaptureFormat Format;
        internal long End;
        internal double Now=>1+End/(double)Format.SampleRate;
        internal HeadAlignmentMeasurement? Last;
        internal readonly List<object> Trace=[];
        internal Run(int rate=96000,int channels=2) { Format=new(rate,channels,AudioSampleRepresentation.Float32); }
        internal async Task Start()=>await Analyzer.StartAsync(new(new("synthetic","synthetic","test",[]),Format,Format,"raw"));
        internal void Feed(double[] left,double[]? right=null,HeadEdgeSource? source=null,bool baseline=false,int packet=4096)
        {
            right??=left; int n=left.Length; long first=End;
            for(int offset=0;offset<n;offset+=packet)
            {
                int count=Math.Min(packet,n-offset); var bytes=new byte[count*Format.Channels*4];
                for(int i=0;i<count;i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*Format.Channels*4),(float)left[offset+i]);
                    if(Format.Channels==2) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*8+4),(float)right[offset+i]);
                }
                Analyzer.Process(bytes,new(first+offset,count,first+offset));
            }
            End+=n;
            var snapshot=Analyzer.CreateSnapshot(new(){FftSize=n,Alignment=new(),LeftEdgeReference=Model.Reference,RightEdgeReference=Model.Reference})!;
            Last=snapshot.Alignment!; Model.Update(Last,source??Source,0,false,Now);
            if(baseline)
            {
                var l=left.Select(v=>(double)(float)v).ToArray(); var r=right.Select(v=>(double)(float)v).ToArray();
                var original=BaselineHeadAlignmentAnalyzer.Measure(l,r,Format,first,snapshot.Spectrum,new());
                Old.Update(original,source??Source,0,false,Now);
                Trace.Add(new{firstFrame=first,endFrame=End,now=Now,oldState=Old.Current.State.ToString(),oldChange=Old.Current.Combined.ChangePercent,
                    oldRise=original.Channels[0].RiseMicroseconds,oldFall=original.Channels[0].FallMicroseconds,oldRiseCount=original.Channels[0].RiseCount,oldFallCount=original.Channels[0].FallCount,
                    state=Model.Current.State.ToString(),change=Model.Current.Combined.ChangePercent,edges=Last.Channels[0].Edges!.Diagnostics,
                    pcmSha256=Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(l.AsSpan())))});
            }
        }
        public async ValueTask DisposeAsync()=>await Analyzer.DisposeAsync();
    }
    private static double[] Ramp(int rate,double riseUs=140,double fallUs=140,double phase=0,int n=8192,double gain=1,bool invert=false,double noise=0,int period=100)
    {
        var a=new double[n]; double rise=riseUs*rate/1e6/.8,fall=fallUs*rate/1e6/.8;
        for(int i=0;i<n;i++)
        {
            double p=(i+phase)%period;
            double value=p<10?-.4:p<10+rise?-.4+.8*(p-10)/rise:p<period*.5?.4:p<period*.5+fall?.4-.8*(p-period*.5)/fall:-.4;
            a[i]=(invert?-1:1)*gain*value+noise*Math.Sin(i*2.2);
        }
        return a;
    }
    private static void Feed(Run run,double[] pcm,int count=10,bool baseline=false)
    { for(int i=0;i<count;i++) run.Feed(pcm,baseline:baseline); }
    [Fact]
    public async Task ResolvedPositiveControl_UsesFrozenReferenceAndRecognizesImprovement()
    {
        await using var run=new Run(); await run.Start(); Feed(run,Ramp(96000));
        Assert.Equal(HeadEdgeAdjustmentState.ReadyToZero,run.Model.Current.State);
        Assert.True(run.Model.ZeroHere(run.Now)); var reference=run.Model.Reference;
        Feed(run,Ramp(96000,95,95));
        Assert.Same(reference,run.Model.Reference); Assert.True(reference!.Qualified);
        Assert.Equal(HeadEdgeAdjustmentState.WorkingCopy,run.Model.Current.State);
        Assert.InRange(run.Model.Current.Combined.ChangePercent!.Value,45,50);
    }
    [Theory]
    [InlineData(22050)] [InlineData(44100)] [InlineData(48000)] [InlineData(88200)] [InlineData(96000)]
    public async Task SamplingPhases_PreserveConfidenceAndDoNotRequire192k(int rate)
    {
        for(int phase=0;phase<17;phase++)
        {
            await using var run=new Run(rate); await run.Start(); run.Feed(Ramp(rate,45,45,phase/17.0));
            var w=run.Last!.Channels[0].Edges!;
            Assert.Equal(w.Detected-w.Boundary,w.Complete+w.Incomplete);
            Assert.Equal(w.Detected-w.Boundary,w.Groups.Sum(g=>g.Count)+w.Ambiguous);
            Assert.InRange(w.Groups.Count,1,8);
            if(rate<=44100) Assert.True(w.ResolutionLimited+w.LowConfidence>0);
            if(rate==48000) Assert.True(w.LowConfidence>0);
            if(rate>=88200) Assert.True(w.Candidates+w.LowConfidence>0);
            Assert.Null(run.Model.Current.Combined.ChangePercent);
        }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task SourceChanges_InvalidateReference(int change)
    {
        await using var run=new Run(); await run.Start(); var pcm=Ramp(96000); Feed(run,pcm); run.Model.ZeroHere(run.Now);
        var source=change switch { 0=>Source with{DeviceId="other"},1=>Source with{ProcessingSignature="EQ"},2=>Source with{Discontinuities=1},3=>Source with{RecordingLabel="other copy"},_=>Source };
        if(change<4) run.Feed(pcm,source:source);
        else run.Model.Update(run.Last! with{Options=new(){BandSplitHz=4000}},Source,0,false,run.Now+.01);
        Assert.False(run.Model.Current.HasZero); Assert.Null(run.Model.Reference);
    }
    [Fact]
    public async Task ProvisionalZero_NeverSilentlyBecomesQualified()
    {
        await using var run=new Run(); await run.Start(); var pcm=Ramp(96000); run.Feed(pcm); Assert.True(run.Model.ZeroHere(run.Now));
        Assert.True(run.Model.Current.ZeroIsUncertain); var id=run.Model.Reference!.Id;
        Feed(run,pcm); Assert.Null(run.Model.Current.Combined.ChangePercent); Assert.Equal(id,run.Model.Reference.Id);
        Assert.True(run.Model.Current.ZeroIsUncertain); Assert.True(run.Model.ZeroHere(run.Now)); Assert.False(run.Model.Current.ZeroIsUncertain);
    }
    [Fact]
    public async Task OppositePhysicalEdges_RequireHoldInsteadOfCancellation()
    {
        await using var run=new Run(); await run.Start(); Feed(run,Ramp(96000,160,160)); run.Model.ZeroHere(run.Now);
        Feed(run,Ramp(96000,130,190));
        Assert.Equal(HeadEdgeAdjustmentState.Conflict,run.Model.Current.State); Assert.Null(run.Model.Current.Combined.ChangePercent);
        Assert.Equal(0,run.Model.Current.Combined.DisplayPercent);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task DegradationAndPopulationLoss_NeverProducePositiveEvidence(int defect)
    {
        await using var run=new Run(); await run.Start(); Feed(run,Ramp(96000)); run.Model.ZeroHere(run.Now); var reference=run.Model.Reference;
        var pcm=defect switch { 0=>Ramp(96000,200,200),1=>Ramp(96000,140,140,noise:.15),2=>Ramp(96000,140,140,gain:3),3=>Ramp(96000,25,25),_=>Ramp(96000) };
        if(defect==4) for(int i=1000;i<3000;i++) pcm[i]*=.2;
        if(defect==5) for(int i=0;i<pcm.Length;i++) pcm[i]=Math.Clamp(pcm[i],-.2,.2);
        for(int i=0;i<12;i++) { run.Feed(pcm); Assert.False(run.Model.Current.Combined.ChangePercent is >1e-6); }
        Assert.Same(reference,run.Model.Reference);
    }
    [Fact]
    public async Task InversionAndGain_PreserveCombinedMeaningWithoutChannelSwapping()
    {
        await using var a=new Run(); await using var b=new Run(); await a.Start(); await b.Start();
        Feed(a,Ramp(96000,140,180)); Feed(b,Ramp(96000,140,180,invert:true,gain:.6));
        a.Model.ZeroHere(a.Now); b.Model.ZeroHere(b.Now);
        Feed(a,Ramp(96000,95,125)); Feed(b,Ramp(96000,95,125,invert:true,gain:.6));
        Assert.Equal(a.Model.Current.State,b.Model.Current.State);
        Assert.NotNull(a.Model.Current.Combined.ChangePercent);
        Assert.Equal(a.Model.Current.Combined.ChangePercent!.Value,b.Model.Current.Combined.ChangePercent!.Value,3);
        Assert.Equal(a.Model.Current.Rise.ChangePercent!.Value,b.Model.Current.Fall.ChangePercent!.Value,3);
    }
    [Fact]
    public async Task OverlapStalePauseAndBoundedHistory_DoNotCreateEvidence()
    {
        await using var run=new Run(); await run.Start(); run.Feed(Ramp(96000));
        for(int i=0;i<20;i++) run.Model.Update(run.Last,Source,0,false,run.Now+i*.001);
        Assert.Equal(1,run.Model.Current.Windows);
        Feed(run,Ramp(96000),40); Assert.InRange(run.Model.Current.Windows,3,24); run.Model.ZeroHere(run.Now);
        run.Model.Advance(run.Now+1.1); Assert.Equal(HeadEdgeAdjustmentState.Stale,run.Model.Current.State); Assert.False(run.Model.ZeroHere(run.Now+1.1));
        run.Model.SetActive(false,run.Now+1.2); Assert.Equal(HeadEdgeAdjustmentState.Paused,run.Model.Current.State); Assert.Null(run.Model.Current.Combined.ChangePercent);
    }
    [Fact]
    public async Task LegacyAggregateWithoutContext_CannotCertifyNewPointer()
    {
        await using var run=new Run(); await run.Start(); run.Feed(Ramp(96000));
        var old=run.Last! with{Channels=run.Last!.Channels.Select(c=>c with{Edges=null}).ToArray()};
        var model=new HeadEdgeAdjustmentModel(); model.Update(old,Source,0,false,1);
        Assert.False(model.Current.CanZero); Assert.Null(model.Current.Combined.ChangePercent);
    }
    [Fact]
    public async Task PacketPartition_DoesNotChangeContextAndRawPcm()
    {
        await using var a=new Run(); await using var b=new Run(); await a.Start(); await b.Start();
        var pcm=Ramp(96000); var hash=SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan()));
        a.Feed(pcm,packet:8192); b.Feed(pcm,packet:137);
        Assert.Equal(a.Last!.Channels[0].Edges!.Diagnostics,b.Last!.Channels[0].Edges!.Diagnostics);
        Assert.Equal(a.Last.Channels[0].Edges!.Groups,b.Last.Channels[0].Edges!.Groups);
        Assert.Equal(hash,SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan())));
    }
    private static string Root()
    {
        var p=new DirectoryInfo(AppContext.BaseDirectory);
        while(p!=null&&!File.Exists(Path.Combine(p.FullName,"MZTools.csproj"))) p=p.Parent;
        return p?.FullName??throw new DirectoryNotFoundException("Repository root");
    }
    private static void Evidence(string name,object value)
    {
        string p=Path.Combine(Root(),"TestResults","Azimuth-2026-11-10","current"); Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,name+".json"),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
    }
    // Independent 384 kHz analog grid, HP40 + Butterworth LP, then a windowed-sinc antialias resampler.
    private static double[] Analog(string profile,int rate,int n,double cutoff,int seed,double longFraction=.5,double phase=0,byte[]? program=null)
    {
        const int grid=384000; var timing=PulseReferenceCatalog.GetWriterTiming(profile);
        int length=(int)Math.Ceiling((n+8.0)*grid/rate)+256; var x=new double[length]; var random=new Random(seed);
        double edge=0; bool high=false,bit=false; int bitIndex=0; double hp=0,last=0,y1=0,y2=0,x1=0,x2=0;
        double alpha=Math.Exp(-2*Math.PI*40/grid),k=Math.Tan(Math.PI*cutoff/grid),norm=1/(1+Math.Sqrt(2)*k+k*k);
        double b0=k*k*norm,b1=2*b0,b2=b0,a1=2*(k*k-1)*norm,a2=(1-Math.Sqrt(2)*k+k*k)*norm;
        for(int i=0;i<length;i++)
        {
            if(i>=edge)
            {
                high=!high; if(high)
                {
                    bit=program==null?random.NextDouble()<longFraction:((program[(bitIndex/8)%program.Length]>>(7-bitIndex%8))&1)!=0; bitIndex++;
                }
                double us=high?(bit?timing.LongHighMicroseconds:timing.ShortHighMicroseconds):(bit?timing.LongLowMicroseconds:timing.ShortLowMicroseconds);
                edge+=us*grid/1e6;
            }
            double raw=high?.4:-.4; hp=alpha*(hp+raw-last); last=raw;
            double y=b0*hp+b1*x1+b2*x2-a1*y1-a2*y2; x2=x1; x1=hp; y2=y1; y1=y; x[i]=y;
        }
        var result=new double[n]; const int half=64,phases=256;
        double fc=Math.Min(.45*rate,18000)/grid; var kernels=new double[phases][];
        for(int p=0;p<phases;p++)
        {
            kernels[p]=new double[2*half+1]; double sum=0;
            for(int j=-half;j<=half;j++)
            {
                double t=j-p/(double)phases; double sinc=Math.Abs(t)<1e-12?2*fc:Math.Sin(2*Math.PI*fc*t)/(Math.PI*t);
                double w=.5+.5*Math.Cos(Math.PI*t/(half+1)); double v=sinc*w; kernels[p][j+half]=v; sum+=v;
            }
            for(int j=0;j<kernels[p].Length;j++) kernels[p][j]/=sum;
        }
        for(int i=0;i<n;i++)
        {
            double at=128+(i+phase)*grid/rate; int center=(int)at; int p=Math.Min(phases-1,(int)((at-center)*phases));
            double sum=0; for(int j=-half;j<=half;j++) sum+=x[center+j]*kernels[p][j+half]; result[i]=sum;
        }
        return result;
    }
    [Fact]
    public async Task CatalogAdcBandwidthMatrix_RecordsActualOldAndNewProductionOnIdenticalPcm()
    {
        var rows=new List<object>(); int positive=0;
        foreach(string profile in new[]{"rom800","rom700pal","ic1200","ic2400","ic2800","ic3200","tc1","tc2","tc3"})
        foreach(int rate in new[]{22050,44100,48000,88200,96000})
        foreach(double cutoff in new[]{12000.0,6000,3000})
        {
            const int n=8192; var pcm=Analog(profile,rate,n*8,cutoff,381);
            await using var run=new Run(rate); await run.Start();
            for(int w=0;w<8;w++) run.Feed(pcm.AsSpan(w*n,n).ToArray(),baseline:true);
            bool qualified=run.Model.Current.State==HeadEdgeAdjustmentState.ReadyToZero;
            bool zero=run.Model.ZeroHere(run.Now); bool oldZero=run.Old.ZeroHere(run.Now);
            var original=run.Model.Reference;
            // Degrade the same analog data; do not upsample recorded FLAC or substitute a stand-in model.
            var degraded=Analog(profile,rate,n*12,Math.Min(cutoff,3000),381);
            for(int w=0;w<12;w++)
            {
                run.Feed(degraded.AsSpan(w*n,n).ToArray(),baseline:true);
                if(run.Model.Current.Combined.ChangePercent is >1e-6) positive++;
            }
            Assert.Same(original,run.Model.Reference);
            rows.Add(new{profile,rate,cutoff,qualified,zero,oldZero,oldState=run.Old.Current.State.ToString(),oldChange=run.Old.Current.Combined.ChangePercent,
                state=run.Model.Current.State.ToString(),change=run.Model.Current.Combined.ChangePercent,run.Model.Current.Diagnostics,
                trace=run.Trace,pcmSha256=Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan())))});
        }
        Evidence("catalog-adc-bandwidth",new{cases=rows.Count,falsePositiveWindows=positive,rows}); Assert.Equal(0,positive);
    }
    [Theory]
    [InlineData("testAudioStarB03A.flac",24)]
    [InlineData("SonyHF90V2A.flac",300)]
    public async Task RealFlac_MultipleUnresampledExcerptsAndLp3kCannotClaimImprovement(string name,int documentedStart)
    {
        NativeAudioCodecs.Configure(); string path=Path.Combine(Root(),"MZTool.Tests","FLAC",name);
        Assert.True(File.Exists(path),"Approved real FLAC fixture must be present: "+path);
        byte[] before; using(var stream=File.OpenRead(path)) before=SHA256.HashData(stream);
        using var reader=AudioSignalAnalysisService.OpenReader(path); int rate=(int)reader.Format.SampleRate;
        var rows=new List<object>(); int positive=0; var starts=new[]{1.0,(double)documentedStart,reader.Format.FrameCount/(double)rate*.25,reader.Format.FrameCount/(double)rate*.5,reader.Format.FrameCount/(double)rate*.75,reader.Format.FrameCount/(double)rate-8};
        foreach(double start in starts)
        {
            const int n=8192; int length=n*20; var left=new double[length]; var right=new double[length]; long first=(long)(start*rate);
            reader.ReadFrames(first,length,(i,l,r)=>{int index=(int)(i-first); left[index]=l/8388608.0; right[index]=r/8388608.0;});
            for(int channel=0;channel<2;channel++)
            {
                var pcm=channel==0?left:right; var lp=LowPass(pcm,rate,3000);
                await using var run=new Run(rate,1); await run.Start();
                for(int w=0;w<8;w++) run.Feed(pcm.AsSpan(w*n,n).ToArray(),baseline:true);
                bool qualified=run.Model.Current.State==HeadEdgeAdjustmentState.ReadyToZero;
                run.Model.ZeroHere(run.Now); run.Old.ZeroHere(run.Now);
                var id=run.Model.Reference;
                for(int w=0;w<12;w++)
                {
                    // Paired excerpt starts at precisely the same PCM position as its zero sequence.
                    run.Feed(lp.AsSpan(w*n,n).ToArray(),baseline:true);
                    if(run.Model.Current.Combined.ChangePercent is >1e-6) positive++;
                }
                Assert.Same(id,run.Model.Reference);
                rows.Add(new{start,channel,rate,qualified,oldState=run.Old.Current.State.ToString(),oldChange=run.Old.Current.Combined.ChangePercent,
                    state=run.Model.Current.State.ToString(),change=run.Model.Current.Combined.ChangePercent,run.Model.Current.Diagnostics,trace=run.Trace});
            }
        }
        byte[] after; using(var stream=File.OpenRead(path)) after=SHA256.HashData(stream);
        Evidence(Path.GetFileNameWithoutExtension(name),new{sha256=Convert.ToHexString(before),unchanged=before.SequenceEqual(after),falsePositiveWindows=positive,rows});
        Assert.Equal(before,after); Assert.Equal(0,positive);
    }
    private static double[] LowPass(double[] x,int rate,double cutoff)
    {
        double k=Math.Tan(Math.PI*cutoff/rate),norm=1/(1+Math.Sqrt(2)*k+k*k),b0=k*k*norm,b1=2*b0,b2=b0,a1=2*(k*k-1)*norm,a2=(1-Math.Sqrt(2)*k+k*k)*norm;
        double x1=0,x2=0,y1=0,y2=0; var result=new double[x.Length];
        for(int i=0;i<x.Length;i++) { double y=b0*x[i]+b1*x1+b2*x2-a1*y1-a2*y2; x2=x1;x1=x[i];y2=y1;y1=y;result[i]=y; }
        return result;
    }
    [Fact]
    public async Task ExistingMzfProgramBits_AllRealWriterProfilesRemainConservative()
    {
        string path=Path.Combine(Root(),"MZTool.Tests","MZF","Tc122.mzf"); var file=File.ReadAllBytes(path);
        var hash=SHA256.HashData(file); var body=file.AsSpan(128).ToArray(); Assert.NotEmpty(body);
        var rows=new List<object>();
        foreach(string profile in new[]{"rom800","rom700pal","ic1200","ic2400","ic2800","ic3200","tc1","tc2","tc3"})
        foreach(int rate in new[]{44100,96000})
        {
            const int n=8192;var original=Analog(profile,rate,n*8,6000,1,program:body);var degraded=Analog(profile,rate,n*12,3000,1,program:body);
            await using var run=new Run(rate);await run.Start();
            for(int i=0;i<8;i++)run.Feed(original.AsSpan(i*n,n).ToArray(),baseline:true);
            run.Model.ZeroHere(run.Now);run.Old.ZeroHere(run.Now);
            for(int i=0;i<12;i++) { run.Feed(degraded.AsSpan(i*n,n).ToArray(),baseline:true);Assert.False(run.Model.Current.Combined.ChangePercent is >1e-6); }
            rows.Add(new{profile,rate,state=run.Model.Current.State.ToString(),trace=run.Trace});
        }
        Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(path)));
        Evidence("mz-program-bits",new{file="Tc122.mzf",sha256=Convert.ToHexString(hash),cases=rows.Count,rows,
            scope="Actual existing program payload bits through catalog writer intervals and the documented analog model; does not alter or certify any MZ/IC/TC decoder."});
    }
    [Fact]
    public void IsolatedRcPhaseSweep_CalibratesProvisionalResolutionClasses()
    {
        var rows=new List<object>();
        foreach(int rate in new[]{22050,44100,48000,88200,96000})
        {
            var errors=new List<double>(); int limited=0,low=0,candidate=0;
            for(int phase=0;phase<=200;phase++)
            {
                // Continuous analytic RC response; 10–90 = tau*ln(9) = 45 us.
                const double tau=45e-6/2.1972245773362196;
                var pcm=new double[4096]; double period=.004,half=period*.5;
                for(int i=0;i<pcm.Length;i++)
                {
                    double t=(i+phase/201.0)/rate%period;
                    pcm[i]=t<half?-.4+.8*(1-Math.Exp(-t/tau)):.4-.8*(1-Math.Exp(-(t-half)/tau));
                }
                var f=new AudioCaptureFormat(rate,1,AudioSampleRepresentation.Float32);
                var measured=HeadAlignmentAnalyzer.Measure(pcm,[],f,0,[],new()); var channel=measured.Channels[0];
                errors.Add(100*Math.Abs(channel.RiseMicroseconds!.Value/45-1));
                limited+=channel.Edges!.ResolutionLimited; low+=channel.Edges.LowConfidence; candidate+=channel.Edges.Candidates;
            }
            errors.Sort(); rows.Add(new{rate,phases=201,p95AbsoluteErrorPercent=errors[(int)(errors.Count*.95)],limited,low,candidate});
            if(rate<=48000) Assert.True(limited+low>candidate);
            if(rate==96000) Assert.True(candidate>0);
        }
        Evidence("rc-phase-sweep",new{edgeMicroseconds=45,rows,interpretation="Model-specific sampling error, not ADC/hardware certification. 2 and 4 periods are conservative provisional classification boundaries; current changes must also clear Ts/min(edge) allowance."});
    }
    [Theory]
    [InlineData(.2)] [InlineData(.8)] [InlineData(0.0)]
    public async Task DataMixAndMissingGroups_DoNotReweightFrozenZero(double longFraction)
    {
        const int n=8192;
        await using var run=new Run(); await run.Start();
        var initial=Analog("rom800",96000,n*10,3000,52);
        for(int i=0;i<10;i++) run.Feed(initial.AsSpan(i*n,n).ToArray());
        Assert.Equal(HeadEdgeAdjustmentState.ReadyToZero,run.Model.Current.State);
        run.Model.ZeroHere(run.Now); var reference=run.Model.Reference;
        var changed=Analog("rom800",96000,n*16,3000,832,longFraction);
        for(int i=0;i<16;i++)
        {
            run.Feed(changed.AsSpan(i*n,n).ToArray());
            Assert.False(run.Model.Current.Combined.ChangePercent is >1e-6);
            Assert.Same(reference,run.Model.Reference);
        }
        if(longFraction==0) Assert.Null(run.Model.Current.Combined.ChangePercent);
    }
    [Fact]
    public async Task StereoWorkerBudget_IsMeasuredWithBoundedGroupStorage()
    {
        await using var run=new Run(); await run.Start(); var pcm=Ramp(96000,n:32768); run.Feed(pcm);
        Assert.InRange(run.Last!.Channels[0].Edges!.Groups.Count,1,8);
        var samples=new List<double>(); long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<24;i++) { var sw=Stopwatch.StartNew(); run.Feed(Ramp(96000)); samples.Add(sw.Elapsed.TotalMilliseconds); }
        allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        var raw=Ramp(96000).Select(v=>(double)(float)v).ToArray();
        var spectrum=run.Analyzer.CreateSnapshot(new(){FftSize=8192})!.Spectrum;
        var oldTimes=new List<double>();var newTimes=new List<double>();
        for(int i=0;i<20;i++)
        {
            var sw=Stopwatch.StartNew();BaselineHeadAlignmentAnalyzer.Measure(raw,raw,run.Format,0,spectrum,new());
            if(i>=4)oldTimes.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();HeadAlignmentAnalyzer.Measure(raw,raw,run.Format,0,spectrum,new());
            if(i>=4)newTimes.Add(sw.Elapsed.TotalMilliseconds);
        }
        double baselineMedian=oldTimes.Order().ElementAt(oldTimes.Count/2),currentMedian=newTimes.Order().ElementAt(newTimes.Count/2);
        Evidence("worker-budget",new{rate=96000,channels=2,frames=8192,windows=samples.Count,medianMilliseconds=samples.Order().ElementAt(12),maxMilliseconds=samples.Max(),allocatedBytes=allocated,
            baselineMeasurementMedianMilliseconds=baselineMedian,currentMeasurementMedianMilliseconds=currentMedian,incrementalMeasurementMilliseconds=currentMedian-baselineMedian,
            scope="Entire PCM packing, history, FFT, old diagnostics, stereo, edge groups and model; not callback/ISR. Target <10 ms is reported, not a timing assertion on shared CI."});
        Assert.InRange(run.Model.Current.Windows,3,24);
    }
}
