using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QDTool
{
    internal static class Mz800IplDskWriter
    {
        public const int CylinderCount = Mz800DskImage.CylinderCount;
        public const int SideCount = Mz800DskImage.SideCount;
        public const int SectorsPerTrack = Mz800DskImage.SectorsPerTrack;
        public const int SectorSize = Mz800DskImage.SectorSize;
        public const int LogicalSectorCount = Mz800DskImage.LogicalSectorCount;
        public const int DiskHeaderSize = Mz800DskImage.DiskHeaderSize;
        public const int TrackBlockSize = Mz800DskImage.TrackBlockSize;
        public const int ImageSize = Mz800DskImage.ImageSize;
        public const int MaxIplStagedSize = 0xBCE9;
        public const int MzfHeaderMemoryAddress = 0x10F0;

        public static byte[] Build(TapeRecord record, string? bootName = null)
        {
            record = PrepareRecordForIpl(record);
            ValidateRecord(record);

            DskImage image = Mz800DskImage.CreateModel("MZTools IPLDSK");

            byte[] ipl = BuildIplBlock(record, bootName);
            Mz800DskImage.WriteLogicalBlock(image, 0, ipl);

            byte[] body = record.Body.MzfBody;
            int bodySectors = (body.Length + SectorSize - 1) / SectorSize;
            byte[] logicalSector = new byte[SectorSize];
            for (int sectorIndex = 0; sectorIndex < bodySectors; sectorIndex++)
            {
                Array.Clear(logicalSector);
                int sourceOffset = sectorIndex * SectorSize;
                int length = Math.Min(SectorSize, body.Length - sourceOffset);
                body.AsSpan(sourceOffset, length).CopyTo(logicalSector.AsSpan());
                Mz800DskImage.WriteLogicalBlock(image, sectorIndex + 1, logicalSector);
            }

            return image.Serialize();
        }

        public static void WriteFile(string path, TapeRecord record, string? bootName = null) =>
            File.WriteAllBytes(path, Build(record, bootName));

        public static (int Cylinder, int Side, int Sector) MapLogicalBlock(int block) =>
            Mz800DskImage.MapLogicalBlock(block);

        public static string NormalizeBootName(string? value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "PROGRAM" : value.Trim();
            var builder = new StringBuilder(12);
            foreach (char character in source)
            {
                if (builder.Length == 12)
                {
                    break;
                }
                builder.Append(character is >= ' ' and <= '~' ? character : ' ');
            }

            string result = builder.ToString().Trim();
            return result.Length == 0 ? "PROGRAM" : result;
        }

        internal static TapeRecord PrepareRecordForIpl(TapeRecord source)
        {
            ArgumentNullException.ThrowIfNull(source);
            byte[] body = source.Body.MzfBody;
            int load = source.Header.MzfStart;
            int exec = source.Header.MzfExec;
            long bodyEnd = (long)load + body.Length;
            if (exec >= load && exec < bodyEnd)
            {
                return source;
            }

            int headerEnd = MzfHeaderMemoryAddress + TapeRecord.HeaderLength;
            if (exec < MzfHeaderMemoryAddress || exec >= headerEnd)
            {
                return source;
            }

            byte[] serializedHeader = source.GetSerializedHeader();
            var visited = new HashSet<int>();
            while (exec >= MzfHeaderMemoryAddress && exec < headerEnd && visited.Add(exec))
            {
                int offset = exec - MzfHeaderMemoryAddress;
                if (offset > serializedHeader.Length - 3 || serializedHeader[offset] != 0xC3)
                {
                    break;
                }

                exec = BinaryPrimitives.ReadUInt16LittleEndian(serializedHeader.AsSpan(offset + 1, 2));
            }

            if (exec < MzfHeaderMemoryAddress || exec >= headerEnd)
            {
                TapeRecord resolved = source.DeepClone();
                MZQFileHeader resolvedHeader = resolved.Header;
                resolvedHeader.MzfExec = (ushort)exec;
                resolved.Header = resolvedHeader;
                return resolved;
            }

            if (load < headerEnd)
            {
                throw new InvalidOperationException(
                    "Cannot create IPL DSK: the program starts in the MZF header area but its body overlaps that area.");
            }

            int bodyOffset = load - MzfHeaderMemoryAddress;
            int stagedSize = checked(bodyOffset + body.Length);
            if (stagedSize > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    "Cannot create IPL DSK: including the executable MZF header exceeds the 16-bit address space.");
            }

            var stagedBody = new byte[stagedSize];
            source.GetSerializedHeader().CopyTo(stagedBody, 0);
            body.CopyTo(stagedBody, bodyOffset);

            TapeRecord staged = source.DeepClone();
            MZQFileHeader header = staged.Header;
            header.MzfSize = (ushort)stagedSize;
            header.MzfStart = MzfHeaderMemoryAddress;
            MZQFileBody stagedRecordBody = staged.Body;
            stagedRecordBody.DataSize = (ushort)stagedSize;
            stagedRecordBody.MzfBody = stagedBody;
            stagedRecordBody.TrailingData = Array.Empty<byte>();
            staged.Header = header;
            staged.Body = stagedRecordBody;
            byte[] stagedRawHeader = staged.GetSerializedHeader();
            return new TapeRecord(
                stagedRawHeader,
                header,
                stagedRecordBody,
                staged.Profile,
                staged.MetadataOrigin);
        }

        private static void ValidateRecord(TapeRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            byte[]? body = record.Body.MzfBody;
            if (body == null || body.Length == 0)
            {
                throw new InvalidOperationException("Cannot create IPL DSK: the MZF program body is empty.");
            }
            if (body.Length != record.Body.DataSize || body.Length != record.Header.MzfSize)
            {
                throw new InvalidOperationException(
                    $"Cannot create IPL DSK: MZF sizes are inconsistent (header {record.Header.MzfSize} B, body declaration {record.Body.DataSize} B, actual body {body.Length} B).");
            }
            if (body.Length > MaxIplStagedSize)
            {
                throw new InvalidOperationException(
                    $"Cannot create IPL DSK: staged program is {body.Length} bytes, but the safe direct-IPL limit is {MaxIplStagedSize} bytes ($BCE9). Try ZX0/ZX7 compression or Auto.");
            }
            if ((long)record.Header.MzfStart + body.Length > 0x10000L)
            {
                throw new InvalidOperationException(
                    "Cannot create IPL DSK: the program LOAD and SIZE exceed the 16-bit MZ address space.");
            }

            int requiredSectors = 1 + (body.Length + SectorSize - 1) / SectorSize;
            if (requiredSectors > LogicalSectorCount)
            {
                throw new InvalidOperationException(
                    $"Cannot create IPL DSK: {requiredSectors} sectors are required, but the image contains {LogicalSectorCount} sectors.");
            }
        }

        private static byte[] BuildIplBlock(TapeRecord record, string? bootName)
        {
            byte[] result = new byte[SectorSize];
            result[0] = 0x03;
            "IPLPRO"u8.CopyTo(result.AsSpan(1, 6));
            result.AsSpan(0x07, 13).Fill(0x0D);

            string displayName = NormalizeBootName(
                bootName ?? SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname));
            SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(displayName).CopyTo(result, 0x07);

            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x14, 2), record.Header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x16, 2), record.Header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x18, 2), record.Header.MzfExec);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x1E, 2), 1);
            return result;
        }

    }
}
