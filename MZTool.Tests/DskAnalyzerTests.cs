namespace MZTools.Tests;

using System.Buffers.Binary;

public class DskAnalyzerTests
{
    [Fact]
    public void RawPhysicalOrderAndOffsetsAreExactAndReadOnly()
    {
        var image = DskImage.CreateUniform(3, 2, 3, 128, 20, 0x4E, 0, "raw");
        image.Tracks[0]!.Sectors.Reverse();
        image.ReplaceTrackGeometry(2, 5, 512, [5, 1, 9, 3, 7], 0x4E, 0);
        var bytes = image.Serialize();
        var document = DskDocument.Open(bytes);
        var map = DskAnalyzer.Analyze(document);
        Assert.Equal(0, map.Errors);
        Assert.Equal(0, map.Warnings);
        Assert.Equal(new byte[] { 22, 21, 20 }, map.Tracks[0].Sectors.Select(s => s.R));
        Assert.Equal(0x200, map.Tracks[0].Sectors[0].FileOffset);
        Assert.Equal(0x280, map.Tracks[0].Sectors[1].FileOffset);
        Assert.Equal(5, map.Tracks[2].Sectors.Count);
        foreach (var s in map.Sectors) Assert.Equal(s.Data, bytes.AsSpan((int)s.FileOffset, s.DataLength).ToArray());
        Assert.All(map.Sectors, s => Assert.Equal(DskSectorRole.Unknown, s.Role));
        Assert.Equal(bytes, document.Serialize());
        Assert.False(document.IsModified);
    }

    [Fact]
    public void MissingTrackAndTrailingBytesAreInformational()
    {
        var bytes = DskImage.CreateUniform(2, 1, 1, 128, 1, 0x4E, 0, "missing").Serialize();
        bytes[0x35] = 0;
        var map = DskAnalyzer.Analyze(bytes);
        Assert.True(map.Tracks[1].IsMissing);
        Assert.Contains(map.Issues, i => i.Code == "DSK_MISSING_TRACK" && i.Severity == DskIssueSeverity.Info);
        Assert.Contains(map.Issues, i => i.Code == "DSK_TRAILING_DATA");
        Assert.Equal(0, map.Errors);
    }

    [Fact]
    public void ContainerFlagsDuplicateAddressesChAndFdcStatus()
    {
        var image = DskImage.CreateUniform(2, 1, 3, 128, 1, 0x4E, 0, "bad");
        image.Tracks[0]!.Sectors[1].SectorId = 1;
        image.Tracks[0]!.Sectors[2].Cylinder = 7;
        image.Tracks[1]!.Sectors[0].FdcStatus1 = 0x40;
        image.Tracks[1]!.Sectors[1].FdcStatus2 = 0x20;
        var map = DskAnalyzer.Analyze(image);
        Assert.Contains(map.Issues, i => i.Code == "DSK_DUPLICATE_SECTOR_ID");
        Assert.Contains(map.Issues, i => i.Code == "DSK_SECTOR_CH");
        Assert.Contains(map.Issues, i => i.Code == "DSK_FDC_STATUS" && i.Severity == DskIssueSeverity.Warning);
        Assert.Contains(map.Issues, i => i.Code == "DSK_FDC_STATUS" && i.Severity == DskIssueSeverity.Error);
    }

