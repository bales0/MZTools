using System.Buffers.Binary;

namespace MZTools.Tests;

public class QuickDiskImageTests
{
    [Theory]
    [InlineData("Old_MZ700.qd", QdImageFormat.SharpLegacyLogical)]
    [InlineData("HxC_DSKA0000_Blank.QD", QdImageFormat.HxcPhysical)]
    [InlineData("hxc_Mario.qd", QdImageFormat.HxcPhysical)]
    [InlineData("Flashfloppy_Blank.qd", QdImageFormat.FlashFloppyPhysical)]
    public void Detector_RecognizesGoldenImages(string name, QdImageFormat expected)
    {
        Assert.Equal(expected, QdFormatDetector.Detect(ReadQd(name)));
    }

    [Fact]
    public void Detector_RejectsRandomData()
    {
        Assert.Equal(QdImageFormat.Unknown, QdFormatDetector.Detect(new byte[1024]));
    }

    [Fact]
    public void SaveAs_UsesFilterIndexToDistinguishThreeQdWriters()
    {
        Assert.Equal(TapeDocumentFormat.QdHxc, MainWindow.ResolveSaveFormat(".qd", 3));
        Assert.Equal(TapeDocumentFormat.QdFlashFloppy, MainWindow.ResolveSaveFormat(".qd", 4));
        Assert.Equal(TapeDocumentFormat.QdSharpLegacy, MainWindow.ResolveSaveFormat(".qd", 5));
        Assert.Equal(3, MainWindow.GetSaveFilterIndex(TapeDocumentFormat.QdHxc));
        Assert.Equal(4, MainWindow.GetSaveFilterIndex(TapeDocumentFormat.QdFlashFloppy));
        Assert.Equal(5, MainWindow.GetSaveFilterIndex(TapeDocumentFormat.QdSharpLegacy));
    }

    [Fact]
    public void EmptyMzqImage_WritesAndReads()
    {
        string path = Path.Combine(TapeTestData.CreateTempDirectory(), "empty.mzq");
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                new MZQFileReader().WriteMZQHeaderToFile(stream, 0);
            }

