using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

public sealed class CompressionCacheTests
{
    [Fact]public void AtoBtoAReusesCompletedPayloadAndReturnsIndependentArrays()
    {
        var cache=new MzfCompressionCache();var a=new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0);var b=a with{Direction=CompressionDirection.Backward};int calls=0;
        CompressionPayload Compute(byte[] bytes){calls++;return new((byte[])bytes.Clone(),7);}
        var first=cache.GetOrCreate([1,2,3],a,Compute,TestContext.Current.CancellationToken);first.Data[0]=99;
        cache.GetOrCreate([1,2,3],b,Compute,TestContext.Current.CancellationToken);var last=cache.GetOrCreate([1,2,3],a,Compute,TestContext.Current.CancellationToken);
        Assert.Equal(2,calls);Assert.Equal(new byte[]{1,2,3},last.Data);Assert.Equal(7,last.Delta);
        cache.GetOrCreate([1,2,4],a,Compute,TestContext.Current.CancellationToken);Assert.Equal(3,calls);
    }

    [Fact]public void EveryCompressionOptionIsPartOfTheKey()
    {
        var cache=new MzfCompressionCache();var a=new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0);int calls=0;
        var choices=new[]{a,a with{Algorithm=MzfCompressionAlgorithm.Zx7},a with{Direction=CompressionDirection.Backward},a with{Zx0Quick=true},a with{Zx7EmbeddedLoader=true},a with{SkipBytes=1}};
        foreach(var choice in choices)cache.GetOrCreate([0,1,2],choice,_=>{calls++;return new([1],0);},TestContext.Current.CancellationToken);
        Assert.Equal(choices.Length,calls);
    }

    [Fact]public void CancelledAndFailedComputationsAreNeverStoredAndCancelledHitsDoNotReturn()
    {
        var cache=new MzfCompressionCache();var a=new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7);using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(()=>cache.GetOrCreate([1],a,_=>throw new IOException(),TestContext.Current.CancellationToken));Assert.Equal(0,cache.Count);
        Assert.Throws<OperationCanceledException>(()=>cache.GetOrCreate([1],a,_=>{cancellation.Cancel();return new([2],0);},cancellation.Token));Assert.Equal(0,cache.Count);
        cache.GetOrCreate([1],a,_=>new([2],0),TestContext.Current.CancellationToken);
        Assert.Throws<OperationCanceledException>(()=>cache.GetOrCreate([1],a,_=>throw new Exception(),cancellation.Token));
    }

    [Fact]public void LruEvictsByMemoryBudgetAndDoesNotRetainOversizedPayloads()
    {
        var cache=new MzfCompressionCache(514);var a=new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7);int calls=0;
        CompressionPayload Compute(byte[] _){calls++;return new([9],0);}
        cache.GetOrCreate([1],a,Compute,TestContext.Current.CancellationToken);cache.GetOrCreate([2],a,Compute,TestContext.Current.CancellationToken);cache.GetOrCreate([1],a,Compute,TestContext.Current.CancellationToken);cache.GetOrCreate([3],a,Compute,TestContext.Current.CancellationToken);
        cache.GetOrCreate([2],a,Compute,TestContext.Current.CancellationToken);Assert.Equal(4,calls);Assert.Equal(2,cache.Count);Assert.True(cache.Bytes<=514);
        cache.GetOrCreate([4],a,_=>new(new byte[600],0),TestContext.Current.CancellationToken);Assert.Equal(2,cache.Count);
    }

    [Theory][InlineData(2)][InlineData(3)]
    public async Task SharedPayloadStillUsesCurrentHeaderAndValidatesTarget(int algorithm)
    {
        using var files=new MediaDocumentTests();var session=await files.Open();var source=session.Model.Records[0];var options=new MzfCompressionOptions((MzfCompressionAlgorithm)algorithm);
        var first=MzfCompressionService.Compress(source,options,CompressionTarget.MzfTape,TestContext.Current.CancellationToken);var secondSource=source.DeepClone();var header=secondSource.Header;header.MzfFname[0]=(byte)'Z';header.MzfStart=0x5000;header.MzfExec=0x5000;secondSource.Header=header;
        var second=MzfCompressionService.Compress(secondSource,options,CompressionTarget.MzfTape,TestContext.Current.CancellationToken);
        Assert.Equal((byte)'Z',second.Record.Header.MzfFname[0]);Assert.Equal(source.Body.MzfBody,MzfDecompressionService.Decompress(second.Record).Record.Body.MzfBody);
        Assert.Equal(0x5000,MzfDecompressionService.Decompress(second.Record).Record.Header.MzfStart);
        second.Record.Body.MzfBody[0]^=255;var again=MzfCompressionService.Compress(source,options,CompressionTarget.MzfTape,TestContext.Current.CancellationToken);Assert.Equal(first.Record.Body.MzfBody,again.Record.Body.MzfBody);
        Assert.Contains($"{first.PackedSize:N0} / {first.OriginalSize:N0} B",MzfCompressionSizes.Describe(first.Record));
        var row=new RecordRow(first.Record,0);Assert.Contains("%",row.CompressionSizes);Assert.Contains("%",row.Compression);
        var embedded=new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7,Zx7EmbeddedLoader:true);
        MzfCompressionService.Compress(source,embedded,CompressionTarget.MzfTape,TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(()=>MzfCompressionService.Compress(source,embedded,CompressionTarget.QuickDisk,TestContext.Current.CancellationToken));
    }
}
