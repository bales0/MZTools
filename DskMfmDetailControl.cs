using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

// An unrolled, viewport-bounded view of the encoder's real output cells.
internal sealed class DskMfmDetailControl : FrameworkElement
{
    private PhysicalTrack? track;
    private IReadOnlyList<DskGeneratedRegion> regions=Array.Empty<DskGeneratedRegion>();
    private int trackIndex;
    private double start, length=1;
    private Point? drag;
    private bool moved;
    internal bool Stored {get;set;}
    internal string Message {get;set;}="Select a sector to generate its MFM track.";
    internal event Action<DskSectorAddress>? Selected;
    internal PhysicalTrack? Track=>track;
    internal double Start=>start;
    internal double Length=>length;
    internal int RenderedBytes {get;private set;}
    internal int RenderedCells {get;private set;}
    private double PlotWidth=>Math.Max(1,ActualWidth-32);
    internal DskMfmDetailControl() { Focusable=true; ClipToBounds=true; MinHeight=100; }
    internal void SetTrack(PhysicalTrack? value,int index,int? descriptor=null)
    {
        bool changed=!ReferenceEquals(track,value); track=value; trackIndex=index;
        regions=value==null?Array.Empty<DskGeneratedRegion>():Stored?DskGeneratedProjection.StoredRegions(value):DskGeneratedProjection.Regions(value);
        if(changed) { start=0; length=value?.BitCellCount??1; if(descriptor is int d) FitSector(d); }
        InvalidateVisual();
    }
    internal void Fit() { start=0; length=track?.BitCellCount??1; InvalidateVisual(); }
    internal void FitSector(int descriptor)
    {
        if(track==null || descriptor<0 || descriptor>=track.Sectors.Count) return;
        var s=track.Sectors[descriptor]; start=Math.Max(0,s.HeaderCell-192);
        length=Math.Min(track.BitCellCount-start,s.DataCell+s.Data.Length*16L+32-start); InvalidateVisual();
    }
    internal void ZoomBy(double factor,double anchor=.5)
    {
        if(track==null || track.BitCellCount==0 || !double.IsFinite(factor) || factor<=0) return;
        anchor=Math.Clamp(anchor,0,1); double next=Math.Clamp(length/factor,Math.Min(16,track.BitCellCount),track.BitCellCount);
        start=Math.Clamp(start+(length-next)*anchor,0,track.BitCellCount-next); length=next; InvalidateVisual();
    }
    internal void Pan(double cells) { if(track==null) return; start=Math.Clamp(start+cells,0,track.BitCellCount-length); InvalidateVisual(); }
    internal long CellAt(double x)=>track==null || track.BitCellCount==0?0:Math.Clamp((long)Math.Floor(start+(x-16)/PlotWidth*length),0,track.BitCellCount-1);
    internal DskSectorAddress? Hit(Point point)
    {
        if(track==null || point.X<16 || point.X>ActualWidth-16 || point.Y<32 || point.Y>ActualHeight-20) return null;
        long cell=CellAt(point.X); var region=regions.FirstOrDefault(r=>cell>=r.StartCell && cell<r.EndCell);
        return region?.Descriptor is int d?new(trackIndex,d):null;
    }
    internal static byte ReadByte(PhysicalTrack source,long cell)
    {
        byte value=0; for(int bit=0;bit<8;bit++) value=(byte)((value<<1)|(PackedBitCells.Get(source.PackedBitCells,checked((int)(cell+bit*2+1)))?1:0)); return value;
    }
    private void Text(DrawingContext dc,string text,double x,double y,Brush? ink=null,double size=12)
        =>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Consolas"),size,ink??SystemColors.WindowTextBrush,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(SystemColors.WindowBrush,null,new Rect(RenderSize)); RenderedBytes=RenderedCells=0;
        if(track==null || track.BitCellCount==0) { Text(dc,Message,12,8); return; }
        double px=PlotWidth/length, end=start+length, h=Math.Max(30,ActualHeight-80);
        double X(double c)=>16+(c-start)*px;
        Text(dc,$"{(Stored?"STORED HFE CELLS":"GENERATED MFM")} · track {trackIndex} · cells {(long)start:N0}–{(long)end:N0} · wheel: zoom · drag: pan",12,4);
        foreach(var region in regions.Where(r=>r.EndCell>start && r.StartCell<end)) {
            double a=X(Math.Max(start,region.StartCell)),b=X(Math.Min(end,region.EndCell));
            Brush color=region.Kind=="DATA"?Brushes.LightGreen:region.Kind.Contains("CRC")?Brushes.Plum:region.Kind.Contains("AM")?Brushes.Orange:region.Kind=="Sync"?Brushes.LightSkyBlue:Brushes.LightGray;
            dc.DrawRectangle(color,new Pen(Brushes.Gray,.5),new Rect(a,32,Math.Max(.1,b-a),h));
            if(b-a>60) Text(dc,region.Kind,a+3,34,Brushes.Black,11);
        }
        if(px*16>=28 && (!Stored || track.Encoding!=PhysicalEncoding.Unknown)) {
            foreach(var region in regions.Where(r=>!Stored || r.Descriptor!=null))
            for(long c=region.StartCell+(long)Math.Max(0,Math.Ceiling((start-region.StartCell)/16))*16;c+16<=Math.Min(Math.Min(end,track.BitCellCount),region.EndCell);c+=16) {
                Text(dc,ReadByte(track,c).ToString("X2"),X(c)+2,54,Brushes.Black); RenderedBytes++;
            }
        }
        if(px>=5) {
            double high=Math.Min(ActualHeight-38,100),low=Math.Min(ActualHeight-24,125);
            for(long c=(long)Math.Ceiling(start);c<Math.Min(end,track.BitCellCount);c++) {
                bool bit=PackedBitCells.Get(track.PackedBitCells,checked((int)c)); double x=X(c),y=bit?high:low;
                if(Stored && track.WeakBitMask.Length>0 && PackedBitCells.Get(track.WeakBitMask,(int)c)) dc.DrawRectangle(Brushes.OrangeRed,null,new Rect(x,high,px,Math.Max(1,low-high)));
                dc.DrawLine(new Pen(Brushes.DarkBlue,1),new Point(x,y),new Point(x+px,y));
                if(c>0 && PackedBitCells.Get(track.PackedBitCells,checked((int)c-1))!=bit) dc.DrawLine(new Pen(Brushes.DarkBlue,.6),new Point(x,high),new Point(x,low));
                if(px>=14) Text(dc,$"{(Stored?"":c%2==0?"C":"D")}{(bit?1:0)}",x+1,75,Brushes.Black,10);
                RenderedCells++;
            }
        }
        foreach(long marker in track.IndexCells.Concat(track.Timing.Select(t=>t.CellOffset)).Concat(track.V2Controls.Select(t=>t.CellOffset)).Where(c=>c>=start && c<end)) dc.DrawLine(new Pen(Brushes.Magenta,1.5),new Point(X(marker),28),new Point(X(marker),ActualHeight-20));
        Text(dc,Stored?"Stored cells; HEX only inside decoded fields · orange = weak cell · magenta = index/timing/control":px>=5?"C = clock cell · D = data cell · HEX reads data bits (16 MFM cells / byte)":"GAP / Sync / IDAM / CHRN / CRC / DAM / DATA · zoom for HEX and clock/data cells",12,ActualHeight-18,null,10);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { ZoomBy(e.Delta>0?1.5:1/1.5,(e.GetPosition(this).X-16)/PlotWidth); e.Handled=true; }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); drag=e.GetPosition(this); moved=false; CaptureMouse(); e.Handled=true; }
    protected override void OnMouseMove(MouseEventArgs e) {
        var p=e.GetPosition(this); if(drag is Point previous && e.LeftButton==MouseButtonState.Pressed) {
            if(moved || Math.Abs(p.X-previous.X)>3) { Pan((previous.X-p.X)/PlotWidth*length); drag=p; moved=true; }
        } else if(track!=null) ToolTip=$"{(Stored?"Stored":"Generated")} cell {CellAt(p.X):N0} · {Hit(p)?.ToString()??"Gap / outside sector"}";
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); if(!moved && Hit(e.GetPosition(this)) is {} address) Selected?.Invoke(address); drag=null; e.Handled=true; }
    protected override void OnKeyDown(KeyEventArgs e) {
        if(e.Key==Key.Home) Fit(); else if(e.Key is Key.Add or Key.OemPlus) ZoomBy(1.5); else if(e.Key is Key.Subtract or Key.OemMinus) ZoomBy(1/1.5); else if(e.Key==Key.Left) Pan(-length*.1); else if(e.Key==Key.Right) Pan(length*.1); else return; e.Handled=true;
    }
}
