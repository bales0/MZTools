using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MZTools.Capture;

namespace MZTools.Desktop.Controls;

// The same graph component paints offline Core and live Capture snapshots. No callback or growing WAV is accessed.
public sealed partial class AudioChart
{
    public static readonly StyledProperty<LiveSignalSnapshot?> LiveSnapshotProperty=AvaloniaProperty.Register<AudioChart,LiveSignalSnapshot?>(nameof(LiveSnapshot));
    public LiveSignalSnapshot? LiveSnapshot {get=>GetValue(LiveSnapshotProperty);set=>SetValue(LiveSnapshotProperty,value);}
    public static readonly StyledProperty<int> LiveChannelProperty=AvaloniaProperty.Register<AudioChart,int>(nameof(LiveChannel));
    public int LiveChannel {get=>GetValue(LiveChannelProperty);set=>SetValue(LiveChannelProperty,value);}
    public static readonly StyledProperty<bool> LiveAutoScaleProperty=AvaloniaProperty.Register<AudioChart,bool>(nameof(LiveAutoScale),true);
    public bool LiveAutoScale {get=>GetValue(LiveAutoScaleProperty);set=>SetValue(LiveAutoScaleProperty,value);}
    public static readonly StyledProperty<double> LiveGainProperty=AvaloniaProperty.Register<AudioChart,double>(nameof(LiveGain),1);
    public double LiveGain {get=>GetValue(LiveGainProperty);set=>SetValue(LiveGainProperty,value);}
    public event Action<double,double>? LiveZoom;
    public event Action<long>? LivePan;
    public static readonly StyledProperty<long?> LiveSelectionStartProperty=AvaloniaProperty.Register<AudioChart,long?>(nameof(LiveSelectionStart));
    public long? LiveSelectionStart {get=>GetValue(LiveSelectionStartProperty);set=>SetValue(LiveSelectionStartProperty,value);}
    public static readonly StyledProperty<long?> LiveSelectionEndProperty=AvaloniaProperty.Register<AudioChart,long?>(nameof(LiveSelectionEnd));
    public long? LiveSelectionEnd {get=>GetValue(LiveSelectionEndProperty);set=>SetValue(LiveSelectionEndProperty,value);}
    public event Action<long,long>? LiveSelected;
    private bool liveSelecting;private long liveSelectFirst;
    private Point? livePress;
    private double liveFrequencyMin,liveFrequencyMax;
    private void InitializeLive()
    {
        PropertyChanged+=(_,e)=>{if(e.Property==LiveSnapshotProperty||e.Property==LiveChannelProperty||e.Property==LiveAutoScaleProperty||e.Property==LiveGainProperty||e.Property==ComparisonProperty||e.Property==LiveSelectionStartProperty||e.Property==LiveSelectionEndProperty||e.Property==EdgeLeftProperty||e.Property==EdgeRightProperty||e.Property==EdgeRangeProperty||e.Property==ShowLiveReferenceProperty)InvalidateVisual();};
        AddHandler(PointerWheelChangedEvent,(_,e)=>
        {
            if(LiveSnapshot is not { } s)return;double factor=e.Delta.Y>0?1.5:1/1.5,anchor=Math.Clamp((e.GetPosition(this).X-Plot.X)/Plot.Width,0,1);
            if(Kind==AudioPlotKind.Spectrum){double max=liveFrequencyMax>liveFrequencyMin?liveFrequencyMax:s.Format.SampleRate/2.0,width=Math.Clamp((max-liveFrequencyMin)/factor,s.FrequencyResolutionHz,s.Format.SampleRate/2.0);liveFrequencyMin=Math.Clamp(liveFrequencyMin+(max-liveFrequencyMin-width)*anchor,0,s.Format.SampleRate/2.0-width);liveFrequencyMax=liveFrequencyMin+width;InvalidateVisual();}
            else LiveZoom?.Invoke(factor,anchor);e.Handled=true;
        },Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent,(_,e)=>{if(LiveSnapshot is not null&&e.GetCurrentPoint(this).Properties.IsLeftButtonPressed){Focus();livePress=e.GetPosition(this);liveSelecting=e.KeyModifiers.HasFlag(KeyModifiers.Shift)&&Kind==AudioPlotKind.Waveform;if(liveSelecting&&LiveSnapshot is { } s)liveSelectFirst=s.WaveStartFrame+(long)(Math.Clamp((livePress.Value.X-Plot.X)/Plot.Width,0,1)*(s.WaveEndFrame-s.WaveStartFrame));e.Pointer.Capture(this);e.Handled=true;}},Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent,(_,e)=>
        {
            if(LiveSnapshot is not { } s||livePress is not { } first)return;var at=e.GetPosition(this);
            if(liveSelecting){long last=s.WaveStartFrame+(long)(Math.Clamp((at.X-Plot.X)/Plot.Width,0,1)*(s.WaveEndFrame-s.WaveStartFrame));if(last!=liveSelectFirst)LiveSelected?.Invoke(liveSelectFirst,last);}
            else if(Kind==AudioPlotKind.Spectrum){double max=liveFrequencyMax>liveFrequencyMin?liveFrequencyMax:s.Format.SampleRate/2.0,width=max-liveFrequencyMin;liveFrequencyMin=Math.Clamp(liveFrequencyMin-(at.X-first.X)/Plot.Width*width,0,s.Format.SampleRate/2.0-width);liveFrequencyMax=liveFrequencyMin+width;InvalidateVisual();}
            else LivePan?.Invoke((long)Math.Round(-(at.X-first.X)/Plot.Width*(Kind==AudioPlotKind.Histogram?1000:s.WaveEndFrame-s.WaveStartFrame)));
            livePress=at;e.Handled=true;
        },Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent,(_,e)=>{if(livePress is not null){livePress=null;e.Pointer.Capture(null);e.Handled=true;}},Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent,(_,e)=>{if(LiveSnapshot is null)return;if(e.Key is Key.Add or Key.OemPlus){LiveZoom?.Invoke(1.5,.5);e.Handled=true;}if(e.Key is Key.Subtract or Key.OemMinus){LiveZoom?.Invoke(1/1.5,.5);e.Handled=true;}},Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    private void RenderLive(DrawingContext c,LiveSignalSnapshot s)
    {
        if(Kind==AudioPlotKind.StereoIndicator){RenderStereo(c,s.Alignment?.Stereo);return;}
        if(Kind==AudioPlotKind.Histogram){RenderLivePulses(c,s);return;}
        bool right=LiveChannel==1&&s.Format.Channels==2,both=LiveChannel==2&&s.Format.Channels==2;
        if(Kind==AudioPlotKind.Spectrum)
        {
            Grid(c,$"{s.SpectrumMethod} · N={s.FftSize} · Δf={s.FrequencyResolutionHz:0.###} Hz");Text(c,$"FFT frames [{s.SpectrumStartFrame:N0}, {s.SpectrumEndFrame:N0})",Plot.X,Plot.Bottom+6);
            double max=liveFrequencyMax>liveFrequencyMin?liveFrequencyMax:s.Format.SampleRate/2.0;
            using(c.PushClip(Plot))
            {
                void Channel(bool r,string color)=>DrawSpectrumSeries(c,s.Spectrum,liveFrequencyMin,max,r,color);
                Channel(right,right?"wave-r":"wave-l");if(both)Channel(true,"wave-r");
            }
            Text(c,$"{liveFrequencyMin:0.##} Hz",Plot.X,Plot.Y);Text(c,$"{max:0.##} Hz",Plot.Right-110,Plot.Y);Text(c,"+20 dBFS",0,Plot.Y+14);Text(c,"−160",0,Plot.Bottom-12);return;
        }
        Grid(c,$"Live PCM FS · {(both?"L + R overlay":right?"R":"L / mono")} · {(LiveAutoScale?"Auto":"Gain ×"+LiveGain.ToString("0.###"))} · {(s.Triggered?"Triggered":"Free run")}");
        double peak=s.Waveform.Select(b=>both?Math.Max(Math.Max(Math.Abs(b.LeftMin),Math.Abs(b.LeftMax)),Math.Max(Math.Abs(b.RightMin),Math.Abs(b.RightMax))):right?Math.Max(Math.Abs(b.RightMin),Math.Abs(b.RightMax)):Math.Max(Math.Abs(b.LeftMin),Math.Abs(b.LeftMax))).DefaultIfEmpty().Max();double scale=LiveAutoScale?(peak>0?.9/peak:1):Math.Clamp(LiveGain,.001,1000);
        double X(long frame)=>Plot.X+(frame-s.WaveStartFrame)/(double)Math.Max(1,s.WaveEndFrame-s.WaveStartFrame)*Plot.Width;
        double Y(double value)=>Plot.Y+Plot.Height/2-value*scale*Plot.Height/2;
        using(c.PushClip(Plot))
        {
            if(LiveSelectionStart is { } first&&LiveSelectionEnd is { } last&&last>first)c.DrawRectangle(Brush("selected"),null,new Rect(X(first),Plot.Y,Math.Max(0,X(last)-X(first)),Plot.Height));
            void Channel(bool r,string color)=>DrawWaveSeries(c,s.Waveform,X,Y,r,color);
            Channel(right,right?"wave-r":"wave-l");if(both)Channel(true,"wave-r");
        }
        Text(c,$"{1/scale:0.###}",0,Plot.Y);Text(c,"0",25,Plot.Y+Plot.Height/2);Text(c,$"{-1/scale:0.###}",0,Plot.Bottom-12);
        for(int i=0;i<=4;i++){long frame=s.WaveStartFrame+(long)((s.WaveEndFrame-s.WaveStartFrame)*i/4.0);Text(c,$"{frame/(double)s.Format.SampleRate:0.######} s",Math.Min(Plot.Right-90,Plot.X+Plot.Width*i/4),Plot.Bottom+6);}
    }
    private void RenderLivePulses(DrawingContext c,LiveSignalSnapshot s)
    {
        Grid(c,"Live pulse durations · µs · measured / reference comparison, not decoder acceptance");if(s.Pulses is not { } p)return;
        double min=p.HistogramMinimumMicroseconds,max=p.HistogramMaximumMicroseconds,count=Math.Max(1,p.Histogram.SelectMany(b=>b.Counts).DefaultIfEmpty().Max());
        using(c.PushClip(Plot))
        {
            foreach(var bin in p.Histogram)for(int group=0;group<bin.Counts.Count;group++)
            {
                double x=Plot.X+(bin.MinimumMicroseconds-min)/Math.Max(.001,max-min)*Plot.Width,height=bin.Counts[group]/count*Plot.Height,width=Math.Max(1,(bin.MaximumMicroseconds-bin.MinimumMicroseconds)/(max-min)*Plot.Width/6);
                c.DrawRectangle(group<4?Pulse(AudioPulseReference.Groups[group]):Brush("muted"),null,new Rect(x+group*width,Plot.Bottom-height,width,height));
            }
            if(ShowLiveReference)foreach(var guide in p.Options.Reference.Guides){double x=Plot.X+(guide.Microseconds*p.Options.ReferenceScale-min)/(max-min)*Plot.Width;c.DrawLine(new Pen(Pulse(AudioPulseReference.Groups[(int)guide.Group])),new Point(x,Plot.Y),new Point(x,Plot.Bottom));}
        }
        Text(c,$"{count:N0}",0,Plot.Y);Text(c,$"{min:0.###} µs",Plot.X,Plot.Bottom+6);Text(c,$"{max:0.###} µs",Plot.Right-110,Plot.Bottom+6);
    }
    public static readonly StyledProperty<HeadAlignmentComparison?> ComparisonProperty=AvaloniaProperty.Register<AudioChart,HeadAlignmentComparison?>(nameof(Comparison));
    public HeadAlignmentComparison? Comparison {get=>GetValue(ComparisonProperty);set=>SetValue(ComparisonProperty,value);}
    private void DrawWaveSeries(DrawingContext c,IEnumerable<LiveWaveBucket> buckets,Func<long,double> x,Func<double,double> y,bool right,string color)
    {
        Point? previous=null;foreach(var bucket in buckets)
        {
            double minimum=right?bucket.RightMin:bucket.LeftMin,maximum=right?bucket.RightMax:bucket.LeftMax;var point=new Point(x(bucket.FirstFrame),y(minimum));
            if(bucket.EndFrame-bucket.FirstFrame==1){if(previous is { } old)c.DrawLine(new Pen(Brush(color)),old,point);previous=point;}
            else{c.DrawLine(new Pen(Brush(color)),point,new Point(point.X,y(maximum)));previous=null;}
        }
    }
    private void DrawSpectrumSeries(DrawingContext c,IEnumerable<LiveSpectrumBin> bins,double minimum,double maximum,bool right,string color)
    {
        Point? previous=null;foreach(var bin in bins.Where(b=>b.FrequencyHz>=minimum&&b.FrequencyHz<=maximum))
        {
            var point=new Point(Plot.X+(bin.FrequencyHz-minimum)/Math.Max(.001,maximum-minimum)*Plot.Width,Plot.Bottom-((right?bin.RightDbfs:bin.LeftDbfs)+160)/180*Plot.Height);
            if(previous is { } old)c.DrawLine(new Pen(Brush(color)),old,point);previous=point;
        }
    }
    private void RenderAlignment(DrawingContext c,HeadAlignmentComparison comparison)
    {
        Grid(c,"A / B · per-metric relative bars, original units retained · no combined score");
        if(!comparison.Assessable){Text(c,comparison.Status,Plot.X,Plot.Y+10);return;}
        var rows=comparison.Differences.Take(14).ToArray();double height=Plot.Height/Math.Max(1,rows.Length);
        for(int i=0;i<rows.Length;i++)
        {
            var row=rows[i];double y=Plot.Y+i*height,scale=Math.Max(Math.Abs(row.A),Math.Abs(row.B));if(row.Unit=="%")scale=100;if(row.Unit is "FS" or "0–1")scale=1;if(scale==0)scale=1;
            Text(c,$"{row.Channel} · {row.Metric} ({row.Unit}) · Δ {row.Delta:0.###}",Plot.X,y);
            double width=Math.Max(1,Plot.Width-400),first=Plot.X+360;
            bool signed=row.A<0||row.B<0;double origin=first+(signed?width/2:0),length=signed?width/2:width;
            c.DrawLine(new Pen(Brush("border")),new Point(origin,y),new Point(origin,y+height-1));
            c.DrawRectangle(Brush("wave-l"),null,new Rect(origin+Math.Min(0,row.A)/scale*length,y+3,Math.Abs(row.A)/scale*length,4));
            c.DrawRectangle(Brush("wave-r"),null,new Rect(origin+Math.Min(0,row.B)/scale*length,y+10,Math.Abs(row.B)/scale*length,4));
        }
    }

    public static readonly StyledProperty<bool> ShowLiveReferenceProperty=AvaloniaProperty.Register<AudioChart,bool>(nameof(ShowLiveReference),true);
    public bool ShowLiveReference {get=>GetValue(ShowLiveReferenceProperty);set=>SetValue(ShowLiveReferenceProperty,value);}
    public static readonly StyledProperty<HeadEdgeAdjustmentSnapshot?> EdgeLeftProperty=AvaloniaProperty.Register<AudioChart,HeadEdgeAdjustmentSnapshot?>(nameof(EdgeLeft));
    public HeadEdgeAdjustmentSnapshot? EdgeLeft {get=>GetValue(EdgeLeftProperty);set=>SetValue(EdgeLeftProperty,value);}
    public static readonly StyledProperty<HeadEdgeAdjustmentSnapshot?> EdgeRightProperty=AvaloniaProperty.Register<AudioChart,HeadEdgeAdjustmentSnapshot?>(nameof(EdgeRight));
    public HeadEdgeAdjustmentSnapshot? EdgeRight {get=>GetValue(EdgeRightProperty);set=>SetValue(EdgeRightProperty,value);}
    public static readonly StyledProperty<double> EdgeRangeProperty=AvaloniaProperty.Register<AudioChart,double>(nameof(EdgeRange),50);
    public double EdgeRange {get=>GetValue(EdgeRangeProperty);set=>SetValue(EdgeRangeProperty,value);}
    private void RenderEdges(DrawingContext c)
    {
        Grid(c,"Match this tape copy · relative edge change from your zero · no calibrated angle");
        int index=0;foreach(var snapshot in new[]{EdgeLeft,EdgeRight})
        {
            double y=Plot.Y+index*Plot.Height/2,width=Plot.Width/2;
            Text(c,(index==0?"L / mono":"R")+" · "+snapshot?.State,Plot.X,y+3);
            if(snapshot is { } s)
            {
                double middle=Plot.X+width;double value=Math.Clamp(s.Combined.DisplayPercent??0,-EdgeRange,EdgeRange)/Math.Max(1,EdgeRange);
                c.DrawLine(new Pen(Brush("border"),2),new Point(Plot.X,y+45),new Point(Plot.Right,y+45));c.DrawLine(new Pen(Brush("muted")),new Point(middle,y+30),new Point(middle,y+60));
                c.DrawLine(new Pen(Brush(index==0?"wave-l":"wave-r"),3),new Point(middle+value*width,y+28),new Point(middle+value*width,y+62));
                Text(c,$"−{EdgeRange:0.#}%",Plot.X,y+65);Text(c,"0",middle-4,y+65);Text(c,$"+{EdgeRange:0.#}%",Plot.Right-60,y+65);
                Text(c,$"Rise {s.Rise.CurrentMicroseconds:0.###} µs · fall {s.Fall.CurrentMicroseconds:0.###} µs · zero [{s.ZeroStartFrame}, {s.ZeroEndFrame})",Plot.X,y+90);
                Text(c,s.Message,Plot.X,y+108);
            }
            index++;
        }
    }
    private void RenderStereo(DrawingContext c,HeadAlignmentStereo? stereo)
    {
        Grid(c,"Stereo L / R · signal phase modulo one cycle, not mechanical head angle");
        if(stereo is not {Assessable:true} s){Text(c,stereo?.Status??"No stereo measurement",Plot.X,Plot.Y+15);return;}
        var center=new Point(Plot.X+Plot.Width/3,Plot.Y+Plot.Height/2);double radius=Math.Min(Plot.Width/4,Plot.Height/2-20);
        c.DrawEllipse(null,new Pen(Brush("border"),2),center,radius,radius);
        if(s.SignalPhaseDegrees is { } phase){double angle=phase*Math.PI/180;c.DrawLine(new Pen(Brush("wave-r"),3),center,new Point(center.X+Math.Cos(angle)*radius,center.Y-Math.Sin(angle)*radius));Text(c,$"R−L {phase:0.###}°",center.X-radius,center.Y+radius+4);}else Text(c,"Known qualifying tone required for phase",center.X-radius,center.Y);
        Text(c,$"Coherence {s.Coherence:0.###}",Plot.X+Plot.Width*.65,Plot.Y+40);Text(c,$"Right delay {s.DelayMicroseconds:0.###} µs",Plot.X+Plot.Width*.65,Plot.Y+70);Text(c,$"R−L level {s.LevelDifferenceDb:0.###} dB",Plot.X+Plot.Width*.65,Plot.Y+100);
    }

}
