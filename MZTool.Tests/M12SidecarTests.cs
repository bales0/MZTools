namespace MZTools.Tests;

public sealed class M12SidecarTests : IDisposable
{
    private readonly string directory = TapeTestData.CreateTempDirectory();
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);
    private string Tape(string name, byte[]? trailing = null)
    {
        string path = PathFor(name);
        File.WriteAllBytes(path, TapeTestData.CreateMzf([1, 2, 3], trailing: trailing));
        return path;
    }

    [Theory]
    [InlineData("game.m12", "game.m2i")]
    [InlineData("GAME.M12", "GAME.m2i")]
    [InlineData("game.mzf", "game.mfi")]
    [InlineData("game.mzt", "game.mti")]
    public void CompanionMappingIsExact(string main, string sidecar) =>
        Assert.Equal(PathFor(sidecar), SidecarService.GetSidecarPath(PathFor(main)));

    [Fact]
    public void SameBasenameM12AndMzfReadOnlyTheirOwnSidecars()
    {
        string m12 = Tape("GAME.M12"), mzf = Tape("GAME.MZF");
        File.WriteAllText(PathFor("GAME.m2i"), "TYPE=IC\r\nSPEED=1:2\r\n"); // sd2cmt2 EXAMPLE.M2I
        File.WriteAllText(PathFor("GAME.mfi"), "TYPE=TC\nSPEED=1:3\n");
        var record12 = new MZTFileReader().ReadStandaloneMzf(m12);
        var recordF = new MZTFileReader().ReadStandaloneMzf(mzf);
        Assert.Equal(PathFor("GAME.m2i"), SidecarService.LoadForMzf(m12, record12));
        Assert.Equal(PathFor("GAME.mfi"), SidecarService.LoadForMzf(mzf, recordF));
        Assert.Equal(TapeProfile.Ic1_2, record12.Profile); Assert.Equal(MetadataOrigin.LoadedFromM2i, record12.MetadataOrigin);
        Assert.Equal(TapeProfile.Tc1_3, recordF.Profile); Assert.Equal(MetadataOrigin.LoadedFromMfi, recordF.MetadataOrigin);
        Assert.Equal(new byte[] { 1, 2, 3 }, record12.Body.MzfBody);
    }

    [Theory]
    [InlineData("m12", "mfi")]
    [InlineData("mzf", "m2i")]
    public void MissingCompanionNeverFallsBackToTheOtherFormat(string extension, string other)
    {
        string path = Tape("GAME." + extension);
        File.WriteAllText(PathFor("GAME." + other), "TYPE=IC\nSPEED=1:2\n");
        var record = new MZTFileReader().ReadStandaloneMzf(path);
        Assert.Null(SidecarService.LoadForMzf(path, record));
        Assert.Equal(TapeProfile.Normal1_1, record.Profile); Assert.Equal(MetadataOrigin.Implicit, record.MetadataOrigin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TYPE=TC\nSPEED=1:4\n")]
    [InlineData("TYPE=UNKNOWN\nSPEED=1:2\n")]
    public void InvalidM2iRetainsNormalDefaultAndDoesNotConsumeMfi(string content)
    {
        string path = Tape("game.m12"); File.WriteAllText(PathFor("game.m2i"), content);
        File.WriteAllText(PathFor("game.mfi"), "TYPE=IC\nSPEED=1:2\n");
        var record = new MZTFileReader().ReadStandaloneMzf(path);
        Assert.Equal(PathFor("game.m2i"), SidecarService.LoadForMzf(path, record));
        Assert.Equal(TapeProfile.Normal1_1, record.Profile); Assert.Equal(MetadataOrigin.Implicit, record.MetadataOrigin);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void M12SavePreservesSelectedTrailingPolicyAndRegeneratesOnlyM2i(bool preserve)
    {
        string path = Tape("game.m12", [0xAA, 0x55]); byte[] original = File.ReadAllBytes(path);
        var record = new MZTFileReader().ReadStandaloneMzf(path); record.Profile = TapeProfile.UltraMz800;
        File.WriteAllText(PathFor("game.m2i"), "stale"); File.WriteAllText(PathFor("game.mfi"), "TYPE=IC\nSPEED=1:2\n");
        TapeDocumentWriter.SaveMzf(path, record, preserve);
        Assert.Equal(preserve ? original : original[..131], File.ReadAllBytes(path));
        Assert.Equal("TYPE=UL_MZ800\n", File.ReadAllText(PathFor("game.m2i")).Replace("\r", ""));
        Assert.Equal("TYPE=IC\nSPEED=1:2\n", File.ReadAllText(PathFor("game.mfi")));
        Assert.Equal(MetadataOrigin.LoadedFromM2i, record.MetadataOrigin);
        Assert.Equal(preserve ? 2 : 0, record.Body.TrailingData.Length);
        Assert.False(File.Exists(PathFor("game.mzf")));
    }

    [Fact]
    public void M12SaveAsDoesNotCopyOldSidecarBindingAndExplicitGenerationUsesM2i()
    {
        string source = Tape("source.m12"); File.WriteAllText(PathFor("source.m2i"), "TYPE=IC\nSPEED=1:2\n");
        var record = new MZTFileReader().ReadStandaloneMzf(source); SidecarService.LoadForMzf(source, record);
        string target = PathFor("target.m12"); TapeDocumentWriter.SaveMzf(target, record, true);
        Assert.False(File.Exists(PathFor("target.m2i"))); Assert.Equal(TapeProfile.Normal1_1, record.Profile);
        record.Profile = TapeProfile.Tc1_2;
        TapeDocumentWriter.SaveMzf(target, record, true, createSidecar: true);
        Assert.Equal("TYPE=TC\nSPEED=1:2\n", File.ReadAllText(PathFor("target.m2i")).Replace("\r", ""));
        Assert.Equal(MetadataOrigin.LoadedFromM2i, record.MetadataOrigin);
        Assert.Equal("TYPE=IC\nSPEED=1:2\n", File.ReadAllText(PathFor("source.m2i")));
    }

    [Fact]
    public void GenerateM2iLeavesMainBytesUnchangedAndRejectsWrongDocumentShape()
    {
        string path = Tape("game.m12"); byte[] original = File.ReadAllBytes(path);
        var record = new MZTFileReader().ReadStandaloneMzf(path); record.Profile = TapeProfile.Ic1_2;
        Assert.Equal(PathFor("game.m2i"), TapeDocumentWriter.GenerateSidecar(path, TapeDocumentFormat.M12, [record]));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("TYPE=IC\nSPEED=1:2\n", File.ReadAllText(PathFor("game.m2i")).Replace("\r", ""));
        Assert.Equal(MetadataOrigin.LoadedFromM2i, record.MetadataOrigin);
        Assert.Throws<InvalidOperationException>(() => TapeDocumentWriter.GenerateSidecar(path, TapeDocumentFormat.M12, [record, record]));
        Assert.Throws<ArgumentException>(() => TapeDocumentWriter.GenerateSidecar(path, TapeDocumentFormat.Mzf, [record]));
        Assert.False(File.Exists(PathFor("game.mfi")));
    }

    [Fact]
    public void FailedM2iInstallationRollsBackM12AndLeavesRecordUnchanged()
    {
        string path = Tape("game.m12", [0xAA]); byte[] original = File.ReadAllBytes(path);
        var record = new MZTFileReader().ReadStandaloneMzf(path); record.Profile = TapeProfile.Ic1_2;
        record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
        record.Body.MzfBody[0] = 0x42;
        Directory.CreateDirectory(PathFor("game.m2i")); // Force sidecar install failure after main-file install.
        Assert.ThrowsAny<IOException>(() => TapeDocumentWriter.SaveMzf(path, record, false, createSidecar: true));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(TapeProfile.Ic1_2, record.Profile); Assert.Equal(MetadataOrigin.CreatedOrModifiedInAdvanced, record.MetadataOrigin);
        Assert.Equal(new byte[] { 0xAA }, record.Body.TrailingData);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory), p => p.EndsWith(".tmp") || p.EndsWith(".bak"));
    }

    [Fact]
    public void DialogFiltersRetainExistingIndicesAndSelectM12AsItsOwnFormat()
    {
        Assert.Contains("*.m12", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("*.m12", FeatureModePolicy.GetExportFilter());
        Assert.Contains("Reconstructed M12 from prepared IPL record (*.m12)|*.m12", FeatureModePolicy.GetIplExportFilter());
        int index = MainWindow.GetSaveFilterIndex(TapeDocumentFormat.M12);
        string[] entries = FeatureModePolicy.GetSaveFilter().Split('|');
        Assert.Equal("*.m12", entries[index * 2 - 1]);
        Assert.Equal(TapeDocumentFormat.M12, MainWindow.ResolveSaveFormat(".m12", index));
        Assert.Equal(7, MainWindow.GetSaveFilterIndex(TapeDocumentFormat.Mzf));
        Assert.Equal(TapeDocumentFormat.QdHxc, MainWindow.ResolveSaveFormat(".qd", 3));
        Assert.False(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.M12));
    }

    [Fact]
    public void ReconstructedIplRecordExportsToM12WithTheSameBytesAsMzf()
    {
        var original = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2, 3]));
        var reconstructed = Mz800IplDskReader.Read(Mz800IplDskWriter.Build(original)).Record;
        byte[] expected = TapeDocumentWriter.SerializeMzf(reconstructed, preserveTrailing: true);
        string m12 = PathFor("ipl.m12"), mzf = PathFor("ipl.mzf");
        TapeDocumentWriter.SaveMzf(m12, reconstructed.DeepClone(), preserveTrailing: true);
        TapeDocumentWriter.SaveMzf(mzf, reconstructed.DeepClone(), preserveTrailing: true);
        Assert.Equal(expected, File.ReadAllBytes(m12));
        Assert.Equal(File.ReadAllBytes(mzf), File.ReadAllBytes(m12));
        Assert.Equal(reconstructed.Body.MzfBody, new MZTFileReader().ReadStandaloneMzf(m12).Body.MzfBody);
        Assert.False(File.Exists(PathFor("ipl.m2i")));
        Assert.All(SharpTapeExporter.GetSeparateOutputPaths(m12, [reconstructed, reconstructed]), path => Assert.EndsWith(".m12", path));
    }

    [Fact]
    public void M2iUsesEveryExistingSingleRecordProfileWithoutChangingTapeBytes()
    {
        string path = Tape("game.m12"); byte[] original = File.ReadAllBytes(path);
        foreach (TapeProfile profile in Enum.GetValues<TapeProfile>())
        {
            var record = new MZTFileReader().ReadStandaloneMzf(path); record.Profile = profile;
            TapeDocumentWriter.GenerateSidecar(path, TapeDocumentFormat.M12, [record]);
            var loaded = new MZTFileReader().ReadStandaloneMzf(path); SidecarService.LoadForMzf(path, loaded);
            Assert.Equal(profile, loaded.Profile); Assert.Equal(MetadataOrigin.LoadedFromM2i, loaded.MetadataOrigin);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

}
