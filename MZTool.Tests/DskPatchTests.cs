namespace MZTools.Tests;

public sealed class DskPatchTests
{
    [Fact]
    public void ExportJsonPreviewApplyReproducesExactTargetAndKeepsOriginalUntilApply()
    {
        var source = DskDocumentFactory.CreateCpm(false);
        var target = DskDocument.Open(source.Serialize());
        target.FileSystem.Insert("ADDED.BIN", Enumerable.Range(0, 8000).Select(i => (byte)(i * 13)).ToArray()); target.MarkModified();
        byte[] before = source.Serialize(); byte[] expected = target.Serialize();
        var patch = DskPatchService.FromJson(DskPatchService.ToJson(DskPatchService.Export(before, expected)));
        var preview = DskPatchService.Preview(source, patch);
        Assert.Equal(before, source.Serialize()); Assert.False(source.IsModified);
        Assert.Contains("descriptor", preview.Report);
        DskPatchService.Apply(source, preview);
        Assert.Equal(expected, source.Serialize());
        Assert.Equal(patch.TargetSha256, DskHexEditService.Hash(source.Serialize()));
        Assert.True(source.IsModified);
    }

    [Theory]
    [InlineData("source-hash")]
    [InlineData("geometry")]
    [InlineData("expected-bytes")]
    [InlineData("expected-hash")]
    [InlineData("target-hash")]
    [InlineData("identity")]
    [InlineData("negative-offset")]
    [InlineData("overlap")]
    [InlineData("version")]
    public void InvalidPatchNeverChangesSource(string change)
    {
        var (source, patch) = SingleChange();
        var range = patch.Ranges.Single();
        patch = change switch
        {
            "source-hash" => patch with { SourceSha256 = new string('0', 64) },
            "geometry" => patch with { SourceGeometry = patch.SourceGeometry with { Cylinders = 1 } },
            "expected-bytes" => patch with { Ranges = [range with { ExpectedOriginalHex = "00", ExpectedOriginalSha256 = DskHexEditService.Hash([0]) }] },
            "expected-hash" => patch with { Ranges = [range with { ExpectedOriginalSha256 = new string('0', 64) }] },
            "target-hash" => patch with { TargetSha256 = new string('0', 64) },
            "identity" => patch with { Ranges = [range with { R = 99 }] },
            "negative-offset" => patch with { Ranges = [range with { Offset = -1 }] },
            "overlap" => patch with { Ranges = [range, range] },
            _ => patch with { Version = 2 }
        };
        byte[] before = source.Serialize();
        Assert.Throws<InvalidDataException>(() => DskPatchService.Preview(source, patch));
        Assert.Equal(before, source.Serialize()); Assert.False(source.IsModified);
    }

    [Fact]
    public void ChangedDocumentInvalidatesValidatedPreview()
    {
        var (source, patch) = SingleChange();
        var preview = DskPatchService.Preview(source, patch);
        source.FileSystem.Insert("KEEP.BIN", [1]); source.MarkModified();
        byte[] unsaved = source.Serialize();
        Assert.Throws<InvalidDataException>(() => DskPatchService.Apply(source, preview));
        Assert.Equal(unsaved, source.Serialize());
    }

    [Fact]
    public void GeometryAndContainerChangesCannotBeExportedAsSectorPatch()
    {
        var source = DskDocumentFactory.CreateCpm(false);
        var target = DskDocument.Open(source.Serialize());
        target.Image.Creator = "Other creator"; target.MarkModified();
        Assert.Throws<InvalidDataException>(() => DskPatchService.Export(source.Serialize(), target.Serialize()));
        Assert.Throws<InvalidDataException>(() => DskPatchService.Export(source.Serialize(), DskDocumentFactory.CreateCpm(true).Serialize()));
        target = DskDocument.Open(source.Serialize()); target.Image.Tracks[10]!.Sectors.Reverse(); target.MarkModified();
        Assert.Throws<InvalidDataException>(() => DskPatchService.Export(source.Serialize(), target.Serialize()));
    }

    [Fact]
    public void MalformedJsonAndFilesystemDamageAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => DskPatchService.FromJson("{broken"));
        Assert.Throws<InvalidDataException>(() => DskPatchService.Preview(DskDocumentFactory.CreateCpm(false), DskPatchService.FromJson("{}")));
        var source = DskDocumentFactory.CreateCpm(false);
        var session = DskHexEditService.OpenCpmBlock(source, 0);
        byte[] replacement = session.OriginalBuffer; replacement[0] = 0; replacement[12] = 0xFF;
        var broken = DskHexEditService.Preview(session, replacement).ResultBytes;
        Assert.Throws<InvalidDataException>(() => DskPatchService.Export(source.Serialize(), broken));
    }

    private static (DskDocument Source, DskPatch Patch) SingleChange()
    {
        var source = DskDocumentFactory.CreateCpm(false);
        var target = DskDocument.Open(source.Serialize());
        target.Image.Tracks[10]!.Sectors[2].Data[15] ^= 0x33; target.MarkModified();
        return (source, DskPatchService.Export(source.Serialize(), target.Serialize()));
    }
}
