using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MZTools.Tests;

public class Mz800MultiGameIplDskWriterTests
{
    [Fact]
    public void Build_WritesMetadataTableAndPayloadsWithoutOverlap()
    {
        byte[] firstBody = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();
        byte[] secondBody = Enumerable.Range(0, 513).Select(value => (byte)(value * 7)).ToArray();
        TapeRecord first = CreateRecord(firstBody, "FIRST", 0x0000, 0x0040);
        TapeRecord second = CreateRecord(secondBody, "SECOND", 0x8000, 0x8123);

        MultiGameIplBuildResult result = Mz800MultiGameIplDskWriter.Build([
            Input(first, "First game"),
            Input(second, "Second game")
        ]);

        byte[] ipl = ReadLogicalBlock(result.Image, 0);
        Assert.Equal("MZTOOLS MULT", Encoding.ASCII.GetString(ipl, 0x07, 12));
        Assert.Equal(0x0D, ipl[0x13]);
        Assert.All(ipl[0x20..], value => Assert.Equal(0, value));

        byte[] menu = ReadLogicalBytes(result.Image, 1, result.MenuByteSize);
        int metadataOffset = Mz800MultiGameIplDskWriter.FindMenuFooterOffset(menu);
        Assert.Equal(menu.Length - Mz800MultiGameIplDskWriter.MultiGameFooterSize, metadataOffset);
        Assert.Equal("QDMG", Encoding.ASCII.GetString(menu, metadataOffset, 4));
        Assert.Equal(Mz800MultiGameIplDskWriter.FormatVersion, menu[metadataOffset + 4]);
        Assert.Equal(Mz800MultiGameIplDskWriter.EntrySize, menu[metadataOffset + 5]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(menu.AsSpan(metadataOffset + 6, 2)));
        Assert.Equal(result.MenuSectorCount, BinaryPrimitives.ReadUInt16LittleEndian(menu.AsSpan(metadataOffset + 10, 2)));

        Assert.Equal(1 + result.MenuSectorCount, result.Entries[0].StartBlock);
        Assert.Equal(result.Entries[0].StartBlock + 2, result.Entries[1].StartBlock);
        Assert.Equal(3, result.Entries[1].SectorCount);
        AssertPayload(result.Image, result.Entries[0], firstBody);
        AssertPayload(result.Image, result.Entries[1], secondBody);

        int tableOffset = BinaryPrimitives.ReadUInt16LittleEndian(menu.AsSpan(metadataOffset + 8, 2));
        Assert.True(tableOffset + result.Entries.Count * Mz800MultiGameIplDskWriter.EntrySize <= metadataOffset);
        AssertTableEntry(menu.AsSpan(tableOffset, 26), result.Entries[0]);
        AssertTableEntry(menu.AsSpan(tableOffset + 26, 26), result.Entries[1]);
    }

    [Fact]
    public void Reader_RecognizesValidatedMetadataInIplComment()
    {
        MultiGameIplBuildResult result = Mz800MultiGameIplDskWriter.Build([
            Input(CreateRecord(new byte[300], "OBSOLETE", 0x2000, 0x2010), "Obsolete")
        ]);
        byte[] image = (byte[])result.Image.Clone();
        byte[] ipl = ReadLogicalBlock(image, 0);
        byte[] menu = ReadLogicalBytes(image, 1, result.MenuByteSize);
        int footerOffset = Mz800MultiGameIplDskWriter.FindMenuFooterOffset(menu);
        menu.AsSpan(footerOffset, Mz800MultiGameIplDskWriter.MultiGameFooterSize).CopyTo(ipl.AsSpan(0x20));
        menu.AsSpan(footerOffset, Mz800MultiGameIplDskWriter.MultiGameFooterSize).Clear();
        WriteLogicalBlock(image, 0, ipl);
        WriteLogicalBytes(image, 1, menu);

        DskDocument document = DskDocument.Open(image);

        var fileSystem = Assert.IsType<MultiGameIplFileSystem>(document.FileSystem);
        Assert.Equal(MultiGameMetadataLayout.IplProComment, fileSystem.Metadata.Layout);
        Assert.True(fileSystem.CanConfigure);
        Assert.Equal(new byte[300], fileSystem.Extract(Assert.Single(fileSystem.ReadDirectory())));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(255, 1)]
    [InlineData(256, 1)]
    [InlineData(257, 2)]
    [InlineData(511, 2)]
    [InlineData(512, 2)]
    [InlineData(513, 3)]
    public void Build_UsesCeilingSectorCount(int size, int expectedSectors)
    {
        MultiGameIplBuildResult result = Mz800MultiGameIplDskWriter.Build([
            Input(CreateRecord(new byte[size], "SIZE", 0, 0), "Size")
        ]);

        Assert.Equal(expectedSectors, result.Entries[0].SectorCount);
    }

