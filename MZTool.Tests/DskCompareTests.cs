namespace MZTools.Tests;

public class DskCompareTests
{
    private static IEnumerable<DskDiffItem> Changed(DskComparison comparison, DskDiffLevel level) =>
        comparison.Items.Where(i => i.Level == level && i.State is DskDiffState.Changed or DskDiffState.OnlyInLeft or DskDiffState.OnlyInRight);

    [Theory]
    [InlineData("cpm")] [InlineData("fsmz")] [InlineData("mrs")] [InlineData("raw")]
    public void IdenticalImagesHaveNoChangesAndRemainUnmodified(string format)
    {
        var left = format switch { "cpm" => DskDocumentFactory.CreateCpm(false), "fsmz" => DskDocumentFactory.CreateFsmz(), "mrs" => DskDocumentFactory.CreateMrs(), _ => DskDocument.Open(DskImage.CreateUniform(2, 1, 2, 128, 1, 0x4E, 0, "raw").Serialize()) };
        var before = left.Serialize(); var right = DskDocument.Open(before);
        var result = DskCompareService.Compare(left, right);
        Assert.True(result.Identical); Assert.All(Enum.GetValues<DskDiffLevel>(), level => Assert.Empty(Changed(result, level)));
        Assert.Equal(before, left.Serialize()); Assert.Equal(before, right.Serialize()); Assert.False(left.IsModified); Assert.False(right.IsModified);
    }

    [Fact]
    public void OneChangedPhysicalByteHasAnExactHexDiff()
    {
        var left = DskDocumentFactory.CreateCpm(false); var right = DskDocument.Open(left.Serialize());
        right.Image.Tracks[0]!.Sectors[0].Data[37] ^= 0xFF; right.MarkModified();
        var before = right.Serialize(); var result = DskCompareService.Compare(left, right);
        var item = Assert.Single(Changed(result, DskDiffLevel.PhysicalSectors));
        Assert.Equal(new DskSectorAddress(0, 0), item.LeftAddress); Assert.Equal(new DskSectorAddress(0, 0), item.RightAddress);
        var diff = Assert.Single(DskCompareService.ByteDifferences(item.LeftBytes, item.RightBytes)); Assert.Equal(37, diff.Offset);
        Assert.Contains("Data changed: True", item.Detail); Assert.Contains("Metadata changed: False", item.Detail);
        Assert.Equal(before, right.Serialize()); Assert.True(right.IsModified); Assert.Contains("unsaved edits", result.RightName);
    }

    [Fact]
    public void DescriptorAndFdcChangesAreVisibleEvenWithoutDataChanges()
    {
        var left = DskDocumentFactory.CreateCpm(false); var right = DskDocument.Open(left.Serialize());
        right.Image.Tracks[0]!.Sectors[0].FdcStatus2 = 0x20; right.MarkModified();
        var result = DskCompareService.Compare(left, right); var item = Assert.Single(Changed(result, DskDiffLevel.PhysicalSectors));
        Assert.Contains("Data changed: False", item.Detail); Assert.Contains("Metadata changed: True", item.Detail); Assert.Contains("ST1/ST2 changed: True", item.Detail);
        Assert.Equal(5, Assert.Single(DskCompareService.ByteDifferences(item.LeftStructure, item.RightStructure)).Offset);
        Assert.Contains(Changed(result, DskDiffLevel.Container), row => row.Name.Contains("raw header/descriptors"));
    }

    [Fact]
    public void DescriptorReorderingIsComparedByPhysicalPositionNotSectorId()
    {
        var left = DskDocumentFactory.CreateCpm(false); var right = DskDocument.Open(left.Serialize());
        right.Image.Tracks[0]!.Sectors.Reverse(); right.MarkModified();
        var result = DskCompareService.Compare(left, right);
        Assert.Equal(8, Changed(result, DskDiffLevel.PhysicalSectors).Count());
        var first = result.Items.Single(i => i.Level == DskDiffLevel.PhysicalSectors && i.LeftAddress == new DskSectorAddress(0, 0));
        Assert.Equal(1, first.LeftStructure![2]); Assert.Equal(5, first.RightStructure![2]); Assert.Equal(new DskSectorAddress(0, 0), first.RightAddress);
    }

