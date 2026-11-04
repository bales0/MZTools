using Avalonia;
using Avalonia.Media;

namespace MZTools.Desktop.Controls;

public sealed partial class RegionMap
{
    internal const int WrappedRows = 8;
    internal const double WrappedLabelWidth = 110, WrappedRowHeight = 31;
    private double WrappedTrackWidth => Math.Max(600, Bounds.Width) - WrappedLabelWidth - 12;
    internal static IBrush RegionColor(QuickDiskRegionKind kind) => kind switch
    {
        QuickDiskRegionKind.Count => Brushes.MediumPurple,
        QuickDiskRegionKind.Header => Brushes.DarkOrange,
        QuickDiskRegionKind.Payload or QuickDiskRegionKind.HostBlock or QuickDiskRegionKind.HostSector => Brushes.MediumSeaGreen,
        QuickDiskRegionKind.Framing or QuickDiskRegionKind.Crc => Brushes.Khaki,
        QuickDiskRegionKind.OutsideWindow => Brushes.DimGray,
        _ => Brushes.LightGray
    };
    internal QuickDiskRegion? HitWrapped(Point point)
    {
        if (Session?.Snapshot is not { } snapshot || point.X < WrappedLabelWidth || point.X >= WrappedLabelWidth + WrappedTrackWidth) return null;
        int row = (int)(point.Y / WrappedRowHeight) - 1;
        if (row < 0 || row >= WrappedRows || point.Y % WrappedRowHeight >= WrappedRowHeight - 5) return null;
        double position = (row + (point.X-WrappedLabelWidth)/WrappedTrackWidth)*snapshot.Layout.Length/WrappedRows;
        return snapshot.Layout.Regions.FirstOrDefault(r=>r.Start<=position && r.End>position);
    }
    private void RenderWrapped(DrawingContext dc, QuickDiskVisualSnapshot snapshot)
    {
        void Label(string text, double x, double y) => Text(dc, text, new Point(x,y));
        Label(snapshot.Layout.IsPhysical ? "Track bitcells" : "File bytes",3,4);
        Label("Headers: orange | Data: green | CRC: yellow | Gaps: gray | Selected: blue",WrappedLabelWidth,4);
        for (int row=0;row<WrappedRows;row++)
        {
            double first=snapshot.Layout.Length*(double)row/WrappedRows, last=snapshot.Layout.Length*(double)(row+1)/WrappedRows;
            double y=(row+1)*WrappedRowHeight;
            Label($"0x{(long)first:X}",3,y+6);
            var selected=new List<Rect>();
            foreach (var region in snapshot.Layout.Regions.Where(r=>r.End>first && r.Start<last))
            {
                double x=WrappedLabelWidth+(Math.Max(first,region.Start)-first)/(last-first)*WrappedTrackWidth;
                double width=(Math.Min(last,region.End)-Math.Max(first,region.Start))/(last-first)*WrappedTrackWidth;
                var rect=new Rect(x,y,Math.Max(.5,width),WrappedRowHeight-5);
                bool active=region==Session!.SelectedRegion || (Session.SelectedRegion is null && region.FileIndex>=0 && Session.Selection.Contains(region.FileIndex));
                dc.DrawRectangle(active?Brushes.CornflowerBlue:RegionColor(region.Kind),new Pen(Brushes.DarkSlateGray),rect);
                if(active)selected.Add(rect);
            }
            dc.DrawRectangle(null,new Pen(Brushes.DarkGray),new Rect(WrappedLabelWidth,y,WrappedTrackWidth,WrappedRowHeight-5));
            foreach(var rect in selected)dc.DrawRectangle(null,new Pen(Brushes.DodgerBlue,3),rect);
        }
    }
}
