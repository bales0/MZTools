using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace MZTools;

internal enum DskRawOrder { Physical, SectorId }
internal static class DskRawRangeService
{
    private static DskSectorLayout[] Selection(DskDocument document, IReadOnlyList<DskSectorAddress> addresses, DskRawOrder order)
    {
        if (addresses.Count == 0 || addresses.Distinct().Count() != addresses.Count) throw new InvalidDataException("Select distinct physical sectors.");
        var layout = DskAnalyzer.Analyze(document);
        var sectors = addresses.Select(a => layout.Sectors.SingleOrDefault(s => s.Address == a) ?? throw new InvalidDataException("Selected sector does not exist.")).ToArray();
        if (order == DskRawOrder.SectorId && sectors.GroupBy(s => s.Track).Any(g => document.Image.Tracks[g.Key]!.Sectors.GroupBy(s => s.SectorId).Any(ids => ids.Count() != 1)))
            throw new InvalidDataException("Sector-ID order is ambiguous because a selected track has duplicate IDs.");
        return order == DskRawOrder.Physical ? sectors.OrderBy(s => s.Track).ThenBy(s => s.PhysicalIndex).ToArray() : sectors.OrderBy(s => s.Track).ThenBy(s => s.R).ToArray();
    }
    internal static byte[] Export(DskDocument document, IReadOnlyList<DskSectorAddress> addresses, DskRawOrder order) => Selection(document, addresses, order).SelectMany(s => s.Data).ToArray();
    internal static DskHexEditPreview PreviewImport(DskDocument document, IReadOnlyList<DskSectorAddress> addresses, DskRawOrder order, byte[] data)
    {
        var sectors = Selection(document, addresses, order); int required = sectors.Sum(s => s.DataLength);
        if (data.Length != required) throw new InvalidDataException($"Import requires exactly {required} bytes; got {data.Length}.");
        int bufferOffset = 0;
        var segments = sectors.Select(s => { var segment = new DskHexSegment(checked((int)s.FileOffset), bufferOffset, s.DataLength, false); bufferOffset += s.DataLength; return segment; }).ToArray();
        var session = new DskHexEditSession(document.Serialize(), sectors.SelectMany(s => s.Data).ToArray(), segments,
            $"Import {sectors.Length} sectors in {order} order", sectors.Aggregate(DskSectorRole.Unknown, (role, s) => role | s.Role), document.AttachedDpb);
        return DskHexEditService.Preview(session, data);
    }
}
