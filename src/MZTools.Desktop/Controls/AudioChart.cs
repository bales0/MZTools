using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

public enum AudioPlotKind {Waveform,Histogram,Scatter,Drift,Spectrum,Spectrogram,Alignment,HeadEdges,StereoIndicator}

// Paints prepared Core snapshots only. Source reading, decoding and FFT are never performed here.
public sealed partial class AudioChart : Control
{
    public static readonly StyledProperty<AudioSession?> SessionProperty=AvaloniaProperty.Register<AudioChart,AudioSession?>(nameof(Session));
    public AudioSession? Session {get=>GetValue(SessionProperty);set=>SetValue(SessionProperty,value);}
    public static readonly StyledProperty<AudioPlotKind> KindProperty=AvaloniaProperty.Register<AudioChart,AudioPlotKind>(nameof(Kind));
    public AudioPlotKind Kind {get=>GetValue(KindProperty);set=>SetValue(KindProperty,value);}
    private AudioSession? observed;
    private WriteableBitmap? leftRaster,rightRaster;
    private AudioSpectrogram? rasterSource;
    private Point? press;private long pressStart,pressEnd;private double pressAxis;private bool selecting,moved;
    private Rect Plot=>new(56,24,Math.Max(1,Bounds.Width-68),Math.Max(1,Bounds.Height-52));
    private IBrush Brush(string name)=>this.FindResource("F2."+name) as IBrush??Brushes.Transparent;
    private void Text(DrawingContext c,string value,double x,double y,string color="text",double size=11)=>c.DrawText(new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,size,Brush(color)),new Point(x,y));
    public AudioChart()
    {
        Focusable=true;ClipToBounds=true;MinHeight=180;InitializeLive();
        AttachedToVisualTree+=(_,_)=>{DisposeRaster();Bind();};DetachedFromVisualTree+=(_,_)=>{if(observed is not null)observed.PropertyChanged-=Changed;observed=null;DisposeRaster();};
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {base.OnPropertyChanged(change);if(change.Property==SessionProperty||change.Property==KindProperty)Bind();}
    private void Bind()
    {
        if(observed!=Session){if(observed is not null)observed.PropertyChanged-=Changed;observed=Session;if(observed is not null)observed.PropertyChanged+=Changed;DisposeRaster();}
        if(Kind==AudioPlotKind.Spectrogram)PrepareRaster();InvalidateVisual();
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {if(Kind==AudioPlotKind.Spectrogram&&e.PropertyName=="SpectrogramData")PrepareRaster();InvalidateVisual();}
    private void DisposeRaster(){leftRaster?.Dispose();rightRaster?.Dispose();leftRaster=rightRaster=null;rasterSource=null;}
    private void PrepareRaster()
    {
        if(Session?.SpectrogramData==rasterSource)return;DisposeRaster();
        if(Session?.SpectrogramData is not {Columns.Count:>0} data)return;
        rasterSource=data;int width=data.Columns.Count,height=data.Columns[0].Left.Length;
        Color low=(Brush("graph-grid") as ISolidColorBrush)?.Color??Colors.Transparent,high=(Brush("wave-l") as ISolidColorBrush)?.Color??Colors.Transparent;
        WriteableBitmap Make(bool right)
        {
            var bitmap=new WriteableBitmap(new PixelSize(width,height),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);
            using var frame=bitmap.Lock();var bytes=new byte[frame.RowBytes*height];
            for(int x=0;x<width;x++)for(int y=0;y<height;y++)
            {
                double db=right?data.Columns[x].Right[y]:data.Columns[x].Left[y],amount=Math.Clamp((db+160)/160,0,1);
                int at=(height-1-y)*frame.RowBytes+x*4;bytes[at]=(byte)(low.B+(high.B-low.B)*amount);bytes[at+1]=(byte)(low.G+(high.G-low.G)*amount);bytes[at+2]=(byte)(low.R+(high.R-low.R)*amount);bytes[at+3]=255;
            }
            Marshal.Copy(bytes,0,frame.Address,bytes.Length);return bitmap;
        }
        leftRaster=Make(false);rightRaster=Make(true);
    }
    private double FrameX(long frame)=>Plot.X+(frame-Session!.ViewStart)/(double)Math.Max(1,Session.ViewEnd-Session.ViewStart)*Plot.Width;
    private long AtFrame(double x)=>Session!.FrameAt(x-Plot.X,Plot.Width);
    private void Grid(DrawingContext c,string title)
    {
        c.DrawRectangle(Brush("surface"),new Pen(Brush("border")),new Rect(Bounds.Size));Text(c,title,8,4);
        for(int i=0;i<=4;i++){double y=Plot.Y+Plot.Height*i/4;c.DrawLine(new Pen(Brush("graph-grid")),new Point(Plot.X,y),new Point(Plot.Right,y));}
    }
    private void TimeAxis(DrawingContext c)
    {
        if(Session is not { } s)return;
        for(int i=0;i<=4;i++){long frame=s.ViewStart+(long)((s.ViewEnd-s.ViewStart)*i/4.0);double x=Plot.X+Plot.Width*i/4;Text(c,$"{frame/(double)s.Model.SampleRate:0.######} s",Math.Clamp(x-20,0,Math.Max(0,Bounds.Width-90)),Plot.Bottom+6);}
    }
    public override void Render(DrawingContext c)
    {
        base.Render(c);if(Kind==AudioPlotKind.HeadEdges){RenderEdges(c);return;}if(Kind==AudioPlotKind.Alignment&&Comparison is { } comparison){RenderAlignment(c,comparison);return;}if(LiveSnapshot is { } live){RenderLive(c,live);return;}if(Session is not { } s){Grid(c,"No audio analysis");return;}
        switch(Kind)
        {
            case AudioPlotKind.Waveform:Waveform(c,s);break;
            case AudioPlotKind.Histogram:Histogram(c,s);break;
            case AudioPlotKind.Scatter:Scatter(c,s);break;
            case AudioPlotKind.Drift:Drift(c,s);break;
            case AudioPlotKind.Spectrum:Spectrum(c,s);break;
            case AudioPlotKind.Spectrogram:Spectrogram(c,s);break;
        }
    }
    private void Waveform(DrawingContext c,AudioSession s)
    {
        Grid(c,$"PCM FS · {(s.AutoScale?"Auto scale":"Display gain ×"+s.Gain.ToString("0.###"))} · frames [{s.ViewStart:N0}, {s.ViewEnd:N0})");TimeAxis(c);
        if(s.DisplayedTransform!=s.EffectiveTransform){Text(c,"Preparing requested PCM transform…",Plot.X,Plot.Y+20);return;}
        var buckets=s.DisplayWaveform.VisibleBuckets(s.ViewStart,s.ViewEnd,Math.Max(1,(int)Plot.Width));
        bool both=s.ChannelMode==2&&!s.EffectiveTransform.Mix&&s.Model.ChannelCount==2,right=s.ChannelMode==1&&!s.EffectiveTransform.Mix;
        double peak=buckets.Count==0?0:buckets.Max(b=>both?Math.Max(Math.Max(Math.Abs(b.LeftMin),Math.Abs(b.LeftMax)),Math.Max(Math.Abs(b.RightMin),Math.Abs(b.RightMax))):right?Math.Max(Math.Abs(b.RightMin),Math.Abs(b.RightMax)):Math.Max(Math.Abs(b.LeftMin),Math.Abs(b.LeftMax)));
        double scale=s.AutoScale?(peak>0?.9/peak:1):s.Gain;
        double Y(double value)=>Plot.Y+Plot.Height/2-value*scale*Plot.Height/2;
        Text(c,$"{1/scale:0.###}",0,Plot.Y);Text(c,"0",25,Plot.Y+Plot.Height/2);Text(c,$"{-1/scale:0.###}",0,Plot.Bottom-12);
        using(c.PushClip(Plot))
        {
            foreach(var interval in s.SelectedIntervals){double left=Math.Max(Plot.X,FrameX(interval.Start)),last=Math.Min(Plot.Right,FrameX(interval.End));if(last>left)c.DrawRectangle(Brush("selected"),null,new Rect(left,Plot.Y,last-left,Plot.Height));}
            void Channel(bool r,string color)=>DrawWaveSeries(c,buckets.Select(b=>new MZTools.Capture.LiveWaveBucket(b.StartSample,b.EndSample,b.LeftMin,b.LeftMax,b.RightMin,b.RightMax)),FrameX,Y,r,color);
            Channel(right,right?"wave-r":"wave-l");if(both)Channel(true,"wave-r");
            foreach(var region in s.Model.Regions.Where(r=>r.EndSample>s.ViewStart&&r.StartSample<s.ViewEnd))
            {double left=Math.Max(Plot.X,FrameX(region.StartSample)),last=Math.Min(Plot.Right,FrameX(region.EndSample));if(last>left)c.DrawRectangle(Brush(region.Kind.StartsWith("Unassigned",StringComparison.Ordinal)?"disk-unassigned":"disk-system"),null,new Rect(left,Plot.Bottom-5,last-left,5));}
        }
        Text(c,s.EffectiveTransform.Mix?"Mix / average":both?"L + R overlay":right?"R":"L / mono",Plot.Right-110,Plot.Y+4);
    }
    private IBrush Pulse(string group)=>Brush(group switch{"SHORT High"=>"pulse-sh","SHORT Low"=>"pulse-sl","LONG High"=>"pulse-lh",_=>"pulse-ll"});
    private void Histogram(DrawingContext c,AudioSession s)
    {
        Grid(c,"Pulse duration histogram · exact sample-duration counts · µs");var bounds=s.HistogramBounds();double min=s.HistogramMin??bounds.Min,max=s.HistogramMax??bounds.Max;
        double count=Math.Max(1,s.Distributions.SelectMany(d=>d.Value.Histogram).Select(b=>b.Count).DefaultIfEmpty().Max());
        double X(double value)=>Plot.X+(value-min)/Math.Max(double.Epsilon,max-min)*Plot.Width;
        using(c.PushClip(Plot))
        {
            int series=0;foreach(var distribution in s.Distributions)
            {
                foreach(var bin in distribution.Value.Histogram.Where(b=>b.DurationMicroseconds>=min&&b.DurationMicroseconds<=max))
                {double x=X(bin.DurationMicroseconds)+series*.8,height=bin.Count/count*Plot.Height;c.DrawRectangle(Pulse(distribution.Group),null,new Rect(x,Plot.Bottom-height,Math.Max(1,Plot.Width/(max-min)*1_000_000.0/s.Model.SampleRate/4),height));}series++;
            }
            if(s.ShowReference&&s.SelectedReference is { } reference)foreach(var guide in reference.Guides.Where(g=>s.VisibleGroups.Contains(g.Group)))
            {double x=X(guide.Microseconds);c.DrawLine(new Pen(Pulse(guide.Group),1),new Point(x,Plot.Y),new Point(x,Plot.Bottom));}
        }
        Text(c,$"{count:N0}",0,Plot.Y);Text(c,"0",25,Plot.Bottom-12);Text(c,$"{min:0.###} µs",Plot.X,Plot.Bottom+6);Text(c,$"{max:0.###} µs",Plot.Right-90,Plot.Bottom+6);
    }
    private void Scatter(DrawingContext c,AudioSession s)
    {
        Grid(c,"High / Low scatter · measured pairs (bounded deterministic thinning), µs");var points=s.MeasuredBlock?.Scatter??[];
        var bounds=s.HistogramBounds();double min=s.HistogramMin??bounds.Min,max=s.HistogramMax??bounds.Max,span=Math.Max(.001,max-min);
        using(c.PushClip(Plot))foreach(var point in points){double x=Plot.X+(point.HighMicroseconds-min)/span*Plot.Width,y=Plot.Bottom-(point.LowMicroseconds-min)/span*Plot.Height;c.DrawEllipse(point.Outlier?Brush("error"):Pulse(point.Long?"LONG High":"SHORT High"),null,new Point(x,y),2,2);}
        Text(c,$"Low {max:0.###}",0,Plot.Y);Text(c,$"High/Low {min:0.###} … {max:0.###} µs",Plot.X,Plot.Bottom+6);
    }
    private void Drift(DrawingContext c,AudioSession s)
    {
        Grid(c,"Relative timing error % / local stddev % · global speed offset is separate");TimeAxis(c);var windows=s.MeasuredBlock?.Drift??[];
        double min=Math.Min(0,windows.Select(w=>w.RelativeTimingErrorPercent).DefaultIfEmpty().Min()),max=Math.Max(1,windows.Select(w=>Math.Max(w.RelativeTimingErrorPercent,w.LocalStdDevPercent)).DefaultIfEmpty().Max());double span=Math.Max(.01,max-min);
        using(c.PushClip(Plot))
        {
            Point? previous=null,spread=null;foreach(var window in windows)
            {
                double x=FrameX(window.StartSample+(window.EndSample-window.StartSample)/2);var p=new Point(x,Plot.Bottom-(window.RelativeTimingErrorPercent-min)/span*Plot.Height);var q=new Point(x,Plot.Bottom-(window.LocalStdDevPercent-min)/span*Plot.Height);
                if(previous is { } a)c.DrawLine(new Pen(Brush("wave-l"),1.5),a,p);if(spread is { } b)c.DrawLine(new Pen(Brush("wave-r"),1.5),b,q);previous=p;spread=q;
            }
        }
        Text(c,$"{max:0.##}%",0,Plot.Y);Text(c,$"{min:0.##}%",0,Plot.Bottom-12);
    }
    private void FrequencyAxis(DrawingContext c,AudioSession s)
    {Text(c,$"{s.FrequencyMin:0.###} Hz",Plot.X,Plot.Bottom+6);Text(c,$"{s.FrequencyMax:0.###} Hz",Plot.Right-100,Plot.Bottom+6);}
    private void Spectrum(DrawingContext c,AudioSession s)
    {
        Grid(c,"Periodic Hann amplitude spectrum · dBFS, not PSD · wheel/pan = frequency");FrequencyAxis(c,s);Text(c,"+20",10,Plot.Y);Text(c,"−160",0,Plot.Bottom-12);
        if(s.SpectrumData is not { } data)return;bool both=s.ChannelMode==2&&!s.EffectiveTransform.Mix&&s.Model.ChannelCount==2,right=s.ChannelMode==1&&!s.EffectiveTransform.Mix;
        using(c.PushClip(Plot))
        {
            void Channel(bool r,string color)=>DrawSpectrumSeries(c,data.Bins,s.FrequencyMin,s.FrequencyMax,r,color);
            Channel(right,right?"wave-r":"wave-l");if(both)Channel(true,"wave-r");
        }
    }
    private void Spectrogram(DrawingContext c,AudioSession s)
    {
        Grid(c,"FFT amplitude spectrogram · −160…0 dBFS · wheel=time, Shift+wheel=frequency");TimeAxis(c);
        if(s.SpectrogramData is not {Columns.Count:>0} data||rasterSource!=data||leftRaster is null||rightRaster is null)return;
        bool both=s.ChannelMode==2&&!s.EffectiveTransform.Mix&&s.Model.ChannelCount==2,right=s.ChannelMode==1&&!s.EffectiveTransform.Mix;
        double maxHz=data.Columns[0].Left.Length*data.FrequencyGroup*data.SampleRate/(double)data.FftSize;
        using(c.PushClip(Plot))
        {
            void Draw(WriteableBitmap bitmap,Rect target)
            {
                double top=(maxHz-s.FrequencyMax)/maxHz*bitmap.PixelSize.Height,height=(s.FrequencyMax-s.FrequencyMin)/maxHz*bitmap.PixelSize.Height;
                for(int i=0;i<data.Columns.Count;i++){var column=data.Columns[i];double first=FrameX(column.StartFrame),last=FrameX(column.EndFrame);if(last<Plot.X||first>Plot.Right)continue;c.DrawImage(bitmap,new Rect(i,top,1,height),new Rect(first,target.Y,Math.Max(1,last-first),target.Height));}
            }
            if(both){Draw(leftRaster,new Rect(Plot.X,Plot.Y,Plot.Width,Plot.Height/2-2));Draw(rightRaster,new Rect(Plot.X,Plot.Y+Plot.Height/2+2,Plot.Width,Plot.Height/2-2));Text(c,"L",Plot.X+4,Plot.Y+4);Text(c,"R",Plot.X+4,Plot.Y+Plot.Height/2+4);}
            else Draw(right?rightRaster:leftRaster,Plot);
        }
        Text(c,$"{s.FrequencyMax:0.##} Hz",0,Plot.Y);Text(c,$"{s.FrequencyMin:0.##}",0,Plot.Bottom-12);
    }
    public void Fit(){if(Session is not { } s)return;if(Kind is AudioPlotKind.Histogram or AudioPlotKind.Scatter)s.FitHistogram();else if(Kind==AudioPlotKind.Spectrum)s.FitFrequency();else s.Fit();}
    public void Zoom(double factor,double anchor=.5){if(Session is not { } s)return;if(Kind is AudioPlotKind.Histogram or AudioPlotKind.Scatter)s.ZoomHistogram(factor,anchor);else if(Kind==AudioPlotKind.Spectrum)s.ZoomFrequency(factor,anchor);else s.Zoom(factor,anchor);}
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {if(Session is not { } s||e.Delta.Y==0)return;double factor=e.Delta.Y>0?1.5:1/1.5,anchor=Math.Clamp((e.GetPosition(this).X-Plot.X)/Plot.Width,0,1);if(Kind==AudioPlotKind.Spectrogram&&e.KeyModifiers.HasFlag(KeyModifiers.Shift))s.ZoomFrequency(factor);else Zoom(factor,anchor);e.Handled=true;}
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if(Session is null||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus();press=e.GetPosition(this);pressStart=Session.ViewStart;pressEnd=Session.ViewEnd;pressAxis=Kind is AudioPlotKind.Histogram or AudioPlotKind.Scatter?Session.HistogramMin??Session.HistogramBounds().Min:Session.FrequencyMin;selecting=e.KeyModifiers.HasFlag(KeyModifiers.Shift)&&Kind is AudioPlotKind.Waveform or AudioPlotKind.Spectrogram;moved=false;e.Pointer.Capture(this);e.Handled=true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if(Session is not { } s)return;var at=e.GetPosition(this);
        if(press is { } first&&Math.Abs(at.X-first.X)>3)
        {
            moved=true;if(selecting){long a=AtFrame(first.X),b=AtFrame(at.X);if(a!=b)s.SetSelection([(Math.Min(a,b),Math.Max(a,b))]);}
            else if(Kind is AudioPlotKind.Histogram or AudioPlotKind.Scatter){var full=s.HistogramBounds();double width=(s.HistogramMax??full.Max)-(s.HistogramMin??full.Min);s.PanHistogram(pressAxis-(at.X-first.X)/Plot.Width*width-(s.HistogramMin??full.Min));}
            else if(Kind==AudioPlotKind.Spectrum)s.PanFrequency(pressAxis-(at.X-first.X)/Plot.Width*(s.FrequencyMax-s.FrequencyMin)-s.FrequencyMin);
            else{long count=pressEnd-pressStart,from=pressStart-(long)Math.Round((at.X-first.X)/Plot.Width*count);s.ShowRange(from,from+count);}
        }
        if(Kind==AudioPlotKind.Waveform)
        {long frame=AtFrame(at.X);var bucket=s.DisplayWaveform.VisibleBuckets(frame,Math.Min(s.Model.FrameCount,frame+1),1).FirstOrDefault();ToolTip.SetTip(this,$"Frame {frame:N0} · {frame/(double)s.Model.SampleRate:0.######} s\nBucket [{bucket.StartSample:N0}, {bucket.EndSample:N0}) · L [{bucket.LeftMin:0.######}, {bucket.LeftMax:0.######}] FS · R [{bucket.RightMin:0.######}, {bucket.RightMax:0.######}] FS\n{(bucket.EndSample-bucket.StartSample==1?"Single frame":"Min/max envelope, not an exact cursor sample")}");}
        else if(Kind==AudioPlotKind.Spectrum)ToolTip.SetTip(this,$"{s.FrequencyMin+(at.X-Plot.X)/Plot.Width*(s.FrequencyMax-s.FrequencyMin):0.###} Hz · FFT interval/N/method in spectral details");
        base.OnPointerMoved(e);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if(press is null||Session is null)return;
        if(!moved&&Kind==AudioPlotKind.Waveform)
        {
            long frame=Math.Min(Session.Model.FrameCount-1,AtFrame(e.GetPosition(this).X));
            var region=e.GetPosition(this).Y>=Plot.Bottom-8?Session.Regions.FirstOrDefault(r=>r.Start<=frame&&r.End>frame):null;
            if(region is not null)Session.SelectedRegion=region;else Session.SetSelection([(frame,frame+1)]);
        }
        press=null;e.Pointer.Capture(null);e.Handled=true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(Session is null)return;
        if(e.Key is Key.Add or Key.OemPlus){Zoom(1.5);e.Handled=true;}else if(e.Key is Key.Subtract or Key.OemMinus){Zoom(1/1.5);e.Handled=true;}else if(e.Key==Key.Home){Fit();e.Handled=true;}
        else if(e.Key is Key.Left or Key.Right){int direction=e.Key==Key.Left?-1:1;if(Kind is AudioPlotKind.Histogram or AudioPlotKind.Scatter)Session.PanHistogram(direction*((Session.HistogramMax??Session.HistogramBounds().Max)-(Session.HistogramMin??Session.HistogramBounds().Min))/4);else if(Kind==AudioPlotKind.Spectrum)Session.PanFrequency(direction*(Session.FrequencyMax-Session.FrequencyMin)/4);else Session.Pan(direction*Math.Max(1,(Session.ViewEnd-Session.ViewStart)/4));e.Handled=true;}
        base.OnKeyDown(e);
    }
}
