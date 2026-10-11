namespace MZTools.Tests;

public class DskSectorSelectionTests
{
    [Fact]
    public void ToggleRangeAndCombinedRangeRespectDescriptorOrderAndDuplicates()
    {
        var order=Enumerable.Range(0,6).Select(i=>new DskSectorAddress(i/3,i%3)).ToArray();
        var selected=DskSectorSelection.Next(new[] {order[0]},order[0],order[4],order,true,false);
        Assert.Equal(2,selected.Count);
        selected=DskSectorSelection.Next(selected,order[0],order[4],order,true,false); Assert.Single(selected);
        selected=DskSectorSelection.Next(selected,order[4],order[1],order,false,true);
        Assert.Equal(order.Skip(1).Take(4).ToHashSet(),selected);
        selected=DskSectorSelection.Next(new[] {order[5]},order[0],order[2],order,true,true);
        Assert.Equal(new[] {order[0],order[1],order[2],order[5]}.ToHashSet(),selected);
        Assert.Single(DskSectorSelection.Next(selected,null,order[3],order,false,false));
    }
    [Fact]
    public void OwnerCountsAreDistinctSectorsAndDistinguishPartialAndFull()
    {
        var disk=DskDocumentFactory.CreateCpm(false); disk.FileSystem.Insert("A.BIN",new byte[4096]); disk.FileSystem.Insert("B.BIN",new byte[4096]);
        var layout=DskAnalyzer.Analyze(disk); var a=layout.Sectors.Where(s=>s.Owners.Any(o=>o.FileName=="A.BIN")).ToArray();
        Assert.True(a.Length>1);
        var partial=DskSectorSelection.Owners(layout,new[] {a[0].Address}.ToHashSet()); var key=a[0].Owners.First().FileKey;
        Assert.Equal((1,a.Length),partial[key]);
        var full=DskSectorSelection.Owners(layout,a.Select(s=>s.Address).ToHashSet()); Assert.Equal((a.Length,a.Length),full[key]);
        Assert.Contains(full,p=>p.Key!=key && p.Value.Selected==0);
    }
}
