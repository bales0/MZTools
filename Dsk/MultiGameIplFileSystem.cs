using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QDTool
{
    // Native MZTools multi-game IPL format (QDMG). The container remains a
    // normal inverted MZ-800 IPL disk; this layer exposes its embedded menu.
    internal sealed class MultiGameIplFileSystem : IDskFileSystem
    {
        private const int EntrySize = Mz800MultiGameIplDskWriter.EntrySize;
        private readonly DskImage image;
        private readonly List<DskFileEntry> entries;
        private readonly int usedBlocks;

        private MultiGameIplFileSystem(DskImage image)
        {
            if (!FsmzFileSystem.HasGeometry(image))
                throw new InvalidDataException("The image does not use MZ-800 IPL geometry.");
            this.image = image;
            byte[] ipl = ReadBlock(0);
            if (ipl[0] != 3 || !ipl.AsSpan(1, 6).SequenceEqual("IPLPRO"u8) || !ipl.AsSpan(0x20, 4).SequenceEqual("QDMG"u8))
                throw new InvalidDataException("The MZTools multi-game IPL signature is missing.");
            if (ipl[0x24] != Mz800MultiGameIplDskWriter.FormatVersion || ipl[0x25] != EntrySize)
                throw new InvalidDataException($"Unsupported MZTools multi-game IPL format version {ipl[0x24]} or entry size {ipl[0x25]}.");

            int menuSize = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x14, 2));
            int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x26, 2));
            int tableOffset = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x28, 2));
            int menuSectors = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x2A, 2));
            if (entryCount is < 1 or > Mz800MultiGameIplDskWriter.MaxEntries || menuSectors < 1 ||
                menuSize is < 1 or > ushort.MaxValue || menuSectors != BlocksFor(menuSize))
                throw new InvalidDataException("The MZTools multi-game IPL header contains invalid menu dimensions.");

            byte[] menu = ReadBytes(1, menuSize);
            if (tableOffset < 0 || tableOffset > menu.Length - checked(entryCount * EntrySize))
                throw new InvalidDataException("The MZTools multi-game entry table lies outside the menu program.");

            entries = new List<DskFileEntry>(entryCount);
            var occupied = new HashSet<int>(Enumerable.Range(0, 1 + menuSectors));
            for (int index = 0; index < entryCount; index++)
            {
                ReadOnlySpan<byte> raw = menu.AsSpan(tableOffset + index * EntrySize, EntrySize);
                int startBlock = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(16, 2));
                int size = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(18, 2));
                int blocks = BlocksFor(size);
                if (size < 1 || startBlock < 1 + menuSectors || startBlock + blocks > Mz800DskImage.LogicalSectorCount)
                    throw new InvalidDataException($"Multi-game IPL entry {index + 1} references blocks outside the image.");
                for (int block = startBlock; block < startBlock + blocks; block++)
                    if (!occupied.Add(block)) throw new InvalidDataException($"Multi-game IPL entry {index + 1} overlaps another payload.");

                string name = SharpMzEncoding.ConvertMzfNameToASCIIString(raw[..16].ToArray()).TrimEnd();
                byte compression = raw[25];
                entries.Add(new DskFileEntry
                {
                    Key = index.ToString(),
                    Name = string.IsNullOrWhiteSpace(name) ? $"GAME {index + 1}" : name,
                    FileType = 1,
                    Size = size,
                    LoadAddress = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(20, 2)),
                    ExecuteAddress = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(22, 2)),
                    StartBlock = startBlock,
                    Blocks = blocks,
                    Notes = compression switch { 0 => "Uncompressed", 1 => "ZX0", 2 => "ZX7", _ => $"Compression {compression}" }
                });
            }
            usedBlocks = occupied.Count;
        }

        public DskFileSystemType Type => DskFileSystemType.MultiIpl;
        public string DisplayName => "MZTools multi-game IPL";
        public bool IsReadOnly => true;
        public long UsedBytes => usedBlocks * Mz800DskImage.SectorSize;
        public long FreeBytes => (Mz800DskImage.LogicalSectorCount - usedBlocks) * Mz800DskImage.SectorSize;
        public IReadOnlyList<string> Warnings => Array.Empty<string>();

        internal static bool TryOpen(DskImage image, out MultiGameIplFileSystem? result)
        {
            if (image.TrackCount != Mz800DskImage.CylinderCount || image.SideCount != Mz800DskImage.SideCount)
            {
                result = null;
                return false;
            }
            try { result = new MultiGameIplFileSystem(image); return true; }
            catch (InvalidDataException) { result = null; return false; }
        }

        public IReadOnlyList<DskFileEntry> ReadDirectory() => entries;

        public byte[] Extract(DskFileEntry entry) => ReadBytes(entry.StartBlock, checked((int)entry.Size));

        public void Insert(string name, byte[] data, byte fileType = 1, ushort loadAddress = 0, ushort executeAddress = 0, int user = 0) =>
            throw new NotSupportedException("MZTools multi-game IPL images are currently read-only.");
        public void Delete(DskFileEntry entry, bool force = false) =>
            throw new NotSupportedException("MZTools multi-game IPL images are currently read-only.");
        public void Rename(DskFileEntry entry, string newName) =>
            throw new NotSupportedException("MZTools multi-game IPL images are currently read-only.");

        private byte[] ReadBytes(int startBlock, int size)
        {
            var output = new byte[size];
            int copied = 0;
            for (int index = 0; copied < size; index++)
            {
                byte[] block = ReadBlock(startBlock + index);
                int count = Math.Min(block.Length, size - copied);
                block.AsSpan(0, count).CopyTo(output.AsSpan(copied));
                copied += count;
            }
            return output;
        }

        private byte[] ReadBlock(int block)
        {
            (int cylinder, int side, int sector) = Mz800DskImage.MapLogicalBlock(block);
            byte[] source = image.GetSector(cylinder, side, sector).Data;
            var result = new byte[source.Length];
            for (int index = 0; index < source.Length; index++) result[index] = (byte)(source[index] ^ 0xFF);
            return result;
        }

        private static int BlocksFor(int size) => checked((size + Mz800DskImage.SectorSize - 1) / Mz800DskImage.SectorSize);
    }
}
