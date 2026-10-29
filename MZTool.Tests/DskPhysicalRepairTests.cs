namespace MZTools.Tests;

public class DskPhysicalRepairTests
{
    private static byte[] Image() => DskImage.CreateUniform(3, 1, 2, 512, 1, 0x4E, 0xE5, "source").Serialize();

    [Fact]
    public void MetadataAndOrderChangePreservesEveryPayloadAndOtherTracks()
    {
        var document = DskDocument.Open(Image());
        document.Image.Tracks[1]!.Sectors[0].Data[0] = 0x11;
        document.Image.Tracks[1]!.Sectors[1].Data[0] = 0x22;
        document.MarkModified();
        var before = document.Serialize();
        var change = new DskTrackProperties(1, 1, 0, 0x33, 0x44, [new(1, 7, 0, 0), new(0, 8, 0, 0)]);
        var preview = DskPhysicalPropertyService.Preview(document, "edited", change);
        Assert.Equal(before, document.Serialize());
        preview.Apply(document);
        var reopened = DskImage.Parse(document.Serialize());
        Assert.Equal("edited", reopened.Creator);
        Assert.Equal(new byte[] { 7, 8 }, reopened.Tracks[1]!.Sectors.Select(s => s.SectorId));
        Assert.Equal(0x22, reopened.Tracks[1]!.Sectors[0].Data[0]);
        Assert.Equal(0x11, reopened.Tracks[1]!.Sectors[1].Data[0]);
        Assert.Equal(0x33, reopened.Tracks[1]!.Gap);
        Assert.Equal(0x44, reopened.Tracks[1]!.Filler);
        int start = reopened.Tracks[0]!.FileOffset;
        Assert.Equal(before.AsSpan(start, reopened.Tracks[0]!.BlockSize).ToArray(), document.Serialize().AsSpan(start, reopened.Tracks[0]!.BlockSize).ToArray());
        Assert.True(document.IsModified);
    }

    [Fact]
    public void DuplicateOrderIsRejectedAndDocumentUnchanged()
    {
        var document = DskDocument.Open(Image()); var original = document.Serialize();
        Assert.Throws<InvalidDataException>(() => DskPhysicalPropertyService.Preview(document, "test", new(0, 0, 0, 0x4E, 0xE5, [new(0, 1, 0, 0), new(0, 2, 0, 0)])));
        Assert.Equal(original, document.Serialize());
    }

    [Theory]
    [InlineData(0x30, 2)]
    [InlineData(0x34, 1)]
    public void DeterministicHeaderRepairPreservesAllTrackBytes(int offset, byte value)
    {
        byte[] original = Image(); byte[] damaged = (byte[])original.Clone(); damaged[offset] = value;
        var plan = DskContainerRepairService.Inspect(damaged).Single(p => p.Risk == DskRepairRisk.SafeDeterministic);
        Assert.Equal(original, plan.Preview!.Result);
        Assert.Equal(original[256..], plan.Preview.Result[256..]);
        Assert.Equal(value, damaged[offset]);
        Assert.Contains("Analyzer before:", plan.Preview.Report);
        Assert.Contains("Analyzer after:", plan.Preview.Report);
        Assert.Contains("Changed byte ranges:", plan.Preview.Report);
        Assert.Equal(0, DskAnalyzer.Analyze(plan.Preview.Result).Errors);
    }

    [Fact]
    public void TrailingBytesAreNeverSilentlyAbsorbedOrTruncated()
    {
        byte[] original = Image(); byte[] trailing = original.Concat(new byte[256]).ToArray();
        var plans = DskContainerRepairService.Inspect(trailing);
        Assert.DoesNotContain(plans, p => p.Risk == DskRepairRisk.SafeDeterministic);
        var truncate = plans.Single(p => p.RequiresTruncateConfirmation);
        Assert.Equal(original, truncate.Preview!.Result);
        Assert.Equal(original.Length + 256, trailing.Length);
    }

