using Avalonia;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

public sealed partial class AudioChart
{
    private void RenderWpfWaveform(DrawingContext dc, AudioSession s)
    {
        dc.DrawRectangle(Brushes.White,new Pen(Brushes.LightGray),new Rect(Bounds.Size));
        string channels=s.EffectiveTransform.Mix?"Mix / average":s.ChannelMode==2?"blue Left • orange Right":s.ChannelMode==1?"orange Right":"blue Left";
        Text(dc,$"PCM • {channels} • {(s.AutoScale?"Auto scale":$"FS ×{s.Gain:G3}")} • gray: unassigned • wheel: zoom • drag: pan • Shift+drag: select audio",Plot.X,3);
        if(ShowIssueMarkers)Text(dc,"Red: measured anomalies • orange: failed / unresolved block, exact damaged byte unknown",Plot.X,18,size:10);
        if(s.IsDeepAnalysis&&s.HasTransformDraft || s.DisplayedTransform!=s.EffectiveTransform){Text(dc,"Preparing requested PCM transform…",Plot.X,Plot.Y+20);return;}
        var visible=s.DisplayWaveform.VisibleBuckets(s.ViewStart,s.ViewEnd,Math.Max(1,(int)Math.Ceiling(Plot.Width)));
        double mid=Plot.Y+18+(Plot.Height-18)/2, amplitude=Math.Max(1,(Plot.Height-22)/2);
        using(dc.PushClip(new Rect(Plot.X,Plot.Y-18,Plot.Width,Plot.Height+18)))
        {
            foreach(var region in s.DisplayRegions.Where(r=>r.EndSample>s.ViewStart && r.StartSample<s.ViewEnd).OrderBy(r=>r.Kind=="Unassigned audio"?0:r.Kind.StartsWith("Gap")?1:r.Kind is "Unresolved" or "Recovered"?3:2))
            {
                IBrush color=region.Kind=="Unassigned audio" || region.Kind.StartsWith("Gap")?Brushes.LightGray:region.Kind=="Unresolved"?Brushes.IndianRed:region.Kind=="Recovered"?Brushes.Goldenrod:region.Kind.Contains("checksum")?Brushes.MediumPurple:region.Kind.Contains("sync")?Brushes.DarkOrange:Brushes.SteelBlue;
                double x=Math.Max(Plot.X,FrameX(region.StartSample)),right=Math.Min(Plot.Right,FrameX(region.EndSample));
                if(right>x)dc.DrawRectangle(color,null,new Rect(x,Plot.Y,right-x,13));
            }
            if(ShowIssueMarkers)
            foreach(var group in s.PreviewIssues.Where(i=>i.End>s.ViewStart&&i.Start<s.ViewEnd).GroupBy(i=>Math.Clamp((int)FrameX(Math.Max(s.ViewStart,i.Start)),(int)Plot.X,(int)Plot.Right)))
            {
                double x=group.Key,right=Math.Min(Plot.Right,group.Max(i=>FrameX(i.End)));
                dc.DrawRectangle(group.Any(i=>i.Code is "AUDIO_CHECKSUM_FAILURE" or "AUDIO_UNRESOLVED_REGION")?Brushes.DarkOrange:Brushes.Crimson,null,new Rect(x,Plot.Y+15,Math.Max(2,right-x),6));
            }
            double labelEnd=Plot.X;
            foreach(var region in s.DisplayRegions.Where(r=>r.Record.HasValue && r.EndSample>s.ViewStart && r.StartSample<s.ViewEnd && r.Kind!="Recovered").GroupBy(r=>r.Record).Select(g=>g.MinBy(r=>r.StartSample)!).OrderBy(r=>r.StartSample))
            {
                double x=Math.Max(Plot.X,FrameX(region.StartSample))+2;
                if(x<labelEnd || x>=Plot.Right)continue;
                string name=s.Model.Blocks.FirstOrDefault(b=>b.Record==region.Record)?.Name??region.Kind;
                var label=new FormattedText($"{region.Record}: {name}",System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,11,Brushes.SteelBlue){MaxTextWidth=Math.Max(1,Plot.Right-x),MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis};
                dc.DrawText(label,new Point(x,Plot.Y-17));labelEnd=x+label.Width+3;
            }
            dc.DrawLine(new Pen(Brushes.LightGray),new Point(Plot.X,mid),new Point(Plot.Right,mid));
            void Channel(bool right,IBrush color)
            {
                if(visible.Count==0)return;
                // The overview may cover thousands of source frames per bucket.
                // Never expand it into solid rectangles when individual frames fit on screen.
                if(s.ViewEnd-s.ViewStart<=Plot.Width && visible.Any(b=>b.EndSample-b.StartSample>1))
                {Text(dc,"Loading PCM sample detail…",Plot.X+8,Plot.Y+28);return;}
                bool samples=visible.All(b=>b.EndSample-b.StartSample==1);
                double peak=s.AutoScale?Math.Max(.00001,visible.Max(b=>right?Math.Max(Math.Abs(b.RightMin),Math.Abs(b.RightMax)):Math.Max(Math.Abs(b.LeftMin),Math.Abs(b.LeftMax)))):1;
                double Y(WaveformBucket b,bool upper)=>mid-Math.Clamp((right?upper?b.RightMax:b.RightMin:upper?b.LeftMax:b.LeftMin)*(s.AutoScale?1:s.Gain)/peak,-1,1)*amplitude;
                double X(long frame)=>Math.Clamp(FrameX(frame),Plot.X,Plot.Right);
                var geometry=new StreamGeometry();
                using(var context=geometry.Open())
                {
                    context.BeginFigure(new Point(X(visible[0].StartSample),Y(visible[0],true)),!samples);
                    foreach(var bucket in visible){context.LineTo(new Point(X(bucket.StartSample),Y(bucket,true)));if(!samples)context.LineTo(new Point(X(bucket.EndSample),Y(bucket,true)));}
                    if(!samples)for(int i=visible.Count-1;i>=0;i--){context.LineTo(new Point(X(visible[i].EndSample),Y(visible[i],false)));context.LineTo(new Point(X(visible[i].StartSample),Y(visible[i],false)));}
                    context.EndFigure(!samples);
                }
                dc.DrawGeometry(samples?null:color,samples?new Pen(color):null,geometry);
            }
            if(s.ChannelMode!=1 || s.EffectiveTransform.Mix)Channel(false,Brushes.SteelBlue);
            if(s.ChannelMode!=0 && s.Model.ChannelCount>1 && !s.EffectiveTransform.Mix)Channel(true,Brushes.DarkOrange);
            foreach(var interval in s.HasVisualSelection?s.SelectedIntervals:[])
            {
                double x=Math.Max(Plot.X,FrameX(interval.Start)),right=Math.Min(Plot.Right,FrameX(interval.End));
                if(right>x)dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35,20,80,190)),new Pen(Brushes.RoyalBlue,1.5),new Rect(x,Plot.Y,right-x,Plot.Height));
            }
        }
        dc.DrawLine(new Pen(Brushes.Gray),Plot.TopLeft,Plot.BottomLeft);dc.DrawLine(new Pen(Brushes.Gray),Plot.BottomLeft,Plot.BottomRight);
        string level=s.AutoScale?"1 rel":$"{1/s.Gain:G3} FS";
        Text(dc,"+"+level,4,Plot.Y+20);Text(dc,"0",38,mid-7);Text(dc,"−"+level,4,Plot.Bottom-15);TimeAxis(dc);
    }
}
