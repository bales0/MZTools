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
    [InlineData(false, 40, 1, 63)]
    [InlineData(false, 40, 2, 63)]
    [InlineData(false, 80, 2, 63)]
    [InlineData(true, 40, 2, 127)]
    public void FsmzFactorySupportsStandardAndIplDiskGeometries(bool ipldisk, int tracks, int sides, int directoryLimit)
    {
        DskDocument document = DskDocumentFactory.CreateFsmz(ipldisk, tracks, sides);

        Assert.Equal(DskFileSystemType.Fsmz, document.FileSystem.Type);
        Assert.Equal(tracks, document.Image.TrackCount);
        Assert.Equal(sides, document.Image.SideCount);
        Assert.Equal(directoryLimit, Assert.IsType<FsmzFileSystem>(document.FileSystem).DirectoryLimit);

        document.FileSystem.Insert("TEST", [1, 2, 3], loadAddress: 0x1200, executeAddress: 0x1200);
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        Assert.Equal([1, 2, 3], reopened.FileSystem.Extract(Assert.Single(reopened.FileSystem.ReadDirectory())));
    }

    [Theory]
    [InlineData(false, 9, 40000)]
    [InlineData(true, 18, 70000)]
    public void Cpm_FactoryUsesSharpGeometryAndRoundTripsMultiExtentFiles(bool highDensity, int sectors, int length)
    {
        DskDocument document = DskDocumentFactory.CreateCpm(highDensity);
        Assert.Equal(DskFileSystemType.Cpm, document.FileSystem.Type);
        Assert.Equal(highDensity ? "CP/M HD" : "LEC CP/M DD (720 KiB)", document.FileSystem.DisplayName);
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

    [Theory]
    [InlineData(false, 80, 1, 9, 170)]
    [InlineData(false, 80, 2, 9, 350)]
    [InlineData(true, 80, 1, 18, 170)]
    [InlineData(true, 80, 2, 18, 350)]
    public void LecCpmFactorySupportsOneAndTwoSidedDisks(bool highDensity, int tracks, int sides, int sectors, int expectedDsm)
    {
        DskDocument document = DskDocumentFactory.CreateCpm(highDensity, tracks, sides);
        CpmFileSystem fileSystem = Assert.IsType<CpmFileSystem>(document.FileSystem);

        Assert.Equal(expectedDsm, fileSystem.Dpb.Dsm);
        Assert.Equal(sides, document.Image.SideCount);
        Assert.Equal(sectors, document.Image.Tracks[0]!.Sectors.Count);
        byte[] payload = Enumerable.Range(0, 5000).Select(value => (byte)(value * 31)).ToArray();
        fileSystem.Insert("SIDE.BIN", payload);
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry entry = Assert.Single(reopened.FileSystem.ReadDirectory());
        Assert.Equal(payload, reopened.FileSystem.Extract(entry)[..payload.Length]);
    }

    [Fact]
    public void CpmMzfImportUsesHostEightDotThreeNameInsteadOfLongEmbeddedTitle()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MZF", "Tc122.mzf");
        TapeRecord record = new MZTFileReader().ReadStandaloneMzf(path);
        DskDocument document = DskDocumentFactory.CreateCpm(false);
        string embeddedName = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);

        string importName = DskEditorControl.GetImportName(document.FileSystem.Type, path, embeddedName);
        document.FileSystem.Insert(importName, record.Body.MzfBody,
            record.Header.MzfFtype, record.Header.MzfStart, record.Header.MzfExec);
        DskFileEntry entry = Assert.Single(document.FileSystem.ReadDirectory());

        Assert.Equal("Turbo Copy V1.22", embeddedName);
        Assert.Equal("TC122.MZF", importName);
        Assert.Equal("TC122", entry.Name);
        Assert.Equal("MZF", entry.Extension);
        Assert.Equal(record.Body.MzfBody, document.FileSystem.Extract(entry)[..record.Body.MzfBody.Length]);
    }

    [Theory]
    [InlineData("long file name.mzf", "LONG_FIL.MZF")]
    [InlineData("a.b.c.bin", "A_B_C.BIN")]
    [InlineData("?.mzf", "FILE.MZF")]
    public void CpmImportNameIsNormalizedToEightDotThree(string sourceName, string expected)
    {
        Assert.Equal(expected, DskEditorControl.GetImportName(
            DskFileSystemType.Cpm, sourceName, "An embedded MZF title that is too long"));
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

    [Theory]
    [InlineData(80, 1, 720)]
    [InlineData(80, 2, 1440)]
    public void MrsFactorySupportsOneAndTwoSidedCapacity(int tracks, int sides, int expectedBlocks)
    {
        DskDocument document = DskDocumentFactory.CreateMrs(tracks, sides);
        byte[] payload = Enumerable.Range(0, 900).Select(value => (byte)(value * 7)).ToArray();
        document.FileSystem.Insert("ONE.MZF", payload, loadAddress: 0x1200, executeAddress: 0x1300);
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry entry = Assert.Single(reopened.FileSystem.ReadDirectory());

        Assert.Equal(DskFileSystemType.Mrs, reopened.FileSystem.Type);
        Assert.Equal(sides, reopened.Image.SideCount);
        Assert.Equal(expectedBlocks * 512L, reopened.Image.Tracks.Count * 9L * 512L);
        Assert.Equal(payload, reopened.FileSystem.Extract(entry)[..payload.Length]);
    }

    [Fact]
    public void Uniform512ImageWithoutSharpBootTrack_RemainsRaw()
    {
        DskImage image = DskImage.CreateUniform(80, 2, 9, 512, 1, 0x4E, 0xE5, "raw");
        DskDocument document = DskDocument.Open(image.Serialize());

        Assert.Equal(DskFileSystemType.Raw, document.FileSystem.Type);
    }

    [Fact]
    public void BlankSharpRawGeometry_RemainsRawWithoutFsmzWarning()
    {
        DskDocument document = DskDocumentFactory.CreateRaw(40, 2, 16, 256, 1, 0x2A, 0xE5, "MZTools");

        Assert.Equal(DskFileSystemType.Raw, document.FileSystem.Type);
        Assert.Equal("Raw / unknown", document.FileSystem.DisplayName);
        Assert.Empty(document.FileSystem.Warnings);
        Assert.Equal(327680, document.Image.TotalSectorDataBytes);
    }

    [Theory]
    [InlineData(9, 737280)]
    [InlineData(18, 1474560)]
    public void RawDdAndHdFactoriesKeepRequestedCapacity(int sectors, long expectedBytes)
    {
        DskDocument document = DskDocumentFactory.CreateRaw(80, 2, sectors, 512, 1, 0x4E, 0xE5, "MZTools");

        Assert.Equal(DskFileSystemType.Raw, document.FileSystem.Type);
        Assert.Equal(expectedBytes, document.Image.TotalSectorDataBytes);
        Assert.Empty(document.FileSystem.Warnings);
    }

    [Fact]
    public void LemmingsFactoryCreatesItsSpecialPhysicalTrack()
    {
        DskDocument document = DskDocumentFactory.CreateLemmings();

        Assert.Equal(DskFileSystemType.Raw, document.FileSystem.Type);
        Assert.Equal(160, document.Image.Tracks.Count);
        Assert.Equal(16, document.Image.Tracks[1]!.Sectors.Count);
        Assert.All(document.Image.Tracks[1]!.Sectors, sector =>
        {
            Assert.Equal(256, sector.Data.Length);
            Assert.All(sector.Data, value => Assert.Equal(0xE5, value));
        });
        Assert.Equal([1, 6, 2, 7, 3, 8, 4, 9, 5, 21],
            document.Image.Tracks[16]!.Sectors.Select(sector => (int)sector.SectorId));
        Assert.Equal(10, document.Image.Tracks[16]!.Sectors.Count);
        Assert.Equal(9, document.Image.Tracks[17]!.Sectors.Count);
    }

    [Theory]
    [InlineData(0, "1,2,3,4,5,6,7,8,9")]
    [InlineData(1, "1,6,2,7,3,8,4,9,5")]
    [InlineData(2, "1,4,7,2,5,8,3,6,9")]
    public void CustomRawFactoryAppliesSelectedSectorOrder(int orderValue, string expectedText)
    {
        var order = (DskDocumentFactory.RawSectorOrder)orderValue;
        DskDocument document = DskDocumentFactory.CreateRaw(40, 1, 9, 512, 1, 0x4E, 0xA5, "test", order);
        int[] expected = expectedText.Split(',').Select(int.Parse).ToArray();

        Assert.Equal(DskFileSystemType.Raw, document.FileSystem.Type);
        Assert.Equal(expected, document.Image.Tracks[0]!.Sectors.Select(sector => (int)sector.SectorId));
        Assert.All(document.Image.Tracks.SelectMany(track => track!.Sectors), sector => Assert.All(sector.Data, value => Assert.Equal(0xA5, value)));
    }

    [Fact]
    public void CustomRawFactoryAcceptsExplicitSectorIdsAndAllSupportedSectorSizes()
    {
        foreach (int sectorSize in new[] { 128, 256, 512, 1024 })
        {
            DskDocument document = DskDocumentFactory.CreateRaw(
                10, 2, 3, sectorSize, 1, 0x4E, 0x00, "test",
                DskDocumentFactory.RawSectorOrder.Custom, [5, 1, 9]);

            Assert.Equal([5, 1, 9], document.Image.Tracks[0]!.Sectors.Select(sector => (int)sector.SectorId));
            Assert.All(document.Image.Tracks.SelectMany(track => track!.Sectors), sector => Assert.Equal(sectorSize, sector.Data.Length));
        }
    }

    [Fact]
    public void IplOnlyGeometryWithoutValidDinfo_IsNotMisdetectedAsFsmz()
    {
        DskImage image = Mz800DskImage.CreateModel("IPL test");
        var ipl = new byte[256];
        ipl[0] = 3;
        "IPLPRO"u8.CopyTo(ipl.AsSpan(1));
        Mz800DskImage.WriteLogicalBlock(image, 0, ipl);

        DskDocument document = DskDocument.Open(image.Serialize());

        Assert.Equal(DskFileSystemType.BootOnly, document.FileSystem.Type);
        Assert.DoesNotContain("DINFO", document.FileSystem.ReadDirectory().Select(entry => entry.Name));
    }

    [Fact]
    public void MultiGameIpl_IsPresentedAsGamesAndExtractsPayloads()
    {
        byte[] first = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();
        byte[] second = Enumerable.Range(0, 700).Select(value => (byte)(value * 7)).ToArray();
        MultiGameIplBuildResult built = Mz800MultiGameIplDskWriter.Build([
            new(CreateRecord(first, 0x2000, 0x2010), "FIRST GAME", new MzfCompressionOptions(MzfCompressionAlgorithm.None), first.Length),
            new(CreateRecord(second, 0x8000, 0x8123), "SECOND GAME", new MzfCompressionOptions(MzfCompressionAlgorithm.None), second.Length)
        ]);

        DskDocument document = DskDocument.Open(built.Image);
        IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();

        Assert.Equal(DskFileSystemType.MultiIpl, document.FileSystem.Type);
        Assert.Equal(2, entries.Count);
        Assert.Equal("FIRST GAME", entries[0].Name);
        Assert.Equal(0x2000, entries[0].LoadAddress);
        Assert.Equal(first, document.FileSystem.Extract(entries[0]));
        Assert.Equal(second, document.FileSystem.Extract(entries[1]));
    }

    [Fact]
    public void PersonalCpm80_CustomTrackOrderRoundTripsFiles()
    {
        DskImage image = DskImage.CreateSharpBootDataDisk(40, 2, 8, highDensityInterleave: false, "P-CP/M test");
        var boot = new byte[256];
        boot[0] = 3;
        "IPLPRO"u8.CopyTo(boot.AsSpan(1));
        SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("P-CP/M80").CopyTo(boot, 7);
        Mz800DskImage.WriteLogicalBlock(image, 0, boot);
        CpmFileSystem.Format(image, CpmDpb.PersonalCpm80);
        DskDocument document = DskDocument.Open(image.Serialize());
        byte[] data = Enumerable.Range(0, 3000).Select(value => (byte)(value * 13)).ToArray();

        Assert.Equal(DskFileSystemType.Cpm, document.FileSystem.Type);
        Assert.Equal("P-CP/M80 (MZ-2Z047)", document.FileSystem.DisplayName);
        document.FileSystem.Insert("HELLO.COM", data);
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry entry = Assert.Single(reopened.FileSystem.ReadDirectory());

        Assert.Equal(data, reopened.FileSystem.Extract(entry)[..data.Length]);
    }

    [Theory]
    [InlineData(false, "P-CP/M80 (MZ-2Z047)", 8, 327680)]
    [InlineData(true, "P-CP/M80 SDS 400K", 10, 408576)]
    public void PersonalCpmFactoriesCreateRecognizableWritableImages(
        bool sds400, string expectedName, int sectors, long expectedBytes)
    {
        DskDocument document = DskDocumentFactory.CreatePersonalCpm80(sds400);

        Assert.Equal(DskFileSystemType.Cpm, document.FileSystem.Type);
        Assert.Equal(expectedName, document.FileSystem.DisplayName);
        Assert.Equal(expectedBytes, document.Image.TotalSectorDataBytes);
        Assert.Equal(sectors, document.Image.Tracks[0]!.Sectors.Count);
        Assert.Empty(document.FileSystem.ReadDirectory());

        byte[] payload = Enumerable.Range(0, 1025).Select(value => (byte)(value * 23)).ToArray();
        document.FileSystem.Insert("TEST.COM", payload);
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry entry = Assert.Single(reopened.FileSystem.ReadDirectory());
        Assert.Equal(payload, reopened.FileSystem.Extract(entry)[..payload.Length]);
    }

    [Fact]
    public void SuppliedMultiGameFixture_ListsBothGames()
    {
        DskDocument document = DskDocument.Open(FixturePath("multi.dsk"));
        IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();

        Assert.Equal(DskFileSystemType.MultiIpl, document.FileSystem.Type);
        Assert.Equal(["FLAPPY ver 1.0A", "Highway Ver 1.02"], entries.Select(entry => entry.Name));
        Assert.Equal([24331L, 18143L], entries.Select(entry => entry.Size));
    }

    [Fact]
    public void SuppliedPersonalCpmFixture_ListsDirectory()
    {
        DskDocument document = DskDocument.Open(FixturePath("mz-2z047v10a.DSK"));
        IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();

        Assert.Equal(DskFileSystemType.Cpm, document.FileSystem.Type);
        Assert.Equal("P-CP/M80 (MZ-2Z047)", document.FileSystem.DisplayName);
        Assert.Equal(25, entries.Count);
        Assert.Contains(entries, entry => entry.Name == "ASM" && entry.Extension == "COM" && entry.Size == 8192);
    }

    [Fact]
    public void SuppliedSds400Fixture_UsesTwoSidedInvertedCpmLayout()
    {
        DskDocument document = DskDocument.Open(FixturePath("sds400.dsk"));
        IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();

        Assert.Equal(DskFileSystemType.Cpm, document.FileSystem.Type);
        Assert.Equal("P-CP/M80 SDS 400K", document.FileSystem.DisplayName);
        Assert.Equal(38, entries.Count);
        DskFileEntry building = Assert.Single(entries, entry => entry.Name == "BUILDING" && entry.Extension == "COM");
        Assert.Equal(41728, building.Size);
        Assert.Equal(41728, document.FileSystem.Extract(building).Length);

        byte[] payload = Enumerable.Range(0, 3000).Select(value => (byte)(value * 19)).ToArray();
        document.FileSystem.Insert("ZZTEST.BIN", payload);
        document.MarkModified();
        DskDocument reopened = DskDocument.Open(document.Serialize());
        DskFileEntry inserted = Assert.Single(reopened.FileSystem.ReadDirectory(), entry => entry.Name == "ZZTEST" && entry.Extension == "BIN");
        Assert.Equal(payload, reopened.FileSystem.Extract(inserted)[..payload.Length]);
    }

    [Fact]
    public void SuppliedSingleGameFixture_RemainsImportableAsIpl()
    {
        string path = FixturePath("FLAPPY ver 1.0A.dsk");
        DskDocument document = DskDocument.Open(path);
        Mz800IplDskReadResult imported = Mz800IplDskReader.ReadFile(path);

        Assert.Equal(DskFileSystemType.SingleIpl, document.FileSystem.Type);
        Assert.Equal("FLAPPY 1.0A", imported.BootName);
        Assert.Equal(44033, imported.Record.Body.MzfBody.Length);
    }

    [Fact]
    public void SuppliedRtk2Fixture_ExposesIplproAndRawSectors()
    {
        DskDocument document = DskDocument.Open(FixturePath("RTK2.DSK"));
        IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();

        Assert.Equal(DskFileSystemType.BootOnly, document.FileSystem.Type);
        DskFileEntry bootstrap = Assert.Single(entries, entry => entry.Key == "iplpro");
        Assert.Equal("KYRANDIA", bootstrap.Name);
        Assert.Equal(512, bootstrap.Size);
        Assert.Equal(512, document.FileSystem.Extract(bootstrap).Length);
        DskFileEntry dataArea = Assert.Single(entries, entry => entry.Key == "disk:data");
        byte[] decoded = document.FileSystem.Extract(dataArea);
        Assert.Equal(document.Image.TotalSectorDataBytes - 4096, decoded.Length);
        Assert.NotEqual([3, (byte)'I', (byte)'P', (byte)'L', (byte)'P', (byte)'R', (byte)'O'], decoded[..7]);
        Assert.Equal(1448, entries.Count);
    }

    private static string FixturePath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DSK", name));

    private static TapeRecord CreateRecord(byte[] data, ushort load, ushort execute)
    {
        var header = new MZQFileHeader
        {
            MzfFtype = 1,
            MzfFname = new byte[16],
            MzfSize = checked((ushort)data.Length),
            MzfStart = load,
            MzfExec = execute
        };
        var body = new MZQFileBody
        {
            DataSize = checked((ushort)data.Length),
            MzfBody = data,
            TrailingData = Array.Empty<byte>()
        };
        return new TapeRecord(new byte[TapeRecord.HeaderLength], header, body);
    }
}
