using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static QDTool.MzfFormatSupport;
using static QDTool.SharpBinary;
using static QDTool.SharpQdCrc;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MZQHeader
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] StartSign;
    public byte FileBlocksCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] Crc;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MZQFileHeader
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] StartSign;
    public byte MzfHeaderSign;
    public ushort DataSize;
    public byte MzfFtype;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] MzfFname;
    public byte MzfFnameEnd;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public byte[] Unused1;
    public ushort MzfSize;
    public ushort MzfStart;
    public ushort MzfExec;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 104)]
    public byte[] MzfHeaderDescription;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] Crc;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MZQFileBody
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] StartSign;
    public byte MzfBodySign;
    public ushort DataSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65535)]
    public byte[] MzfBody;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] Crc;
    public byte[] TrailingData;
}

namespace QDTool
{
    internal static class SharpQdCrc
    {
        private static ushort crc;

        public static ushort CRC_check(byte data, bool initialize = false)
        {
            if (initialize)
            {
                crc = 0;
            }

            byte remaining = data;
            for (int index = 0; index < 8; index++)
            {
                byte xor = (byte)(remaining & 1);
                remaining >>= 1;
                if ((crc & 0x8000) != 0)
                {
                    xor ^= 1;
                }

                crc <<= 1;
                if (xor != 0)
                {
                    crc ^= 0x8005;
                }
            }
            return crc;
        }

        public static ushort CRC_check(ushort data, bool initialize = false)
        {
            CRC_check((byte)(data & 0xFF), initialize);
            return CRC_check((byte)(data >> 8));
        }

        public static ushort CRC_check(
            byte[] buffer,
            int offset,
            int count,
            bool initialize = false)
        {
            bool init = initialize;
            for (int index = offset; index < offset + count; index++)
            {
                CRC_check(buffer[index], init);
                init = false;
            }
            return crc;
        }

        public static byte ReverseBits(byte value)
        {
            value = (byte)((value & 0x55) << 1 | (value >> 1) & 0x55);
            value = (byte)((value & 0x33) << 2 | (value >> 2) & 0x33);
            return (byte)((value & 0x0F) << 4 | (value >> 4) & 0x0F);
        }
    }

    #region Enums, records and limits

    public enum QdImageFormat
        {
            Unknown,
            SharpLegacyLogical,
            HxcPhysical,
            FlashFloppyPhysical
        }

        internal sealed record QdReadResult(
            QdImageFormat Format,
            IReadOnlyList<TapeRecord> Records,
            QuickDiskPhysicalProfile? PhysicalProfile = null)
        {
            public TapeDocumentFormat DocumentFormat => Format switch
            {
                QdImageFormat.SharpLegacyLogical => TapeDocumentFormat.QdSharpLegacy,
                QdImageFormat.HxcPhysical => TapeDocumentFormat.QdHxc,
                QdImageFormat.FlashFloppyPhysical => TapeDocumentFormat.QdFlashFloppy,
                _ => throw new InvalidOperationException("Unknown QD image format has no document format.")
            };
        }

        internal static class QuickDiskLimits
        {
            // MZ-1F11 system format limit. The standard MZ-800 ROM directory
            // buffer below is smaller and is the normal creation/editing limit.
            public const int FormatMaximumFiles = 50;
            public const int StandardDirectoryEntries = 34;

            public static void ValidateImportedCount(int fileCount)
            {
                if (fileCount > FormatMaximumFiles)
                {
                    throw new InvalidDataException(
                        $"Unsupported QuickDisk directory: {fileCount} files exceed the format limit of {FormatMaximumFiles}.");
                }
            }

            public static bool TryValidateForSave(int fileCount, bool allowImportedNonStandard, out string error)
            {
                int limit = allowImportedNonStandard ? FormatMaximumFiles : StandardDirectoryEntries;
                if (fileCount > limit)
                {
                    error = allowImportedNonStandard
                        ? $"QuickDisk supports at most {FormatMaximumFiles} files."
                        : $"A standard MZ-800 QuickDisk supports at most {StandardDirectoryEntries} directory entries; remove {fileCount - StandardDirectoryEntries} file(s) before saving.";
                    return false;
                }
                error = string.Empty;
                return true;
            }
        }

    #endregion

    #region Format detection

    internal static class QdFormatDetector
        {
            private static ReadOnlySpan<byte> HxcSignature => "HXCQDDRV"u8;

            public static QdImageFormat Detect(ReadOnlySpan<byte> bytes)
            {
                if (bytes.Length >= HxcSignature.Length && bytes[..HxcSignature.Length].SequenceEqual(HxcSignature))
                {
                    return QdImageFormat.HxcPhysical;
                }

                if (bytes.Length >= 5 && bytes[3] == (byte)'Q' && bytes[4] == (byte)'D')
                {
                    return QdImageFormat.FlashFloppyPhysical;
                }

                if (bytes.Length >= 8 &&
                    bytes[0] == 0x00 && bytes[1] == 0x16 && bytes[2] == 0x16 && bytes[3] == 0xA5 &&
                    bytes[5] == (byte)'C' && bytes[6] == (byte)'R' && bytes[7] == (byte)'C')
                {
                    return QdImageFormat.SharpLegacyLogical;
                }

                return QdImageFormat.Unknown;
            }
        }

    #endregion

    #region Common Sharp QD framing and CRC

    internal sealed record SharpQdFrame(long Position, byte Type, byte[] Bytes);

        internal static class SharpQdFrameCodec
        {
            internal const int HeaderDataLength = 64;
            internal const int HeaderFrameLength = 70;
            private static readonly object CrcLock = new();

            public static IReadOnlyList<SharpQdFrame> FindFrames(ReadOnlySpan<byte> decoded, long firstBitPosition = 0)
            {
                var frames = new List<SharpQdFrame>();
                for (int index = 0; index <= decoded.Length - 4; index++)
                {
                    if (decoded[index] != 0xA5 || !HasSharpSync(decoded, index))
                    {
                        continue;
                    }

                    int frameLength;
                    byte type = decoded[index + 1];
                    byte[] countCandidate = decoded.Slice(index, 4).ToArray();
                    if ((type & 1) == 0 && HasValidCrc(countCandidate))
                    {
                        frames.Add(new SharpQdFrame(firstBitPosition + index * 16L, 0x02, countCandidate));
                        continue;
                    }

                    if (type is 0x00 or 0x05)
                    {
                        if (index + 4 > decoded.Length)
                        {
                            continue;
                        }

                        int dataLength = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Slice(index + 2, 2));
                        if (type == 0x00 && dataLength != HeaderDataLength)
                        {
                            continue;
                        }

                        frameLength = 1 + 1 + 2 + dataLength + 2;
                    }
                    else
                    {
                        continue;
                    }

                    if (frameLength < 4 || index > decoded.Length - frameLength)
                    {
                        continue;
                    }

                    byte[] bytes = decoded.Slice(index, frameLength).ToArray();
                    if (HasValidCrc(bytes))
                    {
                        frames.Add(new SharpQdFrame(firstBitPosition + index * 16L, type, bytes));
                    }
                }

                return frames;
            }

            public static List<(MZQFileHeader, MZQFileBody)> DecodeRecords(IEnumerable<SharpQdFrame> candidates)
            {
                List<SharpQdFrame> frames = candidates
                    .OrderBy(frame => frame.Position)
                    .Aggregate(new List<SharpQdFrame>(), (result, frame) =>
                    {
                        if (!result.Any(existing =>
                            Math.Abs(existing.Position - frame.Position) <= 15 &&
                            existing.Bytes.AsSpan().SequenceEqual(frame.Bytes)))
                        {
                            result.Add(frame);
                        }
                        return result;
                    });

                bool foundCountFrame = false;
                string? lastError = null;
                foreach (SharpQdFrame countFrame in frames.Where(frame => frame.Type == 0x02))
                {
                    foundCountFrame = true;
                    int blockCount = countFrame.Bytes[1];
                    if ((blockCount & 1) != 0)
                    {
                        lastError = "FNBLK/header/body inconsistency: the Sharp QD block count is odd.";
                        continue;
                    }

                    QuickDiskLimits.ValidateImportedCount(blockCount / 2);

                    if (blockCount == 0)
                    {
                        return new List<(MZQFileHeader, MZQFileBody)>();
                    }

                    List<SharpQdFrame> following = frames
                        .Where(frame => frame.Position > countFrame.Position && frame.Type is 0x00 or 0x05)
                        .ToList();
                    if (following.Count < blockCount)
                    {
                        lastError = "Corrupt Sharp QD frame sequence: FNBLK declares more blocks than were found.";
                        continue;
                    }

                    var records = new List<(MZQFileHeader, MZQFileBody)>();
                    bool consistent = true;
                    for (int block = 0; block < blockCount; block += 2)
                    {
                        SharpQdFrame headerFrame = following[block];
                        SharpQdFrame bodyFrame = following[block + 1];
                        if (headerFrame.Type != 0x00 || bodyFrame.Type != 0x05)
                        {
                            lastError = "FNBLK/header/body inconsistency in Sharp QD image.";
                            consistent = false;
                            break;
                        }

                        MZQFileHeader header = DecodeHeader(headerFrame.Bytes);
                        MZQFileBody body = DecodeBody(bodyFrame.Bytes);
                        if (header.MzfSize != body.DataSize)
                        {
                            lastError = $"FNBLK/header/body inconsistency: header declares {header.MzfSize} bytes, body declares {body.DataSize} bytes.";
                            consistent = false;
                            break;
                        }
                        records.Add((header, body));
                    }

                    if (consistent)
                    {
                        return records;
                    }
                }

                if (!foundCountFrame)
                {
                    throw new InvalidDataException("Corrupt Sharp QD frame sequence: a CRC-valid FNBLK frame was not found.");
                }

                throw new InvalidDataException(lastError ?? "Corrupt Sharp QD frame sequence.");
            }