    [Fact]
    public void MissingTracksAndWrongCHAreAmbiguous()
    {
        byte[] missing = Image(); missing[0x30] = 4; missing[0x37] = 0;
        Assert.DoesNotContain(DskContainerRepairService.Inspect(missing), p => p.Risk == DskRepairRisk.SafeDeterministic);
        byte[] wrong = Image(); wrong[0x30] = 2; wrong[256 + 0x10] = 7;
        Assert.DoesNotContain(DskContainerRepairService.Inspect(wrong), p => p.Risk == DskRepairRisk.SafeDeterministic);
    }

    [Fact]
    public void StalePreviewCannotApply()
    {
        var document = DskDocument.Open(Image());
        var preview = DskPhysicalPropertyService.Preview(document, "new", new(0, 0, 0, 0x4E, 0xE5, [new(0, 1, 0, 0), new(1, 2, 0, 0)]));
        document.Image.Tracks[2]!.Sectors[0].Data[0] ^= 1; document.MarkModified();
        var changed = document.Serialize();
        Assert.Throws<InvalidOperationException>(() => preview.Apply(document));
        Assert.Equal(changed, document.Serialize());
    }

    [Fact]
    public void IntentionalFdcStatusEditsArePreviewedAndPreservePayload()
    {
        var document = DskDocument.Open(Image()); var payload = document.Image.Tracks[0]!.Sectors[0].Data.ToArray();
        var preview = DskPhysicalPropertyService.Preview(document, "source", new(0, 0, 0, 0x4E, 0xE5, [new(0, 1, 0x20, 0x20), new(1, 2, 0, 0)]));
        Assert.Contains("DSK_FDC_STATUS", preview.Report); preview.Apply(document);
        Assert.Equal(0x20, document.Image.Tracks[0]!.Sectors[0].FdcStatus1); Assert.Equal(payload, document.Image.Tracks[0]!.Sectors[0].Data);
    }

    private static void InFolder(Action<string> test)
    {
        string folder = TapeTestData.CreateTempDirectory();
        try { test(folder); }
        finally { Directory.Delete(folder, true); }
    }

    private static DskDocument RepairedFile(string path)
    {
        byte[] bytes = Image(); bytes[0x34] = 1;
        File.WriteAllBytes(path, bytes);
        var inspection = DskOpenCoordinator.Inspect(bytes, path);
        return inspection.Apply(Assert.Single(inspection.Plans), File.ReadAllBytes(path));
    }

    [Fact]
    public void HealthyOpenNeedsNoPromptAndSavesByteExactly() => InFolder(folder =>
    {
        byte[] bytes = Image(); string path = Path.Combine(folder, "healthy.dsk");
        File.WriteAllBytes(path, bytes);
        var inspection = DskOpenCoordinator.Inspect(bytes, path);
        Assert.Equal(DskOpenStatus.Healthy, inspection.Status);
        Assert.False(inspection.NeedsRepairPrompt); Assert.Empty(inspection.Plans);
        Assert.Empty(inspection.ManualPlans);
        var document = Assert.IsType<DskDocument>(inspection.Original);
        Assert.False(document.IsModified); Assert.False(document.RequiresRepairSaveAs);
        document.Save(); Assert.Equal(bytes, File.ReadAllBytes(path));
    });

