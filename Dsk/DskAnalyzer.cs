using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

// A snapshot reader only: never writes sectors or marks the document modified.
internal sealed class DskAnalyzer
{
    private readonly DskImage image;
    private readonly DskLayoutModel model = new();
    private readonly Dictionary<(int Track, int Id), List<DskSectorLayout>> byId = new();
    private DskAnalyzer(DskImage image) => this.image = image;

    internal static DskLayoutModel Analyze(DskDocument document) => new DskAnalyzer(document.Image).Build(document.FileSystem);
    internal static DskLayoutModel Analyze(DskImage image, CpmDpb? dpb = null) => new DskAnalyzer(image).Build(null, dpb);
    internal static DskLayoutModel Analyze(byte[] bytes)
    {
        try { return Analyze(DskImage.Parse(bytes)); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            var result = new DskLayoutModel();
            result.Issues.Add(new("DSK_PARSE_FAILED", DskIssueSeverity.Error, "Image cannot be parsed.", e.Message));
            return result;
        }
    }

    private DskLayoutModel Build(IDskFileSystem? fs, CpmDpb? dpb = null)
    {
        Container();
        if (fs == null)
        {
            try { fs = DskFileSystemDetector.Detect(image); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
            { Issue("FS_DETECTION_FAILED", DskIssueSeverity.Warning, "Filesystem detection failed.", e.Message); }
        }
        model.FileSystem = fs?.DisplayName ?? "Raw / unknown";
        try
        {
            if (dpb != null || fs is CpmFileSystem) Cpm(dpb ?? ((CpmFileSystem)fs!).Dpb);
            else if (fs is FsmzFileSystem || HasFsmzMarker() || HasFsmzDinfoEvidence()) Fsmz(fs as FsmzFileSystem);
            else if (fs is MrsFileSystem || HasMrsMarker()) Mrs();
            else if (fs?.Type is DskFileSystemType.SingleIpl or DskFileSystemType.MultiIpl) Ipl(fs);
            else
            {
                // Geometry alone is not proof of a filesystem. A failed CP/M
                // detector may, however, leave plausible directory extents.
                bool recovered = false;
                foreach (var candidate in CpmDpb.PresetsFor(image))
                    if (HasCpmDirectoryEvidence(candidate))
                    {
                        Issue("CPM_LAYOUT_CANDIDATE", DskIssueSeverity.Info, "Analyzing a candidate CP/M layout rejected by the normal filesystem reader.", candidate.Name);
                        Cpm(candidate); recovered = true; break;
                    }
                if (!recovered && fs?.Type == DskFileSystemType.BootOnly) Ipl(fs);
            }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { Issue("FS_LAYOUT_INVALID", DskIssueSeverity.Error, "Filesystem layout cannot be fully mapped.", e.Message); }
        if (fs != null)
            foreach (var warning in fs.Warnings.Distinct())
                Issue("FS_LEGACY_WARNING", warning.StartsWith("Unsafe:") ? DskIssueSeverity.Unsafe : DskIssueSeverity.Warning, warning, "Reported by the existing filesystem reader.");
        foreach (var issue in model.Issues.Where(i => i.Track != null))
            foreach (var sector in model.Sectors.Where(s => s.Track == issue.Track && (issue.Sector == null || s.PhysicalIndex == issue.Sector))) sector.HasIssue = true;
        return model;
    }

    private void Container()
    {
        model.Cylinders = image.TrackCount; model.Sides = image.SideCount;
        if (image.TrackCount * image.SideCount != image.Tracks.Count)
            Issue("DSK_TRACK_COUNT", DskIssueSeverity.Error, "Header geometry does not match the track collection.");
        long offset = DskImage.HeaderSize;
        for (int ti = 0; ti < image.Tracks.Count; ti++)
        {
            var track = image.Tracks[ti];
            if (track == null)
            {
                model.Tracks.Add(new(ti, ti / Math.Max(1, (int)image.SideCount), ti % Math.Max(1, (int)image.SideCount), offset, 0, 0, true, Array.Empty<DskSectorLayout>()));
                Issue("DSK_MISSING_TRACK", DskIssueSeverity.Info, "Track is absent (tsize=0).", track: ti);
                continue;
            }
            if (track.Cylinder != ti / image.SideCount || track.Side != ti % image.SideCount)
                Issue("DSK_TRACK_CH", DskIssueSeverity.Warning, "Track C/H metadata differs from its physical position.", track: ti);
            int padding = track.BlockSize - DskImage.TrackHeaderSize - track.Sectors.Sum(s => s.Data.Length);
            if (padding < 0 || (track.BlockSize & 255) != 0 || track.BlockSize != track.DeclaredBlockSize)
                Issue("DSK_TRACK_SIZE", DskIssueSeverity.Error, "Track size/padding is inconsistent.", track: ti);
            var sectors = new List<DskSectorLayout>();
            long sectorOffset = offset + DskImage.TrackHeaderSize;
            for (int si = 0; si < track.Sectors.Count; si++)
            {
                var s = track.Sectors[si];
                var sector = new DskSectorLayout { Address = new(ti, si), C = s.Cylinder, H = s.Side, R = s.SectorId, N = s.SizeCode,
                    St1 = s.FdcStatus1, St2 = s.FdcStatus2, FileOffset = sectorOffset, Data = (byte[])s.Data.Clone() };
                sectors.Add(sector);
                if (!byId.TryGetValue((ti, s.SectorId), out var group)) byId[(ti, s.SectorId)] = group = new();
                group.Add(sector);
                if (s.Cylinder != track.Cylinder || s.Side != track.Side)
                    Issue("DSK_SECTOR_CH", DskIssueSeverity.Warning, "Sector C/H differs from track metadata.", track: ti, sector: si);
                if (s.SizeCode > 3 || s.Data.Length != (128 << s.SizeCode) || (s.DeclaredDataLength != 0 && s.DeclaredDataLength != s.Data.Length))
                    Issue("DSK_SECTOR_SIZE", DskIssueSeverity.Error, "Sector data size does not match N or N is unsupported.", track: ti, sector: si);
                if (s.FdcStatus1 != 0 || s.FdcStatus2 != 0)
                    Issue("DSK_FDC_STATUS", (s.FdcStatus1 & 0x25) != 0 || (s.FdcStatus2 & 0x31) != 0 ? DskIssueSeverity.Error : DskIssueSeverity.Warning,
                        "Sector contains FDC status flags.", $"ST1={s.FdcStatus1:X2}, ST2={s.FdcStatus2:X2}; preserved controller status, not repaired.", ti, si);
                if (sectorOffset + s.Data.Length > offset + track.DeclaredBlockSize)
                    Issue("DSK_SECTOR_OUTSIDE_TRACK", DskIssueSeverity.Error, "Sector extends beyond the track block.", track: ti, sector: si);
                sectorOffset += s.Data.Length;
            }
            foreach (var group in sectors.GroupBy(s => (s.C, s.H, s.R)).Where(g => g.Count() > 1))
                foreach (var s in group) Issue("DSK_DUPLICATE_SECTOR_ID", DskIssueSeverity.Error, "Duplicate C/H/R sector address.", track: ti, sector: s.PhysicalIndex);
            model.Tracks.Add(new(ti, track.Cylinder, track.Side, offset, track.BlockSize, padding, false, sectors));
            offset += track.BlockSize;
        }
        if (image.ContainerTrailingData.Length != 0)
            Issue("DSK_TRAILING_DATA", DskIssueSeverity.Info, "Container has trailing data.", $"{image.ContainerTrailingData.Length} bytes after declared tracks.");
    }

    private DskSectorLayout Sector(int track, int id)
    {
        if (!byId.TryGetValue((track, id), out var found) || found.Count != 1)
            throw new InvalidDataException($"Physical track {track}, sector R={id} is missing or ambiguous.");
        return found[0];
    }
    private DskSectorLayout FsmzSector(int block) => Sector((block / 16) ^ 1, block % 16 + 1);
    private DskSectorLayout MrsSector(int block) => Sector(block / 9, block % 9 + 1);
    private static byte[] Decode(DskSectorLayout s, bool inverted) => s.Data.Select(b => inverted ? (byte)(b ^ 255) : b).ToArray();
    private static int Word(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
    private bool HasFsmzMarker()
    {
        try { var b = Decode(FsmzSector(16), true); return b[0] == 0x80 && b[1] == 1; } catch (InvalidDataException) { return false; }
    }
    private bool HasMrsMarker()
    {
        try { return Decode(MrsSector(36), true)[36] == 0xFA; } catch (InvalidDataException) { return false; }
    }
    private bool HasFsmzDinfoEvidence()
    {
        if (!FsmzFileSystem.HasGeometry(image)) return false;
        try
        {
            var info = Decode(FsmzSector(15), true);
            bool plausible = info[1] >= 24 && Word(info, 4) >= info[1] && Word(info, 4) < image.Tracks.Count * 16;
            if (plausible) Issue("FSMZ_LAYOUT_CANDIDATE", DskIssueSeverity.Info, "DINFO indicates a candidate FSMZ layout with damaged directory marker.");
            return plausible;
        }
        catch (InvalidDataException) { return false; }
    }
    private void Mark(DskSectorLayout s, DskSectorRole role, int? logical = null, int? allocation = null)
    {
        s.Role |= role;
        if (role.HasFlag(DskSectorRole.Data)) s.Role &= ~DskSectorRole.Free;
        if (logical is int l && !s.LogicalBlocks.Contains(l)) s.LogicalBlocks.Add(l);
        if (allocation is int a && !s.AllocationBlocks.Contains(a)) s.AllocationBlocks.Add(a);
    }
    private void Issue(string code, DskIssueSeverity severity, string description, string detail = "", int? track = null, int? sector = null, int? block = null, string? file = null)
        => model.Issues.Add(new(code, severity, description, detail, track, sector, block, file));
    private void BlockIssue(string code, string text, DskSectorLayout? sector, int block, string? file = null, DskIssueSeverity severity = DskIssueSeverity.Unsafe)
        => Issue(code, severity, text, track: sector?.Track, sector: sector?.PhysicalIndex, block: block, file: file);

    private void Fsmz(FsmzFileSystem? fs)
    {
        var info = Decode(FsmzSector(15), true);
        int area = info[1], last = Word(info, 4), used = Word(info, 2), total = image.Tracks.Count * 16;
        int limit = fs?.DirectoryLimit ?? 63;
        if (fs == null)
            for (int slot = 64; slot <= 127; slot++)
                if (Decode(FsmzSector(16 + slot / 8), true)[slot % 8 * 32] != 0) { limit = 127; break; }
        model.FileSystem = limit == 127 ? "IPLDISK / Extended FSMZ (127 entries)" : "MZ-BASIC / FSMZ (63 entries)";
        if (area < 24 || area >= total || last < area || last >= total || last - area >= 2000)
            BlockIssue("FSMZ_DINFO_INVALID", "DINFO file area or declared last block is invalid.", FsmzSector(15), 15);
        var header = Decode(FsmzSector(16), true);
        if (header[0] != 0x80 || header[1] != 1)
            BlockIssue("FSMZ_DIRECTORY_MARKER", "Directory marker 80 01 is missing.", FsmzSector(16), 16);
        bool Allocated(int block) => block >= area && block - area < 2000 && (info[6 + (block - area) / 8] & (1 << ((block - area) % 8))) != 0;
        var claims = new Dictionary<int, string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < total; b++)
        {
            try { Mark(FsmzSector(b), b == 0 ? DskSectorRole.Boot : b == 15 ? DskSectorRole.AllocationMap | DskSectorRole.Reserved :
                b >= 16 && b <= 16 + limit / 8 ? DskSectorRole.Directory | DskSectorRole.Reserved :
                b < area || b > last ? DskSectorRole.Reserved : Allocated(b) ? DskSectorRole.Data : DskSectorRole.Free, b); }
            catch (InvalidDataException e) { Issue("FSMZ_BLOCK_MAP", DskIssueSeverity.Error, e.Message, block: b); }
        }
        for (int slot = 1; slot <= limit; slot++)
        {
            var dir = FsmzSector(16 + slot / 8); var raw = Decode(dir, true).AsSpan(slot % 8 * 32, 32).ToArray();
            if (raw[0] == 0) continue;
            string key = slot.ToString(), name = SharpMzEncoding.ConvertMzfNameToASCIIString(raw.AsSpan(1, 17).ToArray());
            int start = Word(raw, 30), size = Word(raw, 20), count = Math.Max(1, (size + 255) / 256);
            model.FileCount++;
            if (!names.Add(name)) BlockIssue("FSMZ_DUPLICATE_NAME", "Duplicate filename: " + name, dir, 16 + slot / 8, key, DskIssueSeverity.Warning);
            if (start < area || (long)start + count > total || (long)start + count > last + 1)
                BlockIssue("FSMZ_FILE_OUT_OF_RANGE", $"File {name} lies outside the declared file area.", dir, start, key);
            for (int i = 0; i < count; i++)
            {
                int b = start + i;
                DskSectorLayout? s = null;
                try { s = FsmzSector(b); } catch (InvalidDataException) { }
                if (claims.ContainsKey(b)) BlockIssue("FSMZ_OVERLAPPING_FILES", "Two files use the same block.", s, b, key);
                claims[b] = key;
                if (!Allocated(b)) BlockIssue("FSMZ_BITMAP_MISMATCH", "File block is not marked allocated in DINFO.", s ?? dir, b, key);
                if (s != null) { Mark(s, DskSectorRole.Data, b); s.Owners.Add(new(key, name, b, b, 0, 256, i)); }
            }
        }
        int bits = 0;
        for (int r = 0; r < 2000; r++)
        {
            if ((info[6 + r / 8] & (1 << (r % 8))) == 0) continue;
            int b = area + r; bits++;
            if (b > last) BlockIssue("FSMZ_BITMAP_OUT_OF_RANGE", "Bitmap allocation exceeds declared area.", FsmzSector(15), b);
            else if (!claims.ContainsKey(b)) BlockIssue("FSMZ_ORPHAN_BLOCK", "Bitmap block has no directory owner.", b < total ? FsmzSector(b) : null, b, severity: DskIssueSeverity.Warning);
        }
        if (used != area + bits) BlockIssue("FSMZ_USED_COUNTER_MISMATCH", $"Used counter {used} differs from reserved + allocated blocks ({area + bits}).", FsmzSector(15), 15, severity: DskIssueSeverity.Warning);
    }

    private byte[] CpmBlock(CpmDpb dpb, int block)
    {
        var bytes = new byte[dpb.BlockSize];
        for (int p = 0; p < bytes.Length; p += 128)
        {
            var (t, r, offset) = CpmFileSystem.MapByteOffset(dpb, block, p);
            var s = Sector(t, r);
            if (s.DataLength != 512) throw new InvalidDataException($"CP/M sector at track {t}, R={r} must have 512 bytes.");
            Decode(s, dpb.Inverted).AsSpan(offset, 128).CopyTo(bytes.AsSpan(p, 128));
        }
        return bytes;
    }
    private bool HasCpmDirectoryEvidence(CpmDpb dpb)
    {
        try
        {
            var bytes = CpmBlock(dpb, 0);
            return Enumerable.Range(0, bytes.Length / 32).Any(i => bytes[i * 32] <= 15 && bytes.AsSpan(i * 32 + 1, 8).ToArray().All(b => (b & 127) is >= 32 and <= 126));
        }
        catch (InvalidDataException) { return false; }
    }
    private void Cpm(CpmDpb dpb)
    {
        model.FileSystem = dpb.Name;
        bool native = CpmDpbSignature.From(dpb) == CpmDpbSignature.From(CpmDpb.PersonalCpm80);
        if (dpb.Spt == 0 || dpb.Spt % 4 != 0 || dpb.BlockSize == 0 || dpb.BlockSize % 128 != 0 || dpb.Blm != (1 << dpb.Bsh) - 1)
            throw new InvalidDataException("CP/M DPB geometry is inconsistent.");
        var directoryBlocks = Enumerable.Range(0, 16).Where(b => (((dpb.Al0 << 8) | dpb.Al1) & (1 << (15 - b))) != 0).ToHashSet();
        if (directoryBlocks.Count * dpb.BlockSize < (dpb.Drm + 1) * 32)
            Issue("CPM_DIRECTORY_ALLOCATION", DskIssueSeverity.Unsafe, "DPB directory blocks do not cover DRM.");
        foreach (int t in DskDocumentFactory.GetSystemPhysicalTracks(dpb, image))
            foreach (var s in model.Tracks[t].Sectors) Mark(s, t == 1 ? DskSectorRole.Boot | DskSectorRole.Reserved | (native ? DskSectorRole.NativeIpl : 0) : DskSectorRole.System | DskSectorRole.Reserved);
        var blockSectors = new Dictionary<int, List<(DskSectorLayout Sector, int Offset, int Logical)>>();
        var physicalRanges = new Dictionary<(int Track, int Index, int Offset), int>();
        for (int b = 0; b <= dpb.Dsm; b++)
        {
            var mapped = new List<(DskSectorLayout, int, int)>();
            try
            {
                for (int p = 0; p < dpb.BlockSize; p += 128)
                {
                    var (t, r, offset) = CpmFileSystem.MapByteOffset(dpb, b, p); var s = Sector(t, r);
                    if (s.DataLength != 512 || offset + 128 > s.DataLength) throw new InvalidDataException("CP/M physical mapping has incompatible sector size.");
                    if ((s.Role & (DskSectorRole.System | DskSectorRole.Boot)) != 0)
                        BlockIssue("CPM_SYSTEM_DATA_OVERLAP", "DPB maps allocation data into the boot/system area.", s, b);
                    var rangeKey = (s.Track, s.PhysicalIndex, offset);
                    if (physicalRanges.TryGetValue(rangeKey, out int previousBlock))
                        BlockIssue("CPM_PHYSICAL_MAP_ALIAS", $"Allocation blocks {previousBlock} and {b} map to the same physical byte range.", s, b);
                    else physicalRanges[rangeKey] = b;
                    Mark(s, directoryBlocks.Contains(b) ? DskSectorRole.Directory | DskSectorRole.Reserved : DskSectorRole.Free, b * dpb.BlockSize / 128 + p / 128, b);
                    mapped.Add((s, offset, b * dpb.BlockSize / 128 + p / 128));
                }
                blockSectors[b] = mapped;
            }
            catch (InvalidDataException e) { Issue("CPM_PHYSICAL_MAP_MISMATCH", DskIssueSeverity.Error, e.Message, block: b); }
        }
        var directory = directoryBlocks.Order().SelectMany(b => CpmBlock(dpb, b)).Take((dpb.Drm + 1) * 32).ToArray();
        var claimed = new Dictionary<int, string>(); var extents = new Dictionary<string, SortedSet<int>>();
        for (int slot = 0; slot < directory.Length / 32; slot++)
        {
            byte[] raw = directory.AsSpan(slot * 32, 32).ToArray(); if (raw[0] == 0xE5) continue;
            var dirSector = blockSectors[directoryBlocks.Order().ElementAt(slot * 32 / dpb.BlockSize)][slot * 32 % dpb.BlockSize / 128].Sector;
            if (raw[0] > 15)
            {
                // CP/M 3 label/time entries are structured metadata, not files.
                Issue("CPM_USER_METADATA", raw[0] is 0x20 or 0x21 ? DskIssueSeverity.Info : DskIssueSeverity.Warning,
                    $"Directory entry user/type byte is {raw[0]:X2}.", track: dirSector.Track, sector: dirSector.PhysicalIndex); continue;
            }
            string name = Encoding.ASCII.GetString(raw.Skip(1).Take(8).Select(b => (byte)(b & 127)).ToArray()).TrimEnd();
            string extension = Encoding.ASCII.GetString(raw.Skip(9).Take(3).Select(b => (byte)(b & 127)).ToArray()).TrimEnd();
            string key = $"{raw[0]}:{name}.{extension}";
            string display = extension.Length > 0 ? name + "." + extension : name;
            int extent = (raw[14] & 63) * 32 + raw[12], group = extent / (dpb.Exm + 1);
            if (name.Length == 0 || name.Contains(' ') || extension.Contains(' ') ||
                raw.Skip(1).Take(11).Any(b => (b & 127) < 32 || (b & 127) > 126 || "<>.,;:=?*[]".Contains((char)(b & 127))))
                Issue("CPM_INVALID_FILENAME", DskIssueSeverity.Warning, "Invalid CP/M 8.3 filename metadata.", track: dirSector.Track, sector: dirSector.PhysicalIndex, file: key);
            if (raw[12] > 31 || raw[15] > 128 || (raw[14] & 0x40) != 0)
                Issue("CPM_BROKEN_EXTENT", DskIssueSeverity.Unsafe, "Invalid extent fields or RC.", track: dirSector.Track, sector: dirSector.PhysicalIndex, file: key);
            if (!extents.TryGetValue(key, out var groups)) extents[key] = groups = new();
            if (!groups.Add(group)) Issue("CPM_DUPLICATE_EXTENT", DskIssueSeverity.Warning, "Duplicate filename/extent group in the same user area.", track: dirSector.Track, sector: dirSector.PhysicalIndex, file: key);
            var pointers = new List<int>();
            for (int p = 16; p < 32; p += dpb.Dsm <= 255 ? 1 : 2)
            { int b = dpb.Dsm <= 255 ? raw[p] : Word(raw, p); if (b != 0) pointers.Add(b); }
            int bytes = ((extent & dpb.Exm) * 128 + Math.Min(128, (int)raw[15])) * 128;
            if (pointers.Count * dpb.BlockSize < bytes)
                Issue("CPM_SIZE_ALLOCATION_MISMATCH", DskIssueSeverity.Unsafe, "Extent RC requires more blocks than its allocation list.", track: dirSector.Track, sector: dirSector.PhysicalIndex, file: key);
            for (int i = 0; i < pointers.Count; i++)
            {
                int b = pointers[i];
                if (b > dpb.Dsm) { BlockIssue("CPM_BLOCK_OUT_OF_RANGE", "Allocation pointer exceeds DSM.", dirSector, b, key); continue; }
                if (directoryBlocks.Contains(b)) BlockIssue("CPM_DIRECTORY_DATA_OVERLAP", "File uses a reserved directory block.", dirSector, b, key);
                if (claimed.TryGetValue(b, out var previous)) BlockIssue(previous == key ? "CPM_DUPLICATE_ALLOCATION" : "CPM_CROSSLINKED_BLOCK", "Allocation block has multiple claims.", blockSectors.GetValueOrDefault(b)?.FirstOrDefault().Sector ?? dirSector, b, key);
                claimed[b] = key;
                if (!blockSectors.TryGetValue(b, out var parts)) continue;
                foreach (var (s, offset, logical) in parts)
                { Mark(s, DskSectorRole.Data | (native && key == "0:PCPM.SYS" ? DskSectorRole.SystemFile : 0)); s.Owners.Add(new(key, display, b, logical, offset, 128, group * (dpb.Dsm <= 255 ? 16 : 8) + i, extent, raw[0])); }
            }
        }
        model.FileCount = extents.Count;
        if (native)
        {
            bool present = extents.ContainsKey("0:PCPM.SYS");
            bool sys = Enumerable.Range(0, directory.Length / 32).Any(slot => directory[slot * 32] == 0 &&
                Encoding.ASCII.GetString(directory, slot * 32 + 1, 8) == "PCPM    " &&
                new string(directory.Skip(slot * 32 + 9).Take(3).Select(b => (char)(b & 127)).ToArray()) == "SYS" && (directory[slot * 32 + 10] & 128) != 0);
            Issue("PCPM_SYSTEM_FILE", DskIssueSeverity.Info, $"Native IPL expects PCPM.SYS; user 0 present: {(present ? "Yes" : "No")}; SYS attribute: {(sys ? "Yes" : "No")}.");
            var systemEntries = Enumerable.Range(0, directory.Length / 32)
                .Select(slot => directory.AsSpan(slot * 32, 32).ToArray())
                .Where(entry => PersonalCpmSystemInstaller.IsSystemDirectoryEntry(entry)).ToArray();
            if (present && !PersonalCpmSystemInstaller.IsSystemDirectoryEntry(directory.AsSpan(0, 32)))
                Issue("PCPM_SYS_NOT_FIRST_DIRECTORY_ENTRY", DskIssueSeverity.Warning,
                    "PCPM.SYS is not directory entry #0; native P-CP/M80 cannot boot.",
                    "The IPL reads only user 0 PCPM.SYS in the first entry. Installation must safely relocate the existing first extent without changing its allocation/data.", file: "0:PCPM.SYS");
            if (systemEntries.Length > 1 || systemEntries.Any(entry => (entry[12] & ~dpb.Exm) != 0 || (entry[14] & 63) != 0))
                Issue("PCPM_SYS_INVALID_EXTENTS", DskIssueSeverity.Unsafe,
                    "Native PCPM.SYS has duplicate/multiple or noninitial extents.",
                    "The native IPL uses the allocation list of directory entry 0, not subsequent directory extents. Do not merge or discard entries heuristically.", file: "0:PCPM.SYS");
        }
        foreach (var (key, groups) in extents)
            if (groups.Count != 0 && (groups.Min != 0 || groups.Max + 1 != groups.Count))
                Issue("CPM_EXTENT_GAP", DskIssueSeverity.Warning, "File has missing logical extent groups.", file: key);
        Issue("CPM_FREE_DERIVED", DskIssueSeverity.Info, "Free allocation blocks are derived from directory extents and DPB reserved blocks.", "CP/M has no on-disk allocation bitmap; orphan allocation cannot be inferred from arbitrary data bytes.");
    }

    private void Mrs()
    {
        model.FileSystem = "MRS";
        byte[] first = Decode(MrsSector(36), true); int position = 36;
        while (position < 512 && first[position] == 0xFA) position++;
        int fatCount = position - 36, directoryBlock = position;
        while (position < 512 && first[position] == 0xFD) position++;
        int directoryCount = position - directoryBlock, dataBlock = position;
        if (fatCount == 2 && directoryCount >= 2) { fatCount = 3; directoryBlock++; directoryCount--; }
        if (fatCount is < 1 or > 3 || directoryCount is < 1 or > 8) throw new InvalidDataException("MRS FAT/directory markers are invalid.");
        byte[] fat = Enumerable.Range(36, fatCount).SelectMany(b => Decode(MrsSector(b), true)).ToArray();
        byte[] dir = Enumerable.Range(directoryBlock, directoryCount).SelectMany(b => Decode(MrsSector(b), true)).ToArray();
        int total = image.Tracks.Count * 9;
        if (total > fat.Length) Issue("MRS_FAT_COVERAGE", DskIssueSeverity.Error, "FAT does not cover all image blocks.");
        var files = new Dictionary<int, List<(string Key, string Name, int Count)>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 0; slot < dir.Length / 32; slot++)
        {
            var raw = dir.AsSpan(slot * 32, 32).ToArray(); if (raw[0] <= 32) continue;
            int id = raw[11]; var sector = MrsSector(directoryBlock + slot * 32 / 512); string key = slot.ToString();
            string name = Encoding.ASCII.GetString(raw, 0, 8).TrimEnd() + "." + Encoding.ASCII.GetString(raw, 8, 3).TrimEnd();
            if (id == 0 || id >= 0xFA) { BlockIssue("MRS_INVALID_FILE_ID", "Directory entry has invalid file ID.", sector, directoryBlock, key); continue; }
            if (!files.TryGetValue(id, out var sameId)) files[id] = sameId = new();
            else BlockIssue("MRS_DUPLICATE_FILE_ID", "Multiple directory files share a file ID.", sector, directoryBlock, key);
            sameId.Add((key, name, Word(raw, 14)));
            if (!names.Add(name)) BlockIssue("MRS_DUPLICATE_FILENAME", "Duplicate MRS filename.", sector, directoryBlock, key, DskIssueSeverity.Warning);
            model.FileCount++;
        }
        var counts = new Dictionary<int, int>();
        for (int b = 0; b < Math.Min(total, fat.Length); b++)
        {
            if (b / 9 == 1)
            {
                foreach (var s in model.Tracks[1].Sectors) Mark(s, DskSectorRole.Boot | DskSectorRole.Reserved);
                if (fat[b] is > 0 and < 0xFA) BlockIssue("MRS_RESERVED_DATA_OVERLAP", "FAT assigns the boot track to file data.", model.Tracks[1].Sectors.FirstOrDefault(), b);
                continue;
            }
            var sector = MrsSector(b); int id = fat[b];
            Mark(sector, b < 36 ? DskSectorRole.System | DskSectorRole.Reserved : b < directoryBlock ? DskSectorRole.Fat | DskSectorRole.Reserved : b < dataBlock ? DskSectorRole.Directory | DskSectorRole.Reserved : id == 0 ? DskSectorRole.Free : id >= 0xFA ? DskSectorRole.Reserved : DskSectorRole.Data, b);
            if (id is > 0 and < 0xFA)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
                if (b < dataBlock) BlockIssue("MRS_RESERVED_DATA_OVERLAP", "FAT assigns a reserved/metadata block to file data.", sector, b);
                if (!files.TryGetValue(id, out var owners)) BlockIssue("MRS_FAT_ORPHAN_BLOCK", "FAT file ID has no directory entry.", sector, b, severity: DskIssueSeverity.Warning);
                else
                    foreach (var file in owners)
                    { Mark(sector, DskSectorRole.Data); sector.Owners.Add(new(file.Key, file.Name, b, b, 0, 512, counts[id] - 1)); }
            }
            else if (b >= dataBlock && id is 0xFA or 0xFD)
                BlockIssue("MRS_INVALID_FAT_MARKER", "Metadata FAT marker appears in the data region.", sector, b);
        }
        foreach (var (id, owners) in files)
            foreach (var file in owners)
                if (file.Count != counts.GetValueOrDefault(id)) Issue("MRS_BLOCK_COUNT_MISMATCH", DskIssueSeverity.Unsafe, $"{file.Name}: directory declares {file.Count} blocks, FAT has {counts.GetValueOrDefault(id)}.", file: file.Key);
        for (int b = total; b < fat.Length; b++)
            if (fat[b] is > 0 and < 0xFA) BlockIssue("MRS_BLOCK_OUT_OF_RANGE", "FAT allocates a file block outside the image.", MrsSector(36 + b / 512), b);
    }

