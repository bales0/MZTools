using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MZTools.Capture;

namespace MZTools;

internal sealed class HeadEdgeAdjustmentControl : DockPanel
{
    internal readonly HeadEdgeAdjustmentModel[] Models=[new(),new()];
    internal readonly Button ZeroHere=new() { Content="Zero here",Margin=new(4),Padding=new(12,4,12,4),IsEnabled=false };
    internal readonly CheckBox Repeated=new() { Content="Same tape section replayed",Margin=new(5),ToolTip="Your declaration that you replay the same tape section with unchanged gain/EQ. No automatic content matching; the edge calculation is unchanged. Changing this declaration clears the manual zero." };
    private readonly ComboBox damping=new() { ItemsSource=new[]{"0.25","0.5","1","2"},SelectedIndex=1,Margin=new(4) },range=new() { ItemsSource=new[]{"10","25","50","100"},SelectedIndex=2,Margin=new(4) };
    private readonly TextBox recording=new() { Width=160,Margin=new(4) };
    private readonly TextBlock detail=Text("");
    internal readonly SignalStatus status=new();
    private readonly EdgeDial left=new() { Title="Left • combined edges" },right=new() { Title="Right • combined edges" };
    private readonly Grid dials=new();
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(33) };
    private int channels;
    internal HeadEdgeAdjustmentControl()
    {
        var top=new StackPanel(); SetDock(top,Dock.Top); Children.Add(top);
        top.Children.Add(Text("Match playback to this particular tape copy. A studio reference is not required. The recorded alignment may differ from your deck's nominal setting."));
        var controls=new WrapPanel(); controls.Children.Add(ZeroHere);
        var clear=new Button { Content="Clear zero",Margin=new(4),Padding=new(8,4,8,4) }; controls.Children.Add(clear);
        Add(controls,"Damping s",damping); Add(controls,"Dial ±%",range); controls.Children.Add(Repeated); top.Children.Add(controls);
        var label=new StackPanel(); var copy=new WrapPanel(); Add(copy,"Tape copy / section (optional)",recording); label.Children.Add(copy); label.Children.Add(detail);
        top.Children.Add(new Expander { Header="Tape label / stability details",Content=label }); top.Children.Add(status);
        var bottom=Text("Each channel combines rising/falling times equally. Positive = shorter edges than your zero. Damping changes pointers only. Content, speed, noise and EQ can move them too; verify by replaying a recognizable section. No automatic best/ideal azimuth.");
        SetDock(bottom,Dock.Bottom); Children.Add(bottom);
        dials.ColumnDefinitions.Add(new()); dials.ColumnDefinitions.Add(new()); dials.Children.Add(left); Grid.SetColumn(right,1); dials.Children.Add(right); Children.Add(dials);
        ZeroHere.ToolTip="Vynulovat zde: set zero for currently usable channels. Each channel is qualified independently.";
        ZeroHere.Click+=(_,_)=>{ foreach(var model in Models) model.ZeroHere(Now); Draw(); };
        clear.Click+=(_,_)=>Reset("Manual zero cleared — let fresh measurements settle.");
        Repeated.Click+=(_,_)=>Reset("Comparison mode changed — set a new zero.");
        recording.TextChanged+=(_,_)=>Reset("Tape label changed — set a new zero.");
        damping.SelectionChanged+=(_,_)=>{ foreach(var model in Models) model.SetDamping(double.Parse((string)damping.SelectedItem,CultureInfo.InvariantCulture)); };
        range.SelectionChanged+=(_,_)=>Draw();
        timer.Tick+=(_,_)=>{ foreach(var model in Models) model.Advance(Now); Draw(); };
        Loaded+=(_,_)=>timer.Start(); Unloaded+=(_,_)=>timer.Stop(); Draw();
    }
    private static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    internal void Reset(string reason)
    { foreach(var model in Models) model.Reset(reason); Draw(); }
    internal void SetActive(bool active)
    { for(int i=0;i<Models.Length;i++) Models[i].SetActive(active&&i<channels,Now); if(!active) Draw(); }
    internal void Update(HeadAlignmentMeasurement? measurement,AudioCaptureStreamInfo? source,long discontinuities,bool active)
    {
        if(source==null) { Reset("New capture — zero is session-local and must be set again."); return; }
        int count=source.ActualFormat.Channels;
        if(channels!=count)
        {
            channels=count; left.Title=count==1?"Mono • combined edges":"Left • combined edges";
            right.Visibility=count==1?Visibility.Collapsed:Visibility.Visible; Grid.SetColumnSpan(left,count==1?2:1);
        }
        if(!active||measurement==null) { SetActive(false); return; }
        for(int i=0;i<count;i++) Models[i].Update(measurement,new(source.Device.Id,source.Processing?.ToString()??source.ProcessingDescription,discontinuities,recording.Text),i,Repeated.IsChecked==true,Now);
        Draw();
    }
    private void Draw()
    {
        int usable=0; for(int i=0;i<channels;i++) if(Models[i].Current.CanZero) usable++;
        ZeroHere.IsEnabled=usable>0; ZeroHere.Content=channels==1?"Zero here (Mono)":usable==channels?"Zero here (L + R)":$"Zero usable ({usable}/{channels})";
        status.Update(Models,channels);
        detail.Text="";
        for(int i=0;i<channels;i++)
        {
            var v=Models[i].Current;
            detail.Text+=(i==0?"":"\n")+$"{(i==0?channels==1?"Mono":"Left":"Right")}: {v.Windows} distinct windows • rise MAD {v.Rise.WindowMadPercent:F1}% / temporal {v.Rise.TemporalMadPercent:F1}% • fall {v.Fall.WindowMadPercent:F1}% / {v.Fall.TemporalMadPercent:F1}% • sample {v.SamplePeriodMicroseconds:F2} µs"+
                (v.HasZero?$" • zero [{v.ZeroStartFrame:N0}, {v.ZeroEndFrame:N0}){(v.ZeroIsUncertain?" • uncertain manual zero":"")}":"")+"\n"+v.Message;
        }
        double limit=double.Parse((string)range.SelectedItem,CultureInfo.InvariantCulture);
        for(int i=0;i<2;i++)
        {
            var dial=i==0?left:right; var snapshot=Models[i].Current;
            dial.Limit=limit; dial.Comparable=snapshot.State is HeadEdgeAdjustmentState.WorkingCopy or HeadEdgeAdjustmentState.RepeatedPassage;
            dial.Repeated=Repeated.IsChecked==true&&!snapshot.ZeroIsUncertain; dial.Value=snapshot.Combined; dial.Rise=snapshot.Rise; dial.Fall=snapshot.Fall; dial.InvalidateVisual();
        }
    }
    private static TextBlock Text(string text)=>new() { Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(5) };
    private static void Add(Panel panel,string title,FrameworkElement field) { panel.Children.Add(new TextBlock { Text=title,VerticalAlignment=VerticalAlignment.Center,Margin=new(5) }); panel.Children.Add(field); }
    internal sealed class SignalStatus:FrameworkElement
    {
        private HeadEdgeAdjustmentModel[] models=[];
        private int channels;
        private string lastTip="";
        internal SignalStatus() { Height=36; Margin=new(5,2,5,2); ClipToBounds=true; }
        internal void Update(HeadEdgeAdjustmentModel[] values,int count)
        {
            models=values; channels=count;
            string tip="Signal / Stability / Zero: ✓ usable, ! uncertain/limited, × unavailable, — absent.\n";
            for(int i=0;i<count;i++) tip+=(i==0?count==1?"Mono":"Left":"Right")+": "+values[i].Current.State+" — "+values[i].Current.Message+"\n";
            if(lastTip!=tip)
            {
                lastTip=tip;
                ToolTip=new ToolTip { Content=Text(tip.Trim()),MaxWidth=640,Placement=System.Windows.Controls.Primitives.PlacementMode.Mouse,HorizontalOffset=16,VerticalOffset=20 };
                AutomationProperties.SetName(this,tip.Trim());
            }
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            void Label(string text,double x,Brush color)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,color,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,10));
            int count=Math.Max(1,channels); double width=ActualWidth/count;
            for(int i=0;i<count;i++)
            {
                var snapshot=models.Length>i?models[i].Current:null;
                double x=i*width; Label(i==0?count==1?"Mono":"Left":"Right",x,Brushes.Black); x+=42;
                bool fresh=snapshot!=null&&snapshot.State is not (HeadEdgeAdjustmentState.Waiting or HeadEdgeAdjustmentState.Unavailable or HeadEdgeAdjustmentState.Paused or HeadEdgeAdjustmentState.Stale);
                bool stable=snapshot?.State is HeadEdgeAdjustmentState.ReadyToZero or HeadEdgeAdjustmentState.WorkingCopy or HeadEdgeAdjustmentState.RepeatedPassage;
                var entries=new[]{("Signal",fresh?1:0),("Stable",fresh?stable?1:2:0),("Zero",snapshot?.HasZero==true?snapshot.ZeroIsUncertain?2:1:3)};
                foreach(var (name,state) in entries)
                {
                    Brush color=state==1?Brushes.ForestGreen:state==2?Brushes.DarkGoldenrod:state==0?Brushes.Firebrick:Brushes.Gray;
                    dc.DrawEllipse(color,null,new(x+8,18),8,8);
                    Label(state==1?"✓":state==2?"!":state==0?"×":"—",x+3,Brushes.White); Label(name,x+20,Brushes.Black); x+=82;
                }
            }
        }
    }
    private sealed class EdgeDial:FrameworkElement
    {
        internal string Title="";
        internal HeadEdgeNeedle? Value,Rise,Fall;
        internal double Limit=50;
        internal bool Comparable,Repeated;
        internal EdgeDial() { ClipToBounds=true; }
        protected override void OnRender(DrawingContext dc)
        {
            double w=ActualWidth,h=ActualHeight; if(w<=0||h<=0) return;
            dc.DrawRoundedRectangle(Brushes.WhiteSmoke,new Pen(Brushes.LightGray,1),new Rect(5,5,Math.Max(0,w-10),Math.Max(0,h-10)),10,10);
            void Text(string text,double x,double y,double size=12,Brush? brush=null)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,brush??Brushes.DimGray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
            Text(Title,15,12,15,Brushes.Black);
            // A shallow VU arc with a virtual pivot below the face remains readable in a short panel.
            double radius=Math.Max(25,Math.Min(w*.60,(h-114)/(1-Math.Cos(40*Math.PI/180)))),cx=w*.5,cy=50+radius;
            Point Position(double v,double length)=>new(cx+Math.Sin(v*Math.PI/180)*length,cy-Math.Cos(v*Math.PI/180)*length);
            var geometry=new StreamGeometry(); using(var path=geometry.Open()) { path.BeginFigure(Position(-40,radius),false,false); for(int a=-39;a<=40;a++) path.LineTo(Position(a,radius),true,false); }
            geometry.Freeze(); dc.DrawGeometry(null,new Pen(Brushes.SlateGray,2),geometry);
            for(int i=-4;i<=4;i++)
            {
                double angle=i*10; dc.DrawLine(new Pen(Brushes.SlateGray,i==0?2:1),Position(angle,radius),Position(angle,radius-(i%2==0?12:6)));
                if(i%2==0) { var point=Position(angle,radius+15); Text((Limit*i/4).ToString("+0;-0;0"),point.X-12,point.Y-8,11); }
            }
            var color=Comparable?Repeated?Brushes.RoyalBlue:Brushes.DarkGoldenrod:Brushes.Gray;
            double angleValue=Value?.DisplayPercent is double percent?Math.Clamp(percent/Limit,-1,1)*40:0;
            var pen=new Pen(color,3); if(!Comparable) pen.DashStyle=DashStyles.Dash;
            dc.PushClip(new RectangleGeometry(new Rect(10,40,Math.Max(0,w-20),Math.Max(0,h-100))));
            dc.DrawLine(pen,new(cx,cy),Position(angleValue,Math.Max(4,radius-15))); dc.DrawEllipse(color,null,new(cx,cy),5,5);
            dc.Pop();
            dc.DrawRectangle(Brushes.WhiteSmoke,null,new Rect(10,Math.Max(30,h-60),Math.Max(0,w-20),55));
            string Time(double? us)=>us.HasValue?$"{us:F2} µs":"n/a";
            Text($"Mean {Time(Value?.CurrentMicroseconds)} • "+(Comparable&&Value?.ChangePercent is double raw?$"raw Δ {raw:+0.0;-0.0;0.0}%{(Math.Abs(raw)>Limit?" (off scale)":"")}":"held / unavailable"),15,h-48,12,color);
            Text($"↑ {Time(Rise?.CurrentMicroseconds)} / ↓ {Time(Fall?.CurrentMicroseconds)} • zero {Time(Value?.ZeroMicroseconds)}",15,h-28,11);
        }
    }
}
