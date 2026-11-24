using System.Buffers.Binary;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

public sealed class TapeExportGapTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"mztools-gap-tests-"+Guid.NewGuid().ToString("N"));
    public TapeExportGapTests()=>Directory.CreateDirectory(directory);
    public static TheoryData<int,int> ProfilesAndFormats
    {
        get{var data=new TheoryData<int,int>();foreach(var profile in Enum.GetValues<TapeProfile>().Where(p=>p is not (TapeProfile.Ultra or TapeProfile.UltraMz700 or TapeProfile.UltraMz800)))foreach(var format in Enum.GetValues<SharpTapeOutputFormat>())data.Add((int)profile,(int)format);return data;}
    }
    private static TapeRecord Record(int index=0,TapeProfile profile=TapeProfile.Normal1_1)
    {
        var bytes=MediaDocumentTests.Mzf("PROGRAM"+index,8);for(int i=128;i<bytes.Length;i++)bytes[i]=(byte)(i+index);
        using var stream=new MemoryStream(bytes);using var reader=new BinaryReader(stream);var record=new MZTFileReader().ReadMzfRecord(reader);record.Profile=profile;return record;
    }
    private string Export(IReadOnlyList<TapeRecord> records,SharpTapeOutputFormat format,TapeExportGapOptions? gaps=null,int rate=44100,SharpTapeMachine machine=SharpTapeMachine.Mz800)
    {
        string path=Path.Combine(directory,Guid.NewGuid()+"."+format.ToString().ToLowerInvariant());
        SharpTapeExporter.Export(path,records,format,machine,wavSampleRate:rate,cancellationToken:TestContext.Current.CancellationToken,gaps:gaps);return path;
    }
    private readonly record struct Run(bool High,long Units);
    private static List<Run> Runs(byte[] bytes,SharpTapeOutputFormat format)
    {
        var runs=new List<Run>();
        void Add(bool high,long units){if(runs.Count>0&&runs[^1].High==high)runs[^1]=runs[^1] with{Units=runs[^1].Units+units};else runs.Add(new(high,units));}
        if(format==SharpTapeOutputFormat.Wav)
        {
            Assert.Equal("RIFF"u8.ToArray(),bytes[..4]);Assert.Equal((uint)(bytes.Length-8),BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
            Assert.Equal((uint)(bytes.Length-44),BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
            foreach(byte value in bytes.AsSpan(44)){Assert.True(value is 20 or 235);Add(value==235,1);}
        }
        else foreach(byte raw in bytes)
        {
            int value=unchecked((sbyte)raw);if(value==0){Assert.NotEmpty(runs);runs[^1]=runs[^1] with{Units=runs[^1].Units+127};}else Add(value>0,Math.Abs(value));
        }
        return runs;
    }
    private static long GapUnits(SharpTapeOutputFormat format,int ms,int rate=44100)=>format switch{SharpTapeOutputFormat.Lep=>20L*ms,SharpTapeOutputFormat.L16=>63L*ms,_=>(long)Math.Round((long)ms*rate/1000d)};
    private static void TrimLow(List<Run> runs,long start,long end)
    {
        Assert.False(runs[0].High);Assert.True(runs[0].Units>=start);runs[0]=runs[0] with{Units=runs[0].Units-start};if(runs[0].Units==0)runs.RemoveAt(0);
        Assert.False(runs[^1].High);Assert.True(runs[^1].Units>=end);runs[^1]=runs[^1] with{Units=runs[^1].Units-end};if(runs[^1].Units==0)runs.RemoveAt(runs.Count-1);
    }
    [Theory][MemberData(nameof(ProfilesAndFormats))]
    public void LegacyAndInternalLoaderSignalsRemainUnchanged(int profile,int formatValue)
    {
        var format=(SharpTapeOutputFormat)formatValue;var record=Record(profile:(TapeProfile)profile);var original=TapeDocumentWriter.SerializeMzf(record,true);
        var legacy=File.ReadAllBytes(Export([record],format));Assert.Equal(legacy,File.ReadAllBytes(Export([record],format,TapeExportGapOptions.Legacy)));
        var standard=File.ReadAllBytes(Export([record],format,TapeExportGapOptions.Standard));var expected=Runs(legacy,format);var actual=Runs(standard,format);
        TrimLow(actual,GapUnits(format,1000),GapUnits(format,1000));Assert.Equal(expected.Count,actual.Count);
        for(int i=0;i<expected.Count;i++){Assert.Equal(expected[i].High,actual[i].High);Assert.InRange(actual[i].Units,expected[i].Units-(format==SharpTapeOutputFormat.Wav?1:0),expected[i].Units+(format==SharpTapeOutputFormat.Wav?1:0));}
        if(format!=SharpTapeOutputFormat.Wav)Assert.Equal(legacy,standard.AsSpan(1000,legacy.Length).ToArray());
        Assert.Equal(original,TapeDocumentWriter.SerializeMzf(record,true));
    }
    [Theory][InlineData(1,0)][InlineData(2,0)][InlineData(3,0)][InlineData(1,1)][InlineData(2,1)][InlineData(3,1)][InlineData(1,2)][InlineData(2,2)][InlineData(3,2)]
    public void CombinedHasExactlyStartBetweenEndAndImportsEveryProgram(int programs,int formatValue)
    {
        var format=(SharpTapeOutputFormat)formatValue;var records=Enumerable.Range(0,programs).Select(i=>Record(i)).ToArray();
        string path=Export(records,format,TapeExportGapOptions.Standard);var standard=File.ReadAllBytes(path);var legacy=File.ReadAllBytes(Export(records,format));
        var runs=Runs(standard,format);Assert.Equal(programs+1,runs.Count(r=>!r.High&&r.Units>=GapUnits(format,1000)));
        long expected=GapUnits(format,2000*programs);Assert.InRange(runs.Sum(r=>r.Units)-Runs(legacy,format).Sum(r=>r.Units),expected-1,expected+1);
        var imported=SharpTapeImporter.ReadFile(path,cancellationToken:TestContext.Current.CancellationToken);Assert.Equal(programs,imported.Count);
        for(int i=0;i<programs;i++)Assert.Equal(records[i].Body.MzfBody,imported[i].Body.MzfBody);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void SeparateFilesEachHaveOnlyStartAndEnd(int formatValue)
    {
        var format=(SharpTapeOutputFormat)formatValue;var records=Enumerable.Range(0,3).Select(i=>Record(i)).ToArray();
        var paths=SharpTapeExporter.ExportSeparate(Path.Combine(directory,"separate."+format.ToString().ToLowerInvariant()),records,format,SharpTapeMachine.Mz800,false,gaps:TapeExportGapOptions.Standard,cancellationToken:TestContext.Current.CancellationToken);
        Assert.Equal(3,paths.Count);
        for(int i=0;i<paths.Count;i++)
        {
            var actual=Runs(File.ReadAllBytes(paths[i]),format);var expected=Runs(File.ReadAllBytes(Export([records[i]],format)),format);
            Assert.Equal(2,actual.Count(r=>!r.High&&r.Units>=GapUnits(format,1000)));Assert.InRange(actual.Sum(r=>r.Units)-expected.Sum(r=>r.Units),GapUnits(format,2000)-1,GapUnits(format,2000)+1);
        }
    }
    [Theory][InlineData(44100)][InlineData(22050)]
    public void WavSampleTotalsAndLowAmplitudesAreCorrect(int rate)
    {
        var records=new[]{Record(),Record(1)};var legacy=File.ReadAllBytes(Export(records,SharpTapeOutputFormat.Wav,rate:rate));var standard=File.ReadAllBytes(Export(records,SharpTapeOutputFormat.Wav,TapeExportGapOptions.Standard,rate));
        Assert.Equal((uint)rate,BinaryPrimitives.ReadUInt32LittleEndian(standard.AsSpan(24)));Assert.InRange(standard.LongLength-legacy.LongLength,4L*rate-1,4L*rate+1);
        Assert.All(standard.Skip(44).Take(rate),b=>Assert.Equal(20,b));Assert.All(standard.TakeLast(rate-1),b=>Assert.Equal(20,b));
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void MaximumPausesAreLowAndRoundtripWithoutOverflow(int formatValue)
    {
        var format=(SharpTapeOutputFormat)formatValue;var record=Record();string path=Export([record],format,new(60000,60000,60000));var runs=Runs(File.ReadAllBytes(path),format);
        Assert.False(runs[0].High);Assert.False(runs[^1].High);Assert.InRange(runs[0].Units,GapUnits(format,60000),GapUnits(format,60000)+1000);
        Assert.Equal(record.Body.MzfBody,Assert.Single(SharpTapeImporter.ReadFile(path,cancellationToken:TestContext.Current.CancellationToken)).Body.MzfBody);
    }
    [Theory][InlineData(-1)][InlineData(60001)]
    public void InvalidGapsDoNotCreateOutputs(int value)
    {
        string path=Path.Combine(directory,"invalid.wav");Assert.Throws<ArgumentOutOfRangeException>(()=>SharpTapeExporter.Export(path,[Record()],SharpTapeOutputFormat.Wav,cancellationToken:TestContext.Current.CancellationToken,gaps:new(value,0,0)));Assert.False(File.Exists(path));
    }
    [Fact]public void CancelledExportDoesNotCreateOutputOrDeleteExistingSeparateFiles()
    {
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);cancellation.Cancel();string path=Path.Combine(directory,"cancel.wav");
        Assert.Throws<OperationCanceledException>(()=>SharpTapeExporter.Export(path,[Record()],SharpTapeOutputFormat.Wav,cancellationToken:cancellation.Token,gaps:TapeExportGapOptions.Standard));Assert.False(File.Exists(path));
        var records=new[]{Record()};var target=SharpTapeExporter.GetSeparateOutputPaths(path,records)[0];File.WriteAllBytes(target,[1,2,3]);
        Assert.Throws<OperationCanceledException>(()=>SharpTapeExporter.ExportSeparate(path,records,SharpTapeOutputFormat.Wav,SharpTapeMachine.Mz800,true,gaps:TapeExportGapOptions.Standard,cancellationToken:cancellation.Token));Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(target));
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public void MachineTimingChoicesRetainTheProgramSignal(int machineValue)
    {
        foreach(var format in Enum.GetValues<SharpTapeOutputFormat>())
        {
            var record=Record();var machine=(SharpTapeMachine)machineValue;
            var legacy=Runs(File.ReadAllBytes(Export([record],format,machine:machine)),format);
            var standard=Runs(File.ReadAllBytes(Export([record],format,TapeExportGapOptions.Standard,machine:machine)),format);TrimLow(standard,GapUnits(format,1000),GapUnits(format,1000));
            Assert.Equal(legacy.Count,standard.Count);for(int i=0;i<legacy.Count;i++){Assert.Equal(legacy[i].High,standard[i].High);Assert.InRange(standard[i].Units,legacy[i].Units-(format==SharpTapeOutputFormat.Wav?1:0),legacy[i].Units+(format==SharpTapeOutputFormat.Wav?1:0));}
        }
    }
    [Theory][InlineData("WAV",2)][InlineData("LEP",0)][InlineData("L16",1)]
    public void BatchConversionExplicitlyRetainsLegacy(string target,int formatValue)
    {
        var record=Record();string input=Path.Combine(directory,"source.mzf");File.WriteAllBytes(input,TapeDocumentWriter.SerializeMzf(record,true));
        var preview=BatchProcessService.Preview([input],new(BatchOperation.ConvertFormat,Path.Combine(directory,"out"),target),cancellationToken:TestContext.Current.CancellationToken);
        var row=Assert.Single(preview.Rows);Assert.NotNull(row.ImageBytes);Assert.Equal(File.ReadAllBytes(Export([record],(SharpTapeOutputFormat)formatValue)),row.ImageBytes);
    }
    [Fact]public void RiffLimitRejectsTheNextIntervalBeforeWritingIt()
    {
        string path=Path.Combine(directory,"limit.wav");using var stream=new FileStream(path,FileMode.Create,FileAccess.ReadWrite);
        var type=typeof(SharpTapeExporter).GetNestedType("WavSink",System.Reflection.BindingFlags.NonPublic)!;
        using var sink=(IDisposable)Activator.CreateInstance(type,stream,44100)!;
        type.GetField("totalSamples",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(sink,uint.MaxValue-36L);
        var error=Assert.Throws<System.Reflection.TargetInvocationException>(()=>type.GetMethod("WriteInterval")!.Invoke(sink,[false,1000d]));Assert.IsType<InvalidOperationException>(error.InnerException);Assert.True(stream.Length<=44);
    }
    [Fact]public void StandardReferenceFingerprintsAreStable()
    {
        var actual=new SortedDictionary<string,string>();
        foreach(var profile in Enum.GetValues<TapeProfile>().Where(p=>p is not (TapeProfile.Ultra or TapeProfile.UltraMz700 or TapeProfile.UltraMz800)))
        foreach(var format in Enum.GetValues<SharpTapeOutputFormat>())
            actual[$"{profile}/{format}"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Export([Record(profile:profile)],format,TapeExportGapOptions.Standard))));
        string? initial=Environment.GetEnvironmentVariable("MZTOOLS_INITIAL_STANDARD_GOLDEN");
        if(initial is not null)
        {
            Assert.False(File.Exists(initial),"Initial Standard reference must never overwrite an existing reference.");Directory.CreateDirectory(Path.GetDirectoryName(initial)!);
            File.WriteAllText(initial,System.Text.Json.JsonSerializer.Serialize(actual,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
        string reference=initial??Path.Combine(AppContext.BaseDirectory,"Fixtures","export-standard-gaps.json");
        Assert.Equal(System.Text.Json.JsonSerializer.Deserialize<SortedDictionary<string,string>>(File.ReadAllText(reference))!,actual);Assert.Equal(39,actual.Count);
    }
    public void Dispose()=>Directory.Delete(directory,true);
}
