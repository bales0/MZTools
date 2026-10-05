namespace MZTools.Tests;

public sealed class DskHexEditTests
{
    [Theory]
    [InlineData("AA BB", 0, "1f", "1F BB", 3)]
    [InlineData("AA BB", 2, "C", "AA CB", 4)]
    [InlineData("AA BB", 4, "0", "AA B0", 5)]
    [InlineData("AA\r\nBB", 1, "23", "A2\r\n3B", 5)]
    public void FixedHexTypingOverwritesNibblesWithoutMovingBytes(string before, int caret, string input, string expected, int next)
    {
        Assert.True(FixedHexInput.TryOverwrite(before, caret, input, out string after, out int actualNext));
        Assert.Equal(expected, after);
        Assert.Equal(before.Length, after.Length); Assert.Equal(next, actualNext);
    }

    [Theory]
    [InlineData("G")] [InlineData("xyz")] [InlineData(" ")] [InlineData("\n")] [InlineData("12345")]
    public void FixedHexRejectsInvalidOrOverflowTypingAtomically(string input)
    {
        Assert.False(FixedHexInput.TryOverwrite("AA BB", 0, input, out string after, out _));
        Assert.Equal("AA BB", after);
    }

    [Theory]
    [InlineData("10 20", true)] [InlineData("1020", true)] [InlineData("10\r\n20", true)]
    [InlineData("1", false)] [InlineData("10 GG", false)] [InlineData("10 20 30", false)]
    public void HexPasteRequiresWholeBytesAndPreservesLength(string paste, bool valid)
    {
        Assert.Equal(valid, FixedHexInput.TryPaste("AA BB CC", 4, paste, out string after, out _));
        Assert.Equal(valid ? "AA 10 20" : "AA BB CC", after);
    }

    [Fact]
    public void OneByteSectorEditPreservesAllOtherImageBytesAndUnsavedFiles()
    {
        var document = DskDocumentFactory.CreateCpm(false);
        document.FileSystem.Insert("KEEP.BIN", [1, 2, 3]); document.MarkModified();
        byte[] original = document.Serialize();
        var session = DskHexEditService.OpenSector(document, new(10, 2));
        byte[] edited = session.OriginalBuffer; edited[19] ^= 0x7F;
        var preview = DskHexEditService.Preview(session, edited);
        Assert.Equal(original, document.Serialize());
        Assert.Single(preview.Changes);
        Assert.False(preview.RequiresFilesystemConfirmation);
        DskHexEditService.Apply(document, preview);
        byte[] result = document.Serialize();
        int fileOffset = session.Segments[0].FileOffset + 19;
        Assert.Equal(Enumerable.Range(0, result.Length).Where(i => result[i] != original[i]), [fileOffset]);
        Assert.Equal(new byte[] { 1, 2, 3 }, document.FileSystem.Extract(document.FileSystem.ReadDirectory().Single())[..3]);
    }

