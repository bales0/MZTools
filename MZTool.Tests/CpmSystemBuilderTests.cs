using System.Security.Cryptography;

namespace MZTools.Tests;

public sealed class CpmSystemReferenceTheoryAttribute : TheoryAttribute
{
    public CpmSystemReferenceTheoryAttribute()
    {
        if (!Enumerable.Range(0, 5).All(index => File.Exists(CpmSystemBuilderTests.ReferencePath(index))))
            Skip = "Place the five documented CP/M references in the sibling _SFD800 directory to run local full-image regressions.";
    }
}

public sealed class CpmSystemBuilderTests
{
    internal static string ReferencePath(int index)
    {
        string[] paths = ["CPMv23_System/CPMv23 System.dsk", "CPMv41_System/CPMv41 System_DD_720kB_IRQ.dsk",
            "CPMv41_System/CPMv41_System_HD_1440K_IRQ.dsk", "CPMv42_System/CPMv42 SystemDD720kB.dsk",
            "CPMv42_System/CPMv42 SystemHD1440kB_POLL.dsk"];
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../_SFD800", paths[index]));
    }

    [CpmSystemReferenceTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void RealRegisteredSystemsRemainIdentifiedWithDifferentComAndGameFiles(int index)
    {
        var source = DskDocument.Open(ReferencePath(index));
        var definition = CpmSystemProfileRegistry.VerifiedTemplates[index];
        Assert.Equal(definition.ReferenceImageSha256, Sha(source.Serialize()));
        var registered = CpmSystemProfileRegistry.ResolveVerifiedSource(source);
        Assert.Equal(definition.Id, registered.Id);
        string originalWholeHash = Sha(source.Serialize());
        string systemHash = registered.SystemAreaSha256;
        source.FileSystem.Insert("REGTEST.COM", [0xC9, 1, 2, 3], user: 7);
        source.FileSystem.Insert("REGGAME.COM", Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray(), user: 8);
        source.MarkModified();
        source = DskDocument.Open(source.Serialize());
        Assert.NotEqual(originalWholeHash, Sha(source.Serialize()));
        Assert.Equal(systemHash, CpmSystemFingerprint.From(source).SystemAreaSha256);
        Assert.Equal(registered.Id, CpmSystemProfileRegistry.ResolveVerifiedSource(source).Id);
        Assert.Equal(CpmRuntimeVerification.StaticValidated, registered.Verification);
        var target = DskDocumentFactory.CreateCpm(registered.Layout == CpmLayoutId.LecHd1440);
        target.FileSystem.Insert("KEEP.COM", [1, 2, 3, 4]); target.MarkModified();
        byte[] original = target.Serialize();
        byte[] payload = target.FileSystem.Extract(target.FileSystem.ReadDirectory().Single());
        var report = CpmSystemBuilder.Apply(target, source, registered);
        AssertOutsideSystemAreaEqual(original, target.Serialize(), registered.SystemPhysicalTracks);
        Assert.Equal(payload, target.FileSystem.Extract(Assert.Single(target.FileSystem.ReadDirectory())));
        Assert.Equal("KEEP", target.FileSystem.ReadDirectory()[0].Name);
        Assert.NotEmpty(report.ChangedRanges);
        source.Image.Tracks[2]!.Sectors[0].Data[^1] ^= 1; source.MarkModified();
        Assert.NotEqual(systemHash, CpmSystemFingerprint.From(source).SystemAreaSha256);
        Assert.Null(CpmSystemProfileRegistry.TryResolveVerifiedSource(source));
        Assert.True(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
    }

    [CpmSystemReferenceTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void DescriptorChangesNeverRemainRegisteredOrInstallIntoCanonicalTarget(int index)
    {
        var source = DskDocument.Open(ReferencePath(index));
        var profile = CpmSystemProfileRegistry.ResolveVerifiedSource(source);
        source.Image.Tracks[4]!.Sectors.Reverse(); source.MarkModified();
        Assert.Null(CpmSystemProfileRegistry.TryResolveVerifiedSource(source));
        var target = DskDocumentFactory.CreateCpm(profile.Layout == CpmLayoutId.LecHd1440);
        byte[] before = target.Serialize();
        Assert.False(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
        Assert.Throws<InvalidDataException>(() => CpmSystemBuilder.Apply(target, source, profile));
        Assert.Equal(before, target.Serialize()); Assert.False(target.IsModified);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompatibleSourceWithFilesProducesUnverifiedReportAndPreservesTarget(bool hd)
    {
        var source = SystemSource(hd, 0x91);
        source.FileSystem.Insert("FOREIGN.COM", [1, 2, 3]); source.MarkModified();
        var target = DskDocumentFactory.CreateCpm(hd);
        target.FileSystem.Insert("KEEP.COM", [4, 5, 6]); target.MarkModified();
        byte[] before = target.Serialize(); byte[] sourceBefore = source.Serialize();
        var report = DskBootSystemService.InstallWithReport(target, source);
        Assert.Null(report.TransferMode); Assert.Equal(BootLoaderKind.Unknown, report.BootLoader);
        Assert.Contains("Unverified", report.ToText());
        AssertOutsideSystemAreaEqual(before, target.Serialize(), report.SystemPhysicalTracks);
        Assert.Equal("KEEP", Assert.Single(target.FileSystem.ReadDirectory()).Name);
        Assert.Equal(sourceBefore, source.Serialize());
    }

    [Fact]
    public void MissingAndNonCanonicalSystemTracksCannotBeFingerprinted()
    {
        var source = SystemSource(false, 0x91);
        Assert.Throws<InvalidDataException>(() => CpmSystemFingerprint.HashSystemArea(source.Image, [1, 0]));
        Assert.Throws<InvalidDataException>(() => CpmSystemFingerprint.HashSystemArea(source.Image, [0, 0]));
        Assert.Throws<InvalidDataException>(() => CpmSystemFingerprint.HashSystemArea(source.Image, [999]));
        byte[] missingBytes = source.Serialize(); missingBytes[0x34 + 2] = 0;
        var missing = DskImage.Parse(missingBytes);
        Assert.Null(missing.Tracks[2]);
        Assert.Throws<InvalidDataException>(() => CpmSystemFingerprint.HashSystemArea(missing, [0, 1, 2, 3]));
    }

    [Fact]
    public void UserDirectoryErrorsRemainFatalForSystemSources()
    {
        var source = SystemSource(false, 0x89);
        source.FileSystem.Insert("BROKEN.COM", [1, 2, 3]); source.MarkModified();
        var dir = DskAnalyzer.Analyze(source).Sectors.First(sector => sector.Role.HasFlag(DskSectorRole.Directory));
        var bytes = source.Image.Tracks[dir.Track]!.Sectors[dir.PhysicalIndex].Data;
        bytes[12] = 0xFF; bytes[15] = 0xFF; source.MarkModified();
        source = DskDocument.Open(source.Serialize());
        var target = DskDocumentFactory.CreateCpm(false); byte[] before = target.Serialize();
        Assert.False(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
        Assert.Throws<InvalidDataException>(() => DskBootSystemService.InstallWithReport(target, source));
        Assert.Equal(before, target.Serialize()); Assert.False(target.IsModified);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildCopiesOnlySystemAreaAndProducesExactReport(bool highDensity)
    {
        CpmLayoutId layout = highDensity ? CpmLayoutId.LecHd1440 : CpmLayoutId.LecDd720;
        DskDocument target = DskDocumentFactory.CreateCpm(highDensity);
        target.FileSystem.Insert("KEEP.BIN", Enumerable.Range(0, 9000).Select(i => (byte)(i * 31)).ToArray());
        target.MarkModified();
        DskDocument source = SystemSource(highDensity, 0x42);
        CpmSystemProfile profile = Profile(source, layout, CpmTransferMode.Polling);
        byte[] original = target.Serialize();

        CpmSystemBuildResult result = CpmSystemBuilder.Build(target, source, profile);

        Assert.Equal(original, target.Serialize());
        Assert.True(target.IsModified);
        Assert.NotEmpty(result.Report.ChangedRanges);
        Assert.Equal(Sha(result.ImageBytes), result.Report.ResultSha256);
        Assert.Contains("Changed byte ranges", result.Report.ToText());
        DskDocument built = DskDocument.Open(result.ImageBytes);
        foreach (int track in profile.SystemPhysicalTracks)
            Assert.Equal(source.Image.Tracks[track]!.Sectors.SelectMany(s => s.Data),
                built.Image.Tracks[track]!.Sectors.SelectMany(s => s.Data));
        Assert.Equal(target.FileSystem.Extract(target.FileSystem.ReadDirectory().Single()),
            built.FileSystem.Extract(built.FileSystem.ReadDirectory().Single()));
        AssertOutsideSystemAreaEqual(original, result.ImageBytes, profile.SystemPhysicalTracks);
    }

    [Fact]
    public void ApplyReplacesTargetOnlyAfterSuccessfulValidation()
    {
        DskDocument target = DskDocumentFactory.CreateCpm(false);
        DskDocument source = SystemSource(false, 0x51);
        CpmSystemProfile profile = Profile(source, CpmLayoutId.LecDd720, CpmTransferMode.Polling);

        CpmSystemBuildReport report = CpmSystemBuilder.Apply(target, source, profile);

        Assert.True(target.IsModified);
        Assert.Equal(report.ResultSha256, Sha(target.Serialize()));
        Assert.True(DskBootInfo.Inspect(target).HasSystemBytes);
    }

    [Fact]
    public void WrongSourceHashIsRejectedWithoutChangingTarget()
    {
        DskDocument target = DskDocumentFactory.CreateCpm(false);
        DskDocument source = SystemSource(false, 0x61);
        CpmSystemProfile profile = Profile(source, CpmLayoutId.LecDd720, CpmTransferMode.Polling);
        DskDocument changedSource = SystemSource(false, 0x62);
        byte[] before = target.Serialize();

        CpmSystemBuildPreflight preflight = CpmSystemBuilder.Preflight(target, changedSource, profile);

        Assert.False(preflight.CanBuild);
        Assert.Contains(preflight.Errors, error => error.Contains("SHA-256"));
        Assert.Throws<InvalidDataException>(() => CpmSystemBuilder.Apply(target, changedSource, profile));
        Assert.Equal(before, target.Serialize());
        Assert.False(target.IsModified);
    }

    [Fact]
    public void WrongLayoutIsRejectedWithoutChangingTarget()
    {
        DskDocument target = DskDocumentFactory.CreateCpm(true);
        DskDocument source = SystemSource(false, 0x71);
        CpmSystemProfile profile = Profile(source, CpmLayoutId.LecDd720, CpmTransferMode.Polling);
        byte[] before = target.Serialize();

        Assert.False(CpmSystemBuilder.Preflight(target, source, profile).CanBuild);
        Assert.Throws<InvalidDataException>(() => CpmSystemBuilder.Apply(target, source, profile));
        Assert.Equal(before, target.Serialize());
    }

    [Fact]
    public void RegisteredTemplatesCoverPollingAndIrqForDdAndHd()
    {
        var templates = CpmSystemProfileRegistry.VerifiedTemplates;
        Assert.Contains(templates, p => p.Layout == CpmLayoutId.LecDd720 && p.TransferMode == CpmTransferMode.Polling);
        Assert.Contains(templates, p => p.Layout == CpmLayoutId.LecDd720 && p.TransferMode == CpmTransferMode.Irq);
        Assert.Contains(templates, p => p.Layout == CpmLayoutId.LecHd1440 && p.TransferMode == CpmTransferMode.Polling);
        Assert.Contains(templates, p => p.Layout == CpmLayoutId.LecHd1440 && p.TransferMode == CpmTransferMode.Irq);
        Assert.Equal(templates.Count, templates.Select(p => p.Fingerprint.SystemAreaSha256).Distinct().Count());
        Assert.All(templates, template => Assert.Equal(CpmRuntimeVerification.StaticValidated, template.Verification));
    }

    [Fact]
    public void DataOnlySourceIsRejected()
    {
        DskDocument source = DskDocumentFactory.CreateCpm(false);
        var definition = new CpmSystemTemplateDefinition("test", "test", CpmSystemFingerprint.From(source),
            CpmTransferMode.Polling, BootLoaderKind.CustomVerified, CpmRuntimeVerification.StaticValidated);
        Assert.Throws<InvalidDataException>(() => CpmSystemProfileRegistry.CreateProfile(definition, source));
    }

    [Fact]
    public void UnsupportedBrandingChangeIsRejected()
    {
        DskDocument target = DskDocumentFactory.CreateCpm(false);
        DskDocument source = SystemSource(false, 0x73);
        CpmSystemProfile profile = Profile(source, CpmLayoutId.LecDd720, CpmTransferMode.Polling);

        CpmSystemBuildPreflight preflight = CpmSystemBuilder.Preflight(target, source, profile,
            new CpmSystemBuildOptions(SystemBrandingPolicy.None));

        Assert.False(preflight.CanBuild);
        Assert.Contains(preflight.Errors, error => error.Contains("branding"));
    }

    [Fact]
    public void InstallUsesStandardSaveAndSaveAsAndNeverSavesAutomatically()
    {
        DskDocument target = DskDocumentFactory.CreateCpm(false);
        DskDocument source = SystemSource(false, 0x75);
        CpmSystemProfile profile = Profile(source, CpmLayoutId.LecDd720, CpmTransferMode.Polling);
        CpmSystemBuildResult result = CpmSystemBuilder.Build(target, source, profile);
        string directory = Path.Combine(Path.GetTempPath(), "mztools-system-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "built.dsk");
            target.Save(path);
            Assert.False(target.IsModified);
            byte[] stored = File.ReadAllBytes(path);
            CpmSystemBuilder.Apply(target, source, profile);
            Assert.True(target.IsModified); Assert.Equal(stored, File.ReadAllBytes(path));
            target.Save(); Assert.False(target.IsModified);
            Assert.Equal(result.ImageBytes, File.ReadAllBytes(path));
            string copyPath = Path.Combine(directory, "copy.dsk"); target.Save(copyPath);
            Assert.Equal(result.ImageBytes, File.ReadAllBytes(copyPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static void ExerciseDialogOnCurrentStaThread()
    {
        DskDocument target = DskDocumentFactory.CreateCpm(false);
        DskDocument source = SystemSource(false, 0x76);
        CpmSystemProfile profile = Profile(source, CpmLayoutId.LecDd720, CpmTransferMode.Polling);
        var dialog = new CpmSystemBuilderDialog(null, target);
        dialog.SetSourceForTesting(source, profile);
        Assert.True(dialog.CanInstall);
        Assert.Contains("Ready:", dialog.ReportText);
        Assert.Contains("Registered", dialog.IdentificationText);
        var dialogType = typeof(CpmSystemBuilderDialog);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Assert.Null(dialogType.GetField("sourceMode", flags)); Assert.Null(dialogType.GetField("build", flags));
        dialog.SetCompatibleSourceForTesting(source);
        Assert.True(dialog.CanInstall);
        Assert.Contains("Unverified", dialog.IdentificationText);
        Assert.Contains("Ready to install", dialog.ReportText);
        var foreign = SystemSource(false, 0x8B);
        // Known -> foreign in the same dialog must forget the previous profile/hash.
        dialog.SetSourceForTesting(source, profile);
        dialog.LoadSource(foreign);
        Assert.True(dialog.CanInstall); Assert.Contains("Unverified", dialog.IdentificationText);
        Assert.DoesNotContain("Test verified system", dialog.IdentificationText);
        byte[] beforeFailure = target.Serialize();
        dialog.RejectSource("broken.dsk", "Parse failure");
        Assert.False(dialog.CanInstall);
        Assert.Throws<InvalidOperationException>(() => dialog.InstallCurrentSource());
        Assert.Equal(beforeFailure, target.Serialize());
        dialog.LoadSource(foreign); Assert.True(dialog.CanInstall);
        int applied = 0; dialog.SystemInstalled += (_, _) => applied++;
        dialog.InstallCurrentSource(); Assert.Equal(1, applied); Assert.True(target.IsModified);
        Assert.Contains("Transfer mode: Unverified", dialog.ReportText);
        Assert.Equal(foreign.Image.Tracks[1]!.Sectors.SelectMany(s => s.Data), target.Image.Tracks[1]!.Sectors.SelectMany(s => s.Data));
        dialog.SetSourceForTesting(source, profile); Assert.True(dialog.CanInstall); Assert.Contains("Registered", dialog.IdentificationText);
        dialog.LoadSource(DskDocumentFactory.CreateCpm(false)); Assert.False(dialog.CanInstall);
        dialog.SetCompatibleSourceForTesting(DskDocumentFactory.CreateCpm(true));
        Assert.False(dialog.CanInstall);
        dialog.Close();
        var personal = new CpmSystemBuilderDialog(null, DskDocumentFactory.CreatePersonalCpm80(false));
        Assert.False(personal.CanInstall); personal.Close();
        if (File.Exists(PersonalCpmSystemTests.FullReference))
        {
            var nativeTarget = DskDocumentFactory.CreatePersonalCpm80(false);
            nativeTarget.FileSystem.Insert("UIKEEP.COM", new byte[128]); nativeTarget.MarkModified();
            var nativeDialog = new CpmSystemBuilderDialog(null, nativeTarget);
            var nativeSource = DskDocument.Open(PersonalCpmSystemTests.FullReference);
            nativeDialog.LoadSource(nativeSource);
            Assert.True(nativeDialog.CanInstall);
            Assert.Contains("Registered exact IPL + system-file match", nativeDialog.IdentificationText);
            Assert.Contains("System storage: PCPM.SYS", nativeDialog.IdentificationText);
            Assert.Contains("14848 B", nativeDialog.IdentificationText);
            Assert.Contains("UIKEEP.COM", nativeDialog.ReportText);
            Assert.Contains("entry #0", nativeDialog.ReportText);
            var sector = DskAnalyzer.Analyze(nativeSource).Sectors.First(s => s.Files == "PCPM.SYS");
            nativeSource.Image.Tracks[sector.Track]!.Sectors[sector.PhysicalIndex].Data[0] ^= 1;
            nativeSource.MarkModified();
            nativeDialog.LoadSource(nativeSource);
            Assert.True(nativeDialog.CanInstall); Assert.Contains("Unverified", nativeDialog.IdentificationText);
            Assert.DoesNotContain("Registered", nativeDialog.IdentificationText);
            nativeDialog.LoadSource(SystemSource(false, 0x8B)); Assert.False(nativeDialog.CanInstall);
            nativeDialog.LoadSource(DskDocument.Open(PersonalCpmSystemTests.FullReference)); Assert.True(nativeDialog.CanInstall);
            nativeDialog.InstallCurrentSource(); Assert.Contains("BootTrackPlusSystemFile", nativeDialog.ReportText);
            Assert.Equal(2, nativeTarget.FileSystem.ReadDirectory().Count);
            Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(nativeTarget));
            Assert.Contains("PCPM.SYS directory slot: 0", nativeDialog.ReportText);
            Assert.Contains("Previous slot 0 entry relocated: yes", nativeDialog.ReportText);
            Assert.Equal(PersonalCpmSystemInstaller.ProfileId, CpmSystemProfileRegistry.ResolveVerifiedSource(nativeTarget).Id);
            nativeDialog.Close();
            var fullTarget = DskDocumentFactory.CreatePersonalCpm80(false);
            for (int index = 0; index < 64; index++) fullTarget.FileSystem.Insert($"F{index}.COM", new byte[128]);
            fullTarget.MarkModified();
            var fullDialog = new CpmSystemBuilderDialog(null, fullTarget);
            byte[] fullBefore = fullTarget.Serialize();
            fullDialog.LoadSource(DskDocument.Open(PersonalCpmSystemTests.FullReference));
            Assert.False(fullDialog.CanInstall); Assert.Contains("no free directory entry", fullDialog.ReportText);
            Assert.Equal(fullBefore, fullTarget.Serialize()); fullDialog.Close();
        }

        if (File.Exists(ReferencePath(1)))
        {
            var referenceTarget = DskDocumentFactory.CreateCpm(false);
            var referenceDialog = new CpmSystemBuilderDialog(null, referenceTarget);
            referenceDialog.LoadSource(DskDocument.Open(ReferencePath(1))); Assert.True(referenceDialog.CanInstall);
            Assert.Contains("Registered exact system-area match", referenceDialog.IdentificationText);
            referenceDialog.LoadSource(SystemSource(false, 0x8B)); Assert.True(referenceDialog.CanInstall);
            Assert.Contains("Unverified", referenceDialog.IdentificationText);
            Assert.DoesNotContain("CP/M 4.1", referenceDialog.IdentificationText);
            referenceDialog.InstallCurrentSource(); Assert.Contains("Transfer mode: Unverified", referenceDialog.ReportText);
            referenceDialog.Close();
        }

        var newDialogType = typeof(DskNewDialog);
        var newDialog = Activator.CreateInstance(newDialogType, flags, null, [null], null)!;
        object NewField(string name) => newDialogType.GetField(name, flags)!.GetValue(newDialog)!;
        var formatBox = NewField("formatBox");
        Assert.Null(newDialogType.GetField("bootBox", flags)); Assert.Null(newDialogType.GetField("bootRow", flags));
        Assert.Null(newDialogType.GetField("bootPathBox", flags)); Assert.Null(newDialogType.GetField("bootBrowseButton", flags));
        for (int format = 4; format <= 7; format++)
        {
            formatBox.GetType().GetProperty("SelectedIndex")!.SetValue(formatBox, format);
            Assert.Equal(format, formatBox.GetType().GetProperty("SelectedIndex")!.GetValue(formatBox));
        }
        formatBox.GetType().GetProperty("SelectedIndex")!.SetValue(formatBox, 8);
        Assert.Equal(8, formatBox.GetType().GetProperty("SelectedIndex")!.GetValue(formatBox));
        newDialogType.GetMethod("Close")!.Invoke(newDialog, null);
    }

    private static DskDocument SystemSource(bool highDensity, byte marker)
    {
        DskDocument blank = DskDocumentFactory.CreateCpm(highDensity);
        byte[] bytes = blank.Serialize();
        DskImage image = DskImage.Parse(bytes);
        DskImage.DskTrack boot = image.Tracks[1]!;
        boot.Sectors[0].Data[128] = marker;
        boot.Sectors[1].Data[0] = (byte)(marker ^ 0xFF);
        return DskDocument.Open(image.Serialize());
    }

    private static CpmSystemProfile Profile(DskDocument source, CpmLayoutId layout, CpmTransferMode mode)
    {
        var definition = new CpmSystemTemplateDefinition("test", "Test verified system", CpmSystemFingerprint.From(source) with { Layout = layout }, mode,
            BootLoaderKind.CustomVerified, CpmRuntimeVerification.StaticValidated, Sha(source.Serialize()));
        return CpmSystemProfileRegistry.CreateProfile(definition, source);
    }

    private static void AssertOutsideSystemAreaEqual(byte[] beforeBytes, byte[] afterBytes, IReadOnlyList<int> systemTracks)
    {
        DskImage before = DskImage.Parse(beforeBytes);
        DskImage after = DskImage.Parse(afterBytes);
        for (int track = 0; track < before.Tracks.Count; track++)
        {
            if (systemTracks.Contains(track)) continue;
            Assert.Equal(before.Tracks[track]!.Sectors.SelectMany(s => s.Data),
                after.Tracks[track]!.Sectors.SelectMany(s => s.Data));
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
