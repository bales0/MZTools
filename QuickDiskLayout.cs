using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal enum QuickDiskRegionKind { Count, Header, Framing, Payload, Crc, Gap, OutsideWindow, HostBlock, HostSector }

internal sealed record QuickDiskRegion(
    QuickDiskRegionKind Kind, long Start, long Length, string Name,
    int FileIndex = -1, int Size = 0, ushort Load = 0, ushort Execute = 0)
{
    public long End => Start + Length;
    public string Offset => $"0x{Start:X}";
    public string LoadText => FileIndex < 0 ? "—" : $"0x{Load:X4}";
    public string ExecuteText => FileIndex < 0 ? "—" : $"0x{Execute:X4}";
    public string FileNumber => FileIndex < 0 ? "—" : (FileIndex + 1).ToString();
}

// Coordinates are track bitcells for physical images and container bytes otherwise.
// Unassigned space is deliberately not called free: it may contain sync/carrier or unknown data.
internal sealed record QuickDiskLayout(
    TapeDocumentFormat Format, bool IsPreview, bool IsPhysical, long Length,
    long TrackFileOffset, long? WindowStart, long? WindowEnd,
    IReadOnlyList<QuickDiskRegion> Regions)
{
    internal byte[] Image { get; init; } = Array.Empty<byte>();
    internal IReadOnlyDictionary<QuickDiskRegion, byte[]> HostBytes { get; init; } = new Dictionary<QuickDiskRegion, byte[]>();
    internal byte[] GetBlockBytes(QuickDiskRegion region)
    {
        if (!Regions.Contains(region)) throw new ArgumentException("Block does not belong to this map.");
        if (HostBytes.TryGetValue(region, out var hostBytes)) return (byte[])hostBytes.Clone();
        if (!IsPhysical) return Image.AsSpan(checked((int)region.Start), checked((int)region.Length)).ToArray();
        bool decoded = region.Kind is not (QuickDiskRegionKind.Gap or QuickDiskRegionKind.OutsideWindow);
        int cellsPerByte = decoded ? 16 : 8;
        var bytes = new byte[checked((int)((region.Length + cellsPerByte - 1) / cellsPerByte))];
        for (long cell = 0; cell < region.Length; cell += decoded ? 2 : 1)
        {
            long position = region.Start + cell;
            if ((Image[TrackFileOffset + position / 8] & (1 << (int)(position % 8))) != 0)
            {
                long bit = decoded ? cell / 2 : cell;
                bytes[bit / 8] |= (byte)(1 << (int)(bit % 8));
            }
        }
        return bytes;
    }

    public string Unit => IsPhysical ? "track bitcells" : "file bytes";
    public string Summary => $"{(IsPreview ? "Preview of rebuilt image — unsaved, not original positions" : "Original image — read-only")}" +
        $" | {Format} | {Length:N0} {Unit}" +
        (IsPhysical ? $" | Track file offset 0x{TrackFileOffset:X}; data window [0x{WindowStart:X}, 0x{WindowEnd:X}) bitcells" : " | Logical layout; no physical track positions");

    public string Detail(QuickDiskRegion region) =>
        $"{region.Kind}: {region.Name}\nStart 0x{region.Start:X}, end 0x{region.End:X} (exclusive), length {region.Length:N0} {Unit}" +
        (IsPhysical ? $"\nContainer byte 0x{TrackFileOffset + region.Start / 8:X}, bit {region.Start % 8} (LSB-first)" : "") +
        (region.FileIndex >= 0 ? $"\nFile #{region.FileIndex + 1}: {region.Size:N0} B; LOAD {region.LoadText}, EXEC {region.ExecuteText}" : "");
}

internal static class QuickDiskLayoutBuilder
{
    public static QuickDiskLayout Build(TapeDocument document)
    {
        if (!document.IsQuickDisk) throw new InvalidOperationException("Not a QuickDisk document.");
        bool preview = document.IsModified || document.QuickDiskSourceImage == null;
        byte[] image = preview ? BuildPreviewImage(document) : document.QuickDiskSourceImage!;
        return Read(image, document.Format, preview);
    }