            Assert.Equal(8, new FileInfo(path).Length);
            Assert.Empty(new MZQFileReader().ReadFile(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void ContainerFormatsSupportEmptyDocuments()
    {
        Assert.True(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.Mzt));
        Assert.True(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.Mzq));
        Assert.True(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.Qdf));
        Assert.True(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.QdSharpLegacy));
        Assert.True(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.QdHxc));
        Assert.True(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.QdFlashFloppy));
        Assert.False(MainWindow.SupportsEmptyDocument(TapeDocumentFormat.Mzf));
        Assert.Empty(TapeDocumentWriter.SerializeMzt(Array.Empty<TapeRecord>()));
    }

    [Fact]
    public void IplExportFilterOffersOnlyMeaningfulProgramAndTapeFormats()
    {
        string filter = FeatureModePolicy.GetIplExportFilter();

        Assert.Contains("*.bin", filter);
        Assert.Contains("*.mzf", filter);
        Assert.Contains("*.lep", filter);
        Assert.Contains("*.l16", filter);
        Assert.Contains("*.wav", filter);
        Assert.DoesNotContain("*.dsk", filter);
        Assert.DoesNotContain("*.qd", filter);
    }

    [Fact]
    public void AdvancedStatus_ShowsQdTypeCountAndNonStandardWarning()
    {
        string status = MainWindow.GetQdAdvancedStatus(TapeDocumentFormat.QdHxc, 38);

        Assert.Contains("HxC physical", status);
        Assert.Contains("Files: 38 / 34", status);
        Assert.Contains("format supports up to 50", status);
        Assert.Equal("Quickdisk image type: —", MainWindow.GetQdAdvancedStatus(TapeDocumentFormat.Mzf, 1));
    }

    [Theory]
    [InlineData("Old_MZ700.qd")]
    [InlineData("HxC_DSKA0000_Blank.QD")]
    [InlineData("Flashfloppy_Blank.qd")]
    public void GoldenBlankImages_OpenWithNoRecords(string name)
    {
        Assert.Empty(QdImageReaderWriter.Read(ReadQd(name)).Records);
    }

    [Theory]
    [InlineData(QdImageFormat.SharpLegacyLogical, 0xF00F)]
    [InlineData(QdImageFormat.HxcPhysical, 0x32000)]
    [InlineData(QdImageFormat.FlashFloppyPhysical, 0x32000)]
    public void NewEmptyImage_WritesAndReadsWithFormatAndSizePreserved(QdImageFormat format, int expectedSize)
    {
        byte[] image = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), format);
        QdReadResult result = QdImageReaderWriter.Read(image);

        Assert.Empty(result.Records);
        Assert.Equal(format, result.Format);
        Assert.Equal(expectedSize, image.Length);
    }

    [Theory]
    [InlineData("hxc_Mario.qd", 203388)]
    [InlineData("HxC_DSKA0000_Blank.QD", 203389)]
    public void FormatHxc_PreservesLoadedProfile(string fixture, int expectedBitRate)
    {
        byte[] original = ReadQd(fixture);
        QdReadResult loaded = QdImageReaderWriter.Read(original);
        QuickDiskPhysicalProfile profile = Assert.IsType<QuickDiskPhysicalProfile>(loaded.PhysicalProfile);

        byte[] formatted = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), loaded.Format, profile);
        QdReadResult reread = QdImageReaderWriter.Read(formatted);

        Assert.Empty(reread.Records);
        Assert.Equal(loaded.Format, reread.Format);
        Assert.Equal(original.Length, formatted.Length);
        Assert.Equal(profile, reread.PhysicalProfile);
        Assert.Equal((uint)expectedBitRate, reread.PhysicalProfile!.BitRate);
        Assert.Equal((byte)0x01, reread.PhysicalProfile!.BlankFiller);
    }

    [Fact]
    public void FormatFlashFloppy_PreservesLoadedProfile()
    {
        byte[] original = ReadQd("Flashfloppy_Blank.qd");
        QdReadResult loaded = QdImageReaderWriter.Read(original);
        QuickDiskPhysicalProfile profile = Assert.IsType<QuickDiskPhysicalProfile>(loaded.PhysicalProfile);

        byte[] formatted = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), loaded.Format, profile);
        QdReadResult reread = QdImageReaderWriter.Read(formatted);

        Assert.Empty(reread.Records);
        Assert.Equal(QdImageFormat.FlashFloppyPhysical, reread.Format);
        Assert.Equal(original.Length, formatted.Length);
        Assert.Equal(profile, reread.PhysicalProfile);
        Assert.Equal((byte)0x11, reread.PhysicalProfile!.BlankFiller);
    }

    [Fact]
    public void FormatLegacyImage_PreservesTypeAndLength()
    {
        TapeRecord mario = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        QdReadResult loaded = QdImageReaderWriter.Read(QdImageReaderWriter.Write([mario], QdImageFormat.SharpLegacyLogical));

        byte[] formatted = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), loaded.Format);
        QdReadResult reread = QdImageReaderWriter.Read(formatted);

        Assert.Empty(reread.Records);
        Assert.Equal(QdImageFormat.SharpLegacyLogical, reread.Format);
        Assert.Equal(0xF00F, formatted.Length);
    }

    [Fact]
    public void StandardAndTechnicalFileLimits_AreEnforced()
    {
        Assert.True(QuickDiskLimits.TryValidateForSave(0, false, out _));
        Assert.True(QuickDiskLimits.TryValidateForSave(34, false, out _));
        Assert.False(QuickDiskLimits.TryValidateForSave(35, false, out string standardError));
        Assert.Contains("34", standardError);
        Assert.True(QuickDiskLimits.TryValidateForSave(50, true, out _));
        Assert.False(QuickDiskLimits.TryValidateForSave(51, true, out string formatError));
        Assert.Contains("50", formatError);
    }

    [Fact]
    public void ImportedFiftyFileImage_IsAcceptedButStandardWriterRejectsThirtyFive()
    {
        TapeRecord tiny = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0x42]));
        TapeRecord[] fifty = Enumerable.Repeat(tiny, 50).ToArray();
        byte[] importedImage = QdImageReaderWriter.Write(
            fifty,
            QdImageFormat.SharpLegacyLogical,
            allowImportedNonStandard: true);

        Assert.Equal(50, QdImageReaderWriter.Read(importedImage).Records.Count);
        byte[] outOfStandardImport = (byte[])importedImage.Clone();
        outOfStandardImport[4] = 102;
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(outOfStandardImport));
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write(
            fifty[..35],
            QdImageFormat.SharpLegacyLogical));
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write(
            Enumerable.Repeat(tiny, 51).ToArray(),
            QdImageFormat.SharpLegacyLogical,
            allowImportedNonStandard: true));
    }

    [Fact]
    public void LegacyBlankWriter_ReproducesGoldenImage()
    {
        byte[] written = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), QdImageFormat.SharpLegacyLogical);

        Assert.Equal(0xF00F, written.Length);
        Assert.Equal(ReadQd("Old_MZ700.qd"), written);
    }

    [Fact]
    public void MarioGolden_DecodesAllFramesAndRecord()
    {
        byte[] image = ReadQd("hxc_Mario.qd");
        QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(image, QdImageFormat.HxcPhysical);
        IReadOnlyList<SharpQdFrame> frames = QuickDiskMfmCodec.FindSharpFrames(container.Track);
        QdReadResult result = QdImageReaderWriter.Read(image);

        Assert.Contains(frames, frame => frame.Type == 0x02 && frame.Bytes[^2..].SequenceEqual(new byte[] { 0xFA, 0x91 }));
        Assert.Contains(frames, frame => frame.Type == 0x00 && frame.Bytes[^2..].SequenceEqual(new byte[] { 0x0F, 0x72 }));
        Assert.Contains(frames, frame => frame.Type == 0x05 && frame.Bytes[^2..].SequenceEqual(new byte[] { 0xD3, 0x5C }));
        Assert.True(frames.Select(frame => (int)(frame.Position & 15)).Distinct().Count() >= 3);

        TapeRecord record = Assert.Single(result.Records);
        Assert.Equal((byte)0x01, record.Header.MzfFtype);
        Assert.Equal(
            "MARIO SPECIAL",
            SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname));
        Assert.Equal((ushort)43202, record.Header.MzfSize);
        Assert.Equal((ushort)0x1200, record.Header.MzfStart);
        Assert.Equal((ushort)0x1200, record.Header.MzfExec);
        Assert.Equal(43202, record.Body.MzfBody.Length);
        Assert.Equal(new byte[]
        {
            0xF3, 0x31, 0x00, 0xD0, 0xD3, 0xE6, 0xD3, 0xE0,
            0xD3, 0xE3, 0x3E, 0x01, 0xD3, 0xF0, 0x21, 0x00,
            0xD0, 0x11, 0x01, 0xD0, 0x01, 0xE7, 0x03, 0x36,
            0x00, 0xED, 0xB0, 0x21, 0x00, 0xD8, 0x11, 0x01
        }, record.Body.MzfBody[..32]);
        Assert.Equal(new byte[]
        {
            0x18, 0x18, 0x00, 0x00, 0x30, 0x0C, 0x00, 0x00,
            0x30, 0x0C, 0x00, 0x00, 0x30, 0x0C, 0x00, 0x00,
            0x18, 0x18, 0x00, 0x00, 0x0F, 0xF0, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF
        }, record.Body.MzfBody[^32..]);
        Assert.All(record.Header.MzfHeaderDescription[38..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(QdImageFormat.SharpLegacyLogical)]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void Mario_RoundTripsThroughEveryQdWriter(QdImageFormat format)
    {
        TapeRecord original = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);

        byte[] output = QdImageReaderWriter.Write([original], format);
        QdReadResult reread = QdImageReaderWriter.Read(output);
        TapeRecord actual = Assert.Single(reread.Records);

        AssertRecordEquivalent(original, actual);
        Assert.Equal(format, reread.Format);
        Assert.Equal(format == QdImageFormat.SharpLegacyLogical ? 0xF00F : 0x32000, output.Length);
    }

    [Theory]
    [InlineData(".qdf")]
    [InlineData(".mzq")]
    public void Mario_RoundTripsThroughExistingLogicalFormats(string extension)
    {
        TapeRecord original = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        string path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}{extension}");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                if (extension == ".qdf")
                {
                    var writer = new QDFFileReader();
                    writer.WriteQDFHeaderToFile(stream, 2);
                    writer.WriteQDFFileHeaderToFile(stream, original.Header);
                    writer.WriteQDFFileBodyToFile(stream, original.Body);
                    writer.WriteBytesToStream(stream, 0, QDFFileReader.ImageSize - stream.Length);
                }
                else
                {
                    var writer = new MZQFileReader();
                    writer.WriteMZQHeaderToFile(stream, 2);
                    writer.WriteMZQFileHeaderToFile(stream, original.Header);
                    writer.WriteMZQFileBodyToFile(stream, original.Body);
                }
            }

            (MZQFileHeader, MZQFileBody) block = extension == ".qdf"
                ? Assert.Single(new QDFFileReader().ReadFile(path))
                : Assert.Single(new MZQFileReader().ReadFile(path));
            AssertRecordEquivalent(original, TapeRecord.FromLegacy(block.Item1, block.Item2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(QdImageFormat.SharpLegacyLogical)]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void StandaloneMzf_RoundTripsRepresentableDataAndDropsTrailing(QdImageFormat format)
    {
        string mzfPath = Path.Combine(AppContext.BaseDirectory, "MZF", "Barbar6.mzf");
        TapeRecord original = new MZTFileReader().ReadStandaloneMzf(mzfPath);
        MZQFileBody body = original.Body;
        body.TrailingData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        original.Body = body;

        TapeRecord actual = Assert.Single(QdImageReaderWriter.Read(QdImageReaderWriter.Write([original], format)).Records);

        AssertRecordEquivalent(original, actual);
        Assert.Empty(actual.Body.TrailingData);
        Assert.All(actual.Header.MzfHeaderDescription[38..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(QdImageFormat.HxcPhysical, 0x31C00, 0x3200, 0x25600)]
    [InlineData(QdImageFormat.FlashFloppyPhysical, 0x31A99, 0x31A9, 0x2B745)]
    public void PhysicalWriter_UsesSpecifiedContainerProfile(QdImageFormat format, int length, int start, int end)
    {
        byte[] output = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), format);
        QuickDiskContainer parsed = HxcFlashFloppyQdContainer.Parse(output, format);

        Assert.Equal(0x32000, output.Length);
        Assert.Equal((uint)0x400, parsed.Descriptor.Offset);
        Assert.Equal((uint)length, parsed.Descriptor.Length);
        Assert.Equal((uint)start, parsed.Descriptor.WindowStart);
        Assert.Equal((uint)end, parsed.Descriptor.WindowEnd);
        Assert.Equal(0x31C00, parsed.Profile.StoredTrackLength);
        Assert.Equal(0x200, parsed.Profile.DescriptorOffset);
        Assert.Equal(0x400, parsed.Profile.DataOffset);
        if (format == QdImageFormat.HxcPhysical) Assert.Equal((uint)203388, parsed.BitRate);
    }

    [Fact]
    public void FlashFloppyWriter_UsesRealMzTiming()
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        byte[] qdf = QDFFileReader.BuildImage([record]);
        byte[] encoded = QuickDiskMfmCodec.Encode(qdf.AsSpan(QuickDiskPhysicalWriter.QdfSignatureLength));
        byte[] output = QdImageReaderWriter.Write([record], QdImageFormat.FlashFloppyPhysical);

        Assert.Equal(0x32000, output.Length);
        Assert.Equal("QD"u8.ToArray(), output.AsSpan(3, 2).ToArray());
        Assert.Equal((uint)0x400, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(0x200, 4)));
        Assert.Equal((uint)0x31A99, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(0x204, 4)));
        Assert.Equal((uint)0x31A9, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(0x208, 4)));
        Assert.Equal((uint)0x2B745, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(0x20C, 4)));
        Assert.Equal(0x28000, encoded.Length);
        Assert.All(output.AsSpan(0x400, 0x35A9 - 0x400).ToArray(), value => Assert.Equal(0x11, value));
        Assert.Equal(encoded, output.AsSpan(0x35A9, encoded.Length).ToArray());
        Assert.All(output.AsSpan(0x35A9 + encoded.Length).ToArray(), value => Assert.Equal(0x11, value));

        QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(output, QdImageFormat.FlashFloppyPhysical);
        SharpQdFrame count = QuickDiskMfmCodec.FindSharpFrames(container.Track)
            .Where(frame => frame.Type == 0x02)
            .MinBy(frame => frame.Position)!;
        Assert.Equal(0x576F * 8L + 1, count.Position);
        Assert.Equal(QuickDiskPhysicalWriter.FlashFloppyFnblkOffsetRaw * 8L + 1,
            count.Position - 0x31A9 * 8L);

        TapeRecord reread = Assert.Single(QdImageReaderWriter.Read(output).Records);
        AssertRecordEquivalent(record, reread);
    }

    [Fact]
    public void HxcWriter_MatchesMarioCanonicalLayout()
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        byte[] output = QdImageReaderWriter.Write([record], QdImageFormat.HxcPhysical);
        QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(output, QdImageFormat.HxcPhysical);
        IReadOnlyList<SharpQdFrame> frames = QuickDiskMfmCodec.FindSharpFrames(container.Track);
        SharpQdFrame count = frames.Where(frame => frame.Type == 0x02).MinBy(frame => frame.Position)!;
        SharpQdFrame header = frames.Where(frame => frame.Type == 0x00).MinBy(frame => frame.Position)!;
        SharpQdFrame body = frames.Where(frame => frame.Type == 0x05).MinBy(frame => frame.Position)!;

        Assert.Equal(0x32000, output.Length);
        Assert.Equal("HXCQDDRV"u8.ToArray(), output.AsSpan(0, 8).ToArray());
        Assert.Equal((uint)0x31C00, container.Descriptor.Length);
        Assert.Equal((uint)0x3200, container.Descriptor.WindowStart);
        Assert.Equal((uint)0x25600, container.Descriptor.WindowEnd);
        Assert.Equal((uint)203388, container.BitRate);
        Assert.Equal(0x3202 * 8L + 6, count.Position);
        Assert.Equal(0x478A * 8L + 6, header.Position);
        Assert.Equal(0x4A34 * 8L + 6, body.Position);
        Assert.Equal(HxcCanonicalTrackBuilder.MarioFnblkReadyOffsetBits,
            count.Position - 0x3200 * 8L);
        Assert.Equal(5512 * 8L, header.Position - count.Position);
        Assert.Equal(682 * 8L, body.Position - header.Position);
        Assert.All(container.Track.AsSpan(0x3300, 0x1400).ToArray(), value => Assert.Equal(0xAA, value));
        QuickDiskContainer golden = HxcFlashFloppyQdContainer.Parse(
            ReadQd("hxc_Mario.qd"), QdImageFormat.HxcPhysical);
        SharpQdFrame goldenCount = QuickDiskMfmCodec.FindSharpFrames(golden.Track)
            .Where(frame => frame.Type == 0x02)
            .MinBy(frame => frame.Position)!;
        Assert.Equal(goldenCount.Bytes, count.Bytes);

        TapeRecord reread = Assert.Single(QdImageReaderWriter.Read(output).Records);
        AssertRecordEquivalent(record, reread);
    }

    [Fact]
    public void MarioGoldenReference_HasExpectedGeometryAndFramePositions()
    {
        byte[] image = ReadQd("hxc_Mario.qd");
        Assert.Equal(
            "71244B7B7F833822044B78F74D4A698B16C49F2874CF602890EAF7ECE7CDD43E",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)));
        QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(image, QdImageFormat.HxcPhysical);
        IReadOnlyList<SharpQdFrame> frames = QuickDiskMfmCodec.FindSharpFrames(container.Track);
        SharpQdFrame count = frames.Where(frame => frame.Type == 0x02).MinBy(frame => frame.Position)!;
        SharpQdFrame header = frames.Where(frame => frame.Type == 0x00).MinBy(frame => frame.Position)!;
        SharpQdFrame body = frames.Where(frame => frame.Type == 0x05).MinBy(frame => frame.Position)!;

        Assert.Equal((uint)0x400, container.Descriptor.Offset);
        Assert.Equal((uint)0x31C00, container.Descriptor.Length);
        Assert.Equal((uint)0x3200, container.Descriptor.WindowStart);
        Assert.Equal((uint)0x25600, container.Descriptor.WindowEnd);
        Assert.Equal((uint)203388, container.BitRate);
        Assert.Equal(0x3202 * 8L + 6, count.Position);
        Assert.Equal(0x478A * 8L + 3, header.Position);
        Assert.Equal(0x4A35 * 8L + 2, body.Position);
    }

    [Theory]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void PhysicalCapacityPreflight_AgreesWithWriter(QdImageFormat format)
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);

        Assert.True(QuickDiskPhysicalWriter.TryValidateCapacity([record], format, out string error), error);
        Assert.Equal(0x32000, QdImageReaderWriter.Write([record], format).Length);
    }

    [Theory]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void PhysicalWriters_RoundTripTwoFiles(QdImageFormat format)
    {
        TapeRecord first = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0x11, 0x22], name: "FIRST"));
        TapeRecord second = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0x33, 0x44, 0x55], name: "SECOND"));

        byte[] output = QdImageReaderWriter.Write([first, second], format);
        QdReadResult reread = QdImageReaderWriter.Read(output);

        Assert.Collection(
            reread.Records,
            actual => AssertRecordEquivalent(first, actual),
            actual => AssertRecordEquivalent(second, actual));
        QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(output, format);
        SharpQdFrame count = QuickDiskMfmCodec.FindSharpFrames(container.Track)
            .Where(frame => frame.Type == 0x02)
            .MinBy(frame => frame.Position)!;
        Assert.Equal(4, count.Bytes[1]);
    }

    [Theory]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void PhysicalReader_AcceptsHistoricalPlacements(QdImageFormat format)
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        QuickDiskPhysicalProfile profile = QuickDiskPhysicalProfile.For(format);
        byte[] encoded;
        int historicalTrackDataStart;
        if (format == QdImageFormat.FlashFloppyPhysical)
        {
            byte[] qdf = QDFFileReader.BuildImage([record]);
            encoded = QuickDiskMfmCodec.Encode(qdf.AsSpan(QuickDiskPhysicalWriter.QdfSignatureLength));
            historicalTrackDataStart = 0x3A90 - profile.DataOffset;
        }
        else
        {
            encoded = QuickDiskMfmCodec.Encode(SharpQdFrameCodec.BuildPhysicalByteStream([record]));
            historicalTrackDataStart = profile.WindowStart;
        }

        byte[] track = Enumerable.Repeat(profile.BlankFiller, profile.StoredTrackLength).ToArray();
        encoded.CopyTo(track, historicalTrackDataStart);
        byte[] historicalImage = HxcFlashFloppyQdContainer.Write(track, profile);

        TapeRecord reread = Assert.Single(QdImageReaderWriter.Read(historicalImage).Records);
        AssertRecordEquivalent(record, reread);
    }

    [Fact]
    public void QdfBuilder_MatchesLegacyStreamingWriter()
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        byte[] expected;
        using (var stream = new MemoryStream())
        {
            var writer = new QDFFileReader();
            writer.WriteQDFHeaderToFile(stream, 2);
            writer.WriteQDFFileHeaderToFile(stream, record.Header);
            writer.WriteQDFFileBodyToFile(stream, record.Body);
            writer.WriteBytesToStream(stream, 0, QDFFileReader.ImageSize - stream.Length);
            expected = stream.ToArray();
        }

        Assert.Equal(expected, QDFFileReader.BuildImage([record]));
    }

    [Fact]
    public void FlashFloppyWriter_UsesQdfCapacityLimit()
    {
        TapeRecord template = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        TapeRecord first = WithBodySize(template, 37500);
        TapeRecord second = WithBodySize(template, 37500);

        Assert.False(QuickDiskPhysicalWriter.TryValidateCapacity(
            [first, second], QdImageFormat.FlashFloppyPhysical, out string error));
        Assert.Contains("capacity", error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() =>
            QdImageReaderWriter.Write([first, second], QdImageFormat.FlashFloppyPhysical));
    }

    [Fact]
    public void HxcWriter_RejectsCapacityOverflowConsistently()
    {
        TapeRecord template = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        TapeRecord first = WithBodySize(template, 62000);
        TapeRecord second = WithBodySize(template, 62000);

        Assert.False(QuickDiskPhysicalWriter.TryValidateCapacity(
            [first, second], QdImageFormat.HxcPhysical, out string error));
        Assert.Contains("capacity", error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() =>
            QdImageReaderWriter.Write([first, second], QdImageFormat.HxcPhysical));
    }

    [Theory]
    [InlineData(QdImageFormat.SharpLegacyLogical)]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void Writers_AreDeterministic(QdImageFormat format)
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);

        Assert.Equal(QdImageReaderWriter.Write([record], format), QdImageReaderWriter.Write([record], format));
    }

    [Fact]
    public void Writers_RejectCapacityOverflow()
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        MZQFileHeader header = record.Header;
        header.MzfSize = 62000;
        record.Header = header;
        MZQFileBody body = record.Body;
        body.DataSize = 62000;
        body.MzfBody = new byte[62000];
        record.Body = body;

        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write([record], QdImageFormat.SharpLegacyLogical));
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write([record, record], QdImageFormat.HxcPhysical));
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write([record, record], QdImageFormat.FlashFloppyPhysical));
    }

    [Fact]
    public void ContainerValidation_RejectsTruncatedAndOutOfBoundsDescriptors()
    {
        byte[] truncated = "HXCQDDRV"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(truncated));

        byte[] image = ReadQd("HxC_DSKA0000_Blank.QD");
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512, 4), 0x40000000);
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(image));

        image = ReadQd("Flashfloppy_Blank.qd");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(516, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(524, 4), length + 1);
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(image));
    }

    [Fact]
    public void ContainerValidation_RejectsUnsupportedGeometry()
    {
        byte[] image = ReadQd("HxC_DSKA0000_Blank.QD");
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), 2);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => QdImageReaderWriter.Read(image));
        Assert.Contains("Unsupported QuickDisk geometry", error.Message);
    }

    [Fact]
    public void ValidNonSharpPhysicalImage_OpensAsReadOnlyUnknown()
    {
        byte[] image = ReadQd("HxC_DSKA0000_Blank.QD");
        for (int i = 1024; i < 10000; i++) image[i] = (byte)i;

        var result = QdImageReaderWriter.Read(image);
        Assert.Equal(QuickDiskHostFormat.Unknown, result.Analysis.Identification.HostFormat);
        Assert.False(result.Analysis.Identification.IsNativeSharpMz);
        Assert.Empty(result.Records);
        Assert.Contains("No supported host format could be identified.", result.Analysis.Identification.Warnings);
    }

    private static byte[] ReadQd(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "QD", name);
        if (!File.Exists(path))
        {
            string? replacement = name.ToLowerInvariant() switch
            {
                "hxc_mario.qd" => "hxc_MarioMZ1500.qd",
                "hxc_dska0000_blank.qd" => "hxc_Blank.QD",
                "flashfloppy_blank.qd" => "FF_Blank.qd",
                _ => null
            };
            if (replacement is not null)
            {
                path = Path.Combine(AppContext.BaseDirectory, "QD", replacement);
            }
        }
        if (!File.Exists(path) && name.Equals("Old_MZ700.qd", StringComparison.OrdinalIgnoreCase))
        {
            return QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), QdImageFormat.SharpLegacyLogical);
        }
        return File.ReadAllBytes(path);
    }

    private static void AssertRecordEquivalent(TapeRecord expected, TapeRecord actual)
    {
        Assert.Equal(expected.Header.MzfFtype, actual.Header.MzfFtype);
        Assert.Equal(expected.Header.MzfFname, actual.Header.MzfFname);
        Assert.Equal(expected.Header.MzfFnameEnd, actual.Header.MzfFnameEnd);
        Assert.Equal(expected.Header.MzfSize, actual.Header.MzfSize);
        Assert.Equal(expected.Header.MzfStart, actual.Header.MzfStart);
        Assert.Equal(expected.Header.MzfExec, actual.Header.MzfExec);
        Assert.Equal(expected.Header.MzfHeaderDescription[..38], actual.Header.MzfHeaderDescription[..38]);
        Assert.Equal(expected.Body.MzfBody, actual.Body.MzfBody);
    }

    private static TapeRecord WithBodySize(TapeRecord source, ushort size)
    {
        TapeRecord result = source.DeepClone();
        MZQFileHeader header = result.Header;
        header.MzfSize = size;
        result.Header = header;
        MZQFileBody body = result.Body;
        body.DataSize = size;
        body.MzfBody = new byte[size];
        result.Body = body;
        return result;
    }
}