    [Fact]
    public void AddedAndDeletedFilesHaveSeparateStatesAndSideSpecificMapKeys()
    {
        var left = DskDocumentFactory.CreateFsmz(); left.FileSystem.Insert("OLD", [1]); left.MarkModified();
        var right = DskDocument.Open(left.Serialize()); right.FileSystem.Delete(right.FileSystem.ReadDirectory()[0]); right.FileSystem.Insert("NEW", [2]); right.MarkModified();
        var result = DskCompareService.Compare(left, right); var old = result.Items.Single(i => i.Level == DskDiffLevel.Filesystem && i.Name == "OLD"); var added = result.Items.Single(i => i.Level == DskDiffLevel.Filesystem && i.Name == "NEW");
        Assert.Equal(DskDiffState.OnlyInLeft, old.State); Assert.NotNull(old.LeftFileKey); Assert.Null(old.RightFileKey);
        Assert.Equal(DskDiffState.OnlyInRight, added.State); Assert.Null(added.LeftFileKey); Assert.NotNull(added.RightFileKey);
    }

    [Fact]
    public void CpmAttributesAreMetadataChangesWithUnchangedPayloadAndAllocation()
    {
        var left = DskDocumentFactory.CreateCpm(false); left.FileSystem.Insert("TEST.BIN", new byte[70000]); left.MarkModified();
        var right = DskDocument.Open(left.Serialize()); var e = right.FileSystem.ReadDirectory()[0];
        ((CpmFileSystem)right.FileSystem).SetAttributes(e, true, true, true); right.MarkModified();
        var result = DskCompareService.Compare(left, right); var item = result.Items.Single(i => i.Level == DskDiffLevel.Filesystem && i.Name == "0:TEST.BIN");
        Assert.Equal(DskDiffState.Changed, item.State); Assert.Contains("Content changed: False", item.Detail); Assert.Contains("Metadata changed: True", item.Detail); Assert.Contains("Allocation changed: False", item.Detail);
        Assert.Empty(DskCompareService.ByteDifferences(item.LeftBytes, item.RightBytes)); Assert.NotEmpty(DskCompareService.ByteDifferences(item.LeftStructure, item.RightStructure));
    }

    [Fact]
    public void ChangedFilePayloadIsIndependentOfMetadataAndAllocation()
    {
        var left = DskDocumentFactory.CreateCpm(false); left.FileSystem.Insert("TEST.BIN", new byte[512]); left.MarkModified();
        var right = DskDocument.Open(left.Serialize()); var key = right.FileSystem.ReadDirectory()[0].Key;
        var sector = DskAnalyzer.Analyze(right).Sectors.First(s => s.Owners.Any(o => o.FileKey == key));
        var owner = sector.Owners.First(o => o.FileKey == key);
        right.Image.Tracks[sector.Track]!.Sectors[sector.PhysicalIndex].Data[owner.Offset] = 0x7F; right.MarkModified();
        var result = DskCompareService.Compare(left, right); var item = result.Items.Single(i => i.Level == DskDiffLevel.Filesystem && i.Name == "0:TEST.BIN");
        Assert.Contains("Content changed: True", item.Detail); Assert.Contains("Metadata changed: False", item.Detail); Assert.Contains("Allocation changed: False", item.Detail);
        Assert.Equal(0, Assert.Single(DskCompareService.ByteDifferences(item.LeftBytes, item.RightBytes)).Offset);
    }

