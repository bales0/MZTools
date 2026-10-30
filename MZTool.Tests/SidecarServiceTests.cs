namespace MZTools.Tests;

public class SidecarServiceTests
{
    [Theory]
    [InlineData((int)TapeProfile.Normal1_1,"NORMAL","1:1")]
    [InlineData((int)TapeProfile.Normal1_2,"NORMAL","1:2")]
    [InlineData((int)TapeProfile.Normal1_3,"NORMAL","1:3")]
    [InlineData((int)TapeProfile.Normal1_4,"NORMAL","1:4")]
    [InlineData((int)TapeProfile.Ic1_1,"IC","1:1")]
    [InlineData((int)TapeProfile.Ic1_2,"IC","1:2")]
    [InlineData((int)TapeProfile.Ic1_3,"IC","1:3")]
    [InlineData((int)TapeProfile.Ic1_4,"IC","1:4")]
    [InlineData((int)TapeProfile.Tc1_1,"TC","1:1")]
    [InlineData((int)TapeProfile.Tc1_2,"TC","1:2")]
    [InlineData((int)TapeProfile.Tc1_3,"TC","1:3")]
    public void PresentationNames_DoNotChangeMfiM2iOrMtiProfileBytes(int profile,string type,string speed)
    {
        var record=CreateProfiledRecord((TapeProfile)profile);
        string canonical=$"TYPE={type}\nSPEED={speed}\n";
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(canonical),SidecarService.SerializeMfi(record));
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes("RECORD=1\n"+canonical),SidecarService.SerializeMti([record]));
        Assert.True(SidecarService.TryParseProfile(canonical.Split('\n'),out var parsed)); Assert.Equal(record.Profile,parsed);
        if(type=="IC") Assert.False(SidecarService.TryParseProfile(["TYPE=IC FAST IPL","SPEED="+speed],out _));
    }
    [Fact]
    public void SaveMzt_WithoutTargetMti_DoesNotCreateOneAndResetsMetadata()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string target = Path.Combine(directory, "new.mzt");
            TapeRecord record = CreateProfiledRecord(TapeProfile.Ic1_3);

            TapeDocumentWriter.SaveMzt(target, [record]);

            Assert.True(File.Exists(target));
            Assert.False(File.Exists(Path.ChangeExtension(target, ".mti")));
            Assert.Equal(TapeProfile.Normal1_1, record.Profile);
            Assert.Equal(MetadataOrigin.Implicit, record.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SaveMzt_WithOrphanTargetMti_RegeneratesItCompletelyInCurrentOrder()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string target = Path.Combine(directory, "new.mzt");
            string mti = Path.ChangeExtension(target, ".mti");
            File.WriteAllText(mti, string.Join('\n', Enumerable.Range(1, 7).Select(number => $"RECORD={number}\nTYPE=UL\n")));
            TapeRecord a = CreateProfiledRecord(TapeProfile.Normal1_2, "A");
            TapeRecord b = CreateProfiledRecord(TapeProfile.Tc1_2, "B");

            TapeDocumentWriter.SaveMzt(target, [b, a]);

            string text = File.ReadAllText(mti);
            Assert.Equal(2, text.Split("RECORD=", StringSplitOptions.None).Length - 1);
            Assert.StartsWith("RECORD=1\nTYPE=TC\nSPEED=1:2", text.Replace("\r", ""));
            Assert.Contains("RECORD=2\nTYPE=NORMAL\nSPEED=1:2", text.Replace("\r", ""));
            Assert.Equal(MetadataOrigin.LoadedFromMti, a.MetadataOrigin);
            Assert.Equal(MetadataOrigin.LoadedFromMti, b.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SaveAsNewMzt_DoesNotCopyOldBindingOrCreateMissingMti()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string oldMti = Path.Combine(directory, "old.mti");
            string target = Path.Combine(directory, "backup.mzt");
            File.WriteAllText(oldMti, "RECORD=1\nTYPE=IC\nSPEED=1:3\n");
            TapeRecord record = CreateProfiledRecord(TapeProfile.Ic1_3);

            TapeDocumentWriter.SaveMzt(target, [record]);

            Assert.Equal("RECORD=1\nTYPE=IC\nSPEED=1:3\n", File.ReadAllText(oldMti).Replace("\r", ""));
            Assert.False(File.Exists(Path.ChangeExtension(target, ".mti")));
            Assert.Equal(MetadataOrigin.Implicit, record.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Mfi_LoadsAndExistingTargetMfiIsAlwaysSynchronized()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string source = Path.Combine(directory, "source.mzf");
            string sourceMfi = Path.ChangeExtension(source, ".mfi");
            File.WriteAllBytes(source, TapeTestData.CreateMzf([1, 2, 3]));
            File.WriteAllText(sourceMfi, "TYPE=IC\nSPEED=1:3\n");
            TapeRecord record = new MZTFileReader().ReadStandaloneMzf(source);

            string? binding = SidecarService.LoadForMzf(source, record);
            Assert.Equal(sourceMfi, binding, ignoreCase: true);
            Assert.Equal(TapeProfile.Ic1_3, record.Profile);
            Assert.Equal(MetadataOrigin.LoadedFromMfi, record.MetadataOrigin);

            string target = Path.Combine(directory, "target.mzf");
            string targetMfi = Path.ChangeExtension(target, ".mfi");
            File.WriteAllText(targetMfi, "TYPE=UL\n");
            record.Profile = TapeProfile.Normal1_3;
            TapeDocumentWriter.SaveMzf(target, record, preserveTrailing: false);

            Assert.Equal("TYPE=NORMAL\nSPEED=1:3\n", File.ReadAllText(targetMfi).Replace("\r", ""));
            Assert.Equal(MetadataOrigin.LoadedFromMfi, record.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SaveMzf_WithoutTargetMfiDoesNotCreateOne()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string target = Path.Combine(directory, "plain.mzf");
            TapeRecord record = CreateProfiledRecord(TapeProfile.Tc1_3);

            TapeDocumentWriter.SaveMzf(target, record, preserveTrailing: false);

            Assert.True(File.Exists(target));
            Assert.False(File.Exists(Path.ChangeExtension(target, ".mfi")));
            Assert.Equal(TapeProfile.Normal1_1, record.Profile);
            Assert.Equal(MetadataOrigin.Implicit, record.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Mti_LoadsProfilesByOneBasedRecordNumber()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string mzt = Path.Combine(directory, "game.mzt");
            string mti = Path.ChangeExtension(mzt, ".mti");
            File.WriteAllText(mti, "RECORD=1\nTYPE=NORMAL\nSPEED=1:2\n\nRECORD=2\nTYPE=TC\nSPEED=1:3\n");
            TapeRecord first = CreateProfiledRecord(TapeProfile.Normal1_1);
            TapeRecord second = CreateProfiledRecord(TapeProfile.Normal1_1);

            string? binding = SidecarService.LoadForMzt(mzt, [first, second]);

            Assert.Equal(mti, binding, ignoreCase: true);
            Assert.Equal(TapeProfile.Normal1_2, first.Profile);
            Assert.Equal(TapeProfile.Tc1_3, second.Profile);
            Assert.Equal(MetadataOrigin.LoadedFromMti, first.MetadataOrigin);
            Assert.Equal(MetadataOrigin.LoadedFromMti, second.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GenerateSidecar_CreatesMfiWithoutRewritingTheMzf()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string mzf = Path.Combine(directory, "game.mzf");
            byte[] mainFile = TapeTestData.CreateMzf([1, 2, 3]);
            File.WriteAllBytes(mzf, mainFile);
            TapeRecord record = CreateProfiledRecord(TapeProfile.UltraMz800);

            string sidecar = TapeDocumentWriter.GenerateSidecar(mzf, TapeDocumentFormat.Mzf, [record]);

            Assert.Equal(Path.ChangeExtension(mzf, ".mfi"), sidecar, ignoreCase: true);
            Assert.Equal(mainFile, File.ReadAllBytes(mzf));
            Assert.Equal("TYPE=UL_MZ800\n", File.ReadAllText(sidecar).Replace("\r", ""));
            Assert.Equal(MetadataOrigin.LoadedFromMfi, record.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GenerateSidecar_CreatesCompleteMtiInCurrentOrder()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string mzt = Path.Combine(directory, "collection.mzt");
            TapeRecord first = CreateProfiledRecord(TapeProfile.Tc1_2, "FIRST");
            TapeRecord second = CreateProfiledRecord(TapeProfile.Normal1_4, "SECOND");
            File.WriteAllBytes(mzt, TapeDocumentWriter.SerializeMzt([first, second]));

            string sidecar = TapeDocumentWriter.GenerateSidecar(
                mzt,
                TapeDocumentFormat.Mzt,
                [second, first]);

            string text = File.ReadAllText(sidecar).Replace("\r", "");
            Assert.StartsWith("RECORD=1\nTYPE=NORMAL\nSPEED=1:4", text);
            Assert.Contains("RECORD=2\nTYPE=TC\nSPEED=1:2", text);
            Assert.Equal(MetadataOrigin.LoadedFromMti, first.MetadataOrigin);
            Assert.Equal(MetadataOrigin.LoadedFromMti, second.MetadataOrigin);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static TapeRecord CreateProfiledRecord(TapeProfile profile, string name = "TEST")
    {
        TapeRecord record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2], name: name));
        record.Profile = profile;
        record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
        return record;
    }
}
