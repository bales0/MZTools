using System.Text.Json;

namespace MZTools.Tests;

public class QuickDiskVisualTests
{
    [Theory]
    [InlineData((int)TapeDocumentFormat.Mzq)] [InlineData((int)TapeDocumentFormat.Qdf)] [InlineData((int)TapeDocumentFormat.QdSharpLegacy)]
    [InlineData((int)TapeDocumentFormat.QdHxc)] [InlineData((int)TapeDocumentFormat.QdFlashFloppy)]
    public async Task RegionExportWritesActualBytesAndPreservesDestinationOnCancelOrCollision(int formatValue)
    {
        var record=TapeTestData.ReadRecord(TapeTestData.CreateMzf(Enumerable.Range(0,128).Select(i=>(byte)i).ToArray()));
        var document=new TapeDocument {Format=(TapeDocumentFormat)formatValue};document.Records.Add(record);
        document.QuickDiskSourceImage=QuickDiskLayoutBuilder.BuildPreviewImage(document);
        var original=(byte[])document.QuickDiskSourceImage.Clone();var snapshot=QuickDiskVisualSnapshot.Capture(document);
        var payload=snapshot.Layout.Regions.Single(r=>r.Kind==QuickDiskRegionKind.Payload);
        string folder=Path.Combine(Path.GetTempPath(),"MZTools-QD-export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"payload.bin");
        try {
            await snapshot.ExportRegionAsync(payload,path,false,CancellationToken.None);
            Assert.Equal(record.Body.MzfBody,await File.ReadAllBytesAsync(path));
            await Assert.ThrowsAsync<IOException>(()=>snapshot.ExportRegionAsync(payload,path,false,CancellationToken.None));
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>snapshot.ExportRegionAsync(payload,path,true,canceled.Token));
            Assert.Equal(record.Body.MzfBody,await File.ReadAllBytesAsync(path));
            if(snapshot.Layout.IsPhysical) {
                var gap=snapshot.Layout.Regions.First(r=>r.Kind==QuickDiskRegionKind.Gap);
                var expected=new byte[(gap.Length+7)/8];
                for(long cell=0;cell<gap.Length;cell++) expected[cell/8]|=(byte)(snapshot.Bit(gap.Start+cell)<<(int)(cell%8));
                await snapshot.ExportRegionAsync(gap,path,true,CancellationToken.None);
                Assert.Equal(expected,await File.ReadAllBytesAsync(path));
            }
            Assert.Single(Directory.GetFiles(folder));Assert.Equal(original,document.QuickDiskSourceImage);Assert.False(document.IsModified);
        } finally {foreach(string file in Directory.GetFiles(folder)) File.Delete(file);Directory.Delete(folder);}
    }

