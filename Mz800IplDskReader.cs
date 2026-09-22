using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using static QDTool.MzfFormatSupport;

namespace QDTool
{
    internal sealed record Mz800IplDskReadResult(
        TapeRecord Record,
        string BootName,
        ushort StartBlock,
        int ProgramSectorCount,
        Mz800IplDskInfo DiskInfo);

    internal sealed record Mz800IplDskInfo(
        int PayloadCapacityBytes,
        int UsedSectorBytes,
        int FreeCapacityBytes,
        int FreeSectorCount);

    internal static class Mz800IplDskReader
    {
        private const int TrackHeaderSize = 0x100;
        private const int SectorDescriptorOffset = 0x18;
        private const int SectorDescriptorSize = 8;

        public static Mz800IplDskReadResult ReadFile(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return Read(File.ReadAllBytes(path));
        }

        public static Mz800IplDskReadResult Read(ReadOnlySpan<byte> image)
        {
            int[,] sectorOffsets = ParseContainer(image);
            byte[] ipl = ReadLogicalBlock(image, sectorOffsets, 0);
            ValidateIplSignature(ipl);
            if (ipl.AsSpan(0x20, 4).SequenceEqual("QDMG"u8))
            {
                throw new InvalidDataException(
                    $"Unsupported DSK: MZTools multi-game IPL DSK version {ipl[0x24]} cannot be imported as one MZF record.");
            }

            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x14, 2));
            ushort load = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x16, 2));
            ushort exec = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x18, 2));
            ushort startBlock = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x1E, 2));

            if (size == 0)
            {
                throw new InvalidDataException("Unsupported DSK: the IPLPRO program size is zero.");
            }
            if (startBlock == 0)
            {
                throw new InvalidDataException("Unsupported DSK: IPLPRO START_BLOCK must be at least 1.");
            }
            if ((uint)load + size > 0x10000U)
            {
                throw new InvalidDataException("Unsupported DSK: IPLPRO LOAD and SIZE exceed the 16-bit MZ address space.");
            }

            int sectorCount = (size + Mz800IplDskWriter.SectorSize - 1) /
                Mz800IplDskWriter.SectorSize;
            int endBlock = checked(startBlock + sectorCount);
            if (startBlock >= Mz800IplDskWriter.LogicalSectorCount ||
                endBlock > Mz800IplDskWriter.LogicalSectorCount)
            {
                throw new InvalidDataException(
                    "Unsupported DSK: the declared IPLPRO payload extends beyond the disk capacity.");
            }

            var bodyBytes = new byte[size];
            for (int index = 0; index < sectorCount; index++)
            {
                byte[] sector = ReadLogicalBlock(image, sectorOffsets, startBlock + index);
                int destinationOffset = index * Mz800IplDskWriter.SectorSize;
                int length = Math.Min(Mz800IplDskWriter.SectorSize, size - destinationOffset);
                sector.AsSpan(0, length).CopyTo(bodyBytes.AsSpan(destinationOffset));
            }

            RejectAdditionalPayload(image, sectorOffsets, startBlock, endBlock);
            string bootName = DecodeBootName(ipl.AsSpan(0x07, 13));
            TapeRecord record = CreateRecord(bootName, size, load, exec, bodyBytes);
            int payloadSectorCapacity = Mz800IplDskWriter.LogicalSectorCount - 1;
            int freeSectorCount = payloadSectorCapacity - sectorCount;
            var diskInfo = new Mz800IplDskInfo(
                payloadSectorCapacity * Mz800IplDskWriter.SectorSize,
                sectorCount * Mz800IplDskWriter.SectorSize,
                freeSectorCount * Mz800IplDskWriter.SectorSize,
                freeSectorCount);
            return new Mz800IplDskReadResult(
                record,
                bootName,
                startBlock,
                sectorCount,
                diskInfo);
        }

        private static int[,] ParseContainer(ReadOnlySpan<byte> image)
        {
            if (image.Length < Mz800IplDskWriter.DiskHeaderSize)
            {
                throw new InvalidDataException("Invalid DSK: the disk header is truncated.");
            }
            if (!image[..34].SequenceEqual("EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8))
            {
                throw new InvalidDataException("Invalid DSK: the Extended CPC DSK signature is missing.");
            }

            int cylinders = image[0x30];
            int sides = image[0x31];
            if (cylinders != Mz800IplDskWriter.CylinderCount ||
                sides != Mz800IplDskWriter.SideCount)
            {
                throw new InvalidDataException(
                    $"Unsupported DSK geometry: expected {Mz800IplDskWriter.CylinderCount} cylinders and {Mz800IplDskWriter.SideCount} sides, found {cylinders} and {sides}.");
            }

            int trackCount = checked(cylinders * sides);
            if (0x34 + trackCount > Mz800IplDskWriter.DiskHeaderSize)
            {
                throw new InvalidDataException("Invalid DSK: the track-size table exceeds the disk header.");
            }

            var sectorOffsets = new int[trackCount, Mz800IplDskWriter.SectorsPerTrack];
            int trackOffset = Mz800IplDskWriter.DiskHeaderSize;
            for (int physicalTrack = 0; physicalTrack < trackCount; physicalTrack++)
            {
                int trackSize = image[0x34 + physicalTrack] * 0x100;
                if (trackSize != Mz800IplDskWriter.TrackBlockSize)
                {
                    throw new InvalidDataException(
                        $"Unsupported DSK geometry: track {physicalTrack} has size 0x{trackSize:X}, expected 0x{Mz800IplDskWriter.TrackBlockSize:X}.");
                }
                if (trackOffset > image.Length - trackSize)
                {
                    throw new InvalidDataException($"Invalid DSK: track {physicalTrack} is truncated.");
                }

                ParseTrack(image.Slice(trackOffset, trackSize), physicalTrack, trackOffset, sectorOffsets);
                trackOffset = checked(trackOffset + trackSize);
            }

            if (trackOffset != image.Length)
            {
                throw new InvalidDataException(
                    $"Invalid DSK: container length is {image.Length} bytes, but its track table declares {trackOffset} bytes.");
            }
            return sectorOffsets;
        }

        private static void ParseTrack(
            ReadOnlySpan<byte> track,
            int physicalTrack,
            int absoluteTrackOffset,
            int[,] sectorOffsets)
        {
            int cylinder = physicalTrack / Mz800IplDskWriter.SideCount;
            int side = physicalTrack & 1;
            if (!track[..12].SequenceEqual("Track-Info\r\n"u8))
            {
                throw new InvalidDataException($"Invalid DSK: track C={cylinder}, H={side} has no Track-Info signature.");
            }
            if (track[0x10] != cylinder || track[0x11] != side)
            {
                throw new InvalidDataException($"Invalid DSK: track header C/H does not match C={cylinder}, H={side}.");
            }
            if (track[0x14] != 1 || track[0x15] != Mz800IplDskWriter.SectorsPerTrack)
            {
                throw new InvalidDataException(
                    $"Unsupported DSK geometry: track C={cylinder}, H={side} must contain 16 sectors with N=1.");
            }

            var seen = new bool[Mz800IplDskWriter.SectorsPerTrack];
            int dataOffset = TrackHeaderSize;
            for (int descriptorIndex = 0; descriptorIndex < Mz800IplDskWriter.SectorsPerTrack; descriptorIndex++)
            {
                int descriptorOffset = SectorDescriptorOffset + descriptorIndex * SectorDescriptorSize;
                ReadOnlySpan<byte> descriptor = track.Slice(descriptorOffset, SectorDescriptorSize);
                int sectorId = descriptor[2];
                int actualLength = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[6..8]);
                if (descriptor[0] != cylinder || descriptor[1] != side || descriptor[3] != 1)
                {
                    throw new InvalidDataException(
                        $"Invalid DSK: sector descriptor {descriptorIndex + 1} on C={cylinder}, H={side} has invalid C/H/N values.");
                }
                if (sectorId < 1 || sectorId > Mz800IplDskWriter.SectorsPerTrack)
                {
                    throw new InvalidDataException(
                        $"Invalid DSK: sector ID {sectorId} on C={cylinder}, H={side} is outside 1..16.");
                }
                if (seen[sectorId - 1])
                {
                    throw new InvalidDataException(
                        $"Invalid DSK: duplicate sector ID {sectorId} on C={cylinder}, H={side}.");
                }
                if (actualLength != 0 && actualLength != Mz800IplDskWriter.SectorSize)
                {
                    throw new InvalidDataException(
                        $"Unsupported DSK geometry: sector C={cylinder}, H={side}, R={sectorId} has data length {actualLength}, expected 256.");
                }
                if (dataOffset > track.Length - Mz800IplDskWriter.SectorSize)
                {
                    throw new InvalidDataException(
                        $"Invalid DSK: sector data for C={cylinder}, H={side}, R={sectorId} is truncated.");
                }

                seen[sectorId - 1] = true;
                sectorOffsets[physicalTrack, sectorId - 1] = absoluteTrackOffset + dataOffset;
                dataOffset += Mz800IplDskWriter.SectorSize;
            }

            if (seen.Any(value => !value) || dataOffset != track.Length)
            {
                throw new InvalidDataException($"Invalid DSK: track C={cylinder}, H={side} has missing or overlapping sector data.");
            }
        }

        private static byte[] ReadLogicalBlock(ReadOnlySpan<byte> image, int[,] sectorOffsets, int block)
        {
            (int cylinder, int side, int sector) = Mz800IplDskWriter.MapLogicalBlock(block);
            int physicalTrack = cylinder * Mz800IplDskWriter.SideCount + side;
            int offset = sectorOffsets[physicalTrack, sector - 1];
            var result = new byte[Mz800IplDskWriter.SectorSize];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = (byte)(image[offset + index] ^ 0xFF);
            }
            return result;
        }

        private static void ValidateIplSignature(ReadOnlySpan<byte> ipl)
        {
            if (ipl[0] != 0x03 || !ipl.Slice(1, 6).SequenceEqual("IPLPRO"u8))
            {
                throw new InvalidDataException("Unsupported DSK: no compatible MZ-800 IPLPRO boot sector.");
            }
        }

        private static void RejectAdditionalPayload(
            ReadOnlySpan<byte> image,
            int[,] sectorOffsets,
            int startBlock,
            int endBlock)
        {
            for (int block = 1; block < Mz800IplDskWriter.LogicalSectorCount; block++)
            {
                if (block >= startBlock && block < endBlock)
                {
                    continue;
                }

                byte[] sector = ReadLogicalBlock(image, sectorOffsets, block);
                if (sector.Any(value => value != 0))
                {
                    throw new InvalidDataException(
                        $"Unsupported DSK: logical block {block} contains data outside the declared IPLPRO payload; this may be a multipart or boot-menu disk.");
                }
            }
        }

        private static string DecodeBootName(ReadOnlySpan<byte> encodedName)
        {
            int length = encodedName.IndexOf((byte)0x0D);
            if (length < 0)
            {
                length = encodedName.Length;
            }

            char[] characters = encodedName[..length]
                .ToArray()
                .Select(SharpMzEncoding.FromSHASCII)
                .Select(value => char.IsControl((char)value) ? ' ' : (char)value)
                .ToArray();
            string result = new string(characters).Trim();
            return result.Length == 0 ? "IPL_PROGRAM" : result;
        }

        private static TapeRecord CreateRecord(
            string bootName,
            ushort size,
            ushort load,
            ushort exec,
            byte[] bodyBytes)
        {
            var mzfName = new byte[16];
            byte[] encodedName = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(bootName);
            encodedName.AsSpan(0, Math.Min(encodedName.Length, mzfName.Length)).CopyTo(mzfName);

            var header = new MZQFileHeader
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfHeaderSign = 0,
                DataSize = 0x0040,
                MzfFtype = 0x01,
                MzfFname = mzfName,
                MzfFnameEnd = 0x0D,
                Unused1 = ExpectedUnused.ToArray(),
                MzfSize = size,
                MzfStart = load,
                MzfExec = exec,
                MzfHeaderDescription = new byte[104],
                Crc = ExpectedCrc.ToArray()
            };
            var body = new MZQFileBody
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfBodySign = 0x05,
                DataSize = size,
                MzfBody = bodyBytes,
                Crc = ExpectedCrc.ToArray(),
                TrailingData = Array.Empty<byte>()
            };
            return TapeRecord.FromLegacy(header, body);
        }
    }
}
