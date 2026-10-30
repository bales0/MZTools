using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

// Rendering and hit testing consume the immutable Core snapshot, never a decoder.
public sealed class DiskMap : Control
{
    public static readonly StyledProperty<DiskSession?> SessionProperty=AvaloniaProperty.Register<DiskMap,DiskSession?>(nameof(Session));
    public DiskSession? Session {get=>GetValue(SessionProperty);set=>SetValue(SessionProperty,value);}
    public static readonly StyledProperty<int> ModeProperty=AvaloniaProperty.Register<DiskMap,int>(nameof(Mode));
    public int Mode {get=>GetValue(ModeProperty);set=>SetValue(ModeProperty,value);}
    static DiskMap(){AffectsRender<DiskMap>(SessionProperty,ModeProperty);}
    public DiskMap(){Focusable=true;ClipToBounds=true;MinHeight=260;}
    public event Action<DiskSectorRow,KeyModifiers>? SectorSelected;
    private Point? press;private double pressOffset,pressPanX,pressPanY;private bool moved;
    private IBrush Brush(string name)=>this.FindResource("F2."+name) as IBrush??Brushes.Transparent;
    private void Text(DrawingContext c,string text,Point p)=>c.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,12,Brush("text")),p);
    public void Fit(){if(Session is null)return;if(Mode>0){Session.SurfaceZoom=1;Session.SurfacePanX=Session.SurfacePanY=0;}else{Session.MapZoom=1;Session.MapOffset=0;}InvalidateVisual();}
    public void FitSelection()
    {
        if(Session is null||Session.SectorSelection.Count==0)return;
        if(Mode>0)
        {
            var address=Session.SectorSelection.First();var model=new DskSurfaceRenderModel(Session.Snapshot,Mode-1,true);
            var band=model.Bands.FirstOrDefault(b=>b.Sectors.Any(x=>x.Address==address));
            if(band!=default){var span=band.Sectors.First(x=>x.Address==address);double angle=(span.Start+span.End)*Math.PI;double radius=Math.Min(Bounds.Width,Bounds.Height-28)/2-8;double r=(band.Inner+band.Outer)/2;Session.SurfaceZoom=4;Session.SurfacePanX=-Math.Sin(angle)*radius*r*4/Math.Max(1,Bounds.Width);Session.SurfacePanY=Math.Cos(angle)*radius*r*4/Math.Max(1,Bounds.Height);}InvalidateVisual();return;
        }
        int first=Session.SectorSelection.Min(a=>a.Track),last=Session.SectorSelection.Max(a=>a.Track);
        Session.MapZoom=Math.Clamp(Session.Snapshot.Tracks.Count/(double)(last-first+1),1,80);Session.MapOffset=first;InvalidateVisual();
    }
    public void Zoom(double factor,double anchor=.5)
    {
        if(Session is null)return;if(Mode>0){ZoomSurface(factor,new Point(Bounds.Width/2,Bounds.Height/2));return;}double count=Session.Snapshot.Tracks.Count;
        double old=count/Session.MapZoom;Session.MapZoom=Math.Clamp(Session.MapZoom*factor,1,Math.Max(1,count));
        Session.MapOffset=Math.Clamp(Session.MapOffset+old*anchor-count/Session.MapZoom*anchor,0,Math.Max(0,count-count/Session.MapZoom));InvalidateVisual();
    }
    private void ZoomSurface(double factor,Point anchor)
    {
        if(Session is not { } s)return;double old=s.SurfaceZoom;s.SurfaceZoom=Math.Clamp(old*factor,1,32);double ratio=s.SurfaceZoom/old;
        double cx=Bounds.Width/2+s.SurfacePanX*Bounds.Width,cy=(Bounds.Height+24)/2+s.SurfacePanY*Bounds.Height;
        s.SurfacePanX=(anchor.X-(anchor.X-cx)*ratio-Bounds.Width/2)/Math.Max(1,Bounds.Width);
        s.SurfacePanY=(anchor.Y-(anchor.Y-cy)*ratio-(Bounds.Height+24)/2)/Math.Max(1,Bounds.Height);InvalidateVisual();
    }
    private string Color(DskVisualSector s)=>s.Issues.Count!=0?"error-bg":(s.Role&(DskSectorRole.Directory|DskSectorRole.System|DskSectorRole.Boot))!=0?"disk-system":s.Owners.Count!=0?"disk-file":(s.Role&DskSectorRole.Free)!=0?"disk-free":"disk-unassigned";
    public override void Render(DrawingContext c)
    {
        base.Render(c);c.DrawRectangle(Brush("surface"),new Pen(Brush("border")),new Rect(Bounds.Size));if(Session is not { } s)return;
        if(Mode>0){DrawSurface(c,s);return;}
        double rowHeight=Math.Max(1,(Bounds.Height-30)/Math.Max(1,s.Snapshot.Tracks.Count)*s.MapZoom);
        using(c.PushClip(new Rect(0,24,Bounds.Width,Math.Max(0,Bounds.Height-24))))
        {
        foreach(var track in s.Snapshot.Tracks)
        {
            double y=24+(track.PhysicalIndex-s.MapOffset)*rowHeight;if(y+rowHeight<24||y>Bounds.Height)continue;
            if(rowHeight>=15)Text(c,$"C{track.SlotCylinder}/H{track.SlotSide}",new(4,y));
            var sectors=s.Sectors.Where(x=>x.Track==track.PhysicalIndex).ToArray();
            for(int i=0;i<sectors.Length;i++)
            {
                var sector=sectors[i];double width=(Bounds.Width-64)/Math.Max(1,sectors.Length);var rect=new Rect(64+i*width,y,Math.Max(1,width-1),Math.Max(1,rowHeight-1));
                bool selected=s.SectorSelection.Contains(sector.Sector.Address);c.DrawRectangle(Brush(Color(sector.Sector)),new Pen(Brush(selected?"focus":"disk-border"),selected?2:1),rect);
                if(width>=38&&rowHeight>=18)using(c.PushClip(rect))Text(c,$"R{sector.Sector.R}",new(rect.X+4,y+2));
            }
            if(sectors.Length==0)c.DrawRectangle(Brush("disk-unassigned"),null,new Rect(64,y,Math.Max(1,Bounds.Width-64),Math.Max(1,rowHeight-1)));
        }
        }
        Text(c,$"{(s.LogicalOrder?"Sector-ID order":"Descriptor order")} · track rows [{s.MapOffset:0.##}, {s.MapOffset+s.Snapshot.Tracks.Count/s.MapZoom:0.##})",new(8,4));
    }
    private void DrawSurface(DrawingContext c,DiskSession s)
    {
        var model=new DskSurfaceRenderModel(s.Snapshot,Mode-1,true);double radius=Math.Min(Bounds.Width,Bounds.Height-28)/2-8;radius*=s.SurfaceZoom;var center=new Point(Bounds.Width/2+s.SurfacePanX*Bounds.Width,(Bounds.Height+24)/2+s.SurfacePanY*Bounds.Height);
        foreach(var band in model.Bands)foreach(var span in band.Sectors)
        {
            var sector=s.Sectors.First(x=>x.Sector.Address==span.Address);var geometry=new StreamGeometry();
            Point P(double a,double r)=>new(center.X+Math.Sin(a*Math.PI*2)*radius*r,center.Y-Math.Cos(a*Math.PI*2)*radius*r);
            using(var g=geometry.Open())
            {
                g.BeginFigure(P(span.Start,band.Inner),true);
                int steps=Math.Max(2,(int)((span.End-span.Start)*120));
                for(int i=0;i<=steps;i++)g.LineTo(P(span.Start+(span.End-span.Start)*i/steps,band.Outer));
                for(int i=steps;i>=0;i--)g.LineTo(P(span.Start+(span.End-span.Start)*i/steps,band.Inner));g.EndFigure(true);
            }
            c.DrawGeometry(Brush(Color(sector.Sector)),new Pen(Brush(s.SectorSelection.Contains(span.Address)?"focus":"disk-border"),s.SectorSelection.Contains(span.Address)?2:.5),geometry);
        }
        Text(c,$"Side {Mode-1} · schematic surface; C0 outermost; positions are not measured flux",new(8,4));
    }
    internal DiskSectorRow? Hit(Point point)
    {
        if(Session is not { } s)return null;
        if(Mode>0)
        {
            double radius=(Math.Min(Bounds.Width,Bounds.Height-28)/2-8)*s.SurfaceZoom;if(radius<=0)return null;
            var hit=new DskSurfaceRenderModel(s.Snapshot,Mode-1,true).Hit((point.X-Bounds.Width/2-s.SurfacePanX*Bounds.Width)/radius,(point.Y-(Bounds.Height+24)/2-s.SurfacePanY*Bounds.Height)/radius);
            return hit?.Sector is { } address?s.Sectors.FirstOrDefault(x=>x.Sector.Address==address):null;
        }
        double rowHeight=Math.Max(1,(Bounds.Height-30)/Math.Max(1,s.Snapshot.Tracks.Count)*s.MapZoom);
        int track=(int)Math.Floor((point.Y-24)/rowHeight+s.MapOffset);if(point.X<64||point.Y<24)return null;
        var sectors=s.Sectors.Where(x=>x.Track==track).ToArray();int index=(int)((point.X-64)/Math.Max(1,Bounds.Width-64)*sectors.Length);
        return index>=0&&index<sectors.Length?sectors[index]:null;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if(e.Delta.Y==0)return;
        if(Mode>0)ZoomSurface(e.Delta.Y>0?1.5:1/1.5,e.GetPosition(this));
        else Zoom(e.Delta.Y>0?1.5:1/1.5,Math.Clamp((e.GetPosition(this).Y-24)/Math.Max(1,Bounds.Height-30),0,1));e.Handled=true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed){Focus();press=e.GetPosition(this);pressOffset=Session?.MapOffset??0;pressPanX=Session?.SurfacePanX??0;pressPanY=Session?.SurfacePanY??0;moved=false;e.Pointer.Capture(this);e.Handled=true;}
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if(press is { } start&&Session is { } s&&Math.Abs(e.GetPosition(this).X-start.X)+Math.Abs(e.GetPosition(this).Y-start.Y)>4)
        {
            moved=true;
            if(Mode>0){s.SurfacePanX=pressPanX+(e.GetPosition(this).X-start.X)/Math.Max(1,Bounds.Width);s.SurfacePanY=pressPanY+(e.GetPosition(this).Y-start.Y)/Math.Max(1,Bounds.Height);}
            else s.MapOffset=Math.Clamp(pressOffset-(e.GetPosition(this).Y-start.Y)/Math.Max(1,Bounds.Height-30)*s.Snapshot.Tracks.Count/s.MapZoom,0,Math.Max(0,s.Snapshot.Tracks.Count-s.Snapshot.Tracks.Count/s.MapZoom));InvalidateVisual();
        }
        ToolTip.SetTip(this,Hit(e.GetPosition(this))?.Detail);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {if(press is null)return;if(!moved&&Hit(e.GetPosition(this)) is { } row)SectorSelected?.Invoke(row,e.KeyModifiers);press=null;e.Pointer.Capture(null);e.Handled=true;}
    protected override void OnKeyDown(KeyEventArgs e)
    {if(e.Key is Key.Add or Key.OemPlus){Zoom(1.5);e.Handled=true;}else if(e.Key is Key.Subtract or Key.OemMinus){Zoom(1/1.5);e.Handled=true;}else if(e.Key==Key.Home){Fit();e.Handled=true;}
        else if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down&&Session is { } s&&s.Sectors.Count>0)
        {int index=s.SelectedSector is { } selected?s.Sectors.ToList().IndexOf(selected):0;int direction=e.Key is Key.Left or Key.Up?-1:1;SectorSelected?.Invoke(s.Sectors[Math.Clamp(index+direction,0,s.Sectors.Count-1)],e.KeyModifiers);e.Handled=true;}
        base.OnKeyDown(e);}
}