    [Fact]
    public void TruncatedImageAndBadDescriptorProduceStructuredErrors()
    {
        var bytes = DskImage.CreateUniform(1, 1, 1, 128, 1, 0x4E, 0, "bad").Serialize();
        Assert.Contains(DskAnalyzer.Analyze(bytes[..^1]).Issues, i => i.Code == "DSK_PARSE_FAILED");
        bytes[0x100 + 0x1B] = 7;
        Assert.Contains(DskAnalyzer.Analyze(bytes).Issues, i => i.Code == "DSK_PARSE_FAILED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FsmzValidFilesHaveExactBlockOwnership(bool extended)
    {
        var doc = DskDocumentFactory.CreateFsmz(extended);
        doc.FileSystem.Insert("GAME", new byte[700]); doc.MarkModified();
        var file = Assert.Single(doc.FileSystem.ReadDirectory());
        var map = DskAnalyzer.Analyze(doc);
        Assert.Equal(0, map.Errors); Assert.Equal(0, map.Warnings);
        var sectors = map.Sectors.Where(s => s.Owners.Any(o => o.FileKey == file.Key)).ToArray();
        Assert.Equal(3, sectors.Length);
        Assert.Equal(Enumerable.Range(file.StartBlock, 3), sectors.SelectMany(s => s.LogicalBlocks).Order());
        Assert.All(sectors, s => Assert.Equal((file.StartBlock / 16) ^ 1, s.Track));
    }

    [Theory]
    [InlineData("bitmap", "FSMZ_BITMAP_MISMATCH")]
    [InlineData("orphan", "FSMZ_ORPHAN_BLOCK")]
    [InlineData("counter", "FSMZ_USED_COUNTER_MISMATCH")]
    [InlineData("overlap", "FSMZ_OVERLAPPING_FILES")]
    [InlineData("range", "FSMZ_FILE_OUT_OF_RANGE")]
    [InlineData("dinfo", "FSMZ_DINFO_INVALID")]
    [InlineData("marker", "FSMZ_DIRECTORY_MARKER")]
    public void FsmzCorruptAllocationsAreReportedWithoutRepair(string corruption, string code)
    {
        var doc = DskDocumentFactory.CreateFsmz(false);
        doc.FileSystem.Insert("FIRST", new byte[512]); doc.FileSystem.Insert("SECOND", new byte[256]);
        var entries = doc.FileSystem.ReadDirectory();
        var info = ReadFsmz(doc.Image, 15);
        var dir = ReadFsmz(doc.Image, 16);
        switch (corruption)
        {
            case "bitmap": info[6] &= 0xFE; break;
            case "orphan": info[6] |= 0x80; break;
            case "counter": WriteWord(info, 2, 100); break;
            case "overlap": WriteWord(dir, 64 + 30, entries[0].StartBlock); break;
            case "range": WriteWord(dir, 32 + 30, 65000); break;
            case "dinfo": info[1] = 1; break;
            case "marker": dir[0] = 0; break;
        }
        WriteFsmz(doc.Image, 15, info); WriteFsmz(doc.Image, 16, dir);
        byte[] before = doc.Image.Serialize();
        var map = DskAnalyzer.Analyze(doc.Image);
        Assert.Contains(map.Issues, i => i.Code == code);
        Assert.Equal(before, doc.Image.Serialize());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CpmAllPresetsRespectPhysicalMapsAnd128ByteSubranges(int preset)
    {
        var doc = CpmDocument(preset);
        doc.FileSystem.Insert("ASM.COM", new byte[7000], user: 3); doc.MarkModified();
        var fs = Assert.IsType<CpmFileSystem>(doc.FileSystem);
        var file = Assert.Single(fs.ReadDirectory());
        var map = DskAnalyzer.Analyze(doc);
        Assert.Equal(0, map.Errors); Assert.Equal(0, map.Warnings);
        var parts = map.Sectors.SelectMany(s => s.Owners.Where(o => o.FileKey == file.Key).Select(o => (s, o))).ToArray();
        Assert.NotEmpty(parts);
        Assert.All(parts, pair => {
            var (t, r, p) = CpmFileSystem.MapByteOffset(fs.Dpb, pair.o.Block, pair.o.LogicalSector * 128 % fs.Dpb.BlockSize);
            Assert.Equal(t, pair.s.Track); Assert.Equal(r, pair.s.R); Assert.Equal(p, pair.o.Offset); Assert.Equal(128, pair.o.Length);
        });
        Assert.Contains(parts, p => p.o.Offset == 384);
        if (preset == 2)
        {
            Assert.Equal(new[] { 2, 4, 6 }, parts.Select(p => p.s.Track).Distinct().Order());
            Assert.All(parts.Where(p => p.o.Block == 1), p => Assert.Equal(2, p.s.Track));
        }
        if (preset == 3) Assert.Contains(parts, p => p.s.R >= 11);
        Assert.Contains(map.Sectors, s => s.Role.HasFlag(DskSectorRole.Directory));
    }

    [Theory]
    [InlineData("range", "CPM_BLOCK_OUT_OF_RANGE")]
    [InlineData("crosslink", "CPM_CROSSLINKED_BLOCK")]
    [InlineData("duplicateblock", "CPM_DUPLICATE_ALLOCATION")]
    [InlineData("extent", "CPM_BROKEN_EXTENT")]
    [InlineData("duplicatefile", "CPM_DUPLICATE_EXTENT")]
    [InlineData("reserved", "CPM_DIRECTORY_DATA_OVERLAP")]
    [InlineData("size", "CPM_SIZE_ALLOCATION_MISMATCH")]
    public void CpmBadExtentsAreReportedEvenWhenDetectorRejectsThem(string corruption, string code)
    {
        var doc = CpmDocument(0);
        doc.FileSystem.Insert("FIRST.COM", new byte[512]); doc.FileSystem.Insert("SECOND.COM", new byte[512]);
        var dpb = Assert.IsType<CpmFileSystem>(doc.FileSystem).Dpb;
        var (t, r, _) = CpmFileSystem.MapByteOffset(dpb, 0, 0);
        var bytes = doc.Image.Tracks[t]!.Sectors.Single(s => s.SectorId == r).Data;
        switch (corruption)
        {
            case "range": WriteWord(bytes, 16, dpb.Dsm + 1); break;
            case "crosslink": bytes.AsSpan(16, 2).CopyTo(bytes.AsSpan(48, 2)); break;
            case "duplicateblock": bytes.AsSpan(16, 2).CopyTo(bytes.AsSpan(18, 2)); break;
            case "extent": bytes[15] = 200; break;
            case "duplicatefile": bytes.AsSpan(1, 11).CopyTo(bytes.AsSpan(33, 11)); break;
            case "reserved": WriteWord(bytes, 16, 1); break;
            case "size": bytes[15] = 128; break;
        }
        var before = doc.Image.Serialize();
        var map = DskAnalyzer.Analyze(doc.Image);
        Assert.Contains(map.Issues, i => i.Code == code);
        Assert.Equal(before, doc.Image.Serialize());
    }

    [Fact]
    public void CpmInvalidPhysicalMapAndSystemOverlapAreReported()
    {
        var doc = CpmDocument(0);
        var dpb = Assert.IsType<CpmFileSystem>(doc.FileSystem).Dpb;
        var badMap = Enumerable.Repeat(new CpmSectorAddress(999, 1), 2000).ToArray();
        Assert.Contains(DskAnalyzer.Analyze(doc.Image, dpb with { PhysicalSectorMap = badMap }).Issues, i => i.Code == "CPM_PHYSICAL_MAP_MISMATCH");
        var overlapMap = Enumerable.Repeat(new CpmSectorAddress(0, 1), 2000).ToArray();
        Assert.Contains(DskAnalyzer.Analyze(doc.Image, dpb with { PhysicalSectorMap = overlapMap }).Issues, i => i.Code == "CPM_SYSTEM_DATA_OVERLAP");
    }

    [Fact]
    public void MrsOwnershipFollowsFatFileId()
    {
        var doc = DskDocumentFactory.CreateMrs();
        doc.FileSystem.Insert("UTIL.DAT", new byte[1200]); doc.MarkModified();
        var entry = Assert.Single(doc.FileSystem.ReadDirectory());
        var map = DskAnalyzer.Analyze(doc);
        Assert.Equal(0, map.Errors); Assert.Equal(0, map.Warnings);
        var mapped = map.Sectors.Where(s => s.Owners.Any(o => o.FileKey == entry.Key)).ToArray();
        Assert.Equal(3, mapped.Length);
        Assert.All(mapped, s => Assert.Equal(DskSectorRole.Data, s.Role));
        Assert.Equal(new[] { 45, 46, 47 }, mapped.SelectMany(s => s.LogicalBlocks));
    }

    [Theory]
    [InlineData("count", "MRS_BLOCK_COUNT_MISMATCH")]
    [InlineData("missing", "MRS_BLOCK_COUNT_MISMATCH")]
    [InlineData("orphan", "MRS_FAT_ORPHAN_BLOCK")]
    [InlineData("reserved", "MRS_RESERVED_DATA_OVERLAP")]
    [InlineData("invalidid", "MRS_INVALID_FILE_ID")]
    public void MrsFatAndDirectoryInconsistenciesAreReported(string corruption, string code)
    {
        var doc = DskDocumentFactory.CreateMrs(); doc.FileSystem.Insert("UTIL.DAT", new byte[512]);
        var fat = ReadMrs(doc.Image, 36); var dir = ReadMrs(doc.Image, 39);
        switch (corruption)
        {
            case "count": WriteWord(dir, 14, 2); break;
            case "missing": fat[45] = 0; break;
            case "orphan": fat[60] = 100; break;
            case "reserved": fat[35] = 1; break;
            case "invalidid": dir[11] = 0xFF; break;
        }
        WriteMrs(doc.Image, 36, fat); WriteMrs(doc.Image, 39, dir);
        var before = doc.Image.Serialize(); var map = DskAnalyzer.Analyze(doc.Image);
        Assert.Contains(map.Issues, i => i.Code == code); Assert.Equal(before, doc.Image.Serialize());
    }

    [Theory]
    [InlineData("mz-2z047v10a.DSK")]
    [InlineData("sds400.dsk")]
    [InlineData("FLAPPY ver 1.0A.dsk")]
    public void ReferenceImagesHaveNoUnsafeOrErrorFalsePositives(string name)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DSK", name));
        var doc = DskDocument.Open(path);
        var before = doc.Serialize(); var map = DskAnalyzer.Analyze(doc);
        Assert.True(map.Errors == 0, map.Report());
        Assert.Equal(before, doc.Serialize()); Assert.False(doc.IsModified);
    }

    [Fact]
    public void EditorInitializesAllTabsAndLoadsSnapshotOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try
            {
                // Exercise compiled XAML and event wiring without displaying a
                // window; backend tests above remain independent of WPF.
                var assembly = typeof(DskEditorControl).Assembly;
                var appType = assembly.GetType("MZTools.App")!;
                object app = Activator.CreateInstance(appType)!;
                appType.GetMethod("InitializeComponent")!.Invoke(app, null);
                var control = Activator.CreateInstance(typeof(DskEditorControl))!;
                var type = control.GetType();
                type.GetMethod("LoadDocument", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(control, [DskDocumentFactory.CreateCpm(false)]);
                var layout = (DskLayoutModel)type.GetField("diskLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
                Assert.Equal(1447, layout.Sectors.Count());
                Assert.Equal(0, layout.Errors);
                foreach (string field in new[] { "dskViews", "diskMap", "mapBlocksGrid", "issuesGrid", "mapInspectorTabs" })
                    Assert.NotNull(type.GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control));
                var map = type.GetField("diskMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
                var sizeType = map.GetType().GetMethod("Measure")!.GetParameters()[0].ParameterType;
                map.GetType().GetMethod("Measure")!.Invoke(map, [Activator.CreateInstance(sizeType, [1200d, 5000d])]);
                var rectType = map.GetType().GetMethod("Arrange")!.GetParameters()[0].ParameterType;
                map.GetType().GetMethod("Arrange")!.Invoke(map, [Activator.CreateInstance(rectType, [0d, 0d, 1200d, 5000d])]);
                var core = System.Reflection.Assembly.Load("PresentationCore");
                var pixelFormat = core.GetType("System.Windows.Media.PixelFormats")!.GetProperty("Pbgra32")!.GetValue(null)!;
                var bitmapType = core.GetType("System.Windows.Media.Imaging.RenderTargetBitmap")!;
                var bitmap = Activator.CreateInstance(bitmapType, [1200, 5000, 96d, 96d, pixelFormat])!;
                bitmapType.GetMethod("Render")!.Invoke(bitmap, [map]);
                foreach (string name in new[] { "multi.dsk", "multi2.dsk" })
                {
                    var doc = DskDocument.Open(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DSK", name)));
                    var before = doc.Serialize();
                    type.GetMethod("LoadDocument", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, [doc]);
                    var info = type.GetField("infoText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
                    string text = (string)info.GetType().GetProperty("Text")!.GetValue(info)!;
                    Assert.Contains(name == "multi.dsk" ? "IPLPRO logical block 0, byte offset 0x20" : "menu footer at byte offset 0x186", text);
                    Assert.DoesNotContain("legacy", text, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, doc.Serialize()); Assert.False(doc.IsModified);
                    type.GetMethod("CanonicalizeMultiIplForSave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, null);
                    Assert.Equal(MultiGameMetadataLayout.MenuFooter, Assert.IsType<MultiGameIplFileSystem>(doc.FileSystem).Metadata.Layout);
                    if (name == "multi.dsk") Assert.True(doc.IsModified);
                    else { Assert.False(doc.IsModified); Assert.Equal(before, doc.Serialize()); }
                }
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                object Field(string name) => type.GetField(name, flags)!.GetValue(control)!;
                bool Enabled(string name) => (bool)Field(name).GetType().GetProperty("IsEnabled")!.GetValue(Field(name))!;
                Assert.False(Enabled("installBootSystemMenu")); // Current document is multi IPL.
                Assert.False(Enabled("convertFormatMenu"));
                Assert.Equal("Disk ▾", Field("diskMenuButton").GetType().GetProperty("Content")!.GetValue(Field("diskMenuButton")));
                Assert.Equal(28d, Field("diskMenuButton").GetType().GetProperty("Height")!.GetValue(Field("diskMenuButton")));
                Assert.Contains("Bootable: Yes", (string)Field("infoText").GetType().GetProperty("Text")!.GetValue(Field("infoText"))!);
                var originalUiDocument = Field("document");
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [DskDocumentFactory.CreateCpm(false)]);
                Assert.True(Enabled("installBootSystemMenu"));
                Assert.True(Enabled("convertFormatMenu"));
                Assert.True(Enabled("compareDiskMenu"));
                Assert.Null(type.GetField("filePropertiesMenu", flags));
                Assert.True(Enabled("structureInspectorMenu")); Assert.Null(type.GetField("structureInspectorButton", flags));
                Assert.Null(type.GetField("dskPropertiesButton", flags));
                var propertyDocument = DskDocumentFactory.CreateCpm(false);
                propertyDocument.FileSystem.Insert("ONE.BIN", [1]); propertyDocument.FileSystem.Insert("TWO.BIN", [2]); propertyDocument.MarkModified();
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [propertyDocument]);
                var propertyGrid = Field("directoryGrid");
                var propertyEntries = ((System.Collections.IList)propertyGrid.GetType().GetProperty("Items")!.GetValue(propertyGrid)!).Cast<DskFileEntry>().ToArray();
                byte[] propertyFixture = propertyDocument.Serialize();
                propertyGrid.GetType().GetProperty("SelectedItem")!.SetValue(propertyGrid, propertyEntries[0]);
                var columns = ((System.Collections.IList)propertyGrid.GetType().GetProperty("Columns")!.GetValue(propertyGrid)!).Cast<object>().ToArray();
                foreach (string flag in new[] { "ReadOnly", "System", "Archived" })
                    Assert.Equal("DataGridTemplateColumn", columns.Single(c => (string)c.GetType().GetProperty("SortMemberPath")!.GetValue(c)! == flag).GetType().Name);
                Assert.False((bool)columns.First(c => (string)c.GetType().GetProperty("SortMemberPath")!.GetValue(c)! == "User").GetType().GetProperty("IsReadOnly")!.GetValue(columns.First(c => (string)c.GetType().GetProperty("SortMemberPath")!.GetValue(c)! == "User"))!);
                var updatedFile = (DskFileEntry)type.GetMethod("ApplyInlineProperty", flags)!.Invoke(control, [propertyEntries[0], "User", "3"])!;
                Assert.Equal(3, updatedFile.User);
                updatedFile = (DskFileEntry)type.GetMethod("ApplyInlineProperty", flags)!.Invoke(control, [updatedFile, "ReadOnly", "True"])!;
                Assert.True(updatedFile.ReadOnly);
                byte[] beforeInvalid = propertyDocument.Serialize();
                Assert.Throws<System.Reflection.TargetInvocationException>(() => type.GetMethod("ApplyInlineProperty", flags)!.Invoke(control, [updatedFile, "User", "99"]));
                Assert.Equal(beforeInvalid, propertyDocument.Serialize());
                var checkboxType = System.Reflection.Assembly.Load("PresentationFramework").GetType("System.Windows.Controls.CheckBox")!;
                var checkbox = Activator.CreateInstance(checkboxType)!;
                checkboxType.GetProperty("DataContext")!.SetValue(checkbox, updatedFile);
                checkboxType.GetProperty("Tag")!.SetValue(checkbox, "Archived");
                checkboxType.GetProperty("IsChecked")!.SetValue(checkbox, true);
                var routedArgsType = type.GetMethod("FileFlag_Click", flags)!.GetParameters()[1].ParameterType;
                var clickArgs = Activator.CreateInstance(routedArgsType)!;
                clickArgs.GetType().GetProperty("RoutedEvent")!.SetValue(clickArgs, checkboxType.GetField("ClickEvent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)!.GetValue(null));
                type.GetMethod("FileFlag_Click", flags)!.Invoke(control, [checkbox, clickArgs]);
                Assert.True(propertyDocument.FileSystem.ReadDirectory().Single(entry => entry.User == 3).Archived);
                propertyDocument = DskDocument.Open(propertyFixture);
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [propertyDocument]);
                var selectedItems = (System.Collections.IList)propertyGrid.GetType().GetProperty("SelectedItems")!.GetValue(propertyGrid)!;
                foreach (var item in ((System.Collections.IList)propertyGrid.GetType().GetProperty("Items")!.GetValue(propertyGrid)!).Cast<object>().ToArray()) selectedItems.Add(item);
                // Use the actual grid-bound item: native directory reads create new entry instances.
                checkboxType.GetProperty("DataContext")!.SetValue(checkbox, selectedItems[0]);
                checkboxType.GetProperty("Tag")!.SetValue(checkbox, "System");
                type.GetMethod("FileFlag_Click", flags)!.Invoke(control, [checkbox, clickArgs]);
                Assert.All(propertyDocument.FileSystem.ReadDirectory(), file => Assert.True(file.System));
                Assert.Equal(2, selectedItems.Count);
                Assert.NotNull(propertyGrid.GetType().GetProperty("RowStyle")!.GetValue(propertyGrid));
                Assert.NotNull(propertyGrid.GetType().GetProperty("CellStyle")!.GetValue(propertyGrid));
                var related = (HashSet<DskSectorAddress>)Field("relatedMapSectors");
                var relatedLayout = (DskLayoutModel)Field("diskLayout");
                var fileKeys = selectedItems.Cast<DskFileEntry>().Select(file => file.Key).ToHashSet();
                var expectedRelated = relatedLayout.Sectors.Where(sector => sector.Owners.Any(owner => fileKeys.Contains(owner.FileKey)))
                    .Select(sector => sector.Address).ToHashSet();
                Assert.NotEmpty(related); Assert.True(related.SetEquals(expectedRelated));
                var oneRelated = relatedLayout.Sectors.First(sector => related.Contains(sector.Address));
                type.GetMethod("SelectLayoutSector", flags)!.Invoke(control, [oneRelated]);
                Assert.True(related.SetEquals(expectedRelated)); // Active sector does not erase the file/block group highlight.
                Assert.Same(oneRelated, Field("mapBlocksGrid").GetType().GetProperty("SelectedItem")!.GetValue(Field("mapBlocksGrid")));
                var groupUpdated = (IReadOnlyList<DskFileEntry>)type.GetMethod("ApplyInlineProperties", flags)!.Invoke(control,
                    [propertyDocument.FileSystem.ReadDirectory(), "User", "4"])!;
                Assert.All(groupUpdated, file => Assert.Equal(4, file.User));
                propertyDocument = DskDocument.Open(propertyFixture);
                var inlineMrs = DskDocumentFactory.CreateMrs();
                inlineMrs.FileSystem.Insert("INLINE.BIN", [1, 2, 3]); inlineMrs.MarkModified();
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [inlineMrs]);
                var mrsEntry = inlineMrs.FileSystem.ReadDirectory()[0];
                mrsEntry = (DskFileEntry)type.GetMethod("ApplyInlineProperty", flags)!.Invoke(control, [mrsEntry, "LoadAddress", "0xABCD"])!;
                mrsEntry = (DskFileEntry)type.GetMethod("ApplyInlineProperty", flags)!.Invoke(control, [mrsEntry, "ExecuteAddress", "22136"])!;
                Assert.Equal(0xABCD, mrsEntry.LoadAddress); Assert.Equal(0x5678, mrsEntry.ExecuteAddress);
                byte[] beforeMrsInvalid = inlineMrs.Serialize();
                Assert.Throws<System.Reflection.TargetInvocationException>(() => type.GetMethod("ApplyInlineProperty", flags)!.Invoke(control, [mrsEntry, "LoadAddress", "0x10000"]));
                Assert.Equal(beforeMrsInvalid, inlineMrs.Serialize());
                Assert.Equal(new byte[] {1,2,3}, inlineMrs.FileSystem.Extract(mrsEntry).Take(3));
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [DskDocumentFactory.CreateCpm(false)]);
                Assert.Contains("System: None", (string)Field("infoText").GetType().GetProperty("Text")!.GetValue(Field("infoText"))!);
                Assert.Contains("Capabilities", (string)type.GetMethod("AnalysisReportWithCapabilities", flags)!.Invoke(control, null)!);
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [DskDocumentFactory.CreateFsmz()]);
                Assert.False(Enabled("installBootSystemMenu"));
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [originalUiDocument]);
                Assert.False(Enabled("dskClearSelectionButton"));
                var views = Field("dskViews");
                var viewItems = (System.Collections.IList)views.GetType().GetProperty("Items")!.GetValue(views)!;
                Assert.Equal(2, viewItems.Count);
                Assert.Equal(new[] { "Files", "Disk Map" }, viewItems.Cast<object>().Select(item => item.GetType().GetProperty("Header")!.GetValue(item)!.ToString()));
                Assert.Null(type.GetField("buildBootSystemMenu", flags));
                Assert.Null(type.GetField("hexEditSectorMenu", flags));
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [propertyDocument]);
                Assert.Null(type.GetField("editSectorButton", flags));
                Assert.Null(type.GetField("editBlockButton", flags));
                Assert.Null(type.GetField("editTab", flags));
                var editGrid = Field("mapBlocksGrid");
                var editLayout = (DskLayoutModel)Field("diskLayout");
                var firstHex = editLayout.Sectors.First(s => s.Track == 10);
                var secondHex = editLayout.Sectors.First(s => s.Track == 11);
                var ownerType = System.Reflection.Assembly.Load("PresentationFramework").GetType("System.Windows.Window")!;
                var hexOwner = Activator.CreateInstance(ownerType)!;
                ownerType.GetProperty("Content")!.SetValue(hexOwner, control);
                editGrid.GetType().GetProperty("SelectedItem")!.SetValue(editGrid, firstHex);
                Assert.Null(type.GetField("hexWindow", flags)!.GetValue(control));
                Assert.IsNotType<DskHexEditorControl>(Field("editHexHost").GetType().GetProperty("Content")!.GetValue(Field("editHexHost")));
                type.GetMethod("HexWindow_Click", flags)!.Invoke(control, [null, null]);
                var embeddedHex = Assert.IsType<DskHexEditorControl>(Field("editHexHost").GetType().GetProperty("Content")!.GetValue(Field("editHexHost")));
                var separateHexWindow = Field("hexWindow");
                Assert.Same(Field("editHexHost"), separateHexWindow.GetType().GetProperty("Content")!.GetValue(separateHexWindow));
                Assert.Null(separateHexWindow.GetType().GetProperty("Owner")!.GetValue(separateHexWindow)); // Hidden owner cannot be assigned before Show in WPF.
                Assert.Contains("Hex Editor", separateHexWindow.GetType().GetProperty("Title")!.GetValue(separateHexWindow)!.ToString());
                embeddedHex.UnlockEditing();
                var pendingBytes = (byte[])firstHex.Data.Clone(); pendingBytes[0] ^= 1;
                embeddedHex.SetEditedHex(DskHexEditService.FormatHex(pendingBytes));
                type.GetProperty("ConfirmDiscardHexChanges", flags)!.SetValue(control, (Func<bool>)(() => false));
                separateHexWindow.GetType().GetMethod("Close")!.Invoke(separateHexWindow, null);
                Assert.Same(separateHexWindow, Field("hexWindow")); // Closing with unapplied bytes can be cancelled.
                editGrid.GetType().GetProperty("SelectedItem")!.SetValue(editGrid, secondHex);
                Assert.Equal(firstHex.Address, ((DskSectorLayout)Field("selectedMapSector")).Address);
                Assert.Same(embeddedHex, Field("editHexHost").GetType().GetProperty("Content")!.GetValue(Field("editHexHost")));
                int discardPrompts = 0;
                type.GetProperty("ConfirmDiscardHexChanges", flags)!.SetValue(control, (Func<bool>)(() => { discardPrompts++; return true; }));
                type.GetMethod("SelectLayoutSector", flags)!.Invoke(control, [secondHex]);
                Assert.Equal(1, discardPrompts); // Map→list synchronization must not ask twice.
                Assert.Equal(secondHex.Address, ((DskSectorLayout)Field("selectedMapSector")).Address);
                Assert.NotSame(embeddedHex, Field("editHexHost").GetType().GetProperty("Content")!.GetValue(Field("editHexHost")));
                Assert.Same(separateHexWindow, Field("hexWindow")); // Selection reuses one modeless window.
                Assert.Equal(secondHex.Address, type.GetField("activeHexSector", flags)!.GetValue(control));
                var bufferChoices = Field("editBlockBox");
                bufferChoices.GetType().GetProperty("SelectedIndex")!.SetValue(bufferChoices, 1);
                Assert.IsType<int>(type.GetField("activeHexBlock", flags)!.GetValue(control));
                Assert.IsType<DskHexEditorControl>(Field("editHexHost").GetType().GetProperty("Content")!.GetValue(Field("editHexHost")));
                bufferChoices.GetType().GetProperty("SelectedIndex")!.SetValue(bufferChoices, 0);
                Assert.Null(type.GetField("activeHexBlock", flags)!.GetValue(control));
                var cancelling = Assert.IsType<DskHexEditorControl>(Field("editHexHost").GetType().GetProperty("Content")!.GetValue(Field("editHexHost")));
                byte[] beforeCancel = propertyDocument.Serialize();
                cancelling.UnlockEditing(); cancelling.SetEditedHex(DskHexEditService.FormatHex(pendingBytes));
                cancelling.Cancel();
                Assert.Null(type.GetField("hexWindow", flags)!.GetValue(control));
                Assert.Equal(beforeCancel, propertyDocument.Serialize());
                type.GetMethod("SelectLayoutSector", flags)!.Invoke(control, [firstHex]);
                Assert.Null(type.GetField("hexWindow", flags)!.GetValue(control));
                var mapSurface = Field("diskMap");
                var interactions = assembly.GetType("MZTools.MapInteraction")!;
                var moveGrid = interactions.GetMethod("MoveGridSelection", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                var keyboardType = moveGrid.GetParameters()[1].ParameterType;
                foreach (string key in new[] { "Right", "Left", "Down", "Up" })
                    Assert.True((bool)moveGrid.Invoke(null, [editGrid, Enum.Parse(keyboardType, key)])!);
                Assert.False((bool)moveGrid.Invoke(null, [editGrid, Enum.Parse(keyboardType, "Escape")])!);
                Assert.True((bool)mapSurface.GetType().GetMethod("MoveSelection", flags)!.Invoke(mapSurface, [1])!);
                Assert.Equal(firstHex.PhysicalIndex + 1, ((DskSectorLayout)Field("selectedMapSector")).PhysicalIndex);
                Assert.Null(type.GetField("hexWindow", flags)!.GetValue(control));
                Assert.NotNull(editGrid.GetType().GetProperty("CellStyle")!.GetValue(editGrid));
                Assert.NotNull(editGrid.GetType().GetProperty("RowStyle")!.GetValue(editGrid));
                type.GetMethod("ClearMapSelection", flags)!.Invoke(control, [true]);
                Assert.True((bool)mapSurface.GetType().GetMethod("MoveSelection", flags)!.Invoke(mapSurface, [-1])!);
                Assert.Equal(editLayout.Sectors.Last().Address, ((DskSectorLayout)Field("selectedMapSector")).Address);
                ownerType.GetProperty("Content")!.SetValue(hexOwner, null);
                ownerType.GetMethod("Close")!.Invoke(hexOwner, null);
                var fileBox = Field("directoryGrid");
                fileBox.GetType().GetProperty("SelectedIndex")!.SetValue(fileBox, 0);
                Assert.Null(type.GetField("editPropertiesHost", flags));
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [originalUiDocument]);
                foreach (string removed in new[] { "sectorsTab", "sectorsGrid", "analyzeTab", "issueDetailText" })
                    Assert.Null(type.GetField(removed, flags));
                Assert.Null(type.GetMethod("AnalyzeFile_Click", flags));
                var tab = Field("mapTab");
                views.GetType().GetProperty("SelectedItem")!.SetValue(views, tab);
                var blockGrid = Field("mapBlocksGrid");
                var currentLayout = (DskLayoutModel)Field("diskLayout");
                var sector = currentLayout.Sectors.First(s => s.Role.HasFlag(DskSectorRole.Data));
                blockGrid.GetType().GetProperty("SelectedItem")!.SetValue(blockGrid, sector);
                Assert.True(Enabled("dskClearSelectionButton"));
                Assert.Equal(sector, Field("selectedMapSector"));
                Assert.NotNull(Field("editHexHost").GetType().GetProperty("Content")!.GetValue(Field("editHexHost")));
                type.GetMethod("Measure")!.Invoke(control, [Activator.CreateInstance(sizeType, [1100d, 600d])]);
                type.GetMethod("Arrange")!.Invoke(control, [Activator.CreateInstance(rectType, [0d, 0d, 1100d, 600d])]);
                type.GetMethod("UpdateLayout")!.Invoke(control, null);
                var panel = tab.GetType().GetProperty("Content")!.GetValue(tab)!;
                var children = (System.Collections.IList)panel.GetType().GetProperty("Children")!.GetValue(panel)!;
                QuickDiskMapTests.RaiseBlankClick(children[0]!);
                Assert.Null(type.GetField("selectedMapSector", flags)!.GetValue(control));
                Assert.False(Enabled("dskClearSelectionButton"));
                Assert.Null(blockGrid.GetType().GetProperty("SelectedItem")!.GetValue(blockGrid));
                Assert.Null(type.GetField("dskMapHexButton", flags));
                var browserType = assembly.GetType("MZTools.HexBrowser")!;
                object browser = Activator.CreateInstance(browserType)!;
                browserType.GetMethod("ShowRawData", flags)!.Invoke(browser, ["Selected sector", sector.Data, null]);
                browserType.GetMethod("Close")!.Invoke(browser, null);
                var files = Field("multiIplGrid");
                files.GetType().GetProperty("SelectedIndex")!.SetValue(files, 0);
                Assert.True(Enabled("dskClearSelectionButton"));
                Assert.Null(type.GetField("dskMapHexButton", flags));
                files.GetType().GetProperty("SelectedItem")!.SetValue(files, null);
                Assert.False(Enabled("dskClearSelectionButton"));
                var badImage = DskImage.CreateUniform(2, 1, 3, 128, 1, 0x4E, 0, "diagnostics");
                badImage.Tracks[1]!.Sectors[0].FdcStatus1 = 0x40;
                badImage.Tracks[1]!.Sectors[1].FdcStatus2 = 0x20;
                var badBytes = badImage.Serialize();
                var badDocument = DskDocument.Open(badBytes);
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [badDocument]);
                var badLayout = (DskLayoutModel)Field("diskLayout");
                Assert.True(badLayout.Errors > 0); Assert.True(badLayout.Warnings > 0);
                Assert.Equal($"Errors\n{badLayout.Errors}", Field("analysisErrorsButton").GetType().GetProperty("Content")!.GetValue(Field("analysisErrorsButton")));
                Assert.Equal($"Unsafe\n{badLayout.Unsafe}", Field("analysisUnsafeButton").GetType().GetProperty("Content")!.GetValue(Field("analysisUnsafeButton")));
                Assert.Equal(badLayout.Summary, Field("analysisSummaryText").GetType().GetProperty("Text")!.GetValue(Field("analysisSummaryText")));
                var inspector = Field("mapInspectorTabs");
                inspector.GetType().GetProperty("SelectedIndex")!.SetValue(inspector, 1);
                var filter = Field("analysisFilterBox");
                var issues = Field("issuesGrid");
                DskAnalysisIssue[] VisibleIssues() => ((System.Collections.IEnumerable)issues.GetType().GetProperty("ItemsSource")!.GetValue(issues)!).Cast<DskAnalysisIssue>().ToArray();
                Assert.Equal(badLayout.Issues, VisibleIssues());
                type.GetMethod("AnalysisCount_Click", flags)!.Invoke(control, [Field("analysisErrorsButton"), null]);
                Assert.Equal(1, filter.GetType().GetProperty("SelectedIndex")!.GetValue(filter));
                Assert.Equal(1, inspector.GetType().GetProperty("SelectedIndex")!.GetValue(inspector));
                Assert.Contains("Unsafe findings", (string)Field("analysisSeverityText").GetType().GetProperty("Text")!.GetValue(Field("analysisSeverityText"))!);
                Assert.NotEmpty(VisibleIssues());
                Assert.All(VisibleIssues(), issue => Assert.True(issue.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe));
                var error = VisibleIssues().First(issue => issue.Track.HasValue && issue.Sector.HasValue);
                issues.GetType().GetProperty("SelectedItem")!.SetValue(issues, error);
                Assert.True(Enabled("dskClearSelectionButton"));
                var selected = (DskSectorLayout)Field("selectedMapSector");
                Assert.Equal(error.Track, selected.Track); Assert.Equal(error.Sector, selected.PhysicalIndex);
                Assert.Equal(selected, blockGrid.GetType().GetProperty("SelectedItem")!.GetValue(blockGrid));
                Assert.Equal(error, issues.GetType().GetProperty("SelectedItem")!.GetValue(issues));
                Assert.Contains(error.Code, (string)Field("sectorDetailText").GetType().GetProperty("Text")!.GetValue(Field("sectorDetailText"))!);
                Assert.Contains(error.Explanation, (string)Field("sectorDetailText").GetType().GetProperty("Text")!.GetValue(Field("sectorDetailText"))!);
                Assert.Contains("DSK_FDC_STATUS", badLayout.Report());
                filter.GetType().GetProperty("SelectedIndex")!.SetValue(filter, 2);
                Assert.NotEmpty(VisibleIssues()); Assert.All(VisibleIssues(), issue => Assert.Equal(DskIssueSeverity.Warning, issue.Severity));
                Assert.Null(type.GetField("selectedMapSector", flags)!.GetValue(control));
                filter.GetType().GetProperty("SelectedIndex")!.SetValue(filter, 3);
                Assert.All(VisibleIssues(), issue => Assert.Equal(DskIssueSeverity.Info, issue.Severity));
                type.GetMethod("AnalysisCount_Click", flags)!.Invoke(control, [Field("analysisUnsafeButton"), null]);
                Assert.Equal(4, filter.GetType().GetProperty("SelectedIndex")!.GetValue(filter));
                Assert.Equal(badLayout.Unsafe, VisibleIssues().Length);
                Assert.All(VisibleIssues(), issue => Assert.Equal(DskIssueSeverity.Unsafe, issue.Severity));
                filter.GetType().GetProperty("SelectedIndex")!.SetValue(filter, 0);
                Assert.Equal(badBytes, badDocument.Serialize()); Assert.False(badDocument.IsModified);
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [DskDocumentFactory.CreateCpm(false)]);
                Assert.Equal(0, ((DskLayoutModel)Field("diskLayout")).Errors);
                Assert.DoesNotContain(VisibleIssues(), issue => issue.Code == "DSK_FDC_STATUS");
                var infoWithoutSector = VisibleIssues().First(issue => issue.Track == null && issue.FileKey == null);
                issues.GetType().GetProperty("SelectedItem")!.SetValue(issues, infoWithoutSector);
                Assert.True(Enabled("dskClearSelectionButton"));
                Assert.Null(type.GetField("dskMapHexButton", flags));
                type.GetMethod("ClearMapSelection_Click", flags)!.Invoke(control, [null, null]);
                Assert.False(Enabled("dskClearSelectionButton"));
                Assert.Null(issues.GetType().GetProperty("SelectedItem")!.GetValue(issues));
                type.GetMethod("LoadDocument", flags)!.Invoke(control, [propertyDocument]);
                var comparisonRight = DskDocument.Open(propertyDocument.Serialize());
                ((CpmFileSystem)comparisonRight.FileSystem).SetAttributes(comparisonRight.FileSystem.ReadDirectory()[0], true, false, false); comparisonRight.MarkModified();
                var comparison = DskCompareService.Compare(propertyDocument, comparisonRight);
                var comparedFile = comparison.Items.Single(i => i.Level == DskDiffLevel.Filesystem && i.Name == "0:ONE.BIN");
                type.GetMethod("SelectComparisonItem", flags)!.Invoke(control, [comparedFile]);
                Assert.Equal(comparedFile.LeftFileKey, ((DskFileEntry)Field("directoryGrid").GetType().GetProperty("SelectedItem")!.GetValue(Field("directoryGrid"))!).Key);
                var comparedSector = comparison.Items.First(i => i.Level == DskDiffLevel.PhysicalSectors);
                type.GetMethod("SelectComparisonItem", flags)!.Invoke(control, [comparedSector]);
                Assert.Equal(comparedSector.LeftAddress, ((DskSectorLayout)Field("selectedMapSector")).Address);
                DskConversionTests.ExerciseDialogOnCurrentStaThread();
                DskFilePropertyTests.ExerciseDialogOnCurrentStaThread();
                DskCompareTests.ExerciseWindowsOnCurrentStaThread();
                DskStructureTests.ExerciseWindowOnCurrentStaThread();
                CpmSystemBuilderTests.ExerciseDialogOnCurrentStaThread();
                DskHexEditTests.ExerciseWindowOnCurrentStaThread();
                var structureSnapshot = DskStructureService.Build(propertyDocument);
                var structureBlock = structureSnapshot.Items.First(i => i.Tab == DskStructureTab.Allocation && i.Value == "Used");
                type.GetMethod("SelectStructureItem", flags)!.Invoke(control, [structureBlock]);
                Assert.Contains(((DskSectorLayout)Field("selectedMapSector")).Address, structureBlock.Addresses);
                Assert.True(Enabled("dskClearSelectionButton"));
                type.GetMethod("SelectStructureItem", flags)!.Invoke(control, [null]);
                Assert.False(Enabled("dskClearSelectionButton"));
                QuickDiskMapTests.ExerciseEditorOnCurrentStaThread();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA UI initialization timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void MultiIplMenuProgramsAndFreeSectorsAreMappedWithoutChangingWriter()
    {
        var record = new TapeRecord(new byte[TapeRecord.HeaderLength],
            new MZQFileHeader { MzfFtype = 1, MzfFname = new byte[16], MzfSize = 700, MzfStart = 0x1200, MzfExec = 0x1200 },
            new MZQFileBody { DataSize = 700, MzfBody = new byte[700], TrailingData = Array.Empty<byte>() });
        var build = Mz800MultiGameIplDskWriter.Build([
            new(record, "FIRST", new MzfCompressionOptions(MzfCompressionAlgorithm.None), 700),
            new(record, "SECOND", new MzfCompressionOptions(MzfCompressionAlgorithm.None), 700)]);
        var doc = DskDocument.Open(build.Image);
        var map = DskAnalyzer.Analyze(doc);
        Assert.Equal(0, map.Errors);
        Assert.Contains(map.Sectors, s => s.Role.HasFlag(DskSectorRole.Boot));
        Assert.Contains(map.Sectors, s => s.Role.HasFlag(DskSectorRole.System));
        Assert.Contains(map.Sectors, s => s.Role == DskSectorRole.Free);
        foreach (var entry in doc.FileSystem.ReadDirectory())
            Assert.Equal(entry.Blocks, map.Sectors.Count(s => s.Owners.Any(o => o.FileKey == entry.Key)));
        Assert.Equal(build.Image, doc.Serialize()); Assert.False(doc.IsModified);
    }

