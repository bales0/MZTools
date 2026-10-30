using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace MZTools;

// Explicit compatibility repair only. Parsing and ordinary serialization remain byte-preserving.
internal static class DskExtendedLengthRepair
{
    internal static byte[] CreateCopy(byte[] original)
    {
        var image = DskImage.Parse(original);
        var result = (byte[])original.Clone();
        foreach (var track in image.Tracks.Where(t => t != null))
            for (int index = 0; index < track!.Sectors.Count; index++)
            {
                var sector = track.Sectors[index];
                if (sector.DeclaredDataLength == 0)
                    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(track.FileOffset + 0x18 + index * 8 + 6, 2), checked((ushort)sector.Data.Length));
            }
        var reopened = DskImage.Parse(result);
        if (reopened.Tracks.Where(t => t != null).SelectMany(t => t!.Sectors).Any(s => s.DeclaredDataLength != s.Data.Length))
            throw new InvalidDataException("Repaired Extended DSK lengths failed validation.");
        return result;
    }

    internal static void WriteNewCopy(string sourcePath, string destinationPath)
    {
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A length repair must not overwrite its source.");
        byte[] result = CreateCopy(File.ReadAllBytes(sourcePath));
        using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(result);
    }
}
