using System;
using System.Buffers.Binary;
using System.IO;

namespace MZTools;

internal static class AudioSyntheticHeader
{
    internal static TapeRecord Create(byte[] payload, string name, byte type, ushort load, ushort exec, string description)
    {
        if (payload.Length is < 1 or > ushort.MaxValue) throw new ArgumentException("MZF payload length must be 1–65535 bytes.");
        if (type == 0) throw new ArgumentException("Specify a nonzero MZF type.");
        var encoded = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(name);
        if (string.IsNullOrWhiteSpace(name) || encoded.Length > 16 || name.Contains('\r') || name.Contains('\0') ||
            SharpMzEncoding.ConvertMzfNameToASCIIString(encoded).TrimEnd() != name.TrimEnd())
            throw new ArgumentException("Enter 1–16 characters representable in the Sharp filename character set.");
        byte[] bytes = new byte[128 + payload.Length]; bytes[0] = type; encoded.CopyTo(bytes, 1); bytes[17] = 13;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, 2), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), load); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), exec);
        var note = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("SYNTHETIC HEADER; " + description);
        note.AsSpan(0, Math.Min(104, note.Length)).CopyTo(bytes.AsSpan(24, 104)); payload.CopyTo(bytes, 128);
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        return new MZTFileReader().ReadMzfRecord(reader);
    }
}
