using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

// A continuous QD stream on a schematic Archimedean spiral, never measured geometry.
internal sealed class QuickDiskSurfaceCanvas : FrameworkElement
{
    private const int Turns=32;
    private const double Pitch=.78/Turns, HalfWidth=Pitch*.36;
    private QuickDiskVisualSnapshot? geometrySnapshot;
    private Size geometrySize;
    private readonly List<(QuickDiskRegion Region,Geometry Shape)> shapes=new();
    internal QuickDiskVisualSnapshot? Snapshot {get;set;}
    internal IReadOnlyCollection<QuickDiskRegion> Selection {get;set;}=Array.Empty<QuickDiskRegion>();
    internal event Action<QuickDiskRegion?,ModifierKeys>? Selected;
    internal event Action? ZoomChanged;
    internal double Zoom {get;private set;}=1;
    private Vector pan;
    private Point? drag;
    private bool moved;
    private Point Center=>new(ActualWidth/2,ActualHeight/2+10);
    private double Radius=>Math.Max(1,Math.Min(ActualWidth,ActualHeight-40)/2-20);
    private Matrix Transform=>new(Zoom,0,0,Zoom,pan.X+Center.X*(1-Zoom),pan.Y+Center.Y*(1-Zoom));
    private Point BasePoint(Point point) {var matrix=Transform;matrix.Invert();return matrix.Transform(point);}
    internal QuickDiskSurfaceCanvas() {Focusable=true;ClipToBounds=true;MinHeight=100;}
    internal void Fit() {Zoom=1;pan=default;InvalidateVisual();ZoomChanged?.Invoke();}
    internal void ZoomBy(double factor,Point? anchor=null)
    {
        if(!double.IsFinite(factor) || factor<=0) return;
        var point=anchor??Center;var before=BasePoint(point);Zoom=Math.Clamp(Zoom*factor,1,60);
        pan=new(point.X-Center.X-(before.X-Center.X)*Zoom,point.Y-Center.Y-(before.Y-Center.Y)*Zoom);
        InvalidateVisual();ZoomChanged?.Invoke();
    }
    internal Point PointAt(double position)
    {
        double turn=Math.Clamp(position/Math.Max(1,Snapshot?.Layout.Length??1),0,1)*Turns;
        return Transform.Transform(SpiralPoint(turn,0));
    }
    private Point SpiralPoint(double turn,double radialOffset)
    {
        double angle=turn*Math.PI*2-Math.PI/2,radius=Radius*(.98-Pitch*turn+radialOffset);
        return new(Center.X+Math.Cos(angle)*radius,Center.Y+Math.Sin(angle)*radius);
    }
    internal double? PositionAt(Point point)
    {
        if(Snapshot==null || Snapshot.Layout.Length==0) return null;
        Vector vector=BasePoint(point)-Center;double radius=vector.Length/Radius;
        if(radius<.20-HalfWidth || radius>.98+HalfWidth) return null;
        double phase=((Math.Atan2(vector.Y,vector.X)+Math.PI/2)/(2*Math.PI)+1)%1;
        double turn=phase+Math.Round((.98-radius)/Pitch-phase);
        if(turn<0 || turn>Turns || Math.Abs(radius-(.98-Pitch*turn))>HalfWidth) return null;
        return turn/Turns*Snapshot.Layout.Length;
    }
    internal QuickDiskRegion? Hit(Point point)=>PositionAt(point) is double position?
        Snapshot!.Layout.Regions.FirstOrDefault(r=>r.Start<=position && position<r.End):null;
    private Geometry Segment(double first,double last)
    {
        var geometry=new StreamGeometry();using(var path=geometry.Open()) {
            int steps=Math.Max(2,(int)Math.Ceiling((last-first)*256));
            path.BeginFigure(SpiralPoint(first,HalfWidth),true,true);
            for(int i=1;i<=steps;i++) path.LineTo(SpiralPoint(first+(last-first)*i/steps,HalfWidth),true,false);
            for(int i=steps;i>=0;i--) path.LineTo(SpiralPoint(first+(last-first)*i/steps,-HalfWidth),true,false);
        }
        geometry.Freeze();return geometry;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(SystemColors.WindowBrush,null,new Rect(RenderSize));if(Snapshot==null) return;
        var text=new FormattedText("Schematic QD spiral · continuous stream · outer start → inner end",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,SystemColors.WindowTextBrush,VisualTreeHelper.GetDpi(this).PixelsPerDip) {MaxTextWidth=Math.Max(1,ActualWidth-24),Trimming=TextTrimming.CharacterEllipsis};
        dc.DrawText(text,new(12,4));dc.PushTransform(new MatrixTransform(Transform));
        dc.DrawEllipse(Brushes.Black,null,Center,Radius*.99,Radius*.99);
        double total=Math.Max(1,Snapshot.Layout.Length);
        if(!ReferenceEquals(geometrySnapshot,Snapshot) || geometrySize!=RenderSize) {
            shapes.Clear();foreach(var region in Snapshot.Layout.Regions.Where(r=>r.Length>0))
                shapes.Add((region,Segment(region.Start/total*Turns,region.End/total*Turns)));
            geometrySnapshot=Snapshot;geometrySize=RenderSize;
        }
        var selected=new List<Geometry>();
        foreach(var (region,geometry) in shapes) {
                Brush color=region.Kind is QuickDiskRegionKind.Header or QuickDiskRegionKind.Count?DiskMapVisuals.Metadata:
                    region.Kind is QuickDiskRegionKind.Payload or QuickDiskRegionKind.HostBlock or QuickDiskRegionKind.HostSector?DiskMapVisuals.Data:QuickDiskVisualCanvas.RegionColor(region.Kind);
                dc.DrawGeometry(color,new Pen(Brushes.DarkSlateGray,.5/Zoom),geometry);
                if(Selection.Contains(region)) selected.Add(geometry);
        }
        foreach(var geometry in selected) dc.DrawGeometry(null,new Pen(Brushes.DodgerBlue,2/Zoom),geometry);
        dc.DrawEllipse(SystemColors.WindowBrush,new Pen(Brushes.Gray,1/Zoom),Center,Radius*.17,Radius*.17);
        dc.Pop();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) {ZoomBy(e.Delta>0?1.5:1/1.5,e.GetPosition(this));e.Handled=true;}
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {Focus();drag=e.GetPosition(this);moved=false;CaptureMouse();e.Handled=true;}
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point=e.GetPosition(this);
        if(drag is Point previous && IsMouseCaptured) {if(moved || (point-previous).Length>3) {pan+=point-previous;drag=point;moved=true;InvalidateVisual();}}
        else ToolTip=Hit(point) is {} region?Snapshot!.Layout.Detail(region):null;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {if(drag!=null && !moved) Selected?.Invoke(Hit(e.GetPosition(this)),Keyboard.Modifiers);drag=null;ReleaseMouseCapture();e.Handled=true;}
    protected override void OnKeyDown(KeyEventArgs e)
    {if(e.Key==Key.Home) Fit();else if(e.Key is Key.Add or Key.OemPlus) ZoomBy(2);else if(e.Key is Key.Subtract or Key.OemMinus) ZoomBy(.5);else if(e.Key==Key.Escape) Selected?.Invoke(null,ModifierKeys.None);else return;e.Handled=true;}
}
