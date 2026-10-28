using System.Buffers.Binary;
using System.Text;

namespace MZTools.Tests;

public class Mz800IplDskReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(5000)]
    public void WriterReaderRoundTrip_PreservesPayloadAndAddresses(int size)
    {
        byte[] body = Enumerable.Range(0, size).Select(value => (byte)(value * 37 + 11)).ToArray();
        TapeRecord source = ReadRecord(body, load: 0x1E00, exec: 0x1E21, name: "ROUND TRIP");

        Mz800IplDskReadResult result = Mz800IplDskReader.Read(
            Mz800IplDskWriter.Build(source, "BOOT test"));

        Assert.Equal("BOOT test", result.BootName);
        Assert.Equal((ushort)1, result.StartBlock);
        Assert.Equal((size + 255) / 256, result.ProgramSectorCount);
        Assert.Equal((byte)0x01, result.Record.Header.MzfFtype);
        Assert.Equal((ushort)size, result.Record.Header.MzfSize);
        Assert.Equal((ushort)0x1E00, result.Record.Header.MzfStart);
        Assert.Equal((ushort)0x1E21, result.Record.Header.MzfExec);
        Assert.Equal(body, result.Record.Body.MzfBody);
        Assert.Empty(result.Record.Body.TrailingData);
        Assert.Equal(TapeProfile.Normal1_1, result.Record.Profile);
        Assert.Equal(MetadataOrigin.Implicit, result.Record.MetadataOrigin);
        Assert.Equal("BOOT test", SharpMzEncoding.ConvertMzfNameToASCIIString(result.Record.Header.MzfFname));
        Assert.Equal(result.Record.Header.MzfSize,
            BinaryPrimitives.ReadUInt16LittleEndian(result.Record.GetSerializedHeader().AsSpan(18, 2)));
    }

    [Fact]
    public void Read_ReportsDiskTypeAndFreeSectorCapacity()
    {
        Mz800IplDskReadResult result = Mz800IplDskReader.Read(
            Mz800IplDskWriter.Build(ReadRecord(new byte[257])));

        Assert.Equal(327424, result.DiskInfo.PayloadCapacityBytes);
        Assert.Equal(512, result.DiskInfo.UsedSectorBytes);
        Assert.Equal(326912, result.DiskInfo.FreeCapacityBytes);
        Assert.Equal(1277, result.DiskInfo.FreeSectorCount);
        string status = MainWindow.GetIplDskAdvancedStatus(result.DiskInfo);
        Assert.Contains("MZ-800 IPLPRO", status);
        Assert.Contains("326912 B", status);
    }

    [Theory]
    [InlineData(0x0000, 0x0010)]
    [InlineData(0x1200, 0x1220)]
    [InlineData(0x1E00, 0x1F00)]
    public void WriterReaderRoundTrip_DoesNotRelocatePayload(int load, int exec)
    {
        byte[] body = Enumerable.Range(0, 1021).Select(value => (byte)value).ToArray();
        TapeRecord source = ReadRecord(body, load, exec);

        TapeRecord actual = Mz800IplDskReader.Read(Mz800IplDskWriter.Build(source)).Record;

        Assert.Equal((ushort)load, actual.Header.MzfStart);
        Assert.Equal((ushort)exec, actual.Header.MzfExec);
        Assert.Equal(body, actual.Body.MzfBody);
    }

    [Fact]
    public void Read_UsesSectorIdsWhenDescriptorsAreReordered()
    {
        byte[] body = Enumerable.Range(0, 600).Select(value => (byte)(value * 13)).ToArray();
        byte[] image = Mz800IplDskWriter.Build(ReadRecord(body));
        SwapDescriptorAndData(image, cylinder: 0, side: 1, firstIndex: 0, secondIndex: 5);

        TapeRecord actual = Mz800IplDskReader.Read(image).Record;

        Assert.Equal(body, actual.Body.MzfBody);
    }

    [Fact]
    public void Read_ImportsPreparedHeaderAsPayloadWithoutReconstructingIt()
    {
        byte[] mzf = CreateMzf([0x11, 0x22, 0x33], load: 0x1200, exec: 0x116C);
        mzf[0x7C] = 0x00;
        TapeRecord source = TapeTestData.ReadRecord(mzf);
        TapeRecord prepared = Mz800IplDskWriter.PrepareRecordForIpl(source);

        TapeRecord actual = Mz800IplDskReader.Read(Mz800IplDskWriter.Build(source)).Record;

        Assert.Equal(prepared.Header.MzfStart, actual.Header.MzfStart);
        Assert.Equal(prepared.Header.MzfExec, actual.Header.MzfExec);
        Assert.Equal(prepared.Body.MzfBody, actual.Body.MzfBody);
    }

    [Fact]
    public void Read_PreservesCompressedPayloadWithoutDecompression()
    {
        TapeRecord source = ReadRecord(Enumerable.Repeat((byte)0xA5, 2048).ToArray(), 0x3000, 0x3100);
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new(MzfCompressionAlgorithm.Zx0),
            CompressionTarget.IplDsk).Record;

        TapeRecord actual = Mz800IplDskReader.Read(Mz800IplDskWriter.Build(packed)).Record;

        Assert.Equal(packed.Header.MzfSize, actual.Header.MzfSize);
        Assert.Equal(packed.Header.MzfStart, actual.Header.MzfStart);
        Assert.Equal(packed.Header.MzfExec, actual.Header.MzfExec);
        Assert.Equal(packed.Body.MzfBody, actual.Body.MzfBody);
    }

    [Fact]
    public void Read_ToleratesUnusedBytesInFinalDeclaredSector()
    {
        byte[] image = Mz800IplDskWriter.Build(ReadRecord([1, 2, 3]));
        SetLogicalByte(image, block: 1, offset: 200, logicalValue: 0x77);

        TapeRecord actual = Mz800IplDskReader.Read(image).Record;

        Assert.Equal(new byte[] { 1, 2, 3 }, actual.Body.MzfBody);
    }

    [Fact]
    public void Read_RejectsDataOutsideDeclaredPayload()
    {
        byte[] image = Mz800IplDskWriter.Build(ReadRecord([1, 2, 3]));
        SetLogicalByte(image, block: 2, offset: 0, logicalValue: 0x77);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Mz800IplDskReader.Read(image));

        Assert.Contains("multipart or boot-menu", exception.Message);
    }

    [Fact]
    public void Read_RejectsInvalidContainerAndTrackMetadata()
    {
        byte[] valid = Mz800IplDskWriter.Build(ReadRecord([1]));

        AssertError(valid, image => image[0] ^= 0x01, "signature");
        AssertError(valid, image => image[0x30] = 39, "geometry");
        AssertError(valid, image => image[0x34] = 0x10, "track 0 has size");
        AssertError(valid, image => image[0x100] ^= 0x01, "Track-Info");
        AssertError(valid, image => image[0x100 + 0x18 + 8 + 2] = 1, "duplicate sector ID");
        AssertError(valid, image => image[0x100 + 0x18 + 3] = 2, "invalid C/H/N");
        AssertError(valid, image => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x100 + 0x18 + 6, 2), 128), "data length");

        InvalidDataException truncated = Assert.Throws<InvalidDataException>(
            () => Mz800IplDskReader.Read(valid.AsSpan(0, valid.Length - 1)));
        Assert.Contains("truncated", truncated.Message);
    }

    [Fact]
    public void Read_RejectsInvalidIplFields()
    {
        byte[] valid = Mz800IplDskWriter.Build(ReadRecord([1, 2, 3]));

        AssertError(valid, image => SetLogicalByte(image, 0, 1, (byte)'X'), "no compatible");
        AssertError(valid, image => SetLogicalUInt16(image, 0, 0x14, 0), "size is zero");
        AssertError(valid, image => SetLogicalUInt16(image, 0, 0x1E, 0), "START_BLOCK");
        AssertError(valid, image => SetLogicalUInt16(image, 0, 0x16, 0xFFFF), "address space");
        AssertError(valid, image => SetLogicalUInt16(image, 0, 0x1E, 1280), "disk capacity");
        AssertError(valid, image =>
        {
            SetLogicalUInt16(image, 0, 0x14, 512);
            SetLogicalUInt16(image, 0, 0x1E, 1279);
        }, "disk capacity");
    }

    [Fact]
    public void Read_UsesSafeFallbackForEmptyBootName()
    {
        byte[] image = Mz800IplDskWriter.Build(ReadRecord([1]));
        for (int index = 0; index < 13; index++)
        {
            SetLogicalByte(image, 0, 0x07 + index, 0x0D);
        }

        Mz800IplDskReadResult result = Mz800IplDskReader.Read(image);

        Assert.Equal("IPL_PROGRAM", result.BootName);
        Assert.Equal("IPL_PROGRAM", SharpMzEncoding.ConvertMzfNameToASCIIString(result.Record.Header.MzfFname));
    }

    [Fact]
    public void FeatureModePolicy_OffersDskInOpenAndIplExportFilters()
    {
        Assert.Contains("*.dsk", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("MZ-800 IPL floppy", FeatureModePolicy.GetOpenFilter());
        Assert.Contains("MZ-800 bootable IPL floppy (*.dsk)|*.dsk", FeatureModePolicy.GetExportFilter(singleRecord: true));
        Assert.Contains("MZ-800 multi-game IPL floppy (*.dsk)|*.dsk", FeatureModePolicy.GetExportFilter(singleRecord: false));
        Assert.DoesNotContain("*.dsk", FeatureModePolicy.GetSaveFilter()); // Tape Save and IPL export have separate policies.
    }

    [Theory]
    [InlineData(2, 0, false, "ZX0 (forward)")]
    [InlineData(2, 1, false, "ZX0 (backward)")]
    [InlineData(3, 0, false, "ZX7 (forward)")]
    [InlineData(3, 1, false, "ZX7 (backward)")]
    [InlineData(3, 0, true, "ZX7 (forward, embedded)")]
    [InlineData(3, 1, true, "ZX7 (backward, embedded)")]
    public void CompressionDetector_RecognizesQdToolLoaders(
        int algorithmValue,
        int directionValue,
        bool embedded,
        string expected)
    {
        TapeRecord source = ReadRecord(
            Enumerable.Range(0, 4096).Select(value => (byte)(value & 31)).ToArray(),
            load: 0x3000,
            exec: 0x3100);
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new(
                (MzfCompressionAlgorithm)algorithmValue,
                (CompressionDirection)directionValue,
                Zx7EmbeddedLoader: embedded),
            CompressionTarget.MzfTape).Record;

        Assert.Equal(expected, MzfLoaderBuilder.DetectCompression(packed));
    }

    [Fact]
    public void CompressionDetector_ReturnsUnknownForOrdinaryRecord()
    {
        Assert.Equal("None / unknown", MzfLoaderBuilder.DetectCompression(ReadRecord([1, 2, 3])));
    }

    private static TapeRecord ReadRecord(byte[] body, int load = 0x1200, int exec = 0x1200, string name = "TEST") =>
        TapeTestData.ReadRecord(CreateMzf(body, load, exec, name));

    private static byte[] CreateMzf(byte[] body, int load, int exec, string name = "TEST")
    {
        byte[] result = new byte[TapeRecord.HeaderLength + body.Length];
        result[0] = 0x01;
        Encoding.ASCII.GetBytes(name.AsSpan(0, Math.Min(name.Length, 16)), result.AsSpan(1));
        result[17] = 0x0D;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18, 2), checked((ushort)body.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), checked((ushort)load));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), checked((ushort)exec));
        body.CopyTo(result, TapeRecord.HeaderLength);
        return result;
    }

    private static void AssertError(byte[] source, Action<byte[]> mutate, string messagePart)
    {
        byte[] image = (byte[])source.Clone();
        mutate(image);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Mz800IplDskReader.Read(image));
        Assert.Contains(messagePart, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void SwapDescriptorAndData(byte[] image, int cylinder, int side, int firstIndex, int secondIndex)
    {
        int trackOffset = 0x100 + (cylinder * 2 + side) * 0x1100;
        SwapRanges(image, trackOffset + 0x18 + firstIndex * 8, trackOffset + 0x18 + secondIndex * 8, 8);
        SwapRanges(image, trackOffset + 0x100 + firstIndex * 256, trackOffset + 0x100 + secondIndex * 256, 256);
    }

    private static void SwapRanges(byte[] bytes, int firstOffset, int secondOffset, int length)
    {
        for (int index = 0; index < length; index++)
        {
            (bytes[firstOffset + index], bytes[secondOffset + index]) =
                (bytes[secondOffset + index], bytes[firstOffset + index]);
        }
    }

    private static void SetLogicalUInt16(byte[] image, int block, int offset, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        SetLogicalByte(image, block, offset, bytes[0]);
        SetLogicalByte(image, block, offset + 1, bytes[1]);
    }

    private static void SetLogicalByte(byte[] image, int block, int offset, byte logicalValue)
    {
        (int cylinder, int side, int sector) = Mz800IplDskWriter.MapLogicalBlock(block);
        int trackOffset = 0x100 + (cylinder * 2 + side) * 0x1100;
        int physicalOffset = trackOffset + 0x100 + (sector - 1) * 256 + offset;
        image[physicalOffset] = (byte)(logicalValue ^ 0xFF);
    }
}
