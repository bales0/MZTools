using System.Text;

namespace MZTools.Tests;

public class FormatHelperTests
{
    [Fact]
    public void ConvertMzfNameToAsciiString_StopsAtNulPadding()
    {
        byte[] name = new byte[16];
        "GAME"u8.CopyTo(name);

        Assert.Equal("GAME", SharpMzEncoding.ConvertMzfNameToASCIIString(name));
    }

    [Theory]
    [InlineData(0x00, 0x00)]
    [InlineData(0x01, 0x80)]
    [InlineData(0x55, 0xAA)]
    [InlineData(0xF0, 0x0F)]
    public void ReverseBits_ReturnsExpectedValue(byte input, byte expected)
    {
        Assert.Equal(expected, SharpQdCrc.ReverseBits(input));
    }

    [Fact]
    public void ConvertMzfNameToASCIIString_StopsAtCarriageReturn()
    {
        byte[] name = Encoding.ASCII.GetBytes("HELLO\rIGNORED");

        string result = SharpMzEncoding.ConvertMzfNameToASCIIString(name);

        Assert.Equal("HELLO", result);
    }

    [Fact]
    public void EncodeMzfFileName_UsesSharpEncodingAndCarriageReturnPadding()
    {
        byte[] encoded = MainWindow.EncodeMzfFileName("game 1");

        Assert.Equal(16, encoded.Length);
        Assert.Equal("game 1", SharpMzEncoding.ConvertMzfNameToASCIIString(encoded));
        Assert.All(encoded[6..], value => Assert.Equal(0x0D, value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678901234567")]
    [InlineData("PŘÍLIŠ")]
    public void EncodeMzfFileName_RejectsInvalidNames(string value)
    {
        Assert.Throws<InvalidOperationException>(() => MainWindow.EncodeMzfFileName(value));
    }

    [Fact]
    public void ReadBytesExact_ThrowsForTruncatedInput()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2 });
        using var reader = new BinaryReader(stream);

        EndOfStreamException exception = Assert.Throws<EndOfStreamException>(
            () => SharpBinary.ReadBytesExact(reader, 3, "test field"));

        Assert.Contains("test field", exception.Message);
        Assert.Contains("expected 3 bytes, got 2", exception.Message);
    }

    [Theory]
    [InlineData(0x01, "OBJ")]
    [InlineData(0x05, "RB ")]
    [InlineData(0xFF, "???")]
    public void ConvertFtypeToDescription_ReturnsExpectedDescription(byte fileType, string expected)
    {
        Assert.Equal(expected, SharpMzEncoding.ConvertFtypeToDescription(fileType));
    }
}
