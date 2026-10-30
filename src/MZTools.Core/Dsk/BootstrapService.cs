using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace MZTools;

internal sealed record BootstrapMetadata(string Name, byte FileType, ushort Size, ushort Load, ushort Execute);
internal static class BootstrapService
{
    internal static byte[] ExportBootstrap(DskDocument document)
    {
        Export(document); // Recognition and unique header validation.
        var track = document.Image.Tracks[1]!;
        if (track.Sectors.Count != 16 || track.Sectors.Any(s => s.Data.Length != 256) ||
            !track.Sectors.Select(s => (int)s.SectorId).Order().SequenceEqual(Enumerable.Range(1, 16)))
            throw new InvalidDataException("Bootstrap track export requires unique sectors 1..16 × 256 B.");
        // Complete native boot-track code, in logical sector-ID order, decoded.
        return track.Sectors.OrderBy(s => s.SectorId).SelectMany(s => s.Data.Select(b => (byte)(b ^ 255))).ToArray();
    }
    internal static DskHexEditPreview PreviewBootstrap(DskDocument document, byte[] decodedTrack, bool clear = false)
    {
        byte[] original = ExportBootstrap(document);
        if (decodedTrack.Length != original.Length) throw new InvalidDataException("Bootstrap replacement requires exactly 4096 decoded bytes.");
        if (!clear && !decodedTrack.AsSpan(1, 6).SequenceEqual("IPLPRO"u8)) throw new InvalidDataException("Replacement does not contain an IPLPRO header.");
        var sectors = DskAnalyzer.Analyze(document).Sectors.Where(s => s.Track == 1).OrderBy(s => s.R).ToArray();
        // Metadata overlap is explicit in the preview and requires confirmation.
        // Clear removes boot code while retaining independently owned filesystem structures.
        byte[] data = (byte[])decodedTrack.Clone();
        if (clear)
            for (int i = 0; i < sectors.Length; i++)
                if ((sectors[i].Role & (DskSectorRole.Directory | DskSectorRole.AllocationMap | DskSectorRole.Fat | DskSectorRole.SystemFile)) != 0)
                    original.AsSpan(i * 256, 256).CopyTo(data.AsSpan(i * 256));
        var session = new DskHexEditSession(document.Serialize(), original,
            sectors.Select((s, i) => new DskHexSegment(checked((int)s.FileOffset), i * 256, 256, true)).ToArray(),
            "IPLPRO complete boot track (4096 decoded bytes, logical sector IDs 1..16).\n" +
            string.Join("\n", sectors.Select(s => $"R={s.R}: {s.Role}; owners: {s.Files}")) +
            "\nReplacement can overlap filesystem structures. Clear preserves directory/FAT/allocation structures. Bootability is not certified.",
            sectors.Aggregate(DskSectorRole.Boot, (role, s) => role | s.Role), document.AttachedDpb);
        return DskHexEditService.Preview(session, data);
    }
    internal static byte[] Export(DskDocument document)
    {
        var track = document.Image.Tracks.Count > 1 ? document.Image.Tracks[1] : null;
        if (track == null || track.Sectors.Count(s => s.SectorId == 1 && s.Data.Length == 256) != 1) throw new InvalidDataException("No unique Sharp IPLPRO header sector is available.");
        byte[] header = new DskBlockDevice(document.Image).ReadSector(1, 1, true);
        if (!header.AsSpan(1, 6).SequenceEqual("IPLPRO"u8)) throw new InvalidDataException("IPLPRO bootstrap was not recognized.");
        return header;
    }
    internal static BootstrapMetadata Inspect(DskDocument document)
    {
        var header = Export(document);
        return new(SharpMzEncoding.ConvertMzfNameToASCIIString(header.AsSpan(7, 13).ToArray()), header[0], BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)), BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)), BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(24)));
    }
    internal static DskHexEditPreview PreviewMetadata(DskDocument document, BootstrapMetadata metadata)
    {
        byte[] header = Export(document);
        if (metadata.Name.Length > 13 || metadata.Name.Any(c => c < 32 || c > 126)) throw new InvalidDataException("IPLPRO name must be at most 13 printable ASCII characters.");
        // Size is safe only when existing payload interpretation does not change.
        if (metadata.Size != BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20))) throw new InvalidDataException("Metadata editing cannot change the existing payload size. Replace the bootstrap header only after checking its payload/layout impact.");
        header[0] = metadata.FileType; header.AsSpan(7, 13).Fill(13);
        SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(metadata.Name).CopyTo(header, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), metadata.Load); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), metadata.Execute);
        return PreviewHeader(document, header);
    }
    internal static DskHexEditPreview PreviewHeader(DskDocument document, byte[] decodedHeader)
    {
        if (decodedHeader.Length != 256) throw new InvalidDataException("A bootstrap header must contain exactly 256 decoded bytes.");
        var layout = DskAnalyzer.Analyze(document); var sector = layout.Sectors.Single(s => s.Track == 1 && s.R == 1 && s.DataLength == 256);
        var session = new DskHexEditSession(document.Serialize(), Export(document), [new(checked((int)sector.FileOffset), 0, 256, true)],
            "IPLPRO bootstrap header (decoded bytes). Payload/system code is unchanged. Replacement or clear may prevent booting.\n" + sector.Detail,
            sector.Role | DskSectorRole.Boot, document.AttachedDpb);
        return DskHexEditService.Preview(session, decodedHeader);
    }
}