    [Theory]
    [InlineData(0x30, 0)]
    [InlineData(0x30, 2)]
    [InlineData(0x34, 1)]
    public void OpenOffersOnlyApplicableRepairAndPreservesEveryTrack(int offset, byte value)
    {
        byte[] bytes = Image(); bytes[offset] = value;
        var before = bytes.ToArray();
        var inspection = DskOpenCoordinator.Inspect(bytes);
        Assert.Equal(DskOpenStatus.RepairAvailable, inspection.Status);
        Assert.True(inspection.NeedsRepairPrompt);
        Assert.All(inspection.Plans, p => { Assert.NotNull(p.Preview); Assert.False(p.RequiresTruncateConfirmation); });
        var document = inspection.Apply(Assert.Single(inspection.Plans), before);
        Assert.True(document.IsModified); Assert.True(document.RequiresRepairSaveAs);
        Assert.Equal(Image(), document.Serialize()); Assert.Equal(before, bytes);
        Assert.Equal(0, DskAnalyzer.Analyze(document).Errors);
        Assert.Throws<InvalidOperationException>(() => document.Save());
    }

    [Fact]
    public void CancelOrDeclineDoesNotApplyOrCreateFiles() => InFolder(folder =>
    {
        byte[] bytes = Image(); bytes[0x30] = 2;
        string path = Path.Combine(folder, "damaged.dsk"); File.WriteAllBytes(path, bytes);
        var inspection = DskOpenCoordinator.Inspect(bytes, path);
        var original = Assert.IsType<DskDocument>(inspection.Original);
        Assert.True(inspection.NeedsRepairPrompt);
        // Cancelling discards the inspection; declining returns its untouched original.
        Assert.False(original.IsModified); Assert.False(original.RequiresRepairSaveAs);
        Assert.Equal(bytes, original.Serialize()); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder));
    });

    [Fact]
    public void TrailingDataOpensWithoutPromptAndRequiresExplicitTruncateConfirmation() => InFolder(folder =>
    {
        byte[] bytes = Image().Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
        string path = Path.Combine(folder, "trailing.dsk"); File.WriteAllBytes(path, bytes);
        var inspection = DskOpenCoordinator.Inspect(bytes, path);
        Assert.False(inspection.NeedsRepairPrompt); Assert.Empty(inspection.Plans);
        var manual = Assert.Single(inspection.ManualPlans);
        Assert.Equal("DSK_TRAILING_DATA", manual.IssueCode); Assert.True(manual.RequiresTruncateConfirmation);
        var document = Assert.IsType<DskDocument>(inspection.Original);
        document.MarkModified(); document.Save(); Assert.Equal(bytes, File.ReadAllBytes(path));
        var plan = Assert.Single(DskContainerRepairService.Inspect(bytes), p => p.RequiresTruncateConfirmation);
        Assert.Throws<InvalidOperationException>(() => document.ApplyContainerRepair(plan));
        Assert.Throws<InvalidOperationException>(() => DskOpenCoordinator.ApplyCopy(bytes, path, plan, bytes));
        Assert.Equal(bytes, document.Serialize());
        document.ApplyContainerRepair(plan, truncateConfirmed: true);
        Assert.True(document.RequiresRepairSaveAs);
        Assert.Equal(Image(), document.Serialize());
        Assert.Throws<IOException>(() => document.Save(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(256)]
    public void InvalidSignatureOrTruncatedHeaderHasSpecificDiagnosticAndNoPrompt(int length)
    {
        var inspection = DskOpenCoordinator.Inspect(new byte[length]);
        Assert.Equal(DskOpenStatus.UnsupportedOrUnparseable, inspection.Status);
        Assert.Null(inspection.Original); Assert.False(inspection.NeedsRepairPrompt);
        Assert.Empty(inspection.ManualPlans);
        Assert.Contains(length < 256 ? "truncated" : "signature", inspection.Diagnostic);
    }

    [Fact]
    public void MissingTrackAndAmbiguousGeometryNeverGetFabricatedRecovery()
    {
        byte[] bytes = Image(); bytes[0x30] = 4; bytes[0x37] = 5;
        var missing = DskOpenCoordinator.Inspect(bytes);
        Assert.Equal(DskOpenStatus.CannotSafelyRepair, missing.Status);
        Assert.False(missing.NeedsRepairPrompt); Assert.Contains("truncated", missing.Diagnostic);
        bytes = Image(); bytes[0x30] = 2; bytes[256 + 0x10] = 7;
        var ambiguous = DskOpenCoordinator.Inspect(bytes);
        Assert.False(ambiguous.NeedsRepairPrompt);
        Assert.Equal(bytes, ambiguous.Original!.Serialize());
        Assert.Contains(DskAnalyzer.Analyze(ambiguous.Original).Issues, i => i.Code == "DSK_TRACK_CH");
    }

    [Fact]
    public void SectorIdsAndFdcStatusArePreservedWithoutRecoverySuggestions()
    {
        byte[] bytes = Image();
        bytes[256 + 0x18] = 7; bytes[256 + 0x18 + 4] = 0x20;
        bytes[256 + 0x20 + 2] = bytes[256 + 0x18 + 2];
        var inspection = DskOpenCoordinator.Inspect(bytes);
        Assert.False(inspection.NeedsRepairPrompt);
        Assert.Equal(bytes, inspection.Original!.Serialize());
        Assert.Contains(DskAnalyzer.Analyze(inspection.Original).Issues, i => i.Code == "DSK_FDC_STATUS");
    }

    [Theory]
    [InlineData(256 + 0x18, 7)]
    [InlineData(256 + 0x18 + 4, 0x20)]
    [InlineData(256 + 0x20 + 2, 1)]
    public void HeaderRepairIsNotOfferedOverAmbiguousSectorMetadata(int offset, byte value)
    {
        byte[] bytes = Image(); bytes[0x34] = 1; bytes[offset] = value;
        var before = bytes.ToArray();
        var inspection = DskOpenCoordinator.Inspect(bytes);
        Assert.Equal(DskOpenStatus.CannotSafelyRepair, inspection.Status);
        Assert.False(inspection.NeedsRepairPrompt); Assert.Empty(inspection.Plans);
        Assert.Empty(inspection.ManualPlans);
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void ContainerRepairPreviewCannotApplyAfterEditingTheOpenDocument()
    {
        byte[] bytes = Image().Concat(new byte[] { 1, 2, 3 }).ToArray();
        var document = DskDocument.Open(bytes);
        var plan = DskContainerRepairService.Inspect(bytes).Single(p => p.Preview != null);
        document.Image.Tracks[0]!.Sectors[0].Data[0] ^= 1; document.MarkModified();
        var edited = document.Serialize();
        Assert.Throws<InvalidOperationException>(() => document.ApplyContainerRepair(plan, truncateConfirmed: true));
        Assert.Equal(edited, document.Serialize()); Assert.False(document.RequiresRepairSaveAs);
    }

    [Fact]
    public void RepairedSaveRejectsOriginalRelativeAndCaseVariantsButAllowsNewCopy() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk");
        var document = RepairedFile(path); var original = File.ReadAllBytes(path);
        string hash = ImageVerificationService.Hash(original);
        Assert.Equal(Path.GetFullPath(path), document.RepairOriginalPath);
        Assert.Throws<InvalidOperationException>(() => document.Save());
        foreach (string alias in new[] { path, path.ToUpperInvariant(), Path.GetRelativePath(Environment.CurrentDirectory, path), Path.Combine(folder, ".", "original.dsk") })
            Assert.Throws<IOException>(() => document.Save(alias));
        Assert.True(document.RequiresRepairSaveAs); Assert.True(document.IsModified);
        string output = Path.Combine(folder, "original_repaired.dsk");
        document.Save(output);
        Assert.False(document.RequiresRepairSaveAs); Assert.False(document.IsModified);
        Assert.Equal(Image(), File.ReadAllBytes(output));
        document.MarkModified(); document.Save(); // Subsequent Save uses the new active copy.
        Assert.Throws<IOException>(() => document.Save(path));
        Assert.Equal(hash, ImageVerificationService.Hash(File.ReadAllBytes(path)));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    });

    [Fact]
    public void HardlinkToRepairSourceCannotBypassSaveAs() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"), alias = Path.Combine(folder, "alias.dsk");
        var document = RepairedFile(path); var before = File.ReadAllBytes(path);
        Assert.True(CreateHardLink(alias, path, IntPtr.Zero), "CreateHardLink failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        Assert.Throws<IOException>(() => document.Save(alias));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(before, File.ReadAllBytes(alias));
    });

    [Fact]
    public void NormalSaveStillProtectsOriginalIfSavedCopyIsReplacedByHardlink() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"), copy = Path.Combine(folder, "copy.dsk");
        var document = RepairedFile(path); var before = File.ReadAllBytes(path);
        document.Save(copy); File.Delete(copy);
        Assert.True(CreateHardLink(copy, path, IntPtr.Zero));
        document.MarkModified();
        Assert.Throws<IOException>(() => document.Save());
        Assert.True(document.IsModified); Assert.Equal(before, File.ReadAllBytes(path));
    });

    [DskSymbolicLinkFact]
    public void FileAndDirectorySymlinksCannotBypassOriginalProtection() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"), link = Path.Combine(folder, "link.dsk");
        string directoryLink = Path.Combine(folder, "directory-link");
        var document = RepairedFile(path); var before = File.ReadAllBytes(path);
        File.CreateSymbolicLink(link, path);
        Directory.CreateSymbolicLink(directoryLink, folder);
        try
        {
            Assert.Throws<IOException>(() => document.Save(link));
            Assert.Throws<IOException>(() => document.Save(Path.Combine(directoryLink, "original.dsk")));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(link); Directory.Delete(directoryLink); }
    });

    [Fact]
    public void OriginalIsLockedAgainstWritesAndDeletesThroughoutDestinationCommit() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"); var document = RepairedFile(path);
        var before = File.ReadAllBytes(path);
        using (document.ProtectRepairDestination(Path.Combine(folder, "new.dsk")))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1, 2, 3]));
            Assert.Throws<IOException>(() => File.Delete(path));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        document.Save(Path.Combine(folder, "new.dsk"));
    });

    [Fact]
    public void StaleFilePreviewIsRejectedEvenWhenCallerPassesOldSnapshot() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"); byte[] bytes = Image(); bytes[0x34] = 1;
        File.WriteAllBytes(path, bytes); var inspection = DskOpenCoordinator.Inspect(bytes, path);
        var plan = Assert.Single(inspection.Plans);
        byte[] changed = bytes.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(path, changed);
        Assert.Throws<InvalidOperationException>(() => inspection.Apply(plan, changed));
        Assert.Throws<InvalidOperationException>(() => inspection.Apply(plan, bytes));
        Assert.Equal(changed, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(folder));
    });

    [Fact]
    public void FailedApplyAndSaveDoNotChangeDocumentOrSource() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"); var document = RepairedFile(path);
        byte[] original = File.ReadAllBytes(path), prepared = document.Serialize();
        var invalid = new DskRepairPlan("invalid", "invalid", DskRepairRisk.SafeDeterministic, false,
            new DskOperationPreview(prepared, [0], "invalid candidate"));
        Assert.Throws<InvalidDataException>(() => document.ApplyContainerRepair(invalid));
        Assert.ThrowsAny<IOException>(() => document.Save(Path.Combine(folder, "missing", "output.dsk")));
        Assert.True(document.IsModified); Assert.True(document.RequiresRepairSaveAs);
        Assert.Equal(prepared, document.Serialize()); Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder));
    });

    [Fact]
    public void ProtectionSurvivesCloneReopenReplacementAndFormatting() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"); var document = RepairedFile(path);
        foreach (var copy in new[] { document.Clone(), document.Reopen(document.Serialize()), document.Reformatted(DskDocument.Open(Image())) })
        {
            copy.ReplaceContents(Image());
            Assert.True(copy.RequiresRepairSaveAs); Assert.True(copy.IsModified);
            Assert.Throws<InvalidOperationException>(() => copy.Save());
            Assert.Throws<IOException>(() => copy.Save(path));
        }
        Assert.Single(Directory.GetFiles(folder));
    });

    [Fact]
    public void MissingOrReplacedOriginalBlocksUnverifiableWrites() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"); var document = RepairedFile(path);
        File.Move(path, Path.Combine(folder, "moved.dsk"));
        Assert.Throws<FileNotFoundException>(() => document.Save(Path.Combine(folder, "copy.dsk")));
        File.WriteAllBytes(path, Image());
        Assert.Throws<IOException>(() => document.Save(Path.Combine(folder, "copy.dsk")));
        Assert.False(File.Exists(Path.Combine(folder, "copy.dsk")));
        Assert.True(document.RequiresRepairSaveAs);
    });

    [Fact]
    public void RepeatedRepairsProtectBothOriginalAndPreviouslySavedCopy() => InFolder(folder =>
    {
        string path = Path.Combine(folder, "original.dsk"), copy = Path.Combine(folder, "first_repaired.dsk");
        var document = RepairedFile(path); var original = File.ReadAllBytes(path);
        document.Save(copy); var firstCopy = File.ReadAllBytes(copy);
        byte[] trailing = document.Serialize().Concat(new byte[] { 1, 2, 3 }).ToArray();
        document.ReplaceContents(trailing);
        var truncate = Assert.Single(DskContainerRepairService.Inspect(trailing), p => p.RequiresTruncateConfirmation);
        document.ApplyContainerRepair(truncate, truncateConfirmed: true);
        Assert.Throws<IOException>(() => document.Save(path));
        Assert.Throws<IOException>(() => document.Save(copy));
        document.Save(Path.Combine(folder, "second_repaired.dsk"));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(firstCopy, File.ReadAllBytes(copy));
    });

    [Fact]
    public void HealthyBootOnlyKeepsIplImportAndHfeDoesNotOfferDskRepair()
    {
        var boot = DskImage.CreateSharpBootDataDisk(3, 2, 9, false, "boot");
        boot.ReplaceTrackGeometry(2, 7, 512, null, 0x4E, 0xE5);
        byte[] header = new byte[256]; header[0] = 3; "IPLPRO"u8.CopyTo(header.AsSpan(1));
        new DskBlockDevice(boot).WriteSector(1, 1, header, inverted: true);
        var inspection = DskOpenCoordinator.Inspect(boot.Serialize());
        Assert.False(inspection.NeedsRepairPrompt);
        Assert.Equal(DskFileSystemType.BootOnly, inspection.Original!.FileSystem.Type);
        var hfe = HfeImage.FromDsk(DskDocumentFactory.CreateCpm(false));
        byte[] bytes = hfe.Serialize(HfeVersion.V3);
        var physical = DskOpenCoordinator.Inspect(bytes);
        Assert.False(physical.NeedsRepairPrompt); Assert.NotNull(physical.Original!.SourceHfe);
        Assert.Equal(bytes, physical.Original.SerializeContainer());
        var plan = DskContainerRepairService.Inspect(Image().Concat(new byte[] { 1 }).ToArray()).Single(p => p.Preview != null);
        Assert.Throws<InvalidOperationException>(() => physical.Original.ApplyContainerRepair(plan, truncateConfirmed: true));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingName, IntPtr securityAttributes);
}

internal sealed class DskSymbolicLinkFactAttribute : FactAttribute
{
    public DskSymbolicLinkFactAttribute()
    {
        string path = Path.Combine(Path.GetTempPath(), "mztools-link-probe-" + Guid.NewGuid().ToString("N"));
        try { File.CreateSymbolicLink(path, path + "-target"); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { Skip = "Symbolic-link creation is unavailable: " + e.Message; }
        finally { File.Delete(path); }
    }
}
