using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed class QuickDiskVisualCanvas : FrameworkElement
{
    internal QuickDiskVisualSnapshot? Snapshot {get;private set;}
    internal IReadOnlyCollection<QuickDiskRegion> Selection {get;set;}=Array.Empty<QuickDiskRegion>();
    internal event Action<QuickDiskRegion?,ModifierKeys>? Selected;
    internal double Start {get;private set;}
    internal double Length {get;private set;}=1;
    internal int RenderedCells {get;private set;}
    internal int RenderedBytes {get;private set;}
    internal int RenderedTransitions {get;private set;}
    internal static Brush RegionColor(QuickDiskRegionKind kind)=>kind switch {
        QuickDiskRegionKind.Payload or QuickDiskRegionKind.HostBlock or QuickDiskRegionKind.HostSector=>Brushes.LightGreen,
        QuickDiskRegionKind.Header or QuickDiskRegionKind.Count=>Brushes.Orange,
        QuickDiskRegionKind.Framing=>Brushes.LightSkyBlue,
        QuickDiskRegionKind.Crc=>Brushes.Plum,
        QuickDiskRegionKind.OutsideWindow=>Brushes.DimGray,
        _=>Brushes.LightGray
    };
    private Point? drag;
    private double dragStart;
    private bool moved;
    private double WidthForData=>Math.Max(1,ActualWidth-32);
    internal QuickDiskVisualCanvas() {Focusable=true;ClipToBounds=true;MinHeight=140;}
    internal void SetSnapshot(QuickDiskVisualSnapshot snapshot) {Snapshot=snapshot;Selection=Array.Empty<QuickDiskRegion>();Fit();}
    internal void Fit() {Start=0;Length=Math.Max(1,Snapshot?.Layout.Length??1);InvalidateVisual();}
    internal void FitSelection()
    {
        if(Selection.Count==0) return;
        double start=Selection.Min(r=>r.Start),end=Selection.Max(r=>r.End),margin=Math.Max(1,(end-start)*.05);
        Start=Math.Max(0,start-margin);Length=Math.Min(Math.Max(1,Snapshot!.Layout.Length)-Start,end-start+2*margin);InvalidateVisual();
    }
    internal void Zoom(double factor,double anchor=.5)
    {
        if(!double.IsFinite(factor) || factor<=0) return;
        double total=Math.Max(1,Snapshot?.Layout.Length??1); anchor=Math.Clamp(anchor,0,1);
        double next=Math.Clamp(Length/factor,Math.Min(Snapshot?.Layout.IsPhysical==true?16:1,total),total);
        Start=Math.Clamp(Start+(Length-next)*anchor,0,Math.Max(0,total-next));Length=next;InvalidateVisual();
    }
    internal QuickDiskRegion? Hit(Point point)
    {
        if(Snapshot==null || point.X<16 || point.X>ActualWidth-16 || point.Y<45 || point.Y>ActualHeight-24) return null;
        double position=Start+(point.X-16)/WidthForData*Length;
        return Snapshot.Layout.Regions.FirstOrDefault(r=>r.Start<=position && r.End>position);
    }
    private void Text(DrawingContext dc,string value,double x,double y,Brush? color=null,double size=12)
    {
        var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Consolas"),size,color??SystemColors.WindowTextBrush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        text.MaxTextWidth=Math.Max(1,ActualWidth-x-12);text.Trimming=TextTrimming.CharacterEllipsis;dc.DrawText(text,new Point(x,y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(SystemColors.WindowBrush,null,new Rect(RenderSize)); RenderedCells=RenderedBytes=RenderedTransitions=0;
        var model=Snapshot; if(model==null) return;
        double bottom=Math.Max(100,ActualHeight-22),scale=WidthForData/Length;
        Text(dc,(model.Layout.IsPreview?"Rebuilt preview • ":"")+(model.Layout.IsPhysical?"STORED QD MFM • original bit cells • not captured flux":"GENERATED MFM • logical container bytes encoded for illustration • not stored track positions"),16,8);
        dc.PushClip(new RectangleGeometry(new Rect(16,28,WidthForData,Math.Max(1,bottom-28))));
        foreach(var region in model.Layout.Regions.Where(r=>r.End>Start && r.Start<Start+Length)) {
            double x=16+(Math.Max(Start,region.Start)-Start)*scale,width=(Math.Min(Start+Length,region.End)-Math.Max(Start,region.Start))*scale;
            dc.DrawRectangle(RegionColor(region.Kind),new Pen(Selection.Contains(region)?Brushes.DodgerBlue:Brushes.Gray,Selection.Contains(region)?3:1),new Rect(x,32,Math.Max(.5,width),Math.Max(32,bottom-39)));
            if(width>65) Text(dc,region.FileIndex>=0?$"{region.FileNumber} {region.Name}":region.Kind.ToString(),x+3,34);
            bool decoded=region.Kind is not (QuickDiskRegionKind.Gap or QuickDiskRegionKind.OutsideWindow);
            double unit=model.Layout.IsPhysical?16:1;
            if(decoded && unit*scale>=32) {
                long byteStart=model.ByteStart(region);
                int first=(int)Math.Max(0,Math.Ceiling((Start-byteStart)/unit));
                int end=(int)Math.Min((region.End-byteStart)/unit+(model.Layout.IsPhysical && region.Length%16==15?1:0),Math.Ceiling((Start+Length-byteStart)/unit));
                if(model.Layout.HostBytes.TryGetValue(region,out var host)) end=Math.Min(end,host.Length);
                for(int i=first;i<end;i++) { Text(dc,model.Byte(region,i).ToString("X2"),16+(byteStart+i*unit-Start)*scale+2,54);RenderedBytes++; }
            }
        }
        foreach(var finding in model.Findings.Where(f=>f.Start is long start && f.End is long end && end>Start && start<Start+Length)) {
            double x=16+(Math.Max(Start,finding.Start!.Value)-Start)*scale;
            double width=(Math.Min(Start+Length,finding.End!.Value)-Math.Max(Start,finding.Start.Value))*scale;
            dc.DrawRectangle(Brushes.Crimson,null,new Rect(x,28,Math.Max(1,width),4));
        }
        double unitsPerCell=model.Layout.IsPhysical?1:1.0/16,cellWidth=scale*unitsPerCell;
        if(cellWidth>=6) {
            long first=(long)Math.Ceiling(Start/unitsPerCell),end=(long)Math.Min(model.Layout.Length/unitsPerCell,Math.Ceiling((Start+Length)/unitsPerCell));
            double high=Math.Min(bottom-30,100),low=Math.Min(bottom-5,125);
            var signalPen=new Pen(Brushes.DarkBlue,1);
            for(long cell=first;cell<end;cell++) {
                double x=16+(cell*unitsPerCell-Start)*scale;int bit=model.SignalBit(cell);
                dc.DrawLine(signalPen,new Point(x,bit==1?high:low),new Point(x+cellWidth,bit==1?high:low));
                if(cell>0 && model.SignalBit(cell-1)!=bit) {dc.DrawLine(signalPen,new Point(x,high),new Point(x,low));RenderedTransitions++;}
                if(cellWidth>=14) Text(dc,bit.ToString(),x+1,Math.Min(75,high-18),Brushes.Black,10);
                RenderedCells++;
            }
        }
        dc.Pop();Text(dc,$"{Start:N0} … {Start+Length:N0} {model.Layout.Unit} • HEX / bit cells / digital signal • wheel: zoom • drag: pan",16,bottom+4,null,10);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) {Zoom(e.Delta>0?1.5:1/1.5,(e.GetPosition(this).X-16)/WidthForData);e.Handled=true;}
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {Focus();drag=e.GetPosition(this);dragStart=Start;moved=false;CaptureMouse();e.Handled=true;}
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point=e.GetPosition(this);
        if(drag is Point origin && IsMouseCaptured) {
            moved|=Math.Abs(point.X-origin.X)>3;
            if(moved) {Start=Math.Clamp(dragStart-(point.X-origin.X)/WidthForData*Length,0,Math.Max(0,(Snapshot?.Layout.Length??1)-Length));InvalidateVisual();}
        } else ToolTip=Hit(point) is {} region?Snapshot!.Layout.Detail(region):null;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {if(drag!=null && !moved) Selected?.Invoke(Hit(e.GetPosition(this)),Keyboard.Modifiers);drag=null;ReleaseMouseCapture();e.Handled=true;}
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.Key==Key.Home) Fit();else if(e.Key is Key.Add or Key.OemPlus) Zoom(2);else if(e.Key is Key.Subtract or Key.OemMinus) Zoom(.5);
        else if(e.Key==Key.Escape) Selected?.Invoke(null,ModifierKeys.None);else return;e.Handled=true;
    }
}
