namespace MZTools.Tests;

public class DskPhysicalRepairTests
{
    private static byte[] Image() => DskImage.CreateUniform(3, 1, 2, 512, 1, 0x4E, 0xE5, "source").Serialize();

    [Fact]
    public void MetadataAndOrderChangePreservesEveryPayloadAndOtherTracks()
    {
        var document = DskDocument.Open(Image());
        document.Image.Tracks[1]!.Sectors[0].Data[0] = 0x11;
        document.Image.Tracks[1]!.Sectors[1].Data[0] = 0x22;
        document.MarkModified();
        var before = document.Serialize();
        var change = new DskTrackProperties(1, 1, 0, 0x33, 0x44, [new(1, 7, 0, 0), new(0, 8, 0, 0)]);
        var preview = DskPhysicalPropertyService.Preview(document, "edited", change);
        Assert.Equal(before, document.Serialize());
        preview.Apply(document);
        var reopened = DskImage.Parse(document.Serialize());
        Assert.Equal("edited", reopened.Creator);
        Assert.Equal(new byte[] { 7, 8 }, reopened.Tracks[1]!.Sectors.Select(s => s.SectorId));
        Assert.Equal(0x22, reopened.Tracks[1]!.Sectors[0].Data[0]);
        Assert.Equal(0x11, reopened.Tracks[1]!.Sectors[1].Data[0]);
        Assert.Equal(0x33, reopened.Tracks[1]!.Gap);
        Assert.Equal(0x44, reopened.Tracks[1]!.Filler);
        int start = reopened.Tracks[0]!.FileOffset;
        Assert.Equal(before.AsSpan(start, reopened.Tracks[0]!.BlockSize).ToArray(), document.Serialize().AsSpan(start, reopened.Tracks[0]!.BlockSize).ToArray());
        Assert.True(document.IsModified);
    }

    [Fact]
    public void DuplicateOrderIsRejectedAndDocumentUnchanged()
    {
        var document = DskDocument.Open(Image()); var original = document.Serialize();
        Assert.Throws<InvalidDataException>(() => DskPhysicalPropertyService.Preview(document, "test", new(0, 0, 0, 0x4E, 0xE5, [new(0, 1, 0, 0), new(0, 2, 0, 0)])));
        Assert.Equal(original, document.Serialize());
    }

    [Theory]
    [InlineData(0x30, 2)]
    [InlineData(0x34, 1)]
    public void DeterministicHeaderRepairPreservesAllTrackBytes(int offset, byte value)
    {
        byte[] original = Image(); byte[] damaged = (byte[])original.Clone(); damaged[offset] = value;
        var plan = DskContainerRepairService.Inspect(damaged).Single(p => p.Risk == DskRepairRisk.SafeDeterministic);
        Assert.Equal(original, plan.Preview!.Result);
        Assert.Equal(original[256..], plan.Preview.Result[256..]);
        Assert.Equal(value, damaged[offset]);
        Assert.Contains("Analyzer before:", plan.Preview.Report);
        Assert.Contains("Analyzer after:", plan.Preview.Report);
        Assert.Contains("Changed byte ranges:", plan.Preview.Report);
        Assert.Equal(0, DskAnalyzer.Analyze(plan.Preview.Result).Errors);
    }

    [Fact]
    public void TrailingBytesAreNeverSilentlyAbsorbedOrTruncated()
    {
        byte[] original = Image(); byte[] trailing = original.Concat(new byte[256]).ToArray();
        var plans = DskContainerRepairService.Inspect(trailing);
        Assert.DoesNotContain(plans, p => p.Risk == DskRepairRisk.SafeDeterministic);
        var truncate = plans.Single(p => p.RequiresTruncateConfirmation);
        Assert.Equal(original, truncate.Preview!.Result);
        Assert.Equal(original.Length + 256, trailing.Length);
    }

    [Fact]
    public void MissingTracksAndWrongCHAreAmbiguous()
    {
        byte[] missing = Image(); missing[0x30] = 4; missing[0x37] = 0;
        Assert.DoesNotContain(DskContainerRepairService.Inspect(missing), p => p.Risk == DskRepairRisk.SafeDeterministic);
        byte[] wrong = Image(); wrong[0x30] = 2; wrong[256 + 0x10] = 7;
        Assert.DoesNotContain(DskContainerRepairService.Inspect(wrong), p => p.Risk == DskRepairRisk.SafeDeterministic);
    }

    [Fact]
    public void StalePreviewCannotApply()
    {
        var document = DskDocument.Open(Image());
        var preview = DskPhysicalPropertyService.Preview(document, "new", new(0, 0, 0, 0x4E, 0xE5, [new(0, 1, 0, 0), new(1, 2, 0, 0)]));
        document.Image.Tracks[2]!.Sectors[0].Data[0] ^= 1; document.MarkModified();
        var changed = document.Serialize();
        Assert.Throws<InvalidOperationException>(() => preview.Apply(document));
        Assert.Equal(changed, document.Serialize());
    }

    [Fact]
    public void IntentionalFdcStatusEditsArePreviewedAndPreservePayload()
    {
        var document = DskDocument.Open(Image()); var payload = document.Image.Tracks[0]!.Sectors[0].Data.ToArray();
        var preview = DskPhysicalPropertyService.Preview(document, "source", new(0, 0, 0, 0x4E, 0xE5, [new(0, 1, 0x20, 0x20), new(1, 2, 0, 0)]));
        Assert.Contains("DSK_FDC_STATUS", preview.Report); preview.Apply(document);
        Assert.Equal(0x20, document.Image.Tracks[0]!.Sectors[0].FdcStatus1); Assert.Equal(payload, document.Image.Tracks[0]!.Sectors[0].Data);
    }
}
