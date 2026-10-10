namespace MZTools.Tests;

public class CustomCpmLayoutTests
{
    [Theory]
    [InlineData(8, 512, new int[] { 1, 5, 2, 6, 3, 7, 4, 8 })]
    [InlineData(16, 256, new int[] { 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15, 8, 16 })]
    public void PersonalOrderIsExactAndSurvivesReopen(int count, int size, int[] expected)
    {
        var document = DskDocumentFactory.CreateRaw(2, 2, count, size, 1, 0x4E, 0xE5, "test", DskDocumentFactory.RawSectorOrder.PersonalCpm80);
        var reopened = DskDocument.Open(document.Serialize());
        foreach (var track in reopened.Image.Tracks)
            Assert.Equal(expected, track!.Sectors.Select(s => (int)s.SectorId));
    }

    [Theory]
    [InlineData(9, 1)]
    [InlineData(8, 0)]
    [InlineData(16, 2)]
    public void PersonalOrderRejectsUnsupportedGeometry(int count, int first)
    {
        Assert.Throws<ArgumentException>(() => DskDocumentFactory.CreateRaw(2, 1, count, 512, first, 0x4E, 0xE5, "test", DskDocumentFactory.RawSectorOrder.PersonalCpm80));
    }

    private static (DskDocument Document, CpmDpb Dpb) CustomDisk()
    {
        var image = DskImage.CreateUniform(10, 1, 8, 512, 1, 0x4E, 0xE5, "custom");
        var dpb = new CpmDpb(32, 3, 7, 0, 35, 31, 0x80, 0, 8, 1, 1024, "Custom test");
        CpmFileSystem.Format(image, dpb);
        return (DskDocument.Open(image.Serialize()), dpb);
    }

    [Fact]
    public void MaintenancePreviewRejectsChangedInterpretationWithoutChangingBytesOrModifiedFlag()
    {
        var (document, dpb) = CustomDisk();
        CpmLayoutService.Apply(document, CpmLayoutService.Preview(document, dpb));
        var preview = DskDefragmentService.Preview(document);
        CpmLayoutService.Apply(document, CpmLayoutService.Preview(document, dpb with { Name = "Changed interpretation", Cks = 0 }));
        byte[] before = document.Serialize();
        Assert.Throws<InvalidOperationException>(() => preview.Apply(document));
        Assert.Equal(before, document.Serialize()); Assert.False(document.IsModified);
    }

    [Fact]
    public void PreviewAndAttachLeaveBytesAndModifiedStateUnchanged()
    {
        var (document, dpb) = CustomDisk();
        byte[] original = document.Serialize();
        var preview = CpmLayoutService.Preview(document, dpb);
        Assert.True(preview.CanApply, preview.Report);
        Assert.Equal(original, document.Serialize()); // cancellation
        CpmLayoutService.Apply(document, preview);
        Assert.IsType<CpmFileSystem>(document.FileSystem);
        Assert.Equal(original, document.Serialize());
        Assert.False(document.IsModified);
        Assert.Equal(dpb, ((CpmFileSystem)document.Clone().FileSystem).Dpb);
        document.ReplaceContents(original);
        Assert.Equal(dpb, ((CpmFileSystem)document.FileSystem).Dpb);
    }