    internal static byte[] BuildPreviewImage(TapeDocument document)
    {
        if (document.IsReadOnlyQuickDisk) throw new InvalidOperationException("Non-SHARP/unknown QuickDisk content cannot be rebuilt by the SHARP writer.");
        var records = document.Records;
        return document.Format switch
        {
            TapeDocumentFormat.Qdf => QDFFileReader.BuildImage(records),
            // MZQ stores the same compact blocks, but has no fixed capacity or formatting tail.
            TapeDocumentFormat.Mzq => SharpLegacyQdCodec.BuildCompactImage(records),
            TapeDocumentFormat.QdSharpLegacy => QdImageReaderWriter.Write(records, QdImageFormat.SharpLegacyLogical),
            TapeDocumentFormat.QdHxc => QdImageReaderWriter.Write(records, QdImageFormat.HxcPhysical, document.QuickDiskProfile),
            TapeDocumentFormat.QdFlashFloppy => QdImageReaderWriter.Write(records, QdImageFormat.FlashFloppyPhysical, document.QuickDiskProfile),
            _ => throw new InvalidOperationException("Not a QuickDisk format.")
        };
    }

    internal static QuickDiskLayout Read(byte[] image, TapeDocumentFormat format, bool preview = false)
    {
        bool physical = format is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy;
        var blocks = new List<QuickDiskRegion>();
        if (physical)
        {
            var container = HxcFlashFloppyQdContainer.Parse(image,
                format == TapeDocumentFormat.QdHxc ? QdImageFormat.HxcPhysical : QdImageFormat.FlashFloppyPhysical);
            // Validate and use the same CRC-valid frame sequence as the import reader.
            var analysis = QuickDiskHostDetector.Detect(container.Track);
            var hostBytes = new Dictionary<QuickDiskRegion, byte[]>();
            if (analysis.Identification.IsNativeSharpMz)
            {
                var frames = SelectFrames(QuickDiskMfmCodec.FindSharpFrames(container.Track));
                AddFrames(blocks, frames, true);
            }
            else
            {
                void AddHostBlock(QuickDiskHostBlock block)
                {
                    if (block.CellLength == 0) return;
                    var region = new QuickDiskRegion(QuickDiskRegionKind.HostBlock, block.CellOffset, block.CellLength, block.Description, Size: block.Bytes.Length);
                    blocks.Add(region); hostBytes[region] = block.Bytes;
                }
                switch (analysis.Content)
                {
                    case RolandQuickDiskContent roland: foreach (var block in roland.Blocks) AddHostBlock(block); break;
                    case AkaiQuickDiskContent akai: AddHostBlock(akai.Block); break;
                    case Mo5QuickDiskContent mo5:
                        foreach (var sector in mo5.Sectors)
                        {
                            var region = new QuickDiskRegion(QuickDiskRegionKind.HostSector, sector.CellOffset, sector.CellLength,
                                $"MO5 physical #{sector.PhysicalSequence}, ID {sector.SectorId}, logical {sector.LogicalSector}; header {(sector.HeaderValid ? "valid" : "invalid")}, data {(sector.DataValid ? "valid" : "invalid")}{(sector.Duplicate ? "; DUPLICATE" : "")}", Size: sector.Data.Length);
                            blocks.Add(region); hostBytes[region] = sector.Data;
                        }
                        break;
                }
            }
            long length = container.Descriptor.Length * 8L;
            return new(format, preview, true, length, container.Descriptor.Offset,
                container.Descriptor.WindowStart * 8L, container.Descriptor.WindowEnd * 8L,
                FillGaps(blocks, length, container.Descriptor.WindowStart * 8L, container.Descriptor.WindowEnd * 8L)) { Image = image, HostBytes = hostBytes };
        }
        if (format == TapeDocumentFormat.Qdf)
        {
            if (image.Length < 16 || !image.AsSpan(0, 11).SequenceEqual("-QD format-"u8))
                throw new InvalidDataException("Invalid QDF container signature.");
            var frames = SelectFrames(SharpQdFrameCodec.FindFrames(image));
            if (frames.Count == 0) throw new InvalidDataException("QDF FNBLK frame not found.");
            // The byte-stream scanner expresses positions in hypothetical MFM bitcells.
            AddFrames(blocks, frames, false);
        }
        else if (format is TapeDocumentFormat.Mzq or TapeDocumentFormat.QdSharpLegacy)
        {
            // Both formats share validated compact block storage; only QD has a fixed tail.
            var records = SharpLegacyQdCodec.Read(image);
            blocks.Add(new(QuickDiskRegionKind.Count, 0, 8, $"FNBLK: {records.Count * 2} blocks / {records.Count} files (CRC marker)"));
            int position = 8;
            for (int index = 0; index < records.Count; index++)
            {
                var record = records[index];
                AddFileRegion(blocks, QuickDiskRegionKind.Header, position, 74, record, index);
                position += 74;
                AddFileRegion(blocks, QuickDiskRegionKind.Framing, position, 7, record, index);
                AddFileRegion(blocks, QuickDiskRegionKind.Payload, position + 7, record.Body.DataSize, record, index);
                AddFileRegion(blocks, QuickDiskRegionKind.Crc, position + 7 + record.Body.DataSize, 3, record, index);
                position += 10 + record.Body.DataSize;
            }
        }
        else throw new InvalidOperationException("Not a QuickDisk format.");
        return new(format, preview, false, image.Length, 0, null, null, FillGaps(blocks, image.Length)) { Image = image };
    }

