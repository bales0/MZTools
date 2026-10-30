namespace MZTools.Tests;

public class DskStructureTests
{
    private static DskStructureItem Row(DskStructureSnapshot snapshot, DskStructureTab tab, string name) => Assert.Single(snapshot.Items, i => i.Tab == tab && i.Name == name);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FsmzShowsNativeDinfoDirectoryAndBitmapOwners(bool extended)
    {
        var doc = DskDocumentFactory.CreateFsmz(extended);
        doc.FileSystem.Insert("TEST", new byte[513], loadAddress: 0x1200, executeAddress: 0x1234); doc.MarkModified();
        var before = doc.Serialize(); var file = doc.FileSystem.ReadDirectory()[0];
        var result = DskStructureService.Build(doc);
        Assert.Equal($"{(extended ? 127 : 63)} files + header", Row(result, DskStructureTab.Filesystem, "Directory variant").Value);
        Assert.Equal(extended ? 128 : 64, result.Items.Count(i => i.Tab == DskStructureTab.Directory));
        Assert.Equal(256, Row(result, DskStructureTab.RawStructure, "DINFO").Raw!.Length);
        Assert.Contains("LOAD=0x1200", Row(result, DskStructureTab.Directory, "Slot 1").Detail);
        for (int block = file.StartBlock; block < file.StartBlock + 3; block++)
        {
            var row = Row(result, DskStructureTab.Allocation, $"Block {block}");
            Assert.Equal("Used (bitmap)", row.Value); Assert.Contains("TEST", row.Detail);
            var address = Assert.Single(row.Addresses); var sector = result.Layout.Sectors.Single(s => s.Address == address);
            Assert.Equal((block / 16) ^ 1, sector.Track); Assert.Equal(block % 16 + 1, sector.R);
        }
        Assert.Equal("Free (bitmap)", Row(result, DskStructureTab.Allocation, $"Block {file.StartBlock + 3}").Value);
        Assert.Equal(before, doc.Serialize()); Assert.True(doc.IsModified);
    }

