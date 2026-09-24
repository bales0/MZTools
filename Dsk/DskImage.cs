using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QDTool
{
    // Extended CPC DSK container port based on bales0/mzdisk src/libs/dsk
    // (GPL-3.0-or-later, Michal Hucik). Filesystem semantics intentionally
    // live outside this container layer.
    internal sealed class DskImage
    {
        internal const int HeaderSize = 0x100;
        internal const int TrackHeaderSize = 0x100;
        internal const int MaximumAbsoluteTracks = 204;
        internal const int MaximumSectorsPerTrack = 29;
        private static ReadOnlySpan<byte> Signature => "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8;
        private static ReadOnlySpan<byte> TrackSignature => "Track-Info\r\n"u8;

        private readonly byte[] rawHeader;

        private DskImage(byte[] rawHeader, List<DskTrack?> tracks, byte[] trailingData)
        {
            this.rawHeader = rawHeader;
            Tracks = tracks;
            ContainerTrailingData = trailingData;
        }

        internal byte TrackCount
        {
            get => rawHeader[0x30];
            set => rawHeader[0x30] = value;
        }

        internal byte SideCount
        {
            get => rawHeader[0x31];
            set => rawHeader[0x31] = value;
        }

        internal string Creator
        {
            get => Encoding.ASCII.GetString(rawHeader, 0x22, 14).TrimEnd('\0', ' ');
            set
            {
                Span<byte> creatorField = rawHeader.AsSpan(0x22, 14);
                creatorField.Clear();
                byte[] encoded = Encoding.ASCII.GetBytes(value ?? string.Empty);
                encoded.AsSpan(0, Math.Min(creatorField.Length, encoded.Length)).CopyTo(creatorField);
            }
        }

        internal IReadOnlyList<DskTrack?> Tracks { get; }
        internal byte[] ContainerTrailingData { get; set; }
        internal int DeclaredImageSize => HeaderSize + Tracks.Sum(track => track?.BlockSize ?? 0);
        internal int TotalSectorDataBytes => Tracks.Where(track => track != null).Sum(track => track!.Sectors.Sum(sector => sector.Data.Length));

        internal static DskImage Parse(byte[] image)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Length < HeaderSize)
            {
                throw new InvalidDataException("Invalid DSK: the 256-byte image header is truncated.");
            }
            if (!image.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            {
                throw new InvalidDataException("Invalid DSK: the Extended CPC DSK signature is missing.");
            }

            byte[] header = image.AsSpan(0, HeaderSize).ToArray();
            int cylinders = header[0x30];
            int sides = header[0x31];
            if (cylinders == 0)
            {
                throw new InvalidDataException("Invalid DSK: the header declares zero tracks.");
            }
            if (sides is < 1 or > 2)
            {
                throw new InvalidDataException($"Invalid DSK: side count {sides} is outside the supported range 1..2.");
            }
            int absoluteTracks = checked(cylinders * sides);
            if (absoluteTracks > MaximumAbsoluteTracks || 0x34 + absoluteTracks > HeaderSize)
            {
                throw new InvalidDataException($"Invalid DSK: {absoluteTracks} absolute tracks exceed the supported maximum {MaximumAbsoluteTracks}.");
            }

            var tracks = new List<DskTrack?>(absoluteTracks);
            int offset = HeaderSize;
            for (int physicalIndex = 0; physicalIndex < absoluteTracks; physicalIndex++)
            {
                int blockSize = header[0x34 + physicalIndex] << 8;
                if (blockSize == 0)
                {
                    tracks.Add(null);
                    continue;
                }
                if (blockSize < TrackHeaderSize)
                {
                    throw new InvalidDataException($"Invalid DSK: physical track {physicalIndex} has block size {blockSize}, smaller than its header.");
                }
                if (offset > image.Length - blockSize)
                {
                    throw new InvalidDataException($"Invalid DSK: physical track {physicalIndex} is truncated and extends beyond the end of the image.");
                }

                tracks.Add(DskTrack.Parse(image.AsSpan(offset, blockSize), physicalIndex, offset));
                offset = checked(offset + blockSize);
            }

            byte[] trailing = image.AsSpan(offset).ToArray();
            return new DskImage(header, tracks, trailing);
        }

        internal static DskImage CreateUniform(
            int tracks,
            int sides,
            int sectorsPerTrack,
            int sectorSize,
            int firstSectorId,
            byte gap,
            byte filler,
            string creator)
        {
            if (tracks <= 0 || sides is < 1 or > 2 || checked(tracks * sides) > MaximumAbsoluteTracks)
            {
                throw new ArgumentOutOfRangeException(nameof(tracks), "DSK geometry exceeds the supported track/side limits.");
            }
            if (sectorsPerTrack is < 1 or > MaximumSectorsPerTrack)
            {
                throw new ArgumentOutOfRangeException(nameof(sectorsPerTrack));
            }
            byte sizeCode = EncodeSectorSize(sectorSize);
            int blockSize = checked(TrackHeaderSize + sectorsPerTrack * sectorSize);
            int roundedBlockSize = (blockSize + 0xFF) & ~0xFF;
            if ((roundedBlockSize >> 8) > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(sectorsPerTrack), "Track block is too large for Extended DSK tsize.");
            }

            var header = new byte[HeaderSize];
            Signature.CopyTo(header);
            header[0x30] = checked((byte)tracks);
            header[0x31] = checked((byte)sides);
            header.AsSpan(0x34, tracks * sides).Fill(checked((byte)(roundedBlockSize >> 8)));
            var image = new DskImage(header, new List<DskTrack?>(tracks * sides), Array.Empty<byte>());
            image.Creator = creator;
            for (int cylinder = 0; cylinder < tracks; cylinder++)
            {
                for (int side = 0; side < sides; side++)
                {
                    int physicalIndex = cylinder * sides + side;
                    image.TracksInternal.Add(DskTrack.Create(
                        cylinder,
                        side,
                        sectorsPerTrack,
                        sizeCode,
                        firstSectorId,
                        gap,
                        filler,
                        roundedBlockSize,
                        physicalIndex));
                }
            }
            return image;
        }

        internal static DskImage CreateSharpBootDataDisk(
            int tracks,
            int sides,
            int dataSectorsPerTrack,
            bool highDensityInterleave,
            string creator)
        {
            if (tracks <= 0 || sides is < 1 or > 2 || checked(tracks * sides) > MaximumAbsoluteTracks || tracks * sides < 3)
            {
                throw new ArgumentOutOfRangeException(nameof(tracks), "Sharp CP/M/MRS geometry requires at least three absolute tracks.");
            }
            if (dataSectorsPerTrack is not (8 or 9 or 10 or 18))
            {
                throw new ArgumentOutOfRangeException(nameof(dataSectorsPerTrack));
            }

            int absoluteTracks = tracks * sides;
            var header = new byte[HeaderSize];
            Signature.CopyTo(header);
            header[0x30] = checked((byte)tracks);
            header[0x31] = checked((byte)sides);
            var image = new DskImage(header, new List<DskTrack?>(absoluteTracks), Array.Empty<byte>());
            image.Creator = creator;

            for (int physicalIndex = 0; physicalIndex < absoluteTracks; physicalIndex++)
            {
                int cylinder = physicalIndex / sides;
                int side = physicalIndex % sides;
                bool bootTrack = physicalIndex == 1;
                int sectorCount = bootTrack ? 16 : dataSectorsPerTrack;
                int sectorSize = bootTrack ? 256 : 512;
                byte filler = bootTrack ? (byte)0xFF : (byte)0xE5;
                int rawSize = TrackHeaderSize + sectorCount * sectorSize;
                int blockSize = (rawSize + 0xFF) & ~0xFF;
                DskTrack track = DskTrack.Create(cylinder, side, sectorCount, EncodeSectorSize(sectorSize), 1, 0x4E, filler, blockSize, physicalIndex);
                if (!bootTrack)
                {
                    int interleave = highDensityInterleave ? 3 : 2;
                    int orderedCount = (sectorCount & 1) == 0 ? sectorCount : sectorCount + 1;
                    int position = 0;
                    for (int first = 0; first < sectorCount; first += interleave)
                    {
                        for (int offset = 0; offset < interleave && first + offset < sectorCount; offset++)
                        {
                            track.Sectors[position++].SectorId = checked((byte)(1 + first / interleave + orderedCount / interleave * offset));
                        }
                    }
                }
                image.TracksInternal.Add(track);
                header[0x34 + physicalIndex] = checked((byte)(blockSize >> 8));
            }
            return image;
        }

        private List<DskTrack?> TracksInternal => (List<DskTrack?>)Tracks;

        internal void ReplaceTrackGeometry(
            int absoluteTrack,
            int sectorCount,
            int sectorSize,
            IReadOnlyList<int>? sectorIds,
            byte gap,
            byte filler)
        {
            if ((uint)absoluteTrack >= Tracks.Count) throw new ArgumentOutOfRangeException(nameof(absoluteTrack));
            if (sectorIds != null && sectorIds.Count != sectorCount) throw new ArgumentException("The sector ID map must contain one ID per sector.", nameof(sectorIds));
            int cylinder = absoluteTrack / SideCount;
            int side = absoluteTrack % SideCount;
            int rawSize = checked(TrackHeaderSize + sectorCount * sectorSize);
            int blockSize = (rawSize + 0xFF) & ~0xFF;
            DskTrack track = DskTrack.Create(cylinder, side, sectorCount, EncodeSectorSize(sectorSize), 1, gap, filler, blockSize, absoluteTrack);
            if (sectorIds != null)
            {
                for (int index = 0; index < sectorIds.Count; index++) track.Sectors[index].SectorId = checked((byte)sectorIds[index]);
            }
            TracksInternal[absoluteTrack] = track;
            rawHeader[0x34 + absoluteTrack] = checked((byte)(blockSize >> 8));
        }

        internal DskTrack GetTrack(int cylinder, int side)
        {
            if ((uint)cylinder >= TrackCount || (uint)side >= SideCount)
            {
                throw new ArgumentOutOfRangeException(nameof(cylinder));
            }
            int physicalIndex = cylinder * SideCount + side;
            return Tracks[physicalIndex] ??
                throw new InvalidDataException($"DSK track C={cylinder}, H={side} is not present (tsize=0).");
        }

        internal DskSector GetSector(int cylinder, int side, int sectorId) =>
            GetTrack(cylinder, side).Sectors.FirstOrDefault(sector => sector.SectorId == sectorId) ??
            throw new InvalidDataException($"DSK sector C={cylinder}, H={side}, R={sectorId} was not found.");

        internal byte[] Serialize()
        {
            int expectedTracks = checked(TrackCount * SideCount);
            if (expectedTracks != Tracks.Count || expectedTracks > MaximumAbsoluteTracks)
            {
                throw new InvalidOperationException("DSK track collection does not match the header geometry.");
            }

            byte[] header = (byte[])rawHeader.Clone();
            var blocks = new List<byte[]>(Tracks.Count);
            for (int index = 0; index < Tracks.Count; index++)
            {
                DskTrack? track = Tracks[index];
                if (track == null)
                {
                    header[0x34 + index] = 0;
                    blocks.Add(Array.Empty<byte>());
                    continue;
                }
                byte[] block = track.Serialize();
                if ((block.Length & 0xFF) != 0 || block.Length > 0xFF00)
                {
                    throw new InvalidOperationException($"DSK physical track {index} size {block.Length} is not representable by tsize.");
                }
                header[0x34 + index] = checked((byte)(block.Length >> 8));
                blocks.Add(block);
            }

            int length = checked(HeaderSize + blocks.Sum(block => block.Length) + ContainerTrailingData.Length);
            var output = new byte[length];
            header.CopyTo(output, 0);
            int offset = HeaderSize;
            foreach (byte[] block in blocks)
            {
                block.CopyTo(output, offset);
                offset += block.Length;
            }
            ContainerTrailingData.CopyTo(output, offset);
            return output;
        }

        internal static int DecodeSectorSize(byte sizeCode)
        {
            if (sizeCode > 3)
            {
                throw new InvalidDataException($"Unsupported DSK sector size code N={sizeCode}; supported values are 0..3.");
            }
            return 128 << sizeCode;
        }

        internal static byte EncodeSectorSize(int size) => size switch
        {
            128 => 0,
            256 => 1,
            512 => 2,
            1024 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(size), "DSK sectors must contain 128, 256, 512 or 1024 bytes.")
        };

        internal sealed class DskTrack
        {
            private readonly byte[] rawHeader;
            private byte[] padding;

            private DskTrack(byte[] rawHeader, List<DskSector> sectors, byte[] padding, int physicalIndex, int fileOffset)
            {
                this.rawHeader = rawHeader;
                Sectors = sectors;
                this.padding = padding;
                PhysicalIndex = physicalIndex;
                FileOffset = fileOffset;
            }

            internal int PhysicalIndex { get; }
            internal int FileOffset { get; }
            internal int BlockSize => TrackHeaderSize + Sectors.Sum(sector => sector.Data.Length) + padding.Length;
            internal byte Cylinder { get => rawHeader[0x10]; set => rawHeader[0x10] = value; }
            internal byte Side { get => rawHeader[0x11]; set => rawHeader[0x11] = value; }
            internal byte DefaultSizeCode { get => rawHeader[0x14]; set => rawHeader[0x14] = value; }
            internal byte Gap { get => rawHeader[0x16]; set => rawHeader[0x16] = value; }
            internal byte Filler { get => rawHeader[0x17]; set => rawHeader[0x17] = value; }
            internal List<DskSector> Sectors { get; }

            internal static DskTrack Parse(ReadOnlySpan<byte> block, int physicalIndex, int fileOffset)
            {
                if (!block[..TrackSignature.Length].SequenceEqual(TrackSignature))
                {
                    throw new InvalidDataException($"Invalid DSK: physical track {physicalIndex} has no Track-Info signature.");
                }
                byte[] header = block[..TrackHeaderSize].ToArray();
                int count = header[0x15];
                if (count > MaximumSectorsPerTrack)
                {
                    throw new InvalidDataException($"Invalid DSK: physical track {physicalIndex} declares {count} sectors; maximum is {MaximumSectorsPerTrack}.");
                }
                var sectors = new List<DskSector>(count);
                int dataOffset = TrackHeaderSize;
                for (int index = 0; index < count; index++)
                {
                    int descriptorOffset = 0x18 + index * 8;
                    byte[] descriptor = header.AsSpan(descriptorOffset, 8).ToArray();
                    int decodedSize = DecodeSectorSize(descriptor[3]);
                    int declaredSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(6, 2));
                    int dataLength = declaredSize == 0 ? decodedSize : declaredSize;
                    if (dataLength != decodedSize)
                    {
                        throw new InvalidDataException(
                            $"Invalid DSK: sector C={descriptor[0]}, H={descriptor[1]}, R={descriptor[2]} has data length {dataLength}, but N={descriptor[3]} means {decodedSize}.");
                    }
                    if (dataOffset > block.Length - dataLength)
                    {
                        throw new InvalidDataException(
                            $"Invalid DSK: physical track {physicalIndex} has size 0x{block.Length:X}; sector C={descriptor[0]}, H={descriptor[1]}, R={descriptor[2]} extends beyond it.");
                    }
                    sectors.Add(new DskSector(descriptor, block.Slice(dataOffset, dataLength).ToArray(), index));
                    dataOffset += dataLength;
                }
                return new DskTrack(header, sectors, block[dataOffset..].ToArray(), physicalIndex, fileOffset);
            }

            internal static DskTrack Create(int cylinder, int side, int count, byte sizeCode, int firstId, byte gap, byte filler, int blockSize, int physicalIndex)
            {
                var header = new byte[TrackHeaderSize];
                TrackSignature.CopyTo(header);
                header[0x10] = checked((byte)cylinder);
                header[0x11] = checked((byte)side);
                header[0x14] = sizeCode;
                header[0x15] = checked((byte)count);
                header[0x16] = gap;
                header[0x17] = filler;
                int size = DecodeSectorSize(sizeCode);
                var sectors = new List<DskSector>(count);
                for (int index = 0; index < count; index++)
                {
                    var descriptor = new byte[8];
                    descriptor[0] = checked((byte)cylinder);
                    descriptor[1] = checked((byte)side);
                    descriptor[2] = checked((byte)(firstId + index));
                    descriptor[3] = sizeCode;
                    descriptor.CopyTo(header, 0x18 + index * 8);
                    sectors.Add(new DskSector(descriptor, Enumerable.Repeat(filler, size).Select(value => (byte)value).ToArray(), index));
                }
                int paddingSize = blockSize - TrackHeaderSize - count * size;
                return new DskTrack(header, sectors, new byte[paddingSize], physicalIndex, 0);
            }

            internal byte[] Serialize()
            {
                if (Sectors.Count > MaximumSectorsPerTrack)
                {
                    throw new InvalidOperationException("A DSK track cannot contain more than 29 sectors.");
                }
                byte[] header = (byte[])rawHeader.Clone();
                header[0x15] = checked((byte)Sectors.Count);
                for (int index = 0; index < Sectors.Count; index++)
                {
                    DskSector sector = Sectors[index];
                    if (sector.Data.Length != DecodeSectorSize(sector.SizeCode))
                    {
                        throw new InvalidOperationException($"Sector R={sector.SectorId} data length does not match N={sector.SizeCode}.");
                    }
                    sector.SerializeDescriptor().CopyTo(header, 0x18 + index * 8);
                }
                var output = new byte[BlockSize];
                header.CopyTo(output, 0);
                int offset = TrackHeaderSize;
                foreach (DskSector sector in Sectors)
                {
                    sector.Data.CopyTo(output, offset);
                    offset += sector.Data.Length;
                }
                padding.CopyTo(output, offset);
                return output;
            }
        }

        internal sealed class DskSector
        {
            private readonly byte[] descriptor;

            internal DskSector(byte[] descriptor, byte[] data, int physicalIndex)
            {
                this.descriptor = descriptor;
                Data = data;
                PhysicalIndex = physicalIndex;
            }

            internal int PhysicalIndex { get; }
            internal byte Cylinder { get => descriptor[0]; set => descriptor[0] = value; }
            internal byte Side { get => descriptor[1]; set => descriptor[1] = value; }
            internal byte SectorId { get => descriptor[2]; set => descriptor[2] = value; }
            internal byte SizeCode { get => descriptor[3]; set => descriptor[3] = value; }
            internal byte FdcStatus1 { get => descriptor[4]; set => descriptor[4] = value; }
            internal byte FdcStatus2 { get => descriptor[5]; set => descriptor[5] = value; }
            internal byte[] Data { get; set; }

            internal byte[] SerializeDescriptor()
            {
                byte[] result = (byte[])descriptor.Clone();
                int storedLength = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(6, 2));
                if (storedLength != 0)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2), checked((ushort)Data.Length));
                }
                return result;
            }
        }
    }
}