    [Fact]
    public void CpmUserMoveIsNotGuessedAsTheSameNamespaceEntry()
    {
        var left = DskDocumentFactory.CreateCpm(false); left.FileSystem.Insert("TEST.BIN", [1]); left.MarkModified();
        var right = DskDocument.Open(left.Serialize()); var entry = right.FileSystem.ReadDirectory()[0];
        ((CpmFileSystem)right.FileSystem).UpdateAttributes(entry, 3, false, false, false); right.MarkModified();
        var result = DskCompareService.Compare(left, right);
        Assert.Equal(DskDiffState.OnlyInLeft, result.Items.Single(i => i.Name == "0:TEST.BIN").State);
        Assert.Equal(DskDiffState.OnlyInRight, result.Items.Single(i => i.Name == "3:TEST.BIN").State);
    }

    [Fact]
    public void FsmzRelocationChangesAllocationAndDinfoButNotContent()
    {
        var left = DskDocumentFactory.CreateFsmz(); left.FileSystem.Insert("TEST", new byte[300]); left.MarkModified();
        var right = DskDocument.Open(left.Serialize()); var fs = right.FileSystem;
        var bytes = fs.Extract(fs.ReadDirectory()[0]); fs.Delete(fs.ReadDirectory()[0]); fs.Insert("FILLER", new byte[512]); fs.Insert("TEST", bytes); right.MarkModified();
        var result = DskCompareService.Compare(left, right); var item = result.Items.Single(i => i.Level == DskDiffLevel.Filesystem && i.Name == "TEST");
        Assert.Contains("Content changed: False", item.Detail); Assert.Contains("Allocation changed: True", item.Detail);
        Assert.Contains(Changed(result, DskDiffLevel.Filesystem), i => i.Name.Contains("DINFO"));
    }

    [Fact]
    public void MrsFileIdAndFatOwnerChangesAreVisibleWithoutPayloadChanges()
    {
        var left = DskDocumentFactory.CreateMrs(); left.FileSystem.Insert("TEST.BIN", new byte[512]); left.MarkModified();
        var right = DskDocument.Open(left.Serialize()); right.FileSystem.Delete(right.FileSystem.ReadDirectory()[0]); right.FileSystem.Insert("FILLER.BIN", new byte[512]); right.FileSystem.Insert("TEST.BIN", new byte[512]); right.MarkModified();
        var result = DskCompareService.Compare(left, right); var item = result.Items.Single(i => i.Level == DskDiffLevel.Filesystem && i.Name == "TEST.BIN");
        Assert.Contains("Content changed: False", item.Detail); Assert.Contains("Allocation changed: True", item.Detail); Assert.Contains("file ID", item.Detail);
        Assert.Contains(Changed(result, DskDiffLevel.Filesystem), i => i.Name.Contains("FAT ownership"));
    }

    [Fact]
    public void ContainerTracksCreatorMissingAndTrailingBytesAreCompared()
    {
        var original = DskImage.CreateUniform(2, 1, 2, 128, 1, 0x4E, 0, "old").Serialize();
        var changed = (byte[])original.Clone(); changed[0x35] = 0; var right = DskDocument.Open(changed);
        right.Image.Creator = "new"; right.Image.ContainerTrailingData = [1, 2, 3]; right.ReplaceContents(right.Image.Serialize());
        var result = DskCompareService.Compare(DskDocument.Open(original), right);
        Assert.Contains(Changed(result, DskDiffLevel.Container), i => i.Name == "Creator");
        Assert.Contains(Changed(result, DskDiffLevel.Container), i => i.Name == "Container trailing data");
        Assert.Contains(Changed(result, DskDiffLevel.Container), i => i.Detail.Contains("missing track"));
        Assert.All(Changed(result, DskDiffLevel.PhysicalSectors), i => Assert.Equal(DskDiffState.OnlyInLeft, i.State));
        Assert.Contains(result.Items, i => i.Level == DskDiffLevel.Filesystem && i.State == DskDiffState.Unavailable);
    }

