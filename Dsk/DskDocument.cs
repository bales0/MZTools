using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QDTool
{
    internal enum DskFileSystemType
    {
        SingleIpl,
        MultiIpl,
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

        internal DskImage Image { get; private set; }
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

        internal void ReplaceContents(byte[] bytes)
        {
            DskImage replacement = DskImage.Parse(bytes);
            IDskFileSystem replacementFileSystem = DskFileSystemDetector.Detect(replacement);
            Image = replacement;
            FileSystem = replacementFileSystem;
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
        internal enum RawSectorOrder
        {
            Normal,
            Lec,
            LecHd,
            Custom
        }

        internal static DskDocument CreateFsmz(bool ipldisk = true, int tracks = 40, int sides = 2)
        {
            if (checked(tracks * sides) < 4) throw new ArgumentOutOfRangeException(nameof(tracks), "FSMZ requires at least four absolute tracks.");
            DskImage image = DskImage.CreateUniform(tracks, sides, 16, 256, 1, 0x2A, 0xFF, "MZTools");
            FsmzFileSystem.Format(image, ipldisk);
            byte[] bytes = image.Serialize();
            return new DskDocument(image, bytes, null, new FsmzFileSystem(image, ipldisk));
        }

        internal static DskDocument CreateCpm(bool highDensity, int tracks = 80, int sides = 2)
        {
            int sectors = highDensity ? 18 : 9;
            if (checked(tracks * sides) <= 4) throw new ArgumentOutOfRangeException(nameof(tracks), "LEC CP/M requires more than four absolute tracks.");
            DskImage image = DskImage.CreateSharpBootDataDisk(tracks, sides, sectors, highDensity, "MZTools");
            CpmFileSystem.Format(image, CpmDpb.CreateLec(image, highDensity));
            return DskDocument.Open(image.Serialize());
        }

        internal static DskDocument CreatePersonalCpm80(bool sds400)
        {
            int sectors = sds400 ? 10 : 8;
            DskImage image = DskImage.CreateSharpBootDataDisk(40, 2, sectors, highDensityInterleave: false, "MZTools");
            if (sds400)
            {
                for (int absoluteTrack = 0; absoluteTrack < image.Tracks.Count; absoluteTrack += 2)
                {
                    foreach (DskImage.DskSector sector in image.Tracks[absoluteTrack]!.Sectors)
                    {
                        sector.SectorId += 10;
                    }
                }
            }

            var boot = new byte[256];
            boot[0] = 3;
            "IPLPRO"u8.CopyTo(boot.AsSpan(1));
            SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("P-CP/M80").CopyTo(boot, 7);
            Mz800DskImage.WriteLogicalBlock(image, 0, boot);
            CpmFileSystem.Format(image, sds400 ? CpmDpb.Sds400 : CpmDpb.PersonalCpm80);
            return DskDocument.Open(image.Serialize());
        }

        internal static void ClearBootTrack(DskDocument document)
        {
            DskImage.DskTrack bootTrack = RequireSharpBootTrack(document.Image, "target");
            foreach (DskImage.DskSector sector in bootTrack.Sectors)
            {
                Array.Fill(sector.Data, (byte)0xFF);
            }
            document.MarkModified();
        }

        internal static void ImportBootSystemArea(DskDocument target, DskDocument source)
        {
            ValidateMatchingDataGeometry(target.Image, source.Image);
            RequireSharpBootTrack(target.Image, "target");
            RequireSharpBootTrack(source.Image, "source");
            if (target.FileSystem is not CpmFileSystem targetCpm || source.FileSystem is not CpmFileSystem sourceCpm)
            {
                throw new InvalidDataException("Both the boot source and the new disk must contain a recognized CP/M filesystem.");
            }
            ValidateMatchingCpmLayout(targetCpm.Dpb, sourceCpm.Dpb);

            int[] sourceTracks = GetSystemPhysicalTracks(sourceCpm.Dpb, source.Image).ToArray();
            int[] targetTracks = GetSystemPhysicalTracks(targetCpm.Dpb, target.Image).ToArray();
            if (sourceTracks.Length != targetTracks.Length)
            {
                throw new InvalidDataException("The source and target CP/M system areas use different physical track mappings.");
            }
            for (int index = 0; index < sourceTracks.Length; index++)
            {
                CopyTrackData(source.Image, sourceTracks[index], target.Image, targetTracks[index]);
            }
            target.MarkModified();
        }

        private static void ValidateMatchingCpmLayout(CpmDpb target, CpmDpb source)
        {
            if (target.Spt != source.Spt || target.Bsh != source.Bsh || target.Blm != source.Blm ||
                target.Exm != source.Exm || target.Dsm != source.Dsm || target.Drm != source.Drm ||
                target.Al0 != source.Al0 || target.Al1 != source.Al1 || target.Off != source.Off ||
                target.BlockSize != source.BlockSize || target.Inverted != source.Inverted)
            {
                throw new InvalidDataException(
                    $"The boot source CP/M layout (OFF={source.Off}) does not match the new disk (OFF={target.Off}).");
            }
        }

        private static IEnumerable<int> GetSystemPhysicalTracks(CpmDpb dpb, DskImage image)
        {
            var tracks = new SortedSet<int> { 1 };
            for (int logicalTrack = 0; logicalTrack < dpb.Off; logicalTrack++)
            {
                int physicalTrack = dpb.PhysicalTrackMap == null
                    ? logicalTrack
                    : logicalTrack < dpb.PhysicalTrackMap.Count
                        ? dpb.PhysicalTrackMap[logicalTrack]
                        : throw new InvalidDataException($"CP/M system track {logicalTrack} lies outside its physical track map.");
                if ((uint)physicalTrack >= image.Tracks.Count)
                {
                    throw new InvalidDataException($"CP/M system track {physicalTrack} lies outside the disk image.");
                }
                tracks.Add(physicalTrack);
            }
            return tracks;
        }

        private static void CopyTrackData(DskImage source, int sourceTrackIndex, DskImage target, int targetTrackIndex)
        {
            DskImage.DskTrack sourceTrack = source.Tracks[sourceTrackIndex] ??
                throw new InvalidDataException($"The boot source is missing physical track {sourceTrackIndex}.");
            DskImage.DskTrack targetTrack = target.Tracks[targetTrackIndex] ??
                throw new InvalidDataException($"The new disk is missing physical track {targetTrackIndex}.");
            if (sourceTrack.Sectors.Count != targetTrack.Sectors.Count)
            {
                throw new InvalidDataException($"The system track geometry differs at physical track {sourceTrackIndex}.");
            }
            foreach (DskImage.DskSector targetSector in targetTrack.Sectors)
            {
                DskImage.DskSector? sourceSector = sourceTrack.Sectors.FirstOrDefault(sector =>
                    sector.SectorId == targetSector.SectorId && sector.Data.Length == targetSector.Data.Length);
                if (sourceSector == null)
                {
                    throw new InvalidDataException($"The system track geometry differs at physical track {sourceTrackIndex}, sector {targetSector.SectorId}.");
                }
                sourceSector.Data.CopyTo(targetSector.Data, 0);
            }
        }

        private static DskImage.DskTrack RequireSharpBootTrack(DskImage image, string role)
        {
            if (image.Tracks.Count <= 1 || image.Tracks[1] is not DskImage.DskTrack track ||
                track.Sectors.Count != 16 ||
                track.Sectors.Any(sector => sector.Data.Length != 256) ||
                !track.Sectors.Select(sector => (int)sector.SectorId).OrderBy(id => id).SequenceEqual(Enumerable.Range(1, 16)))
            {
                throw new InvalidDataException($"The {role} DSK does not contain a Sharp boot track with sectors 1..16 × 256 B at track 0, side 1.");
            }
            return track;
        }

        private static void ValidateMatchingDataGeometry(DskImage target, DskImage source)
        {
            if (target.TrackCount != source.TrackCount || target.SideCount != source.SideCount ||
                target.Tracks.Count != source.Tracks.Count)
            {
                throw new InvalidDataException(
                    $"The boot source geometry ({source.TrackCount} tracks × {source.SideCount} sides) does not match the new disk ({target.TrackCount} × {target.SideCount}).");
            }

            for (int index = 0; index < target.Tracks.Count; index++)
            {
                if (index == 1) continue;
                DskImage.DskTrack? targetTrack = target.Tracks[index];
                DskImage.DskTrack? sourceTrack = source.Tracks[index];
                if (targetTrack == null || sourceTrack == null ||
                    targetTrack.Sectors.Count != sourceTrack.Sectors.Count)
                {
                    throw new InvalidDataException($"The boot source data geometry differs at physical track {index}.");
                }

                var targetSectors = targetTrack.Sectors.OrderBy(sector => sector.SectorId).ToArray();
                var sourceSectors = sourceTrack.Sectors.OrderBy(sector => sector.SectorId).ToArray();
                for (int sectorIndex = 0; sectorIndex < targetSectors.Length; sectorIndex++)
                {
                    if (targetSectors[sectorIndex].SectorId != sourceSectors[sectorIndex].SectorId ||
                        targetSectors[sectorIndex].Data.Length != sourceSectors[sectorIndex].Data.Length)
                    {
                        throw new InvalidDataException($"The boot source data geometry differs at physical track {index}.");
                    }
                }
            }
        }

        internal static DskDocument CreateMrs(int tracks = 80, int sides = 2)
        {
            int absoluteTracks = checked(tracks * sides);
            if (absoluteTracks < 5 || absoluteTracks > 160)
                throw new ArgumentOutOfRangeException(nameof(tracks), "MRS requires 5 to 160 absolute tracks.");
            DskImage image = DskImage.CreateSharpBootDataDisk(tracks, sides, 9, highDensityInterleave: false, "MZTools");
            MrsFileSystem.Format(image);
            return DskDocument.Open(image.Serialize());
        }

        internal static DskDocument CreateLemmings(int tracks = 80, int sides = 2)
        {
            if (checked(tracks * sides) <= 16) throw new ArgumentOutOfRangeException(nameof(tracks), "The Lemmings geometry requires absolute track 16.");
            DskImage image = DskImage.CreateSharpBootDataDisk(tracks, sides, 9, highDensityInterleave: false, "MZTools");
            image.ReplaceTrackGeometry(1, 16, 256, null, 0x2A, 0xE5);
            image.ReplaceTrackGeometry(16, 10, 512, [1, 6, 2, 7, 3, 8, 4, 9, 5, 21], 0x4E, 0xE5);
            return DskDocument.Open(image.Serialize());
        }

        internal static DskDocument CreateRaw(
            int tracks,
            int sides,
            int sectors,
            int sectorSize,
            int firstSectorId,
            byte gap,
            byte filler,
            string creator,
            RawSectorOrder order = RawSectorOrder.Normal,
            IReadOnlyList<int>? customSectorIds = null)
        {
            DskImage image = DskImage.CreateUniform(tracks, sides, sectors, sectorSize, firstSectorId, gap, filler, creator);
            IReadOnlyList<int>? sectorIds = order switch
            {
                RawSectorOrder.Normal => null,
                RawSectorOrder.Lec => InterleavedIds(sectors, 2, firstSectorId),
                RawSectorOrder.LecHd => InterleavedIds(sectors, 3, firstSectorId),
                RawSectorOrder.Custom when customSectorIds?.Count == sectors => customSectorIds,
                RawSectorOrder.Custom => throw new ArgumentException("Custom sector order must contain exactly one ID per sector.", nameof(customSectorIds)),
                _ => throw new ArgumentOutOfRangeException(nameof(order))
            };
            if (sectorIds != null)
            {
                for (int absoluteTrack = 0; absoluteTrack < image.Tracks.Count; absoluteTrack++)
                    image.ReplaceTrackGeometry(absoluteTrack, sectors, sectorSize, sectorIds, gap, filler);
            }
            return DskDocument.Open(image.Serialize());
        }

        private static IReadOnlyList<int> InterleavedIds(int sectors, int interleave, int firstSectorId)
        {
            int orderedCount = sectors % interleave == 0 ? sectors : sectors + interleave - sectors % interleave;
            var result = new List<int>(sectors);
            for (int first = 0; first < sectors; first += interleave)
                for (int offset = 0; offset < interleave && first + offset < sectors; offset++)
                    result.Add(firstSectorId + first / interleave + orderedCount / interleave * offset);
            return result;
        }
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
            if (MultiGameIplFileSystem.TryOpen(image, out MultiGameIplFileSystem? multiIpl)) return multiIpl!;
            if (SingleGameIplFileSystem.TryOpen(image, out SingleGameIplFileSystem? singleIpl)) return singleIpl!;
            if (FsmzFileSystem.HasGeometry(image))
            {
                try { return new FsmzFileSystem(image); }
                catch (InvalidDataException exception)
                {
                    return HasMzIplPro(image)
                        ? new RawDskFileSystem(image, DskFileSystemType.BootOnly, exception.Message)
                        : new RawDskFileSystem(image);
                }
            }

            bool hasBoot = HasMzBootTrack(image);
            bool cpmGeometry = hasBoot && (HasDataGeometry(image, 8) || HasDataGeometry(image, 9) || HasDataGeometry(image, 10) || HasDataGeometry(image, 18));
            if (cpmGeometry)
            {
                if (MrsFileSystem.TryOpen(image, out MrsFileSystem? mrs)) return mrs!;
                foreach (CpmDpb dpb in CpmDpb.PresetsFor(image))
                {
                    if (CpmFileSystem.TryOpen(image, dpb, out CpmFileSystem? cpm)) return cpm!;
                }
                return new RawDskFileSystem(image, DskFileSystemType.BootOnly, "A Sharp MZ boot track is present, but the data filesystem was not recognized.");
            }

            return new RawDskFileSystem(image, HasMzIplPro(image) ? DskFileSystemType.BootOnly : DskFileSystemType.Raw);
        }

        private static bool HasMzBootTrack(DskImage image) => image.Tracks.Count > 1 &&
            image.Tracks[1] is DskImage.DskTrack boot && boot.Sectors.Count == 16 &&
            boot.Sectors.All(sector => sector.Data.Length == 256);

        private static bool HasMzIplPro(DskImage image)
        {
            if (!HasMzBootTrack(image)) return false;
            DskImage.DskSector? sector = image.Tracks[1]!.Sectors.FirstOrDefault(candidate => candidate.SectorId == 1);
            if (sector == null) return false;
            Span<byte> signature = stackalloc byte[7];
            for (int index = 0; index < signature.Length; index++) signature[index] = (byte)(sector.Data[index] ^ 0xFF);
            return signature[0] == 3 && signature[1..].SequenceEqual("IPLPRO"u8);
        }

        private static bool HasDataGeometry(DskImage image, int sectors) => image.Tracks.Count >= 3 &&
            image.Tracks.Select((track, index) => (track, index)).Where(item => item.index != 1).All(item =>
                item.track != null && item.track.Sectors.Count == sectors && item.track.Sectors.All(sector => sector.Data.Length == 512));
    }

    internal sealed class RawDskFileSystem : IDskFileSystem
    {
        private readonly DskImage image;
        private readonly IReadOnlyList<string> warnings;
        private readonly DskFileEntry? bootstrap;

        internal RawDskFileSystem(DskImage image, DskFileSystemType type = DskFileSystemType.Raw, string? warning = null)
        {
            this.image = image;
            Type = type;
            warnings = warning == null ? Array.Empty<string>() : new[] { warning };
            if (type == DskFileSystemType.BootOnly) bootstrap = TryReadBootstrap();
        }

        public DskFileSystemType Type { get; }
        public string DisplayName => Type == DskFileSystemType.BootOnly
            ? bootstrap == null ? "Boot only / unknown" : "IPL DSK / custom data layout (no directory)"
            : "Raw / unknown";
        public bool IsReadOnly => false;
        public long UsedBytes => image.TotalSectorDataBytes;
        public long FreeBytes => 0;
        public IReadOnlyList<string> Warnings => warnings;

        public IReadOnlyList<DskFileEntry> ReadDirectory()
        {
            var result = new List<DskFileEntry>();
            if (bootstrap != null)
            {
                result.Add(bootstrap);
                result.Add(new DskFileEntry
                {
                    Key = "disk:data",
                    Name = "Decoded data area",
                    Extension = "BIN",
                    Size = image.TotalSectorDataBytes - image.Tracks[1]!.Sectors.Sum(sector => (long)sector.Data.Length),
                    StartBlock = 1,
                    Blocks = image.Tracks.Where((track, index) => index != 1 && track != null).Sum(track => track!.Sectors.Count),
                    Notes = "All data tracks after the boot track; MZ track order, sectors by ID, XOR FF decoded; no file boundaries"
                });
            }
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
            if (entry.Key == "iplpro") return ReadBootstrapPayload(entry);
            if (entry.Key == "disk:data") return ReadDecodedDataArea();
            string[] key = entry.Key.Split(':');
            int trackIndex = int.Parse(key[0]);
            int physicalIndex = int.Parse(key[1]);
            return (byte[])image.Tracks[trackIndex]!.Sectors[physicalIndex].Data.Clone();
        }

        internal void ReplaceSector(DskFileEntry entry, ReadOnlySpan<byte> data)
        {
            if (entry.Key == "iplpro" || entry.Key.StartsWith("disk:", StringComparison.Ordinal))
                throw new InvalidOperationException("Select a raw sector row to replace sector data.");
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

        private DskFileEntry? TryReadBootstrap()
        {
            if (image.Tracks.Count <= 1 || image.Tracks[1] == null) return null;
            DskImage.DskTrack track = image.Tracks[1]!;
            DskImage.DskSector? first = track.Sectors.FirstOrDefault(sector => sector.SectorId == 1 && sector.Data.Length == 256);
            if (first == null) return null;
            byte[] header = first.Data.Select(value => (byte)(value ^ 0xFF)).ToArray();
            if (header[0] != 3 || !header.AsSpan(1, 6).SequenceEqual("IPLPRO"u8)) return null;
            int size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x14, 2));
            int startBlock = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E, 2));
            int blocks = (size + 255) / 256;
            if (size == 0 || startBlock < 1 || startBlock + blocks > 16 ||
                Enumerable.Range(startBlock + 1, blocks).Any(id => !track.Sectors.Any(sector => sector.SectorId == id && sector.Data.Length == 256)))
                return null;
            string name = SharpMzEncoding.ConvertMzfNameToASCIIString(header.AsSpan(7, 13).ToArray()).TrimEnd();
            return new DskFileEntry
            {
                Key = "iplpro",
                Name = string.IsNullOrWhiteSpace(name) ? "IPLPRO" : name,
                Extension = "MZF",
                FileType = 1,
                Size = size,
                LoadAddress = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x16, 2)),
                ExecuteAddress = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x18, 2)),
                StartBlock = startBlock,
                Blocks = blocks,
                Notes = "IPLPRO first-stage loader; not the complete game"
            };
        }

        private byte[] ReadBootstrapPayload(DskFileEntry entry)
        {
            var output = new byte[checked((int)entry.Size)];
            int copied = 0;
            for (int block = 0; block < entry.Blocks; block++)
            {
                byte[] source = image.Tracks[1]!.Sectors.Single(sector => sector.SectorId == entry.StartBlock + block + 1).Data;
                int count = Math.Min(source.Length, output.Length - copied);
                for (int index = 0; index < count; index++) output[copied + index] = (byte)(source[index] ^ 0xFF);
                copied += count;
            }
            return output;
        }

        private byte[] ReadDecodedDataArea()
        {
            var output = new byte[checked((int)image.TotalSectorDataBytes)];
            int written = 0;
            for (int logicalTrack = 0; logicalTrack < image.Tracks.Count; logicalTrack++)
            {
                int pairedTrack = logicalTrack ^ 1;
                int physicalTrack = pairedTrack < image.Tracks.Count ? pairedTrack : logicalTrack;
                DskImage.DskTrack? track = image.Tracks[physicalTrack];
                if (track == null) continue;
                foreach (DskImage.DskSector sector in track.Sectors.OrderBy(sector => sector.SectorId).ThenBy(sector => sector.PhysicalIndex))
                {
                    for (int index = 0; index < sector.Data.Length; index++) output[written++] = (byte)(sector.Data[index] ^ 0xFF);
                }
            }
            int dataOffset = image.Tracks[1]!.Sectors.Sum(sector => sector.Data.Length);
            return output[dataOffset..written];
        }
    }
}
