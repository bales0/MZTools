using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MZTools;

internal enum DskStructureTab { Filesystem, Directory, Allocation, RawStructure }
internal sealed record DskStructureItem(DskStructureTab Tab, string Name, string Value, string Detail,
    byte[]? Raw, IReadOnlyList<DskSectorAddress> Addresses);
internal sealed record DskStructureSnapshot(DskLayoutModel Layout, IReadOnlyList<DskStructureItem> Items, string Summary);

// All buffers and filesystem reads belong to a private snapshot, never the editor document.
internal static class DskStructureService
{
    internal static DskStructureSnapshot Build(DskDocument source)
    {
        var doc = source.Clone();
        var layout = DskAnalyzer.Analyze(doc);
        var items = new List<DskStructureItem>();
        var files = doc.FileSystem.ReadDirectory();
        var physical = layout.Sectors.GroupBy(s => (s.Track, (int)s.R)).ToDictionary(g => g.Key, g => g.Select(s => s.Address).ToArray());
        var ownersByBlock = layout.Sectors.SelectMany(s => s.Owners).GroupBy(o => o.Block).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(o => o.FileName).Distinct()));
        DskSectorAddress[] At(int track, int id) => physical.TryGetValue((track, id), out var addresses) ? addresses : [];
        void Add(DskStructureTab tab, string name, object value, string detail = "", byte[]? raw = null, IEnumerable<DskSectorAddress>? addresses = null) =>
            items.Add(new(tab, name, value.ToString() ?? "", detail, raw, addresses?.Distinct().ToArray() ?? []));
        void Field(string name, object value, string detail = "", byte[]? raw = null, IEnumerable<DskSectorAddress>? addresses = null) => Add(DskStructureTab.Filesystem, name, value, detail, raw, addresses);
        static ushort Word(byte[] raw, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset, 2));
        if (doc.FileSystem is CpmFileSystem cpm)
        {
            var d = cpm.Dpb;
            Field("DPB profile", d.Name, "Detected layout parameters; DPB is not assumed to exist as a raw on-disk record.");
            foreach (var (name, value) in new (string, object)[] { ("SPT",d.Spt), ("BSH",d.Bsh), ("BLM",d.Blm), ("EXM",d.Exm), ("DSM",d.Dsm), ("DRM",d.Drm), ("AL0",$"0x{d.Al0:X2}"), ("AL1",$"0x{d.Al1:X2}"), ("CKS",d.Cks), ("OFF",d.Off), ("Block size",d.BlockSize), ("Inverted bytes",d.Inverted) }) Field(name, value);
            Field("Physical track map", d.PhysicalTrackMap == null ? "Identity (including OFF)" : string.Join(", ", d.PhysicalTrackMap));
            Field("Physical sector map", d.PhysicalSectorMap == null ? "R = logical sector / 4 + 1; 128-byte slices of 512-byte sectors" : string.Join(", ", d.PhysicalSectorMap.Select(a => $"T{a.AbsoluteTrack}/R{a.SectorId}")));
            int mask = d.Al0 << 8 | d.Al1;
            int[] dirBlocks = Enumerable.Range(0, 16).Where(b => (mask & (1 << (15 - b))) != 0).ToArray();
            Field("Directory blocks", string.Join(", ", dirBlocks), $"{d.Drm + 1} slots, 32 bytes each; AL0/AL1 are read most-significant bit first.");
            DskSectorAddress[] CpmAt(int block, int offset)
            {
                var a = CpmFileSystem.MapByteOffset(d, block, offset); return At(a.AbsoluteTrack, a.PhysicalSector);
            }
            var claimed = new Dictionary<int, List<string>>();
            var slots = cpm.ReadRawDirectory();
            for (int slot = 0; slot < slots.Length; slot++)
            {
                var raw = slots[slot];
                string Name(int off, int len) => Encoding.ASCII.GetString(raw.Skip(off).Take(len).Select(b => (byte)(b & 0x7F)).ToArray()).TrimEnd();
                string group = $"User {raw[0]}: {Name(1, 8)}.{Name(9, 3)}";
                int[] pointers = d.Dsm <= 255 ? raw.Skip(16).Select(b => (int)b).ToArray() : Enumerable.Range(0, 8).Select(i => (int)Word(raw, 16 + i * 2)).ToArray();
                bool active = raw[0] <= 15;
                if (active) foreach (int b in pointers.Where(b => b != 0))
                { if (!claimed.TryGetValue(b, out var names)) claimed[b] = names = new(); names.Add($"{group} (slot {slot})"); }
                int extent = (raw[14] & 0x3F) * 32 + raw[12];
                int block = dirBlocks[slot * 32 / d.BlockSize], offset = slot * 32 % d.BlockSize;
                var addresses = CpmAt(block, offset);
                string state = raw[0] == 0xE5 ? "Deleted / unused" : active ? group : $"Special user/label 0x{raw[0]:X2}";
                string detail = $"Directory block {block}, byte offset 0x{offset:X}; 32 bytes.\n{state}\nEX={raw[12]}, S1={raw[13]}, S2=0x{raw[14]:X2}; extent={extent}, extent group={extent & ~d.Exm}, RC={raw[15]}.\nRO={(raw[9]&128)!=0}, SYS={(raw[10]&128)!=0}, ARC={(raw[11]&128)!=0}.\nAllocation pointer slots ({(d.Dsm <= 255 ? 8 : 16)}-bit): {string.Join(", ", pointers)}. Zero means unused pointer.";
                Add(DskStructureTab.Directory, $"Slot {slot}", state, detail, raw, addresses);
                Add(DskStructureTab.RawStructure, $"Directory slot {slot}", "32 decoded bytes", detail, (byte[])raw.Clone(), addresses);
            }
            foreach (var file in files) Field($"File group: user {file.User} {file.Name}.{file.Extension}", $"{file.Extents} extents / {file.Size} bytes", "Slots: " + string.Join(", ", cpm.DirectoryEntriesFor(file).Select(p => p.Index)));
            for (int block = 0; block <= d.Dsm; block++)
            {
                var addresses = Enumerable.Range(0, d.BlockSize / 128).SelectMany(i => CpmAt(block, i * 128)).Distinct().ToArray();
                string owner = claimed.TryGetValue(block, out var names) ? string.Join(", ", names) : "";
                string state = dirBlocks.Contains(block) ? "Directory / reserved" : owner.Length > 0 ? "Used" : "Free";
                Add(DskStructureTab.Allocation, $"Block {block}", state, $"{d.BlockSize} bytes; {state}\nOwners/extent slots: {owner}\nPhysical sectors: {string.Join(", ", addresses)}", addresses: addresses);
            }
            foreach (var b in claimed.Keys.Where(b => b > d.Dsm)) Add(DskStructureTab.Allocation, $"Invalid block {b}", "Beyond DSM", string.Join(", ", claimed[b]));
        }
        else if (doc.FileSystem is FsmzFileSystem fsmz)
        {
            DskSectorAddress[] FsmzAt(int b) => At((b / 16) ^ 1, b % 16 + 1);
            var info = fsmz.DinfoSnapshot(); var dinfoAddress = FsmzAt(15);
            Field("Directory variant", $"{fsmz.DirectoryLimit} files + header", "Slots are 32 bytes; standard directory uses 8 blocks, extended uses 16.");
            Field("Volume number", info[0], "DINFO byte 0", addresses: dinfoAddress);
            Field("File-area start", info[1], "DINFO byte 1 (256-byte logical block)", addresses: dinfoAddress);
            Field("Used blocks", Word(info, 2), "DINFO word at +2; includes reserved area", addresses: dinfoAddress);
            Field("Last block", Word(info, 4), "DINFO word at +4, inclusive", addresses: dinfoAddress);
            Field("Allocation bitmap", "2000 bits", "DINFO +6..255; bit 0 corresponds to file-area start. LSB first.", info[6..], dinfoAddress);
            Add(DskStructureTab.RawStructure, "DINFO", "256 decoded bytes", "Logical block 15; physical track 1 / R16. Stored bytes are inverted; this is the filesystem-decoded buffer.", info, dinfoAddress);
            for (int slot = 0; slot <= fsmz.DirectoryLimit; slot++)
            {
                var raw = fsmz.ReadDirectorySlot(slot); var file = files.FirstOrDefault(f => f.Key == slot.ToString());
                int block = 16 + slot / 8, offset = slot % 8 * 32;
                string state = slot == 0 ? "Directory header (80 01)" : raw[0] == 0 ? "Unused" : file?.Name ?? "Occupied";
                string detail = $"Logical block {block}, byte offset 0x{offset:X}; 32 bytes.\nType=0x{raw[0]:X2}; lock=0x{raw[18]:X2}; size={Word(raw,20)}; LOAD=0x{Word(raw,22):X4}; EXEC=0x{Word(raw,24):X4}; start block={Word(raw,30)}.";
                Add(DskStructureTab.Directory, $"Slot {slot}", state, detail, raw, FsmzAt(block));
                Add(DskStructureTab.RawStructure, $"Directory slot {slot}", "32 decoded bytes", detail, (byte[])raw.Clone(), FsmzAt(block));
            }
            for (int b = 0; b < doc.Image.Tracks.Count * 16; b++)
            {
                int relative = b - fsmz.FileAreaBlock;
                bool used = relative >= 0 && relative < 2000 && (info[6 + relative / 8] & (1 << (relative % 8))) != 0;
                var addresses = FsmzAt(b);
                string owner = ownersByBlock.GetValueOrDefault(b, "");
                string state = b == 15 ? "DINFO" : b >= 16 && b < 16 + (fsmz.DirectoryLimit + 1) / 8 ? "Directory" : b < fsmz.FileAreaBlock ? "Reserved / boot" : b > fsmz.LastBlock ? "Outside declared area" : used ? "Used (bitmap)" : "Free (bitmap)";
                Add(DskStructureTab.Allocation, $"Block {b}", state, $"256 bytes; {state}\nBitmap index: {(relative >= 0 && relative < 2000 ? relative.ToString() : "not represented")}\nDirectory owners: {owner}", addresses: addresses);
            }
        }
        else if (doc.FileSystem is MrsFileSystem mrs)
        {
            DskSectorAddress[] MrsAt(int b) => At(b / 9, b % 9 + 1);
            var fat = mrs.AllocationSnapshot(); var directory = mrs.DirectorySnapshot();
            Field("FAT layout", $"Block 36, {mrs.FatSectorCount} × 512 bytes", "FAT byte index = 512-byte linear block; stored bytes inverted.");
            Field("Reserved blocks", $"0..{mrs.DataBlock - 1}", "Actual FAT values distinguish FF reserved, FA FAT and FD directory; mismatches are shown in Analyzer.");
            Field("Directory area", $"Blocks {mrs.DirectoryBlock}..{mrs.DirectoryBlock + mrs.DirectorySectorCount - 1}", $"{mrs.DirectorySectorCount} sectors, 32-byte slots; includes native padding.");
            Field("Data start", mrs.DataBlock);
            Add(DskStructureTab.RawStructure, "FAT", $"{fat.Length} decoded bytes", "0=free; FF=reserved; FA=FAT; FD=directory; other values identify file owners.", fat, Enumerable.Range(36, mrs.FatSectorCount).SelectMany(MrsAt));
            Add(DskStructureTab.RawStructure, "Directory area", $"{directory.Length} decoded bytes", "Includes inactive slots and trailing native padding.", directory, Enumerable.Range(mrs.DirectoryBlock, mrs.DirectorySectorCount).SelectMany(MrsAt));
            for (int slot = 0; slot < directory.Length / 32; slot++)
            {
                var raw = directory.AsSpan(slot * 32, 32).ToArray(); var file = files.FirstOrDefault(f => f.Key == slot.ToString());
                string state = file == null ? raw.All(b => b == 0x1A) ? "Padding (1A)" : "Inactive" : $"{file.Name}.{file.Extension}";
                int block = mrs.DirectoryBlock + slot * 32 / 512, offset = slot * 32 % 512;
                string detail = $"Block {block}, byte offset 0x{offset:X}; file ID={raw[11]}; block count={Word(raw,14)}; LOAD(+0C)=0x{Word(raw,12):X4}; EXEC(+16)=0x{Word(raw,22):X4}. Exact byte size is not stored.";
                Add(DskStructureTab.Directory, $"Slot {slot}", state, detail, raw, MrsAt(block));
                Add(DskStructureTab.RawStructure, $"Directory slot {slot}", "32 decoded bytes", detail, (byte[])raw.Clone(), MrsAt(block));
            }
            for (int b = 0; b < Math.Min(doc.Image.Tracks.Count * 9, fat.Length); b++)
            {
                byte id = fat[b]; var owners = files.Where(f => f.StartBlock == id).Select(f => $"{f.Name}.{f.Extension}").ToArray();
                string state = id switch { 0 => "Free", 0xFF => "Reserved", 0xFA => "FAT", 0xFD => "Directory", _ => owners.Length == 0 ? "Orphan owner" : "Used" };
                Add(DskStructureTab.Allocation, $"Block {b}", state, $"FAT[{b}]=0x{id:X2}; {state}\nOwner file ID: {id}; files: {string.Join(", ", owners)}", addresses: MrsAt(b));
            }
        }
        else Field("Filesystem", "Structure decoding unavailable", "The filesystem is not recognized. No layout or metadata is guessed.");
        foreach (var issue in layout.Issues) Field($"{issue.Severity}: {issue.Code}", issue.Description, issue.Explanation);
        return new(layout, items, $"{doc.FileSystem.DisplayName} — read-only snapshot{(source.IsModified ? " (includes unsaved changes)" : "")}\n{layout.Issues.Count} analyzer issues. Raw structures are filesystem-decoded bytes (not inverted storage bytes).");
    }
}
