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
            return CreateModel(creator).Serialize();
        }

        internal static DskImage CreateModel(string creator) => DskImage.CreateUniform(
            CylinderCount,
            SideCount,
            SectorsPerTrack,
            SectorSize,
            firstSectorId: 1,
            gap: 0x4E,
            filler: 0xFF,
            creator);

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

            DskImage model = DskImage.Parse(image);
            WriteLogicalBlock(model, block, logicalData);
            model.Serialize().CopyTo(image, 0);
        }

        internal static void WriteLogicalBlock(DskImage image, int block, ReadOnlySpan<byte> logicalData)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (logicalData.Length != SectorSize)
            {
                throw new ArgumentException($"A logical block must contain exactly {SectorSize} bytes.", nameof(logicalData));
            }
            (int cylinder, int side, int sector) = MapLogicalBlock(block);
            byte[] destination = image.GetSector(cylinder, side, sector).Data;
            for (int index = 0; index < SectorSize; index++)
            {
                destination[index] = (byte)(logicalData[index] ^ 0xFF);
            }
        }
    }
}
