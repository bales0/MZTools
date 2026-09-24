using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QDTool
{
    // C# port of bales0/mzdisk src/libs/mzdsk_cpm (GPL-3.0-or-later).
    internal readonly record struct CpmSectorAddress(int AbsoluteTrack, int SectorId);

    internal sealed record CpmDpb(
        ushort Spt, byte Bsh, byte Blm, byte Exm, ushort Dsm, ushort Drm,
        byte Al0, byte Al1, ushort Cks, ushort Off, ushort BlockSize, string Name,
        IReadOnlyList<int>? PhysicalTrackMap = null,
        IReadOnlyList<CpmSectorAddress>? PhysicalSectorMap = null,
        bool Inverted = false)
    {
        internal static CpmDpb Dd { get; } = new(36, 4, 15, 0, 350, 127, 0xC0, 0, 32, 4, 2048, "LEC CP/M DD (720 KiB)");
        internal static CpmDpb Hd { get; } = new(72, 5, 31, 1, 350, 127, 0xC0, 0, 32, 4, 4096, "CP/M HD");
        internal static CpmDpb PersonalCpm80 { get; } = new(
            32, 4, 15, 1, 155, 63, 0x80, 0, 16, 1, 2048, "P-CP/M80 (MZ-2Z047)",
            Enumerable.Range(0, 40).Select(cylinder => cylinder * 2)
                .Concat(Enumerable.Range(1, 39).Reverse().Select(cylinder => cylinder * 2 + 1))
                .ToArray());
        internal static CpmDpb Sds400 { get; } = new(
            80, 4, 15, 1, 191, 127, 0xC0, 0, 32, 1, 2048, "P-CP/M80 SDS 400K",
            PhysicalSectorMap: Enumerable.Range(1, 39)
                .SelectMany(cylinder => Enumerable.Range(1, 10)
                    .Select(sector => new CpmSectorAddress(cylinder * 2 + 1, sector))
                    .Concat(Enumerable.Range(11, 10)
                        .Select(sector => new CpmSectorAddress(cylinder * 2, sector))))
                .ToArray(),
            Inverted: true);

        internal static IEnumerable<CpmDpb> PresetsFor(DskImage image)
        {
            if (LooksLikeSds400Geometry(image) && LooksLikePersonalCpmBoot(image)) yield return Sds400;
            if (image.Tracks.Where(track => track != null).Any(track => track!.Sectors.Count == 8) && LooksLikePersonalCpmBoot(image))
                yield return PersonalCpm80;
            if (image.Tracks.Where(track => track != null).Any(track => track!.Sectors.Count == 9)) yield return CreateLec(image, highDensity: false);
            if (image.Tracks.Where(track => track != null).Any(track => track!.Sectors.Count == 18)) yield return CreateLec(image, highDensity: true);
        }

        internal static CpmDpb CreateLec(DskImage image, bool highDensity)
        {
            int sectors = highDensity ? 18 : 9;
            int blockSize = highDensity ? 4096 : 2048;
            int bsh = highDensity ? 5 : 4;
            const int off = 4;
            int blocks = Math.Max(1, (image.Tracks.Count - off) * sectors * 512 / blockSize);
            int dsm = blocks - 1;
            bool widePointers = dsm > 255;
            int pointerCount = widePointers ? 8 : 16;
            int exm = Math.Max(0, pointerCount * blockSize / 16384 - 1);
            long rawBytes = (long)image.Tracks.Count * sectors * 512;
            string capacity = rawBytes % (1024 * 1024) == 0
                ? $"{rawBytes / (1024 * 1024.0):0.##} MiB"
                : $"{rawBytes / 1024} KiB";
            string name = image.Tracks.Count == 160
                ? highDensity ? "CP/M HD" : "LEC CP/M DD (720 KiB)"
                : $"LEC CP/M {(highDensity ? "HD" : "DD")} ({capacity}, {image.SideCount} side{(image.SideCount == 1 ? "" : "s")})";
            return new CpmDpb(
                checked((ushort)(sectors * 4)), checked((byte)bsh), checked((byte)((1 << bsh) - 1)), checked((byte)exm),
                checked((ushort)dsm), 127, 0xC0, 0, 32, off, checked((ushort)blockSize), name);
        }

        private static bool LooksLikePersonalCpmBoot(DskImage image)
        {
            if (image.Tracks.Count <= 1 || image.Tracks[1] == null) return false;
            DskImage.DskSector? sector = image.Tracks[1]!.Sectors.FirstOrDefault(candidate => candidate.SectorId == 1);
            if (sector == null || sector.Data.Length != 256) return false;
            byte[] logical = sector.Data.Select(value => (byte)(value ^ 0xFF)).ToArray();
            if (logical[0] != 3 || !logical.AsSpan(1, 6).SequenceEqual("IPLPRO"u8)) return false;
            string name = SharpMzEncoding.ConvertMzfNameToASCIIString(logical.AsSpan(7, 13).ToArray());
            return name.Contains("P-CP/M", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeSds400Geometry(DskImage image)
        {
            if (image.TrackCount != 40 || image.SideCount != 2 || image.Tracks.Count != 80) return false;
            for (int absoluteTrack = 0; absoluteTrack < image.Tracks.Count; absoluteTrack++)
            {
                if (absoluteTrack == 1) continue;
                DskImage.DskTrack? track = image.Tracks[absoluteTrack];
                if (track == null || track.Sectors.Count != 10 || track.Sectors.Any(sector => sector.Data.Length != 512)) return false;
                int firstId = (absoluteTrack & 1) == 0 ? 11 : 1;
                if (!track.Sectors.Select(sector => (int)sector.SectorId).Order().SequenceEqual(Enumerable.Range(firstId, 10))) return false;
            }
            return true;
        }
    }

    internal sealed class CpmFileSystem : IDskFileSystem
    {
        private readonly DskBlockDevice device;
        private readonly List<string> warnings = new();

        private CpmFileSystem(DskImage image, CpmDpb dpb)
        {
            device = new DskBlockDevice(image);
            Dpb = dpb;
            ValidateDirectory(ReadRawDirectory());
        }

        internal CpmDpb Dpb { get; }
        public DskFileSystemType Type => DskFileSystemType.Cpm;
        public string DisplayName => Dpb.Name;
        public bool IsReadOnly => warnings.Count != 0;
        public IReadOnlyList<string> Warnings => warnings;
        public long UsedBytes => GetAllocatedBlocks().Count * Dpb.BlockSize;
        public long FreeBytes => (Dpb.Dsm + 1L - GetAllocatedBlocks().Count) * Dpb.BlockSize;

        internal static bool TryOpen(DskImage image, CpmDpb dpb, out CpmFileSystem? result)
        {
            try
            {
                result = new CpmFileSystem(image, dpb);
                return result.warnings.Count == 0;
            }
            catch (InvalidDataException)
            {
                result = null;
                return false;
            }
        }

        internal static void Format(DskImage image, CpmDpb dpb)
        {
            var fs = new CpmFileSystem(image, dpb);
            fs.FormatDirectory();
        }

        public IReadOnlyList<DskFileEntry> ReadDirectory()
        {
            byte[][] entries = ReadRawDirectory();
            return entries.Select((raw, index) => (raw, index))
                .Where(pair => pair.raw[0] <= 15)
                .GroupBy(pair => $"{pair.raw[0]}:{NamePart(pair.raw, 1, 8)}.{NamePart(pair.raw, 9, 3)}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var extents = group.OrderBy(pair => ExtentNumber(pair.raw)).ToList();
                    byte[] first = extents[0].raw;
                    long size = extents.Max(pair =>
                    {
                        int logical = ExtentNumber(pair.raw);
                        return (long)(logical & ~Dpb.Exm) * 16384 + ((logical & Dpb.Exm) * 128L + pair.raw[15]) * 128;
                    });
                    return new DskFileEntry
                    {
                        Key = group.Key,
                        User = first[0],
                        Name = NamePart(first, 1, 8),
                        Extension = NamePart(first, 9, 3),
                        Size = size,
                        ReadOnly = (first[9] & 0x80) != 0,
                        System = (first[10] & 0x80) != 0,
                        Archived = (first[11] & 0x80) != 0,
                        Extents = extents.Count,
                        Blocks = extents.Sum(pair => AllocationBlocks(pair.raw).Count())
                    };
                }).OrderBy(entry => entry.User).ThenBy(entry => entry.Name).ThenBy(entry => entry.Extension).ToList();
        }

        public byte[] Extract(DskFileEntry entry)
        {
            List<byte[]> extents = ReadRawDirectory()
                .Where(raw => Matches(raw, entry))
                .OrderBy(ExtentNumber)
                .ToList();
            if (extents.Count == 0) throw new FileNotFoundException($"CP/M file '{entry.Name}.{entry.Extension}' was not found.");
            using var output = new MemoryStream();
            for (int extentIndex = 0; extentIndex < extents.Count; extentIndex++)
            {
                byte[] extent = extents[extentIndex];
                int bytes = extentIndex < extents.Count - 1
                    ? (Dpb.Dsm <= 255 ? 16 : 8) * Dpb.BlockSize
                    : ((ExtentNumber(extent) & Dpb.Exm) * 128 + extent[15]) * 128;
                foreach (int block in AllocationBlocks(extent))
                {
                    if (bytes <= 0) break;
                    byte[] data = ReadBlock(block);
                    int count = Math.Min(bytes, data.Length);
                    output.Write(data, 0, count);
                    bytes -= count;
                }
            }
            byte[] result = output.ToArray();
            return result.Length > entry.Size ? result[..checked((int)entry.Size)] : result;
        }

        public void Insert(string name, byte[] data, byte fileType = 1, ushort loadAddress = 0, ushort executeAddress = 0, int user = 0)
        {
            if (user is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(user));
            SplitName(name, out string baseName, out string extension);
            if (ReadDirectory().Any(entry => entry.User == user && entry.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase) && entry.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"CP/M file '{baseName}.{extension}' already exists in user area {user}.");

            byte[][] directory = ReadRawDirectory();
            int blocksPerEntry = Dpb.Dsm <= 255 ? 16 : 8;
            int bytesPerEntry = blocksPerEntry * Dpb.BlockSize;
            int entryCount = Math.Max(1, (data.Length + bytesPerEntry - 1) / bytesPerEntry);
            int[] freeSlots = directory.Select((raw, index) => (raw, index)).Where(pair => pair.raw[0] == 0xE5).Select(pair => pair.index).Take(entryCount).ToArray();
            if (freeSlots.Length != entryCount) throw new IOException("The CP/M directory is full.");
            HashSet<int> allocated = GetAllocatedBlocks(directory);
            List<int> freeBlocks = Enumerable.Range(0, Dpb.Dsm + 1).Where(block => !allocated.Contains(block)).ToList();
            int neededBlocks = Math.Max(1, (data.Length + Dpb.BlockSize - 1) / Dpb.BlockSize);
            if (freeBlocks.Count < neededBlocks) throw new IOException("The CP/M disk is full.");

            int sourceOffset = 0;
            int freeBlockIndex = 0;
            for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                int extentBytes = Math.Min(bytesPerEntry, data.Length - sourceOffset);
                int records = Math.Max(1, (extentBytes + 127) / 128);
                int subExtent = records > 128 ? (records - 1) / 128 : 0;
                int logicalExtent = entryIndex * (Dpb.Exm + 1) + subExtent;
                var raw = new byte[32];
                raw[0] = (byte)user;
                WriteNamePart(raw, 1, 8, baseName);
                WriteNamePart(raw, 9, 3, extension);
                raw[12] = (byte)(logicalExtent & 0x1F);
                raw[14] = (byte)(logicalExtent >> 5);
                raw[15] = (byte)(records - subExtent * 128);
                int extentBlocks = Math.Max(1, (extentBytes + Dpb.BlockSize - 1) / Dpb.BlockSize);
                for (int blockPointer = 0; blockPointer < extentBlocks; blockPointer++)
                {
                    int block = freeBlocks[freeBlockIndex++];
                    SetAllocationBlock(raw, blockPointer, block);
                    var buffer = Enumerable.Repeat((byte)0x1A, Dpb.BlockSize).ToArray();
                    int count = Math.Max(0, Math.Min(Dpb.BlockSize, data.Length - sourceOffset));
                    if (count > 0) data.AsSpan(sourceOffset, count).CopyTo(buffer);
                    WriteBlock(block, buffer);
                    sourceOffset += count;
                }
                WriteDirectoryEntry(freeSlots[entryIndex], raw);
            }
        }

        public void Delete(DskFileEntry entry, bool force = false)
        {
            byte[][] directory = ReadRawDirectory();
            bool found = false;
            for (int index = 0; index < directory.Length; index++)
            {
                if (!Matches(directory[index], entry)) continue;
                directory[index][0] = 0xE5;
                WriteDirectoryEntry(index, directory[index]);
                found = true;
            }
            if (!found) throw new FileNotFoundException($"CP/M file '{entry.Name}.{entry.Extension}' was not found.");
        }

        public void Rename(DskFileEntry entry, string newName)
        {
            SplitName(newName, out string baseName, out string extension);
            foreach ((byte[] raw, int index) in ReadRawDirectory().Select((raw, index) => (raw, index)))
            {
                if (!Matches(raw, entry)) continue;
                bool ro = (raw[9] & 0x80) != 0, sys = (raw[10] & 0x80) != 0, arc = (raw[11] & 0x80) != 0;
                WriteNamePart(raw, 1, 8, baseName);
                WriteNamePart(raw, 9, 3, extension);
                if (ro) raw[9] |= 0x80;
                if (sys) raw[10] |= 0x80;
                if (arc) raw[11] |= 0x80;
                WriteDirectoryEntry(index, raw);
            }
        }

        internal void SetAttributes(DskFileEntry entry, bool readOnly, bool system, bool archived)
        {
            foreach ((byte[] raw, int index) in ReadRawDirectory().Select((raw, index) => (raw, index)))
            {
                if (!Matches(raw, entry)) continue;
                raw[9] = (byte)((raw[9] & 0x7F) | (readOnly ? 0x80 : 0));
                raw[10] = (byte)((raw[10] & 0x7F) | (system ? 0x80 : 0));
                raw[11] = (byte)((raw[11] & 0x7F) | (archived ? 0x80 : 0));
                WriteDirectoryEntry(index, raw);
            }
        }

        internal void FormatDirectory()
        {
            int allocation = (Dpb.Al0 << 8) | Dpb.Al1;
            for (int bit = 15; bit >= 0; bit--)
                if ((allocation & (1 << bit)) != 0) WriteBlock(15 - bit, Enumerable.Repeat((byte)0xE5, Dpb.BlockSize).ToArray());
        }

        private void ValidateDirectory(byte[][] directory)
        {
            foreach (byte[] entry in directory)
            {
                if (entry[0] == 0xE5 || entry[0] > 15) continue;
                if (entry[15] > 0x80 || entry[12] > 0x1F) throw new InvalidDataException("CP/M directory contains an invalid extent.");
                foreach (int block in AllocationBlocks(entry))
                    if (block > Dpb.Dsm) warnings.Add($"Unsafe: CP/M extent references block {block} beyond DSM={Dpb.Dsm}.");
            }
        }

        private byte[][] ReadRawDirectory()
        {
            var result = new List<byte[]>();
            int allocation = (Dpb.Al0 << 8) | Dpb.Al1;
            for (int bit = 15; bit >= 0 && result.Count <= Dpb.Drm; bit--)
            {
                if ((allocation & (1 << bit)) == 0) continue;
                byte[] block = ReadBlock(15 - bit);
                for (int offset = 0; offset < block.Length && result.Count <= Dpb.Drm; offset += 32)
                    result.Add(block.AsSpan(offset, 32).ToArray());
            }
            if (result.Count == 0) throw new InvalidDataException("CP/M directory allocation is empty.");
            return result.ToArray();
        }

        private byte[] ReadBlock(int block)
        {
            var output = new byte[Dpb.BlockSize];
            int written = 0;
            while (written < output.Length)
            {
                (int absoluteTrack, int physicalSector, int offset) = MapByteOffset(block, written);
                byte[] sector = device.ReadSector(absoluteTrack, physicalSector, Dpb.Inverted);
                if (sector.Length != 512) throw new InvalidDataException("CP/M requires 512-byte physical sectors.");
                int count = Math.Min(512 - offset, output.Length - written);
                sector.AsSpan(offset, count).CopyTo(output.AsSpan(written));
                written += count;
            }
            return output;
        }

        private void WriteBlock(int block, ReadOnlySpan<byte> data)
        {
            int consumed = 0;
            while (consumed < data.Length)
            {
                (int absoluteTrack, int physicalSector, int offset) = MapByteOffset(block, consumed);
                byte[] sector = device.ReadSector(absoluteTrack, physicalSector, Dpb.Inverted);
                int count = Math.Min(512 - offset, data.Length - consumed);
                data.Slice(consumed, count).CopyTo(sector.AsSpan(offset));
                device.WriteSector(absoluteTrack, physicalSector, sector, Dpb.Inverted);
                consumed += count;
            }
        }

        private (int AbsoluteTrack, int PhysicalSector, int Offset) MapByteOffset(int block, int offsetInBlock)
        {
            int byteOffset = checked(block * Dpb.BlockSize + offsetInBlock);
            int logicalSector = byteOffset / 128;
            int logicalTrack = logicalSector / Dpb.Spt + Dpb.Off;
            int sectorInTrack = logicalSector % Dpb.Spt;
            int offset = sectorInTrack % 4 * 128;
            if (Dpb.PhysicalSectorMap != null)
            {
                int sectorIndex = checked((logicalTrack - Dpb.Off) * (Dpb.Spt / 4) + sectorInTrack / 4);
                if ((uint)sectorIndex >= Dpb.PhysicalSectorMap.Count)
                    throw new InvalidDataException($"CP/M logical sector {logicalSector} lies outside the {Dpb.Name} sector map.");
                CpmSectorAddress address = Dpb.PhysicalSectorMap[sectorIndex];
                return (address.AbsoluteTrack, address.SectorId, offset);
            }
            int absoluteTrack = Dpb.PhysicalTrackMap == null
                ? logicalTrack
                : logicalTrack < Dpb.PhysicalTrackMap.Count
                    ? Dpb.PhysicalTrackMap[logicalTrack]
                    : throw new InvalidDataException($"CP/M logical track {logicalTrack} lies outside the {Dpb.Name} track map.");
            return (absoluteTrack, sectorInTrack / 4 + 1, offset);
        }

        private void WriteDirectoryEntry(int index, byte[] entry)
        {
            int entriesPerBlock = Dpb.BlockSize / 32;
            int blockOrdinal = index / entriesPerBlock;
            int allocation = (Dpb.Al0 << 8) | Dpb.Al1;
            int block = Enumerable.Range(0, 16).Where(i => (allocation & (1 << (15 - i))) != 0).ElementAt(blockOrdinal);
            byte[] content = ReadBlock(block);
            entry.CopyTo(content, index % entriesPerBlock * 32);
            WriteBlock(block, content);
        }

        private HashSet<int> GetAllocatedBlocks(byte[][]? directory = null)
        {
            directory ??= ReadRawDirectory();
            var result = new HashSet<int>();
            int allocation = (Dpb.Al0 << 8) | Dpb.Al1;
            for (int bit = 15; bit >= 0; bit--) if ((allocation & (1 << bit)) != 0) result.Add(15 - bit);
            foreach (byte[] entry in directory.Where(raw => raw[0] != 0xE5))
                foreach (int block in AllocationBlocks(entry)) result.Add(block);
            return result;
        }

        private IEnumerable<int> AllocationBlocks(byte[] entry)
        {
            if (Dpb.Dsm <= 255)
            {
                for (int index = 16; index < 32; index++) if (entry[index] != 0) yield return entry[index];
            }
            else
            {
                for (int index = 16; index < 32; index += 2)
                {
                    int block = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(index, 2));
                    if (block != 0) yield return block;
                }
            }
        }

        private void SetAllocationBlock(byte[] entry, int pointer, int block)
        {
            if (Dpb.Dsm <= 255) entry[16 + pointer] = checked((byte)block);
            else BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(16 + pointer * 2, 2), checked((ushort)block));
        }

        private static bool Matches(byte[] raw, DskFileEntry entry) => raw[0] == entry.User &&
            NamePart(raw, 1, 8).Equals(entry.Name, StringComparison.OrdinalIgnoreCase) &&
            NamePart(raw, 9, 3).Equals(entry.Extension, StringComparison.OrdinalIgnoreCase);
        // S2 bit 7 is an internal CP/M flag and is not part of the extent number.
        private static int ExtentNumber(byte[] raw) => (raw[14] & 0x3F) * 32 + raw[12];
        private static string NamePart(byte[] raw, int offset, int length) => Encoding.ASCII.GetString(raw.Skip(offset).Take(length).Select(value => (byte)(value & 0x7F)).ToArray()).TrimEnd(' ');
        private static void WriteNamePart(byte[] raw, int offset, int length, string value)
        {
            raw.AsSpan(offset, length).Fill((byte)' ');
            Encoding.ASCII.GetBytes(value.ToUpperInvariant()).AsSpan(0, Math.Min(length, value.Length)).CopyTo(raw.AsSpan(offset, length));
        }
        private static void SplitName(string name, out string baseName, out string extension)
        {
            string file = Path.GetFileName(name).ToUpperInvariant();
            extension = Path.GetExtension(file).TrimStart('.');
            baseName = Path.GetFileNameWithoutExtension(file);
            if (baseName.Length is < 1 or > 8 || extension.Length > 3) throw new InvalidDataException("CP/M filenames must use the 8.3 format.");
        }
    }
}
