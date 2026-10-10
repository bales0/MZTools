using System.Buffers.Binary;

namespace MZTools.Tests;

public sealed class NativeComTests : IDisposable
{
    private readonly string folder = TapeTestData.CreateTempDirectory();
    public void Dispose() => Directory.Delete(folder, true);
    private static NativeProgramImage Image(int load = 0x2000, int length = 64, int? exec = null)
    {
        var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf(Enumerable.Range(0, length).Select(i => (byte)i).ToArray()));
        var header = record.Header; header.MzfStart = (ushort)load; header.MzfExec = (ushort)(exec ?? load); record.Header = header;
        return MzfNativeImageParser.Parse(record);
    }
    private static NativeLoaderPlan Plan(NativeProgramImage image, NativeMachineMode mode = NativeMachineMode.Mz800AllRam,
        CpmTargetProfile? profile = null) => NativeLoaderPlacementAnalyzer.Analyze(image, profile ?? CpmTargetProfile.BuiltIns[0], mode, true);

    [Fact]
    public void FeedbackDistinguishesFileTypeFromExtraTapeData()
    {
        var image = Image();
        Assert.Null(NativeComFeedback.SourceProblem(image));
        var typeReason = NativeComFeedback.SourceProblem(image with { Type = 2 });
        Assert.Contains("02 (BTX)", typeReason);
        Assert.Contains("interpreter", typeReason);
        Assert.DoesNotContain("compression", typeReason, StringComparison.OrdinalIgnoreCase);
        var extraData = NativeComFeedback.SourceProblem(image with { Dependencies = ["extra record"] });
        Assert.Contains("additional data after the program", extraData);
        Assert.Contains("standalone", extraData);
        Assert.Contains("multiple tape parts", NativeComFeedback.SourceProblem(image, multipart: true));
    }

    [Fact]
    public void FeedbackOffersCompressionOnlyForUntriedMemoryLayouts()
    {
        Assert.Contains("Choose Auto", NativeComFeedback.ConversionProblem("Program too large for selected CP/M profile (no compression)."));
        var exhausted = NativeComFeedback.ConversionProblem("No safe compression/loader combination.\nProgram too large");
        Assert.Contains("even after trying compression", exhausted);
        Assert.DoesNotContain("Choose Auto", exhausted);
        Assert.Contains("machine mode", NativeComFeedback.ConversionProblem("Selected monitor map requires RAM targets"));
    }

    [Fact]
    public void LowSelectedFromWholeTargetAndHighWhenLowOverlaps()
    {
        Assert.Equal("LOW", Plan(Image()).Loader!.Name);
        var high = Plan(Image(0x800, 0x1000)); Assert.True(high.Supported, high.Report); Assert.Equal("HIGH", high.Loader!.Name);
        var blocked = Plan(Image(0x800, 0xA000)); Assert.False(blocked.Supported); Assert.Null(blocked.Loader);
    }

    [Fact]
    public void EverySegmentAndSourceRangeProtectsLoader()
    {
        var image = Image();
        var multiple = image with { Segments = [new(0x800, new byte[0x1000]), new(0xA000, new byte[0x100])] };
        var plan = Plan(multiple); Assert.False(plan.Supported); Assert.Null(plan.Loader);
        var high = CpmTargetProfile.BuiltIns[0] with { Candidates = [new("HIGH", 0xA000, 256)] };
        Assert.False(Plan(Image(0x2000, 0x9000), profile: high).Supported); // HIGH lies in embedded source
        foreach (int start in new[] { 0x100, 0x200, 0x300, 0x10F0 })
            Assert.False(Plan(Image(), profile: high with { Candidates = [new("custom", start, 256)] }).Supported);
    }

    [Theory]
    [InlineData(0x0800, 0x1000, false)]
    [InlineData(0x1200, 0x0100, false)]
    [InlineData(0x2000, 0x0100, false)]
    [InlineData(0x2200, 0x1800, true)]
    public void CopyDirectionMatchesMemmove(int load, int length, bool backward)
    {
        var plan = Plan(Image(load, length)); Assert.True(plan.Supported, plan.Report); Assert.Equal(backward, plan.Backward);
        byte[] memory = Enumerable.Repeat((byte)0xA5, 65536).ToArray();
        var payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray(); payload.CopyTo(memory, plan.SourceStart);
        if (backward) for (int i = length - 1; i >= 0; i--) memory[load + i] = memory[plan.SourceStart + i];
        else for (int i = 0; i < length; i++) memory[load + i] = memory[plan.SourceStart + i];
        Assert.Equal(payload, memory.AsSpan(load, length).ToArray());
    }

    [Theory]
    [InlineData(0xFFF0, 64, 0xFFF0)]
    [InlineData(0x2000, 64, 0x1000)]
    [InlineData(0x2000, 0, 0x2000)]
    public void InvalidRangesAndEntrypointsReject(int load, int size, int exec) => Assert.False(Plan(Image(load, size, exec)).Supported);

    [Fact]
    public void CpmOverwriteAfterTakeoverIsAllowedButComMustFitBeforehand()
    {
        Assert.True(Plan(Image(0xD000, 256)).Supported); // destination can overwrite OS in all-RAM mode
        var tooBig = Plan(Image(0x2000, 0xB000)); Assert.False(tooBig.Supported); Assert.Contains("too large", tooBig.Report);
        Assert.False(Plan(Image(), profile: CpmTargetProfile.Create("bad", 0x10000)).Supported);
    }

    [Fact]
    public void MonitorMappingsAndCgRomDoNotHideExecutingStage()
    {
        var image = Image();
        var monitor800 = Plan(image, NativeMachineMode.Mz800Monitor); Assert.True(monitor800.Supported); Assert.Equal("LOW", monitor800.Loader!.Name);
        var monitor700 = Plan(image, NativeMachineMode.Mz700Monitor); Assert.True(monitor700.Supported); Assert.Equal("HIGH", monitor700.Loader!.Name);
        Assert.False(Plan(Image(0xB000, 0x1800), NativeMachineMode.Mz700Monitor).Supported);
        Assert.False(Plan(Image(0x800, 256), NativeMachineMode.Mz800Monitor).Supported);
    }

    [Fact]
    public void DependenciesAndNonMachineCodeAreNeverSilentlyConverted()
    {
        var image = Image();
        Assert.False(NativeLoaderPlacementAnalyzer.Analyze(image, CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800AllRam, false).Supported);
        Assert.False(NativeLoaderPlacementAnalyzer.Analyze(image, CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800AllRam, true, true).Supported);
        Assert.False(Plan(image with { Type = 2 }).Supported);
        Assert.False(Plan(image with { Dependencies = ["later tape load"] }).Supported);
        Assert.Throws<InvalidDataException>(() => MzNativeComBuilder.Build(image with { Type = 2 }, CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800AllRam, true));
    }

    [Theory]
    [InlineData(".mzf")]
    [InlineData(".m12")]
    public void FileParserChecksHeaderBodyAndPreservesSource(string extension)
    {
        byte[] bytes = TapeTestData.CreateMzf([1, 2, 3]); string path = Path.Combine(folder, "TEST" + extension); File.WriteAllBytes(path, bytes);
        var image = MzfNativeImageParser.Read(path);
        Assert.Equal(extension == ".m12" ? "M12" : "MZF", image.SourceType);
        Assert.Equal(0x1200, image.EntryPoint); Assert.Equal(new byte[] { 1, 2, 3 }, image.Segments[0].Data);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        File.WriteAllBytes(path, bytes[..^1]); Assert.Throws<EndOfStreamException>(() => MzfNativeImageParser.Read(path));
        File.WriteAllBytes(path, [1, 2]); Assert.Throws<InvalidDataException>(() => MzfNativeImageParser.Read(path));
    }

    [Fact]
    public void PaddingIsExcludedAndNonPaddingTrailingDependencyRejects()
    {
        string path = Path.Combine(folder, "TEST.m12"); File.WriteAllBytes(path, TapeTestData.CreateMzf([1, 2, 3], trailing: [0, 0xFF]));
        var image = MzfNativeImageParser.Read(path); Assert.Empty(image.Dependencies); Assert.Single(image.Warnings); Assert.Equal(3, image.Segments[0].Data.Length);
        var output = MzNativeComBuilder.Build(image, CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, true);
        Assert.Equal(NativeLoaderPlacementAnalyzer.PayloadSource - 0x100 + 3, output.Bytes.Length);
        File.WriteAllBytes(path, TapeTestData.CreateMzf([1, 2, 3], trailing: [1])); Assert.False(Plan(MzfNativeImageParser.Read(path)).Supported);
    }

    [Theory]
    [InlineData((int)NativeMachineMode.Mz800AllRam)]
    [InlineData((int)NativeMachineMode.Mz800Monitor)]
    [InlineData((int)NativeMachineMode.Mz700Monitor)]
    public void GeneratedMetadataMatchesStageRelocationPayloadAndHash(int modeValue)
    {
        var mode = (NativeMachineMode)modeValue;
        var image = Image(); var result = MzNativeComBuilder.Build(image, CpmTargetProfile.BuiltIns[0], mode, true);
        int meta = NativeLoaderPlacementAnalyzer.MetadataSource - 0x100;
        int U(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(result.Bytes.AsSpan(meta + offset, 2));
        Assert.Equal("NZC1"u8.ToArray(), result.Bytes.AsSpan(meta, 4).ToArray());
        Assert.Equal(result.Plan.Loader!.Start, U(4)); Assert.Equal(0x200, U(6)); Assert.InRange(U(8), 1, 192);
        Assert.Equal(image.Segments[0].Data, result.Bytes.AsSpan(0x1200 - 0x100).ToArray());
        Assert.Equal(image.Header, result.Bytes.AsSpan(0x300 - 0x100, 128).ToArray());
        Assert.Contains(ImageVerificationService.Hash(result.Bytes), result.Report);
        Assert.Contains("RESET", result.Report); Assert.Contains("Bootstrap compression: NONE", result.Report);
        Assert.Equal(image.Segments[0].Data, Image().Segments[0].Data);
    }

    [Fact]
    public void VerifiedWriterReplacesOnlyChosenDestination()
    {
        string path = Path.Combine(folder, "GAME.com"); File.WriteAllBytes(path, [9]);
        var result = MzNativeComBuilder.Build(Image(), CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, true);
        NativeComDialog.WriteVerified(path, result.Bytes); Assert.Equal(result.Bytes, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void ImportIsTransactionalPreservesExistingFilesAndValidatesPayload()
    {
        var disk = DskDocumentFactory.CreateCpm(false);
        disk.FileSystem.Insert("KEEP.BIN", [1, 2, 3]); disk.MarkModified();
        var keep = disk.FileSystem.ReadDirectory().Single();
        var keepBytes = disk.FileSystem.Extract(keep);
        var before = disk.Serialize();
        var conversion = MzNativeComBuilder.Build(Image(), CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, true);
        var preview = NativeComImportService.Preview(disk, "GAME.COM", conversion);
        Assert.Equal(before, disk.Serialize());
        preview.Operation.Apply(disk);
        var imported = disk.FileSystem.ReadDirectory().Single(e => e.Key == preview.FileKey);
        Assert.Equal(conversion.Bytes, disk.FileSystem.Extract(imported).Take(conversion.Bytes.Length));
        Assert.Equal(keepBytes, disk.FileSystem.Extract(disk.FileSystem.ReadDirectory().Single(e => e.Name == "KEEP")));
        Assert.True(disk.IsModified);
        var after = disk.Serialize();
        Assert.Throws<InvalidDataException>(() => NativeComImportService.Preview(disk, "GAME.COM", conversion));
        Assert.Equal(after, disk.Serialize());
    }

    [Fact]
    public void ImportRejectsWrongFilesystemInvalidNameAndStalePreview()
    {
        var conversion = MzNativeComBuilder.Build(Image(), CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, true);
        Assert.Contains("No current disk", NativeComImportService.UnavailableReason(null));
        Assert.Throws<InvalidOperationException>(() => NativeComImportService.Preview(DskDocumentFactory.CreateFsmz(), "GAME.COM", conversion));
        var disk = DskDocumentFactory.CreateCpm(false);
        Assert.Throws<InvalidDataException>(() => NativeComImportService.Preview(disk, "GAME.BIN", conversion));
        var preview = NativeComImportService.Preview(disk, "GAME.COM", conversion);
        disk.FileSystem.Insert("LATER.BIN", [1]); disk.MarkModified();
        var before = disk.Serialize();
        Assert.Throws<InvalidOperationException>(() => preview.Operation.Apply(disk));
        Assert.Equal(before, disk.Serialize());
    }

    [Fact]
    public void UnsupportedPlanExposesSpecificReason()
    {
        var plan = Plan(Image(0x2000, 0xB000));
        Assert.False(plan.Supported);
        Assert.Contains("too large", plan.UnsupportedReason);
    }

    [Theory]
    [InlineData("DSK", "FLAPPY ver 1.0A.mzf")]
    [InlineData("MZF", "Flappy.mzf")]
    [InlineData("DSK", "Belegost.mzf")]
    public void ReportedGamesFitDefaultProfileWithoutChangingSourceOrPayload(string directory, string name)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", directory, name));
        byte[] original = File.ReadAllBytes(path);
        var image = MzfNativeImageParser.Read(path);
        var result = MzNativeComBuilder.Build(image, CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, true);
        Assert.True(result.Plan.Supported, result.Report);
        Assert.Equal(original.AsSpan(128, image.Segments[0].Data.Length).ToArray(), result.Bytes.AsSpan(0x1100).ToArray());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Contains("timer IRQ gate", result.Report);
    }

    [Fact]
    public void SuppliedPackedFlappyRestoresExactlyTheSuppliedUncompressedGame()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var packed = new MZTFileReader().ReadStandaloneMzf(Path.Combine(root, "DSK", "FLAPPY ver 1.0A.mzf"));
        var plain = new MZTFileReader().ReadStandaloneMzf(Path.Combine(root, "MZF", "Flappy.mzf"));
        var restored = MzfDecompressionService.Decompress(packed).Record;
        Assert.Equal(plain.Body.MzfBody, restored.Body.MzfBody);
        Assert.Equal(plain.Header.MzfStart, restored.Header.MzfStart);
        Assert.Equal(plain.Header.MzfExec, restored.Header.MzfExec);
        Assert.Contains("preserved unchanged", MzfNativeImageParser.Parse(packed).Warnings.Single());
    }

    [Theory]
    [InlineData("Belegost", "BELEGOST.COM")]
    [InlineData("FLAPPY ver 1.0A", "FLAPPYVE.COM")]
    [InlineData("a/b:c*defghijk", "ABCDEFGH.COM")]
    [InlineData("  ?  ", "PROGRAM.COM")]
    public void SuggestedComNameUsesDecodedHeaderAndFitsCpm(string headerName, string expected)
        => Assert.Equal(expected, NativeComConversionService.SuggestName(headerName));

    [Theory]
    [InlineData((int)MzfCompressionAlgorithm.Zx0, (int)CompressionDirection.Forward)]
    [InlineData((int)MzfCompressionAlgorithm.Zx0, (int)CompressionDirection.Backward)]
    [InlineData((int)MzfCompressionAlgorithm.Zx7, (int)CompressionDirection.Forward)]
    [InlineData((int)MzfCompressionAlgorithm.Zx7, (int)CompressionDirection.Backward)]
    public void ComCompressionRoundTripsAndKeepsOriginalHash(int algorithm, int direction)
    {
        var image = Image(0x3000, 4096);
        byte[] before = image.Segments[0].Data.ToArray();
        var policy = new MzfCompressionOptions((MzfCompressionAlgorithm)algorithm, (CompressionDirection)direction,
            Zx0Quick: algorithm == (int)MzfCompressionAlgorithm.Zx0);
        var result = NativeComConversionService.Build(image, CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, policy);
        byte[] serialized = result.Bytes.AsSpan(0x200, 128).ToArray().Concat(result.Bytes.AsSpan(0x1100).ToArray()).ToArray();
        using var reader = new BinaryReader(new MemoryStream(serialized));
        var restored = MzfDecompressionService.Decompress(new MZTFileReader().ReadMzfRecord(reader)).Record;
        Assert.Equal(before, restored.Body.MzfBody);
        Assert.Equal(image.EntryPoint, restored.Header.MzfExec);
        Assert.Equal(before, image.Segments[0].Data);
        Assert.Contains(image.InputSha256, result.Report);
    }

    [Fact]
    public void AutoCanFitProgramWhichExceedsUncompressedComCeiling()
    {
        var image = Image(0x3000, 8192);
        var profile = CpmTargetProfile.Create("Small TPA", 0x2000);
        Assert.False(Plan(image, NativeMachineMode.Mz800Monitor, profile).Supported);
        var result = NativeComConversionService.Build(image, profile, NativeMachineMode.Mz800Monitor, new(MzfCompressionAlgorithm.Auto));
        Assert.True(result.Plan.Supported, result.Report);
        Assert.True(result.Bytes.Length < 0x2000 - 0x100);
        Assert.Contains("Auto: smallest COM", result.Report);
    }

    [Theory]
    [InlineData("Hlipa.mzf")]
    [InlineData("compress_zx0_HLIPA.mzf")]
    public void HlipaHeaderTrampolineSurvivesComCompression(string name)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "MZF", name));
        byte[] original = File.ReadAllBytes(path);
        var image = MzfNativeImageParser.Read(path);
        Assert.Equal(0x4D, image.Type);
        Assert.True(NativeHeaderExecution.TryGetJump(image, out int target)); Assert.Equal(0xCA00, target);
        Assert.Null(NativeComFeedback.SourceProblem(image));
        var result = NativeComConversionService.Build(image, CpmTargetProfile.BuiltIns[0], NativeMachineMode.Mz800Monitor, new(MzfCompressionAlgorithm.Auto));
        Assert.True(result.Plan.Supported, result.Report);
        Assert.Contains("Executable header preserved", result.Report);
        byte[] serialized = result.Bytes.AsSpan(0x200, 128).ToArray().Concat(result.Bytes.AsSpan(0x1100).ToArray()).ToArray();
        using var reader = new BinaryReader(new MemoryStream(serialized));
        var outputRecord = new MZTFileReader().ReadMzfRecord(reader);
        if (MzfLoaderBuilder.TryGetCompressionInfo(outputRecord, out _)) outputRecord = MzfDecompressionService.Decompress(outputRecord).Record;
        var inputRecord = new MZTFileReader().ReadStandaloneMzf(path);
        if (MzfLoaderBuilder.TryGetCompressionInfo(inputRecord, out _)) inputRecord = MzfDecompressionService.Decompress(inputRecord).Record;
        Assert.Equal(inputRecord.Body.MzfBody, outputRecord.Body.MzfBody);
        Assert.Equal(0x116C, outputRecord.Header.MzfExec);
        Assert.Equal(inputRecord.GetSerializedHeader().AsSpan(24).ToArray(), outputRecord.GetSerializedHeader().AsSpan(24).ToArray());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void UnknownTypeAndInvalidHeaderTrampolinesStillReject()
    {
        var image = Image() with { Type = 0x4D, EntryPoint = 0x116C };
        Assert.False(Plan(image).Supported);
        var header = image.Header.ToArray(); header[124] = 0xC3; header[125] = 0; header[126] = 0x20;
        image = image with { Header = header };
        Assert.True(Plan(image).Supported);
        Assert.False(Plan(image with { Type = 0x99 }).Supported);
        header[126] = 0x40; Assert.False(Plan(image).Supported); // target is outside the supplied payload
    }
}
