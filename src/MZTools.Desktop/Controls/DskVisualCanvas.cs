using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MZTools.Desktop.Controls;

internal sealed class DskVisualCanvas : Control
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
        var point=anchor??new Point(Bounds.Width/2,Bounds.Height/2); var before=BasePoint(point);
        double previous=zoom; zoom=Math.Clamp(zoom*factor,1,60);
        pan=anchor==null?pan*(zoom/previous):new Vector(point.X-before.X*zoom-Bounds.Width*.5*(1-zoom),point.Y-before.Y*zoom-Bounds.Height*.5*(1-zoom));
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
    private Point SurfaceCenter(int side) => new((Side<0?side+.5:.5)*Bounds.Width/SurfaceCount,Bounds.Height/2);
    private double SurfaceRadius => Math.Max(1,Math.Min(Bounds.Width/SurfaceCount,Bounds.Height)/2-20);
    private Rect Visible => new(BasePoint(new(0,0)),BasePoint(new(Bounds.Width,Bounds.Height)));
    private Matrix ViewTransform => new(zoom,0,0,zoom,pan.X+Bounds.Width*.5*(1-zoom),pan.Y+Bounds.Height*.5*(1-zoom));
    private Point BasePoint(Point p) { return ViewTransform.Invert().Transform(p); }
    private Rect TrackRect => new(24,55,Math.Max(1,Bounds.Width-48),Math.Max(55,Math.Min(110,Bounds.Height-95)));
    private void Text(DrawingContext dc,string text,Point point,double size=12,IBrush? brush=null) => dc.DrawText(
        new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,
            brush??Brushes.Black),point);
    private static readonly IBrush WpfControlLight=new SolidColorBrush(Color.Parse("#E3E3E3"));
    private IBrush Fill(DskVisualSector s)
    {
        if(Filtered != null && !Filtered.Contains(s.Address)) return new SolidColorBrush(Color.Parse("#F0F0F0"));
        return Layer switch {
            "Issue severity" => s.Issues.Any(i=>i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe) ? Brushes.Crimson : s.Issues.Any(i=>i.Severity==DskIssueSeverity.Warning) ? Brushes.DarkOrange : WpfControlLight,
            "FDC status" or "CRC / deleted mark" => s.St1!=0 || s.St2!=0 ? Brushes.Crimson : WpfControlLight,
            "Data length anomalies" => s.DeclaredBytes!=s.StoredBytes || s.ParsedNBytes!=s.StoredBytes ? Brushes.DarkOrange : WpfControlLight,
            "Ownership" => s.Owners.Count>0 ? Brushes.MediumPurple : WpfControlLight,
            "Current selection" => Selection.Contains(s.Address) || s.Address==Selected ? Brushes.CornflowerBlue : Related.Contains(s.Address) ? Brushes.Gold : WpfControlLight,
            _ => (s.Role & (DskSectorRole.Boot|DskSectorRole.System|DskSectorRole.Directory|DskSectorRole.Reserved|DskSectorRole.AllocationMap|DskSectorRole.Fat|DskSectorRole.NativeIpl))!=0 ? Brushes.DarkOrange :
                s.Role.HasFlag(DskSectorRole.Free) ? WpfControlLight : s.Role.HasFlag(DskSectorRole.Data) ? Brushes.MediumSeaGreen : WpfControlLight
        };
    }
    public override void Render(DrawingContext dc)
    {
        base.Render(dc); dc.DrawRectangle(Brushes.White,null,new Rect(Bounds.Size));
        if(Snapshot == null) return;
        using var transform=dc.PushTransform(ViewTransform);
        if(Kind==ViewKind.Surface) DrawSurface(dc); else if(Kind==ViewKind.Generated) DrawGenerated(dc); else DrawTrack(dc);
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
            dc.DrawRectangle(Fill(s),new Pen(Brushes.Black,.6),box);
            if(Selection.Contains(s.Address) || s.Address==Selected || Related.Contains(s.Address)) dc.DrawRectangle(null,new Pen(Selection.Contains(s.Address)||s.Address==Selected?Brushes.DodgerBlue:Brushes.Gold,2),box);
            using var clip=dc.PushClip(box);
            IBrush ink=Fill(s) is ISolidColorBrush color && color.Color.R+color.Color.G+color.Color.B<300 ? Brushes.White : Brushes.Black;
            Text(dc,$"{(s.IssueCount>0?"! ":"")}#{s.Descriptor} R{s.R}",new(box.X+3,box.Y+5),11,ink);
            if(box.Width>65) { Text(dc,$"{s.StoredBytes} B",new(box.X+3,box.Y+24),11,ink); Text(dc,s.Role.ToString(),new(box.X+3,box.Y+43),10,ink); }

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
            c.BeginFigure(At(a,outer),true);
            // Two arcs support a single-descriptor full circle without degenerate endpoints.
            double mid=(a+b)/2;
            c.ArcTo(At(mid,outer),new(outer,outer),0,false,SweepDirection.Clockwise);
            c.ArcTo(At(b,outer),new(outer,outer),0,false,SweepDirection.Clockwise);
            c.LineTo(At(b,inner));
            c.ArcTo(At(mid,inner),new(inner,inner),0,false,SweepDirection.CounterClockwise);
            c.ArcTo(At(a,inner),new(inner,inner),0,false,SweepDirection.CounterClockwise);
            c.EndFigure(true);
        } geometry[key]=g; return g;
    }
    private void DrawSurface(DrawingContext dc)
    {
        string key=$"{Side}:{Proportional}:{Bounds.Width}:{Bounds.Height}";
        if(cacheKey!=key) { geometry.Clear(); cacheKey=key; }
        foreach(int side in SurfaceSides) {
        var center=SurfaceCenter(side); double radius=SurfaceRadius;
        var model=SurfaceModel(side);
        foreach(var band in model.Bands) {
            var track=Snapshot!.Tracks.First(t=>t.PhysicalIndex==band.Track);
            if(track.Sectors.Count==0) {
                var pen=new Pen(Brushes.Gray,Math.Max(.5,(band.Outer-band.Inner)*radius*.7)) { DashStyle=DashStyle.Dot };
                dc.DrawEllipse(null,pen,center,(band.Inner+band.Outer)*radius/2,(band.Inner+band.Outer)*radius/2); continue;
            }
            foreach(var span in band.Sectors) {
                var s=track.Sectors[span.Address.Sector]; var g=Wedge(band,span,center,radius);
                if(!g.Bounds.Intersects(Visible)) continue;
                dc.DrawGeometry(Fill(s),new Pen(Brushes.Black,.25),g);
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
            IBrush color=region.Kind=="DATA"?Brushes.MediumSeaGreen:region.Kind.Contains("CRC")?Brushes.MediumPurple:region.Kind.Contains("AM")?Brushes.DarkOrange:WpfControlLight;
            var box=new Rect(x,r.Y,Math.Max(.1,width),r.Height); dc.DrawRectangle(color,new Pen(Brushes.Black,.3),box);
            if(region.Descriptor is int index && (Selection.Contains(new(TrackIndex,index)) || Selected==new DskSectorAddress(TrackIndex,index))) dc.DrawRectangle(null,new Pen(Brushes.CornflowerBlue,2),box);
            if(width>35) { using var clip=dc.PushClip(box); Text(dc,region.Kind,new(x+2,r.Y+8),10,Brushes.Black);  }
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
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { if(e.Delta.Y==0)return;ZoomBy(e.Delta.Y>0?1.2:1/1.2,e.GetPosition(this));e.Handled=true; }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e);if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus();clickCount=e.ClickCount;drag=e.GetPosition(this);moved=false;e.Pointer.Capture(this);e.Handled=true; }
    protected override void OnPointerMoved(PointerEventArgs e) {
        var p=e.GetPosition(this);if(drag is Point start&&e.GetCurrentPoint(this).Properties.IsLeftButtonPressed){var delta=new Vector(p.X-start.X,p.Y-start.Y);if(delta.Length>3||moved){PanBy(delta);drag=p;moved=true;}return;}
        var hit=Hit(p);var sector=hit?.Sector==null?null:Snapshot?.Sectors.FirstOrDefault(s=>s.Address==hit.Value.Sector);
        ToolTip.SetTip(this,sector==null?hit is{} h?$"Track {h.Track} — missing/empty":"":$"{sector.Label}\nStored {sector.StoredBytes} B; declared {sector.DeclaredBytes} B\nST1={sector.St1:X2} ST2={sector.St2:X2}\n{sector.Role}; {sector.Ownership}; issues {sector.IssueCount}");
    }
    internal event Action<int,DskSectorAddress?,bool,KeyModifiers>? Gesture;
    internal void ActivateAt(Point position,bool doubleClick,KeyModifiers modifiers=KeyModifiers.None) {
        var hit=Hit(position);Gesture?.Invoke(hit?.Track??TrackIndex,hit?.Sector,doubleClick,modifiers);Activated?.Invoke(hit?.Track??TrackIndex,hit?.Sector,doubleClick);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e);if(drag==null)return;e.Pointer.Capture(null);if(!moved)ActivateAt(e.GetPosition(this),clickCount>1,e.KeyModifiers);drag=null;e.Handled=true; }
    protected override void OnKeyDown(KeyEventArgs e) {
        if(e.Key==Key.Escape){Gesture?.Invoke(TrackIndex,null,false,e.KeyModifiers);e.Handled=true;return;}
        if(e.Key==Key.Home){Fit();e.Handled=true;return;}if(e.Key is Key.Add or Key.OemPlus){ZoomBy(1.2);e.Handled=true;return;}if(e.Key is Key.Subtract or Key.OemMinus){ZoomBy(1/1.2);e.Handled=true;return;}
        var t=Snapshot?.Tracks.FirstOrDefault(t=>t.PhysicalIndex==TrackIndex);if(t==null||t.Sectors.Count==0)return;
        if(e.Key is Key.Left or Key.Right){int current=Selected?.Track==TrackIndex?Selected.Sector:0;int next=Math.Clamp(current+(e.Key==Key.Left?-1:1),0,t.Sectors.Count-1);Gesture?.Invoke(TrackIndex,t.Sectors[next].Address,false,e.KeyModifiers);e.Handled=true;}
        if(e.Key==Key.Enter&&Selected!=null){Gesture?.Invoke(Selected.Track,Selected,true,e.KeyModifiers);e.Handled=true;}
    }
}
