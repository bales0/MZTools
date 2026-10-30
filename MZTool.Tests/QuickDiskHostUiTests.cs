namespace MZTools.Tests;
public partial class QuickDiskHostDetectionTests {
    [Fact]
    public void DecodedUnitExportRejectsRawTrackAndMakesAnIndependentCopy()
    {
        var decoded = new QuickDiskInspectorControl.Unit("Block", "cell 42", 3, "CRC valid", [1, 2, 3], IsDecoded: true);
        byte[] exported = QuickDiskInspectorControl.GetDecodedExport(decoded);
        Assert.Equal(decoded.Data, exported); exported[0] = 99; Assert.Equal(1, decoded.Data[0]);
        Assert.Throws<InvalidOperationException>(() => QuickDiskInspectorControl.GetDecodedExport(decoded with { IsDecoded = false }));
        Assert.Throws<InvalidOperationException>(() => QuickDiskInspectorControl.GetDecodedExport(decoded with { Data = [] }));
    }
}
