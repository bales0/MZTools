using System.Buffers.Binary;

namespace MZTools.Tests;

public sealed class DskExtendedLengthTests
{
    [Fact]
    public void AllNewDiskFactoriesDeclareActualExtendedSectorLengths()
    {
        foreach (var document in new[] { DskDocumentFactory.CreatePersonalCpm80(false), DskDocumentFactory.CreatePersonalCpm80(true),
            DskDocumentFactory.CreateCpm(false), DskDocumentFactory.CreateCpm(true), DskDocumentFactory.CreateFsmz(), DskDocumentFactory.CreateMrs() })
            AssertLengths(document.Serialize());
        AssertLengths(Mz800DskImage.Create("IPL geometry"));
        var record = CompressionTestData.ReadRecord(CompressionTestData.CreateMzf(new byte[512]));
        AssertLengths(Mz800IplDskWriter.Build(record));
        AssertLengths(Mz800MultiGameIplDskWriter.Build([new(record, "TEST", new(MzfCompressionAlgorithm.None), 512)]).Image);
    }

    [Fact]
    public void LegacyZeroLengthsRemainBytePreservedButExplicitlyDiagnosedAndCompared()
    {
        var valid = Mz800DskImage.Create("test"); var image = DskImage.Parse(valid);
        int offset = image.Tracks[1]!.FileOffset + 0x18 + 6;
        byte[] legacy = (byte[])valid.Clone(); legacy[offset] = legacy[offset + 1] = 0;
        var old = DskDocument.Open(legacy);
        Assert.Equal(legacy, old.Serialize()); Assert.Equal(legacy, old.Image.Serialize());
        var layout = DskAnalyzer.Analyze(old);
        var warning = Assert.Single(layout.Issues, i => i.Code == "DSK_EXTENDED_ZERO_LENGTH");
        Assert.Equal(1, warning.Track); Assert.Equal(0, warning.Sector);
        Assert.Contains($"0x{offset:X}", warning.Detail);
        var physical = layout.Sectors.Single(s => s.Track == 1 && s.PhysicalIndex == 0);
        Assert.Equal(0, physical.DeclaredDataLength); Assert.Equal(256, physical.DataLength);
        Assert.Equal(offset, physical.DescriptorFileOffset + 6);
        var comparison = DskCompareService.Compare(old, DskDocument.Open(valid));
        var row = Assert.Single(comparison.Items, i => i.Level == DskDiffLevel.PhysicalSectors && i.State == DskDiffState.Changed);
        Assert.True(row.DescriptorLengthChanged);
        Assert.Contains("Stored length: 0 B", row.LeftValue); Assert.Contains("Stored length: 256 B", row.RightValue);
        Assert.Contains("Data changed: False", row.Detail); Assert.Contains("+6/+7", row.Detail);
        Assert.Contains($"0x{offset:X}", row.Detail);
        Assert.Empty(DskCompareService.ByteDifferences(row.LeftBytes, row.RightBytes));
        Assert.Equal(7, Assert.Single(DskCompareService.ByteDifferences(row.LeftStructure, row.RightStructure)).Offset);
        byte[] repaired = DskExtendedLengthRepair.CreateCopy(legacy);
        Assert.Equal(valid, repaired); Assert.Equal(legacy, old.Serialize());
        Assert.Equal(repaired, DskExtendedLengthRepair.CreateCopy(repaired));
    }

    [Theory]
    [InlineData("HLIPA.dsk")]
    [InlineData("compress_HLIPA.dsk")]
    [InlineData("multi.dsk")]
    [InlineData("multi2.dsk")]
    [InlineData("CPM80.dsk")]
    public void RealFailingFixturesRepairOnlyZeroDescriptorLengthFields(string name)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MZTool.Tests/DSK", name));
        if (!File.Exists(path)) return; // Optional local hardware regression fixtures.
        byte[] original = File.ReadAllBytes(path), before = (byte[])original.Clone();
        var image = DskImage.Parse(original);
        var allowed = new HashSet<int>();
        foreach (var track in image.Tracks.Where(t => t != null))
            for (int index = 0; index < track!.Sectors.Count; index++)
                if (track.Sectors[index].DeclaredDataLength == 0)
                {
                    allowed.Add(track.FileOffset + 0x18 + index * 8 + 6);
                    allowed.Add(track.FileOffset + 0x18 + index * 8 + 7);
                }
        byte[] repaired = DskExtendedLengthRepair.CreateCopy(original);
        Assert.Equal(before, original); Assert.Equal(before.Length, repaired.Length);
        for (int offset = 0; offset < original.Length; offset++)
            if (!allowed.Contains(offset)) Assert.Equal(original[offset], repaired[offset]);
        AssertLengths(repaired);
        Assert.DoesNotContain(DskAnalyzer.Analyze(repaired).Issues, i => i.Code == "DSK_EXTENDED_ZERO_LENGTH");
        var after = DskImage.Parse(repaired);
        for (int t = 0; t < image.Tracks.Count; t++)
            for (int s = 0; s < image.Tracks[t]!.Sectors.Count; s++)
                Assert.Equal(image.Tracks[t]!.Sectors[s].Data, after.Tracks[t]!.Sectors[s].Data);
    }

    [Fact]
    public void CopyRepairNeverOverwritesExistingDestinationOrSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MZTools-Length-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.dsk"), destination = Path.Combine(directory, "fixed.dsk");
            byte[] bytes = Mz800DskImage.Create("test"); File.WriteAllBytes(source, bytes); File.WriteAllBytes(destination, [1, 2, 3]);
            Assert.Throws<IOException>(() => DskExtendedLengthRepair.WriteNewCopy(source, source));
            Assert.Throws<IOException>(() => DskExtendedLengthRepair.WriteNewCopy(source, destination));
            Assert.Equal(bytes, File.ReadAllBytes(source)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertLengths(byte[] bytes)
    {
        var image = DskImage.Parse(bytes);
        foreach (var track in image.Tracks.Where(t => t != null))
            for (int index = 0; index < track!.Sectors.Count; index++)
            {
                var sector = track.Sectors[index];
                Assert.Equal(sector.Data.Length, sector.DeclaredDataLength);
                Assert.Equal(sector.Data.Length, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(track.FileOffset + 0x18 + index * 8 + 6, 2)));
            }
    }
}