    [Fact]
    public void BootOnlyMixedGeometryMapsKnownBootstrapAndLeavesOtherDataUnknown()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DSK", "RTK2.DSK"));
        var doc = DskDocument.Open(path);
        var map = DskAnalyzer.Analyze(doc);
        Assert.Equal(2, map.Sectors.Count(s => s.Owners.Any(o => o.FileKey == "iplpro")));
        Assert.Contains(map.Sectors, s => s.Track != 1 && s.Role == DskSectorRole.Unknown);
    }

    private static DskDocument CpmDocument(int preset) => preset switch {
        0 => DskDocumentFactory.CreateCpm(false), 1 => DskDocumentFactory.CreateCpm(true),
        2 => DskDocumentFactory.CreatePersonalCpm80(false), _ => DskDocumentFactory.CreatePersonalCpm80(true) };
    private static byte[] ReadFsmz(DskImage image, int b) => image.Tracks[(b / 16) ^ 1]!.Sectors.Single(s => s.SectorId == b % 16 + 1).Data.Select(v => (byte)(v ^ 255)).ToArray();
    private static void WriteFsmz(DskImage image, int b, byte[] bytes) => bytes.Select(v => (byte)(v ^ 255)).ToArray().CopyTo(image.Tracks[(b / 16) ^ 1]!.Sectors.Single(s => s.SectorId == b % 16 + 1).Data, 0);
    private static byte[] ReadMrs(DskImage image, int b) => image.Tracks[b / 9]!.Sectors.Single(s => s.SectorId == b % 9 + 1).Data.Select(v => (byte)(v ^ 255)).ToArray();
    private static void WriteMrs(DskImage image, int b, byte[] bytes) => bytes.Select(v => (byte)(v ^ 255)).ToArray().CopyTo(image.Tracks[b / 9]!.Sectors.Single(s => s.SectorId == b % 9 + 1).Data, 0);
    private static void WriteWord(byte[] b, int p, int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p, 2), checked((ushort)value));
}
