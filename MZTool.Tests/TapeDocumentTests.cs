namespace MZTools.Tests;

public class TapeDocumentTests
{
    [Fact]
    public void ProfilePresentation_SeparatesRomDirectIcAndFastIplWithoutChangingKeysOrOrder()
    {
        Assert.Equal("NORMAL 1:1 (ROM)",TapeProfileNames.ToDisplayName(TapeProfile.Normal1_1));
        foreach(var profile in new[]{TapeProfile.Normal1_2,TapeProfile.Normal1_3,TapeProfile.Normal1_4})
        {
            Assert.Contains("IC NORMAL timing",TapeProfileNames.ToDisplayName(profile));
            Assert.Contains("no IC loader",TapeProfileNames.Description(profile));
            Assert.Contains("asymmetric HIGH/LOW",TapeProfileNames.Description(profile));
            Assert.Equal("NORMAL",TapeProfileComponents.Split(profile).LoaderType);
        }
        foreach(var profile in new[]{TapeProfile.Ic1_1,TapeProfile.Ic1_2,TapeProfile.Ic1_3,TapeProfile.Ic1_4})
        {
            Assert.StartsWith("IC FAST IPL ",TapeProfileNames.ToDisplayName(profile));
            Assert.Equal("IC",TapeProfileComponents.Split(profile).LoaderType);
        }
        Assert.Equal("IC FAST IPL",TapeProfileNames.LoaderDisplayName("IC"));
        Assert.Equal(new[]{"1:1","1:2","1:3","1:4"},TapeProfileComponents.GetAvailableSpeeds("IC"));
        Assert.Equal("TC 1:1",TapeProfileNames.ToDisplayName(TapeProfile.Tc1_1));
        Assert.Equal(new[]{0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15},Enum.GetValues<TapeProfile>().Select(p=>(int)p));
        Assert.Equal(14,(int)TapeProfile.Ic1_1); Assert.Equal(15,(int)TapeProfile.Tc1_1);
    }
    [Fact]
    public void StandaloneMzf_CapturesTrailingAndBasicSaveRemovesIt()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            byte[] body = [1, 2, 3];
            byte[] trailing = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            string source = Path.Combine(directory, "source.mzf");
            string target = Path.Combine(directory, "target.mzf");
            File.WriteAllBytes(source, TapeTestData.CreateMzf(body, trailing: trailing));

            TapeRecord record = new MZTFileReader().ReadStandaloneMzf(source);
            Assert.Equal(trailing, record.Body.TrailingData);

            TapeDocumentWriter.SaveMzf(target, record, preserveTrailing: false);

            Assert.Equal(128 + body.Length, new FileInfo(target).Length);
            Assert.Empty(record.Body.TrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void StandaloneMzf_AdvancedPreserveKeepsTrailingByteForByte()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            byte[] trailing = Enumerable.Range(0, 256).Select(value => (byte)(255 - value)).ToArray();
            string source = Path.Combine(directory, "source.mzf");
            string target = Path.Combine(directory, "target.mzf");
            byte[] expected = TapeTestData.CreateMzf([1, 2, 3], trailing: trailing);
            File.WriteAllBytes(source, expected);
            TapeRecord record = new MZTFileReader().ReadStandaloneMzf(source);

            TapeDocumentWriter.SaveMzf(target, record, preserveTrailing: true);

            Assert.Equal(expected, File.ReadAllBytes(target));
            Assert.Equal(trailing, record.Body.TrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void BinaryDescription_RoundTripsByteForByte()
    {
        byte[] description = Enumerable.Range(0, 104).Select(value => (byte)(value * 37)).ToArray();
        byte[] source = TapeTestData.CreateMzf([0xAA, 0x55], description);
        TapeRecord record = TapeTestData.ReadRecord(source);

        byte[] saved = TapeDocumentWriter.SerializeMzf(record, preserveTrailing: false);

        Assert.Equal(source, saved);
        Assert.Equal(description, saved[24..128]);
    }

    [Fact]
    public void Mzt_UsesDeclaredSizesAndSeparatesInvalidContainerTail()
    {
        byte[][] files =
        [
            TapeTestData.CreateMzf([1], name: "ONE"),
            TapeTestData.CreateMzf([2, 3, 4], name: "TWO"),
            TapeTestData.CreateMzf([5, 6, 7, 8, 9], name: "THREE")
        ];
        byte[] tail = [0xDE, 0xAD, 0xBE, 0xEF];
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "test.mzt");
            File.WriteAllBytes(path, files.SelectMany(bytes => bytes).Concat(tail).ToArray());

            MztReadResult result = new MZTFileReader().ReadMzt(path);

            Assert.Equal(new[] { 1, 3, 5 }, result.Records.Select(record => record.Body.MzfBody.Length));
            Assert.Equal(tail, result.ContainerTrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Mzt_AcceptsAFullSixteenByteNameWithNulInsteadOfCr()
    {
        byte[] first = TapeTestData.CreateMzf([1, 2], name: "FIRST");
        byte[] second = TapeTestData.CreateMzf([3, 4, 5], name: "SIXTEEN-CHAR-NAME");
        second[17] = 0x00;
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "full-name.mzt");
            File.WriteAllBytes(path, first.Concat(second).ToArray());

            MztReadResult result = new MZTFileReader().ReadMzt(path);

            Assert.Equal(2, result.Records.Count);
            Assert.Empty(result.ContainerTrailingData);
            Assert.Equal(new byte[] { 3, 4, 5 }, result.Records[1].Body.MzfBody);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MztSave_DropsPerRecordTrailingAndMoveCarriesProfile()
    {
        TapeRecord first = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1], name: "A"));
        TapeRecord second = TapeTestData.ReadRecord(TapeTestData.CreateMzf([2, 3], name: "B"));
        MZQFileBody body = first.Body;
        body.TrailingData = [9, 9, 9];
        first.Body = body;
        second.Profile = TapeProfile.Ic1_3;
        second.MetadataOrigin = MetadataOrigin.LoadedFromMti;
        var records = new List<TapeRecord> { first, second };
        records.RemoveAt(1);
        records.Insert(0, second);

        byte[] saved = TapeDocumentWriter.SerializeMzt(records);

        Assert.Equal(TapeProfile.Ic1_3, records[0].Profile);
        Assert.Equal(128 + 2 + 128 + 1, saved.Length);
        Assert.Equal("B", System.Text.Encoding.ASCII.GetString(saved, 1, 1));
    }

