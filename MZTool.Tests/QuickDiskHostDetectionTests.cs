using System.Text;
namespace MZTools.Tests;

// Full image integration is optional: supplied media need not be committed.
// The independent synthetic fixtures below always run, including on a clean clone.
public sealed class QuickDiskReferenceTheoryAttribute : TheoryAttribute
{
    public QuickDiskReferenceTheoryAttribute()
    {
        if (!new[] { "DSKA0001_Roland.QD", "DSKA0002_MO5_CQ90-028_formatted.QD", "DSKA0003_Akai_formatted.QD" }
            .All(name => File.Exists(Path.Combine(AppContext.BaseDirectory, "QDReference", name))))
            Skip = "Place the three documented full QD reference images in specification and rebuild to run local integration tests.";
    }
}

public sealed class SharpQdReferenceTheoryAttribute : TheoryAttribute
{
    public SharpQdReferenceTheoryAttribute()
    {
        if (!new[] { "FF_Blank_MZ_Formated.qd", "hxc_Blank_MZ_Formated.QD", "FF_Blank_MZ_Formated_TC122.qd", "hxc_Blank_MZ_Formated_TC122.QD" }
            .All(name => File.Exists(Path.Combine(AppContext.BaseDirectory, "QD", name))))
            Skip = "Place the supplied formatted SHARP media in MZTool.Tests/QD to run reference regressions.";
    }
}