    [Fact]
    public void InvalidDpbAndAliasAreReportedWithoutMutation()
    {
        var (document, dpb) = CustomDisk();
        byte[] original = document.Serialize();
        foreach (var wrong in new[] { dpb with { Dsm = 200 }, dpb with { Blm = 15 }, dpb with { PhysicalTrackMap = new[] { 0, 2, 2, 3, 4, 5, 6, 7, 8, 9 } } })
        {
            var preview = CpmLayoutService.Preview(document, wrong);
            Assert.False(preview.CanApply, preview.Report);
            Assert.Equal(original, document.Serialize());
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InvalidDirectoryOwnershipIsShownAndCannotBeAttached(bool outOfRange)
    {
        var (document, dpb) = CustomDisk();
        CpmLayoutService.Apply(document, CpmLayoutService.Preview(document, dpb));
        document.FileSystem.Insert("FIRST.BIN", new byte[128]); document.FileSystem.Insert("SECOND.BIN", new byte[128]); document.MarkModified();
        var fs = (CpmFileSystem)document.FileSystem;
        byte[] first = fs.ReadRawDirectoryEntry(0), second = fs.ReadRawDirectoryEntry(1);
        second[16] = outOfRange ? checked((byte)(dpb.Dsm + 1)) : first[16];
        fs.WriteRawDirectoryEntry(1, second);
        byte[] bytes = document.Image.Serialize(); var damaged = DskDocument.Open(bytes);
        var preview = CpmLayoutService.Preview(damaged, dpb);
        Assert.False(preview.CanApply);
        Assert.Contains(outOfRange ? "CPM_BLOCK_OUT_OF_RANGE" : "CPM_CROSSLINKED_BLOCK", preview.Report);
        Assert.Throws<InvalidOperationException>(() => CpmLayoutService.Apply(damaged, preview));
        Assert.Equal(bytes, damaged.Serialize()); Assert.False(damaged.IsModified);
    }

    [Fact]
    public void AttachedLayoutIsUsedByPropertiesInspectorAndHexEditor()
    {
        var (document, dpb) = CustomDisk();
        CpmLayoutService.Apply(document, CpmLayoutService.Preview(document, dpb));
        document.FileSystem.Insert("TEST.BIN", Enumerable.Repeat((byte)0x42, 256).ToArray());
        document.MarkModified();
        var entry = document.FileSystem.ReadDirectory().Single();
        DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { ReadOnly = true });
        Assert.True(document.FileSystem.ReadDirectory().Single().ReadOnly);
        Assert.Equal("Custom test", DskStructureService.Build(document).Layout.FileSystem);
        var session = DskHexEditService.OpenCpmBlock(document, 1);
        var replacement = session.OriginalBuffer; replacement[0] = 0x5A;
        DskHexEditService.Apply(document, DskHexEditService.Preview(session, replacement));
        Assert.Equal(0x5A, document.FileSystem.Extract(document.FileSystem.ReadDirectory().Single())[0]);
    }

    [Fact]
    public void UserDefinedUiMapsOrderExplicitlyAndAllNewDialogsConstructOnSta()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var owner = new System.Windows.Window();
                new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
                var dialog = (DskNewDialog)typeof(DskNewDialog).GetConstructors(flags).Single().Invoke([owner]);
                var format = (System.Windows.Controls.ComboBox)typeof(DskNewDialog).GetField("formatBox", flags)!.GetValue(dialog)!;
                format.SelectedItem = format.Items.Cast<object>().Single(item => item.ToString()!.StartsWith("User defined image", StringComparison.Ordinal));
                var order = (System.Windows.Controls.ComboBox)typeof(DskNewDialog).GetField("orderBox", flags)!.GetValue(dialog)!;
                var ids = (System.Windows.Controls.TextBox)typeof(DskNewDialog).GetField("sectorIdsBox", flags)!.GetValue(dialog)!;
                order.SelectedItem = order.Items.Cast<object>().Single(item => item.ToString() == "P-CP/M80 interleave");
                Assert.Equal(DskDocumentFactory.RawSectorOrder.PersonalCpm80, typeof(DskNewDialog).GetProperty("SelectedSectorOrder", flags)!.GetValue(dialog)); Assert.True(ids.IsReadOnly);
                Assert.Equal("1,9,2,10,3,11,4,12,5,13,6,14,7,15,8,16", ids.Text);
                Assert.False(ids.IsEnabled);
                var sectors = (System.Windows.Controls.TextBox)typeof(DskNewDialog).GetField("sectorsBox", flags)!.GetValue(dialog)!;
                sectors.Text = "8";
                Assert.Equal("1,5,2,6,3,7,4,8", ids.Text);
                sectors.Text = "9";
                Assert.Contains("requires 8 or 16", ids.Text);
                foreach (var item in order.Items.Cast<object>().Where(item => item.ToString() is "Normal" or "LEC interleave 2" or "LEC HD interleave 3"))
                {
                    order.SelectedItem = item;
                    var selected = (DskDocumentFactory.RawSectorOrder)typeof(DskNewDialog).GetProperty("SelectedSectorOrder", flags)!.GetValue(dialog)!;
                    var expected = DskDocumentFactory.CreateRaw(1, 1, 9, 256, 1, 0x4E, 0xFF, "test", selected);
                    Assert.Equal(string.Join(",", expected.Image.Tracks[0]!.Sectors.Select(sector => sector.SectorId)), ids.Text);
                    Assert.True(ids.IsReadOnly);
                    Assert.False(ids.IsEnabled);
                }
                order.SelectedItem = order.Items.Cast<object>().Single(item => item.ToString() == "Custom sector IDs"); Assert.False(ids.IsReadOnly);
                Assert.True(ids.IsEnabled);
                ids.Text = "9,8,7,6,5,4,3,2,1";
                order.SelectedIndex = 0;
                order.SelectedItem = order.Items.Cast<object>().Single(item => item.ToString() == "Custom sector IDs");
                Assert.Equal("9,8,7,6,5,4,3,2,1", ids.Text);
                Assert.Equal(DskDocumentFactory.RawSectorOrder.Custom, typeof(DskNewDialog).GetProperty("SelectedSectorOrder", flags)!.GetValue(dialog)); dialog.Close();
                var disk = DskDocumentFactory.CreatePersonalCpm80(false);
                new CpmLayoutDialog(owner, disk).Close(); new DskPhysicalPropertiesDialog(owner, disk).Close(); new DskRawRangeDialog(owner, disk).Close();
                new BootstrapDialog(owner, disk).Close(); new DskRepairDialog(owner, disk.Serialize()).Close(); new BatchProcessDialog(owner).Close();
                new PhysicalTrackViewerWindow(owner, HfeImage.FromDsk(DskDocumentFactory.CreateRaw(1, 1, 8, 512, 1, 0x4E, 0xE5, "test"))).Close();
                owner.Close();
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA dialog checks timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void ChangedInterpretationInvalidatesPendingHexPreviewEvenWithIdenticalBytes()
    {
        var (document, dpb) = CustomDisk(); CpmLayoutService.Apply(document, CpmLayoutService.Preview(document, dpb));
        var session = DskHexEditService.OpenCpmBlock(document, 1); var data = session.OriginalBuffer; data[0] ^= 1;
        var preview = DskHexEditService.Preview(session, data);
        CpmLayoutService.Apply(document, CpmLayoutService.Preview(document, dpb with { Name = "Alternate", Cks = 0 }));
        var before = document.Serialize(); Assert.Throws<InvalidDataException>(() => DskHexEditService.Apply(document, preview)); Assert.Equal(before, document.Serialize());
    }
}