    private static IReadOnlyList<SharpQdFrame> SelectFrames(IReadOnlyList<SharpQdFrame> candidates)
    {
        var frames = new List<SharpQdFrame>();
        foreach (var frame in candidates.OrderBy(f => f.Position))
            if (!frames.Any(f => Math.Abs(f.Position - frame.Position) <= 15 && f.Bytes.AsSpan().SequenceEqual(frame.Bytes)))
                frames.Add(frame);
        foreach (var count in frames.Where(f => f.Type == 0x02))
        {
            var selected = new List<SharpQdFrame> { count };
            selected.AddRange(frames.Where(f => f.Position > count.Position && f.Type is 0x00 or 0x05).Take(count.Bytes[1]));
            try
            {
                SharpQdFrameCodec.DecodeRecords(selected);
                return selected;
            }
            catch (InvalidDataException) { }
        }
        if (frames.Count > 0) throw new InvalidDataException("No consistent QuickDisk frame sequence.");
        return Array.Empty<SharpQdFrame>(); // unformatted physical blank
    }

    private static void AddFrames(List<QuickDiskRegion> blocks, IReadOnlyList<SharpQdFrame> frames, bool physical)
    {
        if (frames.Count == 0) return;
        var records = SharpQdFrameCodec.DecodeRecords(frames).Select(b => TapeRecord.FromLegacy(b.Item1, b.Item2)).ToList();
        long scale = physical ? 16 : 1;
        long Position(SharpQdFrame frame) => physical ? frame.Position : frame.Position / 16;
        // Physical positions start on the first data cell, excluding its preceding clock.
        long Length(int bytes) => bytes * scale - (physical ? 1 : 0);
        blocks.Add(new(QuickDiskRegionKind.Count, Position(frames[0]), Length(4),
            $"FNBLK: {records.Count * 2} blocks / {records.Count} files (CRC valid)"));
        for (int index = 0; index < records.Count; index++)
        {
            var header = frames[1 + index * 2];
            var body = frames[2 + index * 2];
            var record = records[index];
            AddFileRegion(blocks, QuickDiskRegionKind.Header, Position(header), Length(header.Bytes.Length), record, index);
            AddFileRegion(blocks, QuickDiskRegionKind.Framing, Position(body), 4 * scale, record, index);
            AddFileRegion(blocks, QuickDiskRegionKind.Payload, Position(body) + 4 * scale, record.Body.DataSize * scale, record, index);
            AddFileRegion(blocks, QuickDiskRegionKind.Crc, Position(body) + (4L + record.Body.DataSize) * scale, Length(2), record, index);
        }
    }

    private static void AddFileRegion(List<QuickDiskRegion> regions, QuickDiskRegionKind kind,
        long start, long length, TapeRecord record, int index)
    {
        if (length == 0) return;
        regions.Add(new(kind, start, length,
            SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname).TrimEnd(), index,
            record.Body.DataSize, record.Header.MzfStart, record.Header.MzfExec));
    }

    private static IReadOnlyList<QuickDiskRegion> FillGaps(List<QuickDiskRegion> blocks, long length,
        long? windowStart = null, long? windowEnd = null)
    {
        var result = new List<QuickDiskRegion>();
        long position = 0;
        void Gap(long end)
        {
            while (position < end)
            {
                bool outside = windowStart.HasValue && (position < windowStart || position >= windowEnd);
                long next = position < windowStart ? Math.Min(end, windowStart.Value) :
                    position < windowEnd ? Math.Min(end, windowEnd.Value) : end;
                result.Add(new(outside ? QuickDiskRegionKind.OutsideWindow : QuickDiskRegionKind.Gap,
                    position, next - position, outside ? "Outside data window" : "Sync / gap / unassigned data (not necessarily free)"));
                position = next;
            }
        }
        foreach (var block in blocks.OrderBy(b => b.Start))
        {
            if (block.Start < position || block.Length <= 0 || block.End > length)
                throw new InvalidDataException("QuickDisk map contains overlapping or out-of-range blocks.");
            Gap(block.Start);
            result.Add(block);
            position = block.End;
        }
        Gap(length);
        return result;
    }
}
