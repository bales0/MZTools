using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MZTools.Capture;

namespace MZTools;

/// <summary>Bounded, display-only drawing adapter; receives no capture callbacks.</summary>
internal sealed class LiveSignalCanvas : FrameworkElement
{
    internal LiveSignalSnapshot? Snapshot { get; set; }
    internal bool Spectrum { get; init; }
    internal int Channel { get; set; } = 2;
    internal double Gain { get; set; } = 1;
    internal bool AutoScale { get; set; }
    internal event Action<double,double>? ZoomRequested;
    internal event Action<double>? PanRequested;
    private Point? drag;
    internal LiveSignalCanvas()
    {
        MinHeight=100; ClipToBounds=true;
        MouseWheel += (_,e) => { if (!Spectrum) ZoomRequested?.Invoke(e.Delta>0 ? .5 : 2,Math.Clamp((e.GetPosition(this).X-62)/Math.Max(1,ActualWidth-78),0,1)); e.Handled=true; };
        MouseLeftButtonDown += (_,e) => { if (!Spectrum) { drag=e.GetPosition(this); CaptureMouse(); } };
        MouseLeftButtonUp += (_,_) => { drag=null; ReleaseMouseCapture(); };
        LostMouseCapture += (_,_) => drag=null;
        MouseMove += (_,e) => { if (drag is Point previous) { var current=e.GetPosition(this); PanRequested?.Invoke((previous.X-current.X)/Math.Max(1,ActualWidth-78)); drag=current; } };
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White,null,new Rect(RenderSize));
        double width=Math.Max(1,ActualWidth-78),height=Math.Max(1,ActualHeight-52),left=62,top=18;
        void Text(string text,double x,double y,Brush? color=null) => dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,color??Brushes.DimGray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        var grid=new Pen(Brushes.Gainsboro,1);
        for(int i=0;i<=4;i++) { double y=top+height*i/4; dc.DrawLine(grid,new(left,y),new(left+width,y)); Text(Spectrum ? $"{-40*i} dB" : $"{1-i*.5:F1}",2,y-7); }
        var data=Snapshot;
        if(data==null) { Text("Waiting for input…",left,top+24); return; }
        bool stereo=data.Format.Channels==2;
        for(int ch=0;ch<(stereo?2:1);ch++)
        {
            if(stereo && Channel!=2 && Channel!=ch) continue;
            var pen=new Pen(ch==0?Brushes.RoyalBlue:Brushes.DarkOrange,1);
            var geometry=new StreamGeometry();
            using(var path=geometry.Open())
            {
                if(Spectrum)
                {
                    bool started=false;
                    foreach(var bin in data.Spectrum)
                    {
                        var point=new Point(left+width*bin.FrequencyHz/(data.Format.SampleRate/2.0),top+height*Math.Clamp(-(ch==0?bin.LeftDbfs:bin.RightDbfs)/160,0,1));
                        if(!started) { path.BeginFigure(point,false,false); started=true; } else path.LineTo(point,true,false);
                    }
                }
                else
                {
                    double amplitude=1;
                    if(AutoScale) { amplitude=0; foreach(var b in data.Waveform) amplitude=Math.Max(amplitude,Math.Max(Math.Abs(ch==0?b.LeftMin:b.RightMin),Math.Abs(ch==0?b.LeftMax:b.RightMax))); amplitude=Math.Max(.00001,amplitude); }
                    long frames=Math.Max(1,data.WaveEndFrame-data.WaveStartFrame);
                    bool sampleLine=false;
                    foreach(var b in data.Waveform)
                    {
                        double x=left+width*(b.FirstFrame+(b.EndFrame-b.FirstFrame)*.5-data.WaveStartFrame)/frames;
                        double Y(double v) => top+height*(.5-.48*Math.Clamp(v*Gain/amplitude,-1,1));
                        double y1=Y(ch==0?b.LeftMin:b.RightMin),y2=Y(ch==0?b.LeftMax:b.RightMax);
                        if(b.EndFrame-b.FirstFrame==1)
                        {
                            if(!sampleLine) path.BeginFigure(new(x,y1),false,false); else path.LineTo(new(x,y1),true,false);
                            sampleLine=true;
                        }
                        else { path.BeginFigure(new(x,y1),false,false); path.LineTo(new(x,Math.Abs(y1-y2)<1?y1+1:y2),true,false); sampleLine=false; }
                    }
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null,pen,geometry);
        }
        for(int i=0;i<=4;i++)
        {
            double x=left+width*i/4; dc.DrawLine(grid,new(x,top),new(x,top+height));
            double value=Spectrum ? data.Format.SampleRate*.5*i/4 : (data.WaveStartFrame+(data.WaveEndFrame-data.WaveStartFrame)*i/4.0)/data.Format.SampleRate;
            Text(Spectrum?$"{value:N0} Hz":$"{value:F4} s",Math.Clamp(x-20,0,Math.Max(0,ActualWidth-75)),top+height+5);
        }
        Text(Spectrum?$"Hann • N={data.FftSize} • Δf={data.FrequencyResolutionHz:F2} Hz • peak amplitude dBFS":$"{(stereo?"Blue L / orange R":"Blue Mono")} • {(AutoScale?"auto scale":"FS")} × {Gain:G3} • {(data.Triggered?"triggered":"free run")} • min/max or sample lines • display only",left,0);
    }
}
