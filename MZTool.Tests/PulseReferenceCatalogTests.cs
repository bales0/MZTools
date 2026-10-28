namespace MZTools.Tests;

public class PulseReferenceCatalogTests
{
    [Fact]
    public void ReferenceChoices_UseSimpleProfilesAndSharedExactTimingWithoutTechnicalRows()
    {
        Assert.Equal(new[]{"MZ-800 ROM","MZ-700 ROM (PAL)","MZ-700 ROM (NTSC)","NORMAL 1:2","NORMAL 1:3","NORMAL 1:4","IC 1:2","IC 1:3","IC 1:4","TC 1:2","TC 1:3","MZ-700 FAST3"},AudioPulseReferences.Choices.Select(r=>r.Name));
        Assert.Equal(15,AudioPulseReferences.BuiltIn.Count);
        foreach(string id in new[]{"service800","service700","tccompat2","tccompat3","ic1200","tc1"})
        { Assert.DoesNotContain(AudioPulseReferences.Choices,r=>r.CatalogId==id); Assert.NotNull(PulseReferenceCatalog.Get(id)); }
        foreach(int speed in new[]{2,3,4})
        {
            var normal=AudioPulseReferences.Choices.Single(r=>r.Name=="NORMAL 1:"+speed);
            var ic=AudioPulseReferences.Choices.Single(r=>r.Name=="IC 1:"+speed);
            Assert.Equal(normal.CatalogId,ic.CatalogId); Assert.Same(normal.Guides,ic.Guides);
            Assert.Equal(AudioPulseReferences.MeasurementKey(normal),AudioPulseReferences.MeasurementKey(ic));
        }
        Assert.Equal(9,AudioPulseReferences.UniqueMeasurements(AudioPulseReferences.Choices).Count());
        var tc=AudioPulseReferences.Choices.Single(r=>r.Name=="TC 1:2");
        Assert.Equal(new[]{141.818,140.909,282.727,282.727},tc.Guides.Select(g=>g.Microseconds));
    }
    [Fact]
    public void ActiveCatalog_RemovesOnlyOldIcCompatibilityRows()
    {
        foreach(string id in new[]{"iccompat2","iccompat3","iccompat4"})
        {
            Assert.DoesNotContain(PulseReferenceCatalog.Entries,e=>e.Id==id);
            Assert.DoesNotContain(PulseReferenceCatalog.Comparisons,r=>r.CatalogId==id);
            Assert.Throws<ArgumentException>(()=>PulseReferenceCatalog.Get(id));
        }
        Assert.Equal(15,PulseReferenceCatalog.Comparisons.Count);
        foreach(string id in new[]{"ic1200","ic2400","ic2800","ic3200"}) Assert.Equal(PulseReferenceKind.WriterExact,PulseReferenceCatalog.Get(id).Kind);
        foreach(string id in new[]{"rxic2","rxic3","rxic4"})
        {
            var receiver=PulseReferenceCatalog.Get(id); Assert.Equal(PulseReferenceKind.ReceiverDecision,receiver.Kind);
            Assert.True(receiver.ReceiverMaxUs>receiver.ReceiverMinUs); Assert.Empty(receiver.Guides);
        }
        foreach(string id in new[]{"tccompat2","tccompat3","fast3"}) Assert.Equal(PulseReferenceKind.ProjectCompatibility,PulseReferenceCatalog.Get(id).Kind);
        foreach(string id in new[]{"tc1","tc2","tc3"}) Assert.Equal(PulseReferenceKind.TimerOutput,PulseReferenceCatalog.Get(id).Kind);
    }
    [Theory]
    [InlineData((int)TapeProfile.Ic1_1,"ic1200","IC writer 1:1",234.573,263.894,469.145,494.802)]
    [InlineData((int)TapeProfile.Ic1_2,"ic2400","IC writer 1:2",113.621,139.278,234.573,260.229)]
    [InlineData((int)TapeProfile.Ic1_3,"ic2800","IC writer 1:3",87.965,124.617,175.930,223.577)]
    [InlineData((int)TapeProfile.Ic1_4,"ic3200","IC writer 1:4",76.969,117.286,157.604,179.595)]
    public void CanonicalIcNames_UseExactDatabaseRows(int profile,string id,string name,double sh,double sl,double lh,double ll)
    {
        Assert.Equal(id,PulseReferenceCatalog.ProfileId((TapeProfile)profile));
        var entry=PulseReferenceCatalog.Get(id); Assert.StartsWith(name,entry.Software); Assert.Equal(PulseReferenceKind.WriterExact,entry.Kind);
        Assert.Equal(new[]{sh,sl,lh,ll},entry.Guides.Select(g=>g.Microseconds));
        var writer=PulseReferenceCatalog.GetWriterTiming(id);
        Assert.Equal(new[]{sh,sl,lh,ll},new[]{writer.ShortHighMicroseconds,writer.ShortLowMicroseconds,writer.LongHighMicroseconds,writer.LongLowMicroseconds});
        Assert.Contains("CMT_INTERCOPY_TURBOCOPY_TIMING_REFERENCE.md",entry.SourceFile);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandardImport_BadHeaderCanRecoverCompactCopyOrFreshFastProgram(bool compactCopy)
    {
        byte[] mzf = TapeTestData.CreateMzf([0,255,85,170,1,2,3,4],name:"RECOVER");
        var samples = new List<byte>();
        int shortLow = 10, shortHigh = 11, longLow = 20, longHigh = 21;
        void Pulse(bool isLong) {
            samples.AddRange(Enumerable.Repeat((byte)16,isLong ? longLow : shortLow));
            samples.AddRange(Enumerable.Repeat((byte)240,isLong ? longHigh : shortHigh));
        }
        void Bytes(byte[] bytes, bool corrupt = false) {
            ushort checksum = (ushort)bytes.Sum(b=>System.Numerics.BitOperations.PopCount(b));
            if(corrupt) checksum ^= 1;
            foreach(byte value in bytes.Concat(new[] {(byte)(checksum>>8),(byte)checksum})) {
                for(int mask=128;mask>0;mask>>=1) Pulse((value&mask)!=0);
                Pulse(true);
            }
        }
        void Block(byte[] bytes, bool corrupt = false) {
            for(int i=0;i<PulseReferenceCatalog.HeaderLeader;i++) Pulse(false);
            for(int i=0;i<20;i++) Pulse(true);
            for(int i=0;i<20;i++) Pulse(false);
            Pulse(true); Pulse(true); Bytes(bytes,corrupt);
        }
        Block(mzf[..128],corrupt:true);
        if(compactCopy) {
            for(int i=0;i<256;i++) Pulse(false);
            Bytes(mzf[..128]);
        } else {
            shortLow=3; shortHigh=4; longLow=6; longHigh=8;
            Block(mzf[..128]);
        }
        Block(mzf[128..]);
        string path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".wav");
        try {
            using(var writer = new BinaryWriter(File.Create(path))) {
                writer.Write("RIFF"u8); writer.Write(36+samples.Count); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(44100);
                writer.Write(44100); writer.Write((ushort)1); writer.Write((ushort)8);
                writer.Write("data"u8); writer.Write(samples.Count); writer.Write(samples.ToArray());
            }
            var record = Assert.Single(SharpTapeImporter.ReadFile(path));
            Assert.Equal(mzf[..128],record.GetSerializedHeader()); Assert.Equal(mzf[128..],record.Body.MzfBody);
            if(!compactCopy) Assert.Equal(TapeProfile.Normal1_4,record.Profile);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StandardImport_Barbarian700RecoversAllFiveRecordsLikeHeuristics()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","WAV","Barbarian2_700.wav"));
        if (!File.Exists(path)) return; // Optional local capture, not distributed with source.
        var standard = SharpTapeImporter.ReadFile(path);
        var heuristic = WavHeuristicAnalyzer.AnalyzeFile(path);
        Assert.Equal(5,heuristic.Records.Count);
        Assert.Equal(5,standard.Count);
        Assert.Equal(3,standard.Count(r=>r.Profile == TapeProfile.Normal1_4));
        for(int i=0;i<standard.Count;i++) {
            Assert.Equal(heuristic.Records[i].Profile,standard[i].Profile);
            Assert.Equal(heuristic.Records[i].GetSerializedHeader(),standard[i].GetSerializedHeader());
            Assert.Equal(heuristic.Records[i].Body.MzfBody,standard[i].Body.MzfBody);
        }
    }

    [Theory]
    [InlineData((int)SharpTapeMachine.Mz800, 44100, (int)TapeProfile.Normal1_1)]
    [InlineData((int)SharpTapeMachine.Mz700, 44100, (int)TapeProfile.Mz700_1_1)]
    [InlineData((int)SharpTapeMachine.Mz700PalRom, 22050, (int)TapeProfile.Mz700_1_1)]
    [InlineData((int)SharpTapeMachine.Mz700NtscRom, 22050, (int)TapeProfile.Mz700_1_1)]
    public void NativeTiming_BothImportersAgree(int machine, int sampleRate, int expected)
    {
        var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0,255,85,170,1,2,3,4], name:"NATIVE"));
        string path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".wav");
        try {
            SharpTapeExporter.Export(path,[record],SharpTapeOutputFormat.Wav,(SharpTapeMachine)machine,sampleRate);
            var standard = Assert.Single(SharpTapeImporter.ReadFile(path));
            var heuristic = Assert.Single(WavHeuristicAnalyzer.AnalyzeFile(path).Records);
            Assert.Equal((TapeProfile)expected,standard.Profile); Assert.Equal(standard.Profile,heuristic.Profile);
            Assert.Equal(record.GetSerializedHeader(),standard.GetSerializedHeader());
            Assert.Equal(record.Body.MzfBody,standard.Body.MzfBody);
            Assert.Equal(standard.GetSerializedHeader(),heuristic.GetSerializedHeader());
            Assert.Equal(standard.Body.MzfBody,heuristic.Body.MzfBody);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StandardNativeTiming_IsIndependentForEveryRecord()
    {
        var records = new[] { TapeProfile.Normal1_1, TapeProfile.Mz700_1_1, TapeProfile.Normal1_1 }
            .Select((profile,index) => {
                var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0,255,85,170],name:"NATIVE"+index));
                record.Profile = profile; record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                return record;
            }).ToArray();
        string path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".wav");
        try {
            SharpTapeExporter.Export(path,records,SharpTapeOutputFormat.Wav);
            var standard = SharpTapeImporter.ReadFile(path);
            Assert.Equal(records.Select(r=>r.Profile),standard.Select(r=>r.Profile));
            Assert.Equal(records.Select(r=>Convert.ToHexString(r.GetSerializedHeader())),standard.Select(r=>Convert.ToHexString(r.GetSerializedHeader())));
            Assert.Equal(records.Select(r=>Convert.ToHexString(r.Body.MzfBody)),standard.Select(r=>Convert.ToHexString(r.Body.MzfBody)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeTiming_RequiresBothBlocksToIdentifyMz700()
    {
        var reference = SharpZ80TimingTable.Mz700Normal;
        var timing = new Native1xPulseTiming(reference.ShortHighMicroseconds,reference.ShortLowMicroseconds,
            reference.LongHighMicroseconds,reference.LongLowMicroseconds);
        Assert.Equal(TapeProfile.Mz700_1_1,WavHeuristicAnalyzer.ClassifyNative1xTiming(timing,timing).ResultProfile);
        Assert.Equal(TapeProfile.Normal1_1,WavHeuristicAnalyzer.ClassifyNative1xTiming(timing,default).ResultProfile);
        Assert.Equal(TapeProfile.Normal1_1,WavHeuristicAnalyzer.ClassifyNative1xTiming(default,timing).ResultProfile);
        var rom800 = SharpZ80TimingTable.Rom800Normal;
        var other = new Native1xPulseTiming(rom800.ShortHighMicroseconds,rom800.ShortLowMicroseconds,
            rom800.LongHighMicroseconds,rom800.LongLowMicroseconds);
        Assert.Equal(TapeProfile.Normal1_1,WavHeuristicAnalyzer.ClassifyNative1xTiming(timing,other).ResultProfile);
    }

    [Theory]
    [InlineData((int)SharpTapeMachine.Mz700PalRom,(int)TapeProfile.Normal1_1)]
    [InlineData((int)SharpTapeMachine.Mz700PalRom,(int)TapeProfile.Mz700_1_1)]
    [InlineData((int)SharpTapeMachine.Mz700NtscRom,(int)TapeProfile.Normal1_1)]
    [InlineData((int)SharpTapeMachine.Mz700NtscRom,(int)TapeProfile.Mz700_1_1)]
    public void ExplicitRegionalTiming_AppliesToImportedNativeProfilesAndRoundTrips(int machine,int profile)
    {
        var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0,255,85,170,1,2,3,4],name:"REGIONAL"));
        record.Profile = (TapeProfile)profile; record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
        var header = record.GetSerializedHeader(); var body = record.Body.MzfBody.ToArray();
        string id = (SharpTapeMachine)machine == SharpTapeMachine.Mz700PalRom ? "rom700pal" : "rom700ntsc";
        Assert.All(SharpTapeProfileEncoder.Build(record,(SharpTapeMachine)machine),stage=>Assert.Equal(PulseReferenceCatalog.GetWriterTiming(id),stage.Pulses));
        string path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".wav");
        try {
            SharpTapeExporter.Export(path,[record],SharpTapeOutputFormat.Wav,(SharpTapeMachine)machine);
            var result = WavHeuristicAnalyzer.AnalyzeFile(path);
            var recovered = Assert.Single(result.Records);
            Assert.Equal(header,recovered.GetSerializedHeader()); Assert.Equal(body,recovered.Body.MzfBody);
            Assert.Equal(TapeProfile.Mz700_1_1,recovered.Profile);
            var standard = Assert.Single(SharpTapeImporter.ReadFile(path));
            Assert.Equal(header,standard.GetSerializedHeader()); Assert.Equal(body,standard.Body.MzfBody);
            Assert.Equal(recovered.Profile,standard.Profile);
            Assert.Equal((TapeProfile)profile,record.Profile); Assert.Equal(header,record.GetSerializedHeader());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RegionalTiming_DoesNotReplaceCopierOrFast3Profiles()
    {
        foreach(var profile in new[] {TapeProfile.Ic1_4,TapeProfile.Tc1_3,TapeProfile.Mz700_1_3,TapeProfile.Normal1_2}) {
            var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1,2,3])); record.Profile = profile;
            var original = SharpTapeProfileEncoder.Build(record,SharpTapeMachine.Mz800);
            var regional = SharpTapeProfileEncoder.Build(record,SharpTapeMachine.Mz700PalRom);
            Assert.Equal(original.Select(s=>s.Pulses),regional.Select(s=>s.Pulses));
            Assert.Equal(original.Select(s=>Convert.ToHexString(s.Data)),regional.Select(s=>Convert.ToHexString(s.Data)));
        }
    }

    [Fact]
    public void SharedMetrics_BoundScatterDriftAndIssueStorageAndRetainExactCounts()
    {
        const int pulses = 4098*256;
        var engine = new AudioPulseMeasurementEngine(new("PCM",1000000,0,pulses*500L,1,"ZeroCrossing",false,1,0,"USER",1,"test","1"),
            [new[] {100.0},new[] {100.0},new[] {200.0},new[] {200.0}]);
        long end = 0;
        for(int i=0;i<pulses;i++) { end += 100; engine.Add(i%2!=0,100,end); }
        var result = engine.Finish();
        Assert.Equal(pulses,result.Distributions.Sum(d=>d.Count));
        Assert.Equal(4096,result.Drift.Count); Assert.Equal(2,result.Omitted);
        Assert.InRange(result.Scatter.Count,1,20000); Assert.Equal(pulses/2,result.ScatterPairs);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ExactRegionalWriter_IsExplicitAndKeepsCompatibilityDefault()
    {
        var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0,255]));
        Assert.Equal(PulseReferenceCatalog.GetWriterTiming("service700"),SharpTapeProfileEncoder.Build(record,SharpTapeMachine.Mz700)[0].Pulses);
        Assert.Equal(PulseReferenceCatalog.GetWriterTiming("rom700pal"),SharpTapeProfileEncoder.Build(record,SharpTapeMachine.Mz700PalRom)[0].Pulses);
        Assert.Equal(PulseReferenceCatalog.GetWriterTiming("rom700ntsc"),SharpTapeProfileEncoder.Build(record,SharpTapeMachine.Mz700NtscRom)[0].Pulses);
    }
    [Theory]
    [InlineData(WavPulseMode.ZeroCrossing, 1)]
    [InlineData(WavPulseMode.Schmitt, .65)]
    [InlineData(WavPulseMode.AdaptiveZeroCrossing, 1)]
    internal void VisualAndManual_UseTheSamePcmPassAndMetrics(WavPulseMode detector, double gate)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0,255,85,170,1,2,3,4]));
            SharpTapeExporter.Export(path,[record],SharpTapeOutputFormat.Wav,SharpTapeMachine.Mz800);
            var analysis = AudioSignalAnalysisService.Analyze(path);
            var decoded = WavHeuristicAnalyzer.AnalyzeFile(path,includeCandidates:true);
            var schmittCandidate = decoded.Candidates.First(c=>c.PulseMode==WavPulseMode.Schmitt && c.ChecksumValid);
            var visualAccumulator = new AudioBlockAccumulator(1,"Actual Schmitt",schmittCandidate,schmittCandidate.Profile,false,analysis.SampleRate,path);
            long pulseEnd = visualAccumulator.Start+200;
            visualAccumulator.Add(WavPulseMode.ZeroCrossing,true,100,pulseEnd);
            visualAccumulator.Add(WavPulseMode.Schmitt,true,200,pulseEnd);
            var visualMetrics = visualAccumulator.Finish();
            Assert.Equal(1,visualMetrics.Distributions.Sum(d=>d.Count));
            Assert.Equal(visualAccumulator.Start,Assert.Single(visualMetrics.MeasurementIssues).StartSample);
            var reference = analysis.Blocks[0];
            long start = reference.StartSample, end = reference.EndSample;
            var projection = AudioPulseReference.User("Same four comparison targets",reference.Distributions[0].Reference!.Value,
                reference.Distributions[1].Reference!.Value,reference.Distributions[2].Reference!.Value,reference.Distributions[3].Reference!.Value);
            var options = new AudioRecoveryOptions { Channel = 0, ZeroCrossing = detector == WavPulseMode.ZeroCrossing,
                Schmitt = detector == WavPulseMode.Schmitt, AdaptiveZeroCrossing = detector == WavPulseMode.AdaptiveZeroCrossing,
                Normal = !reference.Inverted, Inverted = reference.Inverted, ThresholdScales = [gate] };
            var manual = new AudioRecoveryMeasurements(analysis.SampleRate,start,end,options,projection,null,path);
            using (var reader = AudioSignalAnalysisService.OpenReader(path))
                WavHeuristicAnalyzer.VisitRangeSignal(reader,0,end,new(),gate,(_,_,_) => {},manual.Observe,default,
                    measureAdaptive: detector == WavPulseMode.AdaptiveZeroCrossing);
            var expected = Assert.Single(manual.Finish()).Block;
            var actual = AudioRangeInspection.Analyze(analysis,start,end,new(),0,detector,gate,reference.Inverted,
                reference with { ClassificationScale = 1 },preprocessingStart:0).Block;
            Assert.NotEmpty(actual.Drift);
            for(int i=0;i<4;i++) {
                Assert.Equal(expected.Distributions[i].Histogram,actual.Distributions[i].Histogram);
                Assert.Equal(expected.Distributions[i].Mean,actual.Distributions[i].Mean);
                Assert.Equal(expected.Distributions[i].P95,actual.Distributions[i].P95);
            }
            Assert.Equal(expected.Scatter,actual.Scatter); Assert.Equal(expected.Drift,actual.Drift);
            Assert.Equal(expected.Outliers,actual.Outliers);
            Assert.Equal(detector.ToString(),actual.PulseSource!.Detector);
            Assert.Equal(gate,actual.PulseSource.ThresholdScale);
            long split = start + (end-start)/3, resume = start + 2*(end-start)/3;
            var selected = AudioRangeInspection.AnalyzeSelection(analysis,[(start,split),(resume,end)],0,false,
                reference with { ClassificationScale = 1 },detector:detector,thresholdScale:gate,preprocessingStart:0);
            var part1 = AudioRangeInspection.Analyze(analysis,start,split,new(),0,detector,gate,reference.Inverted,reference with { ClassificationScale = 1 },preprocessingStart:0).Block;
            var part2 = AudioRangeInspection.Analyze(analysis,resume,end,new(),0,detector,gate,reference.Inverted,reference with { ClassificationScale = 1 },preprocessingStart:0).Block;
            Assert.Equal(part1.ScatterPairs+part2.ScatterPairs,selected.ScatterPairs);
            Assert.Equal(part1.Drift.Concat(part2.Drift),selected.Drift);
            Assert.Equal(2,selected.PulseSource!.Intervals.Count);
            var optionsWithLog = options with { Diagnostics = true };
            var enabled = WavHeuristicAnalyzer.AnalyzeRange(path,0,analysis.FrameCount,options:optionsWithLog);
            var disabled = WavHeuristicAnalyzer.AnalyzeRange(path,0,analysis.FrameCount,options:optionsWithLog with { Diagnostics = false });
            Assert.NotEmpty(enabled.Records); Assert.Empty(disabled.Diagnostics);
            Assert.Contains(enabled.Diagnostics,e=>e.Stage=="Assembly" && e.Context.StartsWith("Pass 1:"));
            Assert.Contains(enabled.Diagnostics,e=>e.Stage=="Assembly" && e.Context.StartsWith("Cross-pass final assembly"));
            Assert.Equal(enabled.Records.Select(r=>Convert.ToHexString(r.GetSerializedHeader())+Convert.ToHexString(r.Body.MzfBody)),
                disabled.Records.Select(r=>Convert.ToHexString(r.GetSerializedHeader())+Convert.ToHexString(r.Body.MzfBody)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Diagnostics_AreBoundedPerPass()
    {
        var log = new AudioRecoveryDiagnostics();
        for(int pass=0;pass<2;pass++) {
            log.Context = $"Pass {pass+1}: test";
            for(int i=0;i<30;i++) log.Add(new("Header","test","state",i,0,0,false,"Schmitt",0,null,0,""));
        }
        Assert.Equal(32,log.Events.Count); Assert.Equal(28,log.Omitted);
        Assert.Equal(16,log.Events.Count(e => e.Context.StartsWith("Pass 2:")));
    }

    [Fact]
    public void Catalog_ValidatesOriginsContextsAndSeparatesTimingKinds()
    {
        PulseReferenceCatalog.Validate(PulseReferenceCatalog.Entries);
        Assert.Equal(15, AudioPulseReferences.BuiltIn.Count);
        var rom = PulseReferenceCatalog.Get("rom800");
        Assert.Equal(PulseReferenceKind.WriterExact, rom.Kind);
        Assert.Equal(9, rom.Guides.Count);
        Assert.Equal(844 * 1e6 / 3546875, rom.Writer!.Value.ShortHighMicroseconds, 10);
        Assert.Equal(896 * 1e6 / 3546875, PulseReferenceCatalog.GetRomLow("rom800",false,"Data",true), 10);
        Assert.Equal(906 * 1e6 / 3546875, PulseReferenceCatalog.GetRomLow("rom800",false,"Data",false), 10);
        Assert.NotEqual(PulseReferenceCatalog.GetWriterTiming("rom700pal"), PulseReferenceCatalog.GetWriterTiming("rom700ntsc"));
        Assert.Equal(PulseReferenceKind.TimerOutput,PulseReferenceCatalog.Get("tc2").Kind);
        Assert.Equal(156 / 1.1,PulseReferenceCatalog.GetWriterTiming("tc2").ShortHighMicroseconds,10);
        Assert.Throws<ArgumentException>(() => PulseReferenceCatalog.GetWriterTiming("rxic2"));
        Assert.Equal(619*1e6/3546875,PulseReferenceCatalog.Get("rxic2").ReceiverMinUs!.Value,10);
        Assert.Throws<ArgumentException>(() => PulseReferenceCatalog.GetRomLow("rom800",false,"Unproven context",false));
        Assert.Throws<ArgumentException>(() => PulseReferenceCatalog.Validate([rom,rom]));
        Assert.Throws<ArgumentException>(() => PulseReferenceCatalog.Validate([rom with { SourceFile = "" }]));
        Assert.Throws<ArgumentException>(() => PulseReferenceCatalog.Validate([rom with { Kind = (PulseReferenceKind)999 }]));
        Assert.Throws<ArgumentException>(() => PulseReferenceCatalog.Validate([rom with { Guides = [new("Bogus",1,"Unknown")] }]));
        Assert.Throws<ArgumentException>(() => PulseReferenceCatalog.Validate([PulseReferenceCatalog.Get("rxic2") with { ReceiverMaxUs = 1 }]));
        var before = rom.Guides.ToArray(); _ = AudioPulseReference.User("Session",100,200,300,400);
        Assert.Equal(before,rom.Guides);
    }

    [Theory]
    [InlineData(22050)] [InlineData(44100)] [InlineData(48000)] [InlineData(96000)]
    public void SamplesAndMicroseconds_RoundTripWithoutReplacingReference(int rate)
    {
        var reference = PulseReferenceCatalog.Get("rom800");
        var source = new AudioPulseMeasurementSource("test PCM",(uint)rate,0,100000,1,"Schmitt",false,.65,0,"rom800",1,"Leader","pass1");
        var targets = AudioPulseMeasurementEngine.Targets(SharpZ80TimingTable.Rom800Normal);
        var a = new AudioPulseMeasurementEngine(source,targets);
        var b = new AudioRecoveryMeasurements((uint)rate,0,100000,new() { ZeroCrossing=false,Inverted=false,ThresholdScales=[.65] },null,null);
        b.IncludeChannel(0);
        long end = 0;
        for (int i=0;i<1024;i++)
        {
            bool high = i % 2 == 0;
            double duration = Math.Round((i % 4 < 2 ? 258.819 : 487.189)*rate/1e6*8)/8;
            end += (long)Math.Ceiling(duration);
            a.Add(high,duration,end); b.Observe(0,WavPulseMode.Schmitt,high,duration,end);
        }
        var expected = a.Finish(); var actual = Assert.Single(b.Finish()).Block;
        for(int i=0;i<4;i++) {
            Assert.Equal(expected.Distributions[i].Histogram,actual.Distributions[i].Histogram);
            Assert.Equal(expected.Distributions[i].Mean,actual.Distributions[i].Mean);
            Assert.Equal(expected.Distributions[i].StdDev,actual.Distributions[i].StdDev);
            Assert.Equal(expected.Distributions[i].P95,actual.Distributions[i].P95);
        }
        Assert.Equal(expected.Scatter,actual.Scatter); Assert.Equal(expected.Drift,actual.Drift);
        Assert.Equal(expected.Outliers,actual.Outliers); Assert.Equal(expected.GlobalScale,actual.GlobalTimingScale);
        Assert.Equal(9,reference.Guides.Count);
    }

    [Fact]
    public void Comparison_UsesActualRetainedEventsAndNeverFabricatesAnAbsoluteFirstError()
    {
        AudioRecoveryEvent Event(string pass,string reason,long sample) => new("Checksum","CHECKSUM",reason,sample,sample/44100.0,1,false,"Schmitt",4,4,10,pass);
        var result = new AudioRegionRecoveryResult("same PCM",44100,0,10000,[],[],[],[],[new(1,1,1,2),new(.65,1,0,2)]) {
            Diagnostics = [Event("Pass 1: Adaptive","recorded=10 calculated=10",100),Event("Pass 2: Adaptive","recorded=10 calculated=9",101)] };
        var mismatch = AudioDecodePassComparison.Compare(result,0,1);
        Assert.Equal("First observed mismatch",mismatch.Status);
        Assert.Single(mismatch.Rows,r=>r.Status=="First observed mismatch");
        var identical = AudioDecodePassComparison.Compare(result,0,0);
        Assert.Equal("No mismatch in aligned retained events",identical.Status);
        Assert.Equal("Unable to align",AudioDecodePassComparison.Compare(result with { Diagnostics=[] },0,1).Status);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>AudioDecodePassComparison.Compare(result,0,1,cancellation.Token));
    }
}