    [Fact]
    public void Build_IsDeterministicAndDoesNotMutateSources()
    {
        TapeRecord first = CreateRecord(Enumerable.Repeat((byte)0x33, 300).ToArray(), "ONE", 0x4000, 0x4010);
        TapeRecord second = CreateRecord(Enumerable.Repeat((byte)0x77, 600).ToArray(), "TWO", 0x1000, 0x1010);
        byte[] firstBefore = TapeDocumentWriter.SerializeMzf(first, preserveTrailing: true);
        byte[] secondBefore = TapeDocumentWriter.SerializeMzf(second, preserveTrailing: true);
        MultiGameIplInput[] inputs = [Input(first, "One"), Input(second, "Two")];

        byte[] one = Mz800MultiGameIplDskWriter.Build(inputs).Image;
        byte[] two = Mz800MultiGameIplDskWriter.Build(inputs).Image;

        Assert.Equal(one, two);
        Assert.Equal(firstBefore, TapeDocumentWriter.SerializeMzf(first, preserveTrailing: true));
        Assert.Equal(secondBefore, TapeDocumentWriter.SerializeMzf(second, preserveTrailing: true));
    }

    [Fact]
    public void Build_RejectsCapacityOverflow()
    {
        MultiGameIplInput[] inputs = Enumerable.Range(0, 7)
            .Select(index => Input(
                CreateRecord(new byte[Mz800IplDskWriter.MaxIplStagedSize], $"GAME{index}", 0, 0),
                $"Game {index}"))
            .ToArray();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Mz800MultiGameIplDskWriter.Build(inputs));

