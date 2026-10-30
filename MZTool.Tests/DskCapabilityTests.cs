namespace MZTools.Tests;

public class DskCapabilityTests
{
    private static DskDocument Cpm(int layout) => layout switch
    {
        0 => DskDocumentFactory.CreatePersonalCpm80(false),
        1 => DskDocumentFactory.CreatePersonalCpm80(true),
        2 => DskDocumentFactory.CreateCpm(false),
        _ => DskDocumentFactory.CreateCpm(true)
    };

    private static DskDocument BootOnly()
    {
        var image = Mz800DskImage.CreateModel("boot-only");
        var boot = new byte[256];
        boot[0] = 3; "IPLPRO"u8.CopyTo(boot.AsSpan(1));
        Mz800DskImage.WriteLogicalBlock(image, 0, boot);
        return DskDocument.Open(image.Serialize());
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CpmProfilesAndInstallRetainEverythingOutsideSystemSectorPayloads(int layout)
    {
        var target = Cpm(layout);
        var source = Cpm(layout);
        target.FileSystem.Insert("ONE.BIN", Enumerable.Range(0, 17000).Select(i => (byte)i).ToArray(), user: 2);
        target.FileSystem.Insert("TWO.COM", [1, 2, 3, 4]);
        target.MarkModified();
        var profile = Assert.Single(DskCapabilityService.GetAvailableBootSystems(target));
        Assert.Equal(DskFileSystemType.Cpm, profile.CompatibleFileSystem);
        Assert.Contains("source DSK", profile.DisplayName);
        Assert.True(DskCapabilityService.CanInstallBootSystem(target, profile).IsCompatible);
        foreach (int t in profile.RequiredSystemTracks)
            foreach (var sector in source.Image.Tracks[t]!.Sectors) sector.Data[^1] = 0xA6;
        source.MarkModified();
        byte[] before = target.Serialize(), sourceBefore = source.Serialize();
        string key = DskCapabilityService.CompatibilityKey(target);
        var map = DskAnalyzer.Analyze(target);
        var permitted = new bool[before.Length];
        foreach (var sector in map.Sectors.Where(s => profile.RequiredSystemTracks.Contains(s.Address.Track)))
            Array.Fill(permitted, true, checked((int)sector.FileOffset), sector.DataLength);
        DskBootSystemService.Install(target, source);
        var after = target.Serialize();
        Assert.True(target.IsModified);
        Assert.Equal(before.Length, after.Length);
        Assert.NotEqual(before, after);
        for (int i = 0; i < before.Length; i++) if (!permitted[i]) Assert.Equal(before[i], after[i]);
        Assert.Equal(sourceBefore, source.Serialize());
        var reopened = DskDocument.Open(after);
        Assert.Equal(key, DskCapabilityService.CompatibilityKey(reopened));
        Assert.Equal(2, reopened.FileSystem.ReadDirectory().Count);
        Assert.Equal(0, DskAnalyzer.Analyze(reopened).Errors);
    }

    [Fact]
    public void NativePersonalCpmCannotInstallArbitraryBootBytesWithoutSystemFile()
    {
        var target = Cpm(0); var source = Cpm(0);
        foreach (int track in new[] { 0, 1 })
            foreach (var sector in source.Image.Tracks[track]!.Sectors) sector.Data[^1] = 0xA6;
        source.MarkModified();
        byte[] before = target.Serialize();
        Assert.False(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
        Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, source));
        Assert.Equal(before, target.Serialize()); Assert.False(target.IsModified);
    }

