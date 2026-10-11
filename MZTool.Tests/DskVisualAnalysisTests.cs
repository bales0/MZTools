using System.Diagnostics;
using System.Text.Json;

namespace MZTools.Tests;

public class DskVisualAnalysisTests
{
    [Theory]
    [InlineData(3)]
    public void HfeProjectionUsesStoredCellsAndValidatedUserMapping(int version)
    {
        var document=DskDocumentFactory.CreateCpm(false,tracks:6);
        document.FileSystem.Insert("TEST",new byte[] {1,2,3},user:0);
        document.FileSystem.Insert("TEST",new byte[] {4,5,6},user:15);document.MarkModified();
        var image=HfeImage.Parse(HfeImage.FromDsk(document).Serialize((HfeVersion)version));
        var before=image.Serialize(image.Version);var model=new DskVisualSnapshot(image);
        Assert.True(model.IsPhysical);Assert.Same(image.Tracks[0],model.Generate(0,default));
        Assert.Contains(model.Sectors,s=>s.Owners.Any(o=>o.User==0));
        Assert.Contains(model.Sectors,s=>s.Owners.Any(o=>o.User==15));
        Assert.Equal(2,model.Sectors.SelectMany(s=>s.Owners).Select(o=>o.FileKey).Distinct().Count());
        foreach(var sector in model.Sectors) {
            Assert.True(sector.PhysicalCells);Assert.Equal(sector.DataOffset+sector.StoredBytes*16L,sector.EndOffset);
        }
        var regions=DskGeneratedProjection.StoredRegions(image.Tracks[0]);
        Assert.Equal(image.Tracks[0].BitCellCount,regions.Sum(r=>r.Cells));
        Assert.All(regions.Where(r=>r.Kind=="IDAM"),r=>Assert.Equal(16,r.Cells));
        foreach(string format in new[] {"txt","csv","json"}) {
            string report=DskVisualReport.Export(model,format);Assert.Contains("HFE",report);Assert.DoesNotContain("GENERATED MFM",report);
        }
        Assert.Equal(before,image.Serialize(image.Version));
    }
    private static DskVisualSnapshot Model(DskImage image) => new(image,DskAnalyzer.Analyze(image));
    [Fact]
    public void ProjectionPreservesDescriptorOrderDuplicatesOffsetsAndSource()
    {
        var image=DskImage.CreateUniform(2,2,3,128,1,0x4E,0xE5,"visual");
        image.Tracks[0]!.Sectors.Reverse(); image.Tracks[0]!.Sectors[1].SectorId=3;
        var bytes=image.Serialize(); var document=DskDocument.Open(bytes);
        var model=new DskVisualSnapshot(document.Image,DskAnalyzer.Analyze(document));
        Assert.Equal(new byte[] {3,3,1},model.Tracks[0].Sectors.Select(s=>s.R));
        Assert.NotEqual(model.Tracks[0].Sectors[0].Address,model.Tracks[0].Sectors[1].Address);
        foreach(var s in model.Sectors) {
            Assert.Equal(bytes.AsSpan((int)s.DataOffset,s.StoredBytes).ToArray(),s.Data);
            Assert.Equal(s.DataOffset+s.StoredBytes,s.EndOffset);
            Assert.Equal(s.DescriptorOffset+6,0x100+model.Tracks[s.Track].FileOffset-0x100+0x18+s.Descriptor*8+6);
        }
        Assert.Contains(model.Issues,i=>i.Code=="DSK_DUPLICATE_SECTOR_ID");
        Assert.All(model.Sectors,s=>Assert.Equal("Unassigned",s.Ownership));
        foreach(string format in new[] { "json","csv","txt" }) Assert.NotEmpty(DskVisualReport.Export(model,format));
        Assert.Equal(bytes,document.Serialize()); Assert.False(document.IsModified);
    }
    [Fact]
    public void MissingEmptyZeroLengthAndStoredStatusAreNotHidden()
    {
        var image=DskImage.CreateUniform(3,1,2,128,1,0x4E,0,"states");
        image.Tracks[1]!.Sectors.Clear();
        var raw=image.Serialize(); raw[0x36]=0; // Last physical slot absent; its former bytes become trailing data.
        raw[0x11E]=raw[0x11F]=0; raw[0x11C]=0x20; raw[0x11D]=0x40;
        var parsed=DskImage.Parse(raw); var model=Model(parsed);
        Assert.Empty(model.Tracks[1].Sectors); Assert.True(model.Tracks[2].Missing); Assert.Empty(model.Tracks[2].Sectors);
        var s=model.Tracks[0].Sectors[0]; Assert.Equal(0,s.DeclaredBytes); Assert.Equal(128,s.StoredBytes);
        Assert.Contains(s.Issues,i=>i.Code=="DSK_EXTENDED_ZERO_LENGTH");
        Assert.Contains("data error",s.Status); Assert.Contains("deleted",s.Status);
        Assert.True(model.TrailingBytes>0); Assert.Contains(model.Issues,i=>i.Code=="DSK_TRAILING_DATA");
        Assert.Throws<InvalidOperationException>(()=>model.Generate(2,default));
        Assert.Throws<InvalidOperationException>(()=>model.Generate(1,default));
    }
    [Theory]
    [InlineData(1,false)] [InlineData(2,false)] [InlineData(2,true)]
    public void SurfaceHitTestingUsesPhysicalSlotsAndExactDescriptorKeys(int sides,bool weighted)
    {
        var image=DskImage.CreateUniform(5,sides,3,128,1,0x4E,0,"geometry");
        image.Tracks[0]!.Cylinder=99; image.Tracks[0]!.Side=1;
        var model=Model(image);
        for(int side=0;side<sides;side++) {
            var surface=new DskSurfaceRenderModel(model,side,weighted); Assert.Equal(5,surface.Bands.Count);
            Assert.Equal(side,surface.Bands[0].Track);
            foreach(var band in surface.Bands) foreach(var span in band.Sectors) {
                double r=(band.Inner+band.Outer)/2,a=(span.Start+span.End)*Math.PI;
                var hit=surface.Hit(Math.Sin(a)*r,-Math.Cos(a)*r);
                Assert.NotNull(hit); Assert.Equal(span.Address,hit.Value.Sector);
            }
            Assert.Null(surface.Hit(0,0)); Assert.Null(surface.Hit(1.01,0)); Assert.Null(surface.Hit(double.NaN,0));
            Assert.Null(surface.Hit(0,-.5)); // Descriptor boundary at index angle 0.
        }
        Assert.Equal(99,model.Tracks[0].DeclaredCylinder); Assert.Equal(0,model.Tracks[0].SlotCylinder);
    }
    [Fact]
    public void WeightedSpansIncludeZeroDataAndMixedSizesWithoutReordering()
    {
        var model=Model(DskImage.CreateUniform(1,1,3,128,1,0x4E,0,"spans"));
        var input=model.Tracks[0].Sectors.Select((s,i)=>s with { StoredBytes=new[] {0,128,512}[i] }).ToArray();
        var spans=DskSurfaceRenderModel.Spans(input,true);
        Assert.Equal(3,spans.Count); Assert.True(spans[0].End>0); Assert.Equal(1,spans[^1].End,10);
        Assert.True(spans[2].End-spans[2].Start>spans[1].End-spans[1].Start);
        Assert.Equal(input.Select(s=>s.Address),spans.Select(s=>s.Address));
        Assert.Empty(DskSurfaceRenderModel.Spans(Array.Empty<DskVisualSector>(),true));
    }
    [Fact]
    public void GeneratedRegionsUseEncodedOffsetsAndNeverClaimMeasuredCrc()
    {
        var image=DskImage.CreateUniform(1,1,3,256,1,0x4E,0xA5,"mfm"); image.Tracks[0]!.Sectors[1].FdcStatus2=0x40;
        var source=image.Serialize(); var model=Model(image); var generated=model.Generate(0,default);
        Assert.All(generated.Sectors,s=> { Assert.True(s.HeaderCrcValid); Assert.True(s.DataCrcValid); });
        Assert.Equal(0xF8,generated.Sectors[1].DataMark);
        var regions=DskGeneratedProjection.Regions(generated); Assert.Equal(0,regions[0].StartCell); Assert.Equal(generated.BitCellCount,regions[^1].EndCell);
        for(int i=1;i<regions.Count;i++) Assert.Equal(regions[i-1].EndCell,regions[i].StartCell);
        for(int i=0;i<3;i++) {
            Assert.Equal(generated.Sectors[i].HeaderCell,regions.Single(r=>r.Descriptor==i && r.Kind=="IDAM").StartCell);
            Assert.Equal(generated.Sectors[i].DataCell,regions.Single(r=>r.Descriptor==i && r.Kind=="DATA").StartCell);
            Assert.Equal(image.Tracks[0]!.Sectors[i].Data,generated.Sectors[i].Data);
        }
        Assert.All(regions,r=>Assert.Contains("Synthetic",r.DataOrigin)); Assert.Equal(source,image.Serialize());
        using var json=JsonDocument.Parse(DskVisualReport.Export(model,"json"));
        Assert.False(json.RootElement.GetProperty("MeasuredFlux").GetBoolean());
        Assert.Equal("DSK",json.RootElement.GetProperty("GeneratedMfm").GetProperty("GeneratedFrom").GetString());
        Assert.Contains("Not available in DSK",DskVisualReport.Export(model,"txt"));
    }
    [Fact]
    public void UnsupportedGeneratedTrackAndCancellationLeaveSectorViewUsable()
    {
        var image=DskImage.CreateUniform(1,1,1,128,1,0x4E,0,"unsupported"); image.Tracks[0]!.Sectors[0].SizeCode=7;
        var model=Model(image); Assert.Throws<InvalidOperationException>(()=>model.Generate(0,default));
        Assert.Single(model.Tracks[0].Sectors);
        using var c=new CancellationTokenSource(); c.Cancel();
        Assert.Throws<OperationCanceledException>(()=>model.Generate(0,c.Token));
        Assert.Throws<OperationCanceledException>(()=>DskVisualReport.Export(model,"csv",c.Token));
    }
    [Fact]
    public void WorstSupportedGeometryIsBoundedAndReadOnly()
    {
        var image=DskImage.CreateUniform(102,2,29,128,1,0x4E,0,"maximum"); var bytes=image.Serialize();
        long before=GC.GetAllocatedBytesForCurrentThread(); var clock=Stopwatch.StartNew(); var model=Model(image);
        Assert.Equal(5916,model.Sectors.Count());
        var surface=new DskSurfaceRenderModel(model,1,true); Assert.Equal(102,surface.Bands.Count);
        Assert.True(clock.Elapsed<TimeSpan.FromSeconds(15)); Assert.InRange(GC.GetAllocatedBytesForCurrentThread()-before,1,256_000_000);
        Assert.Equal(bytes,image.Serialize());
    }
    [Fact]
    public void ExistingFilesystemOwnershipIsProjectedWithoutAnotherReader()
    {
        var document=DskDocumentFactory.CreateCpm(false); document.FileSystem.Insert("OWN.COM",[1,2,3]);
        var layout=DskAnalyzer.Analyze(document); var model=new DskVisualSnapshot(document.Image,layout);
        Assert.Equal(layout.Sectors.Sum(s=>s.Owners.Count),model.Sectors.Sum(s=>s.Owners.Count));
        Assert.Contains(model.Sectors,s=>s.Owners.Any(o=>o.FileName.Contains("OWN")));
        Assert.Equal(layout.Issues,model.Issues);
    }
    [Fact]
    public void MixedLengthsPaddingAndSnapshotIsolationRetainExactContainerMetadata()
    {
        var image=DskImage.CreateUniform(2,1,3,128,1,0x4E,0xA5,"mixed");
        for(int i=0;i<3;i++) { image.Tracks[0]!.Sectors[i].SizeCode=(byte)i; image.Tracks[0]!.Sectors[i].Data=new byte[128<<i]; }
        image.Tracks[0]!.Cylinder=17;
        var bytes=image.Serialize(); var parsed=DskImage.Parse(bytes); var model=Model(parsed);
        Assert.Equal(new[] {128,256,512},model.Tracks[0].Sectors.Select(s=>s.StoredBytes));
        Assert.True(model.Tracks[0].PaddingBytes>0);
        Assert.Contains(model.Issues,i=>i.Track==0 && i.Code=="DSK_TRACK_CH");
        foreach(var s in model.Tracks[0].Sectors) Assert.Equal(bytes.AsSpan((int)s.DataOffset,s.StoredBytes).ToArray(),s.Data);
        string hash=model.Tracks[0].Sectors[0].DataSha256;
        parsed.Tracks[0]!.Sectors[0].Data[0]=0xFE; parsed.Tracks[0]!.Sectors[0].SectorId=99;
        Assert.Equal(hash,model.Tracks[0].Sectors[0].DataSha256); Assert.Equal(1,model.Tracks[0].Sectors[0].R);
        Assert.Equal(1,model.Generate(0,default).Sectors[0].R);
        parsed.Tracks[0]!.Sectors[0].Data=new byte[17]; var corrupted=Model(parsed);
        Assert.Equal(17,corrupted.Tracks[0].Sectors[0].StoredBytes);
        Assert.Throws<InvalidOperationException>(()=>corrupted.Generate(0,default));
        Assert.Contains(corrupted.Issues,i=>i.Code.Contains("SIZE") || i.Code.Contains("LENGTH"));
    }
    [Theory]
    [InlineData("P-CPM80.dsk")]
    [InlineData("multi2.dsk")]
    [InlineData("CPMv41 Hry COM A.dsk")]
    public void AvailableRealImagesRetainBytesAndExistingAnalysis(string name)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory!=null && !File.Exists(Path.Combine(directory.FullName,"MZTools.csproj"))) directory=directory.Parent;
        string? path=directory==null?null:Path.Combine(directory.FullName,"MZTool.Tests","DSK",name);
        if(path==null || !File.Exists(path)) return; // Optional repository fixtures; synthetic coverage is unconditional.
        var document=DskDocument.Open(File.ReadAllBytes(path)); var before=document.Serialize();
        var layout=DskAnalyzer.Analyze(document); var model=new DskVisualSnapshot(document.Image,layout);
        Assert.Equal(layout.Issues,model.Issues); Assert.Equal(layout.Sectors.Count(),model.Sectors.Count());
        foreach(string format in new[] {"txt","csv","json"}) Assert.NotEmpty(DskVisualReport.Export(model,format));
        Assert.Equal(before,document.Serialize()); Assert.False(document.IsModified);
    }
}
