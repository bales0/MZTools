namespace QDTool.Tests;

public class DskFileSystemTests
{
    [Fact]
    public void Fsmz_InsertRenameDeleteAndReopen_PreservesDataAndSpaceAccounting()
    {
        DskDocument document = DskDocumentFactory.CreateFsmz();
        Assert.Equal(DskFileSystemType.Fsmz, document.FileSystem.Type);
        Assert.Equal(127, Assert.IsType<FsmzFileSystem>(document.FileSystem).DirectoryLimit);
        long initialFree = document.FileSystem.FreeBytes;
        byte[] data = Enumerable.Range(0, 600).Select(index => (byte)(index * 17)).ToArray();

        document.FileSystem.Insert("PROGRAM", data, fileType: 1, loadAddress: 0x1200, executeAddress: 0x1234);
        DskFileEntry entry = Assert.Single(document.FileSystem.ReadDirectory());

        Assert.Equal(data, document.FileSystem.Extract(entry));
        Assert.Equal(0x1200, entry.LoadAddress);
        Assert.Equal(0x1234, entry.ExecuteAddress);
        Assert.Equal(initialFree - 3 * 256, document.FileSystem.FreeBytes);

        document.FileSystem.Rename(entry, "RENAMED");
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry renamed = Assert.Single(reopened.FileSystem.ReadDirectory());
        Assert.Equal("RENAMED", renamed.Name);
        Assert.Equal(data, reopened.FileSystem.Extract(renamed));

        reopened.FileSystem.Delete(renamed);
        Assert.Empty(reopened.FileSystem.ReadDirectory());
        Assert.Equal(initialFree, reopened.FileSystem.FreeBytes);
    }

    [Theory]
    [InlineData(false, 9, 40000)]
    [InlineData(true, 18, 70000)]
    public void Cpm_FactoryUsesSharpGeometryAndRoundTripsMultiExtentFiles(bool highDensity, int sectors, int length)
    {
        DskDocument document = DskDocumentFactory.CreateCpm(highDensity);
        Assert.Equal(DskFileSystemType.Cpm, document.FileSystem.Type);
        Assert.Equal(16, document.Image.Tracks[1]!.Sectors.Count);
        Assert.All(document.Image.Tracks[1]!.Sectors, sector => Assert.Equal(256, sector.Data.Length));
        Assert.Equal(sectors, document.Image.Tracks[0]!.Sectors.Count);
        Assert.Equal(highDensity ? new byte[] { 1, 7, 13, 2, 8, 14 } : new byte[] { 1, 6, 2, 7, 3, 8 },
            document.Image.Tracks[0]!.Sectors.Take(6).Select(sector => sector.SectorId).ToArray());
        byte[] data = Enumerable.Range(0, length).Select(index => (byte)(index * 29 + 3)).ToArray();

        document.FileSystem.Insert("MULTI.BIN", data, user: 7);
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry entry = Assert.Single(reopened.FileSystem.ReadDirectory());
        byte[] extracted = reopened.FileSystem.Extract(entry);

        Assert.Equal(7, entry.User);
        Assert.True(entry.Extents > 1);
        Assert.Equal((length + 127) / 128 * 128, entry.Size);
        Assert.Equal(data, extracted[..data.Length]);
        Assert.All(extracted[data.Length..], value => Assert.Equal(0x1A, value));
    }

    [Fact]
    public void Mrs_InsertRenameDeleteAndReopen_PreservesBlockBasedPayload()
    {
        DskDocument document = DskDocumentFactory.CreateMrs();
        Assert.Equal(DskFileSystemType.Mrs, document.FileSystem.Type);
        byte[] data = Enumerable.Range(0, 700).Select(index => (byte)(index * 11)).ToArray();

        document.FileSystem.Insert("DEMO.BIN", data, loadAddress: 0x2000, executeAddress: 0x2100);
        DskFileEntry entry = Assert.Single(document.FileSystem.ReadDirectory());
        Assert.Equal(2, entry.Blocks);
        Assert.Equal(data, document.FileSystem.Extract(entry)[..data.Length]);

        document.FileSystem.Rename(entry, "OTHER.COM");
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry renamed = Assert.Single(reopened.FileSystem.ReadDirectory());
        Assert.Equal("OTHER", renamed.Name);
        Assert.Equal("COM", renamed.Extension);
        Assert.Equal(0x2000, renamed.LoadAddress);
        Assert.Equal(0x2100, renamed.ExecuteAddress);
        Assert.Equal(data, reopened.FileSystem.Extract(renamed)[..data.Length]);

        reopened.FileSystem.Delete(renamed);
        Assert.Empty(reopened.FileSystem.ReadDirectory());
    }

    [Fact]
    public void Uniform512ImageWithoutSharpBootTrack_RemainsRaw()
    {
        DskImage image = DskImage.CreateUniform(80, 2, 9, 512, 1, 0x4E, 0xE5, "raw");
        DskDocument document = DskDocument.Open(image.Serialize());

        Assert.Equal(DskFileSystemType.Raw, document.FileSystem.Type);
    }
}
