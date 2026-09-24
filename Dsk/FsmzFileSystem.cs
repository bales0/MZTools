using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QDTool
{
    // C# port of bales0/mzdisk src/libs/mzdsk_ipldisk (GPL-3.0-or-later).
    internal sealed class FsmzFileSystem : IDskFileSystem
    {
        private const int BlockSize = 256;
        private const int DirectoryStart = 16;
        private const int DinfoBlock = 15;
        private readonly DskBlockDevice device;
        private readonly List<string> warnings = new();
        private readonly int directoryLimit;
        private byte[] dinfo;

        internal FsmzFileSystem(DskImage image, bool? extendedDirectory = null)
        {
            if (!HasGeometry(image)) throw new InvalidDataException("The disk does not have the FSMZ 16 x 256-byte geometry.");
            device = new DskBlockDevice(image);
            dinfo = ReadBlock(DinfoBlock);
            int totalBlocks = image.Tracks.Count * 16;
            int declaredLastBlock = BinaryPrimitives.ReadUInt16LittleEndian(dinfo.AsSpan(4, 2));
            if (dinfo[1] < 24 || dinfo[1] >= totalBlocks)
                throw new InvalidDataException($"FSMZ DINFO has invalid file-area start block {dinfo[1]}.");
            if (declaredLastBlock + 1 > totalBlocks)
                warnings.Add($"DINFO declares {declaredLastBlock + 1} blocks, but the image contains {totalBlocks}.");
            directoryLimit = (extendedDirectory ?? DetectExtendedDirectory()) ? 127 : 63;
            ValidateAllocations(totalBlocks);
        }

        public DskFileSystemType Type => DskFileSystemType.Fsmz;
        public string DisplayName => directoryLimit == 127 ? "FSMZ / IPLDISK" : "FSMZ / MZ-BASIC";
        public bool IsReadOnly => warnings.Any(message => message.StartsWith("Unsafe:", StringComparison.Ordinal));
        public long UsedBytes => BinaryPrimitives.ReadUInt16LittleEndian(dinfo.AsSpan(2, 2)) * BlockSize;
        public long FreeBytes => Math.Max(0, (BinaryPrimitives.ReadUInt16LittleEndian(dinfo.AsSpan(4, 2)) + 1L) * BlockSize - UsedBytes);
        public IReadOnlyList<string> Warnings => warnings;
        internal int DirectoryLimit => directoryLimit;
        internal byte VolumeNumber => dinfo[0];
        internal int FileAreaBlock => dinfo[1];

        internal static bool HasGeometry(DskImage image) =>
            image.Tracks.Count >= 2 && image.Tracks.Where(track => track != null).All(track =>
                track!.Sectors.Count == 16 && track.Sectors.All(sector => sector.Data.Length == BlockSize));

        internal static void Format(DskImage image, bool ipldisk)
        {
            var device = new DskBlockDevice(image);
            void Write(int block, byte[] data)
            {
                int absoluteTrack = (block / 16) ^ 1;
                device.WriteSector(absoluteTrack, block % 16 + 1, data, inverted: true);
            }
            int totalBlocks = image.Tracks.Count * 16;
            int directoryBlocks = ipldisk ? 16 : 8;
            for (int block = 0; block < directoryBlocks; block++) Write(DirectoryStart + block, new byte[BlockSize]);
            byte[] firstDirectory = new byte[BlockSize]; firstDirectory[0] = 0x80; firstDirectory[1] = 0x01; Write(DirectoryStart, firstDirectory);
            var info = new byte[BlockSize]; info[0] = 0; info[1] = 0x30;
            BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2, 2), info[1]);
            int maximumAddressableBlocks = info[1] + 2000;
            BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(4, 2), checked((ushort)(Math.Min(totalBlocks, maximumAddressableBlocks) - 1)));
            Write(DinfoBlock, info);
        }

        public IReadOnlyList<DskFileEntry> ReadDirectory()
        {
            var result = new List<DskFileEntry>();
            for (int slot = 1; slot <= directoryLimit; slot++)
            {
                byte[] raw = ReadDirectorySlot(slot);
                if (raw[0] == 0) continue;
                result.Add(new DskFileEntry
                {
                    Key = slot.ToString(),
                    Name = SharpMzEncoding.ConvertMzfNameToASCIIString(raw.AsSpan(1, 17).ToArray()),
                    FileType = raw[0],
                    Locked = raw[18] != 0,
                    Size = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(20, 2)),
                    LoadAddress = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(22, 2)),
                    ExecuteAddress = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(24, 2)),
                    StartBlock = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(30, 2)),
                    Blocks = BlocksFor(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(20, 2)))
                });
            }
            return result;
        }

        public byte[] Extract(DskFileEntry entry)
        {
            if (entry.Size > ushort.MaxValue) throw new InvalidDataException("FSMZ entry size is invalid.");
            byte[] result = new byte[entry.Size];
            int copied = 0;
            for (int block = 0; copied < result.Length; block++)
            {
                byte[] source = ReadBlock(entry.StartBlock + block);
                int count = Math.Min(BlockSize, result.Length - copied);
                source.AsSpan(0, count).CopyTo(result.AsSpan(copied));
                copied += count;
            }
            return result;
        }

        public void Insert(string name, byte[] data, byte fileType = 1, ushort loadAddress = 0, ushort executeAddress = 0, int user = 0)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length > ushort.MaxValue) throw new InvalidDataException("FSMZ files cannot exceed 65535 bytes.");
            if (ReadDirectory().Any(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"FSMZ file '{name}' already exists.");
            int slot = Enumerable.Range(1, directoryLimit).FirstOrDefault(index => ReadDirectorySlot(index)[0] == 0);
            if (slot == 0) throw new IOException("The FSMZ directory is full.");
            int blocks = BlocksFor(data.Length);
            int start = FindContiguousFree(blocks);
            for (int index = 0; index < blocks; index++)
            {
                byte[] block = new byte[BlockSize];
                int offset = index * BlockSize;
                data.AsSpan(offset, Math.Min(BlockSize, data.Length - offset)).CopyTo(block);
                WriteBlock(start + index, block);
            }

            var raw = new byte[32];
            raw[0] = fileType;
            byte[] encoded = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(name);
            encoded.AsSpan(0, Math.Min(16, encoded.Length)).CopyTo(raw.AsSpan(1, 16));
            raw[1 + Math.Min(16, encoded.Length)] = 0x0D;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), checked((ushort)data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(22, 2), loadAddress);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(24, 2), executeAddress);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(30, 2), checked((ushort)start));
            WriteDirectorySlot(slot, raw);
            SetAllocated(start, blocks, true);
        }

        public void Delete(DskFileEntry entry, bool force = false)
        {
            if (entry.Locked && !force) throw new IOException($"FSMZ file '{entry.Name}' is locked.");
            byte[] raw = ReadDirectorySlot(int.Parse(entry.Key));
            raw[0] = 0;
            WriteDirectorySlot(int.Parse(entry.Key), raw);
            SetAllocated(entry.StartBlock, entry.Blocks, false);
        }

        public void Rename(DskFileEntry entry, string newName)
        {
            if (ReadDirectory().Any(candidate => candidate.Key != entry.Key && candidate.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"FSMZ file '{newName}' already exists.");
            byte[] raw = ReadDirectorySlot(int.Parse(entry.Key));
            raw.AsSpan(1, 17).Clear();
            byte[] encoded = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(newName);
            encoded.AsSpan(0, Math.Min(16, encoded.Length)).CopyTo(raw.AsSpan(1, 16));
            raw[1 + Math.Min(16, encoded.Length)] = 0x0D;
            WriteDirectorySlot(int.Parse(entry.Key), raw);
        }

        internal byte[] ReadIplPro() => ReadBlock(0);
        internal void WriteIplPro(ReadOnlySpan<byte> data) => WriteBlock(0, data);

        private bool DetectExtendedDirectory()
        {
            for (int slot = 64; slot <= 127; slot++) if (ReadDirectorySlot(slot)[0] != 0) return true;
            return false;
        }

        private void ValidateAllocations(int totalBlocks)
        {
            foreach (DskFileEntry entry in ReadDirectory())
            {
                if (entry.StartBlock < dinfo[1] || entry.StartBlock + entry.Blocks > totalBlocks)
                    warnings.Add($"Unsafe: '{entry.Name}' references blocks outside the file area.");
                else if (Enumerable.Range(0, entry.Blocks).Any(index => !IsAllocated(entry.StartBlock - dinfo[1] + index)))
                    warnings.Add($"Unsafe: DINFO bitmap does not cover all blocks of '{entry.Name}'.");
            }
        }

        private int FindContiguousFree(int count)
        {
            int available = Math.Min(2000, BinaryPrimitives.ReadUInt16LittleEndian(dinfo.AsSpan(4, 2)) + 1 - dinfo[1]);
            for (int relative = 0; relative <= available - count; relative++)
                if (Enumerable.Range(0, count).All(offset => !IsAllocated(relative + offset))) return dinfo[1] + relative;
            throw new IOException("The FSMZ disk has no sufficiently large contiguous free area.");
        }

        private bool IsAllocated(int relativeBlock) => relativeBlock >= 0 && relativeBlock < 2000 &&
            (dinfo[6 + relativeBlock / 8] & (1 << (relativeBlock & 7))) != 0;

        private void SetAllocated(int startBlock, int count, bool allocated)
        {
            int relative = startBlock - dinfo[1];
            if (relative < 0 || relative + count > 2000) throw new InvalidDataException("FSMZ allocation lies outside the DINFO bitmap.");
            for (int index = 0; index < count; index++)
            {
                int bit = relative + index;
                if (allocated) dinfo[6 + bit / 8] |= (byte)(1 << (bit & 7));
                else dinfo[6 + bit / 8] &= (byte)~(1 << (bit & 7));
            }
            int used = BinaryPrimitives.ReadUInt16LittleEndian(dinfo.AsSpan(2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(dinfo.AsSpan(2, 2), checked((ushort)(allocated ? used + count : used - count)));
            WriteBlock(DinfoBlock, dinfo);
        }

        private byte[] ReadDirectorySlot(int slot)
        {
            byte[] block = ReadBlock(DirectoryStart + slot / 8);
            return block.AsSpan((slot & 7) * 32, 32).ToArray();
        }

        private void WriteDirectorySlot(int slot, ReadOnlySpan<byte> value)
        {
            int blockNumber = DirectoryStart + slot / 8;
            byte[] block = ReadBlock(blockNumber);
            value.CopyTo(block.AsSpan((slot & 7) * 32, 32));
            WriteBlock(blockNumber, block);
        }

        private byte[] ReadBlock(int block)
        {
            int absoluteTrack = (block / 16) ^ 1;
            return device.ReadSector(absoluteTrack, block % 16 + 1, inverted: true);
        }

        private void WriteBlock(int block, ReadOnlySpan<byte> data)
        {
            int absoluteTrack = (block / 16) ^ 1;
            device.WriteSector(absoluteTrack, block % 16 + 1, data, inverted: true);
        }

        private static int BlocksFor(long size) => Math.Max(1, checked((int)((size + BlockSize - 1) / BlockSize)));
    }
}
