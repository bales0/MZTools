using System.Buffers.Binary;

namespace MZTools.Tests;

public sealed class HfeEditingTests
{
    private static byte[] Cylinder(byte[] image,int cylinder)
    {
        int table=BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(18))*512;
        int start=BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(table+cylinder*4))*512;
        int length=BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(table+cylinder*4+2));
        return image.AsSpan(start,(length+511)/512*512).ToArray();
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void FileEditsSaveToOriginalVariantAndKeepUntouchedCylinders(int versionNumber)
    {
        var version=(HfeVersion)versionNumber;
        var initial=DskDocumentFactory.CreateFsmz(tracks:6);
        byte[] source=HfeImage.FromDsk(initial).Serialize(version);
        var original=HfeImage.Parse(source);var document=DskDocument.Open(source);
        Assert.False(document.IsReadOnly);Assert.Equal(source,document.SerializeContainer());
        byte[] payload=Enumerable.Range(0,731).Select(i=>(byte)i).ToArray();
        document.FileSystem.Insert("TEST",payload);document.MarkModified();
        byte[] edited=document.SerializeContainer();var physical=HfeImage.Parse(edited);
        Assert.Equal(version,physical.Version);Assert.Equal(source,original.Serialize(version));
        Assert.Equal(payload,DskDocument.OpenHfe(physical).FileSystem.Extract(DskDocument.OpenHfe(physical).FileSystem.ReadDirectory().Single()));
        int unchanged=0;
        for(int c=0;c<initial.Image.TrackCount;c++) {
            bool same=original.Tracks.Where(t=>t.Cylinder==c).All(t=>t.Sectors.SelectMany(s=>s.Data).SequenceEqual(physical.Tracks.Single(p=>p.Cylinder==c && p.Side==t.Side).Sectors.SelectMany(s=>s.Data)));
            if(same) {Assert.Equal(Cylinder(source,c),Cylinder(edited,c));unchanged++;}
        }
        Assert.True(unchanged>0);Assert.All(physical.Tracks.SelectMany(t=>t.Sectors),s=>Assert.True(s.HeaderCrcValid && s.DataCrcValid));
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".hfe");
        try {
            document.Save(path);Assert.False(document.IsModified);Assert.Equal(edited,File.ReadAllBytes(path));
            var entry=document.FileSystem.ReadDirectory().Single();document.FileSystem.Rename(entry,"RENAMED");document.MarkModified();document.Save();
            var reopened=DskDocument.OpenHfe(HfeImage.Parse(File.ReadAllBytes(path)));Assert.Equal("RENAMED",reopened.FileSystem.ReadDirectory().Single().Name);
            reopened.FileSystem.Delete(reopened.FileSystem.ReadDirectory().Single());reopened.MarkModified();Assert.Empty(DskDocument.OpenHfe(HfeImage.Parse(reopened.SerializeContainer())).FileSystem.ReadDirectory());
        } finally {if(File.Exists(path)) File.Delete(path);}
    }
    [Fact]
    public void BootSystemInstallationWorksThroughHfeDocument()
    {
        var system=BundledBootSystems.All[0].Open();var profile=CpmSystemProfileRegistry.ResolveVerifiedSource(system);
        var empty=profile.Layout==CpmLayoutId.PersonalCpm80?DskDocumentFactory.CreatePersonalCpm80(false):DskDocumentFactory.CreateCpm(false);
        var hfe=HfeImage.FromDsk(empty).Serialize(HfeVersion.V3);var target=DskDocument.OpenHfe(HfeImage.Parse(hfe));
        target.FileSystem.Insert("KEEP.COM",new byte[] {0xC9,1,2,3},user:15);target.MarkModified();
        var build=CpmSystemBuilder.Apply(target,system,profile);Assert.NotEmpty(build.ChangedRanges);
        var reopened=DskDocument.OpenHfe(HfeImage.Parse(target.SerializeContainer()));
        Assert.Equal(profile.Id,CpmSystemProfileRegistry.ResolveVerifiedSource(reopened).Id);
        var keep=reopened.FileSystem.ReadDirectory().Single(e=>e.Name=="KEEP");Assert.Equal(15,keep.User);Assert.Equal(new byte[] {0xC9,1,2,3},reopened.FileSystem.Extract(keep).Take(4));
    }
    [Fact]
    public void EditsPreserveVariableTimingIndexAndCellsOutsideChangedFields()
    {
        var generated=HfeImage.FromDsk(DskDocumentFactory.CreateFsmz(tracks:4));
        var source=new HfeImage {Cylinders=generated.Cylinders,Sides=generated.Sides,Tracks=generated.Tracks.Select(t=>new PhysicalTrack {
            Cylinder=t.Cylinder,Side=t.Side,Rpm=t.Rpm,BitRate=t.BitRate,Encoding=t.Encoding,PackedBitCells=t.PackedBitCells,BitCellCount=t.BitCellCount,
            Timing=new PhysicalTiming[] {new(0,72),new(t.BitCellCount/2,60)},IndexCells=new long[] {0,t.BitCellCount/2},Sectors=t.Sectors
        }).ToArray()};
        var original=HfeImage.Parse(source.Serialize(true));var document=DskDocument.OpenHfe(original);
        document.FileSystem.Insert("TIMING",new byte[] {1,2,3});document.MarkModified();
        var after=HfeImage.Parse(document.SerializeContainer());
        for(int i=0;i<original.Tracks.Count;i++) {
            var a=original.Tracks[i];var b=after.Tracks[i];Assert.Equal(a.BitCellCount,b.BitCellCount);Assert.Equal(a.IndexCells,b.IndexCells);Assert.Equal(a.Timing,b.Timing);
            var changed=a.Sectors.Select((s,j)=>(Sector:s,Changed:!s.Data.AsSpan().SequenceEqual(b.Sectors[j].Data))).Where(p=>p.Changed).Select(p=>p.Sector).ToArray();
            for(int cell=0;cell<a.BitCellCount;cell++) if(!changed.Any(s=>cell>=s.DataCell && cell<=s.DataCell+(s.Data.Length+2)*16))
                Assert.Equal(PackedBitCells.Get(a.PackedBitCells,cell),PackedBitCells.Get(b.PackedBitCells,cell));
        }
    }
    [Fact]
    public void WriteProtectedHfeIsBrowsableAndCannotBeMarkedModified()
    {
        var bytes=HfeImage.FromDsk(DskDocumentFactory.CreateFsmz(tracks:4)).Serialize(true);bytes[20]=0;
        var document=DskDocument.OpenHfe(HfeImage.Parse(bytes));Assert.True(document.IsReadOnly);
        Assert.Throws<InvalidOperationException>(()=>document.MarkModified());Assert.Equal(bytes,document.SerializeContainer());
        Assert.Throws<InvalidOperationException>(()=>document.ReplaceContents(document.Serialize()));
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void FormattingChangesFilesystemAndGeometryWithoutWritingSource(int versionNumber)
    {
        var source=HfeImage.FromDsk(DskDocumentFactory.CreateFsmz(tracks:4)).Serialize((HfeVersion)versionNumber);
        var current=DskDocument.OpenHfe(HfeImage.Parse(source),"original.hfe");
        var replacement=DiskFormatService.Create(new(DskNewFormat.LecCpmDd,6,2));
        var formatted=current.Reformatted(replacement);
        Assert.True(formatted.IsModified);Assert.Equal("original.hfe",formatted.FilePath);Assert.Equal(6,formatted.Image.TrackCount);
        Assert.IsType<CpmFileSystem>(formatted.FileSystem);var saved=HfeImage.Parse(formatted.SerializeContainer());Assert.Equal((HfeVersion)versionNumber,saved.Version);
        Assert.Equal(source,current.SerializeContainer());Assert.Empty(DskDocument.OpenHfe(saved).FileSystem.ReadDirectory());
    }
    [Fact]
    public void RawDskCanBeReformattedAndInvalidHfeEditsAreAtomic()
    {
        var raw=DskDocumentFactory.CreateRaw(3,1,3,128,1,0x4E,0xE5,"raw");var bytes=raw.Serialize();
        var formatted=raw.Reformatted(DiskFormatService.Create(new(DskNewFormat.MzBasic,4,2)));
        Assert.Equal(bytes,raw.Serialize());Assert.IsType<FsmzFileSystem>(formatted.FileSystem);Assert.True(formatted.IsModified);
        var hfe=DskDocument.OpenHfe(HfeImage.Parse(HfeImage.FromDsk(formatted).Serialize(true)));var before=hfe.SerializeContainer();
        var incompatible=DskDocumentFactory.CreateFsmz(tracks:5).Serialize();Assert.Throws<InvalidDataException>(()=>hfe.ReplaceContents(incompatible));
        Assert.Equal(before,hfe.SerializeContainer());Assert.False(hfe.IsModified);
    }
}