    [Fact]
    public void FourCpmLayoutsAreNotInterchangeable()
    {
        for (int a = 0; a < 4; a++)
        for (int b = 0; b < 4; b++)
        {
            if (a == b) continue;
            var target = Cpm(a); var source = Cpm(b);
            var before = target.Serialize();
            Assert.False(DskCapabilityService.CanInstallBootSystem(target, Assert.Single(DskCapabilityService.GetAvailableBootSystems(source))).IsCompatible);
            Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, source));
            Assert.Equal(before, target.Serialize());
            Assert.False(target.IsModified);
        }
    }

    [Theory]
    [InlineData("order")] [InlineData("ch")] [InlineData("n")]
    [InlineData("id")] [InlineData("fdc")] [InlineData("late-system")]
    public void IncompatiblePhysicalSourceNeverChangesTarget(string change)
    {
        var target = Cpm(2); var source = Cpm(2);
        source.Image.Tracks[1]!.Sectors[0].Data[^1] = 0xA6;
        switch (change)
        {
            case "order": source.Image.Tracks[4]!.Sectors.Reverse(); break;
            case "ch": source.Image.Tracks[4]!.Sectors[0].Cylinder = 99; break;
            case "n": source.Image.Tracks[4]!.Sectors[0].SizeCode = 0; break;
            case "id": source.Image.Tracks[4]!.Sectors[0].SectorId = 99; break;
            case "fdc": source.Image.Tracks[4]!.Sectors[0].FdcStatus1 = 0x20; break;
            case "late-system": source.Image.Tracks[3]!.Sectors[^1].SectorId = 99; break;
        }
        byte[] before = target.Serialize();
        Assert.False(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
        Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, source));
        Assert.Equal(before, target.Serialize()); Assert.False(target.IsModified);
    }

    [Fact]
    public void DpbChecksumSizeAndPhysicalMapBelongToCompatibilityKey()
    {
        var original = Cpm(2); var cpm = (CpmFileSystem)original.FileSystem;
        var changedDpb = cpm.Dpb with { Cks = (ushort)(cpm.Dpb.Cks + 1) };
        Assert.True(CpmFileSystem.TryOpen(original.Image, changedDpb, out var changed));
        var document = new DskDocument(original.Image, original.Serialize(), null, changed!);
        Assert.NotEqual(DskCapabilityService.CompatibilityKey(original), DskCapabilityService.CompatibilityKey(document));
        Assert.False(DskCapabilityService.CanInstallBootSystem(original, document).IsCompatible);
    }

    [Fact]
    public void EmptySourceHasNoInstallableSystemData()
    {
        var target = Cpm(2); var source = Cpm(2);
        Assert.False(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
        Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, source));
        Assert.False(target.IsModified);
    }

    [Fact]
    public void FailedPostInstallDetectionRetainsExistingUnsavedChanges()
    {
        var target = Cpm(0); var source = Cpm(0);
        target.FileSystem.Insert("KEEP.BIN", [1, 2, 3]); target.MarkModified();
        byte[] before = target.Serialize();
        var boot = source.Image.Tracks[1]!.Sectors.Single(s => s.SectorId == 1);
        boot.Data[0] ^= 0xFF; // Break the P-CP/M IPL signature, not its cached DPB.
        Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, source));
        Assert.Equal(before, target.Serialize()); Assert.True(target.IsModified);
    }

    [Theory]
    [InlineData("single")] [InlineData("multi")] [InlineData("fsmz")]
    [InlineData("ipldisk")] [InlineData("mrs")] [InlineData("raw")] [InlineData("boot")]
    public void NonCpmNeverOffersCpmInstall(string format)
    {
        var record = new TapeRecord(new byte[TapeRecord.HeaderLength],
            new MZQFileHeader { MzfFtype = 1, MzfFname = new byte[16], MzfSize = 4, MzfStart = 0x2000, MzfExec = 0x2000 },
            new MZQFileBody { DataSize = 4, MzfBody = [1, 2, 3, 4], Crc = [], TrailingData = [] });
        var document = format switch
        {
            "single" => DskDocument.Open(Mz800IplDskWriter.Build(record)),
            "multi" => DskDocument.Open(Mz800MultiGameIplDskWriter.Build([new(record, "TEST", new MzfCompressionOptions(MzfCompressionAlgorithm.None), 4)]).Image),
            "fsmz" => DskDocumentFactory.CreateFsmz(false),
            "ipldisk" => DskDocumentFactory.CreateFsmz(true),
            "mrs" => DskDocumentFactory.CreateMrs(),
            "raw" => DskDocument.Open(DskImage.CreateUniform(2, 1, 1, 128, 1, 0x4E, 0, "raw").Serialize()),
            _ => BootOnly()
        };
        if (format == "boot") Assert.Equal(DskFileSystemType.BootOnly, document.FileSystem.Type);
        if (format == "raw") Assert.Equal(DskFileSystemType.Raw, document.FileSystem.Type);
        if (format == "single") Assert.Equal(DskFileSystemType.SingleIpl, document.FileSystem.Type);
        if (format == "multi") Assert.Equal(DskFileSystemType.MultiIpl, document.FileSystem.Type);
        Assert.NotEqual(DskFileSystemType.Cpm, document.FileSystem.Type);
        Assert.Empty(DskCapabilityService.GetAvailableBootSystems(document));
        if (format is "single" or "multi" or "raw" or "boot")
            Assert.Empty(DskCapabilityService.GetAvailableConversions(document));
        Assert.False(DskCapabilityService.CanInstallBootSystem(document, Assert.Single(DskCapabilityService.GetAvailableBootSystems(Cpm(2)))).IsCompatible);
        var before = document.Serialize();
        Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(document, Cpm(2)));
        Assert.Equal(before, document.Serialize()); Assert.False(document.IsModified);
    }
}
