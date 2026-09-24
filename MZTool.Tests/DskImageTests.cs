namespace QDTool.Tests;

public class DskImageTests
{
    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    public void UniformImage_RoundTripsAllSupportedSectorSizes(int sectorSize)
    {
        DskImage created = DskImage.CreateUniform(
            tracks: 3,
            sides: 2,
            sectorsPerTrack: 4,
            sectorSize,
            firstSectorId: 1,
            gap: 0x4E,
            filler: 0xE5,
            creator: "MZTools tests");
        created.GetTrack(1, 1).Sectors[2].FdcStatus1 = 0x20;
        created.GetTrack(1, 1).Sectors[2].FdcStatus2 = 0x40;
        created.GetTrack(2, 0).Sectors.Reverse();
        created.GetTrack(2, 0).Sectors[0].SectorId = 22;
        created.ContainerTrailingData = [0xAA, 0x55];

        byte[] serialized = created.Serialize();
        DskImage parsed = DskImage.Parse(serialized);

        Assert.Equal(serialized, parsed.Serialize());
        Assert.Equal(3, parsed.TrackCount);
        Assert.Equal(2, parsed.SideCount);
        Assert.Equal("MZTools tests", parsed.Creator);
        Assert.Equal(sectorSize, parsed.GetSector(1, 1, 3).Data.Length);
        Assert.Equal(0x20, parsed.GetSector(1, 1, 3).FdcStatus1);
        Assert.Equal(0x40, parsed.GetSector(1, 1, 3).FdcStatus2);
        Assert.Equal(22, parsed.GetTrack(2, 0).Sectors[0].SectorId);
        Assert.Equal(new byte[] { 0xAA, 0x55 }, parsed.ContainerTrailingData);
    }

    [Fact]
    public void Parser_UsesTsizeSumForVariableAndMissingTracks()
    {
        byte[] small = DskImage.CreateUniform(1, 1, 1, 128, 1, 0x2A, 0x11, "small").Serialize();
        byte[] large = DskImage.CreateUniform(1, 1, 2, 256, 7, 0x3A, 0x22, "large").Serialize();
        byte[] combined = new byte[DskImage.HeaderSize +
            (small.Length - DskImage.HeaderSize) +
            (large.Length - DskImage.HeaderSize)];
        small.AsSpan(0, DskImage.HeaderSize).CopyTo(combined);
        combined[0x30] = 3;
        combined[0x31] = 1;
        combined[0x34] = checked((byte)((small.Length - DskImage.HeaderSize) >> 8));
        combined[0x35] = checked((byte)((large.Length - DskImage.HeaderSize) >> 8));
        combined[0x36] = 0;
        small.AsSpan(DskImage.HeaderSize).CopyTo(combined.AsSpan(DskImage.HeaderSize));
        int secondOffset = small.Length;
        large.AsSpan(DskImage.HeaderSize).CopyTo(combined.AsSpan(secondOffset));
        combined[secondOffset + 0x10] = 1;
        combined[secondOffset + 0x18] = 1;
        combined[secondOffset + 0x20] = 1;

        DskImage parsed = DskImage.Parse(combined);

        Assert.Equal(combined, parsed.Serialize());
        Assert.Equal(2, parsed.GetTrack(1, 0).Sectors.Count);
        Assert.Null(parsed.Tracks[2]);
        Assert.Equal(secondOffset, parsed.GetTrack(1, 0).FileOffset);
    }

    [Fact]
    public void Parser_RejectsTrackThatExtendsPastImage()
    {
        byte[] image = DskImage.CreateUniform(1, 1, 1, 256, 1, 0x4E, 0xE5, "test").Serialize();
        byte[] truncated = image[..^1];

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => DskImage.Parse(truncated));

        Assert.Contains("truncated", exception.Message);
        Assert.Contains("track 0", exception.Message);
    }
}