    [Theory]
    [InlineData((int)TapeDocumentFormat.Mzq)] [InlineData((int)TapeDocumentFormat.Qdf)] [InlineData((int)TapeDocumentFormat.QdSharpLegacy)]
    [InlineData((int)TapeDocumentFormat.QdHxc)] [InlineData((int)TapeDocumentFormat.QdFlashFloppy)]
    public void SnapshotUsesActualPayloadCoordinatesAndPreservesInput(int formatValue)
    {
        var format=(TapeDocumentFormat)formatValue;
        var record=TapeTestData.ReadRecord(TapeTestData.CreateMzf(Enumerable.Range(0,128).Select(i=>(byte)i).ToArray()));
        var document=new TapeDocument {Format=format};document.Records.Add(record);
        document.QuickDiskSourceImage=QuickDiskLayoutBuilder.BuildPreviewImage(document);
        var before=(byte[])document.QuickDiskSourceImage.Clone();
        var snapshot=QuickDiskVisualSnapshot.Capture(document);
        Assert.False(snapshot.Layout.IsPreview);Assert.DoesNotContain(snapshot.Findings,f=>f.Severity=="Error");
        var payload=snapshot.Layout.Regions.Single(r=>r.Kind==QuickDiskRegionKind.Payload);
        Assert.Equal(record.Body.MzfBody,Enumerable.Range(0,128).Select(i=>snapshot.Byte(payload,i)));
        if(snapshot.Layout.IsPhysical) {
            Assert.NotNull(snapshot.Layout.HostAnalysis);
            for(long cell=0;cell<snapshot.Layout.Length;cell+=997) Assert.Equal((before[snapshot.Layout.TrackFileOffset+cell/8]>>(int)(cell%8))&1,snapshot.Bit(cell));
            Assert.Throws<ArgumentOutOfRangeException>(()=>snapshot.Bit(snapshot.Layout.Length));
        }
        var expectedSignal=snapshot.Layout.IsPhysical?null:QuickDiskMfmCodec.Encode(before);
        long signalLength=snapshot.Layout.Length*(snapshot.Layout.IsPhysical?1:16);
        for(long cell=0;cell<signalLength;cell+=997) {
            int expected=expectedSignal==null?snapshot.Bit(cell):(expectedSignal[cell/8]>>(int)(cell%8))&1;
            Assert.Equal(expected,snapshot.SignalBit(cell));
        }
        Assert.Throws<ArgumentOutOfRangeException>(()=>snapshot.SignalBit(signalLength));
        foreach(string type in new[] {"txt","csv","json"}) Assert.Contains(snapshot.Hash,snapshot.Export(type));
        using var json=JsonDocument.Parse(snapshot.Export("json"));Assert.True(json.RootElement.GetProperty("Regions").GetArrayLength()>1);
        Assert.Equal(before,document.QuickDiskSourceImage);Assert.False(document.IsModified);
        document.QuickDiskSourceImage[0]^=0xFF;Assert.Equal(before,snapshot.Layout.Image);
    }

    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void NonSharpHostsRetainDecodedUnitsWithoutInventingFiles(int host)
    {
        var track=QuickDiskHostDetectionTests.MakeTrack((QuickDiskHostFormat)host);
        var bytes=QuickDiskHostDetectionTests.Wrap(track,QdImageFormat.HxcPhysical);
        var snapshot=new QuickDiskVisualSnapshot(bytes,TapeDocumentFormat.QdHxc);
        Assert.True(snapshot.Layout.IsPhysical);Assert.DoesNotContain(snapshot.Layout.Regions,r=>r.FileIndex>=0);
        Assert.NotEmpty(snapshot.Layout.HostBytes);Assert.Equal((QuickDiskHostFormat)host,snapshot.Layout.HostAnalysis!.Identification.HostFormat);
        foreach(var pair in snapshot.Layout.HostBytes) {
            Assert.Equal(pair.Value,Enumerable.Range(0,pair.Value.Length).Select(i=>snapshot.Byte(pair.Key,i)));
            if(pair.Key.Kind==QuickDiskRegionKind.HostSector && pair.Value.Length>0) {
                Assert.Equal(pair.Key.End-16,snapshot.ByteStart(pair.Key)+pair.Value.Length*16L);
                Assert.Equal(pair.Value,QuickDiskHostBitstream.Decode(track,(int)snapshot.ByteStart(pair.Key),pair.Value.Length,false));
            }
        }
        Assert.Equal(bytes,snapshot.Layout.Image);
    }

    [Fact]
    public void DamagedMo5PayloadHasNavigableChecksumFinding()
    {
        var track=QuickDiskHostDetectionTests.MakeTrack(QuickDiskHostFormat.ThomsonMo5);
        var snapshot=new QuickDiskVisualSnapshot(QuickDiskHostDetectionTests.Wrap(track,QdImageFormat.HxcPhysical),TapeDocumentFormat.QdHxc);
        var sector=snapshot.Layout.Regions.First(r=>r.Kind==QuickDiskRegionKind.HostSector);
        long cell=snapshot.ByteStart(sector)+15;track[cell/8]^=(byte)(1<<(int)(cell%8));
        var damaged=new QuickDiskVisualSnapshot(QuickDiskHostDetectionTests.Wrap(track,QdImageFormat.HxcPhysical),TapeDocumentFormat.QdHxc);
        Assert.Contains(damaged.Findings,f=>f.Severity=="Warning" && f.Start==sector.Start && f.End==sector.End);
        Assert.DoesNotContain(damaged.Layout.Regions,r=>r.FileIndex>=0);
    }

    [Fact]
    public void InvalidLogicalContainerReportsFailureWithoutInventingRecords()
    {
        byte[] bytes=[1,2,3,4];var snapshot=new QuickDiskVisualSnapshot(bytes,TapeDocumentFormat.QdSharpLegacy);
        Assert.Contains(snapshot.Findings,f=>f.Severity=="Error");Assert.DoesNotContain(snapshot.Layout.Regions,r=>r.FileIndex>=0);
        Assert.Equal(bytes,snapshot.Layout.GetBlockBytes(snapshot.Layout.Regions.Single()));
    }

    [Fact]
    public void ModifiedDocumentIsExplicitlyARebuiltPreview()
    {
        var document=new TapeDocument {Format=TapeDocumentFormat.QdHxc,IsModified=true};
        var snapshot=QuickDiskVisualSnapshot.Capture(document);Assert.True(snapshot.Layout.IsPreview);
        Assert.Contains("not original positions",snapshot.Layout.Summary);
        Assert.True(document.IsModified);Assert.Null(document.QuickDiskSourceImage);
    }
}
