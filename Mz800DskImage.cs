using System;

namespace QDTool
{
    internal static class Mz800DskImage
    {
        public const int CylinderCount = 40;
        public const int SideCount = 2;
        public const int SectorsPerTrack = 16;
        public const int SectorSize = 256;
        public const int LogicalSectorCount = CylinderCount * SideCount * SectorsPerTrack;
        public const int DiskHeaderSize = 0x100;
        public const int TrackBlockSize = 0x1100;
        public const int ImageSize = DiskHeaderSize + CylinderCount * SideCount * TrackBlockSize;

        private const int TrackDataOffset = 0x100;

        public static byte[] Create(string creator)
        {
            var image = new byte[ImageSize];
            InitializeContainer(image, creator);
            return image;
        }

        public static (int Cylinder, int Side, int Sector) MapLogicalBlock(int block)
        {
            if ((uint)block >= LogicalSectorCount)
            {
                throw new ArgumentOutOfRangeException(nameof(block));
            }

            int logicalTrack = block / SectorsPerTrack;
            int physicalTrackIndex = logicalTrack ^ 1;
            return (
                physicalTrackIndex / SideCount,
                physicalTrackIndex & 1,
                (block & (SectorsPerTrack - 1)) + 1);
        }

        public static void WriteLogicalBlock(byte[] image, int block, ReadOnlySpan<byte> logicalData)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Length != ImageSize)
            {
                throw new ArgumentException($"An MZ-800 IPL DSK image must contain exactly {ImageSize} bytes.", nameof(image));
            }
            if (logicalData.Length != SectorSize)
            {
                throw new ArgumentException($"A logical block must contain exactly {SectorSize} bytes.", nameof(logicalData));
            }

            (int cylinder, int side, int sector) = MapLogicalBlock(block);
            int physicalTrackIndex = cylinder * SideCount + side;
            int offset = DiskHeaderSize + physicalTrackIndex * TrackBlockSize +
                TrackDataOffset + (sector - 1) * SectorSize;
            Span<byte> destination = image.AsSpan(offset, SectorSize);
            for (int index = 0; index < SectorSize; index++)
            {
                destination[index] = (byte)(logicalData[index] ^ 0xFF);
            }
        }

        private static void InitializeContainer(byte[] image, string creator)
        {
            "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8.CopyTo(image.AsSpan(0x00));
            Span<byte> creatorField = image.AsSpan(0x22, 14);
            creatorField.Clear();
            for (int index = 0; index < Math.Min(creator.Length, creatorField.Length); index++)
            {
                char character = creator[index];
                creatorField[index] = character is >= ' ' and <= '~' ? (byte)character : (byte)' ';
            }
            image[0x30] = CylinderCount;
            image[0x31] = SideCount;
            image.AsSpan(0x34, CylinderCount * SideCount).Fill(0x11);

            for (int cylinder = 0; cylinder < CylinderCount; cylinder++)
            {
                for (int side = 0; side < SideCount; side++)
                {
                    int physicalTrackIndex = cylinder * SideCount + side;
                    int trackOffset = DiskHeaderSize + physicalTrackIndex * TrackBlockSize;
                    Span<byte> track = image.AsSpan(trackOffset, TrackBlockSize);
                    "Track-Info\r\n"u8.CopyTo(track);
                    track[0x10] = (byte)cylinder;
                    track[0x11] = (byte)side;
                    track[0x14] = 1;
                    track[0x15] = SectorsPerTrack;
                    track[0x16] = 0x4E;
                    track[0x17] = 0xFF;

                    for (int sectorIndex = 0; sectorIndex < SectorsPerTrack; sectorIndex++)
                    {
                        Span<byte> descriptor = track.Slice(0x18 + sectorIndex * 8, 8);
                        descriptor[0] = (byte)cylinder;
                        descriptor[1] = (byte)side;
                        descriptor[2] = (byte)(sectorIndex + 1);
                        descriptor[3] = 1;
                    }

                    track.Slice(TrackDataOffset, SectorsPerTrack * SectorSize).Fill(0xFF);
                }
            }
        }
    }
}
