namespace MZTools.Tests;

public sealed class CompressionWorkflowTests
{
    [Theory]
    [InlineData((int)MzfCompressionAlgorithm.None)]
    [InlineData((int)MzfCompressionAlgorithm.Zx0)]
    [InlineData((int)MzfCompressionAlgorithm.Zx7)]
    public async Task ReopenedMultiIplCanChangeCompressionWithoutDoublePacking(int algorithm)
    {
        var source = Source();
        var policy = new MzfCompressionOptions((MzfCompressionAlgorithm)algorithm, CompressionDirection.Backward);
        var packed = MzfCompressionService.Compress(source, policy, CompressionTarget.IplDsk);
        var keep = new MultiGameIplInput(source, "KEEP", new(MzfCompressionAlgorithm.None), source.Body.MzfBody.Length);
        var disk = Mz800MultiGameIplDskWriter.Build([new(packed.Record, "EDIT", packed.AppliedOptions, packed.OriginalSize), keep]);
        var fs = Assert.IsType<MultiGameIplFileSystem>(DskDocument.Open(disk.Image).FileSystem);
        var row = new MultiGameIplRow(1, fs.GetInputs()[0]);
        Assert.True(row.CanChangeCompression);
        Assert.Equal(source.Body.MzfBody, row.Source.Body.MzfBody);
        row.SetCompressionOptions(new(MzfCompressionAlgorithm.Zx0, CompressionDirection.Forward, Zx0Quick: true));
        var changed = await row.PrepareAsync(CancellationToken.None);
        Assert.Equal(source.Body.MzfBody, MzfDecompressionService.Decompress(changed.Record).Record.Body.MzfBody);
        var rebuilt = Mz800MultiGameIplDskWriter.Build([new(changed.Record, "EDIT", changed.AppliedOptions, changed.OriginalSize), fs.GetInputs()[1]]);
        var reopened = Assert.IsType<MultiGameIplFileSystem>(DskDocument.Open(rebuilt.Image).FileSystem);
        Assert.Equal(source.Body.MzfBody, MzfDecompressionService.Decompress(reopened.GetInputs()[0].Record).Record.Body.MzfBody);
        Assert.Equal(source.Body.MzfBody, reopened.GetInputs()[1].Record.Body.MzfBody);
    }

    [Fact]
    public async Task UnknownStoredLoaderStaysLockedAndUnchanged()
    {
        var source = Source();
        var row = new MultiGameIplRow(1, new MultiGameIplInput(source, "UNKNOWN", new(MzfCompressionAlgorithm.Zx0), source.Body.MzfBody.Length));
        Assert.False(row.CanChangeCompression); Assert.Contains("not recognized", row.CompressionHint);
        Assert.Equal(source.Body.MzfBody, (await row.PrepareAsync(CancellationToken.None)).Record.Body.MzfBody);
    }
    private static TapeRecord Source() => TapeTestData.ReadRecord(TapeTestData.CreateMzf(
        Enumerable.Range(0, 2048).Select(i => (byte)(i % 97)).ToArray()));

    [Fact]
    public async Task IplRowsKeepCompleteSettingsAndInvalidateCacheWhenOnlyDirectionChanges()
    {
        var source = Source(); var row = new MultiGameIplRow(1, source, "TEST");
        var backward = new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0, CompressionDirection.Backward, Zx0Quick: true);
        row.SetCompressionOptions(backward);
        var packed = await row.PrepareAsync(CancellationToken.None);
        Assert.Equal(backward, packed.AppliedOptions);
        Assert.Contains("quick backward", row.CompressionSummary);
        Assert.Equal(source.Body.MzfBody, MzfDecompressionService.Decompress(packed.Record).Record.Body.MzfBody);
        Assert.Same(packed, await row.PrepareAsync(CancellationToken.None));
        var forward = backward with { Direction = CompressionDirection.Forward };
        row.SetCompressionOptions(forward);
        var repacked = await row.PrepareAsync(CancellationToken.None);
        Assert.NotSame(packed, repacked); Assert.Equal(forward, repacked.AppliedOptions);
        Assert.Equal(source.Body.MzfBody, MzfDecompressionService.Decompress(repacked.Record).Record.Body.MzfBody);
    }

    [Fact]
    public async Task ImportedBackwardCompressionIsRetainedUntilSettingsChange()
    {
        var options = new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7, CompressionDirection.Backward);
        var packed = MzfCompressionService.Compress(Source(), options, CompressionTarget.IplDsk);
        var row = new MultiGameIplRow(1, packed.Record, "IMPORT");
        Assert.Equal(options, row.CompressionOptions);
        var prepared = await row.PrepareAsync(CancellationToken.None);
        Assert.Equal(packed.Record.Body.MzfBody, prepared.Record.Body.MzfBody);
        row.Compression = "None";
        Assert.Equal(MzfCompressionAlgorithm.None, row.CompressionOptions.Algorithm);
        Assert.Equal(Source().Body.MzfBody, (await row.PrepareAsync(CancellationToken.None)).Record.Body.MzfBody);
    }

    [Fact]
    public void UnsupportedIplSettingsLeaveExistingSettingsIntact()
    {
        var row = new MultiGameIplRow(1, Source(), "TEST");
        var original = row.CompressionOptions;
        Assert.Throws<InvalidOperationException>(() => row.SetCompressionOptions(new(MzfCompressionAlgorithm.Zx7, Zx7EmbeddedLoader: true)));
        Assert.Throws<InvalidOperationException>(() => row.SetCompressionOptions(new(MzfCompressionAlgorithm.Zx0, SkipBytes: 1)));
        Assert.Equal(original, row.CompressionOptions);
    }

    [Fact]
    public void PartialCompressionCannotSilentlyDropBytesFromStandaloneCom()
    {
        var image = MzfNativeImageParser.Parse(Source());
        var error = Assert.Throws<InvalidOperationException>(() => NativeComConversionService.Build(image,
            CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, new(MzfCompressionAlgorithm.Zx0, SkipBytes: 10)));
        Assert.Contains("standalone COM", error.Message);
    }
}
