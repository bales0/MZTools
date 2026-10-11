using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal sealed record DskGeneratedRegion(int? Descriptor, string Kind, long StartCell, long EndCell)
{
    public long Cells => EndCell - StartCell;
    public string DataOrigin => "Synthetic MFM • generated from sector descriptors";
}
internal static class DskGeneratedProjection
{
    internal static IReadOnlyList<DskGeneratedRegion> StoredRegions(PhysicalTrack track)
    {
        long count=track.BitCellCount; if(count==0) return [];
        var fields=new List<DskGeneratedRegion>();
        void Field(int descriptor,string kind,long start,long cells) {
            start=(start%count+count)%count; long end=start+Math.Min(cells,count);
            fields.Add(new(descriptor,kind,start,Math.Min(end,count)));
            if(end>count) fields.Add(new(descriptor,kind,0,end-count));
        }
        for(int i=0;i<track.Sectors.Count;i++) {
            var s=track.Sectors[i]; int sync=track.Encoding==PhysicalEncoding.Mfm?48:0;
            if(sync>0) Field(i,"Sync",s.HeaderCell,sync);
            Field(i,"IDAM",s.HeaderCell+sync,16); Field(i,"CHRN",s.HeaderCell+sync+16,64);
            Field(i,"Header CRC",s.HeaderCell+sync+80,32);
            if(sync>0) Field(i,"Sync",s.DataCell-16-sync,sync);
            Field(i,s.DataMark==0xF8?"Deleted DAM":"DAM",s.DataCell-16,16);
            Field(i,"DATA",s.DataCell,s.Data.Length*16L); Field(i,"Data CRC",s.DataCell+s.Data.Length*16L,32);
        }
        var result=new List<DskGeneratedRegion>();long cursor=0;
        foreach(var field in fields.OrderBy(f=>f.StartCell)) {
            if(field.StartCell>cursor) result.Add(new(null,"Undecoded",cursor,field.StartCell));
            if(field.EndCell>cursor) result.Add(field with {StartCell=Math.Max(cursor,field.StartCell)});
            cursor=Math.Max(cursor,field.EndCell);
        }
        if(cursor<count) result.Add(new(null,"Undecoded",cursor,count));
        return result;
    }
    internal static IReadOnlyList<DskGeneratedRegion> Regions(PhysicalTrack track)
    {
        var result = new List<DskGeneratedRegion>(); long cursor = 0;
        void Add(int? descriptor,string kind,long end) {
            end = Math.Clamp(end,cursor,track.BitCellCount);
            if(end>cursor) result.Add(new(descriptor,kind,cursor,end)); cursor=end;
        }
        for(int i=0;i<track.Sectors.Count;i++) {
            var s = track.Sectors[i];
            Add(null,"GAP",s.HeaderCell-192); Add(i,"Sync",s.HeaderCell);
            Add(i,"IDAM",s.HeaderCell+64); Add(i,"CHRN",s.HeaderCell+128); Add(i,"Header CRC",s.HeaderCell+160);
            Add(null,"GAP",s.DataCell-256); Add(i,"Sync",s.DataCell-64);
            Add(i,s.DataMark==0xF8?"Deleted DAM":"DAM",s.DataCell);
            Add(i,"DATA",s.DataCell+s.Data.Length*16L); Add(i,"Data CRC",s.DataCell+s.Data.Length*16L+32);
        }
        Add(null,"GAP",track.BitCellCount); return result;
    }
}