            public static List<(MZQFileHeader, MZQFileBody)> DecodeByteStream(ReadOnlySpan<byte> bytes)
            {
                IReadOnlyList<SharpQdFrame> frames = FindFrames(bytes);
                return DecodeRecords(frames);
            }

            internal static bool ContainsPlausibleFrame(ReadOnlySpan<byte> decoded)
            {
                for (int index = 0; index <= decoded.Length - 4; index++)
                {
                    if (decoded[index] != 0xA5 || !HasSharpSync(decoded, index))
                    {
                        continue;
                    }

                    byte marker = decoded[index + 1];
                    if ((marker & 1) == 0)
                    {
                        return true;
                    }
                    if (marker == 0x05)
                    {
                        int length = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Slice(index + 2, 2));
                        if (index <= decoded.Length - (6 + length))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            public static byte[] BuildPhysicalByteStream(IReadOnlyList<TapeRecord> records)
            {
                QuickDiskLimits.ValidateImportedCount(records.Count);

                using var stream = new MemoryStream();
                WriteFramed(stream, EncodeCountFrame(checked((byte)(records.Count * 2))), 256);
                foreach (TapeRecord record in records)
                {
                    WriteFramed(stream, EncodeHeaderFrame(record.Header), 254);
                    WriteFramed(stream, EncodeBodyFrame(record.Body), 256);
                }
                return stream.ToArray();
            }

            public static byte[] EncodeCountFrame(byte blockCount) => AddCrc([0xA5, blockCount]);

            public static byte[] EncodeHeaderFrame(MZQFileHeader header)
            {
                byte[] withoutCrc = new byte[HeaderFrameLength - 2];
                withoutCrc[0] = 0xA5;
                withoutCrc[1] = 0x00;
                BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(2, 2), HeaderDataLength);
                withoutCrc[4] = header.MzfFtype;
                CopyExactOrPad(header.MzfFname, withoutCrc.AsSpan(5, 16));
                withoutCrc[21] = header.MzfFnameEnd;
                CopyExactOrPad(header.Unused1, withoutCrc.AsSpan(22, 2));
                BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(24, 2), header.MzfSize);
                BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(26, 2), header.MzfStart);
                BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(28, 2), header.MzfExec);
                CopyExactOrPad(header.MzfHeaderDescription, withoutCrc.AsSpan(30, 38));
                return AddCrc(withoutCrc);
            }

            public static byte[] EncodeBodyFrame(MZQFileBody body)
            {
                if (body.MzfBody is null || body.MzfBody.Length < body.DataSize)
                {
                    throw new InvalidDataException("QuickDisk body is shorter than its declared size.");
                }

                byte[] withoutCrc = new byte[4 + body.DataSize];
                withoutCrc[0] = 0xA5;
                withoutCrc[1] = 0x05;
                BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(2, 2), body.DataSize);
                body.MzfBody.AsSpan(0, body.DataSize).CopyTo(withoutCrc.AsSpan(4));
                return AddCrc(withoutCrc);
            }

            internal static bool HasValidCrc(ReadOnlySpan<byte> frame)
            {
                lock (CrcLock)
                {
                    ushort value = 0;
                    for (int i = 0; i < frame.Length; i++)
                    {
                        value = CRC_check(frame[i], i == 0);
                    }
                    return value == 0;
                }
            }

            private static byte[] AddCrc(ReadOnlySpan<byte> bytes)
            {
                byte[] result = new byte[bytes.Length + 2];
                bytes.CopyTo(result);
                lock (CrcLock)
                {
                    ushort crc = 0;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        crc = CRC_check(bytes[i], i == 0);
                    }
                    result[^2] = ReverseBits((byte)(crc >> 8));
                    result[^1] = ReverseBits((byte)(crc & 0xFF));
                }
                return result;
            }

            private static MZQFileHeader DecodeHeader(ReadOnlySpan<byte> frame)
            {
                byte[] description = new byte[104];
                frame.Slice(30, 38).CopyTo(description);
                return new MZQFileHeader
                {
                    StartSign = ExpectedStartSign.ToArray(),
                    MzfHeaderSign = 0x00,
                    DataSize = HeaderDataLength,
                    MzfFtype = frame[4],
                    MzfFname = frame.Slice(5, 16).ToArray(),
                    MzfFnameEnd = frame[21],
                    Unused1 = frame.Slice(22, 2).ToArray(),
                    MzfSize = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(24, 2)),
                    MzfStart = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(26, 2)),
                    MzfExec = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(28, 2)),
                    MzfHeaderDescription = description,
                    Crc = ExpectedCrc.ToArray()
                };
            }

            private static MZQFileBody DecodeBody(ReadOnlySpan<byte> frame)
            {
                ushort length = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(2, 2));
                return new MZQFileBody
                {
                    StartSign = ExpectedStartSign.ToArray(),
                    MzfBodySign = 0x05,
                    DataSize = length,
                    MzfBody = frame.Slice(4, length).ToArray(),
                    Crc = ExpectedCrc.ToArray(),
                    TrailingData = Array.Empty<byte>()
                };
            }

            private static bool HasSharpSync(ReadOnlySpan<byte> bytes, int frameIndex)
            {
                int index = frameIndex - 1;
                int syncCount = 0;
                while (index >= 0 && bytes[index] == 0x16)
                {
                    syncCount++;
                    index--;
                }
                if (syncCount < 2)
                {
                    return false;
                }

                // A phase transition at the break/sync boundary can decode the first
                // sync-adjacent byte as 0x10/0x14. Require a nearby break byte while
                // allowing that single boundary artifact.
                int lowerBound = Math.Max(0, index - 16);
                for (; index >= lowerBound; index--)
                {
                    if (bytes[index] == 0x00)
                    {
                        return true;
                    }
                }
                return false;
            }

            private static void WriteFramed(Stream stream, byte[] frame, int trailingZeroCount)
            {
                stream.WriteByte(0x00);
                for (int i = 0; i < 10; i++) stream.WriteByte(0x16);
                stream.Write(frame);
                for (int i = 0; i < 7; i++) stream.WriteByte(0x16);
                for (int i = 0; i < trailingZeroCount; i++) stream.WriteByte(0x00);
            }

            private static void CopyExactOrPad(byte[]? source, Span<byte> destination)
            {
                destination.Clear();
                source?.AsSpan(0, Math.Min(source.Length, destination.Length)).CopyTo(destination);
            }
        }

    #endregion

    #region Sharp legacy logical QD

    internal static class SharpLegacyQdCodec
        {
            public const int ImageSize = 0xF00F;
            private const int LogicalHeaderSize = 8;
            private const int FormattingTerminatorSize = 4;
            private const int MinimumFormattingTailSize = 8;

            public static IReadOnlyList<TapeRecord> Read(ReadOnlySpan<byte> image)
            {
                if (image.Length < LogicalHeaderSize || QdFormatDetector.Detect(image) != QdImageFormat.SharpLegacyLogical)
                {
                    throw new InvalidDataException("Invalid Sharp/MZ legacy QuickDisk header.");
                }

                int blockCount = image[4];
                if ((blockCount & 1) != 0)
                {
                    throw new InvalidDataException("FNBLK/header/body inconsistency: legacy QD block count is odd.");
                }
                QuickDiskLimits.ValidateImportedCount(blockCount / 2);

                int position = LogicalHeaderSize;
                var records = new List<TapeRecord>(blockCount / 2);
                for (int index = 0; index < blockCount / 2; index++)
                {
                    MZQFileHeader header = ReadHeader(image, ref position);
                    MZQFileBody body = ReadBody(image, ref position);
                    if (header.MzfSize != body.DataSize)
                    {
                        throw new InvalidDataException(
                            $"FNBLK/header/body inconsistency: header declares {header.MzfSize} bytes, body declares {body.DataSize} bytes.");
                    }
                    records.Add(TapeRecord.FromLegacy(header, body));
                }
                return records;
            }

            public static byte[] Write(IReadOnlyList<TapeRecord> records, bool allowImportedNonStandard = false)
            {
                if (!QuickDiskLimits.TryValidateForSave(records.Count, allowImportedNonStandard, out string countError))
                {
                    throw new InvalidDataException(countError);
                }

                int logicalLength = LogicalHeaderSize + records.Sum(record => 84 + record.Body.DataSize);
                if (logicalLength + MinimumFormattingTailSize > ImageSize)
                {
                    throw new InvalidDataException("Cannot save: QuickDisk capacity exceeded.");
                }

                byte[] image = new byte[ImageSize];
                ExpectedStartSign.CopyTo(image, 0);
                image[4] = checked((byte)(records.Count * 2));
                ExpectedCrc.CopyTo(image, 5);
                int position = LogicalHeaderSize;
                foreach (TapeRecord record in records)
                {
                    WriteHeader(image, ref position, record.Header);
                    WriteBody(image, ref position, record.Body);
                }

                image[position++] = 0x00;
                image[position++] = 0x16;
                image[position++] = 0x16;
                image[position++] = 0xA5;
                int patternEnd = ImageSize - FormattingTerminatorSize;
                bool write55 = true;
                while (position < patternEnd)
                {
                    image[position++] = write55 ? (byte)0x55 : (byte)0xAA;
                    write55 = !write55;
                }
                ExpectedCrc.CopyTo(image, position);
                image[^1] = 0x00;
                return image;
            }

            public static bool TryValidateCapacity(IReadOnlyList<TapeRecord> records, out string error)
            {
                int required = LogicalHeaderSize + records.Sum(record => 84 + record.Body.DataSize) + MinimumFormattingTailSize;
                if (required > ImageSize)
                {
                    error = $"Cannot save: QuickDisk capacity exceeded ({required} bytes required, {ImageSize} available).";
                    return false;
                }
                error = string.Empty;
                return true;
            }

            private static MZQFileHeader ReadHeader(ReadOnlySpan<byte> image, ref int position)
            {
                EnsureAvailable(image, position, 74, "legacy QD file header");
                if (!image.Slice(position, 4).SequenceEqual(ExpectedStartSign) ||
                    !image.Slice(position + 71, 3).SequenceEqual(ExpectedCrc))
                {
                    throw new InvalidDataException("Corrupt Sharp legacy QD file header marker.");
                }
                if (image[position + 4] != 0x00 || BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 5, 2)) != 64)
                {
                    throw new InvalidDataException("Corrupt Sharp legacy QD file header block.");
                }

                byte[] description = new byte[104];
                image.Slice(position + 33, 38).CopyTo(description);
                var header = new MZQFileHeader
                {
                    StartSign = ExpectedStartSign.ToArray(),
                    MzfHeaderSign = 0,
                    DataSize = 64,
                    MzfFtype = image[position + 7],
                    MzfFname = image.Slice(position + 8, 16).ToArray(),
                    MzfFnameEnd = image[position + 24],
                    Unused1 = image.Slice(position + 25, 2).ToArray(),
                    MzfSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 27, 2)),
                    MzfStart = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 29, 2)),
                    MzfExec = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 31, 2)),
                    MzfHeaderDescription = description,
                    Crc = ExpectedCrc.ToArray()
                };
                position += 74;
                return header;
            }

            private static MZQFileBody ReadBody(ReadOnlySpan<byte> image, ref int position)
            {
                EnsureAvailable(image, position, 10, "legacy QD file body header");
                if (!image.Slice(position, 4).SequenceEqual(ExpectedStartSign) || image[position + 4] != 0x05)
                {
                    throw new InvalidDataException("Corrupt Sharp legacy QD file body marker.");
                }
                ushort length = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 5, 2));
                EnsureAvailable(image, position, 10 + length, "legacy QD file body");
                if (!image.Slice(position + 7 + length, 3).SequenceEqual(ExpectedCrc))
                {
                    throw new InvalidDataException("Corrupt Sharp legacy QD file body CRC marker.");
                }
                var body = new MZQFileBody
                {
                    StartSign = ExpectedStartSign.ToArray(),
                    MzfBodySign = 0x05,
                    DataSize = length,
                    MzfBody = image.Slice(position + 7, length).ToArray(),
                    Crc = ExpectedCrc.ToArray(),
                    TrailingData = Array.Empty<byte>()
                };
                position += 10 + length;
                return body;
            }

            private static void WriteHeader(Span<byte> output, ref int position, MZQFileHeader header)
            {
                ExpectedStartSign.CopyTo(output.Slice(position, 4));
                output[position + 4] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 5, 2), 64);
                output[position + 7] = header.MzfFtype;
                Copy(header.MzfFname, output.Slice(position + 8, 16));
                output[position + 24] = header.MzfFnameEnd;
                Copy(header.Unused1, output.Slice(position + 25, 2));
                BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 27, 2), header.MzfSize);
                BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 29, 2), header.MzfStart);
                BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 31, 2), header.MzfExec);
                Copy(header.MzfHeaderDescription, output.Slice(position + 33, 38));
                ExpectedCrc.CopyTo(output.Slice(position + 71, 3));
                position += 74;
            }

            private static void WriteBody(Span<byte> output, ref int position, MZQFileBody body)
            {
                if (body.MzfBody is null || body.MzfBody.Length < body.DataSize)
                {
                    throw new InvalidDataException("QuickDisk body is shorter than its declared size.");
                }
                ExpectedStartSign.CopyTo(output.Slice(position, 4));
                output[position + 4] = 0x05;
                BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 5, 2), body.DataSize);
                body.MzfBody.AsSpan(0, body.DataSize).CopyTo(output.Slice(position + 7));
                ExpectedCrc.CopyTo(output.Slice(position + 7 + body.DataSize, 3));
                position += 10 + body.DataSize;
            }

            private static void Copy(byte[]? source, Span<byte> destination)
            {
                destination.Clear();
                source?.AsSpan(0, Math.Min(source.Length, destination.Length)).CopyTo(destination);
            }

            private static void EnsureAvailable(ReadOnlySpan<byte> image, int position, int count, string field)
            {
                if (position < 0 || count < 0 || position > image.Length - count)
                {
                    throw new EndOfStreamException($"Unexpected end of file while reading {field}.");
                }
            }
        }

    #endregion

    #region MZQ logical format

    internal class MZQFileReader
        {
            public long currentPosition = 0;
            public long totalLength = 0;
            public long bytesRemaining = 0;

            private static bool ValidateStartSign(byte[] startSign)
            {
                return startSign.SequenceEqual(ExpectedStartSign);
            }

            private static bool ValidateCrc(byte[] crc)
            {
                return crc.SequenceEqual(ExpectedCrc);
            }

            private static bool ValidateQDiskHeader(MZQHeader header)
            {
                return ValidateStartSign(header.StartSign) && ValidateCrc(header.Crc);
            }

            private static bool ValidateQDiskMzfHeader(MZQFileHeader header)
            {
                return ValidateStartSign(header.StartSign) && ValidateCrc(header.Crc);
            }

            private static bool ValidateQDiskMzfBody(MZQFileBody body)
            {
                return ValidateStartSign(body.StartSign) && ValidateCrc(body.Crc);
            }

            public MZQHeader ReadMZQHeader(BinaryReader reader)
            {
                MZQHeader header = new MZQHeader();

                header.StartSign = ReadBytesExact(reader, 4, "MZQ start signature");
                header.FileBlocksCount = reader.ReadByte();
                header.Crc = ReadBytesExact(reader, 3, "MZQ header CRC marker");

                return header;
            }

            public (MZQFileHeader, MZQFileBody) ReadMzfBlock(BinaryReader reader)
            {

                MZQFileHeader header = new MZQFileHeader();

                header.StartSign = ReadBytesExact(reader, 4, "MZQ file header start signature");
                header.MzfHeaderSign = reader.ReadByte();
                header.DataSize = reader.ReadUInt16();
                header.MzfFtype = reader.ReadByte();
                header.MzfFname = ReadBytesExact(reader, 16, "MZF file name");
                header.MzfFnameEnd = reader.ReadByte();
                header.Unused1 = ReadBytesExact(reader, 2, "MZQ unused header bytes");
                header.MzfSize = reader.ReadUInt16();
                header.MzfStart = reader.ReadUInt16();
                header.MzfExec = reader.ReadUInt16();

                // Načtení prvních 38 bajtů pro MzfHeaderDescription
                header.MzfHeaderDescription = new byte[104]; // Inicializace pole 104 bajty
                byte[] descriptionBytes = ReadBytesExact(reader, 38, "MZF header description");
                Array.Copy(descriptionBytes, header.MzfHeaderDescription, descriptionBytes.Length);

                header.Crc = ReadBytesExact(reader, 3, "MZQ file header CRC marker");

                if (!ValidateQDiskMzfHeader(header))
                {
                    throw new InvalidOperationException("Neplatný MZF Header");
                }

                // Načtení začátku MZF Těla pro získání DataSize
                byte[] mzfBodyStartBytes = ReadBytesExact(reader, 7, "MZQ file body header");
                ushort bodyDataSize = BitConverter.ToUInt16(mzfBodyStartBytes, 5);

                // Načtení zbytku MZF Těla
                MZQFileBody body = new MZQFileBody
                {
                    StartSign = mzfBodyStartBytes.Take(4).ToArray(),
                    MzfBodySign = mzfBodyStartBytes[4],
                    DataSize = bodyDataSize,
                    MzfBody = ReadBytesExact(reader, bodyDataSize, "MZQ file body"),
                    Crc = ReadBytesExact(reader, 3, "MZQ file body CRC marker"),
                    TrailingData = Array.Empty<byte>()
                };

                if (!ValidateQDiskMzfBody(body))
                {
                    throw new InvalidOperationException("Neplatný MZF Header");
                }

                if (header.MzfSize != body.DataSize)
                {
                    throw new InvalidDataException(
                        $"MZF size mismatch: header declares {header.MzfSize} bytes, body declares {body.DataSize} bytes.");
                }

                return (header, body);
            }

            public List<(MZQFileHeader, MZQFileBody)> ReadFile(string filePath)
            {
                List<(MZQFileHeader, MZQFileBody)> mzfBlocks = new List<(MZQFileHeader, MZQFileBody)>();

                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    // Read the file header
                    MZQHeader header = ReadMZQHeader(reader);

                    if (!ValidateQDiskHeader(header))
                    {
                        throw new InvalidOperationException("Neplatný MZQHeader");
                    }

                    if (header.FileBlocksCount % 2 != 0)
                    {
                        throw new InvalidDataException("Invalid MZQ block count: header and body blocks must form pairs.");
                    }
                    QuickDiskLimits.ValidateImportedCount(header.FileBlocksCount / 2);

                    // Read each MZF block
                    int fileCount = header.FileBlocksCount / 2;
                    for (int i = 0; i < fileCount; i++)
                    {
                        var mzfBlock = ReadMzfBlock(reader);
                        mzfBlocks.Add(mzfBlock);
                    }

                    currentPosition = fs.Position;
                    totalLength = fs.Length;
                    bytesRemaining = totalLength - currentPosition;
                }

                return mzfBlocks;
            }
            public void WriteMZQHeaderToFile(FileStream fileStream, byte fbCount, bool allowImportedNonStandard = false)
            {
                if ((fbCount & 1) != 0)
                {
                    throw new InvalidDataException("QuickDisk block count must be even.");
                }
                if (!QuickDiskLimits.TryValidateForSave(fbCount / 2, allowImportedNonStandard, out string error))
                {
                    throw new InvalidDataException(error);
                }
                MZQHeader header;

                header.StartSign = new byte[] { 0x00, 0x16, 0x16, 0xa5 };
                fileStream.Write(header.StartSign, 0, header.StartSign.Length);

                header.FileBlocksCount = fbCount;
                fileStream.WriteByte(header.FileBlocksCount);

                header.Crc = Encoding.ASCII.GetBytes("CRC");
                fileStream.Write(header.Crc, 0, header.Crc.Length);
            }

            public void WriteMZQFileHeaderToFile(FileStream fileStream, MZQFileHeader mzfHeader)
            {
                fileStream.Write(mzfHeader.StartSign, 0, mzfHeader.StartSign.Length);

                fileStream.WriteByte(mzfHeader.MzfHeaderSign);

                var dataSizeBytes = BitConverter.GetBytes(mzfHeader.DataSize);
                fileStream.Write(dataSizeBytes, 0, dataSizeBytes.Length);

                fileStream.WriteByte(mzfHeader.MzfFtype);
                fileStream.Write(mzfHeader.MzfFname, 0, mzfHeader.MzfFname.Length);
                fileStream.WriteByte(mzfHeader.MzfFnameEnd);
                fileStream.Write(mzfHeader.Unused1, 0, mzfHeader.Unused1.Length);

                var mzfSizeBytes = BitConverter.GetBytes(mzfHeader.MzfSize);
                fileStream.Write(mzfSizeBytes, 0, mzfSizeBytes.Length);

                var mzfStartBytes = BitConverter.GetBytes(mzfHeader.MzfStart);
                fileStream.Write(mzfStartBytes, 0, mzfStartBytes.Length);

                var mzfExecBytes = BitConverter.GetBytes(mzfHeader.MzfExec);
                fileStream.Write(mzfExecBytes, 0, mzfExecBytes.Length);

                fileStream.Write(mzfHeader.MzfHeaderDescription, 0, 38); // mzfHeader.MzfHeaderDescription.Length

                fileStream.Write(mzfHeader.Crc, 0, mzfHeader.Crc.Length);
            }

            public void WriteMZQFileBodyToFile(FileStream fileStream, MZQFileBody mzfBody)
            {
                fileStream.Write(mzfBody.StartSign, 0, mzfBody.StartSign.Length);
                fileStream.WriteByte(mzfBody.MzfBodySign);

                var dataSizeBytes = BitConverter.GetBytes(mzfBody.DataSize);
                fileStream.Write(dataSizeBytes, 0, dataSizeBytes.Length);

                fileStream.Write(mzfBody.MzfBody, 0, mzfBody.DataSize);

                fileStream.Write(mzfBody.Crc, 0, mzfBody.Crc.Length);
            }
        }

    #endregion

    #region QDF logical/byte-stream format

    class QDFFileReader
    {
        public const long ImageSize = 81936;
        public const long HeaderSize = 7655;
        public const long FileOverhead = 620;

            public static byte[] BuildImage(
                IReadOnlyList<TapeRecord> records,
                bool allowImportedNonStandard = false)
            {
                if (!QuickDiskLimits.TryValidateForSave(records.Count, allowImportedNonStandard, out string countError))
                {
                    throw new InvalidDataException(countError);
                }

                long requiredSize = HeaderSize + records.Sum(record => FileOverhead + record.Body.DataSize);
                if (requiredSize > ImageSize)
                {
                    throw new InvalidDataException(
                        $"Cannot save: QDF capacity exceeded ({requiredSize} bytes required, {ImageSize} available).");
                }

                using var stream = new MemoryStream(checked((int)ImageSize));
                var writer = new QDFFileReader();
                writer.WriteQDFHeaderToFile(stream, checked((byte)(records.Count * 2)), allowImportedNonStandard);
                foreach (TapeRecord record in records)
                {
                    writer.WriteQDFFileHeaderToFile(stream, record.Header);
                    writer.WriteQDFFileBodyToFile(stream, record.Body);
                }
                writer.WriteBytesToStream(stream, 0x00, ImageSize - stream.Length);
                return stream.ToArray();
            }

            public long currentPosition = 0;
            public long totalLength = 0;
            public long bytesRemaining = 0;
            public List<long> positions = new List<long>();

            private static bool ValidateStartSign(byte[] startSign)
            {
                return startSign.SequenceEqual(ExpectedStartSign);
            }

            private static bool ValidateCrc(byte[] crc)
            {
                return crc.SequenceEqual(ExpectedCrc);
            }

            private static bool ValidateQDFHeader(MZQHeader header)
            {
                return ValidateStartSign(header.StartSign) && ValidateCrc(header.Crc);
            }

            private static bool ValidateQDiskMzfHeader(MZQFileHeader header)
            {
                return ValidateStartSign(header.StartSign) && ValidateCrc(header.Crc);
            }

            private static bool ValidateQDiskMzfBody(MZQFileBody body)
            {
                return ValidateStartSign(body.StartSign) && ValidateCrc(body.Crc);
            }

            public bool IsQDFHeader(BinaryReader reader)
            {
                byte[] expectedHeaderBytes = Encoding.ASCII.GetBytes("-QD format-")
                    .Concat(Enumerable.Repeat((byte)0xFF, 5)).ToArray();
                byte[] headerBytes = reader.ReadBytes(expectedHeaderBytes.Length);

                return headerBytes.SequenceEqual(expectedHeaderBytes);
            }

            public bool FindStartSequence(BinaryReader reader)
            {
                bool found00 = false; // Indikátor nalezení 0x00
                int countOf16 = 0;    // Počet postupných 0x16 bytů

                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    byte currentByte = reader.ReadByte();
                    long currentPosition = reader.BaseStream.Position;

                    if (currentPosition == 0x12E8)
                        currentPosition++;

                    if (currentByte == 0x00)
                    {
                        found00 = true; // Nalezen 0x00, čekáme na 0x16
                        countOf16 = 0;
                    }
                    else // found00 je true
                    {
                        if (found00 && currentByte == 0x16)
                        {
                            countOf16++; // Počítání 0x16 bytů
                        }
                        else if (found00 && currentByte == 0xA5 && countOf16 >= 2)
                        {
                            return true; // Úspěšně nalezená sekvence
                        }
                        else
                        {
                            found00 = false; // Reset, pokud byte není 0x16 nebo 0xA5
                        }
                    }
                }

                return false;
            }


            public MZQHeader ReadQDFHeader(BinaryReader reader)
            {
                MZQHeader header = new MZQHeader();
                header.StartSign = ZeroStartSign.ToArray();
                header.FileBlocksCount = 0;
                header.Crc = ZeroCrc.ToArray();

                if (IsQDFHeader(reader))
                {
                    if (FindStartSequence(reader))
                    {
                        positions.Add(reader.BaseStream.Position);
                        positions.Add(3);
                        header.StartSign = ExpectedStartSign.ToArray();
                        CRC_check(0xA5, true);
                        header.FileBlocksCount = reader.ReadByte();
                        ushort crc = CRC_check(header.FileBlocksCount);
                        ushort readCRC = reader.ReadUInt16();
                        byte crcHi = ReverseBits((byte)(crc & 0xFF));  // reverse bits and swap hi-lo bytes too
                        byte crcLo = ReverseBits((byte)(crc >> 8));
                        ushort calculatedCRC = (ushort)(crcLo + (crcHi << 8));
                        //CRC_check((byte)(readCRC & 0xFF));
                        //CRC_check((byte)(readCRC >> 8));
                        crc = CRC_check(readCRC);
                        header.Crc = ExpectedCrc.ToArray();
                        if (readCRC != calculatedCRC || crc != 0)
                        {
                            header.StartSign = ZeroStartSign.ToArray();
                            header.FileBlocksCount = 0;
                            header.Crc = ZeroCrc.ToArray();
                        }
                    }
                }

                return header;
            }

            public (MZQFileHeader, MZQFileBody) ReadQDFMzfBlock(BinaryReader reader)
            {

                MZQFileHeader header = new MZQFileHeader();
                MZQFileBody body = new MZQFileBody();

                if (FindStartSequence(reader))
                {
                    positions.Add(reader.BaseStream.Position);
                    positions.Add(64 + 5);
                    // Načtení jednotlivých členů struktury
                    header.StartSign = ExpectedStartSign.ToArray();
                    CRC_check(0xA5, true);
                    header.MzfHeaderSign = reader.ReadByte();
                    CRC_check(header.MzfHeaderSign);
                    header.DataSize = reader.ReadUInt16();
                    CRC_check(header.DataSize);
                    header.MzfFtype = reader.ReadByte();
                    CRC_check(header.MzfFtype);
                    header.MzfFname = ReadBytesExact(reader, 16, "QDF MZF file name");
                    for (int i = 0; i < 16; i++)
                    {
                        CRC_check(header.MzfFname[i]);
                    }
                    header.MzfFnameEnd = reader.ReadByte();
                    CRC_check(header.MzfFnameEnd);
                    header.Unused1 = ReadBytesExact(reader, 2, "QDF unused header bytes");
                    CRC_check(header.Unused1[0]);
                    CRC_check(header.Unused1[1]);
                    header.MzfSize = reader.ReadUInt16();
                    CRC_check(header.MzfSize);
                    header.MzfStart = reader.ReadUInt16();
                    CRC_check(header.MzfStart);
                    header.MzfExec = reader.ReadUInt16();
                    CRC_check(header.MzfExec);

                    // Načtení prvních 38 bajtů pro MzfHeaderDescription
                    header.MzfHeaderDescription = new byte[104]; // Inicializace pole 104 bajty
                    byte[] descriptionBytes = ReadBytesExact(reader, 38, "QDF MZF header description");
                    Array.Copy(descriptionBytes, header.MzfHeaderDescription, descriptionBytes.Length);
                    for (int i = 0; i < 38; i++)
                    {
                        CRC_check(header.MzfHeaderDescription[i]);
                    }

                    // Načtení CRC
                    header.Crc = ExpectedCrc.ToArray();
                    ushort readCRC = reader.ReadUInt16();
                    ushort crc = CRC_check(readCRC);

                    if (crc != 0)
                    {
                        header.Crc = ZeroCrc;
                        throw new InvalidOperationException("Invalid CRC in QDF MZF Header");
                    }

                    if (!ValidateQDiskMzfHeader(header))
                    {
                        throw new InvalidOperationException("Neplatný MZF Header");
                    }

                    if (FindStartSequence(reader))
                    {
                        // Načtení začátku MZF Těla pro získání DataSize
                        // byte[] mzfBodyStartBytes = reader.ReadBytes(7); // Předpokládáme, že prvních 7 bajtů obsahuje StartSign (4 bajty), MzfBodySign (1 bajt) a DataSize (2 bajty)
                        // ushort bodyDataSize = // BitConverter.ToUInt16(mzfBodyStartBytes, 5);

                        positions.Add(reader.BaseStream.Position);
                        body.StartSign = ExpectedStartSign.ToArray();
                        CRC_check(0xA5, true);
                        body.MzfBodySign = reader.ReadByte();
                        CRC_check(body.MzfBodySign);
                        body.DataSize = reader.ReadUInt16();
                        positions.Add(body.DataSize + 5);
                        CRC_check(body.DataSize);
                        body.MzfBody = ReadBytesExact(reader, body.DataSize, "QDF MZF file body");
                        body.TrailingData = Array.Empty<byte>();
                        for (int i = 0; i < body.DataSize; i++)
                        {
                            CRC_check(body.MzfBody[i]);
                        }
                        body.Crc = ExpectedCrc.ToArray();
                        readCRC = reader.ReadUInt16();
                        crc = CRC_check(readCRC);

                        if (crc != 0)
                        {
                            body.Crc = ZeroCrc;
                            throw new InvalidOperationException("Invalid CRC in QDF MZF Body");
                        }

                        if (!ValidateQDiskMzfBody(body))
                        {
                            throw new InvalidOperationException("Invlid QD/MZF Header");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Missing data block after header block.");
                    }

                    if (header.MzfSize != body.DataSize)
                    {
                        throw new InvalidDataException(
                            $"MZF size mismatch: header declares {header.MzfSize} bytes, body declares {body.DataSize} bytes.");
                    }
                }
                else
                {
                    throw new InvalidDataException("Missing QDF MZF header block.");
                }
                return (header, body);
            }

            public List<(MZQFileHeader, MZQFileBody)> ReadFile(string filePath)
            {
                byte[] image = File.ReadAllBytes(filePath);
                byte[] expectedHeaderBytes = Encoding.ASCII.GetBytes("-QD format-")
                    .Concat(Enumerable.Repeat((byte)0xFF, 5)).ToArray();
                if (image.Length < expectedHeaderBytes.Length ||
                    !image.AsSpan(0, expectedHeaderBytes.Length).SequenceEqual(expectedHeaderBytes))
                {
                    throw new InvalidDataException("Invalid QDF container header.");
                }

                List<(MZQFileHeader, MZQFileBody)> mzfBlocks = SharpQdFrameCodec.DecodeByteStream(image);
                QuickDiskLimits.ValidateImportedCount(mzfBlocks.Count);
                currentPosition = image.Length;
                totalLength = image.Length;
                bytesRemaining = 0;
                return mzfBlocks;
            }

            public void WriteBytesToStream(Stream fileStream, byte val, long repeat)
            {
                for (long i = 0; i < repeat; i++)
                {
                    fileStream.WriteByte(val);
                }
            }

            public void WriteQDFHeaderToFile(Stream fileStream, byte fbCount, bool allowImportedNonStandard = false)
            {
                if ((fbCount & 1) != 0)
                {
                    throw new InvalidDataException("QuickDisk block count must be even.");
                }
                if (!QuickDiskLimits.TryValidateForSave(fbCount / 2, allowImportedNonStandard, out string error))
                {
                    throw new InvalidDataException(error);
                }
                byte[] QDFFileSignature = Encoding.ASCII.GetBytes("-QD format-")
                    .Concat(Enumerable.Repeat((byte)0xFF, 5)).ToArray();
                fileStream.Write(QDFFileSignature, 0, QDFFileSignature.Length);

                WriteBytesToStream(fileStream, 0x00, 0x12EA - 16);
                WriteBytesToStream(fileStream, 0x16, 9);
                fileStream.Write(SharpQdFrameCodec.EncodeCountFrame(fbCount));

                WriteBytesToStream(fileStream, 0x16, 6);
                WriteBytesToStream(fileStream, 0x00, 2794);

                //header.Crc = Encoding.ASCII.GetBytes("CRC");
                //fileStream.Write(header.Crc, 0, header.Crc.Length);
            }

            public void WriteQDFFileHeaderToFile(Stream fileStream, MZQFileHeader mzfHeader)
            {
                WriteBytesToStream(fileStream, 0x16, 10);
                fileStream.Write(SharpQdFrameCodec.EncodeHeaderFrame(mzfHeader));

                WriteBytesToStream(fileStream, 0x16, 7);
                WriteBytesToStream(fileStream, 0x00, 254);
            }

            public void WriteQDFFileBodyToFile(Stream fileStream, MZQFileBody mzfBody)
            {
                WriteBytesToStream(fileStream, 0x16, 10);
                fileStream.Write(SharpQdFrameCodec.EncodeBodyFrame(mzfBody));

                WriteBytesToStream(fileStream, 0x16, 7);
                WriteBytesToStream(fileStream, 0x00, 256);
            }
        }

    #endregion

    #region MFM raw-bitcell codec

    internal static class QuickDiskMfmCodec
        {
            public static byte[] Encode(ReadOnlySpan<byte> data, bool previousDataBit = false)
            {
                byte[] encoded = new byte[data.Length * 2];
                int outputBit = 0;
                bool previous = previousDataBit;
                foreach (byte value in data)
                {
                    for (int bit = 0; bit < 8; bit++)
                    {
                        bool current = ((value >> bit) & 1) != 0;
                        bool clock = !previous && !current;
                        SetLsbFirstBit(encoded, outputBit++, clock);
                        SetLsbFirstBit(encoded, outputBit++, current);
                        previous = current;
                    }
                }
                return encoded;
            }

            public static byte[] Decode(ReadOnlySpan<byte> track, int dataCellPhase)
            {
                if (dataCellPhase is < 0 or > 15)
                {
                    throw new ArgumentOutOfRangeException(nameof(dataCellPhase));
                }

                int bitCount = track.Length * 8;
                int byteCount = Math.Max(0, (bitCount - dataCellPhase + 1) / 16);
                byte[] decoded = new byte[byteCount];
                for (int index = 0; index < byteCount; index++)
                {
                    byte value = 0;
                    int firstCell = dataCellPhase + index * 16;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if (GetLsbFirstBit(track, firstCell + bit * 2))
                        {
                            value |= (byte)(1 << bit);
                        }
                    }
                    decoded[index] = value;
                }
                return decoded;
            }

            public static IReadOnlyList<SharpQdFrame> FindSharpFrames(ReadOnlySpan<byte> track)
            {
                var result = new List<SharpQdFrame>();
                for (int phase = 0; phase < 16; phase++)
                {
                    byte[] decoded = Decode(track, phase);
                    result.AddRange(SharpQdFrameCodec.FindFrames(decoded, phase));
                }
                return result;
            }

            public static bool ContainsPlausibleSharpFrame(ReadOnlySpan<byte> track)
            {
                for (int phase = 0; phase < 16; phase++)
                {
                    if (SharpQdFrameCodec.ContainsPlausibleFrame(Decode(track, phase)))
                    {
                        return true;
                    }
                }
                return false;
            }

            private static bool GetLsbFirstBit(ReadOnlySpan<byte> bytes, int position) =>
                (bytes[position >> 3] & (1 << (position & 7))) != 0;

            private static void SetLsbFirstBit(Span<byte> bytes, int position, bool value)
            {
                if (value)
                {
                    bytes[position >> 3] |= (byte)(1 << (position & 7));
                }
            }
        }

    #endregion

    #region Physical image profiles

    internal sealed record QuickDiskPhysicalProfile(
            QdImageFormat Format,
            int TrackLength,
            int StoredTrackLength,
            int WindowStart,
            int WindowEnd,
            byte BlankFiller,
            uint BitRate,
            int ContainerFileSize = 0x32000,
            int DescriptorOffset = 0x200,
            int DataOffset = 0x400)
        {
            public const int TrackListOffset = 0x200;
            public const int TrackDataOffset = 0x400;
            public const int FileSize = 0x32000;

            public static QuickDiskPhysicalProfile Hxc { get; } = new(
                QdImageFormat.HxcPhysical, 0x31C00, 0x31C00, 0x3200, 0x25600, 0x01, 203388);

            public static QuickDiskPhysicalProfile FlashFloppy { get; } = new(
                QdImageFormat.FlashFloppyPhysical, 0x31A99, 0x31C00, 0x31A9, 0x2B745, 0x11, 0);

            public static QuickDiskPhysicalProfile For(QdImageFormat format) => format switch
            {
                QdImageFormat.HxcPhysical => Hxc,
                QdImageFormat.FlashFloppyPhysical => FlashFloppy,
                _ => throw new System.ArgumentOutOfRangeException(nameof(format), "A physical QD output format is required.")
            };
        }

    #endregion

    #region HxC and FlashFloppy containers

    internal sealed record QuickDiskTrackDescriptor(uint Offset, uint Length, uint WindowStart, uint WindowEnd);

        internal sealed record QuickDiskContainer(
            QdImageFormat Format,
            QuickDiskTrackDescriptor Descriptor,
            byte[] Track,
            uint BitRate,
            QuickDiskPhysicalProfile Profile);

        internal static class HxcFlashFloppyQdContainer
        {
            private const int DescriptorLength = 16;

            public static QuickDiskContainer Parse(ReadOnlySpan<byte> image, QdImageFormat format)
            {
                if (format is not (QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical))
                {
                    throw new ArgumentException("A physical HxC or FlashFloppy QD format is required.", nameof(format));
                }

                uint descriptorOffset;
                uint bitRate = 0;
                if (format == QdImageFormat.HxcPhysical)
                {
                    if (image.Length < 40 || !image[..8].SequenceEqual("HXCQDDRV"u8))
                    {
                        throw new InvalidDataException("Invalid HxC QuickDisk container header.");
                    }

                    uint tracks = ReadUInt32(image, 12);
                    uint sides = ReadUInt32(image, 16);
                    if (tracks != 1 || sides != 1)
                    {
                        throw new NotSupportedException($"Unsupported QuickDisk geometry: {tracks} track(s), {sides} side(s); Sharp MZ requires 1x1.");
                    }
                    uint trackEncoding = ReadUInt32(image, 20);
                    if (trackEncoding != 0)
                    {
                        throw new NotSupportedException($"Unsupported QuickDisk track encoding: {trackEncoding}.");
                    }
                    bitRate = ReadUInt32(image, 28);
                    descriptorOffset = ReadUInt32(image, 36);
                    ulong tableSize = (ulong)tracks * sides * DescriptorLength;
                    if (descriptorOffset > image.Length || (ulong)descriptorOffset + tableSize > (ulong)image.Length)
                    {
                        throw new InvalidDataException("Invalid HxC track descriptor table offset.");
                    }
                }
                else
                {
                    if (image.Length < 5 || image[3] != (byte)'Q' || image[4] != (byte)'D')
                    {
                        throw new InvalidDataException("Invalid FlashFloppy QuickDisk container header.");
                    }
                    descriptorOffset = QuickDiskPhysicalProfile.TrackListOffset;
                    if ((ulong)descriptorOffset + DescriptorLength > (ulong)image.Length)
                    {
                        throw new InvalidDataException("Invalid FlashFloppy track descriptor table offset.");
                    }
                }

                int descriptorIndex = checked((int)descriptorOffset);
                var descriptor = new QuickDiskTrackDescriptor(
                    ReadUInt32(image, descriptorIndex),
                    ReadUInt32(image, descriptorIndex + 4),
                    ReadUInt32(image, descriptorIndex + 8),
                    ReadUInt32(image, descriptorIndex + 12));
                ValidateDescriptor(descriptor, image.Length);

                int dataOffset = checked((int)descriptor.Offset);
                int storedTrackLength = image.Length - dataOffset;
                byte[] track = image.Slice(dataOffset, checked((int)descriptor.Length)).ToArray();
                byte filler = QuickDiskPhysicalProfile.For(format).BlankFiller;
                var profile = new QuickDiskPhysicalProfile(
                    format,
                    checked((int)descriptor.Length),
                    storedTrackLength,
                    checked((int)descriptor.WindowStart),
                    checked((int)descriptor.WindowEnd),
                    filler,
                    bitRate,
                    image.Length,
                    checked((int)descriptorOffset),
                    dataOffset);
                return new QuickDiskContainer(format, descriptor, track, bitRate, profile);
            }

            public static byte[] Write(ReadOnlySpan<byte> track, QuickDiskPhysicalProfile profile)
            {
                if (track.Length != profile.StoredTrackLength)
                {
                    throw new ArgumentException("Physical track storage length does not match its QD profile.", nameof(track));
                }
                if (profile.ContainerFileSize <= 0 || profile.DescriptorOffset < 0 ||
                    profile.DescriptorOffset > profile.ContainerFileSize - DescriptorLength ||
                    profile.DataOffset < 0 || profile.DataOffset > profile.ContainerFileSize - track.Length)
                {
                    throw new InvalidDataException("The preserved QuickDisk physical profile is outside the container bounds.");
                }

                byte[] image = new byte[profile.ContainerFileSize];
                if (profile.Format == QdImageFormat.HxcPhysical)
                {
                    "HXCQDDRV"u8.CopyTo(image);
                    WriteUInt32(image, 8, 0);
                    WriteUInt32(image, 12, 1);
                    WriteUInt32(image, 16, 1);
                    WriteUInt32(image, 20, 0);
                    WriteUInt32(image, 24, 0);
                    WriteUInt32(image, 28, profile.BitRate);
                    WriteUInt32(image, 32, 0);
                    WriteUInt32(image, 36, checked((uint)profile.DescriptorOffset));
                }
                else if (profile.Format == QdImageFormat.FlashFloppyPhysical)
                {
                    image[3] = (byte)'Q';
                    image[4] = (byte)'D';
                }
                else
                {
                    throw new ArgumentException("A physical QD output profile is required.", nameof(profile));
                }

                int descriptor = profile.DescriptorOffset;
                WriteUInt32(image, descriptor, checked((uint)profile.DataOffset));
                WriteUInt32(image, descriptor + 4, checked((uint)profile.TrackLength));
                WriteUInt32(image, descriptor + 8, checked((uint)profile.WindowStart));
                WriteUInt32(image, descriptor + 12, checked((uint)profile.WindowEnd));
                track.CopyTo(image.AsSpan(profile.DataOffset));
                return image;
            }

            private static void ValidateDescriptor(QuickDiskTrackDescriptor descriptor, int imageLength)
            {
                if (descriptor.Offset < QuickDiskPhysicalProfile.TrackDataOffset)
                {
                    throw new InvalidDataException("Invalid track descriptor: track data offset must be at least 0x400.");
                }
                if (descriptor.Length == 0)
                {
                    throw new InvalidDataException("Invalid track descriptor: track length is zero.");
                }
                if ((ulong)descriptor.Offset + descriptor.Length > (ulong)imageLength)
                {
                    throw new InvalidDataException("Invalid track descriptor: track data extends beyond the file.");
                }
                if (descriptor.WindowStart > descriptor.WindowEnd || descriptor.WindowEnd > descriptor.Length)
                {
                    throw new InvalidDataException("Invalid track descriptor: the QuickDisk data window is outside the track.");
                }
            }

            private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

            private static void WriteUInt32(Span<byte> bytes, int offset, uint value) =>
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), value);
        }

    #endregion

    #region Physical reader/writer and high-level API

    internal static class QuickDiskPhysicalReader
        {
            public static IReadOnlyList<TapeRecord> Read(QuickDiskContainer container)
            {
                IReadOnlyList<SharpQdFrame> frames = QuickDiskMfmCodec.FindSharpFrames(container.Track);
                if (frames.Count == 0)
                {
                    if (IsBlankTrack(container.Track))
                    {
                        return Array.Empty<TapeRecord>();
                    }
                    if (QuickDiskMfmCodec.ContainsPlausibleSharpFrame(container.Track))
                    {
                        throw new InvalidDataException("CRC error or corrupt Sharp QD frame.");
                    }
                    throw new InvalidDataException(
                        "The QD container is valid, but it does not contain a supported Sharp MZ QuickDisk image.");
                }

                return SharpQdFrameCodec.DecodeRecords(frames)
                    .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2))
                    .ToList();
            }

            internal static bool IsBlankTrack(ReadOnlySpan<byte> track)
            {
                if (track.IsEmpty) return false;
                Span<int> histogram = stackalloc int[256];
                foreach (byte value in track) histogram[value]++;
                int dominant = 0;
                for (int i = 0; i < histogram.Length; i++) dominant = Math.Max(dominant, histogram[i]);
                return dominant >= track.Length * 98L / 100L;
            }
        }

        internal static class HxcCanonicalTrackBuilder
        {
            internal const int MarioFnblkReadyOffsetBits = 22;
            internal const int GlobalRawBitShift = 5;
            internal const int CountPreSyncBytes = 9;
            internal const int CountPostSyncBytes = 6;
            internal const int CountToHeaderZeroGapBytes = 2736;
            internal const int BlockPreSyncBytes = 10;
            internal const int BlockPostSyncBytes = 7;
            internal const int HeaderToBodyZeroGapBytes = 254;
            internal const int BodyToNextHeaderZeroGapBytes = 256;

            // HxC output intentionally does not reuse FlashFloppy READY timing. This
            // layout is derived from an HXCQDDRV image confirmed by the HxC author as
            // working on MZ-1500 (the Mario reference). Its count/FNBLK starts only
            // 22 bitcells after window_start and the complete track is a continuous MFM
            // carrier, not a compact frame stream inserted into 0x01 blank filler.
            public static byte[] BuildTrack(
                IReadOnlyList<TapeRecord> records,
                QuickDiskPhysicalProfile profile)
            {
                if (!TryGetLayout(records, profile, out int logicalStart, out int logicalTrackLength, out string error))
                {
                    throw new InvalidDataException(error);
                }

                byte[] logicalTrack = new byte[logicalTrackLength];
                byte[] meaningfulLayout = BuildMeaningfulLayout(records);
                meaningfulLayout.CopyTo(logicalTrack, logicalStart);

                byte[] encoded = QuickDiskMfmCodec.Encode(logicalTrack);
                byte[] track = Enumerable.Repeat((byte)0xAA, profile.StoredTrackLength).ToArray();
                int bitCount = Math.Min(encoded.Length * 8, track.Length * 8 - GlobalRawBitShift);
                CopyBitsLsbFirst(encoded, 0, track, GlobalRawBitShift, bitCount);
                return track;
            }

            public static bool TryValidateCapacity(
                IReadOnlyList<TapeRecord> records,
                QuickDiskPhysicalProfile profile,
                out string error) =>
                TryGetLayout(records, profile, out _, out _, out error);

            private static bool TryGetLayout(
                IReadOnlyList<TapeRecord> records,
                QuickDiskPhysicalProfile profile,
                out int logicalStart,
                out int logicalTrackLength,
                out string error)
            {
                logicalStart = 0;
                logicalTrackLength = 0;
                long trackBits = (long)profile.StoredTrackLength * 8;
                long countDataCellBit = (long)profile.WindowStart * 8 + MarioFnblkReadyOffsetBits;
                long countLogicalBit = countDataCellBit - GlobalRawBitShift - 1;
                if (trackBits <= GlobalRawBitShift ||
                    countLogicalBit < 0 ||
                    countLogicalBit % 16 != 0)
                {
                    error = "Cannot save: the HxC profile cannot represent the canonical Mario FNBLK phase.";
                    return false;
                }

                long countLogicalIndex = countLogicalBit / 16;
                long start = countLogicalIndex - CountPreSyncBytes;
                long trackLogicalBytes = (trackBits - GlobalRawBitShift + 15) / 16;
                long meaningfulBytes = GetMeaningfulLayoutLength(records);
                long meaningfulEndBit = GlobalRawBitShift + (start + meaningfulBytes) * 16;
                long dataLimitBit = Math.Min((long)profile.WindowEnd, profile.StoredTrackLength) * 8;
                if (start < 0 ||
                    start > int.MaxValue ||
                    trackLogicalBytes > int.MaxValue ||
                    start + meaningfulBytes > trackLogicalBytes ||
                    meaningfulEndBit > dataLimitBit)
                {
                    error = $"Cannot save: HxC canonical track capacity exceeded ({meaningfulEndBit} meaningful bits required, {dataLimitBit} available).";
                    return false;
                }

                logicalStart = (int)start;
                logicalTrackLength = (int)trackLogicalBytes;
                error = string.Empty;
                return true;
            }

            private static long GetMeaningfulLayoutLength(IReadOnlyList<TapeRecord> records)
            {
                long length = CountPreSyncBytes + 4 + CountPostSyncBytes + CountToHeaderZeroGapBytes;
                foreach (TapeRecord record in records)
                {
                    length += BlockPreSyncBytes + SharpQdFrameCodec.HeaderFrameLength +
                        BlockPostSyncBytes + HeaderToBodyZeroGapBytes;
                    length += BlockPreSyncBytes + 6L + record.Body.DataSize +
                        BlockPostSyncBytes + BodyToNextHeaderZeroGapBytes;
                }
                return length;
            }

            private static byte[] BuildMeaningfulLayout(IReadOnlyList<TapeRecord> records)
            {
                using var stream = new MemoryStream();
                WriteRepeated(stream, 0x16, CountPreSyncBytes);
                stream.Write(SharpQdFrameCodec.EncodeCountFrame(checked((byte)(records.Count * 2))));
                WriteRepeated(stream, 0x16, CountPostSyncBytes);
                WriteRepeated(stream, 0x00, CountToHeaderZeroGapBytes);
                foreach (TapeRecord record in records)
                {
                    WriteRepeated(stream, 0x16, BlockPreSyncBytes);
                    stream.Write(SharpQdFrameCodec.EncodeHeaderFrame(record.Header));
                    WriteRepeated(stream, 0x16, BlockPostSyncBytes);
                    WriteRepeated(stream, 0x00, HeaderToBodyZeroGapBytes);

                    WriteRepeated(stream, 0x16, BlockPreSyncBytes);
                    stream.Write(SharpQdFrameCodec.EncodeBodyFrame(record.Body));
                    WriteRepeated(stream, 0x16, BlockPostSyncBytes);
                    WriteRepeated(stream, 0x00, BodyToNextHeaderZeroGapBytes);
                }
                return stream.ToArray();
            }

            private static void WriteRepeated(Stream stream, byte value, int count)
            {
                for (int i = 0; i < count; i++) stream.WriteByte(value);
            }

            private static void CopyBitsLsbFirst(
                ReadOnlySpan<byte> source,
                int sourceBitOffset,
                Span<byte> destination,
                int destinationBitOffset,
                int bitCount)
            {
                for (int bit = 0; bit < bitCount; bit++)
                {
                    int sourceBit = sourceBitOffset + bit;
                    int destinationBit = destinationBitOffset + bit;
                    int mask = 1 << (destinationBit & 7);
                    if ((source[sourceBit >> 3] & (1 << (sourceBit & 7))) != 0)
                    {
                        destination[destinationBit >> 3] |= (byte)mask;
                    }
                    else
                    {
                        destination[destinationBit >> 3] &= (byte)~mask;
                    }
                }
            }
        }

        internal static class QuickDiskPhysicalWriter
        {
            internal const int QdfSignatureLength = 16;
            internal const int FlashFloppyFnblkOffsetRaw = 0x25C6;

            // FlashFloppy uses the QDF-compatible MFM payload at window_start. Its first
            // count/FNBLK is about 380.4 ms after READY, near the center of the Sharp MZ
            // ROM sync-search window and close to real-MZ formatted media (~379.2 ms).

            public static byte[] Write(
                IReadOnlyList<TapeRecord> records,
                QdImageFormat format,
                QuickDiskPhysicalProfile? sourceProfile = null,
                bool allowImportedNonStandard = false)
            {
                if (!QuickDiskLimits.TryValidateForSave(records.Count, allowImportedNonStandard, out string countError))
                {
                    throw new InvalidDataException(countError);
                }
                QuickDiskPhysicalProfile profile = sourceProfile ?? QuickDiskPhysicalProfile.For(format);
                if (profile.Format != format)
                {
                    throw new ArgumentException("The preserved physical profile does not match the requested QD container type.", nameof(sourceProfile));
                }
                if (format == QdImageFormat.FlashFloppyPhysical)
                {
                    byte[] qdf = QDFFileReader.BuildImage(records, allowImportedNonStandard);
                    byte[] encoded = QuickDiskMfmCodec.Encode(qdf.AsSpan(QdfSignatureLength));
                    int trackDataStart = profile.WindowStart;
                    if (trackDataStart < 0 ||
                        (long)trackDataStart + encoded.Length > profile.WindowEnd ||
                        (long)trackDataStart + encoded.Length > profile.StoredTrackLength)
                    {
                        throw new InvalidDataException("Cannot save: FlashFloppy physical data placement exceeds the track data window.");
                    }

                    byte[] track = Enumerable.Repeat(profile.BlankFiller, profile.StoredTrackLength).ToArray();
                    encoded.CopyTo(track, trackDataStart);
                    return HxcFlashFloppyQdContainer.Write(track, profile);
                }

                byte[] hxcTrack = HxcCanonicalTrackBuilder.BuildTrack(records, profile);
                return HxcFlashFloppyQdContainer.Write(hxcTrack, profile);
            }

            public static bool TryValidateCapacity(
                IReadOnlyList<TapeRecord> records,
                QdImageFormat format,
                out string error,
                QuickDiskPhysicalProfile? sourceProfile = null)
            {
                QuickDiskPhysicalProfile profile = sourceProfile ?? QuickDiskPhysicalProfile.For(format);
                if (profile.Format != format)
                {
                    error = "The preserved physical profile does not match the requested QD container type.";
                    return false;
                }

                if (format == QdImageFormat.FlashFloppyPhysical)
                {
                    long requiredSize = QDFFileReader.HeaderSize +
                        records.Sum(record => QDFFileReader.FileOverhead + record.Body.DataSize);
                    if (requiredSize > QDFFileReader.ImageSize)
                    {
                        error = $"Cannot save: QDF-compatible QuickDisk capacity exceeded ({requiredSize} bytes required, {QDFFileReader.ImageSize} available).";
                        return false;
                    }

                    long canonicalEncodedBytes = (QDFFileReader.ImageSize - QdfSignatureLength) * 2;
                    long capacity = Math.Min(profile.WindowEnd, profile.StoredTrackLength) - (long)profile.WindowStart;
                    if (canonicalEncodedBytes > capacity)
                    {
                        error = "Cannot save: the FlashFloppy profile cannot contain the physical QDF payload.";
                        return false;
                    }

                    error = string.Empty;
                    return true;
                }

                return HxcCanonicalTrackBuilder.TryValidateCapacity(records, profile, out error);
            }
        }

        internal static class QdImageReaderWriter
        {
            public static QdReadResult ReadFile(string filePath)
            {
                byte[] image = File.ReadAllBytes(filePath);
                return Read(image);
            }

            public static QdReadResult Read(ReadOnlySpan<byte> image)
            {
                QdImageFormat format = QdFormatDetector.Detect(image);
                if (format == QdImageFormat.SharpLegacyLogical)
                {
                    return new QdReadResult(format, SharpLegacyQdCodec.Read(image));
                }
                if (format is QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical)
                {
                    QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(image, format);
                    return new QdReadResult(format, QuickDiskPhysicalReader.Read(container), container.Profile);
                }
                throw new InvalidDataException("Unknown .QD format.");
            }

            public static byte[] Write(
                IReadOnlyList<TapeRecord> records,
                QdImageFormat format,
                QuickDiskPhysicalProfile? sourceProfile = null,
                bool allowImportedNonStandard = false) => format switch
            {
                QdImageFormat.SharpLegacyLogical => SharpLegacyQdCodec.Write(records, allowImportedNonStandard),
                QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical =>
                    QuickDiskPhysicalWriter.Write(records, format, sourceProfile, allowImportedNonStandard),
                _ => throw new ArgumentOutOfRangeException(nameof(format), "A concrete QD image format is required.")
            };
        }

    #endregion

}
