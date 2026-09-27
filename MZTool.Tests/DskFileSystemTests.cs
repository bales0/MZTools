namespace MZTools.Tests;

using System.Buffers.Binary;
using NAudio.SoundFile;

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

    [Fact]
    public void Fsmz_RenameCollisionLeavesDirectoryUnchanged()
    {
        DskDocument document = DskDocumentFactory.CreateFsmz();
        document.FileSystem.Insert("FIRST", [1, 2, 3]);
        document.FileSystem.Insert("SECOND", [4, 5, 6]);
        DskFileEntry source = document.FileSystem.ReadDirectory().Single(entry => entry.Name == "FIRST");
        byte[] before = document.Serialize();

        Assert.Throws<IOException>(() => document.FileSystem.Rename(source, "SECOND"));

        Assert.Equal(before, document.Serialize());
        Assert.Equal(["FIRST", "SECOND"], document.FileSystem.ReadDirectory()
            .Select(entry => entry.Name).Order().ToArray());
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

    [Fact]
    public void Cpm_RenameCollisionLeavesEveryExtentAndPayloadUnchanged()
    {
        DskDocument document = DskDocumentFactory.CreateCpm(highDensity: false);
        byte[] first = Enumerable.Range(0, 40000).Select(index => (byte)(index * 17)).ToArray();
        byte[] second = Enumerable.Range(0, 900).Select(index => (byte)(index * 29)).ToArray();
        document.FileSystem.Insert("FIRST.BIN", first, user: 3);
        document.FileSystem.Insert("SECOND.BIN", second, user: 3);
        DskFileEntry source = document.FileSystem.ReadDirectory().Single(entry => entry.Name == "FIRST");
        byte[] before = document.Serialize();

        IOException exception = Assert.Throws<IOException>(
            () => document.FileSystem.Rename(source, "SECOND.BIN"));

        Assert.Contains("already exists", exception.Message);
        Assert.Equal(before, document.Serialize());
        IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();
        Assert.Equal(2, entries.Count);
        Assert.True(source.Extents > 1);
        Assert.Equal(first, document.FileSystem.Extract(entries.Single(entry => entry.Name == "FIRST"))[..first.Length]);
        Assert.Equal(second, document.FileSystem.Extract(entries.Single(entry => entry.Name == "SECOND"))[..second.Length]);
    }

    [Fact]
    public void Cpm_RenameAllowsSameNameInAnotherUserArea()
    {
        DskDocument document = DskDocumentFactory.CreateCpm(highDensity: false);
        document.FileSystem.Insert("FIRST.COM", [1, 2, 3], user: 0);
        document.FileSystem.Insert("TARGET.COM", [4, 5, 6], user: 1);
        DskFileEntry source = document.FileSystem.ReadDirectory().Single(entry => entry.User == 0);

        document.FileSystem.Rename(source, "TARGET.COM");

        DskFileEntry[] entries = document.FileSystem.ReadDirectory().OrderBy(entry => entry.User).ToArray();
        Assert.Equal([0, 1], entries.Select(entry => entry.User));
        Assert.All(entries, entry => Assert.Equal("TARGET", entry.Name));
        Assert.Equal(new byte[] { 1, 2, 3 }, document.FileSystem.Extract(entries[0])[..3]);
        Assert.Equal(new byte[] { 4, 5, 6 }, document.FileSystem.Extract(entries[1])[..3]);
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
    public void ImportBootSystemAreaUsesDpbOffAndLeavesDataAreaUntouched()
    {
        DskDocument source = DskDocumentFactory.CreateCpm(highDensity: false);
        DskDocument target = DskDocumentFactory.CreateCpm(highDensity: false);
        byte[][] originalFirstDataTrack = target.Image.Tracks[4]!.Sectors
            .Select(sector => (byte[])sector.Data.Clone()).ToArray();
        for (int trackIndex = 0; trackIndex < 4; trackIndex++)
        {
            foreach (DskImage.DskSector sector in source.Image.Tracks[trackIndex]!.Sectors)
            {
                Array.Fill(sector.Data, (byte)(0x20 + trackIndex));
            }
        }

        DskDocumentFactory.ImportBootSystemArea(target, source);

        Assert.True(target.IsModified);
        for (int trackIndex = 0; trackIndex < 4; trackIndex++)
        {
            Assert.All(target.Image.Tracks[trackIndex]!.Sectors.SelectMany(sector => sector.Data),
                value => Assert.Equal((byte)(0x20 + trackIndex), value));
        }
        Assert.Equal(originalFirstDataTrack, target.Image.Tracks[4]!.Sectors.Select(sector => sector.Data).ToArray());
    }

    [Fact]
    public void ImportBootSystemAreaRejectsMismatchedDataGeometry()
    {
        DskDocument source = DskDocumentFactory.CreateCpm(highDensity: false);
        DskDocument target = DskDocumentFactory.CreateCpm(highDensity: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => DskDocumentFactory.ImportBootSystemArea(target, source));

        Assert.Contains("data geometry differs", exception.Message);
        Assert.False(target.IsModified);
    }

    [Fact]
    public void ClearBootTrackRemovesGeneratedPersonalCpmBootstrap()
    {
        DskDocument document = DskDocumentFactory.CreatePersonalCpm80(sds400: false);
        Assert.Contains(document.Image.Tracks[1]!.Sectors.SelectMany(sector => sector.Data), value => value != 0xFF);

        DskDocumentFactory.ClearBootTrack(document);

        Assert.True(document.IsModified);
        Assert.All(document.Image.Tracks[1]!.Sectors.SelectMany(sector => sector.Data), value => Assert.Equal(0xFF, value));
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

    [Fact]
    public void Mrs_InsertAndRenameCompareTheCompleteEightDotThreeName()
    {
        DskDocument document = DskDocumentFactory.CreateMrs();
        document.FileSystem.Insert("GAME.BIN", [1, 2, 3]);
        document.FileSystem.Insert("GAME.COM", [4, 5, 6]);
        Assert.Throws<IOException>(() => document.FileSystem.Insert("GAME.BIN", [7]));
        DskFileEntry source = document.FileSystem.ReadDirectory().Single(entry => entry.Extension == "COM");
        byte[] before = document.Serialize();

        IOException exception = Assert.Throws<IOException>(
            () => document.FileSystem.Rename(source, "GAME.BIN"));

        Assert.Contains("already exists", exception.Message);
        Assert.Equal(before, document.Serialize());
        Assert.Equal(["BIN", "COM"], document.FileSystem.ReadDirectory()
            .Select(entry => entry.Extension).Order().ToArray());
    }

    [Fact]
    public void Mrs_RenameAllowsSameBaseNameWithDifferentExtension()
    {
        DskDocument document = DskDocumentFactory.CreateMrs();
        document.FileSystem.Insert("GAME.BIN", [1]);
        document.FileSystem.Insert("OTHER.COM", [2]);
        DskFileEntry source = document.FileSystem.ReadDirectory().Single(entry => entry.Name == "OTHER");

        document.FileSystem.Rename(source, "GAME.COM");

        Assert.Equal(["GAME.BIN", "GAME.COM"], document.FileSystem.ReadDirectory()
            .Select(entry => $"{entry.Name}.{entry.Extension}").Order().ToArray());
    }

    [Fact]
    public void DskExportPolicyRejectsSyntheticCpmMrsAndMultiIplMzf()
    {
        var entry = new DskFileEntry { Key = "1", Name = "GAME", Extension = "COM" };

        Assert.False(DskExportSupport.CanExportMzf(DskFileSystemType.Cpm, entry));
        Assert.False(DskExportSupport.CanExportMzf(DskFileSystemType.Mrs, entry));
        Assert.False(DskExportSupport.CanExportMzf(DskFileSystemType.MultiIpl, entry));
        Assert.True(DskExportSupport.CanExportMzf(DskFileSystemType.Fsmz, entry));
    }

    [Fact]
    public void DskBatchExportNamesSeparateCpmUsersAndCaseInsensitiveCollisions()
    {
        DskFileEntry[] entries =
        [
            new() { Key = "0:A.COM", User = 0, Name = "GAME", Extension = "COM" },
            new() { Key = "1:A.COM", User = 1, Name = "game", Extension = "com" },
            new() { Key = "2:A.COM", User = 1, Name = "U00_GAME", Extension = "COM" }
        ];

        IReadOnlyList<string> names = DskExportSupport.BuildBatchFileNames(
            entries, DskFileSystemType.Cpm, exportMzf: false);

        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("U00_GAME.COM", names[0]);
        Assert.Equal("U01_game.com", names[1]);
        Assert.Equal("U00_GAME_2.COM", names[2]);
    }

    [Fact]
    public void DskMzfSerializationAcceptsMaximumAndRejectsOverflowWithoutTruncation()
    {
        var entry = new DskFileEntry
        {
            Key = "1",
            Name = "MAXIMUM",
            FileType = 0,
            LoadAddress = 0x1200,
            ExecuteAddress = 0x1234
        };
        byte[] maximum = Enumerable.Range(0, ushort.MaxValue).Select(index => (byte)index).ToArray();

        byte[] mzf = DskMzfConverter.Serialize(entry, maximum);

        Assert.Equal(128 + ushort.MaxValue, mzf.Length);
        Assert.Equal(0, mzf[0]);
        Assert.Equal(ushort.MaxValue, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(mzf.AsSpan(18, 2)));
        Assert.Equal(maximum, mzf[128..]);
        Assert.Throws<InvalidDataException>(() => DskMzfConverter.Serialize(entry, new byte[ushort.MaxValue + 1]));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(1, 16)]
    [InlineData(1, 24)]
    [InlineData(2, 8)]
    [InlineData(2, 16)]
    [InlineData(2, 24)]
    public void WavStreamingReader_ReadsAndSeeksNative48Khz(ushort channels, ushort bits)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, CreatePcmWav(channels, bits, 48000, 32));
            using var reader = new WavPcmStreamReader(path);
            var indices = new List<long>();
            reader.ReadFrames(7, 9, (index, _, _) => indices.Add(index));

            Assert.Equal((uint)48000, reader.Format.SampleRate);
            Assert.Equal(channels, reader.Format.Channels);
            Assert.Equal(bits, reader.Format.BitsPerSample);
            Assert.Equal(Enumerable.Range(7, 9).Select(value => (long)value), indices);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FlacStreamingReader_AcceptsNative48KhzWithoutRelabeling()
    {
        string wavPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        string flacPath = Path.ChangeExtension(wavPath, ".flac");
        try
        {
            File.WriteAllBytes(wavPath, CreatePcmWav(2, 16, 48000, 32));
            using (var source = new SoundFileReader(wavPath))
            {
                SoundFileWriter.CreateSoundFile(flacPath, source);
            }
            using var reader = new FlacPcmStreamReader(flacPath);
            long frames = 0;
            reader.ReadFrames((_, _, _) => frames++);

            Assert.Equal((uint)48000, reader.Format.SampleRate);
            Assert.Equal(2, reader.Format.Channels);
            Assert.Equal(32, frames);
        }
        finally
        {
            File.Delete(wavPath);
            File.Delete(flacPath);
        }
    }

    [Fact]
    public void WavExportSupports22050Keeps44100DefaultAndRejectsOtherRates()
    {
        string defaultPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        string lowPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        string invalidPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.wav");
        TapeRecord record = CreateRecord([1, 2, 3, 4], 0x1200, 0x1234);
        try
        {
            SharpTapeExporter.Export(defaultPath, [record], SharpTapeOutputFormat.Wav);
            SharpTapeExporter.Export(
                lowPath,
                [record],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800,
                SharpTapeExporter.WavLowSampleRate);

            byte[] normal = File.ReadAllBytes(defaultPath);
            byte[] low = File.ReadAllBytes(lowPath);
            Assert.Equal((uint)44100, BinaryPrimitives.ReadUInt32LittleEndian(normal.AsSpan(24, 4)));
            Assert.Equal((uint)22050, BinaryPrimitives.ReadUInt32LittleEndian(low.AsSpan(24, 4)));
            Assert.Equal((uint)22050, BinaryPrimitives.ReadUInt32LittleEndian(low.AsSpan(28, 4)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(low.AsSpan(32, 2)));
            Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(low.AsSpan(34, 2)));
            double normalSeconds = BinaryPrimitives.ReadUInt32LittleEndian(normal.AsSpan(40, 4)) / 44100.0;
            double lowSeconds = BinaryPrimitives.ReadUInt32LittleEndian(low.AsSpan(40, 4)) / 22050.0;
            Assert.InRange(Math.Abs(normalSeconds - lowSeconds), 0, 1.0 / 22050);

            Assert.Throws<ArgumentOutOfRangeException>(() => SharpTapeExporter.Export(
                invalidPath,
                [record],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800,
                48000));
            Assert.False(File.Exists(invalidPath));

            WavHeuristicAnalysisResult decoded = WavHeuristicAnalyzer.AnalyzeFile(lowPath);
            Assert.Equal(record.Body.MzfBody, Assert.Single(decoded.Records).Body.MzfBody);
        }
        finally
        {
            File.Delete(defaultPath);
            File.Delete(lowPath);
            File.Delete(invalidPath);
        }
    }

    [Fact]
    public void WavAnalyzer_DecodesEquivalent44100And48000Recordings()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-44100.wav");
        string convertedPath = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-48000.wav");
        TapeRecord expected = CreateRecord([9, 8, 7, 6], 0x2000, 0x2010);
        try
        {
            SharpTapeExporter.Export(sourcePath, [expected], SharpTapeOutputFormat.Wav);
            File.WriteAllBytes(convertedPath, Resample8BitMonoWav(File.ReadAllBytes(sourcePath), 48000));

            WavHeuristicAnalysisResult source = WavHeuristicAnalyzer.AnalyzeFile(sourcePath);
            WavHeuristicAnalysisResult converted = WavHeuristicAnalyzer.AnalyzeFile(convertedPath);

            Assert.Equal((uint)44100, source.Statistics.Format.SampleRate);
            Assert.Equal((uint)48000, converted.Statistics.Format.SampleRate);
            Assert.Equal(expected.Body.MzfBody, Assert.Single(source.Records).Body.MzfBody);
            Assert.Equal(expected.Body.MzfBody, Assert.Single(converted.Records).Body.MzfBody);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(convertedPath);
        }
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
    public void MultiGameIpl_SaveAndReopenPreservesProgramItemsAndPayloads()
    {
        byte[] first = Enumerable.Range(0, 321).Select(value => (byte)(value * 5)).ToArray();
        byte[] second = Enumerable.Range(0, 654).Select(value => (byte)(value * 9)).ToArray();
        MultiGameIplBuildResult built = Mz800MultiGameIplDskWriter.Build([
            new(CreateRecord(first, 0x2100, 0x2110), "FIRST", new MzfCompressionOptions(MzfCompressionAlgorithm.None), first.Length),
            new(CreateRecord(second, 0x4200, 0x4321), "SECOND", new MzfCompressionOptions(MzfCompressionAlgorithm.None), second.Length)
        ]);
        string path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}.dsk");

        try
        {
            DskDocument document = DskDocument.Open(built.Image);
            document.Save(path);
            DskDocument reopened = DskDocument.Open(path);
            IReadOnlyList<DskFileEntry> entries = reopened.FileSystem.ReadDirectory();

            Assert.Equal(DskFileSystemType.MultiIpl, reopened.FileSystem.Type);
            Assert.Collection(entries,
                entry => Assert.Equal("FIRST", entry.Name),
                entry => Assert.Equal("SECOND", entry.Name));
            Assert.Equal(first, reopened.FileSystem.Extract(entries[0]));
            Assert.Equal(second, reopened.FileSystem.Extract(entries[1]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SingleGameIpl_ConfigurationRecordCanBeRecompressedAndReopened()
    {
        byte[] body = Enumerable.Range(0, 2048).Select(value => (byte)(value & 31)).ToArray();
        TapeRecord source = CreateRecord(body, 0x4000, 0x4100);
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0, CompressionDirection.Backward),
            CompressionTarget.IplDsk).Record;
        DskDocument original = DskDocument.Open(Mz800IplDskWriter.Build(packed, "SINGLE TEST"));
        SingleGameIplFileSystem fileSystem = Assert.IsType<SingleGameIplFileSystem>(original.FileSystem);
        var row = new MultiGameIplRow(1, fileSystem.GetRecord(), fileSystem.BootName)
        {
            Compression = "None"
        };

        MzfCompressionResult prepared = await row.PrepareAsync(CancellationToken.None);
        DskDocument rebuilt = DskDocument.Open(Mz800IplDskWriter.Build(prepared.Record, row.MenuName));
        DskFileEntry entry = Assert.Single(rebuilt.FileSystem.ReadDirectory());

        Assert.Equal(DskFileSystemType.SingleIpl, rebuilt.FileSystem.Type);
        Assert.Equal("SINGLE TEST", entry.Name);
        Assert.Equal(body, rebuilt.FileSystem.Extract(entry));
        Assert.Equal(0x4000, entry.LoadAddress);
        Assert.Equal(0x4100, entry.ExecuteAddress);
    }

    [Theory]
    [InlineData(false, true, false, 1, true, true, false, true)]
    [InlineData(false, true, true, 1, true, true, true, true)]
    [InlineData(false, true, false, 2, false, true, false, true)]
    [InlineData(false, true, true, 2, false, true, true, true)]
    [InlineData(true, true, true, 1, true, true, false, false)]
    [InlineData(false, true, true, 0, false, true, false, false)]
    [InlineData(false, true, true, 1, true, false, false, false)]
    public void IplSaveAvailability_RequiresValidLayoutAndEnablesSaveAfterSaveAs(
        bool busy,
        bool hasDocument,
        bool hasPath,
        int programCount,
        bool single,
        bool layoutValid,
        bool expectedSave,
        bool expectedSaveAs)
    {
        Assert.Equal(
            (expectedSave, expectedSaveAs),
            DskEditorControl.GetIplSaveAvailability(
                busy, hasDocument, hasPath, programCount, single, layoutValid));
    }

    [Fact]
    public async Task IplExport_PreservesOriginalRecordWhenIplPreparationOverflowsMemory()
    {
        byte[] body = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();
        TapeRecord source = CreateRecord(body, 0xFF00, 0xFF00);
        var row = new MultiGameIplRow(1, source, "OVERFLOW")
        {
            Compression = "None"
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => row.PrepareAsync(CancellationToken.None));
        TapeRecord exported = row.GetPreparedRecord();

        Assert.Contains("overflow 16-bit", exception.Message);
        Assert.Equal(
            TapeDocumentWriter.SerializeMzf(source, preserveTrailing: true),
            TapeDocumentWriter.SerializeMzf(exported, preserveTrailing: true));
    }

    [Fact]
    public void MultiGameIpl_ConfigurationInputsRebuildWithoutChangingImage()
    {
        byte[] first = Enumerable.Range(0, 513).Select(value => (byte)(value * 3)).ToArray();
        byte[] second = Enumerable.Range(0, 777).Select(value => (byte)(value * 11)).ToArray();
        MultiGameIplBuildResult original = Mz800MultiGameIplDskWriter.Build([
            new(CreateRecord(first, 0x2000, 0x2010), "FIRST", new MzfCompressionOptions(MzfCompressionAlgorithm.None), first.Length),
            new(CreateRecord(second, 0x3000, 0x3010), "SECOND", new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0), 1200)
        ]);

        DskDocument document = DskDocument.Open(original.Image);
        MultiGameIplFileSystem fileSystem = Assert.IsType<MultiGameIplFileSystem>(document.FileSystem);
        MultiGameIplBuildResult rebuilt = Mz800MultiGameIplDskWriter.Build(fileSystem.GetInputs());

        Assert.Equal(original.Image, rebuilt.Image);
    }

    [Fact]
    public void ReplaceContents_ChangesOpenDocumentAndMarksItModified()
    {
        DskDocument document = DskDocumentFactory.CreateFsmz(ipldisk: false);
        byte[] payload = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
        byte[] replacement = Mz800MultiGameIplDskWriter.Build([
            new(CreateRecord(payload, 0x2000, 0x2010), "REPLACED", new MzfCompressionOptions(MzfCompressionAlgorithm.None), payload.Length)
        ]).Image;

        document.ReplaceContents(replacement);

        Assert.True(document.IsModified);
        Assert.Equal(DskFileSystemType.MultiIpl, document.FileSystem.Type);
        Assert.Equal("REPLACED", Assert.Single(document.FileSystem.ReadDirectory()).Name);
        Assert.Equal(replacement, document.Serialize());
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
    public void ObsoleteMultiGameFixture_IsNotRecognizedAsCurrentFormat()
    {
        DskDocument document = DskDocument.Open(FixturePath("multi.dsk"));

        Assert.NotEqual(DskFileSystemType.MultiIpl, document.FileSystem.Type);
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

    private static byte[] CreatePcmWav(ushort channels, ushort bits, uint sampleRate, int frames)
    {
        int bytesPerSample = bits / 8;
        ushort blockAlign = checked((ushort)(channels * bytesPerSample));
        byte[] result = new byte[44 + (frames * blockAlign)];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        "WAVEfmt "u8.CopyTo(result.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), checked(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34, 2), bits);
        "data"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), (uint)(frames * blockAlign));
        if (bits == 8) result.AsSpan(44).Fill(128);
        return result;
    }

    private static byte[] Resample8BitMonoWav(byte[] source, uint targetSampleRate)
    {
        uint sourceSampleRate = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(24, 4));
        int sourceLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(40, 4)));
        int targetLength = checked((int)Math.Round(sourceLength * targetSampleRate / (double)sourceSampleRate));
        byte[] result = new byte[44 + targetLength];
        source.AsSpan(0, 44).CopyTo(result);
        for (int index = 0; index < targetLength; index++)
        {
            int sourceIndex = Math.Min(sourceLength - 1,
                (int)Math.Round(index * sourceSampleRate / (double)targetSampleRate));
            result[44 + index] = source[44 + sourceIndex];
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), targetSampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), targetSampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), (uint)targetLength);
        return result;
    }

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