        Assert.Contains("sectors are required", exception.Message);
        Assert.Contains(Mz800DskImage.LogicalSectorCount.ToString(), exception.Message);
    }

    [Fact]
    public void Build_AcceptsExactCapacityAndRejectsOneAdditionalSector()
    {
        const int entryCount = 7;
        MultiGameIplInput[] probes = Enumerable.Range(0, entryCount)
            .Select(index => Input(CreateRecord([0], $"GAME{index}", 0, 0), $"Game {index}"))
            .ToArray();
        int menuSectors = Mz800MultiGameIplDskWriter.Build(probes).MenuSectorCount;
        int remaining = Mz800DskImage.LogicalSectorCount - 1 - menuSectors;
        var sectorCounts = new int[entryCount];
        for (int index = 0; index < sectorCounts.Length; index++)
        {
            sectorCounts[index] = Math.Min(189, remaining);
            remaining -= sectorCounts[index];
        }
        Assert.Equal(0, remaining);
        int adjustable = Array.FindIndex(sectorCounts, value => value < 189);
        Assert.True(adjustable >= 0);

        MultiGameIplInput[] exact = sectorCounts
            .Select((sectors, index) => Input(
                CreateRecord(new byte[(sectors - 1) * 256 + 1], $"GAME{index}", 0, 0),
                $"Game {index}"))
            .ToArray();
        MultiGameIplBuildResult exactResult = Mz800MultiGameIplDskWriter.Build(exact);
        Assert.Equal(Mz800DskImage.LogicalSectorCount, exactResult.UsedSectorCount);
        Assert.Equal(0, exactResult.FreeSectorCount);

        TapeRecord oneSectorLarger = CreateRecord(
            new byte[sectorCounts[adjustable] * 256 + 1],
            $"GAME{adjustable}",
            0,
            0);
        exact[adjustable] = Input(oneSectorLarger, $"Game {adjustable}");
        Assert.Throws<InvalidOperationException>(() => Mz800MultiGameIplDskWriter.Build(exact));
    }

    [Fact]
    public void Build_PreservesDifferentLoadAndExecCombinationsIncludingBelowStaging()
    {
        MultiGameIplBuildResult result = Mz800MultiGameIplDskWriter.Build([
            Input(CreateRecord(new byte[10], "AT1200", 0x1200, 0x1382), "At 1200"),
            Input(CreateRecord(new byte[20], "ABOVE", 0x1E00, 0x1E00), "Above"),
            Input(CreateRecord(new byte[30], "BELOW", 0x0800, 0x0900), "Below")
        ]);

        Assert.Collection(
            result.Entries,
            entry => { Assert.Equal(0x1200, entry.Load); Assert.Equal(0x1382, entry.Exec); },
            entry => { Assert.Equal(0x1E00, entry.Load); Assert.Equal(0x1E00, entry.Exec); },
            entry => { Assert.Equal(0x0800, entry.Load); Assert.Equal(0x0900, entry.Exec); });
    }

    [Fact]
    public void Build_RejectsInconsistentAndOverflowingEntries()
    {
        TapeRecord empty = CreateRecord([], "EMPTY", 0, 0);
        InvalidOperationException emptyError = Assert.Throws<InvalidOperationException>(() =>
            Mz800MultiGameIplDskWriter.Build([Input(empty, "Empty")]));
        Assert.Contains("empty", emptyError.Message);

        TapeRecord inconsistent = CreateRecord([1, 2, 3], "BAD", 0, 0);
        MZQFileHeader inconsistentHeader = inconsistent.Header;
        inconsistentHeader.MzfSize = 2;
        inconsistent.Header = inconsistentHeader;
        InvalidOperationException sizeError = Assert.Throws<InvalidOperationException>(() =>
            Mz800MultiGameIplDskWriter.Build([Input(inconsistent, "Bad size")]));
        Assert.Contains("inconsistent", sizeError.Message);

        TapeRecord overflow = CreateRecord(new byte[257], "OVER", 0xFF00, 0xFF00);
        InvalidOperationException addressError = Assert.Throws<InvalidOperationException>(() =>
            Mz800MultiGameIplDskWriter.Build([Input(overflow, "Bad address")]));
        Assert.Contains("16-bit", addressError.Message);

        TapeRecord oversized = CreateRecord(
            new byte[Mz800IplDskWriter.MaxIplStagedSize + 1],
            "LARGE",
            0,
            0);
        InvalidOperationException stagingError = Assert.Throws<InvalidOperationException>(() =>
            Mz800MultiGameIplDskWriter.Build([Input(oversized, "Too large")]));
        Assert.Contains("staging limit", stagingError.Message);
    }

    [Fact]
    public void Build_RecordsResolvedCompressionForEveryEntry()
    {
        TapeRecord source = CreateRecord(Enumerable.Repeat((byte)0xA5, 2048).ToArray(), "PACK", 0x4000, 0x4100);
        MzfCompressionOptions[] choices = [
            new(MzfCompressionAlgorithm.None),
            new(MzfCompressionAlgorithm.Zx0),
            new(MzfCompressionAlgorithm.Zx7),
            new(MzfCompressionAlgorithm.Auto)
        ];
        MzfCompressionResult[] prepared = choices
            .Select(choice => MzfCompressionService.Compress(source, choice, CompressionTarget.IplDsk))
            .ToArray();

        MultiGameIplBuildResult result = Mz800MultiGameIplDskWriter.Build(prepared
            .Select((value, index) => new MultiGameIplInput(
                value.Record,
                $"Choice {index}",
                value.AppliedOptions,
                value.OriginalSize))
            .ToArray());

        Assert.Equal(0, result.Entries[0].CompressionCode);
        Assert.Equal(1, result.Entries[1].CompressionCode);
        Assert.Equal(2, result.Entries[2].CompressionCode);
        Assert.DoesNotContain(result.Entries, entry => entry.CompressionCode > 2);
        Assert.All(result.Entries.Skip(1), entry => Assert.Equal(1, entry.Flags));
    }

    [Fact]
    public void DialogRow_ShowsPackedRatioAndPerEntryMaximum()
    {
        TapeRecord source = CreateRecord(new byte[1000], "RATIO", 0x2000, 0x2000);
        var row = new MultiGameIplRow(1, source, "Ratio");
        var prepared = new PreparedMultiGameIplEntry(
            "Ratio",
            3,
            400,
            0x3000,
            0x3010,
            2,
            1,
            1,
            "ZX0 forward",
            1000,
            new byte[400]);

        row.Apply(prepared);

        Assert.Equal(400, row.PackedSize);
        Assert.Equal("40.0%", row.CompressionRatio);
        Assert.Equal(Mz800IplDskWriter.MaxIplStagedSize, row.MaxPayloadSize);
    }

    [Theory]
    [InlineData(2, 0, "ZX0")]
    [InlineData(2, 1, "ZX0")]
    [InlineData(3, 0, "ZX7")]
    [InlineData(3, 1, "ZX7")]
    public async Task DialogRow_DetectsImportedCompressionAndAvoidsDoubleCompression(
        int algorithmValue,
        int directionValue,
        string expectedChoice)
    {
        var algorithm = (MzfCompressionAlgorithm)algorithmValue;
        var direction = (CompressionDirection)directionValue;
        byte[] body = Enumerable.Range(0, 2048).Select(value => (byte)(value & 31)).ToArray();
        TapeRecord source = CreateRecord(body, "DETECT", 0x4000, 0x4100);
        MzfCompressionResult packed = MzfCompressionService.Compress(
            source,
            new MzfCompressionOptions(algorithm, direction),
            CompressionTarget.MzfTape);

        var row = new MultiGameIplRow(1, packed.Record, "Detect");
        MzfCompressionResult unchanged = await row.PrepareAsync(CancellationToken.None);

        Assert.Equal(expectedChoice, row.Compression);
        Assert.Equal(body.Length, row.OriginalSize);
        Assert.Equal(packed.Record.Body.MzfBody.Length, row.PackedSize);
        Assert.Equal(packed.Record.Body.MzfBody, unchanged.Record.Body.MzfBody);
        Assert.Equal(direction, unchanged.AppliedOptions.Direction);
        Assert.Equal(
            TapeDocumentWriter.SerializeMzf(packed.Record, preserveTrailing: false),
            TapeDocumentWriter.SerializeMzf(row.GetPreparedRecord(), preserveTrailing: false));

        row.Compression = "None";
        MzfCompressionResult restored = await row.PrepareAsync(CancellationToken.None);
        Assert.Equal(body, restored.Record.Body.MzfBody);
        Assert.Equal(body, row.GetPreparedRecord().Body.MzfBody);
        Assert.Equal(0x4000, restored.Record.Header.MzfStart);
        Assert.Equal(0x4100, restored.Record.Header.MzfExec);
    }

    [Fact]
    public async Task DialogRow_ConvertsEmbeddedZx7ToIplCompatibleZx7()
    {
        byte[] body = Enumerable.Range(0, 2048).Select(value => (byte)(value & 15)).ToArray();
        TapeRecord source = CreateRecord(body, "EMBEDDED", 0x4000, 0x4100);
        TapeRecord embedded = MzfCompressionService.Compress(
            source,
            new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7, Zx7EmbeddedLoader: true),
            CompressionTarget.MzfTape).Record;

        var row = new MultiGameIplRow(1, embedded, "Embedded");
        MzfCompressionResult prepared = await row.PrepareAsync(CancellationToken.None);

        Assert.Equal("ZX7", row.Compression);
        Assert.Equal(body.Length, row.OriginalSize);
        Assert.True(MzfLoaderBuilder.TryGetCompressionInfo(prepared.Record, out MzfCompressionInfo? info));
        Assert.NotNull(info);
        Assert.False(info.EmbeddedLoader);
    }

    [Fact]
    public void Reader_IdentifiesMultiGameImageInsteadOfTreatingItAsSingleProgram()
    {
        byte[] image = Mz800MultiGameIplDskWriter.Build([
            Input(CreateRecord([1], "ONE", 0x1200, 0x1200), "One"),
            Input(CreateRecord([2], "TWO", 0x1300, 0x1300), "Two")
        ]).Image;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Mz800IplDskReader.Read(image));

        Assert.Contains("MZTools multi-game IPL DSK version 1", exception.Message);
    }

    [Fact]
    public void FinalStub_UsesRomBootTemplateAndControllerInitializationSequence()
    {
        MultiGameIplBuildResult result = Mz800MultiGameIplDskWriter.Build([
            Input(CreateRecord([1], "ONE", 0x1200, 0x1200), "One")
        ]);
        byte[] menu = ReadLogicalBytes(result.Image, 1, result.MenuByteSize);

        Assert.True(ContainsSequence(menu, [0xF3, 0xCD, 0xD5, 0xE8, 0xC2]));
        Assert.True(ContainsSequence(menu,
            [0x21, 0xD1, 0xE4, 0x11, 0xE9, 0xCE, 0x01, 0x0B, 0x00, 0xED, 0xB0]));
        Assert.True(ContainsSequence(menu, [0xCD, 0x30, 0xE5, 0xDD, 0x21, 0xE9, 0xCE, 0xCD, 0xA7, 0xE5]));
    }

    [Fact]
    public void Supplied9zRomMatchesLoaderContract()
    {
        byte[] rom = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ROM", "9Z_504M.ROM"));

        Assert.Equal(8192, rom.Length);
        Assert.Equal(
            "68A91B82517D5642E250CDDDB519DE780FB20BCDE39B2DE8BAEE387CA6BF446A",
            Convert.ToHexString(SHA256.HashData(rom)));
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0xCF, 0x00, 0x00, 0x00, 0x00 },
            rom[0x4D1..0x4DC]); // BOOT parameter template
        Assert.Equal(new byte[] { 0x3E, 0xA5, 0x47, 0xD3 }, rom[0x8D5..0x8D9]); // FDCC&
        Assert.Equal(new byte[] { 0xCD, 0x96, 0xE6, 0xCD }, rom[0x5A7..0x5AB]); // FDREAD
        Assert.Equal(new byte[] { 0xDB, 0xCE, 0xCB, 0x4F }, rom[0xCFC..0xD00]); // JGOPGM
    }

    private static MultiGameIplInput Input(TapeRecord record, string name) =>
        new(record, name, new MzfCompressionOptions(MzfCompressionAlgorithm.None), record.Body.MzfBody.Length);

    private static TapeRecord CreateRecord(byte[] body, string name, ushort load, ushort exec) =>
        CompressionTestData.ReadRecord(CompressionTestData.CreateMzf(
            body,
            name: name,
            load: load,
            exec: exec));

    private static void AssertPayload(byte[] image, PreparedMultiGameIplEntry entry, byte[] expected)
    {
        byte[] actual = ReadLogicalBytes(image, entry.StartBlock, expected.Length);
        Assert.Equal(expected, actual);
        byte[] lastSector = ReadLogicalBlock(image, entry.StartBlock + entry.SectorCount - 1);
        int usedInLast = expected.Length - (entry.SectorCount - 1) * 256;
        Assert.All(lastSector[usedInLast..], value => Assert.Equal(0, value));
    }

    private static void AssertTableEntry(ReadOnlySpan<byte> table, PreparedMultiGameIplEntry expected)
    {
        Assert.Equal(expected.StartBlock, BinaryPrimitives.ReadUInt16LittleEndian(table[16..18]));
        Assert.Equal(expected.Size, BinaryPrimitives.ReadUInt16LittleEndian(table[18..20]));
        Assert.Equal(expected.Load, BinaryPrimitives.ReadUInt16LittleEndian(table[20..22]));
        Assert.Equal(expected.Exec, BinaryPrimitives.ReadUInt16LittleEndian(table[22..24]));
        Assert.Equal(expected.Flags, table[24]);
        Assert.Equal(expected.CompressionCode, table[25]);
    }

    private static byte[] ReadLogicalBytes(byte[] image, int startBlock, int size)
    {
        var result = new byte[size];
        for (int offset = 0; offset < size; offset += 256)
        {
            byte[] sector = ReadLogicalBlock(image, startBlock + offset / 256);
            Array.Copy(sector, 0, result, offset, Math.Min(256, size - offset));
        }
        return result;
    }

    private static byte[] ReadLogicalBlock(byte[] image, int block)
    {
        (int cylinder, int side, int sector) = Mz800DskImage.MapLogicalBlock(block);
        int physicalTrack = cylinder * 2 + side;
        int offset = 0x100 + physicalTrack * 0x1100 + 0x100 + (sector - 1) * 256;
        return image[offset..(offset + 256)].Select(value => (byte)(value ^ 0xFF)).ToArray();
    }

    private static void WriteLogicalBlock(byte[] image, int block, ReadOnlySpan<byte> logical)
    {
        (int cylinder, int side, int sector) = Mz800DskImage.MapLogicalBlock(block);
        int physicalTrack = cylinder * 2 + side;
        int offset = 0x100 + physicalTrack * 0x1100 + 0x100 + (sector - 1) * 256;
        for (int index = 0; index < logical.Length; index++) image[offset + index] = (byte)(logical[index] ^ 0xFF);
    }

    private static void WriteLogicalBytes(byte[] image, int startBlock, ReadOnlySpan<byte> logical)
    {
        for (int offset = 0; offset < logical.Length; offset += Mz800DskImage.SectorSize)
        {
            var block = new byte[Mz800DskImage.SectorSize];
            logical.Slice(offset, Math.Min(block.Length, logical.Length - offset)).CopyTo(block);
            WriteLogicalBlock(image, startBlock + offset / block.Length, block);
        }
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> source, ReadOnlySpan<byte> sequence)
    {
        for (int index = 0; index <= source.Length - sequence.Length; index++)
        {
            if (source.Slice(index, sequence.Length).SequenceEqual(sequence))
            {
                return true;
            }
        }
        return false;
    }

}
