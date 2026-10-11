using System.Diagnostics;

namespace MZTools.Tests;

public class DskMfmDetailTests
{
    [Fact]
    public async Task RequestedTracksAreCachedBoundedAndCancelableWithoutChangingSource()
    {
        var image=DskImage.CreateUniform(10,1,3,128,1,0x4E,0xE5,"cache"); var before=image.Serialize();
        var snapshot=new DskVisualSnapshot(image,DskAnalyzer.Analyze(image)); using var cache=new DskGeneratedTrackCache(snapshot);
        var timer=Stopwatch.StartNew(); var first=await cache.GetAsync(0); Assert.Same(first,await cache.GetAsync(0)); Assert.Equal(1,cache.Count);
        for(int i=1;i<10;i++) await cache.GetAsync(i);
        Assert.Equal(8,cache.Count); Assert.True(timer.Elapsed<TimeSpan.FromSeconds(15));
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cache.GetAsync(0,cancellation.Token));
        cache.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cache.GetAsync(1));
        Assert.Equal(before,image.Serialize());
    }
    [Fact]
    public void ZoomBytesComeFromActualEncoderCellsIncludingHeaderAndPayload()
    {
        var image=DskImage.CreateUniform(1,1,1,128,1,0x4E,0xE5,"bytes");
        image.Tracks[0]!.Sectors[0].Data=Enumerable.Range(0,128).Select(i=>(byte)i).ToArray();
        var snapshot=new DskVisualSnapshot(image,DskAnalyzer.Analyze(image)); var generated=snapshot.Generate(0,default); var sector=generated.Sectors[0];
        Assert.Equal((byte)0xA1,DskMfmDetailControl.ReadByte(generated,sector.HeaderCell));
        Assert.Equal((byte)0xFE,DskMfmDetailControl.ReadByte(generated,sector.HeaderCell+48));
        for(int i=0;i<128;i++) Assert.Equal((byte)i,DskMfmDetailControl.ReadByte(generated,sector.DataCell+i*16));
    }
    [Fact]
    public void CpmUsersZeroToFifteenRemainDistinctInLabelsAndExports()
    {
        var document=DskDocumentFactory.CreateCpm(false);
        for(int u=0;u<16;u++) document.FileSystem.Insert("SAME.COM",new byte[128],user:u);
        var model=new DskVisualSnapshot(document.Image,DskAnalyzer.Analyze(document));
        for(int u=0;u<16;u++) {
            var owned=model.Sectors.Where(s=>s.Owners.Any(o=>o.User==u)).ToArray(); Assert.NotEmpty(owned);
            Assert.All(owned,s=>Assert.Contains($"SAME.COM (user {u})",s.Ownership));
            Assert.Contains(model.Sectors,s=>s.Users.Split(", ").Contains(u.ToString()));
            Assert.Contains($"user {u})",DskVisualReport.Export(model,"csv"));
        }
        Assert.Equal(16,model.Sectors.SelectMany(s=>s.Owners).Select(DskVisualSector.OwnerLabel).Distinct().Count());
    }
}
