namespace MZTools.Tests;

using System.Buffers.Binary;

public class QdmgCompatibilityTests
{
    [Theory]
    [InlineData("multi.dsk", false)]
    [InlineData("multi2.dsk", true)]
    public void BothFixturesExposeVerifiedEntriesAndUnchangedPayloads(string fixture, bool footer)
    {
        var bytes = File.ReadAllBytes(Fixture(fixture));
        var doc = DskDocument.Open(bytes);
        var fs = Assert.IsType<MultiGameIplFileSystem>(doc.FileSystem);
        Assert.Equal(DskFileSystemType.MultiIpl, fs.Type);
        Assert.Equal("MZTools multi-game IPL", fs.DisplayName);
        Assert.DoesNotContain("legacy", fs.MetadataLocationDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(footer ? MultiGameMetadataLayout.MenuFooter : MultiGameMetadataLayout.IplProComment, fs.Metadata.Layout);
        Assert.Equal(footer ? 390 : 32, fs.Metadata.Offset);
        Assert.Equal(footer ? 240 : 244, fs.Metadata.TableOffset);
        Assert.Equal(2, fs.Metadata.MenuSectorCount);
        Assert.True(fs.CanConfigure);
        var entries = fs.ReadDirectory();
        Assert.Equal(2, entries.Count);
        if (footer)
        {
            Entry(entries[0], "BELEGOST.NEW", 3, 41856, 0x1200, 0x1208, "Uncompressed");
            Entry(entries[1], "FLAPPY ver 1.0A", 167, 24331, 0x6B66, 0x6B66, "ZX0");
        }
        else
        {
            Entry(entries[0], "FLAPPY ver 1.0A", 3, 24331, 0x6B66, 0x6B66, "ZX0");
            Entry(entries[1], "Highway Ver 1.02", 99, 18143, 0x7892, 0x7892, "ZX7");
        }
        foreach (var entry in entries)
            Assert.Equal(ReadBytes(doc.Image, entry.StartBlock, (int)entry.Size), fs.Extract(entry));
        Assert.Equal(bytes, doc.Serialize()); Assert.False(doc.IsModified);
        // Existing Disk Map must expose both variants' program ranges.
        var map = DskAnalyzer.Analyze(doc);
        Assert.Equal(0, map.Errors);
        foreach (var entry in entries)
            Assert.Equal(entry.Blocks, map.Sectors.Count(s => s.Owners.Any(o => o.FileKey == entry.Key)));
    }

    [Theory]
    [InlineData("multi.dsk")]
    [InlineData("multi2.dsk")]
    public void RebuildAlwaysUsesMenuFooterAndPreservesEntries(string fixture)
    {
        var source = DskDocument.Open(Fixture(fixture));
        var fs = Assert.IsType<MultiGameIplFileSystem>(source.FileSystem);
        var originalBytes = source.Serialize();
        var built = Mz800MultiGameIplDskWriter.Build(fs.GetInputs());
        var rebuilt = DskDocument.Open(built.Image);
        var result = Assert.IsType<MultiGameIplFileSystem>(rebuilt.FileSystem);
        Assert.Equal(MultiGameMetadataLayout.MenuFooter, result.Metadata.Layout);
        Assert.All(ReadBytes(rebuilt.Image, 0, 256).Skip(0x20), b => Assert.Equal(0, b));
        Assert.Equal(fs.ReadDirectory().Count, result.ReadDirectory().Count);
        for (int i = 0; i < fs.ReadDirectory().Count; i++)
        {
            var before = fs.ReadDirectory()[i]; var after = result.ReadDirectory()[i];
            Assert.Equal(before.Name, after.Name); Assert.Equal(before.LoadAddress, after.LoadAddress);
            Assert.Equal(before.ExecuteAddress, after.ExecuteAddress); Assert.Equal(before.Size, after.Size);
            Assert.Equal(before.Notes, after.Notes); Assert.Equal(fs.Extract(before), result.Extract(after));
        }
        Assert.Equal(originalBytes, source.Serialize()); Assert.False(source.IsModified);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("entrysize")]
    [InlineData("zeroentries")]
    [InlineData("table")]
    [InlineData("sectors")]
    [InlineData("menustart")]
    [InlineData("payloadrange")]
    [InlineData("menuoverlap")]
    [InlineData("gameoverlap")]
    [InlineData("loadrange")]
    [InlineData("wrongoffset")]
    public void InvalidIplProMetadataDoesNotBecomeMultiIpl(string corruption)
    {
        var source = DskDocument.Open(Fixture("multi.dsk"));
        var boot = ReadBytes(source.Image, 0, 256);
        var menu = ReadBytes(source.Image, 1, 394);
        switch (corruption)
        {
            case "version": boot[36] = 99; break;
            case "entrysize": boot[37] = 25; break;
            case "zeroentries": Word(boot, 38, 0); break;
            case "table": Word(boot, 40, 390); break;
            case "sectors": Word(boot, 42, 3); break;
            case "menustart": Word(boot, 0x1E, 2); break;
            case "payloadrange": Word(menu, 244 + 16, 1279); break;
            case "menuoverlap": Word(menu, 244 + 16, 1); break;
            case "gameoverlap": Word(menu, 244 + 26 + 16, 3); break;
            case "loadrange": Word(menu, 244 + 20, 65000); break;
            case "wrongoffset": boot.AsSpan(32, 12).CopyTo(boot.AsSpan(64, 12)); boot.AsSpan(32, 12).Clear(); break;
        }
        WriteBytes(source.Image, 0, boot); WriteBytes(source.Image, 1, menu);
        var reopened = DskDocument.Open(source.Image.Serialize());
        Assert.NotEqual(DskFileSystemType.MultiIpl, reopened.FileSystem.Type);
        Assert.Contains(reopened.FileSystem.Type, new[] { DskFileSystemType.BootOnly, DskFileSystemType.Raw });
    }

    [Fact]
    public void MenuFooterTakesPrecedenceAndInvalidFooterDoesNotFallBackToComment()
    {
        var source = DskDocument.Open(Fixture("multi2.dsk"));
        var boot = ReadBytes(source.Image, 0, 256);
        var oldBoot = ReadBytes(DskDocument.Open(Fixture("multi.dsk")).Image, 0, 256);
        oldBoot.AsSpan(32, 12).CopyTo(boot.AsSpan(32));
        WriteBytes(source.Image, 0, boot);
        var current = Assert.IsType<MultiGameIplFileSystem>(DskDocument.Open(source.Image.Serialize()).FileSystem);
        Assert.Equal(MultiGameMetadataLayout.MenuFooter, current.Metadata.Layout);
        Assert.Equal("BELEGOST.NEW", current.ReadDirectory()[0].Name);
        var menu = ReadBytes(source.Image, 1, 402); menu[390 + 4] = 99;
        WriteBytes(source.Image, 1, menu);
        Assert.NotEqual(DskFileSystemType.MultiIpl, DskDocument.Open(source.Image.Serialize()).FileSystem.Type);
    }

    [Theory]
    [InlineData("multi.dsk", 244)]
    [InlineData("multi2.dsk", 240)]
    public void UnknownCompressionRemainsExtractableButCannotRebuild(string fixture, int table)
    {
        var doc = DskDocument.Open(Fixture(fixture));
        int menuSize = BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(doc.Image, 0, 256).AsSpan(0x14, 2));
        var menu = ReadBytes(doc.Image, 1, menuSize); menu[table + 25] = 77;
        WriteBytes(doc.Image, 1, menu);
        var fs = Assert.IsType<MultiGameIplFileSystem>(DskDocument.Open(doc.Image.Serialize()).FileSystem);
        Assert.False(fs.CanConfigure);
        Assert.NotEmpty(fs.Extract(fs.ReadDirectory()[0]));
        Assert.Throws<NotSupportedException>(() => fs.GetInputs());
    }

    private static void Entry(DskFileEntry e, string name, int start, int size, int load, int exec, string notes)
    {
        Assert.Equal(name, e.Name); Assert.Equal(start, e.StartBlock); Assert.Equal(size, e.Size);
        Assert.Equal(load, e.LoadAddress); Assert.Equal(exec, e.ExecuteAddress); Assert.Equal(notes, e.Notes);
    }
    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DSK", name));
    private static byte[] ReadBytes(DskImage image, int start, int size)
    {
        var result = new byte[size];
        for (int i = 0; i < size; i++)
        {
            int block = start + i / 256;
            result[i] = (byte)(image.Tracks[(block / 16) ^ 1]!.Sectors.Single(s => s.SectorId == block % 16 + 1).Data[i % 256] ^ 255);
        }
        return result;
    }
    private static void WriteBytes(DskImage image, int start, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            int block = start + i / 256;
            image.Tracks[(block / 16) ^ 1]!.Sectors.Single(s => s.SectorId == block % 16 + 1).Data[i % 256] = (byte)(bytes[i] ^ 255);
        }
    }
    private static void Word(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)value);
}
