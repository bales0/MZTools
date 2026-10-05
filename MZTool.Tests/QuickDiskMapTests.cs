using System.Reflection;

namespace MZTools.Tests;

public class QuickDiskMapTests
{
    [Theory]
    [InlineData("Mzq")]
    [InlineData("Qdf")]
    [InlineData("QdSharpLegacy")]
    [InlineData("QdHxc")]
    [InlineData("QdFlashFloppy")]
    public void MapsOriginalAndRebuiltImagesWithoutChangingDocument(string formatName)
    {
        var document = MakeDocument(formatName);
        byte[] image = QuickDiskLayoutBuilder.BuildPreviewImage(document);
        document.QuickDiskSourceImage = image;
        document.IsModified = false;
        byte[] before = (byte[])image.Clone();
        var original = QuickDiskLayoutBuilder.Build(document);
        Assert.False(original.IsPreview);
        Assert.Contains("Original image", original.Summary);
        Assert.Equal(image, before);
        Assert.False(document.IsModified);
        AssertMap(original, image, document.Records);
        document.Records.Reverse();
        document.IsModified = true;
        var preview = QuickDiskLayoutBuilder.Build(document);
        Assert.True(preview.IsPreview);
        Assert.Contains("not original positions", preview.Summary);
        Assert.Equal("SECOND", preview.Regions.First(r => r.Kind == QuickDiskRegionKind.Header).Name);
        Assert.Equal(before, document.QuickDiskSourceImage);
        Assert.True(document.IsModified);
        AssertMap(preview, QuickDiskLayoutBuilder.BuildPreviewImage(document), document.Records);
        document.Clear();
        Assert.Null(document.QuickDiskSourceImage);
    }

    [Theory]
    [InlineData("Mzq")]
    [InlineData("Qdf")]
    [InlineData("QdSharpLegacy")]
    [InlineData("QdHxc")]
    [InlineData("QdFlashFloppy")]
    public void NewEmptyImagesHaveCountAndNoPayloads(string formatName)
    {
        var document = MakeDocument(formatName);
        document.Records.Clear();
        var map = QuickDiskLayoutBuilder.Build(document);
        Assert.True(map.IsPreview);
        Assert.Single(map.Regions, r => r.Kind == QuickDiskRegionKind.Count);
        Assert.DoesNotContain(map.Regions, r => r.FileIndex >= 0);
        AssertCoverage(map);
    }

