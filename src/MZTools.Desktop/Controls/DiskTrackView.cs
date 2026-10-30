using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

public sealed class DiskTrackView : Control
{
    public DiskSession? Session {get;set;}
    private Point? press;private double start;
    public DiskTrackView(){Focusable=true;ClipToBounds=true;MinHeight=150;}
    private IBrush Brush(string name)=>this.FindResource("F2."+name) as IBrush??Brushes.Transparent;
    public void Fit(){if(Session is not {TrackData:{ } t} s)return;s.CellStart=0;s.CellLength=t.BitCellCount;InvalidateVisual();}
    public void Zoom(double factor,double anchor=.5)
    {if(Session is not {TrackData:{ } t} s)return;double old=s.CellLength;s.CellLength=Math.Clamp(old/factor,1,Math.Max(1,t.BitCellCount));s.CellStart=Math.Clamp(s.CellStart+(old-s.CellLength)*anchor,0,t.BitCellCount-s.CellLength);InvalidateVisual();}
    public override void Render(DrawingContext c)
    {
        base.Render(c);c.DrawRectangle(Brush("surface"),new Pen(Brush("border")),new Rect(Bounds.Size));if(Session is not {TrackData:{ } track} s)return;
        var pen=new Pen(Brush("accent"),1);var weak=new Pen(Brush("error"),2);
        int pixels=Math.Max(1,(int)Bounds.Width);
        for(int x=0;x<pixels;x++)
        {
            int first=(int)(s.CellStart+s.CellLength*x/pixels),last=Math.Min((int)track.BitCellCount,(int)(s.CellStart+s.CellLength*(x+1)/pixels)+1);
            bool low=false,high=false,isWeak=false;
            for(int cell=first;cell<last;cell++){bool bit=PackedBitCells.Get(track.PackedBitCells,cell);low|=!bit;high|=bit;if(cell/8<track.WeakBitMask.Length)isWeak|=PackedBitCells.Get(track.WeakBitMask,cell);}
            double a=high?35:95,b=low?95:35;c.DrawLine(isWeak?weak:pen,new Point(x,a),new Point(x,Math.Max(a+1,b)));
        }
        c.DrawText(new FormattedText($"Bitcell range [{s.CellStart:N0}, {s.CellStart+s.CellLength:N0}) · min/max per pixel · {(s.Snapshot.IsPhysical?"stored HFE":"generated MFM, no measured flux")}",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,12,Brush("text")),new Point(8,8));
        foreach(long index in track.IndexCells.Where(i=>i>=s.CellStart&&i<s.CellStart+s.CellLength))c.DrawLine(new Pen(Brush("focus"),2),new Point((index-s.CellStart)/s.CellLength*Bounds.Width,25),new Point((index-s.CellStart)/s.CellLength*Bounds.Width,110));
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e){if(e.Delta.Y==0)return;Zoom(e.Delta.Y>0?1.5:1/1.5,Math.Clamp(e.GetPosition(this).X/Math.Max(1,Bounds.Width),0,1));e.Handled=true;}
    protected override void OnPointerPressed(PointerPressedEventArgs e){if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed){Focus();press=e.GetPosition(this);start=Session?.CellStart??0;e.Pointer.Capture(this);}}
    protected override void OnPointerMoved(PointerEventArgs e){if(press is { } p&&Session is {TrackData:{ } t} s){s.CellStart=Math.Clamp(start-(e.GetPosition(this).X-p.X)/Math.Max(1,Bounds.Width)*s.CellLength,0,Math.Max(0,t.BitCellCount-s.CellLength));InvalidateVisual();}}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){press=null;e.Pointer.Capture(null);}
    protected override void OnKeyDown(KeyEventArgs e){if(e.Key is Key.Add or Key.OemPlus){Zoom(1.5);e.Handled=true;}else if(e.Key is Key.Subtract or Key.OemMinus){Zoom(1/1.5);e.Handled=true;}else if(e.Key==Key.Home){Fit();e.Handled=true;}base.OnKeyDown(e);}
}
