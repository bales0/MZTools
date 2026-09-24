using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QDTool
{
    // C# port of bales0/mzdisk src/libs/mzdsk_mrs (GPL-3.0-or-later).
    internal sealed class MrsFileSystem : IDskFileSystem
    {
        private const int TotalBlocks = 1440;
        private const int FatBlock = 36;
        private readonly DskBlockDevice device;
        private readonly List<string> warnings = new();
        private byte[] fat = Array.Empty<byte>();
        private byte[] directory = Array.Empty<byte>();
        private int fatSectors;
        private int directoryBlock;
        private int directorySectors;
        private int dataBlock;

        private MrsFileSystem(DskImage image)
        {
            device = new DskBlockDevice(image);
            Load();
        }

        public DskFileSystemType Type => DskFileSystemType.Mrs;
        public string DisplayName => "MRS";
        public bool IsReadOnly => warnings.Count != 0;
        public IReadOnlyList<string> Warnings => warnings;
        public long FreeBytes => fat.Take(TotalBlocks).Count(value => value == 0) * 512L;
        public long UsedBytes => ReadDirectory().Sum(entry => entry.Blocks) * 512L;

        internal static bool TryOpen(DskImage image, out MrsFileSystem? result)
        {
            try { result = new MrsFileSystem(image); return true; }
            catch (InvalidDataException) { result = null; return false; }
        }

        internal static void Format(DskImage image)
        {
            var device = new DskBlockDevice(image);
            var fat = new byte[1536];
            for (int index = 0; index < FatBlock; index++) fat[index] = 0xFF;
            fat[FatBlock] = fat[FatBlock + 1] = 0xFA;
            for (int index = 2; index <= 8; index++) fat[FatBlock + index] = 0xFD;
            for (int sector = 0; sector < 3; sector++) device.WriteLinear512Block(FatBlock + sector, fat.AsSpan(sector * 512, 512), inverted: true);
            var directory = new byte[6 * 512];
            for (int slot = 0; slot < 89; slot++)
            {
                Span<byte> raw = directory.AsSpan(slot * 32, 32); raw[..11].Fill(0x20); raw[11] = checked((byte)(slot + 1));
                raw[28] = 0xCD; raw[29] = 0x49; raw[30] = 0x02; raw[31] = 0x21;
            }
            directory.AsSpan(89 * 32).Fill(0x1A);
            for (int sector = 0; sector < 6; sector++) device.WriteLinear512Block(FatBlock + 3 + sector, directory.AsSpan(sector * 512, 512), inverted: true);
        }

        public IReadOnlyList<DskFileEntry> ReadDirectory()
        {
            var result = new List<DskFileEntry>();
            for (int slot = 0; slot < directory.Length / 32; slot++)
            {
                ReadOnlySpan<byte> raw = directory.AsSpan(slot * 32, 32);
                if (raw[0] <= 0x20 || raw[11] == 0 || raw[11] >= 0xFA) continue;
                int blocks = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(14, 2));
                if (blocks > TotalBlocks) { warnings.Add($"Unsafe: MRS slot {slot} declares {blocks} blocks."); continue; }
                result.Add(new DskFileEntry
                {
                    Key = slot.ToString(),
                    Name = Decode(raw[..8]),
                    Extension = Decode(raw.Slice(8, 3)),
                    StartBlock = raw[11],
                    LoadAddress = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(12, 2)),
                    Blocks = blocks,
                    Size = blocks * 512L,
                    ExecuteAddress = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(22, 2)),
                    Notes = "MRS stores block count only; exact byte size is unavailable."
                });
            }
            return result;
        }

        public byte[] Extract(DskFileEntry entry)
        {
            var output = new byte[entry.Blocks * 512];
            int written = 0;
            for (int block = 0; block < Math.Min(TotalBlocks, fat.Length) && written < output.Length; block++)
            {
                if (fat[block] != entry.StartBlock) continue;
                device.ReadLinear512Block(block, inverted: true).CopyTo(output, written);
                written += 512;
            }
            if (written != output.Length) throw new InvalidDataException($"MRS FAT chain for '{entry.Name}' contains {written / 512} blocks, expected {entry.Blocks}.");
            return output;
        }

        public void Insert(string name, byte[] data, byte fileType = 1, ushort loadAddress = 0, ushort executeAddress = 0, int user = 0)
        {
            SplitName(name, out string baseName, out string extension);
            if (ReadDirectory().Any(entry => entry.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))) throw new IOException($"MRS file '{baseName}' already exists.");
            int slot = Enumerable.Range(0, directory.Length / 32).FirstOrDefault(index => directory[index * 32] == 0x20);
            if (directory[slot * 32] != 0x20) throw new IOException("The MRS directory is full.");
            byte fileId = directory[slot * 32 + 11];
            int blocksNeeded = Math.Max(1, (data.Length + 511) / 512);
            int[] blocks = Enumerable.Range(dataBlock, Math.Min(TotalBlocks, fat.Length) - dataBlock).Where(block => fat[block] == 0).Take(blocksNeeded).ToArray();
            if (blocks.Length != blocksNeeded) throw new IOException("The MRS disk is full.");
            for (int index = 0; index < blocks.Length; index++)
            {
                var content = new byte[512];
                int offset = index * 512;
                data.AsSpan(offset, Math.Min(512, data.Length - offset)).CopyTo(content);
                device.WriteLinear512Block(blocks[index], content, inverted: true);
                fat[blocks[index]] = fileId;
            }
            Span<byte> raw = directory.AsSpan(slot * 32, 32);
            WriteName(raw[..8], baseName); WriteName(raw.Slice(8, 3), extension);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(12, 2), loadAddress);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(14, 2), checked((ushort)blocksNeeded));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(22, 2), executeAddress);
            Flush();
        }

        public void Delete(DskFileEntry entry, bool force = false)
        {
            byte fileId = checked((byte)entry.StartBlock);
            for (int block = 0; block < Math.Min(TotalBlocks, fat.Length); block++) if (fat[block] == fileId) fat[block] = 0;
            Span<byte> raw = directory.AsSpan(int.Parse(entry.Key) * 32, 32);
            raw[..11].Fill(0x20); raw.Slice(12, 16).Clear();
            Flush();
        }

        public void Rename(DskFileEntry entry, string newName)
        {
            SplitName(newName, out string baseName, out string extension);
            Span<byte> raw = directory.AsSpan(int.Parse(entry.Key) * 32, 32);
            WriteName(raw[..8], baseName); WriteName(raw.Slice(8, 3), extension);
            FlushDirectory();
        }

        internal void SetAddresses(DskFileEntry entry, ushort load, ushort execute)
        {
            Span<byte> raw = directory.AsSpan(int.Parse(entry.Key) * 32, 32);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(12, 2), load);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(22, 2), execute);
            FlushDirectory();
        }

        internal void Format()
        {
            fat = new byte[1536];
            for (int index = 0; index < FatBlock; index++) fat[index] = 0xFF;
            fat[FatBlock] = fat[FatBlock + 1] = 0xFA;
            for (int index = 2; index <= 8; index++) fat[FatBlock + index] = 0xFD;
            fatSectors = 3; directoryBlock = FatBlock + 3; directorySectors = 6; dataBlock = FatBlock + 9;
            directory = new byte[6 * 512];
            for (int slot = 0; slot < 89; slot++)
            {
                Span<byte> raw = directory.AsSpan(slot * 32, 32);
                raw[..11].Fill(0x20); raw[11] = checked((byte)(slot + 1));
                raw[28] = 0xCD; raw[29] = 0x49; raw[30] = 0x02; raw[31] = 0x21;
            }
            directory.AsSpan(89 * 32).Fill(0x1A);
            Flush();
        }

        private void Load()
        {
            byte[] first = device.ReadLinear512Block(FatBlock, inverted: true);
            if (first[FatBlock] != 0xFA) throw new InvalidDataException("MRS FAT marker was not found at block 36.");
            int position = FatBlock;
            while (position < 512 && first[position] == 0xFA) position++;
            fatSectors = position - FatBlock;
            directoryBlock = position;
            while (position < 512 && first[position] == 0xFD) position++;
            directorySectors = position - directoryBlock;
            dataBlock = position;
            if (fatSectors == 2 && directoryBlock == FatBlock + 2 && directorySectors >= 2)
            { fatSectors = 3; directoryBlock++; directorySectors--; }
            if (fatSectors is < 1 or > 3 || directorySectors is < 1 or > 8) throw new InvalidDataException("MRS FAT/directory layout is invalid.");
            fat = new byte[fatSectors * 512];
            first.CopyTo(fat, 0);
            for (int sector = 1; sector < fatSectors; sector++) device.ReadLinear512Block(FatBlock + sector, inverted: true).CopyTo(fat, sector * 512);
            directory = new byte[directorySectors * 512];
            for (int sector = 0; sector < directorySectors; sector++) device.ReadLinear512Block(directoryBlock + sector, inverted: true).CopyTo(directory, sector * 512);
        }

        private void Flush()
        {
            for (int sector = 0; sector < fatSectors; sector++) device.WriteLinear512Block(FatBlock + sector, fat.AsSpan(sector * 512, 512), inverted: true);
            FlushDirectory();
        }
        private void FlushDirectory()
        {
            for (int sector = 0; sector < directorySectors; sector++) device.WriteLinear512Block(directoryBlock + sector, directory.AsSpan(sector * 512, 512), inverted: true);
        }
        private static string Decode(ReadOnlySpan<byte> value) => Encoding.ASCII.GetString(value).TrimEnd(' ');
        private static void WriteName(Span<byte> destination, string value)
        { destination.Fill(0x20); Encoding.ASCII.GetBytes(value.ToUpperInvariant()).AsSpan(0, Math.Min(destination.Length, value.Length)).CopyTo(destination); }
        private static void SplitName(string value, out string name, out string extension)
        {
            string file = Path.GetFileName(value).ToUpperInvariant(); extension = Path.GetExtension(file).TrimStart('.'); name = Path.GetFileNameWithoutExtension(file);
            if (name.Length is < 1 or > 8 || extension.Length > 3) throw new InvalidDataException("MRS filenames must use the 8.3 format.");
        }
    }
}