    [Theory]
    [InlineData("hxc_Blank.QD")]
    [InlineData("hxc_Blank_MZ_Formated.QD")]
    [InlineData("hxc_Blank_MZ_Formated_TC122.QD")]
    [InlineData("hxc_MarioMZ1500.qd")]
    [InlineData("hxc_MarioMZ1500_MZ_Formatedandloaded.qd")]
    [InlineData("FF_Blank.qd")]
    [InlineData("FF_Blank_MZ_Formated.qd")]
    [InlineData("FF_Blank_MZ_Formated_TC122.qd")]
    [InlineData("FF_Flappy_UL800.qd")]
    [InlineData("Flappy.qd")]
    public void FixturePositionsPointToActualPayloadAndRetainContainerWindow(string name)
    {
        byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "QD", name));
        var read = QdImageReaderWriter.Read(image);
        var document = new TapeDocument { Format = read.DocumentFormat, QuickDiskSourceImage = image, QuickDiskProfile = read.PhysicalProfile };
        document.Records.AddRange(read.Records);
        var map = QuickDiskLayoutBuilder.Build(document);
        AssertMap(map, image, read.Records);
        if (read.PhysicalProfile is { } profile)
        {
            Assert.Equal(profile.DataOffset, map.TrackFileOffset);
            Assert.Equal(profile.WindowStart * 8L, map.WindowStart);
            Assert.Equal(profile.WindowEnd * 8L, map.WindowEnd);
        }
    }

    [Fact]
    public void QdfOffsetsMatchBytesAndCompactMzqMatchesExistingWriter()
    {
        var document = MakeDocument("Qdf");
        var image = QDFFileReader.BuildImage(document.Records);
        var map = QuickDiskLayoutBuilder.Read(image, document.Format);
        var count = Assert.Single(map.Regions, r => r.Kind == QuickDiskRegionKind.Count);
        Assert.Equal(0x12F3, count.Start);
        Assert.Equal(0xA5, image[count.Start]);
        Assert.Equal(4, count.Length);
        Assert.False(map.IsPhysical);
        string path = Path.Combine(Path.GetTempPath(), $"mztools-map-{Guid.NewGuid():N}.mzq");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew))
            {
                var writer = new MZQFileReader();
                writer.WriteMZQHeaderToFile(stream, 4);
                foreach (var record in document.Records)
                {
                    writer.WriteMZQFileHeaderToFile(stream, record.Header);
                    writer.WriteMZQFileBodyToFile(stream, record.Body);
                }
            }
            Assert.Equal(File.ReadAllBytes(path), SharpLegacyQdCodec.BuildCompactImage(document.Records));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ZeroLengthPayloadKeepsFramingAndCrcWithoutInventingData()
    {
        foreach (string format in new[] { "Mzq", "Qdf", "QdSharpLegacy", "QdHxc", "QdFlashFloppy" })
        {
            var document = MakeDocument(format);
            document.Records.Clear(); document.Records.Add(Record("EMPTY", 0));
            var map = QuickDiskLayoutBuilder.Build(document);
            Assert.Single(map.Regions, r => r.Kind == QuickDiskRegionKind.Header);
            Assert.Single(map.Regions, r => r.Kind == QuickDiskRegionKind.Crc);
            Assert.DoesNotContain(map.Regions, r => r.Kind == QuickDiskRegionKind.Payload);
            AssertCoverage(map);
        }
    }

    [Fact]
    public void CapacityFailureDoesNotModifyOriginal()
    {
        var document = MakeDocument("QdSharpLegacy");
        var source = QuickDiskLayoutBuilder.BuildPreviewImage(document);
        document.QuickDiskSourceImage = source;
        document.Records.Add(Record("TOO BIG", 65000));
        Assert.Throws<InvalidDataException>(() => QuickDiskLayoutBuilder.Build(document));
        Assert.Same(source, document.QuickDiskSourceImage);
        Assert.True(document.IsModified);
    }

    [Fact]
    public void UnmodifiedNonStandardImportedDirectoryDoesNotRequireRebuild()
    {
        var records = Enumerable.Range(0, 38).Select(i => Record($"FILE{i}", 1)).ToList();
        var image = SharpLegacyQdCodec.Write(records, allowImportedNonStandard: true);
        var document = new TapeDocument { Format = TapeDocumentFormat.QdSharpLegacy, QuickDiskSourceImage = image };
        document.Records.AddRange(SharpLegacyQdCodec.Read(image));
        var map = QuickDiskLayoutBuilder.Build(document);
        Assert.False(map.IsPreview);
        Assert.Equal(38, map.Regions.Count(r => r.Kind == QuickDiskRegionKind.Header));
        document.IsModified = true;
        Assert.Throws<InvalidDataException>(() => QuickDiskLayoutBuilder.Build(document));
    }

    [Fact]
    public void BadCrcAndTruncatedContainersDoNotYieldMisleadingMap()
    {
        var document = MakeDocument("Qdf");
        var image = QuickDiskLayoutBuilder.BuildPreviewImage(document);
        var map = QuickDiskLayoutBuilder.Read(image, document.Format);
        image[map.Regions.First(r => r.Kind == QuickDiskRegionKind.Payload).Start] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => QuickDiskLayoutBuilder.Read(image, document.Format));
        Assert.Throws<InvalidDataException>(() => QuickDiskLayoutBuilder.Read(image[..20], document.Format));
        document = MakeDocument("QdHxc");
        image = QuickDiskLayoutBuilder.BuildPreviewImage(document);
        Assert.Throws<InvalidDataException>(() => QuickDiskLayoutBuilder.Read(image[..1024], document.Format));
    }

    [Theory]
    [InlineData("QdHxc")]
    [InlineData("QdFlashFloppy")]
    public void PhysicalHexViewDecodesValidatedFramesAndPacksRawGapBits(string format)
    {
        var document = MakeDocument(format);
        var map = QuickDiskLayoutBuilder.Build(document);
        foreach (var region in map.Regions.Where(r => r.Kind is QuickDiskRegionKind.Header or QuickDiskRegionKind.Count))
            Assert.True(SharpQdFrameCodec.HasValidCrc(map.GetBlockBytes(region)));
        foreach (var record in document.Records.Select((value, index) => (value, index)))
        {
            var bytes = map.Regions.Where(r => r.FileIndex == record.index && r.Kind is QuickDiskRegionKind.Framing or QuickDiskRegionKind.Payload or QuickDiskRegionKind.Crc)
                .SelectMany(map.GetBlockBytes).ToArray();
            Assert.Equal(SharpQdFrameCodec.EncodeBodyFrame(record.value.Body), bytes);
        }
        var gap = map.Regions.First(r => r.Kind == QuickDiskRegionKind.Gap && r.Length > 8);
        // Exercise an unaligned slice even when the container's gaps happen to align.
        if (gap.Start % 8 == 0) gap = gap with { Start = gap.Start + 1, Length = gap.Length - 2 };
        map = map with { Regions = [gap] };
        var raw = map.GetBlockBytes(gap);
        Assert.Equal((gap.Length + 7) / 8, raw.LongLength);
        for (long bit = 0; bit < gap.Length; bit++)
        {
            long position = gap.Start + bit;
            bool expected = (map.Image[map.TrackFileOffset + position / 8] & (1 << (int)(position % 8))) != 0;
            Assert.Equal(expected, (raw[bit / 8] & (1 << (int)(bit % 8))) != 0);
        }
    }

    private static void AssertMap(QuickDiskLayout map, byte[] image, IReadOnlyList<TapeRecord> records)
    {
        AssertCoverage(map);
        Assert.Equal(records.Count, map.Regions.Count(r => r.Kind == QuickDiskRegionKind.Header));
        foreach (var region in map.Regions.Where(r => r.Kind == QuickDiskRegionKind.Payload))
        {
            var record = records[region.FileIndex];
            Assert.Equal(record.Header.MzfStart, region.Load);
            Assert.Equal(record.Header.MzfExec, region.Execute);
            Assert.Equal(record.Body.DataSize, region.Size);
            Assert.Equal(record.Body.DataSize * (map.IsPhysical ? 16L : 1), region.Length);
            var payload = new byte[region.Size];
            if (!map.IsPhysical) Array.Copy(image, region.Start, payload, 0, payload.Length);
            else
                for (int index = 0; index < payload.Length; index++)
                    for (int bit = 0; bit < 8; bit++)
                    {
                        long cell = region.Start + index * 16L + bit * 2;
                        if ((image[map.TrackFileOffset + cell / 8] & (1 << (int)(cell % 8))) != 0)
                            payload[index] |= (byte)(1 << bit);
                    }
            Assert.Equal(record.Body.MzfBody.AsSpan(0, record.Body.DataSize).ToArray(), payload);
            Assert.Equal(payload, map.GetBlockBytes(region));
            Assert.Contains(region.Name, map.Detail(region));
        }
    }

    private static void AssertCoverage(QuickDiskLayout map)
    {
        Assert.Equal(0, map.Regions[0].Start);
        Assert.Equal(map.Length, map.Regions[^1].End);
        Assert.All(map.Regions, r => Assert.True(r.Length > 0));
        for (int index = 1; index < map.Regions.Count; index++) Assert.Equal(map.Regions[index - 1].End, map.Regions[index].Start);
    }

    private static TapeDocument MakeDocument(string formatName)
    {
        var format = Enum.Parse<TapeDocumentFormat>(formatName);
        var document = new TapeDocument { Format = format, IsModified = true };
        document.Records.Add(Record("FIRST", 400)); document.Records.Add(Record("SECOND", 1700));
        return document;
    }
    private static TapeRecord Record(string name, int size)
    {
        var header = new MZQFileHeader
        {
            StartSign = [0, 0x16, 0x16, 0xA5], MzfHeaderSign = 0, DataSize = 64, MzfFtype = 1,
            MzfFname = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(name.PadRight(16)),
            MzfFnameEnd = 0x0D, Unused1 = new byte[2], MzfSize = (ushort)size, MzfStart = 0x1200, MzfExec = 0x1210,
            MzfHeaderDescription = new byte[104], Crc = [67, 82, 67]
        };
        var body = new MZQFileBody
        {
            StartSign = [0, 0x16, 0x16, 0xA5], MzfBodySign = 5, DataSize = (ushort)size,
            MzfBody = Enumerable.Range(0, size).Select(i => (byte)(i * 17)).ToArray(), Crc = [67, 82, 67], TrailingData = []
        };
        return TapeRecord.FromLegacy(header, body);
    }

    // Invoked by the existing STA UI test: WPF permits only one Application per AppDomain.
    internal static void ExerciseEditorOnCurrentStaThread()
    {
        var type = typeof(MainWindow);
        object window = Activator.CreateInstance(type)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object Field(string name) => type.GetField(name, flags)!.GetValue(window)!;
        bool Enabled(string name) => (bool)Field(name).GetType().GetProperty("IsEnabled")!.GetValue(Field(name))!;
        void Invoke(string name, params object?[] arguments) => type.GetMethod(name, flags)!.Invoke(window, arguments);
        var document = (TapeDocument)Field("document");
        object tab = Field("quickDiskMapTab");
        object grid = Field("quickDiskBlocks");
        foreach (string format in new[] { "Mzq", "Qdf", "QdSharpLegacy", "QdHxc", "QdFlashFloppy" })
        {
            var sourceDocument = MakeDocument(format);
            var image = QuickDiskLayoutBuilder.BuildPreviewImage(sourceDocument);
            document.Clear(); document.Format = sourceDocument.Format;
            document.Records.AddRange(sourceDocument.Records);
            document.QuickDiskSourceImage = image;
            Invoke("RefreshGrid");
            Assert.False(Enabled("quickDiskClearSelectionButton"));
            tab.GetType().GetProperty("IsSelected")!.SetValue(tab, true);
            Invoke("RefreshQuickDiskMap");
            var map = (QuickDiskLayout)Field("quickDiskLayout");
            Assert.False(map.IsPreview);
            var region = map.Regions.First(r => r.Kind == QuickDiskRegionKind.Payload);
            grid.GetType().GetProperty("SelectedItem")!.SetValue(grid, region);
            Assert.True(Enabled("quickDiskClearSelectionButton"));
            var fileGrid = Field("MzfDataGrid");
            Assert.Equal(region.FileIndex, fileGrid.GetType().GetProperty("SelectedIndex")!.GetValue(fileGrid));
            Assert.Contains(region.Name, (string)Field("quickDiskMapDetail").GetType().GetProperty("Text")!.GetValue(Field("quickDiskMapDetail"))!);
            var hex = ((string Title, byte[] Bytes)?)type.GetMethod("GetSelectedQuickDiskHexBlock", flags)!.Invoke(window, null);
            Assert.NotNull(hex);
            Assert.Equal(document.Records[region.FileIndex].Body.MzfBody, hex.Value.Bytes);
            Assert.True((bool)Field("quickDiskHexButton").GetType().GetProperty("IsEnabled")!.GetValue(Field("quickDiskHexButton"))!);
            fileGrid.GetType().GetProperty("SelectedItem")!.SetValue(fileGrid, null);
            Assert.False(Enabled("quickDiskClearSelectionButton"));
            Assert.Null(grid.GetType().GetProperty("SelectedItem")!.GetValue(grid));
            var mapSurface = Field("quickDiskMap");
            Assert.Null(mapSurface.GetType().GetField("selection", flags)!.GetValue(mapSurface));
            Assert.Empty((HashSet<int>)mapSurface.GetType().GetField("selectedFiles", flags)!.GetValue(mapSurface)!);
            grid.GetType().GetProperty("SelectedItem")!.SetValue(grid, region);
            grid.GetType().GetProperty("SelectedItem")!.SetValue(grid, null);
            Assert.False(Enabled("quickDiskClearSelectionButton"));
            Assert.Equal(-1, fileGrid.GetType().GetProperty("SelectedIndex")!.GetValue(fileGrid));
            Assert.Null(mapSurface.GetType().GetField("selection", flags)!.GetValue(mapSurface));
            Assert.Empty((HashSet<int>)mapSurface.GetType().GetField("selectedFiles", flags)!.GetValue(mapSurface)!);
            Assert.False(document.IsModified);
            Assert.Same(image, document.QuickDiskSourceImage);
            document.IsModified = true; document.Records.Reverse();
            Invoke("RefreshGrid");
            Assert.True(((QuickDiskLayout)Field("quickDiskLayout")).IsPreview);
            object surface = Field("quickDiskMap");
            var sizeType = surface.GetType().GetMethod("Measure")!.GetParameters()[0].ParameterType;
            surface.GetType().GetMethod("Measure")!.Invoke(surface, [Activator.CreateInstance(sizeType, [1000d, 300d])]);
            var rectType = surface.GetType().GetMethod("Arrange")!.GetParameters()[0].ParameterType;
            surface.GetType().GetMethod("Arrange")!.Invoke(surface, [Activator.CreateInstance(rectType, [0d, 0d, 1000d, 300d])]);
            var core = Assembly.Load("PresentationCore");
            var pixelFormat = core.GetType("System.Windows.Media.PixelFormats")!.GetProperty("Pbgra32")!.GetValue(null)!;
            var bitmapType = core.GetType("System.Windows.Media.Imaging.RenderTargetBitmap")!;
            var bitmap = Activator.CreateInstance(bitmapType, [1000, 300, 96d, 96d, pixelFormat])!;
            bitmapType.GetMethod("Render")!.Invoke(bitmap, [surface]);
            var rebuiltMap = (QuickDiskLayout)Field("quickDiskLayout");
            var payload = rebuiltMap.Regions.First(r => r.Kind == QuickDiskRegionKind.Payload);
            double fraction = (payload.Start + payload.Length / 2d) / rebuiltMap.Length * 8;
            var pointType = surface.GetType().GetMethod("Hit", flags)!.GetParameters()[0].ParameterType;
            var point = Activator.CreateInstance(pointType, [110 + (fraction % 1) * 878, (Math.Floor(fraction) + 1) * 31 + 10]);
            Assert.Equal(payload, surface.GetType().GetMethod("Hit", flags)!.Invoke(surface, [point]));
            surface.GetType().GetMethod("ClickAt", flags)!.Invoke(surface, [point]);
            Assert.Equal(payload, grid.GetType().GetProperty("SelectedItem")!.GetValue(grid));
            int payloadIndex = rebuiltMap.Regions.ToList().IndexOf(payload);
            Assert.True((bool)surface.GetType().GetMethod("MoveSelection", flags)!.Invoke(surface, [1])!);
            Assert.Equal(rebuiltMap.Regions[payloadIndex + 1], grid.GetType().GetProperty("SelectedItem")!.GetValue(grid));
            Assert.True((bool)surface.GetType().GetMethod("MoveSelection", flags)!.Invoke(surface, [-1])!);
            Assert.Equal(payload, grid.GetType().GetProperty("SelectedItem")!.GetValue(grid));
            Assert.NotNull(grid.GetType().GetProperty("CellStyle")!.GetValue(grid));
            Assert.Null(type.GetField("quickDiskMapZoom", flags));
            surface.GetType().GetMethod("ClickAt", flags)!.Invoke(surface, [point]);
            Assert.Null(grid.GetType().GetProperty("SelectedItem")!.GetValue(grid));
            Assert.Null(surface.GetType().GetField("selection", flags)!.GetValue(surface));
            Assert.Empty((HashSet<int>)surface.GetType().GetField("selectedFiles", flags)!.GetValue(surface)!);
            // A file-only selection must also clear when clicking outside the track,
            // even though the block list already has no selected item.
            fileGrid.GetType().GetProperty("SelectedIndex")!.SetValue(fileGrid, 0);
            Assert.True(Enabled("quickDiskClearSelectionButton"));
            Assert.False(Enabled("quickDiskHexButton"));
            var outside = Activator.CreateInstance(pointType, [0d, 0d]);
            surface.GetType().GetMethod("ClickAt", flags)!.Invoke(surface, [outside]);
            Assert.Equal(-1, fileGrid.GetType().GetProperty("SelectedIndex")!.GetValue(fileGrid));
            Assert.False(Enabled("quickDiskClearSelectionButton"));
            Assert.Empty((HashSet<int>)surface.GetType().GetField("selectedFiles", flags)!.GetValue(surface)!);

            string extension = format == "Mzq" ? ".mzq" : format == "Qdf" ? ".qdf" : ".qd";
            string path = Path.Combine(Path.GetTempPath(), $"mztools-map-ui-{Guid.NewGuid():N}{extension}");
            try
            {
                File.WriteAllBytes(path, image);
                Assert.True((bool)type.GetMethod("AddFile", flags)!.Invoke(window,
                    [path, true, false, null, null, null, false, 0, 0])!);
                Assert.Equal(image, document.QuickDiskSourceImage);
                Assert.False(((QuickDiskLayout)Field("quickDiskLayout")).IsPreview);
                Assert.False(document.IsModified);
                Assert.Equal(image, File.ReadAllBytes(path));
                document.Records.Reverse(); document.IsModified = true;
                Invoke("RefreshGrid");
                Assert.True(((QuickDiskLayout)Field("quickDiskLayout")).IsPreview);
                Invoke("SaveNativeTapeDocument", path, document.Format);
                Assert.False(document.IsModified);
                Assert.Equal(File.ReadAllBytes(path), document.QuickDiskSourceImage);
                Assert.False(((QuickDiskLayout)Field("quickDiskLayout")).IsPreview);
                Assert.Equal("SECOND", ((QuickDiskLayout)Field("quickDiskLayout")).Regions.First(r => r.Kind == QuickDiskRegionKind.Header).Name);
                var root = type.GetProperty("Content")!.GetValue(window)!;
                root.GetType().GetMethod("Measure")!.Invoke(root, [Activator.CreateInstance(sizeType, [1100d, 600d])]);
                root.GetType().GetMethod("Arrange")!.Invoke(root, [Activator.CreateInstance(rectType, [0d, 0d, 1100d, 600d])]);
                root.GetType().GetMethod("UpdateLayout")!.Invoke(root, null);
                // DataGrid schedules column-size calculations on its dispatcher.
                var dispatcher = root.GetType().GetProperty("Dispatcher")!.GetValue(root)!;
                var priorityType = dispatcher.GetType().Assembly.GetType("System.Windows.Threading.DispatcherPriority")!;
                dispatcher.GetType().GetMethod("Invoke", [typeof(Action), priorityType])!.Invoke(dispatcher,
                    [(Action)(() => { }), Enum.Parse(priorityType, "ApplicationIdle")]);
                Assert.True((double)grid.GetType().GetProperty("ActualHeight")!.GetValue(grid)! >= 80, "Block list must remain usable at normal window size.");
                grid.GetType().GetProperty("SelectedItem")!.SetValue(grid, ((QuickDiskLayout)Field("quickDiskLayout")).Regions.First(r => r.Kind == QuickDiskRegionKind.Payload));
                root.GetType().GetMethod("UpdateLayout")!.Invoke(root, null);
                double mapHeight = (double)Field("quickDiskMap").GetType().GetProperty("ActualHeight")!.GetValue(Field("quickDiskMap"))!;
                RaiseBlankClick(Field("quickDiskMapSummary"));
                Assert.Null(grid.GetType().GetProperty("SelectedItem")!.GetValue(grid));
                Assert.False((bool)Field("quickDiskHexButton").GetType().GetProperty("IsEnabled")!.GetValue(Field("quickDiskHexButton"))!);
                root.GetType().GetMethod("UpdateLayout")!.Invoke(root, null);
                Assert.Equal(mapHeight, (double)Field("quickDiskMap").GetType().GetProperty("ActualHeight")!.GetValue(Field("quickDiskMap"))!);
                if (format == "QdHxc" && Environment.GetEnvironmentVariable("MZTOOLS_QD_MAP_SCREENSHOT") is { Length: > 0 } screenshotPath)
                {
                    var fullBitmap = Activator.CreateInstance(bitmapType, [1100, 600, 96d, 96d, pixelFormat])!;
                    bitmapType.GetMethod("Render")!.Invoke(fullBitmap, [root]);
                    var encoderType = core.GetType("System.Windows.Media.Imaging.PngBitmapEncoder")!;
                    object encoder = Activator.CreateInstance(encoderType)!;
                    var frameType = core.GetType("System.Windows.Media.Imaging.BitmapFrame")!;
                    var frame = frameType.GetMethods().Single(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "BitmapSource").Invoke(null, [fullBitmap]);
                    object frames = encoderType.GetProperty("Frames")!.GetValue(encoder)!;
                    frames.GetType().GetMethod("Add")!.Invoke(frames, [frame]);
                    using var stream = File.Create(screenshotPath);
                    encoderType.GetMethod("Save")!.Invoke(encoder, [stream]);
                }
            }
            finally { File.Delete(path); }
        }
        // A valid unknown physical QD opens into the inspector and no native writer
        // may overwrite its source, even if invoked independently of disabled buttons.
        string unknownPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qd");
        var unknownTrack = new byte[4096]; new Random(501).NextBytes(unknownTrack);
        byte[] unknownImage = QuickDiskHostDetectionTests.Wrap(unknownTrack, QdImageFormat.HxcPhysical);
        try
        {
            File.WriteAllBytes(unknownPath, unknownImage);
            document.Clear();
            Assert.True((bool)type.GetMethod("AddFile", flags)!.Invoke(window, [unknownPath, true, false, null, null, null, false, 0, 0])!);
            Assert.True(document.IsReadOnlyQuickDisk); Assert.Empty(document.Records);
            Assert.False(Enabled("addButton")); Assert.False(Enabled("saveButton")); Assert.False(Enabled("saveAsButton"));
            Assert.False(Enabled("formatQuickDiskButton")); Assert.False(Enabled("deleteButton"));
            Assert.Equal("Collapsed", Field("MzfDataGrid").GetType().GetProperty("Visibility")!.GetValue(Field("MzfDataGrid"))!.ToString());
            Assert.IsType<QuickDiskInspectorControl>(Field("quickDiskInspectorHost").GetType().GetProperty("Content")!.GetValue(Field("quickDiskInspectorHost")));
            Assert.Throws<TargetInvocationException>(() => Invoke("SaveNativeTapeDocument", unknownPath, document.Format));
            Invoke("button_Click_FormatQuickDisk", null!, null!);
            Invoke("button_Click_Delete", null!, null!);
            Assert.Equal(unknownImage, File.ReadAllBytes(unknownPath)); Assert.False(document.IsModified);
            Assert.False(((QuickDiskLayout)Field("quickDiskLayout")).IsPreview);
        }
        finally { File.Delete(unknownPath); }
        foreach (string reference in new[] { "DSKA0001_Roland.QD", "DSKA0002_MO5_CQ90-028_formatted.QD", "DSKA0003_Akai_formatted.QD" })
        {
            string referencePath = Path.Combine(AppContext.BaseDirectory, "QDReference", reference);
            if (!File.Exists(referencePath)) continue; // Optional full media, covered by explicit skip in the reference theory.
            byte[] originalReference = File.ReadAllBytes(referencePath);
            document.Clear();
            Assert.True((bool)type.GetMethod("AddFile", flags)!.Invoke(window, [referencePath, true, false, null, null, null, false, 0, 0])!);
            Assert.True(document.IsReadOnlyQuickDisk); Assert.Empty(document.Records);
            var inspector = Field("quickDiskInspectorHost").GetType().GetProperty("Content")!.GetValue(Field("quickDiskInspectorHost"))!;
            Assert.IsType<QuickDiskInspectorControl>(inspector);
            var inspectorRoot = inspector.GetType().GetProperty("Content")!.GetValue(inspector)!;
            var inspectorChildren = (System.Collections.IList)inspectorRoot.GetType().GetProperty("Children")!.GetValue(inspectorRoot)!;
            var buttons = inspectorChildren[0]!;
            var labels = ((System.Collections.IList)buttons.GetType().GetProperty("Children")!.GetValue(buttons)!).Cast<object>()
                .Select(button => button.GetType().GetProperty("Content")!.GetValue(button)!.ToString()!).ToArray();
            Assert.DoesNotContain(labels, label => label.Contains("identical", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Export decoded block/sector...", labels);
            Assert.DoesNotContain("Copy report", labels);
            Assert.DoesNotContain("Save report...", labels);
            Assert.Equal(reference.Contains("MO5"), labels.Any(label => label.StartsWith("Export MO5 logical raw image")));
            var unitGrid = inspector.GetType().GetField("units", flags)!.GetValue(inspector)!;
            int count = ((System.Collections.IList)unitGrid.GetType().GetProperty("Items")!.GetValue(unitGrid)!).Count;
            Assert.Equal(reference.Contains("MO5") ? 403 : reference.Contains("Roland") ? 6 : 4, count);
            unitGrid.GetType().GetProperty("SelectedIndex")!.SetValue(unitGrid, 3);
            var decodedUnit = Assert.IsType<QuickDiskInspectorControl.Unit>(unitGrid.GetType().GetProperty("SelectedItem")!.GetValue(unitGrid));
            Assert.True(decodedUnit.IsDecoded); Assert.NotEmpty(decodedUnit.Structure);
            Assert.Equal(decodedUnit.Data, QuickDiskInspectorControl.GetDecodedExport(decodedUnit));
            var detailPanel = unitGrid.GetType().GetProperty("Parent")!.GetValue(unitGrid)!;
            var detailChildren = (System.Collections.IList)detailPanel.GetType().GetProperty("Children")!.GetValue(detailPanel)!;
            string detailText = (string)detailChildren[2]!.GetType().GetProperty("Text")!.GetValue(detailChildren[2])!;
            Assert.Contains(decodedUnit.Structure, detailText);
            Assert.Throws<TargetInvocationException>(() => Invoke("SaveNativeTapeDocument", referencePath, document.Format));
            Assert.Equal(originalReference, File.ReadAllBytes(referencePath)); Assert.False(document.IsModified);
        }
        document.Clear();
        document.Format = TapeDocumentFormat.QdSharpLegacy;
        document.Records.Add(Record("TOO BIG", 65000));
        Invoke("RefreshGrid");
        Assert.Null(type.GetField("quickDiskLayout", flags)!.GetValue(window));
        document.Clear(); Invoke("RefreshGrid");
        Assert.False(Enabled("quickDiskClearSelectionButton"));
        Assert.Equal("Collapsed", tab.GetType().GetProperty("Visibility")!.GetValue(tab)!.ToString());
        Assert.Equal(0, Field("tapeViews").GetType().GetProperty("SelectedIndex")!.GetValue(Field("tapeViews")));
        AssertContrastingSelectionAndBlockBorders();
        type.GetMethod("Close")!.Invoke(window, null);
    }

    private static void AssertContrastingSelectionAndBlockBorders()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var surfaceType = typeof(QuickDiskMapControl);
        object surface = Activator.CreateInstance(surfaceType)!;
        var first = new QuickDiskRegion(QuickDiskRegionKind.Header, 0, 50, "FIRST", 0);
        var second = new QuickDiskRegion(QuickDiskRegionKind.Header, 50, 50, "SECOND", 1);
        var layout = new QuickDiskLayout(TapeDocumentFormat.Mzq, false, false, 800, 0, null, null,
            [first, second, new(QuickDiskRegionKind.Gap, 100, 700, "Gap")]);
        surfaceType.GetMethod("SetLayout", flags)!.Invoke(surface, [layout]);
        var sizeType = surfaceType.GetMethod("Measure")!.GetParameters()[0].ParameterType;
        var rectType = surfaceType.GetMethod("Arrange")!.GetParameters()[0].ParameterType;
        surfaceType.GetMethod("Measure")!.Invoke(surface, [Activator.CreateInstance(sizeType, [1000d, 300d])]);
        surfaceType.GetMethod("Arrange")!.Invoke(surface, [Activator.CreateInstance(rectType, [0d, 0d, 1000d, 300d])]);
        var core = Assembly.Load("PresentationCore");
        var pixelFormat = core.GetType("System.Windows.Media.PixelFormats")!.GetProperty("Pbgra32")!.GetValue(null)!;
        var bitmapType = core.GetType("System.Windows.Media.Imaging.RenderTargetBitmap")!;
        byte[] Pixels()
        {
            surfaceType.GetMethod("UpdateLayout")!.Invoke(surface, null);
            object bitmap = Activator.CreateInstance(bitmapType, [1000, 300, 96d, 96d, pixelFormat])!;
            bitmapType.GetMethod("Render")!.Invoke(bitmap, [surface]);
            byte[] pixels = new byte[1000 * 300 * 4];
            bitmapType.GetMethod("CopyPixels", [typeof(Array), typeof(int), typeof(int)])!.Invoke(bitmap, [pixels, 4000, 0]);
            return pixels;
        }
        byte[] Pixel(byte[] pixels, int x, int y) => pixels.AsSpan((y * 1000 + x) * 4, 4).ToArray();
        byte[] unselected = Pixels();
        Assert.Equal(new byte[] { 0, 140, 255, 255 }, Pixel(unselected, 330, 44)); // header orange
        Assert.Equal(Pixel(unselected, 330, 44), Pixel(unselected, 768, 44));
        Assert.True(Pixel(unselected, 548, 44)[2] < 255, "Same-colored unselected blocks must have a visible separator.");
        surfaceType.GetMethod("Select", flags)!.Invoke(surface, [first]);
        byte[] selected = Pixels();
        Assert.Equal(new byte[] { 237, 149, 100, 255 }, Pixel(selected, 330, 44)); // blue
        Assert.Equal(Pixel(unselected, 768, 44), Pixel(selected, 768, 44));
        Assert.True(Pixel(selected, 110, 44)[0] < 40, "Selection has a dark outside border.");
        Assert.True(Pixel(selected, 111, 44)[0] > 200, "Selection has a bright inside border.");
        surfaceType.GetMethod("HighlightFiles", flags)!.Invoke(surface, [new[] { 1 }]);
        Assert.Equal(Pixel(unselected, 768, 44), Pixel(Pixels(), 768, 44));
        surfaceType.GetMethod("Select", flags)!.Invoke(surface, [null]);
        Assert.Equal(new byte[] { 237, 149, 100, 255 }, Pixel(Pixels(), 768, 44));
        surfaceType.GetMethod("HighlightFiles", flags)!.Invoke(surface, [Array.Empty<int>()]);
        Assert.Equal(unselected, Pixels());
        var green = first with { Kind = QuickDiskRegionKind.Payload };
        var greenLayout = layout with { Regions = [green, second, new(QuickDiskRegionKind.Gap, 100, 700, "Gap")] };
        surfaceType.GetMethod("SetLayout", flags)!.Invoke(surface, [greenLayout]);
        byte[] originalGreen = Pixels();
        Assert.Equal(new byte[] { 113, 179, 60, 255 }, Pixel(originalGreen, 330, 44));
        surfaceType.GetMethod("Select", flags)!.Invoke(surface, [green]);
        surfaceType.GetMethod("HighlightFiles", flags)!.Invoke(surface, [new[] { 0 }]);
        Assert.Equal(new byte[] { 237, 149, 100, 255 }, Pixel(Pixels(), 330, 44));
        surfaceType.GetMethod("Select", flags)!.Invoke(surface, [null]);
        surfaceType.GetMethod("HighlightFiles", flags)!.Invoke(surface, [Array.Empty<int>()]);
        Assert.Equal(originalGreen, Pixels());
        surfaceType.GetMethod("HighlightFiles", flags)!.Invoke(surface, [new[] { 0 }]);
        surfaceType.GetMethod("SetLayout", flags)!.Invoke(surface, [greenLayout]);
        Assert.Equal(originalGreen, Pixels());
        // Single-file MZQ: header and payload share a file, but selecting one block
        // must not turn the other block blue through the synchronized file selection.
        var sameFileLayout = greenLayout with { Regions = [green, second with { FileIndex = 0 }, new(QuickDiskRegionKind.Gap, 100, 700, "Gap")] };
        surfaceType.GetMethod("SetLayout", flags)!.Invoke(surface, [sameFileLayout]);
        byte[] sameFileUnselected = Pixels();
        surfaceType.GetMethod("Select", flags)!.Invoke(surface, [green]);
        surfaceType.GetMethod("HighlightFiles", flags)!.Invoke(surface, [new[] { 0 }]);
        byte[] sameFileSelected = Pixels();
        Assert.Equal(new byte[] { 237, 149, 100, 255 }, Pixel(sameFileSelected, 330, 44));
        Assert.Equal(Pixel(sameFileUnselected, 768, 44), Pixel(sameFileSelected, 768, 44));
        surfaceType.GetMethod("Select", flags)!.Invoke(surface, [null]);
        byte[] fileSelected = Pixels();
        Assert.Equal(new byte[] { 237, 149, 100, 255 }, Pixel(fileSelected, 330, 44));
        Assert.Equal(new byte[] { 237, 149, 100, 255 }, Pixel(fileSelected, 768, 44));
    }

    internal static void RaiseBlankClick(object element)
    {
        var core = Assembly.Load("PresentationCore");
        var mouseType = core.GetType("System.Windows.Input.Mouse")!;
        var buttonType = core.GetType("System.Windows.Input.MouseButton")!;
        var eventType = core.GetType("System.Windows.Input.MouseButtonEventArgs")!;
        object args = Activator.CreateInstance(eventType, [mouseType.GetProperty("PrimaryDevice")!.GetValue(null), 0, Enum.Parse(buttonType, "Left")])!;
        var uiType = core.GetType("System.Windows.UIElement")!;
        eventType.GetProperty("RoutedEvent")!.SetValue(args, uiType.GetField("PreviewMouseDownEvent")!.GetValue(null));
        element.GetType().GetMethod("RaiseEvent")!.Invoke(element, [args]);
    }
}
