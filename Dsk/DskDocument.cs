using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QDTool
{
    internal enum DskFileSystemType
    {
        Fsmz,
        Cpm,
        Mrs,
        BootOnly,
        Raw
    }

    internal sealed class DskFileEntry
    {
        public required string Key { get; init; }
        public string Name { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public long Size { get; set; }
        public byte FileType { get; set; }
        public ushort LoadAddress { get; set; }
        public ushort ExecuteAddress { get; set; }
        public int StartBlock { get; set; }
        public int Blocks { get; set; }
        public int Extents { get; set; }
        public int User { get; set; }
        public bool Locked { get; set; }
        public bool ReadOnly { get; set; }
        public bool System { get; set; }
        public bool Archived { get; set; }
        public string Notes { get; set; } = string.Empty;
    }

    internal interface IDskFileSystem
    {
        DskFileSystemType Type { get; }
        string DisplayName { get; }
        bool IsReadOnly { get; }
        long UsedBytes { get; }
        long FreeBytes { get; }
        IReadOnlyList<string> Warnings { get; }
        IReadOnlyList<DskFileEntry> ReadDirectory();
        byte[] Extract(DskFileEntry entry);
        void Insert(string name, byte[] data, byte fileType = 1, ushort loadAddress = 0, ushort executeAddress = 0, int user = 0);
        void Delete(DskFileEntry entry, bool force = false);
        void Rename(DskFileEntry entry, string newName);
    }

    internal sealed class DskDocument
    {
        private byte[] originalBytes;

        internal DskDocument(DskImage image, byte[] originalBytes, string? path, IDskFileSystem fileSystem)
        {
            Image = image;
            this.originalBytes = originalBytes;
            FilePath = path;
            FileSystem = fileSystem;
        }

        internal DskImage Image { get; }
        internal IDskFileSystem FileSystem { get; private set; }
        internal string? FilePath { get; private set; }
        internal bool IsModified { get; private set; }
        internal bool IsReadOnly => FileSystem.IsReadOnly;

        internal static DskDocument Open(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Open(bytes, path);
        }

        internal static DskDocument Open(byte[] bytes, string? path = null)
        {
            byte[] original = (byte[])bytes.Clone();
            DskImage image = DskImage.Parse(bytes);
            IDskFileSystem fileSystem = DskFileSystemDetector.Detect(image);
            return new DskDocument(image, original, path, fileSystem);
        }

        internal byte[] Serialize() => IsModified ? Image.Serialize() : (byte[])originalBytes.Clone();

        internal void MarkModified()
        {
            if (IsReadOnly)
            {
                throw new InvalidOperationException("This disk was opened read-only because its filesystem is inconsistent or unknown.");
            }
            IsModified = true;
        }

        internal void Save(string? path = null)
        {
            string destination = path ?? FilePath ?? throw new InvalidOperationException("The DSK document has no output path.");
            byte[] savedBytes = Image.Serialize();
            File.WriteAllBytes(destination, savedBytes);
            originalBytes = savedBytes;
            FilePath = destination;
            IsModified = false;
        }
    }

    internal static class DskDocumentFactory
    {
        internal static DskDocument CreateFsmz(bool ipldisk = true)
        {
            DskImage image = DskImage.CreateUniform(40, 2, 16, 256, 1, 0x2A, 0xFF, "MZTools");
            FsmzFileSystem.Format(image, ipldisk);
            byte[] bytes = image.Serialize();
            return new DskDocument(image, bytes, null, new FsmzFileSystem(image, ipldisk));
        }

        internal static DskDocument CreateCpm(bool highDensity)
        {
            int sectors = highDensity ? 18 : 9;
            DskImage image = DskImage.CreateSharpBootDataDisk(80, 2, sectors, highDensity, "MZTools");
            CpmFileSystem.Format(image, highDensity ? CpmDpb.Hd : CpmDpb.Sd);
            return DskDocument.Open(image.Serialize());
        }

        internal static DskDocument CreateMrs()
        {
            DskImage image = DskImage.CreateSharpBootDataDisk(80, 2, 9, highDensityInterleave: false, "MZTools");
            MrsFileSystem.Format(image);
            return DskDocument.Open(image.Serialize());
        }

        internal static DskDocument CreateRaw(int tracks, int sides, int sectors, int sectorSize, int firstSectorId, byte gap, byte filler, string creator) =>
            DskDocument.Open(DskImage.CreateUniform(tracks, sides, sectors, sectorSize, firstSectorId, gap, filler, creator).Serialize());
    }

    internal sealed class DskBlockDevice
    {
        private readonly DskImage image;

        internal DskBlockDevice(DskImage image) => this.image = image;

        internal int AbsoluteTrackCount => image.Tracks.Count;

        internal DskImage.DskSector GetSector(int absoluteTrack, int sectorId)
        {
            if ((uint)absoluteTrack >= image.Tracks.Count)
            {
                throw new InvalidDataException($"Absolute track {absoluteTrack} lies outside this image.");
            }
            DskImage.DskTrack track = image.Tracks[absoluteTrack] ??
                throw new InvalidDataException($"Absolute track {absoluteTrack} is missing (tsize=0).");
            return track.Sectors.FirstOrDefault(candidate => candidate.SectorId == sectorId) ??
                throw new InvalidDataException($"Sector ID {sectorId} was not found on absolute track {absoluteTrack}.");
        }

        internal byte[] ReadSector(int absoluteTrack, int sectorId, bool inverted = false)
        {
            byte[] result = (byte[])GetSector(absoluteTrack, sectorId).Data.Clone();
            if (inverted) Invert(result);
            return result;
        }

        internal void WriteSector(int absoluteTrack, int sectorId, ReadOnlySpan<byte> data, bool inverted = false)
        {
            DskImage.DskSector sector = GetSector(absoluteTrack, sectorId);
            if (data.Length != sector.Data.Length)
            {
                throw new InvalidDataException($"Sector C/H index {absoluteTrack}, ID {sectorId} requires {sector.Data.Length} bytes, got {data.Length}.");
            }
            data.CopyTo(sector.Data);
            if (inverted) Invert(sector.Data);
        }

        internal byte[] ReadLinear512Block(int block, bool inverted = false)
        {
            int track = block / 9;
            int sector = block % 9 + 1;
            byte[] data = ReadSector(track, sector, inverted);
            if (data.Length != 512) throw new InvalidDataException($"Block {block} does not address a 512-byte sector.");
            return data;
        }

        internal void WriteLinear512Block(int block, ReadOnlySpan<byte> data, bool inverted = false)
        {
            int track = block / 9;
            int sector = block % 9 + 1;
            WriteSector(track, sector, data, inverted);
        }

        internal static void Invert(Span<byte> data)
        {
            for (int index = 0; index < data.Length; index++) data[index] ^= 0xFF;
        }
    }

    internal static class DskFileSystemDetector
    {
        internal static IDskFileSystem Detect(DskImage image)
        {
            var device = new DskBlockDevice(image);
            if (FsmzFileSystem.HasGeometry(image))
            {
                try { return new FsmzFileSystem(image); }
                catch (InvalidDataException exception) { return new RawDskFileSystem(image, DskFileSystemType.BootOnly, exception.Message); }
            }

            bool hasBoot = HasMzBootTrack(image);
            bool cpmGeometry = hasBoot && (HasDataGeometry(image, 9) || HasDataGeometry(image, 18));
            if (cpmGeometry)
            {
                if (MrsFileSystem.TryOpen(image, out MrsFileSystem? mrs)) return mrs!;
                foreach (CpmDpb dpb in CpmDpb.PresetsFor(image))
                {
                    if (CpmFileSystem.TryOpen(image, dpb, out CpmFileSystem? cpm)) return cpm!;
                }
                return new RawDskFileSystem(image, DskFileSystemType.BootOnly, "A Sharp MZ boot track is present, but the data filesystem was not recognized.");
            }

            return new RawDskFileSystem(image, hasBoot ? DskFileSystemType.BootOnly : DskFileSystemType.Raw);
        }

        private static bool HasMzBootTrack(DskImage image) => image.Tracks.Count > 1 &&
            image.Tracks[1] is DskImage.DskTrack boot && boot.Sectors.Count == 16 &&
            boot.Sectors.All(sector => sector.Data.Length == 256);

        private static bool HasDataGeometry(DskImage image, int sectors) => image.Tracks.Count >= 3 &&
            image.Tracks.Select((track, index) => (track, index)).Where(item => item.index != 1).All(item =>
                item.track != null && item.track.Sectors.Count == sectors && item.track.Sectors.All(sector => sector.Data.Length == 512));
    }

    internal sealed class RawDskFileSystem : IDskFileSystem
    {
        private readonly DskImage image;
        private readonly IReadOnlyList<string> warnings;

        internal RawDskFileSystem(DskImage image, DskFileSystemType type = DskFileSystemType.Raw, string? warning = null)
        {
            this.image = image;
            Type = type;
            warnings = warning == null ? Array.Empty<string>() : new[] { warning };
        }

        public DskFileSystemType Type { get; }
        public string DisplayName => Type == DskFileSystemType.BootOnly ? "Boot only / unknown" : "Raw / unknown";
        public bool IsReadOnly => false;
        public long UsedBytes => image.TotalSectorDataBytes;
        public long FreeBytes => 0;
        public IReadOnlyList<string> Warnings => warnings;

        public IReadOnlyList<DskFileEntry> ReadDirectory()
        {
            var result = new List<DskFileEntry>();
            for (int trackIndex = 0; trackIndex < image.Tracks.Count; trackIndex++)
            {
                DskImage.DskTrack? track = image.Tracks[trackIndex];
                if (track == null) continue;
                foreach (DskImage.DskSector sector in track.Sectors)
                {
                    result.Add(new DskFileEntry
                    {
                        Key = $"{trackIndex}:{sector.PhysicalIndex}",
                        Name = $"Track {track.Cylinder}, side {track.Side}",
                        Extension = $"R={sector.SectorId}",
                        Size = sector.Data.Length,
                        StartBlock = trackIndex,
                        Blocks = sector.PhysicalIndex,
                        Notes = $"N={sector.SizeCode}, ST1={sector.FdcStatus1:X2}, ST2={sector.FdcStatus2:X2}"
                    });
                }
            }
            return result;
        }

        public byte[] Extract(DskFileEntry entry)
        {
            string[] key = entry.Key.Split(':');
            int trackIndex = int.Parse(key[0]);
            int physicalIndex = int.Parse(key[1]);
            return (byte[])image.Tracks[trackIndex]!.Sectors[physicalIndex].Data.Clone();
        }

        internal void ReplaceSector(DskFileEntry entry, ReadOnlySpan<byte> data)
        {
            string[] key = entry.Key.Split(':');
            int trackIndex = int.Parse(key[0]);
            int physicalIndex = int.Parse(key[1]);
            DskImage.DskSector sector = image.Tracks[trackIndex]!.Sectors[physicalIndex];
            if (data.Length != sector.Data.Length)
            {
                throw new InvalidDataException($"Sector requires exactly {sector.Data.Length} bytes, got {data.Length}.");
            }
            data.CopyTo(sector.Data);
        }

        public void Insert(string name, byte[] data, byte fileType = 1, ushort loadAddress = 0, ushort executeAddress = 0, int user = 0) =>
            throw new NotSupportedException("Use raw sector replacement for an unknown filesystem.");
        public void Delete(DskFileEntry entry, bool force = false) => throw new NotSupportedException("Raw sectors cannot be deleted.");
        public void Rename(DskFileEntry entry, string newName) => throw new NotSupportedException("Raw sectors cannot be renamed.");
    }
}
