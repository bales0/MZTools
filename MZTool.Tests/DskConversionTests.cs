namespace MZTools.Tests;

public class DskConversionTests
{
    private static DskConversionProfile Target(DskDocument source, DskConversionTarget target) =>
        DskCapabilityService.GetAvailableConversions(source).Single(p => p.Target == target);

    [Theory]
    [InlineData(false, 0, 1)] [InlineData(true, 0, 1)]
    [InlineData(false, 3, 256)] [InlineData(true, 3, 256)]
    [InlineData(false, 1, 4080)] [InlineData(true, 1, 4080)]
    public void FsmzRelatedConversionRetainsGeometryDataAndNativeMetadata(bool extended, int count, int size)
    {
        var source = DskDocumentFactory.CreateFsmz(extended, 4, 1);
        var fs = (FsmzFileSystem)source.FileSystem; fs.SetVolume(7);
        for (int i = 0; i < count; i++)
        {
            fs.Insert($"FILE{i}", Enumerable.Range(0, size).Select(j => (byte)j).ToArray(), 2, 0x1234, 0x5678);
            var entry = fs.ReadDirectory().Last(); var raw = fs.DirectoryMetadata(entry);
            raw[18] = 0x72; raw[19] = 0x9D; fs.RestoreDirectoryMetadata(entry, raw);
        }
        source.MarkModified(); var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, extended ? DskConversionTarget.Fsmz63 : DskConversionTarget.Fsmz127));
        Assert.True(result.CanConvert, result.Report); Assert.Equal(count, result.CompatibleFiles);
        var output = DskDocument.Open(result.OutputImage!); var converted = Assert.IsType<FsmzFileSystem>(output.FileSystem);
        Assert.Equal(extended ? 63 : 127, converted.DirectoryLimit); Assert.Equal(7, converted.VolumeNumber);
        Assert.Equal(source.Image.TrackCount, output.Image.TrackCount); Assert.Equal(source.Image.SideCount, output.Image.SideCount);
        var oldEntries = fs.ReadDirectory(); var newEntries = converted.ReadDirectory();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(fs.Extract(oldEntries[i]), converted.Extract(newEntries[i]));
            Assert.Equal(fs.DirectoryMetadata(oldEntries[i]).Take(30), converted.DirectoryMetadata(newEntries[i]).Take(30));
        }
        Assert.Equal(before, source.Serialize()); Assert.True(source.IsModified);
    }

    [Fact]
    public void ExtendedShrinkRefusesMoreThan63EntriesWithoutProducingPartialImage()
    {
        var source = DskDocumentFactory.CreateFsmz(true);
        for (int i = 0; i < 64; i++) source.FileSystem.Insert($"FILE{i}", [1]);
        source.MarkModified(); var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.Fsmz63));
        Assert.False(result.CanConvert); Assert.Null(result.OutputImage); Assert.Contains("63-entry", result.Report);
        Assert.Equal(before, source.Serialize());
    }

    [Fact]
    public void StandardDataAtBlock24IsNotAnExtendedDirectoryAndCanBeRelocated()
    {
        var source = DskDocumentFactory.CreateFsmz(false, 4, 1);
        var fs = (FsmzFileSystem)source.FileSystem; fs.SetRebuiltBounds(24, 63);
        fs.Insert("PAYLOAD", Enumerable.Repeat((byte)0x42, 3000).ToArray()); source.MarkModified();
        var before = source.Serialize();
        var reopened = DskDocument.Open(before); Assert.Equal(63, Assert.IsType<FsmzFileSystem>(reopened.FileSystem).DirectoryLimit);
        var result = DskFileTransferService.Preflight(reopened, Target(reopened, DskConversionTarget.Fsmz127));
        Assert.True(result.CanConvert, result.Report);
        var converted = DskDocument.Open(result.OutputImage!); var target = Assert.IsType<FsmzFileSystem>(converted.FileSystem);
        Assert.Equal(32, target.FileAreaBlock); Assert.Equal(63, target.LastBlock);
        Assert.Equal(fs.Extract(fs.ReadDirectory()[0]), target.Extract(target.ReadDirectory()[0]));
        Assert.Equal(before, source.Serialize());
    }

    [Fact]
    public void ExtendedShrinkRelocatesAHighDirectorySlotTransactionally()
    {
        var source = DskDocumentFactory.CreateFsmz(true);
        for (int i = 0; i < 64; i++) source.FileSystem.Insert($"FILE{i}", [(byte)i]);
        foreach (var entry in source.FileSystem.ReadDirectory().Take(63)) source.FileSystem.Delete(entry);
        source.MarkModified(); var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.Fsmz63));
        Assert.True(result.CanConvert, result.Report);
        var output = DskDocument.Open(result.OutputImage!); var file = Assert.Single(output.FileSystem.ReadDirectory());
        Assert.Equal("FILE63", file.Name); Assert.Equal("1", file.Key); Assert.Equal(new byte[] { 63 }, output.FileSystem.Extract(file));
        Assert.Equal(before, source.Serialize());
    }

    [Fact]
    public void CpMToFsmzReportsMetadataLossAndRejectsCrossUserNameCollisions()
    {
        var source = DskDocumentFactory.CreateCpm(false);
        source.FileSystem.Insert("DUP.BIN", [1], user: 0); source.FileSystem.Insert("DUP.BIN", [2], user: 3);
        var fs = (CpmFileSystem)source.FileSystem; fs.SetAttributes(fs.ReadDirectory()[0], true, true, true);
        source.MarkModified(); var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.Fsmz63));
        Assert.False(result.CanConvert); Assert.Null(result.OutputImage);
        Assert.Contains("Duplicate target", result.Report); Assert.Contains("RO/SYS/ARC", result.Report); Assert.Contains("user area", result.Report);
        Assert.Equal(before, source.Serialize());
    }

    [Theory]
    [InlineData("LONGFILENAME.BIN")] [InlineData("INVALID/NAME")]
    public void UnsupportedFilenameCancelsStrictTransfer(string name)
    {
        var source = DskDocumentFactory.CreateFsmz(); source.FileSystem.Insert(name, [1, 2, 3]); source.MarkModified();
        var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.LecDd));
        Assert.False(result.CanConvert); Assert.Null(result.OutputImage); Assert.NotEmpty(result.Issues);
        Assert.Equal(before, source.Serialize());
    }

    [Fact]
    public void InsufficientTargetCapacityRefusesAllOutput()
    {
        var source = DskDocumentFactory.CreateCpm(true);
        for (int i = 0; i < 10; i++) source.FileSystem.Insert($"FILE{i}.BIN", new byte[60000]);
        source.MarkModified(); var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.PersonalCpm));
        Assert.False(result.CanConvert); Assert.Null(result.OutputImage); Assert.Contains("full", result.Report, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, source.Serialize());
    }

    [Fact]
    public void CpmLayoutCopyPreservesUsersAttributesAndContentButDoesNotCopySystem()
    {
        var source = DskDocumentFactory.CreateCpm(false);
        source.FileSystem.Insert("TEST.BIN", Enumerable.Range(0, 17000).Select(i => (byte)i).ToArray(), user: 4);
        var fs = (CpmFileSystem)source.FileSystem; fs.SetAttributes(fs.ReadDirectory()[0], true, true, true);
        source.Image.Tracks[1]!.Sectors[0].Data[^1] = 0xA6; source.MarkModified();
        var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.Sds400));
        Assert.True(result.CanConvert, result.Report);
        var output = DskDocument.Open(result.OutputImage!); var entry = Assert.Single(output.FileSystem.ReadDirectory());
        Assert.Equal(4, entry.User); Assert.True(entry.ReadOnly); Assert.True(entry.System); Assert.True(entry.Archived);
        Assert.Equal(fs.Extract(fs.ReadDirectory()[0]), output.FileSystem.Extract(entry));
        Assert.Contains("None", DskBootInfo.Inspect(output).System);
        Assert.Equal(before, source.Serialize());
    }

    [Theory]
    [InlineData((int)DskConversionTarget.Fsmz63)] [InlineData((int)DskConversionTarget.Fsmz127)]
    [InlineData((int)DskConversionTarget.LecDd)]
    public void MrsTransferRetainsSourceBytesAndReportsAddressPolicy(int target)
    {
        var source = DskDocumentFactory.CreateMrs(); source.FileSystem.Insert("TEST.BIN", [1, 2, 3], loadAddress: 0x2000, executeAddress: 0x2010);
        source.MarkModified(); var before = source.Serialize();
        var result = DskFileTransferService.Preflight(source, Target(source, (DskConversionTarget)target));
        Assert.True(result.CanConvert, result.Report);
        var output = DskDocument.Open(result.OutputImage!); var entry = Assert.Single(output.FileSystem.ReadDirectory());
        Assert.Equal(source.FileSystem.Extract(source.FileSystem.ReadDirectory()[0]), output.FileSystem.Extract(entry));
        if (output.FileSystem is FsmzFileSystem) { Assert.Equal(0x2000, entry.LoadAddress); Assert.Equal(0x2010, entry.ExecuteAddress); }
        else Assert.Contains("loses LOAD/EXEC", result.Report);
        Assert.Equal(before, source.Serialize());
    }

    [Fact]
    public void FsmzToMrsExplicitlyReportsRecordPadding()
    {
        var source = DskDocumentFactory.CreateFsmz(); source.FileSystem.Insert("TEST.BIN", [1]); source.MarkModified();
        var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.Mrs));
        Assert.True(result.CanConvert, result.Report); Assert.Contains("511 padding bytes", result.Report);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FormattedCpmWithoutSystemIsNotReportedAsBootable(bool personal)
    {
        var source = personal ? DskDocumentFactory.CreatePersonalCpm80(false) : DskDocumentFactory.CreateCpm(false);
        source.FileSystem.Insert("DATA.BIN", [1, 2, 3]); source.MarkModified();
        var info = DskBootInfo.Inspect(source);
        Assert.StartsWith("No", info.Bootable); Assert.StartsWith("None", info.System);
        source.Image.Tracks[1]!.Sectors[^1].Data[^1] = 0xA6;
        Assert.StartsWith(personal ? "No" : "Unverified", DskBootInfo.Inspect(source).Bootable);
    }

    [Fact]
    public void DataOnlyFsmzDinfoIsNotMistakenForSystemCode()
    {
        var info = DskBootInfo.Inspect(DskDocumentFactory.CreateFsmz());
        Assert.StartsWith("No", info.Bootable); Assert.Equal("None detected", info.System);
    }

    [Fact]
    public void HeaderOnlyPersonalCpmIsNotAnInstallableSystemSource()
    {
        var source = DskDocumentFactory.CreatePersonalCpm80(false);
        Assert.False(DskCapabilityService.CanInstallBootSystem(DskDocumentFactory.CreatePersonalCpm80(false), source).IsCompatible);
    }

    [Fact]
    public void SaveConversionCannotOverwriteSourceAndWritesTheCompleteOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mztools-conversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string original = Path.Combine(directory, "source.dsk"), output = Path.Combine(directory, "converted.dsk");
        try
        {
            var source = DskDocumentFactory.CreateFsmz(false); File.WriteAllBytes(original, source.Serialize());
            byte[] before = File.ReadAllBytes(original);
            var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.Fsmz127));
            Assert.True(result.CanConvert, result.Report);
            Assert.Throws<InvalidDataException>(() => DskFileTransferService.SaveConvertedImage(result, original, original));
            Assert.Equal(before, File.ReadAllBytes(original));
            DskFileTransferService.SaveConvertedImage(result, output, original);
            Assert.Equal(result.OutputImage, File.ReadAllBytes(output)); Assert.Equal(before, File.ReadAllBytes(original));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
            Assert.Equal(127, Assert.IsType<FsmzFileSystem>(DskDocument.Open(output).FileSystem).DirectoryLimit);
        }
        finally { File.Delete(original); File.Delete(output); Directory.Delete(directory); }
    }

    [Fact]
    public void FailedDestinationCommitCleansTemporaryOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mztools-conversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string blocked = Path.Combine(directory, "blocked.dsk"); Directory.CreateDirectory(blocked);
        try
        {
            var source = DskDocumentFactory.CreateFsmz(false);
            var result = DskFileTransferService.Preflight(source, Target(source, DskConversionTarget.Fsmz127));
            var error = Record.Exception(() => DskFileTransferService.SaveConvertedImage(result, blocked, null));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Empty(Directory.GetFiles(directory)); Assert.True(Directory.Exists(blocked));
        }
        finally { Directory.Delete(blocked); Directory.Delete(directory); }
    }

    internal static void ExerciseDialogOnCurrentStaThread()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var source = DskDocumentFactory.CreateFsmz(false);
        source.FileSystem.Insert("LONGFILENAME.BIN", [1, 2, 3]); source.MarkModified();
        var before = source.Serialize();
        var type = typeof(DskConversionDialog);
        var dialog = type.GetConstructors(flags).Single().Invoke([null, source]);
        object Field(string name) => type.GetField(name, flags)!.GetValue(dialog)!;
        bool Enabled() => (bool)Field("convert").GetType().GetProperty("IsEnabled")!.GetValue(Field("convert"))!;
        Assert.True(Enabled()); // Related FSMZ expansion, with a valid Sharp name.
        Field("profiles").GetType().GetProperty("SelectedItem")!.SetValue(Field("profiles"), Target(source, DskConversionTarget.LecDd));
        Assert.False(Enabled());
        Assert.Contains("8.3", (string)Field("report").GetType().GetProperty("Text")!.GetValue(Field("report"))!);
        Assert.Equal(before, source.Serialize());
        type.GetMethod("Close")!.Invoke(dialog, null);
    }
}
