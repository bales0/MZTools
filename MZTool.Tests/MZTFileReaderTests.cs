using System.Buffers.Binary;

namespace MZTools.Tests;

public class MZTFileReaderTests
{
    [Fact]
    public void ReadMzfFile_ReadsHeaderAndBody()
    {
        byte[] bytes = CreateMzfFile(new byte[] { 0x10, 0x20, 0x30 });
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        var (header, body) = new MZTFileReader().ReadMzfFile(reader);

        Assert.Equal((byte)0x01, header.MzfFtype);
        Assert.Equal((ushort)3, header.MzfSize);
        Assert.Equal((ushort)0x1200, header.MzfStart);
        Assert.Equal((ushort)0x1203, header.MzfExec);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, body.MzfBody);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void ReadMzfFile_ThrowsForTruncatedBody()
    {
        byte[] bytes = CreateMzfFile(new byte[] { 0x10, 0x20, 0x30 });
        Array.Resize(ref bytes, bytes.Length - 1);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        EndOfStreamException exception = Assert.Throws<EndOfStreamException>(
            () => new MZTFileReader().ReadMzfFile(reader));

        Assert.Contains("MZF file body", exception.Message);
    }

    private static byte[] CreateMzfFile(byte[] body)
    {
        byte[] bytes = new byte[128 + body.Length];
        bytes[0] = 0x01;
        "TEST"u8.CopyTo(bytes.AsSpan(1));
        bytes[17] = 0x0D;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, 2), (ushort)body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 0x1200);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 0x1203);
        body.CopyTo(bytes, 128);
        return bytes;
    }
}