    [Fact]
    public void DifferentSectorLengthsAndAbsentBytesHaveExactOffsetSemantics()
    {
        var a = DskImage.CreateUniform(2, 1, 1, 128, 1, 0x4E, 0, "raw"); var b = DskImage.CreateUniform(2, 1, 1, 256, 1, 0x4E, 0, "raw");
        var result = DskCompareService.Compare(DskDocument.Open(a.Serialize()), DskDocument.Open(b.Serialize()));
        var item = result.Items.First(i => i.Level == DskDiffLevel.PhysicalSectors);
        Assert.Contains("Byte count changed: True", item.Detail);
        var changes = DskCompareService.ByteDifferences(item.LeftBytes, item.RightBytes).ToArray(); Assert.Equal(128, changes.Length);
        Assert.Equal(128, changes[0].Offset); Assert.Null(changes[0].Left); Assert.Equal("--", changes[0].LeftHex); Assert.Equal("00", changes[0].RightHex);
    }

    internal static void ExerciseWindowsOnCurrentStaThread()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var left = DskDocumentFactory.CreateCpm(false); var right = DskDocument.Open(left.Serialize());
        right.Image.Tracks[0]!.Sectors[0].FdcStatus2 = 0x20; right.MarkModified();
        var comparison = DskCompareService.Compare(left, right);
        var row = comparison.Items.First(i => i.Level == DskDiffLevel.PhysicalSectors && i.State == DskDiffState.Changed);
        DskDiffItem? selected = null; var window = new DskCompareWindow(null, comparison, item => selected = item);
        object Field(object instance, string name) => instance.GetType().GetField(name, flags)!.GetValue(instance)!;
        bool Enabled(object instance, string name) => (bool)Field(instance, name).GetType().GetProperty("IsEnabled")!.GetValue(Field(instance, name))!;
        Assert.False(Enabled(window, "hex")); window.Select(row); Assert.True(Enabled(window, "hex")); Assert.Same(row, selected);
        var mapSelection = typeof(DskDiskMapControl).GetField("selectedSector", flags)!.GetValue(Field(window, "rightMap")); Assert.NotNull(mapSelection);
        var hex = new DskHexDiffWindow(null, row);
        Assert.Equal(1, Field(hex, "mode").GetType().GetProperty("SelectedIndex")!.GetValue(Field(hex, "mode")));
        var items = (System.Collections.IList)Field(hex, "bytes").GetType().GetProperty("Items")!.GetValue(Field(hex, "bytes"))!;
        Assert.Equal(5, Assert.IsType<DskByteDifference>(Assert.Single(items.Cast<object>())).Offset);
        window.Select(null); Assert.False(Enabled(window, "hex")); Assert.Null(selected);
        Assert.NotSame(Field(window, "detail"), Field(window, "rightDetail"));
        window.Select(row);
        Assert.Contains("Stored length", (string)Field(window, "detail").GetType().GetProperty("Text")!.GetValue(Field(window, "detail"))!);
        Assert.Contains("Stored length", (string)Field(window, "rightDetail").GetType().GetProperty("Text")!.GetValue(Field(window, "rightDetail"))!);
        window.Select(null);
        Assert.Null(typeof(DskDiskMapControl).GetField("selectedSector", flags)!.GetValue(Field(window, "rightMap")));
        ExerciseSynchronizedMapScroll(window, Field);
        hex.GetType().GetMethod("Close")!.Invoke(hex, null); window.GetType().GetMethod("Close")!.Invoke(window, null);

