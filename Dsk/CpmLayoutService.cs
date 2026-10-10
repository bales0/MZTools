using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MZTools;

internal sealed record CpmLayoutPreview(byte[] Snapshot, CpmDpb Dpb, bool CanApply, string Report);

internal static class CpmLayoutService
{
    internal static CpmLayoutPreview Preview(DskDocument document, CpmDpb input)
    {
        // Own the maps: editing the dialog after preview cannot change the planned layout.
        var dpb = input with { PhysicalTrackMap = input.PhysicalTrackMap?.ToArray(), PhysicalSectorMap = input.PhysicalSectorMap?.ToArray() };
        byte[] snapshot = document.Serialize();
        var errors = new List<string>();
        try
        {
            ValidateGeometry(document.Image, dpb);
            var analysis = DskAnalyzer.Analyze(document.Image, dpb);
            if (!CpmFileSystem.TryOpen(document.Image, dpb, out var fs) || fs == null)
                errors.Add("The existing CP/M parser rejected the directory/allocation.");
            if (analysis.Errors != 0) errors.Add("Analyzer reports errors or unsafe allocation.");
            string report = "Attach CP/M layout (interpretation only; image bytes are unchanged).\n" +
                "Layout is retained for this open document; DSK does not store a DPB. Reattach it after reopening.\n\n" +
                $"{dpb.Name}: SPT={dpb.Spt}, BSH={dpb.Bsh}, BLM={dpb.Blm}, EXM={dpb.Exm}, DSM={dpb.Dsm}, DRM={dpb.Drm}, " +
                $"AL0={dpb.Al0:X2}, AL1={dpb.Al1:X2}, CKS={dpb.Cks}, OFF={dpb.Off}, BlockSize={dpb.BlockSize}, Inverted={dpb.Inverted}\n" +
                DirectoryPreview(document.Image, dpb) +
                (fs == null ? "" : $"Used: {fs.UsedBytes} B; free: {fs.FreeBytes} B; free allocation blocks: {fs.FreeBytes / dpb.BlockSize}\nFiles: {fs.ReadDirectory().Count}\n" +
                    string.Join("\n", fs.ReadDirectory().Select(f => $"User {f.User}: {f.Name}.{f.Extension}; {f.Size} B; {f.Extents} extents; {f.Blocks} allocation blocks")) + "\n") + analysis.Report();
            return new(snapshot, dpb, errors.Count == 0, report + "\n" + string.Join("\n", errors));
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return new(snapshot, dpb, false, "Layout cannot be attached: " + e.Message);
        }
    }

    internal static void Apply(DskDocument document, CpmLayoutPreview preview)
    {
        if (!preview.CanApply) throw new InvalidOperationException("The preview contains layout errors.");
        if (!document.Serialize().AsSpan().SequenceEqual(preview.Snapshot)) throw new InvalidOperationException("The document changed since preview. Preview again.");
        var check = Preview(document, preview.Dpb);
        if (!check.CanApply) throw new InvalidDataException(check.Report);
        document.AttachCpmLayout(check.Dpb);
    }

    private static string DirectoryPreview(DskImage image, CpmDpb dpb)
    {
        var report = new StringBuilder("Directory candidates (raw extent metadata; allocation validity is reported by Analyzer below):\n");
        var device = new DskBlockDevice(image);
        int candidates = 0, valid = 0, invalid = 0, special = 0, deleted = 0;
        for (int slot = 0; slot <= dpb.Drm; slot++)
        {
            int position = slot * 32;
            var address = CpmFileSystem.MapByteOffset(dpb, position / dpb.BlockSize, position % dpb.BlockSize);
            byte[] raw = device.ReadSector(address.AbsoluteTrack, address.PhysicalSector, dpb.Inverted).AsSpan(address.Offset, 32).ToArray();
            if (raw[0] == 0xE5) { deleted++; continue; }
            if (raw[0] > 15) { special++; continue; }
            candidates++;
            bool fieldsValid = raw[12] <= 31 && raw[15] <= 128 && (raw[14] & 0x40) == 0;
            if (fieldsValid) valid++; else invalid++;
            if (candidates <= 500)
            {
                string name = Encoding.ASCII.GetString(raw.AsSpan(1, 11).ToArray().Select(b => (byte)(b & 127)).ToArray());
                report.AppendLine($"Slot {slot}; user {raw[0]}; name '{name}'; EX={raw[12]}, S2={raw[14]}, RC={raw[15]}; extent fields {(fieldsValid ? "valid" : "invalid")}");
            }
        }
        report.AppendLine($"Directory candidates: {candidates}; valid extent fields: {valid}; invalid extent fields: {invalid}; special entries: {special}; deleted/unused slots: {deleted}.");
        if (candidates > 500) report.AppendLine("Only the first 500 candidates are listed; all slots were inspected.");
        return report.ToString();
    }

    internal static void ValidateGeometry(DskImage image, CpmDpb dpb)
    {
        // The current CP/M block device supports 512-byte physical sectors only.
        if (dpb.Spt == 0 || dpb.Spt % 4 != 0) throw new InvalidDataException("SPT must be a positive multiple of four 128-byte records.");
        if (dpb.Bsh is < 3 or > 7 || dpb.BlockSize != (128 << dpb.Bsh) || dpb.Blm != (1 << dpb.Bsh) - 1)
            throw new InvalidDataException("BSH, BLM and derived block size are inconsistent (supported blocks: 1024..16384 B).");
        int expectedExm = (dpb.Dsm <= 255 ? 16 : 8) * dpb.BlockSize / 16384 - 1;
        if (expectedExm < 0 || dpb.Exm != expectedExm) throw new InvalidDataException("EXM does not match block size and allocation pointer width.");
        int allocation = (dpb.Al0 << 8) | dpb.Al1;
        int directoryBlocks = ((dpb.Drm + 1) * 32 + dpb.BlockSize - 1) / dpb.BlockSize;
        if (directoryBlocks > 16 || directoryBlocks > dpb.Dsm + 1 || allocation != (0xFFFF << (16 - directoryBlocks) & 0xFFFF))
            throw new InvalidDataException("AL0/AL1 must reserve exactly the leading directory blocks required by DRM.");
        if (dpb.PhysicalSectorMap != null && dpb.PhysicalTrackMap != null)
            throw new InvalidDataException("Specify either PhysicalTrackMap or PhysicalSectorMap, not both.");
        var reserved = DskDocumentFactory.GetSystemPhysicalTracks(dpb, image).ToHashSet();
        var occupied = new HashSet<(int Track, int Sector, int Offset)>();
        var device = new DskBlockDevice(image);
        for (int block = 0; block <= dpb.Dsm; block++)
            for (int offset = 0; offset < dpb.BlockSize; offset += 128)
            {
                var address = CpmFileSystem.MapByteOffset(dpb, block, offset);
                var sector = device.GetSector(address.AbsoluteTrack, address.PhysicalSector);
                var track = image.Tracks[address.AbsoluteTrack]!;
                if (track.Sectors.Count(s => s.SectorId == address.PhysicalSector) != 1 || sector.Data.Length != 512)
                    throw new InvalidDataException("The CP/M mapping requires unique sector IDs and 512-byte sectors.");
                if (reserved.Contains(address.AbsoluteTrack)) throw new InvalidDataException("System/data overlap in physical mapping.");
                if (!occupied.Add((address.AbsoluteTrack, address.PhysicalSector, address.Offset)))
                    throw new InvalidDataException("Physical mapping aliases multiple allocation records.");
            }
    }
}