public class QuickDiskMfmCodecTests
{
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x16)]
    [InlineData(0xA5)]
    [InlineData(0x55)]
    [InlineData(0xAA)]
    [InlineData(0xFF)]
    public void Byte_RoundTripsThroughMfm(byte value)
    {
        byte[] encoded = QuickDiskMfmCodec.Encode([value]);
        Assert.Equal(new byte[] { value }, QuickDiskMfmCodec.Decode(encoded, 1));
    }

    [Fact]
    public void Buffer_RoundTripsThroughMfm()
    {
        var random = new Random(123456);
        byte[] input = new byte[4096];
        random.NextBytes(input);

        Assert.Equal(input, QuickDiskMfmCodec.Decode(QuickDiskMfmCodec.Encode(input), 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    public void FrameFinder_HandlesDifferentInitialBitAlignments(int shift)
    {
        byte[] stream = new byte[16];
        stream[0] = 0x00;
        for (int i = 1; i <= 10; i++) stream[i] = 0x16;
        SharpQdFrameCodec.EncodeCountFrame(0).CopyTo(stream, 11);
        byte[] encoded = QuickDiskMfmCodec.Encode(stream);
        byte[] shifted = ShiftBits(encoded, shift);

        SharpQdFrame frame = Assert.Single(QuickDiskMfmCodec.FindSharpFrames(shifted));
        Assert.Equal((byte)0x02, frame.Type);
        Assert.True(SharpQdFrameCodec.HasValidCrc(frame.Bytes));
    }

    private static byte[] ShiftBits(byte[] source, int shift)
    {
        byte[] result = new byte[source.Length + 2];
        for (int bit = 0; bit < source.Length * 8; bit++)
        {
            if ((source[bit >> 3] & (1 << (bit & 7))) != 0)
            {
                int target = bit + shift;
                result[target >> 3] |= (byte)(1 << (target & 7));
            }
        }
        return result;
    }
}