    [Fact]
    public void InvalidSizeAndStalePreviewNeverMutateDocument()
    {
        var document = DskDocumentFactory.CreateCpm(false);
        var session = DskHexEditService.OpenSector(document, new(10, 0));
        byte[] before = document.Serialize();
        Assert.Throws<InvalidDataException>(() => DskHexEditService.Preview(session, [1]));
        Assert.Equal(before, document.Serialize());
        byte[] edited = session.OriginalBuffer; edited[0] ^= 1;
        var preview = DskHexEditService.Preview(session, edited);
        document.FileSystem.Insert("NEW.BIN", [9]); document.MarkModified();
        byte[] unsaved = document.Serialize();
        Assert.Throws<InvalidDataException>(() => DskHexEditService.Apply(document, preview));
        Assert.Equal(unsaved, document.Serialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpmBlockEditWritesOnlyMappedBytesIncludingInversion(bool inverted)
    {
        var document = inverted ? DskDocumentFactory.CreatePersonalCpm80(true) : DskDocumentFactory.CreateCpm(false);
        var session = DskHexEditService.OpenCpmBlock(document, 10);
        byte[] original = document.Serialize();
        byte[] edited = session.OriginalBuffer;
        edited[0] = 0x34; edited[513] = 0x56; edited[^1] = 0x78;
        var preview = DskHexEditService.Preview(session, edited);
        DskHexEditService.Apply(document, preview);
        Assert.Equal(edited, DskHexEditService.OpenCpmBlock(document, 10).OriginalBuffer);
        var allowed = session.Segments.SelectMany(s => Enumerable.Range(s.FileOffset, s.Length)).ToHashSet();
        byte[] result = document.Serialize();
        Assert.All(Enumerable.Range(0, result.Length).Where(i => original[i] != result[i]), i => Assert.Contains(i, allowed));
        Assert.Equal(3, preview.Changes.Count);
    }

    [Fact]
    public void MetadataDamageRequiresConfirmationAndRetainsRawBytes()
    {
        var document = DskDocumentFactory.CreateCpm(false);
        var session = DskHexEditService.OpenCpmBlock(document, 0);
        Assert.True(session.IsSensitive);
        byte[] original = document.Serialize();
        byte[] edited = session.OriginalBuffer;
        edited[0] = 0; edited[12] = 0xFF; // Invalid extent prevents CP/M re-detection.
        var preview = DskHexEditService.Preview(session, edited);
        Assert.True(preview.RequiresFilesystemConfirmation);
        Assert.Throws<InvalidDataException>(() => DskHexEditService.Apply(document, preview));
        Assert.Equal(original, document.Serialize());
        DskHexEditService.Apply(document, preview, acceptFilesystemImpact: true);
        Assert.Equal(preview.ResultBytes, document.Serialize());
        Assert.Contains(document.FileSystem.Type, new[] { DskFileSystemType.Raw, DskFileSystemType.BootOnly });
    }

    [Fact]
    public void BootWarningAndCancelPreviewLeaveOriginalUntouched()
    {
        var document = DskDocumentFactory.CreateCpm(false);
        var session = DskHexEditService.OpenSector(document, new(1, 0));
        byte[] original = document.Serialize();
        Assert.True(session.IsSensitive);
        byte[] edited = session.OriginalBuffer; edited[0] ^= 1; edited[^1] ^= 2;
        var preview = DskHexEditService.Preview(session, edited);
        Assert.Contains("WARNING", preview.Report);
        Assert.Equal(2, preview.Changes.Count);
        Assert.Equal(original, document.Serialize()); Assert.False(document.IsModified);
        // Returned buffers are defensive copies.
        preview.ResultBytes[0] = 0;
        session.OriginalBuffer[0] = 0;
        Assert.Equal(original, session.OriginalImage);
        Assert.NotEqual(0, preview.ResultBytes[0]);
    }

    [Fact]
    public void HexParserRejectsMalformedBytesAndLengthChanges()
    {
        Assert.Equal(new byte[] { 0, 0xA5, 0xFF }, DskHexEditService.ParseHex("00 a5\r\nFF", 3));
        Assert.Throws<FormatException>(() => DskHexEditService.ParseHex("1F GG", 2));
        Assert.Throws<FormatException>(() => DskHexEditService.ParseHex("F FF", 2));
        Assert.Throws<FormatException>(() => DskHexEditService.ParseHex("FF", 2));
    }

    [Fact]
    public void RawSectorEditUsesDescriptorIndexEvenWithDuplicateIds()
    {
        var image = DskImage.CreateUniform(2, 1, 2, 128, 1, 0x4E, 0, "raw");
        image.Tracks[0]!.Sectors[1].SectorId = 1;
        var document = DskDocument.Open(image.Serialize());
        var session = DskHexEditService.OpenSector(document, new(0, 1));
        byte[] edited = session.OriginalBuffer; edited[0] = 0xAA;
        // Even a precisely addressed edit cannot bypass invalid container geometry.
        Assert.Throws<InvalidDataException>(() => DskHexEditService.Preview(session, edited));
        Assert.Equal(0, document.Image.Tracks[0]!.Sectors[0].Data[0]);
        Assert.Equal(0, document.Image.Tracks[0]!.Sectors[1].Data[0]);
    }

    [Fact]
    public void RawSectorEditAndNoOpAreValidTransactions()
    {
        var document = DskDocument.Open(DskImage.CreateUniform(2, 1, 2, 128, 1, 0x4E, 0, "raw").Serialize());
        var session = DskHexEditService.OpenSector(document, new(0, 1));
        DskHexEditService.Apply(document, DskHexEditService.Preview(session, session.OriginalBuffer));
        Assert.False(document.IsModified);
        byte[] edited = session.OriginalBuffer; edited[0] = 0xAA;
        DskHexEditService.Apply(document, DskHexEditService.Preview(session, edited));
        Assert.Equal(0, document.Image.Tracks[0]!.Sectors[0].Data[0]);
        Assert.Equal(0xAA, document.Image.Tracks[0]!.Sectors[1].Data[0]);
    }

    internal static void ExerciseWindowOnCurrentStaThread()
    {
        Assert.Equal("A..", DskHexEditorControl.FormatCharacters([0x41, 0x92, 0], false));
        Assert.Equal("Ae.", DskHexEditorControl.FormatCharacters([0x41, 0x92, 0], true));
        var document = DskDocumentFactory.CreateCpm(false);
        byte[] original = document.Serialize();
        var session = DskHexEditService.OpenSector(document, new(10, 0));
        var window = new DskHexEditorWindow(null, document, session);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var control = typeof(DskHexEditorWindow).GetField("editor", flags)!.GetValue(window)!;
        var type = control.GetType();
        object Field(string name) => type.GetField(name, flags)!.GetValue(control)!;
        var sizeType = type.GetMethod("Measure")!.GetParameters()[0].ParameterType;
        var rectType = type.GetMethod("Arrange")!.GetParameters()[0].ParameterType;
        void Layout()
        {
            for (int i = 0; i < 4; i++)
            {
                type.GetMethod("Measure")!.Invoke(control, [Activator.CreateInstance(sizeType, [900d, 420d])]);
                type.GetMethod("Arrange")!.Invoke(control, [Activator.CreateInstance(rectType, [0d, 0d, 900d, 420d])]);
                type.GetMethod("UpdateLayout")!.Invoke(control, null);
            }
        }
        object left = Field("originalScroll"), right = Field("editedScroll");
        double Offset(object viewer, string name) => (double)viewer.GetType().GetProperty(name)!.GetValue(viewer)!;
        Layout();
        Assert.True(Offset(left, "ScrollableHeight") > 100);
        Assert.True(Offset(left, "ScrollableWidth") > 20);
        left.GetType().GetMethod("ScrollToVerticalOffset")!.Invoke(left, [100d]);
        left.GetType().GetMethod("ScrollToHorizontalOffset")!.Invoke(left, [20d]); Layout();
        Assert.Equal(100d, Offset(left, "VerticalOffset")); Assert.Equal(100d, Offset(right, "VerticalOffset"));
        Assert.Equal(20d, Offset(right, "HorizontalOffset"));
        right.GetType().GetMethod("ScrollToVerticalOffset")!.Invoke(right, [40d]); Layout();
        Assert.Equal(40d, Offset(left, "VerticalOffset"));
        byte[] characters = session.OriginalBuffer; characters[0] = 0x92;
        window.SetEditedHex(DskHexEditService.FormatHex(characters));
        Assert.StartsWith(".", (string)Field("editedAscii").GetType().GetProperty("Text")!.GetValue(Field("editedAscii"))!);
        Assert.StartsWith("e", (string)Field("editedMz").GetType().GetProperty("Text")!.GetValue(Field("editedMz"))!);
        window.SetEditedHex("invalid");
        Assert.Contains("Invalid", (string)Field("editedMz").GetType().GetProperty("Text")!.GetValue(Field("editedMz"))!);
        window.Revert();
        Assert.Throws<InvalidOperationException>(() => window.PreviewChanges());
        window.UnlockEditing();
        var fixedControl = (DskHexEditorControl)control;
        var hexText = Field("edited");
        Assert.True((bool)hexText.GetType().GetProperty("IsReadOnly")!.GetValue(hexText)!);
        // Exercise the routed input path as well as the pure overwrite helpers.
        var core = System.Reflection.Assembly.Load("PresentationCore");
        var compositionType = core.GetType("System.Windows.Input.TextComposition")!;
        var composition = Activator.CreateInstance(compositionType, [null, hexText, "F"])!;
        var keyboard = core.GetType("System.Windows.Input.Keyboard")!.GetProperty("PrimaryDevice")!.GetValue(null)!;
        var inputArgsType = core.GetType("System.Windows.Input.TextCompositionEventArgs")!;
        var inputArgs = Activator.CreateInstance(inputArgsType, [keyboard, composition])!;
        var previewInput = core.GetType("System.Windows.Input.TextCompositionManager")!.GetField("PreviewTextInputEvent")!.GetValue(null)!;
        inputArgsType.GetProperty("RoutedEvent")!.SetValue(inputArgs, previewInput);
        hexText.GetType().GetProperty("SelectionStart")!.SetValue(hexText, 0);
        hexText.GetType().GetMethod("RaiseEvent")!.Invoke(hexText, [inputArgs]);
        Assert.True((bool)inputArgsType.GetProperty("Handled")!.GetValue(inputArgs)!);
        Assert.StartsWith("F", (string)hexText.GetType().GetProperty("Text")!.GetValue(hexText)!);
        hexText.GetType().GetProperty("SelectionStart")!.SetValue(hexText, 0);
        Assert.True(fixedControl.OverwriteDigits("A"));
        string fixedText = (string)hexText.GetType().GetProperty("Text")!.GetValue(hexText)!;
        Assert.False(fixedControl.OverwriteDigits("x"));
        Assert.Equal(fixedText, hexText.GetType().GetProperty("Text")!.GetValue(hexText));
        Assert.Equal(session.OriginalBuffer.Length, DskHexEditService.ParseHex(fixedText, session.OriginalBuffer.Length).Length);
        hexText.GetType().GetProperty("SelectionStart")!.SetValue(hexText, 0);
        Assert.True(fixedControl.PasteHex("92 41"));
        Assert.StartsWith("eA", (string)Field("editedMz").GetType().GetProperty("Text")!.GetValue(Field("editedMz"))!);
        Assert.False(fixedControl.PasteHex("GG"));
        window.Revert();
        byte[] edited = session.OriginalBuffer; edited[1] ^= 1;
        window.SetEditedHex(DskHexEditService.FormatHex(edited)); window.PreviewChanges();
        window.SetEditedHex(DskHexEditService.FormatHex(session.OriginalBuffer));
        Assert.Throws<InvalidOperationException>(() => window.ApplyPreview());
        window.Revert(); window.Close();
        Assert.Equal(original, document.Serialize()); Assert.False(document.IsModified);
        var applied = new DskHexEditorWindow(null, document, session);
        applied.UnlockEditing(); applied.SetEditedHex(DskHexEditService.FormatHex(edited));
        applied.PreviewChanges(); applied.ApplyPreview();
        Assert.True(applied.Applied); Assert.True(document.IsModified); applied.Close();
    }
}