        byte[] valid = left.Serialize(), legacy = (byte[])valid.Clone();
        int lengthOffset = DskImage.Parse(valid).Tracks[0]!.FileOffset + 0x18 + 6;
        legacy[lengthOffset] = legacy[lengthOffset + 1] = 0;
        var lengthComparison = DskCompareService.Compare(DskDocument.Open(legacy), DskDocument.Open(valid));
        var lengthWindow = new DskCompareWindow(null, lengthComparison);
        Assert.True(Enabled(lengthWindow, "hex"));
        Assert.Equal((int)DskDiffLevel.PhysicalSectors, Field(lengthWindow, "tabs").GetType().GetProperty("SelectedIndex")!.GetValue(Field(lengthWindow, "tabs")));
        Assert.Contains("Stored length: 0 B", (string)Field(lengthWindow, "detail").GetType().GetProperty("Text")!.GetValue(Field(lengthWindow, "detail"))!);
        Assert.Contains($"Stored length: {DskImage.Parse(valid).Tracks[0]!.Sectors[0].Data.Length} B", (string)Field(lengthWindow, "rightDetail").GetType().GetProperty("Text")!.GetValue(Field(lengthWindow, "rightDetail"))!);
        lengthWindow.GetType().GetMethod("Close")!.Invoke(lengthWindow, null);
    }

    private static void ExerciseSynchronizedMapScroll(DskCompareWindow window, Func<object, string, object> field)
    {
        var left = field(window, "leftScroll"); var right = field(window, "rightScroll");
        var type = left.GetType(); var sizeType = type.GetMethod("Measure")!.GetParameters()[0].ParameterType;
        var rectType = type.GetMethod("Arrange")!.GetParameters()[0].ParameterType;
        var borderType = type.Assembly.GetType("System.Windows.Controls.Border")!;
        foreach (var viewer in new[] { left, right })
        {
            var content = Activator.CreateInstance(borderType)!;
            borderType.GetProperty("Width")!.SetValue(content, 1000d);
            borderType.GetProperty("Height")!.SetValue(content, ReferenceEquals(viewer, left) ? 2000d : 1000d);
            type.GetProperty("Content")!.SetValue(viewer, content);
            type.GetMethod("Measure")!.Invoke(viewer, [Activator.CreateInstance(sizeType, [200d, 150d])]);
            type.GetMethod("Arrange")!.Invoke(viewer, [Activator.CreateInstance(rectType, [0d, 0d, 200d, 150d])]);
        }
        void Layout()
        {
            for (int i = 0; i < 4; i++)
            {
                foreach (var viewer in new[] { left, right })
                {
                    type.GetMethod("InvalidateMeasure")!.Invoke(viewer, null);
                    type.GetMethod("Measure")!.Invoke(viewer, [Activator.CreateInstance(sizeType, [200d, 150d])]);
                    type.GetMethod("Arrange")!.Invoke(viewer, [Activator.CreateInstance(rectType, [0d, 0d, 200d, 150d])]);
                    type.GetMethod("UpdateLayout")!.Invoke(viewer, null);
                }
            }
        }
        double Offset(object viewer, string name) => (double)type.GetProperty(name)!.GetValue(viewer)!;
        void Scroll(object viewer, double x, double y)
        { type.GetMethod("ScrollToHorizontalOffset")!.Invoke(viewer, [x]); type.GetMethod("ScrollToVerticalOffset")!.Invoke(viewer, [y]); Layout(); }
        Layout(); Assert.True(Offset(left, "ScrollableWidth") > 120); Assert.True(Offset(right, "ScrollableHeight") > 400); Scroll(left, 120, 400);
        Assert.Equal(120d, Offset(left, "HorizontalOffset")); Assert.Equal(400d, Offset(left, "VerticalOffset"));
        Assert.Equal(120d, Offset(right, "HorizontalOffset")); Assert.Equal(400d, Offset(right, "VerticalOffset"));
        Scroll(right, 40, 200);
        Assert.Equal(40d, Offset(left, "HorizontalOffset")); Assert.Equal(200d, Offset(left, "VerticalOffset"));
        Scroll(left, 80, 1600);
        Assert.Equal(1600d, Offset(left, "VerticalOffset"));
        Assert.Equal(Offset(right, "ScrollableHeight"), Offset(right, "VerticalOffset"));
        Scroll(right, 0, 50); Assert.Equal(50d, Offset(left, "VerticalOffset")); Assert.Equal(0d, Offset(left, "HorizontalOffset"));
    }
}
