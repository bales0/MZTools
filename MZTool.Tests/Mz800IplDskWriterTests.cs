using System.Buffers.Binary;
using System.Text;

namespace MZTools.Tests;

public class Mz800IplDskWriterTests
{
    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(15, 0, 1, 16)]
    [InlineData(16, 0, 0, 1)]
    [InlineData(17, 0, 0, 2)]
    [InlineData(31, 0, 0, 16)]
    [InlineData(32, 1, 1, 1)]
    [InlineData(48, 1, 0, 1)]
    [InlineData(1279, 39, 0, 16)]
    public void BlockMapping_MatchesFsmzLayout(int block, int cylinder, int side, int sector)
    {
        Assert.Equal((cylinder, side, sector), Mz800IplDskWriter.MapLogicalBlock(block));
    }

    [Fact]
    public void Build_CreatesExactExtendedDskGeometry()
    {
        TapeRecord record = CompressionTestData.ReadRecord(CompressionTestData.CreateMzf([1, 2, 3]));

        byte[] image = Mz800IplDskWriter.Build(record);

        Assert.Equal(348416, image.Length);
        Assert.Equal("EXTENDED CPC DSK File\r\nDisk-Info\r\n", Encoding.ASCII.GetString(image, 0, 34));
        Assert.Equal("MZTools IPLDSK", Encoding.ASCII.GetString(image, 0x22, 14));
        Assert.Equal(40, image[0x30]);
        Assert.Equal(2, image[0x31]);
        Assert.All(image[0x34..(0x34 + 80)], value => Assert.Equal(0x11, value));

        for (int physicalTrack = 0; physicalTrack < 80; physicalTrack++)
        {
            int cylinder = physicalTrack / 2;
            int side = physicalTrack & 1;
            int offset = 0x100 + physicalTrack * 0x1100;
            Assert.Equal("Track-Info\r\n", Encoding.ASCII.GetString(image, offset, 12));
            Assert.Equal(cylinder, image[offset + 0x10]);
            Assert.Equal(side, image[offset + 0x11]);
            Assert.Equal(1, image[offset + 0x14]);
            Assert.Equal(16, image[offset + 0x15]);
            Assert.Equal(0x4E, image[offset + 0x16]);
            Assert.Equal(0xFF, image[offset + 0x17]);
            for (int sector = 0; sector < 16; sector++)
            {
                int descriptor = offset + 0x18 + sector * 8;
                Assert.Equal(cylinder, image[descriptor]);
                Assert.Equal(side, image[descriptor + 1]);
                Assert.Equal(sector + 1, image[descriptor + 2]);
                Assert.Equal(1, image[descriptor + 3]);
                Assert.Equal(new byte[2], image[(descriptor + 4)..(descriptor + 6)]);
                Assert.Equal(256, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(descriptor + 6, 2)));
            }
        }
    }

    [Fact]
    public void Build_WritesIplMetadataBodyPaddingAndUnusedSectorsLogically()
    {
        byte[] body = Enumerable.Range(0, 300).Select(value => (byte)(value * 13)).ToArray();
        TapeRecord record = CompressionTestData.ReadRecord(CompressionTestData.CreateMzf(
            body,
            name: "LONG PROGRAM NAME",
            load: 0x2345,
            exec: 0x2468));
        byte[] sourceHeader = record.GetSerializedHeader();
        byte[] sourceBody = (byte[])record.Body.MzfBody.Clone();

        byte[] image = Mz800IplDskWriter.Build(record);
        byte[] ipl = ReadLogicalBlock(image, 0);

        Assert.Equal(0x03, ipl[0]);
        Assert.Equal("IPLPRO", Encoding.ASCII.GetString(ipl, 1, 6));
        Assert.Equal("LONG PROGRAM", Encoding.ASCII.GetString(ipl, 7, 12));
        Assert.Equal(0x0D, ipl[0x13]);
        Assert.Equal((ushort)body.Length, BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x14, 2)));
        Assert.Equal(0x2345, BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x16, 2)));
        Assert.Equal(0x2468, BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x18, 2)));
        Assert.Equal(new byte[4], ipl[0x1A..0x1E]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x1E, 2)));

        Assert.Equal(body[..256], ReadLogicalBlock(image, 1));
        byte[] final = ReadLogicalBlock(image, 2);
        Assert.Equal(body[256..], final[..44]);
        Assert.Equal(new byte[212], final[44..]);
        Assert.Equal(new byte[256], ReadLogicalBlock(image, 3));
        Assert.Equal(sourceHeader, record.GetSerializedHeader());
        Assert.Equal(sourceBody, record.Body.MzfBody);
    }

    [Fact]
    public void Build_TruncatesEndAndEncodesLowercaseDisplayNameAsSharpAscii()
    {
        TapeRecord record = CompressionTestData.ReadRecord(CompressionTestData.CreateMzf([1]));

        byte[] image = Mz800IplDskWriter.Build(record, "FLAPPY ver 1.0A");
        byte[] ipl = ReadLogicalBlock(image, 0);

        Assert.Equal(
            new byte[] { 0x46, 0x4C, 0x41, 0x50, 0x50, 0x59, 0x20, 0xAB, 0x92, 0x9D, 0x20, 0x31 },
            ipl[0x07..0x13]);
        Assert.Equal(
            "FLAPPY ver 1",
            Encoding.ASCII.GetString(ipl[0x07..0x13].Select(SharpMzEncoding.FromSHASCII).ToArray()));
    }

    [Fact]
    public void PrepareRecordForIpl_ResolvesHeaderJumpWithoutChangingBody()
    {
        byte[] body = [0x11, 0x22, 0x33, 0x44];
        byte[] mzf = CompressionTestData.CreateMzf(
            body,
            name: "HEADER EXEC",
            load: 0x1200,
            exec: 0x116C);
        mzf[0x7C] = 0xC3;
        BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(0x7D, 2), 0x1202);
        TapeRecord source = CompressionTestData.ReadRecord(mzf);
        byte[] originalHeader = source.GetSerializedHeader();
        byte[] originalBody = (byte[])source.Body.MzfBody.Clone();

        TapeRecord resolved = Mz800IplDskWriter.PrepareRecordForIpl(source);

        Assert.Equal(0x1200, resolved.Header.MzfStart);
        Assert.Equal(0x1202, resolved.Header.MzfExec);
        Assert.Equal(body.Length, resolved.Header.MzfSize);
        Assert.Equal(body, resolved.Body.MzfBody);
        Assert.Equal(originalHeader, source.GetSerializedHeader());
        Assert.Equal(originalBody, source.Body.MzfBody);
    }

    [Fact]
    public void CompressionForIpl_UsesResolvedHeaderJumpAsOriginalExec()
    {
        byte[] mzf = CompressionTestData.CreateMzf(
            Enumerable.Repeat((byte)0xA5, 512).ToArray(),
            name: "HEADER EXEC",
            load: 0x1200,
            exec: 0x116C);
        mzf[0x7C] = 0xC3;
        BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(0x7D, 2), 0x1300);
        TapeRecord source = CompressionTestData.ReadRecord(mzf);

        MzfCompressionResult result = MzfCompressionService.Compress(
            source,
            new(MzfCompressionAlgorithm.Zx7),
            CompressionTarget.IplDsk);

        Assert.Equal(512, result.OriginalSize);
        Assert.Equal(0x1300, BinaryPrimitives.ReadUInt16LittleEndian(result.Record.Body.MzfBody.AsSpan(0x0C + 1, 2)));
        Assert.Equal(0x1200, source.Header.MzfStart);
        Assert.Equal(0x116C, source.Header.MzfExec);
        Assert.Equal(512, source.Body.MzfBody.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void HlipaFixture_ResolvesHeaderJumpAndBuildsIplImage(int algorithmValue)
    {
        var algorithm = (MzfCompressionAlgorithm)algorithmValue;
        string path = Path.Combine(AppContext.BaseDirectory, "MZF", "Hlipa.mzf");
        TapeRecord source = new MZTFileReader().ReadStandaloneMzf(path);

        TapeRecord resolved = Mz800IplDskWriter.PrepareRecordForIpl(source);

        Assert.Equal(0x1200, resolved.Header.MzfStart);
        Assert.Equal(0xCA00, resolved.Header.MzfExec);
        Assert.Equal(0xB852, resolved.Header.MzfSize);
        Assert.Equal(source.Body.MzfBody, resolved.Body.MzfBody);

        MzfCompressionResult prepared = MzfCompressionService.Compress(
            source,
            new(algorithm),
            CompressionTarget.IplDsk);
        byte[] image = Mz800IplDskWriter.Build(prepared.Record, "HLIPA");
        byte[] ipl = ReadLogicalBlock(image, 0);

        Assert.Equal(Mz800IplDskWriter.ImageSize, image.Length);
        Assert.Equal(prepared.Record.Header.MzfSize,
            BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x14, 2)));
        Assert.Equal(prepared.Record.Header.MzfStart,
            BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x16, 2)));
        Assert.Equal(prepared.Record.Header.MzfExec,
            BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x18, 2)));
        Assert.Equal(0x116C, source.Header.MzfExec);
    }

    [Fact]
    public void Build_InvertsOnlySectorData()
    {
        TapeRecord record = CompressionTestData.ReadRecord(CompressionTestData.CreateMzf([0x00, 0x55, 0xFF]));
        byte[] image = Mz800IplDskWriter.Build(record);
        (int cylinder, int side, int sector) = Mz800IplDskWriter.MapLogicalBlock(1);
        int trackOffset = 0x100 + (cylinder * 2 + side) * 0x1100;
        int dataOffset = trackOffset + 0x100 + (sector - 1) * 256;

        Assert.Equal("Track-Info\r\n", Encoding.ASCII.GetString(image, trackOffset, 12));
        Assert.Equal(new byte[] { 0xFF, 0xAA, 0x00 }, image[dataOffset..(dataOffset + 3)]);
        Assert.All(image[(dataOffset + 3)..(dataOffset + 256)], value => Assert.Equal(0xFF, value));
    }

    [Fact]
    public void Build_RejectsProgramsBeyondSafeStagingLimit()
    {
        TapeRecord record = CompressionTestData.ReadRecord(CompressionTestData.CreateMzf(
            new byte[Mz800IplDskWriter.MaxIplStagedSize + 1],
            load: 0));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Mz800IplDskWriter.Build(record));

        Assert.Contains("48361", exception.Message);
        Assert.Contains("$BCE9", exception.Message);
    }

    [Fact]
    public void Build_UsesPackedMetadataAndBodyWithoutMutatingOriginal()
    {
        byte[] body = Enumerable.Repeat((byte)0xA5, 2048).ToArray();
        TapeRecord source = CompressionTestData.ReadRecord(CompressionTestData.CreateMzf(
            body,
            name: "PACKED",
            load: 0x3000,
            exec: 0x3123));
        byte[] original = TapeDocumentWriter.SerializeMzf(source, preserveTrailing: true);
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new(MzfCompressionAlgorithm.Zx0),
            CompressionTarget.IplDsk).Record;

        byte[] image = Mz800IplDskWriter.Build(packed);
        byte[] ipl = ReadLogicalBlock(image, 0);
        byte[] firstBodySector = ReadLogicalBlock(image, 1);

        Assert.Equal(packed.Header.MzfSize, BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x14, 2)));
        Assert.Equal(packed.Header.MzfStart, BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x16, 2)));
        Assert.Equal(packed.Header.MzfExec, BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x18, 2)));
        Assert.Equal(packed.Body.MzfBody[..Math.Min(256, packed.Body.MzfBody.Length)],
            firstBodySector[..Math.Min(256, packed.Body.MzfBody.Length)]);
        Assert.Equal(original, TapeDocumentWriter.SerializeMzf(source, preserveTrailing: true));
    }

    private static byte[] ReadLogicalBlock(byte[] image, int block)
    {
        (int cylinder, int side, int sector) = Mz800IplDskWriter.MapLogicalBlock(block);
        int physicalTrack = cylinder * 2 + side;
        int offset = 0x100 + physicalTrack * 0x1100 + 0x100 + (sector - 1) * 256;
        return image[offset..(offset + 256)].Select(value => (byte)(value ^ 0xFF)).ToArray();
    }
}