    [Theory]
    [InlineData("dd")] [InlineData("hd")] [InlineData("personal")] [InlineData("sds")]
    public void CpmDpbAndPhysicalBlockMappingFollowDetectedLayout(string profile)
    {
        var doc = profile switch { "personal" => DskDocumentFactory.CreatePersonalCpm80(false), "sds" => DskDocumentFactory.CreatePersonalCpm80(true), _ => DskDocumentFactory.CreateCpm(profile == "hd") };
        doc.FileSystem.Insert("GAME.COM", new byte[35000], user: 3); doc.MarkModified();
        var before = doc.Serialize(); var fs = Assert.IsType<CpmFileSystem>(doc.FileSystem); var result = DskStructureService.Build(doc);
        foreach (var name in new[] { "SPT", "BSH", "BLM", "EXM", "DSM", "DRM", "AL0", "AL1", "CKS", "OFF", "Block size", "Directory blocks", "Physical track map", "Physical sector map" }) Assert.NotNull(Row(result, DskStructureTab.Filesystem, name));
        Assert.Equal(fs.Dpb.Drm + 1, result.Items.Count(i => i.Tab == DskStructureTab.Directory));
        Assert.Equal(fs.Dpb.Dsm + 1, result.Items.Count(i => i.Tab == DskStructureTab.Allocation));
        var slot = Row(result, DskStructureTab.Directory, "Slot 0"); Assert.Contains("User 3: GAME.COM", slot.Value); Assert.Contains("RC=", slot.Detail); Assert.Contains("Allocation pointer slots", slot.Detail);
        var file = fs.ReadDirectory()[0]; var raw = fs.DirectoryEntriesFor(file)[0].Raw;
        int b = fs.Dpb.Dsm <= 255 ? raw[16] : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(16, 2));
        var row = Row(result, DskStructureTab.Allocation, $"Block {b}"); Assert.Equal("Used", row.Value); Assert.Contains("GAME.COM", row.Detail);
        var expected = Enumerable.Range(0, fs.Dpb.BlockSize / 128).Select(i => CpmFileSystem.MapByteOffset(fs.Dpb, b, i * 128)).Select(a => (a.AbsoluteTrack, a.PhysicalSector)).Distinct().Order().ToArray();
        var actual = row.Addresses.Select(a => result.Layout.Sectors.Single(s => s.Address == a)).Select(s => (s.Track, (int)s.R)).Order().ToArray();
        Assert.Equal(expected, actual); Assert.Equal(before, doc.Serialize());
    }

    [Fact]
    public void MrsUsesActualFatAndDirectoryBoundsAndNativeFields()
    {
        var doc = DskDocumentFactory.CreateMrs(); doc.FileSystem.Insert("GAME.COM", new byte[777], loadAddress: 0x3456, executeAddress: 0x789A); doc.MarkModified();
        var before = doc.Serialize(); var fs = Assert.IsType<MrsFileSystem>(doc.FileSystem); var result = DskStructureService.Build(doc);
        Assert.Equal(fs.DataBlock.ToString(), Row(result, DskStructureTab.Filesystem, "Data start").Value);
        Assert.Equal(fs.AllocationSnapshot(), Row(result, DskStructureTab.RawStructure, "FAT").Raw);
        Assert.Equal(fs.DirectorySnapshot(), Row(result, DskStructureTab.RawStructure, "Directory area").Raw);
        Assert.Contains("LOAD(+0C)=0x3456", Row(result, DskStructureTab.Directory, "Slot 0").Detail);
        Assert.Contains("EXEC(+16)=0x789A", Row(result, DskStructureTab.Directory, "Slot 0").Detail);
        var data = Row(result, DskStructureTab.Allocation, $"Block {fs.DataBlock}"); Assert.Equal("Used", data.Value); Assert.Contains("GAME.COM", data.Detail);
        var sector = result.Layout.Sectors.Single(s => s.Address == Assert.Single(data.Addresses)); Assert.Equal(fs.DataBlock / 9, sector.Track); Assert.Equal(fs.DataBlock % 9 + 1, sector.R);
        Assert.Equal(before, doc.Serialize());
    }

    [Theory]
    [InlineData("CPMv41 Hry COM A.dsk")] [InlineData("sds400.dsk")] [InlineData("mz-2z047v10a.DSK")]
    public void ReferenceImagesRemainByteIdentical(string name)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DSK", name));
        var doc = DskDocument.Open(path); var before = doc.Serialize(); var warnings = doc.FileSystem.Warnings.ToArray();
        var result = DskStructureService.Build(doc);
        Assert.NotEmpty(result.Items); Assert.Equal(before, doc.Serialize()); Assert.False(doc.IsModified); Assert.Equal(warnings, doc.FileSystem.Warnings);
        if (result.Items.FirstOrDefault(i => i.Raw != null) is { Raw: { } bytes }) bytes[0] ^= 255;
        Assert.Equal(before, doc.Serialize());
    }

    [Fact]
    public void UnknownFilesystemDoesNotGuessStructuresOrOfferInspector()
    {
        var doc = DskDocument.Open(DskImage.CreateUniform(2, 1, 2, 128, 1, 0x4E, 0, "raw").Serialize());
        Assert.False(DskCapabilityService.CanInspectStructure(doc)); Assert.False(DskCapabilityService.CanInspectStructure(null));
        Assert.Contains(DskStructureService.Build(doc).Items, i => i.Value == "Structure decoding unavailable");
    }

    internal static void ExerciseWindowOnCurrentStaThread()
    {
        var doc = DskDocumentFactory.CreateCpm(false); var snapshot = DskStructureService.Build(doc);
        DskStructureItem? callback = null;
        var window = new DskStructureWindow(null, snapshot, item => callback = item);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object Field(string name) => window.GetType().GetField(name, flags)!.GetValue(window)!;
        bool Enabled(string name) => (bool)Field(name).GetType().GetProperty("IsEnabled")!.GetValue(Field(name))!;
        var tabItems = (System.Collections.IList)Field("tabs").GetType().GetProperty("Items")!.GetValue(Field("tabs"))!;
        Assert.Equal(4, tabItems.Count);
        Assert.False(Enabled("hex")); Assert.False(Enabled("clear"));
        Assert.Equal("Hex view metadata...", Field("hex").GetType().GetProperty("Content")!.GetValue(Field("hex")));
        var slot = Row(snapshot, DskStructureTab.Directory, "Slot 0"); window.Select(slot);
        Assert.Equal(slot, callback); Assert.True(Enabled("hex")); Assert.True(Enabled("clear"));
        var block = Row(snapshot, DskStructureTab.Allocation, "Block 0"); window.Select(block);
        Assert.Equal(block, callback); Assert.False(Enabled("hex")); Assert.True(Enabled("clear"));
        window.Select(null); Assert.Null(callback); Assert.False(Enabled("hex")); Assert.False(Enabled("clear")); window.Close();
    }

    [Fact]
    public void ReadOnlyUnsafeFsmzStillExposesRawAndAnalyzerDiagnostics()
    {
        var doc = DskDocumentFactory.CreateFsmz(); doc.FileSystem.Insert("TEST", [1]);
        doc.Image.Tracks[1]!.Sectors.Single(s => s.SectorId == 16).Data[6] = 0xFF; doc.MarkModified();
        doc = DskDocument.Open(doc.Serialize()); var before = doc.Serialize();
        Assert.True(doc.IsReadOnly); Assert.True(DskCapabilityService.CanInspectStructure(doc));
        var result = DskStructureService.Build(doc); Assert.NotEmpty(result.Layout.Issues);
        Assert.Contains(result.Items, i => i.Name.Contains("FSMZ_BITMAP")); Assert.Equal(before, doc.Serialize()); Assert.False(doc.IsModified);
    }
}
