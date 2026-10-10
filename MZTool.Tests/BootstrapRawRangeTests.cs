namespace MZTools.Tests;

public class BootstrapRawRangeTests
{
    [Fact]
    public void BootstrapMetadataOnlyChangesExpectedHeaderFields()
    {
        var document = DskDocumentFactory.CreatePersonalCpm80(false); var before = document.Serialize(); var metadata = BootstrapService.Inspect(document);
        var preview = BootstrapService.PreviewMetadata(document, metadata with { Name = "BOOT", Load = 0x8000, Execute = 0x8100 });
        Assert.Equal(before, document.Serialize());
        DskHexEditService.Apply(document, preview, true); var after = document.Serialize();
        var header = DskAnalyzer.Analyze(document).Sectors.Single(s => s.Track == 1 && s.R == 1);
        Assert.All(Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]), i => Assert.True(i >= header.FileOffset + 7 && i < header.FileOffset + 26));
        Assert.Equal("BOOT", BootstrapService.Inspect(document).Name);
        Assert.Equal(before[(int)(header.FileOffset + 256)..], after[(int)(header.FileOffset + 256)..]);
    }

    [Fact]
    public void SizeChangeIsRefusedAndClearNeedsFilesystemConsent()
    {
        var document = DskDocumentFactory.CreatePersonalCpm80(false); var before = document.Serialize();
        Assert.Throws<InvalidDataException>(() => BootstrapService.PreviewMetadata(document, BootstrapService.Inspect(document) with { Size = 1 }));
        var clear = BootstrapService.PreviewHeader(document, new byte[256]);
        Assert.True(clear.RequiresFilesystemConfirmation); Assert.Throws<InvalidDataException>(() => DskHexEditService.Apply(document, clear)); Assert.Equal(before, document.Serialize());
    }

    [Fact]
    public void RangeOrdersAreExplicitAndExactLengthIsRequired()
    {
        var document = DskDocumentFactory.CreateRaw(1, 1, 8, 512, 1, 0x4E, 0xE5, "test", DskDocumentFactory.RawSectorOrder.PersonalCpm80);
        document.Image.Tracks[0]!.Sectors[0].Data[0] = 1; document.Image.Tracks[0]!.Sectors[1].Data[0] = 5; document.Image.Tracks[0]!.Sectors[2].Data[0] = 2; document.MarkModified();
        DskSectorAddress[] selection = [new(0, 1), new(0, 2), new(0, 0)]; var original = document.Serialize();
        Assert.Equal(new byte[] { 1, 5, 2 }, DskRawRangeService.Export(document, selection, DskRawOrder.Physical).Where((_, i) => i % 512 == 0));
        Assert.Equal(new byte[] { 1, 2, 5 }, DskRawRangeService.Export(document, selection, DskRawOrder.SectorId).Where((_, i) => i % 512 == 0));
        Assert.Throws<InvalidDataException>(() => DskRawRangeService.PreviewImport(document, selection, DskRawOrder.Physical, [1])); Assert.Equal(original, document.Serialize());
        byte[] replacement = DskRawRangeService.Export(document, selection, DskRawOrder.SectorId); replacement[0] = 0x44;
        var preview = DskRawRangeService.PreviewImport(document, selection, DskRawOrder.SectorId, replacement); Assert.Equal(original, document.Serialize()); DskHexEditService.Apply(document, preview, true);
        Assert.Equal(0x44, document.Image.Tracks[0]!.Sectors[0].Data[0]);
    }

    [Fact]
    public void BootstrapTrackRoundTripAndClearPreserveFsmzAllocationStructures()
    {
        var document = DskDocumentFactory.CreateFsmz(); var device = new DskBlockDevice(document.Image); byte[] header = new byte[256]; header[0] = 3; "IPLPRO"u8.CopyTo(header.AsSpan(1)); device.WriteSector(1, 1, header, true); document.MarkModified();
        var boot = BootstrapService.ExportBootstrap(document); Assert.Equal(4096, boot.Length);
        var same = BootstrapService.PreviewBootstrap(document, boot); Assert.Empty(same.Changes);
        byte[] info = device.ReadSector(1, 16, true); var clear = BootstrapService.PreviewBootstrap(document, new byte[4096], true); DskHexEditService.Apply(document, clear, true);
        Assert.Equal(info, new DskBlockDevice(document.Image).ReadSector(1, 16, true)); Assert.Equal(0, DskAnalyzer.Analyze(document).Errors);
    }
}
