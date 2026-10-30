using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal static class DskSectorSelection
{
    internal static HashSet<DskSectorAddress> Next(IEnumerable<DskSectorAddress> current,DskSectorAddress? anchor,
        DskSectorAddress target,IReadOnlyList<DskSectorAddress> order,bool control,bool shift)
    {
        var result=control?current.ToHashSet():new HashSet<DskSectorAddress>();
        if(shift && anchor!=null) {
            int a=order.ToList().IndexOf(anchor),b=order.ToList().IndexOf(target);
            if(a>=0 && b>=0) { for(int i=Math.Min(a,b);i<=Math.Max(a,b);i++) result.Add(order[i]); return result; }
        }
        if(!control || !result.Remove(target)) result.Add(target);
        return result;
    }
    internal static IReadOnlyDictionary<string,(int Selected,int Total)> Owners(DskLayoutModel layout,ISet<DskSectorAddress> selected)
        =>layout.Sectors.SelectMany(s=>s.Owners.Select(o=>(o.FileKey,s.Address))).Distinct().GroupBy(p=>p.FileKey)
            .ToDictionary(g=>g.Key,g=>(g.Count(p=>selected.Contains(p.Address)),g.Count()));
}
