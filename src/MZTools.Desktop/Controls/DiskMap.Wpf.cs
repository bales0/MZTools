using Avalonia;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

public sealed partial class DiskMap
{
    private IEnumerable<DskSectorLayout> WpfOrdered(DskTrackLayout track, DiskSession session) => session.LogicalOrder
        ? track.Sectors.OrderBy(s=>s.LogicalBlocks.Count==0?int.MaxValue:s.LogicalBlocks.Min()).ThenBy(s=>s.PhysicalIndex) : track.Sectors;
    protected override Size MeasureOverride(Size availableSize)
    {
        if(Mode==0 && Session is {Layout: { } layout} session)
            return new Size((115+38*Math.Max(1,layout.Tracks.Select(t=>t.Sectors.Count).DefaultIfEmpty().Max()))*session.MapZoom,(layout.Tracks.Count+1)*25*session.MapZoom);
        if(Mode>0)return new Size(600,320);
        return base.MeasureOverride(availableSize);
    }
    private void RenderWpfTrackMap(DrawingContext dc,DiskSession session)
    {
        var layout=session.Layout!;double zoom=session.MapZoom;
        void Label(string text,double x,double y)=>dc.DrawText(new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,11*zoom,Brushes.Black),new Point(x*zoom,y*zoom));
        Label(session.LogicalOrder?"Logical order*":"Physical order",3,3);
        for(int col=0;col<layout.Tracks.Select(t=>t.Sectors.Count).DefaultIfEmpty().Max();col++)Label(col.ToString(),115+col*38+4,3);
        foreach(var track in layout.Tracks)
        {
            double y=(track.PhysicalIndex+1)*25;
            if(y*zoom>Bounds.Height)continue;
            Label($"{track.PhysicalIndex}: C{track.Cylinder} H{track.Side}",3,y+3);
            if(track.IsMissing){Label("Missing track (tsize=0)",115,y+3);continue;}
            int col=0;
            foreach(var sector in WpfOrdered(track,session))
            {
                var rect=new Rect((115+col*38)*zoom,y*zoom,36*zoom,23*zoom);
                bool selected=session.SectorSelection.Contains(sector.Address) || sector.Owners.Any(o=>session.FileSelection.Contains(o.FileKey));
                IBrush fill=selected?Brushes.CornflowerBlue:sector.HasIssue?Brushes.Khaki:
                    (sector.Role&(DskSectorRole.Boot|DskSectorRole.System|DskSectorRole.SystemFile|DskSectorRole.Directory|DskSectorRole.Fat|DskSectorRole.AllocationMap))!=0?Brushes.DarkOrange:sector.Role.HasFlag(DskSectorRole.Data)?Brushes.MediumSeaGreen:Brushes.White;
                dc.DrawRectangle(fill,new Pen(Brushes.DarkSlateGray),rect);
                if(selected)dc.DrawRectangle(null,new Pen(Brushes.DodgerBlue,3),rect);
                Label($"{(sector.HasIssue?"!":"")}{sector.R}",115+col*38+3,y+3);col++;
            }
        }
    }
    private DiskSectorRow? HitWpfTrackMap(Point point,DiskSession session)
    {
        double zoom=session.MapZoom;
        int row=(int)(point.Y/(25*zoom))-1,col=(int)((point.X/zoom-115)/38);
        if(point.X/zoom<115 || row<0 || row>=session.Layout!.Tracks.Count || col<0)return null;
        var sector=WpfOrdered(session.Layout.Tracks[row],session).ElementAtOrDefault(col);
        return sector is null?null:session.Sectors.FirstOrDefault(r=>r.Sector.Address==sector.Address);
    }
}
