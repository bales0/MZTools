using System.Security.Cryptography;

namespace MZTools.Tests;

public sealed class CpmSystemBuilderTests
{
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
        Assert.Equal(templates.Count, templates.Select(p => p.Sha256).Distinct().Count());
    }

    [Fact]
    public void DataOnlySourceIsRejected()
    {
        DskDocument source = DskDocumentFactory.CreateCpm(false);
        var definition = new CpmSystemTemplateDefinition("test", "test", CpmLayoutId.LecDd720,
            CpmTransferMode.Polling, BootLoaderKind.CustomVerified, Sha(source.Serialize()));
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
    public void SaveValidatedResultWritesExactImageAndRejectsTamperedReport()
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
            CpmSystemBuilder.SaveValidatedResult(result, path);
            Assert.Equal(result.ImageBytes, File.ReadAllBytes(path));
            CpmSystemBuildResult tampered = result with { ImageBytes = (byte[])result.ImageBytes.Clone() };
            tampered.ImageBytes[^1] ^= 1;
            Assert.Throws<InvalidDataException>(() => CpmSystemBuilder.SaveValidatedResult(tampered, Path.Combine(directory, "bad.dsk")));
            Assert.False(File.Exists(Path.Combine(directory, "bad.dsk")));
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
        Assert.True(dialog.CanBuild);
        Assert.True(dialog.CanInstall);
        Assert.Contains("Ready:", dialog.ReportText);
        dialog.SetCompatibleSourceForTesting(source);
        Assert.True(dialog.CanInstall);
        Assert.False(dialog.CanBuild);
        Assert.Contains("Ready to install", dialog.ReportText);
        dialog.SetCompatibleSourceForTesting(DskDocumentFactory.CreateCpm(true));
        Assert.False(dialog.CanInstall);
        dialog.Close();
        var personal = new CpmSystemBuilderDialog(null, DskDocumentFactory.CreatePersonalCpm80(false));
        Assert.False(personal.CanBuild); Assert.False(personal.CanInstall); personal.Close();
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
        var definition = new CpmSystemTemplateDefinition("test", "Test verified system", layout, mode,
            BootLoaderKind.CustomVerified, Sha(source.Serialize()));
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
