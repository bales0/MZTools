using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed class DskVisualCanvas : FrameworkElement
{
    internal enum ViewKind { Track, Surface, Generated }
    internal DskVisualSnapshot? Snapshot { get; set; }
    internal int TrackIndex { get; set; }
    internal int Side { get; set; }
    internal ViewKind Kind { get; set; }
    internal bool Proportional { get; set; }
    internal string Layer { get; set; } = "Role";
    internal DskSectorAddress? Selected { get; set; }
    internal HashSet<DskSectorAddress> Selection { get; set; }=new();
    internal HashSet<DskSectorAddress> Related { get; set; } = new();
    internal HashSet<DskSectorAddress>? Filtered { get; set; }
    internal PhysicalTrack? Generated { get; set; }
    internal event Action<int,DskSectorAddress?,bool>? Activated;
    internal event Action? ZoomChanged;
    private double zoom = 1;
    private Vector pan;
    private Point? drag;
    private bool moved;
    private int clickCount;
    private readonly Dictionary<(int Track,int Sector),Geometry> geometry = new();
    private string cacheKey = "";
    private readonly Dictionary<(int Side,bool Proportional),DskSurfaceRenderModel> surfaceModels=new();
    internal double Zoom => zoom;
    internal DskVisualCanvas() { Focusable=true; ClipToBounds=true; MinHeight=150; }
    internal void Fit() { zoom=1; pan=default; InvalidateVisual(); ZoomChanged?.Invoke(); }
    internal void ZoomBy(double factor,Point? anchor=null) {
        var point=anchor??new Point(ActualWidth/2,ActualHeight/2); var before=BasePoint(point);
        double previous=zoom; zoom=Math.Clamp(zoom*factor,1,60);
        pan=anchor==null?pan*(zoom/previous):new Vector(point.X-before.X*zoom-ActualWidth*.5*(1-zoom),point.Y-before.Y*zoom-ActualHeight*.5*(1-zoom));
        InvalidateVisual(); ZoomChanged?.Invoke();
    }
    internal void PanBy(Vector delta) { pan+=delta; InvalidateVisual(); }
    internal void Refresh() { geometry.Clear(); cacheKey=""; surfaceModels.Clear(); InvalidateVisual(); }
    private DskSurfaceRenderModel SurfaceModel(int side) {
        var key=(side,Proportional);
        if(!surfaceModels.TryGetValue(key,out var model)) surfaceModels[key]=model=new(Snapshot!,side,Proportional);
        return model;
    }
    internal IEnumerable<int> SurfaceSides => Side<0?Enumerable.Range(0,Snapshot?.Sides??0):new[] {Side};
    private int SurfaceCount => Side<0?Math.Max(1,Snapshot?.Sides??1):1;
    private Point SurfaceCenter(int side) => new((Side<0?side+.5:.5)*ActualWidth/SurfaceCount,ActualHeight/2);
    private double SurfaceRadius => Math.Max(1,Math.Min(ActualWidth/SurfaceCount,ActualHeight)/2-20);
    private Rect Visible => new(BasePoint(new(0,0)),BasePoint(new(ActualWidth,ActualHeight)));
    private Matrix Transform => new(zoom,0,0,zoom,pan.X+ActualWidth*.5*(1-zoom),pan.Y+ActualHeight*.5*(1-zoom));
    private Point BasePoint(Point p) { var m=Transform; m.Invert(); return m.Transform(p); }
    private Rect TrackRect => new(24,55,Math.Max(1,ActualWidth-48),Math.Max(55,Math.Min(110,ActualHeight-95)));
    private void Text(DrawingContext dc,string text,Point point,double size=12,Brush? brush=null) => dc.DrawText(
        new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,
            brush??SystemColors.WindowTextBrush,VisualTreeHelper.GetDpi(this).PixelsPerDip),point);
    private Brush Fill(DskVisualSector s)
    {
        if(Filtered != null && !Filtered.Contains(s.Address)) return SystemColors.ControlBrush;
        return Layer switch {
            "Issue severity" => s.Issues.Any(i=>i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe) ? Brushes.Crimson : s.Issues.Any(i=>i.Severity==DskIssueSeverity.Warning) ? Brushes.DarkOrange : SystemColors.ControlLightBrush,
            "FDC status" or "CRC / deleted mark" => s.St1!=0 || s.St2!=0 ? Brushes.Crimson : SystemColors.ControlLightBrush,
            "Data length anomalies" => s.DeclaredBytes!=s.StoredBytes || s.ParsedNBytes!=s.StoredBytes ? Brushes.DarkOrange : SystemColors.ControlLightBrush,
            "Ownership" => s.Owners.Count>0 ? Brushes.MediumPurple : SystemColors.ControlLightBrush,
            "Current selection" => Selection.Contains(s.Address) || s.Address==Selected ? DiskMapVisuals.Selection : Related.Contains(s.Address) ? Brushes.Gold : SystemColors.ControlLightBrush,
            _ => (s.Role & (DskSectorRole.Boot|DskSectorRole.System|DskSectorRole.Directory|DskSectorRole.Reserved|DskSectorRole.AllocationMap|DskSectorRole.Fat|DskSectorRole.NativeIpl))!=0 ? DiskMapVisuals.Metadata :
                s.Role.HasFlag(DskSectorRole.Free) ? SystemColors.ControlLightBrush : s.Role.HasFlag(DskSectorRole.Data) ? DiskMapVisuals.Data : SystemColors.ControlLightBrush
        };
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(SystemColors.WindowBrush,null,new Rect(RenderSize));
        if(Snapshot == null) return;
        dc.PushTransform(new MatrixTransform(Transform));
        if(Kind==ViewKind.Surface) DrawSurface(dc); else if(Kind==ViewKind.Generated) DrawGenerated(dc); else DrawTrack(dc);
        dc.Pop();
    }
    private void DrawTrack(DrawingContext dc)
    {
        var track=Snapshot!.Tracks.FirstOrDefault(t=>t.PhysicalIndex==TrackIndex); if(track==null) return;
        Text(dc,track.Label,new(24,12));
        if(track.Missing || track.Sectors.Count==0) { Text(dc,track.Missing?"Missing / unformatted track — no sector data":"Empty track — no sector descriptors",new(24,65)); return; }
        var r=TrackRect;
        foreach(var span in DskSurfaceRenderModel.Spans(track.Sectors,Proportional)) {
            var s=track.Sectors[span.Address.Sector];
            var box=new Rect(r.X+r.Width*span.Start,r.Y,Math.Max(.25,r.Width*(span.End-span.Start)-2),r.Height);
            dc.DrawRectangle(Fill(s),new Pen(SystemColors.WindowTextBrush,.6),box);
            if(Selection.Contains(s.Address) || s.Address==Selected || Related.Contains(s.Address)) DiskMapVisuals.DrawSelection(dc,box,Selection.Contains(s.Address) || s.Address==Selected);
            dc.PushClip(new RectangleGeometry(box));
            Brush ink=Fill(s) is SolidColorBrush color && color.Color.R+color.Color.G+color.Color.B<300 ? Brushes.White : Brushes.Black;
            Text(dc,$"{(s.IssueCount>0?"! ":"")}#{s.Descriptor} R{s.R}",new(box.X+3,box.Y+5),11,ink);
            if(box.Width>65) { Text(dc,$"{s.StoredBytes} B",new(box.X+3,box.Y+24),11,ink); Text(dc,s.Role.ToString(),new(box.X+3,box.Y+43),10,ink); }
            dc.Pop();
        }
        Text(dc,"Descriptor index → (equal / normalized stored bytes; not angular length)",new(24,r.Bottom+10),11);
    }
    private Geometry Wedge(DskSurfaceBand band,DskVisualSpan span,Point center,double radius)
    {
        var key=(band.Track,span.Address.Sector); if(geometry.TryGetValue(key,out var value)) return value;
        double a=span.Start+.0005,b=span.End-.0005; if(b<a) { a=span.Start; b=span.End; }
        double inner=(band.Inner+.0004)*radius,outer=(band.Outer-.0004)*radius;
        Point At(double angle,double r) => new(center.X+Math.Sin(angle*Math.PI*2)*r,center.Y-Math.Cos(angle*Math.PI*2)*r);
        var g=new StreamGeometry(); using(var c=g.Open()) {
            c.BeginFigure(At(a,outer),true,true);
            // Two arcs support a single-descriptor full circle without degenerate endpoints.
            double mid=(a+b)/2;
            c.ArcTo(At(mid,outer),new(outer,outer),0,false,SweepDirection.Clockwise,true,false);
            c.ArcTo(At(b,outer),new(outer,outer),0,false,SweepDirection.Clockwise,true,false);
            c.LineTo(At(b,inner),true,false);
            c.ArcTo(At(mid,inner),new(inner,inner),0,false,SweepDirection.Counterclockwise,true,false);
            c.ArcTo(At(a,inner),new(inner,inner),0,false,SweepDirection.Counterclockwise,true,false);
        } g.Freeze(); geometry[key]=g; return g;
    }
    private void DrawSurface(DrawingContext dc)
    {
        string key=$"{Side}:{Proportional}:{ActualWidth}:{ActualHeight}";
        if(cacheKey!=key) { geometry.Clear(); cacheKey=key; }
        foreach(int side in SurfaceSides) {
        var center=SurfaceCenter(side); double radius=SurfaceRadius;
        var model=SurfaceModel(side);
        foreach(var band in model.Bands) {
            var track=Snapshot!.Tracks.First(t=>t.PhysicalIndex==band.Track);
            if(track.Sectors.Count==0) {
                var pen=new Pen(SystemColors.GrayTextBrush,Math.Max(.5,(band.Outer-band.Inner)*radius*.7)) { DashStyle=DashStyles.Dot };
                dc.DrawEllipse(null,pen,center,(band.Inner+band.Outer)*radius/2,(band.Inner+band.Outer)*radius/2); continue;
            }
            foreach(var span in band.Sectors) {
                var s=track.Sectors[span.Address.Sector]; var g=Wedge(band,span,center,radius);
                if(!g.Bounds.IntersectsWith(Visible)) continue;
                dc.DrawGeometry(Fill(s),new Pen(SystemColors.WindowTextBrush,.25),g);
                if(Selection.Contains(s.Address) || s.Address==Selected || Related.Contains(s.Address)) {
                    bool selected=Selection.Contains(s.Address) || s.Address==Selected;
                    dc.DrawGeometry(null,new Pen(selected?Brushes.DodgerBlue:Brushes.Gold,2/zoom),g);
                }
            }
        }
        Text(dc,$"Side {side}",new(center.X-20,center.Y-6),11);
        }
    }
    private void DrawGenerated(DrawingContext dc)
    {
        if(Generated==null) { Text(dc,"Choose Generate MFM to create a synthetic projection.",new(24,40)); return; }
        var r=TrackRect;
        foreach(var region in DskGeneratedProjection.Regions(Generated)) {
            double x=r.X+r.Width*region.StartCell/Generated.BitCellCount, width=r.Width*region.Cells/Generated.BitCellCount;
            Brush color=region.Kind=="DATA"?Brushes.MediumSeaGreen:region.Kind.Contains("CRC")?Brushes.MediumPurple:region.Kind.Contains("AM")?Brushes.DarkOrange:SystemColors.ControlLightBrush;
            var box=new Rect(x,r.Y,Math.Max(.1,width),r.Height); dc.DrawRectangle(color,new Pen(SystemColors.WindowTextBrush,.3),box);
            if(region.Descriptor is int index && (Selection.Contains(new(TrackIndex,index)) || Selected==new DskSectorAddress(TrackIndex,index))) dc.DrawRectangle(null,new Pen(DiskMapVisuals.Selection,2),box);
            if(width>35) { dc.PushClip(new RectangleGeometry(box)); Text(dc,region.Kind,new(x+2,r.Y+8),10,Brushes.Black); dc.Pop(); }
        }
        Text(dc,$"0 … {Generated.BitCellCount:N0} generated bitcells • nominal {Generated.BitRate} bit/s / {Generated.Rpm} RPM",new(24,r.Bottom+12),11);
    }
    internal (int Track,DskSectorAddress? Sector)? Hit(Point point)
    {
        if(Snapshot==null) return null; var p=BasePoint(point);
        if(Kind==ViewKind.Surface) {
            foreach(int side in SurfaceSides) {
                var center=SurfaceCenter(side);
                double x=(p.X-center.X)/SurfaceRadius,y=(p.Y-center.Y)/SurfaceRadius;
                double radius=Math.Sqrt(x*x+y*y);
                var hit=SurfaceModel(side).Hit(x,y);
                if(hit!=null) return hit;
            }
            return null;
        }
        var r=TrackRect; if(!r.Contains(p)) return null;
        double fraction=(p.X-r.X)/r.Width;
        if(Kind==ViewKind.Generated) {
            var region=Generated==null?null:DskGeneratedProjection.Regions(Generated).FirstOrDefault(g=>fraction*Generated.BitCellCount>=g.StartCell && fraction*Generated.BitCellCount<g.EndCell);
            return region?.Descriptor is int descriptor ? (TrackIndex,new(TrackIndex,descriptor)) : null;
        }
        var t=Snapshot.Tracks.FirstOrDefault(t=>t.PhysicalIndex==TrackIndex); if(t==null) return null;
        foreach(var span in DskSurfaceRenderModel.Spans(t.Sectors,Proportional))
            if(fraction>=span.Start && p.X<r.X+r.Width*span.End-2) return (TrackIndex,span.Address);
        return null;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { ZoomBy(e.Delta>0?1.2:1/1.2,e.GetPosition(this)); e.Handled=true; }
    protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); Focus(); if(e.ChangedButton!=MouseButton.Left) return; clickCount=e.ClickCount; drag=e.GetPosition(this); moved=false; CaptureMouse(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p=e.GetPosition(this);
        if(drag is Point start && e.LeftButton==MouseButtonState.Pressed) { var delta=p-start; if(delta.Length>3 || moved) { PanBy(delta); drag=p; moved=true; } return; }
        var hit=Hit(p); var sector=hit?.Sector==null?null:Snapshot?.Sectors.FirstOrDefault(s=>s.Address==hit.Value.Sector);
        if(sector?.PhysicalCells==true) {ToolTip=$"{sector.Label}\n{sector.StoredBytes} B; header/data cells {sector.DescriptorOffset}/{sector.DataOffset}\nCRC header/data: {sector.HeaderCrcValid}/{sector.DataCrcValid}\n{sector.Role}; {sector.Ownership}";return;}
        ToolTip=sector==null?hit is {} h?$"Track {h.Track} — missing/empty":"":$"{sector.Label}\nStored {sector.StoredBytes} B; declared {sector.DeclaredBytes} B\nST1={sector.St1:X2} ST2={sector.St2:X2}\n{sector.Role}; {sector.Ownership}; issues {sector.IssueCount}";
    }
    internal void ActivateAt(Point position,bool doubleClick) {
        if(Hit(position) is {} hit) Activated?.Invoke(hit.Track,hit.Sector,doubleClick);
        else Activated?.Invoke(TrackIndex,null,false);
    }
    protected override void OnMouseUp(MouseButtonEventArgs e) { base.OnMouseUp(e); if(e.ChangedButton!=MouseButton.Left) return; ReleaseMouseCapture(); if(drag!=null && !moved) ActivateAt(e.GetPosition(this),clickCount>1); drag=null; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.Key==Key.Escape) { Activated?.Invoke(TrackIndex,null,false); e.Handled=true; return; }
        if(e.Key==Key.Home) { Fit(); e.Handled=true; return; }
        if(e.Key is Key.Add or Key.OemPlus) { ZoomBy(1.2); e.Handled=true; return; }
        if(e.Key is Key.Subtract or Key.OemMinus) { ZoomBy(1/1.2); e.Handled=true; return; }
        var t=Snapshot?.Tracks.FirstOrDefault(t=>t.PhysicalIndex==TrackIndex); if(t==null || t.Sectors.Count==0) return;
        if(e.Key is Key.Left or Key.Right) { int current=Selected?.Track==TrackIndex?Selected.Sector:0; int next=Math.Clamp(current+(e.Key==Key.Left?-1:1),0,t.Sectors.Count-1); Activated?.Invoke(TrackIndex,t.Sectors[next].Address,false); e.Handled=true; }
        if(e.Key==Key.Enter && Selected!=null) { Activated?.Invoke(Selected.Track,Selected,true); e.Handled=true; }
    }
}