    [Fact]
    public void MztSave_RemovesTrailingFromCurrentRecordModel()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            TapeRecord record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2]));
            MZQFileBody body = record.Body;
            body.TrailingData = [3, 4, 5];
            record.Body = body;

            TapeDocumentWriter.SaveMzt(Path.Combine(directory, "saved.mzt"), [record]);

            Assert.Empty(record.Body.TrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FileFiltersExposeAllSupportedFormats()
    {
        string save = FeatureModePolicy.GetSaveFilter().ToLowerInvariant();
        string open = FeatureModePolicy.GetOpenFilter().ToLowerInvariant();
        string export = FeatureModePolicy.GetExportFilter().ToLowerInvariant();

        Assert.Contains(".wav", export);
        Assert.Contains(".l16", export);
        Assert.Contains(".lep", export);
        Assert.DoesNotContain(".wav", save);
        Assert.Contains("*.qd|", save);
        Assert.Contains(".wav", open);
        Assert.Contains(".flac", open);
        Assert.Contains(".l16", open);
        Assert.Contains(".lep", open);
    }

    [Fact]
    public void QdFilters_UseConsistentQuickdiskImageLabel()
    {
        Assert.Contains("Quickdisk image (*.qd)", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("Wave audio (*.wav)", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("FLAC audio (*.flac)", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("LEP pulse file (*.lep)", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("L16 pulse file (*.l16)", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("Quickdisk image - HxC (*.qd)", FeatureModePolicy.GetSaveFilter());
        Assert.Contains("Quickdisk image - FlashFloppy (*.qd)", FeatureModePolicy.GetSaveFilter());
        Assert.Contains("Quickdisk image - Sharp/MZ legacy (*.qd)", FeatureModePolicy.GetSaveFilter());
        Assert.DoesNotContain("QuickDisk", FeatureModePolicy.GetOpenFilter());
        Assert.DoesNotContain("QuickDisk", FeatureModePolicy.GetSaveFilter());
    }

    [Fact]
    public void TapeProfileComponents_RoundTripEverySupportedProfile()
    {
        foreach (TapeProfile profile in Enum.GetValues<TapeProfile>())
        {
            (string loaderType, string speed) = TapeProfileComponents.Split(profile);

            Assert.Contains(loaderType, TapeProfileComponents.LoaderTypes);
            Assert.Contains(speed, TapeProfileComponents.GetAvailableSpeeds(loaderType));
            Assert.Equal(profile, TapeProfileComponents.Combine(loaderType, speed));
        }
    }

    [Fact]
    public void TcOffersAllAnalyzedSpeeds()
    {
        Assert.Equal(
            new[] { "1:1", "1:2", "1:3" },
            TapeProfileComponents.GetAvailableSpeeds("TC"));
        Assert.Equal("1:1", TapeProfileComponents.NormalizeSpeed("TC", "1:4"));
        Assert.False(SidecarService.TryParseProfile(
            ["TYPE=TC", "SPEED=1:4"], out _));
    }

    [Theory]
    [InlineData("NORMAL", "1:4", "1:4")]
    [InlineData("MZ700", "1:2", "1:1")]
    [InlineData("IC", "1:1", "1:1")]
    [InlineData("UL_MZ800", "1:3", "")]
    public void TapeProfileComponents_NormalizesInvalidSpeeds(
        string loaderType,
        string requestedSpeed,
        string expectedSpeed)
    {
        Assert.Equal(expectedSpeed, TapeProfileComponents.NormalizeSpeed(loaderType, requestedSpeed));
    }
}
