using System.Buffers.Binary;

namespace MZTools.Tests;

internal static class TapeTestData
{
    public static byte[] CreateMzf(byte[] body, byte[]? description = null, byte[]? trailing = null, string name = "TEST")
    {
        byte[] result = new byte[128 + body.Length + (trailing?.Length ?? 0)];
        result[0] = 0x01;
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(result, 1);
        result[17] = 0x0D;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18, 2), (ushort)body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), 0x1200);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), 0x1200);
        (description ?? new byte[104]).CopyTo(result, 24);
        body.CopyTo(result, 128);
        trailing?.CopyTo(result, 128 + body.Length);
        return result;
    }

    public static TapeRecord ReadRecord(byte[] mzf)
    {
        using var stream = new MemoryStream(mzf);
        using var reader = new BinaryReader(stream);
        return new MZTFileReader().ReadMzfRecord(reader);
    }

    public static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mztools-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