    private void Ipl(IDskFileSystem fs)
    {
        var files = fs.ReadDirectory().Where(e => e.Key != "disk:data" && !e.Key.Contains(':')).ToArray(); model.FileCount = files.Length;
        if (!FsmzFileSystem.HasGeometry(image))
        {
            foreach (var s in model.Tracks.ElementAtOrDefault(1)?.Sectors ?? Array.Empty<DskSectorLayout>()) Mark(s, DskSectorRole.Boot);
            foreach (var file in files)
                for (int i = 0; i < file.Blocks; i++)
                {
                    var s = Sector(1, file.StartBlock + i + 1);
                    Mark(s, DskSectorRole.Data, file.StartBlock + i);
                    s.Owners.Add(new(file.Key, file.Name, file.StartBlock + i, file.StartBlock + i, 0, s.DataLength, i));
                }
            return;
        }
        for (int b = 0; b < image.Tracks.Count * 16; b++) Mark(FsmzSector(b), b == 0 ? DskSectorRole.Boot : fs.Type == DskFileSystemType.BootOnly ? DskSectorRole.Unknown : DskSectorRole.Free, b);
        if (fs.Type == DskFileSystemType.MultiIpl)
        {
            var boot = Decode(FsmzSector(0), true); int menuBlocks = (Word(boot, 0x14) + 255) / 256;
            for (int b = 1; b <= menuBlocks; b++) { var s = FsmzSector(b); s.Role = DskSectorRole.System | DskSectorRole.Data; }
        }
        foreach (var file in files)
            for (int i = 0; i < file.Blocks; i++)
            { var s = FsmzSector(file.StartBlock + i); Mark(s, DskSectorRole.Data); s.Owners.Add(new(file.Key, file.Name, file.StartBlock + i, file.StartBlock + i, 0, 256, i)); }
    }
}