public class QuickDiskHostDetectionTests
{
    [SharpQdReferenceTheory]
    [InlineData("FF_Blank_MZ_Formated.qd", 0)]
    [InlineData("hxc_Blank_MZ_Formated.QD", 0)]
    [InlineData("FF_Blank_MZ_Formated_TC122.qd", 1)]
    [InlineData("hxc_Blank_MZ_Formated_TC122.QD", 1)]
    public void FormattedSharpReferenceMediaRemainNativeDespiteResidualHostSyncs(string name, int count)
    {
        byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "QD", name));
        byte[] original = (byte[])image.Clone();
        var result = QdImageReaderWriter.Read(image);
        Assert.True(result.Analysis.Identification.IsNativeSharpMz, QuickDiskAnalysisReport.Build(result));
        Assert.Equal(QuickDiskHostFormat.SharpMz, result.Analysis.Identification.HostFormat);
        Assert.Equal(count, result.Records.Count);
        Assert.False(result.Analysis.Identification.IsBlank);
        Assert.Equal(original, image);
    }

    [Theory]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void FreshlyFormattedEmptySharpMediaAreNative(QdImageFormat format)
    {
        var result = QdImageReaderWriter.Read(QdImageReaderWriter.Write([], format));
        Assert.True(result.Analysis.Identification.IsNativeSharpMz, QuickDiskAnalysisReport.Build(result));
        Assert.Empty(result.Records);
    }

    [QuickDiskReferenceTheory]
    [InlineData("DSKA0001_Roland.QD", 2, "9630EB4ECD6DDB15843A6AB2FD45107C9CEA6F467789D8161986936D92C3666D")]
    [InlineData("DSKA0002_MO5_CQ90-028_formatted.QD", 6, "257F075371988B32A774A93018296E366C3BBF95D43C9247F70475B48807CB51")]
    [InlineData("DSKA0003_Akai_formatted.QD", 4, "EF369C0726518C32178F0F0C098CB7375CD10219C46D2D07582318776BFF6F30")]
    public void RealReferenceHxCAndEquivalentFlashFloppyIdentifySameHost(string name, int host, string sha)
    {
        byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "QDReference", name));
        Assert.Equal(sha, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)));
        var original = (byte[])image.Clone();
        var result = QdImageReaderWriter.Read(image);
        Assert.True((QuickDiskHostFormat)host == result.Analysis.Identification.HostFormat, QuickDiskAnalysisReport.Build(result));
        Assert.Equal(QuickDiskIdentificationConfidence.High, result.Analysis.Identification.Confidence);
        Assert.False(result.Analysis.Identification.IsNativeSharpMz); Assert.Empty(result.Records);
        Assert.Equal(original, image);
        var container = HxcFlashFloppyQdContainer.Parse(image, QdImageFormat.HxcPhysical);
        var ff = QdImageReaderWriter.Read(Wrap(container.Track, QdImageFormat.FlashFloppyPhysical));
        Assert.Equal(QdImageFormat.FlashFloppyPhysical, ff.Format);
        Assert.Equal(result.Analysis.Identification.HostFormat, ff.Analysis.Identification.HostFormat);
        Assert.Equal(result.Analysis.Identification.Confidence, ff.Analysis.Identification.Confidence);
        if (result.Analysis.Content is RolandQuickDiskContent roland)
        {
            Assert.Equal(3, roland.Blocks.Count); Assert.All(roland.Blocks, b => Assert.True(b.IntegrityValid));
            Assert.Contains("Roland S10", QuickDiskAnalysisReport.Build(result));
            Assert.Equal("likely S-10", result.Analysis.Identification.Origin.ProbableDevice);
            string structure = QuickDiskUnitStructure.DescribeBlock(roland.Blocks[1], result);
            Assert.Contains("Shared LOAD", structure); Assert.Contains("Roland S10", structure);
            Assert.Contains("Start relative to switch window", structure);
        }
        if (result.Analysis.Content is Mo5QuickDiskContent mo5)
        {
            Assert.Equal(400, mo5.Sectors.Count); Assert.Equal(51200, mo5.ExportRaw().Length);
        }
        if (result.Analysis.Content is AkaiQuickDiskContent akai) Assert.True(akai.Block.IntegrityValid);
        var doc = new TapeDocument { Format = result.DocumentFormat, QuickDiskReadResult = result, QuickDiskSourceImage = image };
        Assert.True(doc.IsReadOnlyQuickDisk);
        Assert.Throws<InvalidOperationException>(() => QuickDiskLayoutBuilder.BuildPreviewImage(doc));
        var layout = QuickDiskLayoutBuilder.Build(doc);
        Assert.Contains(layout.Regions, r => r.Kind is QuickDiskRegionKind.HostBlock or QuickDiskRegionKind.HostSector);
        foreach (var region in layout.Regions.Where(r => layout.HostBytes.ContainsKey(r))) Assert.Equal(layout.HostBytes[region], layout.GetBlockBytes(region));
        Assert.Contains("Confidence: High", QuickDiskAnalysisReport.Build(result));
        // File names are not passed to the detector. Rename in a temporary directory to prove the file API too.
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_Roland.QD");
        try { File.WriteAllBytes(temp, image); Assert.Equal((QuickDiskHostFormat)host, QdImageReaderWriter.ReadFile(temp).Analysis.Identification.HostFormat); }
        finally { File.Delete(temp); }
    }

    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void IndependentSyntheticFixturesDecodeAcrossBothContainers(int host)
    {
        var track = MakeTrack((QuickDiskHostFormat)host);
        foreach (var format in new[] { QdImageFormat.HxcPhysical, QdImageFormat.FlashFloppyPhysical })
        {
            var result = QdImageReaderWriter.Read(Wrap(track, format));
            Assert.Equal((QuickDiskHostFormat)host, result.Analysis.Identification.HostFormat);
            Assert.Equal(QuickDiskIdentificationConfidence.High, result.Analysis.Identification.Confidence);
            Assert.Empty(result.Records);
            Assert.False(result.Analysis.Identification.IsNativeSharpMz);
            if (result.Analysis.Content is Mo5QuickDiskContent mo5)
            {
                var raw = mo5.ExportRaw(); Assert.Equal(51200, raw.Length);
                foreach (var sector in mo5.Sectors) Assert.Equal((byte)sector.SectorId, raw[(sector.LogicalSector - 1) * 128]);
            }
        }
    }
    [Fact]
    public void BitOrderCrcAndMo5MappingHaveKnownVectors()
    {
        Assert.Equal(0xFEE8, QuickDiskHostBitstream.Crc16("123456789"u8));
        Assert.Equal(0xA6, QuickDiskHostBitstream.Reverse(0x65));
        Assert.Equal(Enumerable.Range(1, 400), Enumerable.Range(1, 400).Select(QuickDiskHostDetector.Mo5LogicalSector).Order());
        Assert.Equal(new[] {321,33,225,129,322,34,226,130}, Enumerable.Range(1,8).Select(QuickDiskHostDetector.Mo5LogicalSector));
        Assert.Equal(new[] {1,9,5,13,2,10,6,14,3,11,7,15,4,12,8,16}, Enumerable.Range(385,16).Select(QuickDiskHostDetector.Mo5LogicalSector));
        byte[] track = Encode([0x16,0xA5,0x5A,0x80], false);
        Assert.Equal(new byte[] {0x16,0xA5,0x5A,0x80}, QuickDiskHostBitstream.Decode(track, 3, 4, false));
        Assert.Equal(new byte[] {0x68,0xA5,0x5A,0x01}, QuickDiskHostBitstream.Decode(track, 3, 4, true));
    }
    [Fact]
    public void DecodedUnitExportRejectsRawTrackAndMakesAnIndependentCopy()
    {
        var decoded = new QuickDiskInspectorControl.Unit("Block", "cell 42", 3, "CRC valid", [1, 2, 3], IsDecoded: true);
        byte[] exported = QuickDiskInspectorControl.GetDecodedExport(decoded);
        Assert.Equal(decoded.Data, exported); exported[0] = 99; Assert.Equal(1, decoded.Data[0]);
        Assert.Throws<InvalidOperationException>(() => QuickDiskInspectorControl.GetDecodedExport(decoded with { IsDecoded = false }));
        Assert.Throws<InvalidOperationException>(() => QuickDiskInspectorControl.GetDecodedExport(decoded with { Data = [] }));
    }
    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void CorruptChecksumAndTruncatedTracksReduceConfidenceWithoutSharpMisidentification(int host)
    {
        var track = MakeTrack((QuickDiskHostFormat)host);
        var valid = QuickDiskHostDetector.Detect(track);
        int cell = valid.Content switch
        {
            RolandQuickDiskContent roland => roland.Blocks[2].CellOffset + (roland.Blocks[2].ExpectedLength - 1) * 16 + 1,
            AkaiQuickDiskContent akai => akai.Block.CellOffset + (akai.Block.ExpectedLength - 1) * 16 + 1,
            Mo5QuickDiskContent mo5 => mo5.Sectors[0].CellOffset + mo5.Sectors[0].CellLength - 15,
            _ => throw new Exception("Synthetic fixture not identified")
        };
        track[cell / 8] ^= (byte)(1 << (cell % 8));
        var damaged = QuickDiskHostDetector.Detect(track);
        Assert.NotEqual(QuickDiskIdentificationConfidence.High, damaged.Identification.Confidence);
        Assert.False(damaged.Identification.IsNativeSharpMz); Assert.NotEmpty(damaged.Identification.Warnings);
        var truncated = QuickDiskHostDetector.Detect(track.AsSpan(0, Math.Min(track.Length, cell / 8)));
        Assert.NotEqual(QuickDiskIdentificationConfidence.High, truncated.Identification.Confidence);
        Assert.False(truncated.Identification.IsNativeSharpMz);
    }
    [Fact]
    public void Mo5MissingDuplicateAndDamagedHeadersAreNotHighConfidenceOrExportable()
    {
        foreach (var logical in new[] { MakeMo5(399), MakeMo5(400, true), MakeMo5(400, false, true) })
        {
            var result = QuickDiskHostDetector.Detect(Encode(logical, false));
            Assert.NotEqual(QuickDiskIdentificationConfidence.High, result.Identification.Confidence);
            var content = Assert.IsType<Mo5QuickDiskContent>(result.Content);
            Assert.Throws<InvalidDataException>(() => content.ExportRaw()); Assert.NotEmpty(result.Identification.Warnings);
        }
    }
    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void MissingSyncDoesNotIdentifySharp(int host)
    {
        byte[] track = MakeTrack((QuickDiskHostFormat)host);
        var result = QuickDiskHostDetector.Detect(track);
        int start = result.Content switch { RolandQuickDiskContent r => r.Blocks[0].CellOffset, AkaiQuickDiskContent a => a.Block.CellOffset, Mo5QuickDiskContent m => m.Sectors[0].CellOffset, _ => 0 };
        for (int cell = start; cell < start + 128; cell++) track[cell / 8] &= (byte)~(1 << (cell % 8));
        var damaged = QuickDiskHostDetector.Detect(track);
        Assert.False(damaged.Identification.IsNativeSharpMz);
        Assert.NotEqual(QuickDiskIdentificationConfidence.High, damaged.Identification.Confidence);
    }
    [Fact]
    public void S700MarkerDamageIsNotAutomaticallyS612()
    {
        byte[] logical = MakeAkai(true); logical[8] ^= 1;
        var result = QuickDiskHostDetector.Detect(Encode(logical, true));
        Assert.Equal(QuickDiskHostFormat.Unknown, result.Identification.HostFormat);
        Assert.NotEmpty(result.Identification.Warnings);
    }
    [Fact]
    public void WireCompatibleSharpRolandWithoutDeviceEvidenceIsAmbiguousNotEditable()
    {
        var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf(new byte[0xC0A0]));
        var image = QdImageReaderWriter.Write([record], QdImageFormat.HxcPhysical);
        var result = QdImageReaderWriter.Read(image);
        Assert.Equal(QuickDiskHostFormat.Unknown, result.Analysis.Identification.HostFormat);
        Assert.False(result.Analysis.Identification.IsNativeSharpMz);
        Assert.Contains(result.Analysis.Identification.Warnings, warning => warning.Contains("ambiguous"));
    }
    [Theory]
    [InlineData(QdImageFormat.HxcPhysical)] [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void BlankUnknownAndRandomAreReadableAndNeverNativeSharp(QdImageFormat format)
    {
        var blank = QdImageReaderWriter.Read(Wrap(Enumerable.Repeat((byte)0xAA, 4096).ToArray(), format));
        Assert.True(blank.Analysis.Identification.IsBlank); Assert.False(blank.Analysis.Identification.IsNativeSharpMz);
        var random = new byte[4096]; new Random(123).NextBytes(random);
        var result = QdImageReaderWriter.Read(Wrap(random, format));
        Assert.Equal(QuickDiskHostFormat.Unknown, result.Analysis.Identification.HostFormat);
        Assert.False(result.Analysis.Identification.IsBlank); Assert.Empty(result.Records);
        Assert.DoesNotContain(result.Analysis.Identification.Warnings, w => w == "CRC error or corrupt Sharp QD frame.");
        var unknown = QuickDiskHostDetector.Detect(Encode("unidentified physical data"u8.ToArray(), false));
        Assert.Equal(QuickDiskHostFormat.Unknown, unknown.Identification.HostFormat);
    }

    internal static byte[] Wrap(byte[] track, QdImageFormat format) => HxcFlashFloppyQdContainer.Write(track,
        new QuickDiskPhysicalProfile(format, track.Length, track.Length, 0, track.Length, 0xAA, 203389, track.Length + 1024));
    private static byte[] MakeTrack(QuickDiskHostFormat format) => format switch
    {
        QuickDiskHostFormat.Roland => Encode(MakeRoland(), true),
        QuickDiskHostFormat.AkaiS612 => Encode(MakeAkai(false), true),
        QuickDiskHostFormat.AkaiS700 => Encode(MakeAkai(true), true),
        QuickDiskHostFormat.ThomsonMo5 => Encode(MakeMo5(400), false),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    private static byte[] MakeRoland()
    {
        var result = new List<byte>(new byte[40]);
        foreach (int length in new[] {4,0x46,0xC0A6})
        {
            var block = Block(length);
            if (length == 0x46) Encoding.ASCII.GetBytes("Roland S10\r").CopyTo(block, 0xC);
            SealCrc(block); result.AddRange(block); result.AddRange(new byte[40]);
        }
        return result.ToArray();
    }
    private static byte[] MakeAkai(bool s700)
    {
        var block = Block(s700 ? 0xC0BD : 0xFC23);
        if (s700) Encoding.ASCII.GetBytes("S700 FORMAT").CopyTo(block, 8);
        SealCrc(block); return block;
    }
    private static byte[] Block(int length)
    {
        var block = new byte[7 + length];
        for (int index = 0; index < 7; index++) block[index] = 0x16;
        block[7] = 0xA5;
        for (int index = 8; index < block.Length - 2; index++) block[index] = (byte)((index * 37 + 11) % 251);
        return block;
    }
    private static void SealCrc(byte[] block)
    {
        ushort crc = QuickDiskHostBitstream.Crc16(block.AsSpan(7, block.Length - 9), true);
        block[^2] = QuickDiskHostBitstream.Reverse((byte)(crc >> 8)); block[^1] = QuickDiskHostBitstream.Reverse((byte)crc);
    }
    private static byte[] MakeMo5(int count, bool duplicate = false, bool damageHeader = false)
    {
        var result = new List<byte>();
        for (int sector = 1; sector <= count; sector++)
        {
            int id = duplicate && sector == 400 ? 399 : sector;
            result.AddRange(Enumerable.Repeat((byte)0x16, 17)); result.Add(0xA5);
            result.Add((byte)(id >> 8)); result.Add((byte)id); result.Add((byte)(0xA5 + (id >> 8) + (byte)id + (damageHeader && sector == 1 ? 1 : 0)));
            result.AddRange(Enumerable.Repeat((byte)0x16, 10)); result.Add(0x5A);
            result.AddRange(Enumerable.Repeat((byte)sector, 128)); result.Add((byte)(0x5A + (byte)sector * 128));
        }
        return result.ToArray();
    }
    // Independent, conventional MFM encoder, with chronological cells packed LSB-first.
    private static byte[] Encode(byte[] logical, bool reverse)
    {
        var result = new byte[(logical.Length * 16 + 3 + 7) / 8]; int cell = 3, previous = 0;
        void Put(int bit) { if (bit != 0) result[cell / 8] |= (byte)(1 << (cell % 8)); cell++; }
        foreach (byte value in logical)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                int data = (value >> (reverse ? bit : 7 - bit)) & 1;
                Put(previous == 0 && data == 0 ? 1 : 0); Put(data); previous = data;
            }
        }
        return result;
    }
}
