using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools
{
    internal enum MultiGameMetadataLayout { MenuFooter, IplProComment }

    internal sealed record MultiGameMetadata(MultiGameMetadataLayout Layout, int Offset,
        int Version, int EntrySize, int EntryCount, int TableOffset, int MenuSectorCount);

    // Native MZTools multi-game IPL format (QDMG). The container remains a
    // normal inverted MZ-800 IPL disk; this layer exposes its embedded menu.
    internal sealed class MultiGameIplFileSystem : IDskFileSystem
    {
        private const int EntrySize = Mz800MultiGameIplDskWriter.EntrySize;
        // Verified against multi.dsk: historical metadata occupies exactly
        // decoded IPLPRO bytes 0x20..0x2B. Never scan arbitrary comment text.
        internal const int IplProMetadataOffset = 0x20;
        private readonly DskImage image;
        private readonly List<DskFileEntry> entries;
        private readonly List<MultiGameIplInput> inputs;
        private readonly int usedBlocks;
        private bool canConfigure = true;

        private MultiGameIplFileSystem(DskImage image)
        {
            if (!FsmzFileSystem.HasGeometry(image))
                throw new InvalidDataException("The image does not use MZ-800 IPL geometry.");
            if (image.Tracks.Any(track => track == null || !track.Sectors.Select(sector => (int)sector.SectorId).Order().SequenceEqual(Enumerable.Range(1, 16))))
                throw new InvalidDataException("The multi-game IPL sector map is missing or ambiguous.");
            this.image = image;
            byte[] ipl = ReadBlock(0);
            if (ipl[0] != 3 || !ipl.AsSpan(1, 6).SequenceEqual("IPLPRO"u8))
                throw new InvalidDataException("The MZTools multi-game IPL signature is missing.");

            int menuSize = BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x14, 2));
            if (menuSize < Mz800MultiGameIplDskWriter.MultiGameFooterSize)
                throw new InvalidDataException("The MZTools multi-game IPL menu is too short.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x1E, 2)) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(ipl.AsSpan(0x16, 2)) + menuSize > 0x10000)
                throw new InvalidDataException("The multi-game IPL menu load range or start block is invalid.");

            byte[] menu = ReadBytes(1, menuSize);
            Metadata = ReadMetadata(ipl, menu);
            int entryCount = Metadata.EntryCount, tableOffset = Metadata.TableOffset, menuSectors = Metadata.MenuSectorCount;

            entries = new List<DskFileEntry>(entryCount);
            inputs = new List<MultiGameIplInput>(entryCount);
            var occupied = new HashSet<int>(Enumerable.Range(0, 1 + menuSectors));
            for (int index = 0; index < entryCount; index++)
            {
                ReadOnlySpan<byte> raw = menu.AsSpan(tableOffset + index * EntrySize, EntrySize);
                int startBlock = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(16, 2));
                int size = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(18, 2));
                int blocks = BlocksFor(size);
                if (size < 1 || startBlock < 1 + menuSectors || startBlock + blocks > Mz800DskImage.LogicalSectorCount)
                    throw new InvalidDataException($"Multi-game IPL entry {index + 1} references blocks outside the image.");
                if (BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(20, 2)) + size > 0x10000)
                    throw new InvalidDataException($"Multi-game IPL entry {index + 1} exceeds the 16-bit load area.");
                for (int block = startBlock; block < startBlock + blocks; block++)
                    if (!occupied.Add(block)) throw new InvalidDataException($"Multi-game IPL entry {index + 1} overlaps another payload.");

                string name = SharpMzEncoding.ConvertMzfNameToASCIIString(raw[..16].ToArray()).TrimEnd();
                byte compression = raw[25];
                var entry = new DskFileEntry
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
                };
                entries.Add(entry);
                byte[] payload = ReadBytes(startBlock, size);
                MzfCompressionOptions compressionOptions = compression switch
                {
                    0 => new MzfCompressionOptions(MzfCompressionAlgorithm.None),
                    1 => new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0),
                    2 => new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7),
                    _ => new MzfCompressionOptions(MzfCompressionAlgorithm.None)
                };
                if (compression > 2 || raw[24] != (compression == 0 ? 0 : 1) || size > Mz800IplDskWriter.MaxIplStagedSize)
                    canConfigure = false;
                inputs.Add(new MultiGameIplInput(
                    CreateRecord(entry, payload), entry.Name, compressionOptions, payload.Length));
            }
            usedBlocks = occupied.Count;
        }

        public DskFileSystemType Type => DskFileSystemType.MultiIpl;
        public string DisplayName => "MZTools multi-game IPL";
        internal MultiGameMetadata Metadata { get; }
        internal string MetadataLocationDescription => Metadata.Layout == MultiGameMetadataLayout.MenuFooter
            ? $"QDMG metadata: menu footer at byte offset 0x{Metadata.Offset:X}; logical block {1 + Metadata.Offset / 256}, offset 0x{Metadata.Offset % 256:X2}. Entry table: menu offset 0x{Metadata.TableOffset:X}."
            : $"QDMG metadata: IPLPRO logical block 0, byte offset 0x{Metadata.Offset:X2}. Entry table: menu offset 0x{Metadata.TableOffset:X}.";
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

        internal bool CanConfigure => canConfigure;

        internal IReadOnlyList<MultiGameIplInput> GetInputs()
        {
            if (!CanConfigure)
                throw new NotSupportedException("This multi-program IPL image uses unsupported compression, flags or payload dimensions and cannot be safely rebuilt.");
            return inputs.Select(input => input with { Record = input.Record.DeepClone() }).ToList();
        }

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

        private static MultiGameMetadata ReadMetadata(byte[] ipl, byte[] menu)
        {
            int footer = Mz800MultiGameIplDskWriter.FindMenuFooterOffset(menu);
            // A present menu footer is authoritative. Invalid current metadata
            // must not be masked by unrelated IPLPRO comment bytes.
            var layout = footer >= 0 ? MultiGameMetadataLayout.MenuFooter : MultiGameMetadataLayout.IplProComment;
            int offset = footer >= 0 ? footer : IplProMetadataOffset;
            ReadOnlySpan<byte> raw = (footer >= 0 ? menu : ipl).AsSpan(offset, Mz800MultiGameIplDskWriter.MultiGameFooterSize);
            if (!raw[..4].SequenceEqual("QDMG"u8))
                throw new InvalidDataException("The MZTools multi-game IPL signature is missing.");
            var metadata = new MultiGameMetadata(layout, offset, raw[4], raw[5],
                BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(8, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(10, 2)));
            if (metadata.Version != Mz800MultiGameIplDskWriter.FormatVersion || metadata.EntrySize != EntrySize)
                throw new InvalidDataException($"Unsupported QDMG version {metadata.Version} or entry size {metadata.EntrySize}.");
            if (metadata.EntryCount is < 1 or > Mz800MultiGameIplDskWriter.MaxEntries || metadata.MenuSectorCount != BlocksFor(menu.Length))
                throw new InvalidDataException("The QDMG header contains invalid menu dimensions.");
            int tableLimit = footer >= 0 ? footer : menu.Length;
            if (metadata.TableOffset > tableLimit - metadata.EntryCount * EntrySize)
                throw new InvalidDataException("The QDMG entry table lies outside the menu program.");
            return metadata;
        }

        private static TapeRecord CreateRecord(DskFileEntry entry, byte[] payload)
        {
            var header = new MZQFileHeader
            {
                StartSign = MzfFormatSupport.ExpectedStartSign.ToArray(),
                MzfHeaderSign = 0,
                DataSize = 0x40,
                MzfFtype = entry.FileType == 0 ? (byte)1 : entry.FileType,
                MzfFname = new byte[16],
                MzfFnameEnd = 0x0D,
                Unused1 = MzfFormatSupport.ExpectedUnused.ToArray(),
                MzfSize = checked((ushort)payload.Length),
                MzfStart = entry.LoadAddress,
                MzfExec = entry.ExecuteAddress,
                MzfHeaderDescription = new byte[104],
                Crc = MzfFormatSupport.ExpectedCrc.ToArray()
            };
            byte[] encodedName = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(entry.Name);
            encodedName.AsSpan(0, Math.Min(encodedName.Length, header.MzfFname.Length)).CopyTo(header.MzfFname);
            var body = new MZQFileBody
            {
                StartSign = MzfFormatSupport.ExpectedStartSign.ToArray(),
                MzfBodySign = 0x05,
                DataSize = checked((ushort)payload.Length),
                MzfBody = (byte[])payload.Clone(),
                Crc = MzfFormatSupport.ExpectedCrc.ToArray(),
                TrailingData = Array.Empty<byte>()
            };
            return TapeRecord.FromLegacy(header, body);
        }
    }
}
