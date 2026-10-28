using MZTools.Capture;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed class LivePulsePanel : DockPanel
{
    internal readonly CheckBox Enabled=Check("Enable live pulse measurements"),Zero=Check("Zero crossing",true),Hysteresis=Check("Hysteresis",true),Adaptive=Check("Adaptive peak",true);
    internal readonly ComboBox Detector=Choice("Zero crossing","Hysteresis","Adaptive peak"),Channel=Choice("Left / Mono","Right");
    internal readonly AudioPulseReferenceControl Reference=new(false);
    private readonly TextBox threshold=Input("1"),deadband=Input("0"),tolerance=Input("20"),scale=Input("1"),window=Input("2");
    private readonly CheckBox invert=Check("Logical HIGH = negative (Sharp READ inversion)",true);
    private readonly TextBlock status=Text("Disabled — enable diagnostics explicitly. No decoder runs in this tab."),scope=Text(""),error=Text("");
    private readonly DataGrid statistics=Table(),timing=Table(),streams=Table(),recent=Table();
    private readonly PulseChart chart=new();
    private readonly Dictionary<string,LivePulseReference> projected=[];
    private string? selectedReferenceKey;
    private double? minimum,maximum;
    private bool referenceFit;
    private readonly Button fitAll=Button("Fit all"),fitReferences=Button("Fit references");
    private (double Minimum,double Span)? panOrigin;
    private int knownChannels;
    internal LivePulseSnapshot? Current { get; private set; }
    internal event Action? ConfigurationChanged,ViewChanged;
    internal LivePulsePanel()
    {
        var header=new StackPanel(); SetDock(header,Dock.Top); Children.Add(header);
        header.Children.Add(Text("Measured pulses → comparison with an explicit reference. Decoder bits / checksums / loader acceptance: NOT RUN. Detector streams stay separate."));
        var detectors=new WrapPanel(); foreach(var control in new[]{Enabled,Zero,Hysteresis,Adaptive}) detectors.Children.Add(control); header.Children.Add(detectors);
        var settings=new WrapPanel(); Add(settings,"Envelope gate ×",threshold); Add(settings,"Adaptive deadband ±% FS",deadband); Add(settings,"Reference scale ×",scale); Add(settings,"Tolerance %",tolerance); Add(settings,"Window s",window); settings.Children.Add(invert);
        header.Children.Add(new Expander { Header="Thresholds / polarity / time window",Content=settings });
        header.Children.Add(new ScrollViewer { Content=Reference,MaxHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto }); header.Children.Add(error);
        header.Children.Add(new Expander { Header="Signal preprocessing / interpretation",Content=Text("Shared offline preprocessing: 0.75 s DC tracker, 20 Hz high-pass; envelope attack 5 ms / release 250 ms. Base gate max(0.0025 FS, envelope ×0.22). Zero crossing uses zero, hysteresis uses the envelope gate × scale, adaptive measures opposite-peak midpoints and applies the extra deadband. Filters affect diagnostics only; archive PCM stays unchanged. Initial/final partial lobes are omitted; adaptive durations use an interpolated 1/8-sample grid, with rounded frame markers. Reference matches are not decoder-accepted bits.") });
        var view=new WrapPanel(); Add(view,"Measured detector",Detector); Add(view,"Channel",Channel);
        var fit=fitAll; var refFit=fitReferences; var plus=Button("Zoom +"); var minus=Button("Zoom −"); foreach(var button in new[]{fit,refFit,plus,minus}) view.Children.Add(button); header.Children.Add(view);
        header.Children.Add(status); header.Children.Add(scope);
        var tabs=new TabControl(); Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header="Histogram",Content=chart });
        foreach(var (name,table) in new[]{("Statistics",statistics),("Timing windows",timing),("Detector streams",streams),("Recent measured pulses",recent)}) tabs.Items.Add(new TabItem { Header=name,Content=table });
        AddColumn(statistics,"Reference comparison group","Group",165); AddColumn(statistics,"Count","Count",90,"N0"); AddColumn(statistics,"Mean µs","MeanMicroseconds",120,"F3"); AddColumn(statistics,"SD µs","StandardDeviationMicroseconds",120,"F3"); AddColumn(statistics,"Measured CV %","CvPercent",125,"F3");
        AddColumn(timing,"Start frame","StartFrame",140,"N0"); AddColumn(timing,"End frame","EndFrame",140,"N0"); AddColumn(timing,"Reference matches","Matches",140,"N0"); AddColumn(timing,"Mean timing scale","MeanTimingScale",150,"F5"); AddColumn(timing,"Reference residual RMS %","ResidualRmsPercent",200,"F3");
        AddColumn(streams,"Detector","Detector",140); AddColumn(streams,"Channel","Channel",80); AddColumn(streams,"Detected since reset","DetectedSinceReset",165,"N0"); AddColumn(streams,"Retained","Retained",90,"N0"); AddColumn(streams,"Capacity evictions","CapacityEvictions",150,"N0"); AddColumn(streams,"Current gate FS","CurrentGateFs",135,"F5");
        AddColumn(streams,"Adaptive overlength resets","AdaptiveHistoryResets",190,"N0");
        AddColumn(recent,"End frame (rounded for adaptive)","EndFrame",215,"N0"); AddColumn(recent,"Measured µs","Microseconds",125,"F3"); AddColumn(recent,"Physical positive","PhysicalHigh",125); AddColumn(recent,"Comparison group","Group",150); AddColumn(recent,"Reference µs (unscaled)","ReferenceMicroseconds",170,"F3"); AddColumn(recent,"Residual %","ResidualPercent",120,"F3"); AddColumn(recent,"Reference match","ReferenceMatch",130);
        foreach(var check in new[]{Enabled,Zero,Hysteresis,Adaptive,invert}) check.Click+=(_,_)=>Changed();
        foreach(var field in new[]{threshold,deadband,tolerance,scale,window}) field.TextChanged+=(_,_)=>{ Changed(); if(field==scale && referenceFit) FitReference(); };
        selectedReferenceKey=Reference.Selected is {} initial?ReferenceKey(initial):null;
        Reference.Changed+=()=>
        {
            string? next=Reference.Selected is {} selected?ReferenceKey(selected):null;
            chart.ReferenceLabel=Reference.Selected?.Name;
            if(next!=selectedReferenceKey) { selectedReferenceKey=next; Changed(); }
            FitReference();
        };
        Reference.VisibilityChanged+=()=>{ chart.ShowReferenceGuides=Reference.ShowGrid; chart.InvalidateVisual(); };
        Detector.SelectionChanged+=(_,_)=>ViewChanged?.Invoke(); Channel.SelectionChanged+=(_,_)=>ViewChanged?.Invoke();
        fit.Click+=(_,_)=>{ referenceFit=false; minimum=maximum=null; PublishView(); };
        refFit.Click+=(_,_)=>FitReference();
        plus.Click+=(_,_)=>ZoomData(.5); minus.Click+=(_,_)=>ZoomData(2); chart.ZoomRequested+=Zoom;
        chart.PanStarted+=BeginPan;
        chart.PanRequested+=Pan;
        PublishView();
    }
    private void PublishView()
    { chart.ViewMinimum=minimum; chart.ViewMaximum=maximum; chart.InvalidateVisual(); AudioSignalChart.MarkFit(fitAll,minimum==null&&!referenceFit); AudioSignalChart.MarkFit(fitReferences,referenceFit); ViewChanged?.Invoke(); }
    private void FitReference()
    {
        if(Reference.Selected is not {} reference || !double.TryParse(scale.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double factor) || !double.IsFinite(factor) || factor is < .1 or > 10) return;
        var values=reference.Guides.Select(g=>g.Microseconds*factor).ToArray();
        if(values.Length==0) return;
        double pad=Math.Max(1,(values.Max()-values.Min())*.2);
        referenceFit=true; minimum=Math.Max(0,values.Min()-pad); maximum=values.Max()+pad; panOrigin=null; PublishView();
    }
    private void BeginPan()
    {
        panOrigin=Current is {} snapshot
            ? (minimum??snapshot.HistogramMinimumMicroseconds,(maximum??snapshot.HistogramMaximumMicroseconds)-(minimum??snapshot.HistogramMinimumMicroseconds)) : null;
    }
    private void Pan(double fraction)
    {
        if(panOrigin is not {} origin) return;
        referenceFit=false;
        minimum=Math.Clamp(origin.Minimum+fraction*origin.Span,0,1e9-origin.Span);
        maximum=minimum+origin.Span; PublishView();
    }
    private void Changed() { Clear(); ConfigurationChanged?.Invoke(); }
    private static string ReferenceKey(AudioPulseReference reference)=>(reference.Kind==PulseReferenceKind.User?"USER:"+reference.Name+":":"")+AudioPulseReferences.MeasurementKey(reference);
    internal LivePulseOptions? Options()
    {
        error.Text=""; if(Enabled.IsChecked!=true) return null;
        double Read(TextBox field) { if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double value)||!double.IsFinite(value)) throw new ArgumentException("Enter finite numeric pulse settings."); return value; }
        try
        {
            double gate=Read(threshold),dead=Read(deadband)/100,tol=Read(tolerance)/100,factor=Read(scale),seconds=Read(window);
            if(Zero.IsChecked!=true&&Hysteresis.IsChecked!=true&&Adaptive.IsChecked!=true) throw new ArgumentException("Select at least one detector.");
            if(gate is < .1 or > 4||dead is < 0 or > 1||tol is < 0 or > 1||factor is < .1 or > 10||seconds is < .05 or > 10) throw new ArgumentException("Gate ×0.1–4; deadband/tolerance 0–100%; reference scale ×0.1–10; window 0.05–10 s.");
            if(Reference.Selected is not {} reference) throw new ArgumentException("Select a four-group duration reference.");
            string key=ReferenceKey(reference);
            if(!projected.TryGetValue(key,out var projectedReference))
            {
                var canonical=AudioPulseReferences.BuiltIn.FirstOrDefault(r=>reference.CatalogId!=null&&r.CatalogId==reference.CatalogId&&AudioPulseReferences.MeasurementKey(r)==AudioPulseReferences.MeasurementKey(reference))??reference;
                projected.Add(key,projectedReference=LivePulseCatalog.From(canonical));
            }
            return new() { Reference=projectedReference,ZeroCrossing=Zero.IsChecked==true,Hysteresis=Hysteresis.IsChecked==true,Adaptive=Adaptive.IsChecked==true,ThresholdScale=gate,AdaptiveDeadbandFs=dead,ReferenceScale=factor,Tolerance=tol,WindowSeconds=seconds,InvertPolarity=invert.IsChecked==true };
        }
        catch(ArgumentException ex) { error.Text=ex.Message; return null; }
    }
    internal LivePulseViewOptions View(AudioCaptureFormat? format)
    {
        int channels=format?.Channels??1;
        if(knownChannels!=channels) { knownChannels=channels; Channel.ItemsSource=channels==1?new[]{"Mono"}:new[]{"Left","Right"}; Channel.SelectedIndex=0; }
        return new() { Detector=(LivePulseDetector)Math.Max(0,Detector.SelectedIndex),Channel=Math.Clamp(Channel.SelectedIndex,0,channels-1),MinimumMicroseconds=minimum,MaximumMicroseconds=maximum,Bins=Math.Clamp((int)Math.Max(8,chart.ActualWidth/4),8,1024) };
    }
    internal void Clear()
    { Current=null; chart.Snapshot=null; chart.InvalidateVisual(); statistics.ItemsSource=timing.ItemsSource=streams.ItemsSource=recent.ItemsSource=null; scope.Text=""; status.Text="Waiting for current pulse data (or disabled). Settings changes start a new diagnostic epoch."; }
    internal void Update(LivePulseSnapshot? snapshot)
    {
        if(snapshot==null) { Clear(); return; }
        Current=snapshot; chart.Snapshot=snapshot; chart.ReferenceLabel=Reference.Selected?.Name; chart.ShowReferenceGuides=Reference.ShowGrid; chart.InvalidateVisual();
        statistics.ItemsSource=snapshot.Distributions; timing.ItemsSource=snapshot.Timing; streams.ItemsSource=snapshot.Streams; recent.ItemsSource=snapshot.RecentPulses;
        status.Text=$"{snapshot.Detector} / {(snapshot.Format.Channels==1?"Mono":snapshot.Channel==0?"Left":"Right")} • measured {snapshot.MeasuredPulses:N0} • reference matches {snapshot.ReferenceMatches:N0} • unclassified {snapshot.Unclassified:N0} • visible {snapshot.VisiblePulses:N0} • capacity evictions {snapshot.CapacityEvictions:N0}";
        string Value(double? number,string format)=>number?.ToString(format,CultureInfo.CurrentCulture)??"n/a";
        scope.Text=$"[{snapshot.StartFrame:N0}, {snapshot.EndFrame:N0}) • {snapshot.Format.SampleRate:N0} Hz • resets {snapshot.ContinuityResets:N0}: {snapshot.ResetReason}\nReference {Reference.Selected?.Name??snapshot.Options.Reference.Name} ×{snapshot.Options.ReferenceScale:G} • mean ratio {Value(snapshot.MeanTimingScale,"F4")} • residual dispersion {Value(snapshot.ResidualJitterPercent,"F2")}% • half-window drift {Value(snapshot.DriftPercent,"+0.00;-0.00;0")}% • trace rows omitted {snapshot.TraceRowsOmitted:N0}";
    }
    private void Zoom(double factor,double anchor)
    {
        if(Current==null) return;
        referenceFit=false;
        double start=minimum??Current.HistogramMinimumMicroseconds,end=maximum??Current.HistogramMaximumMicroseconds;
        double span=Math.Clamp((end-start)*factor,.01,1e9);
        double point=start+(end-start)*anchor;
        minimum=Math.Clamp(point-span*anchor,0,1e9-span); maximum=minimum+span; PublishView();
    }
    private void ZoomData(double factor)
    {
        if(Current==null) return;
        var peak=Current.Histogram.MaxBy(bin=>bin.Counts.Sum());
        double anchor=peak!=null&&peak.Counts.Sum()>0?((peak.MinimumMicroseconds+peak.MaximumMicroseconds)*.5-Current.HistogramMinimumMicroseconds)/(Current.HistogramMaximumMicroseconds-Current.HistogramMinimumMicroseconds):.5;
        Zoom(factor,anchor);
    }
    private static CheckBox Check(string title,bool value=false)=>new() { Content=title,IsChecked=value,Margin=new(5) };
    private static TextBox Input(string text)=>new() { Text=text,Width=65,Margin=new(4) };
    private static ComboBox Choice(params string[] values)=>new() { ItemsSource=values,SelectedIndex=0,Margin=new(4),MinWidth=90 };
    private static Button Button(string text)=>new() { Content=text,Margin=new(4),Padding=new(8,3,8,3) };
    private static TextBlock Text(string text)=>new() { Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(4) };
    private static void Add(Panel panel,string title,FrameworkElement control) { panel.Children.Add(Text(title)); panel.Children.Add(control); }
    private static DataGrid Table()
    {
        var table=new DataGrid { IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserResizeColumns=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,Margin=new(4) };
        var style=new Style(typeof(DataGridCell)); var selected=new Trigger { Property=DataGridCell.IsSelectedProperty,Value=true }; selected.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.RoyalBlue)); selected.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White)); style.Triggers.Add(selected); table.CellStyle=style; return table;
    }
    private static void AddColumn(DataGrid table,string title,string property,int width,string? format=null)=>table.Columns.Add(new DataGridTextColumn { Header=title,Width=width,Binding=new Binding(property) { StringFormat=format,TargetNullValue="—" } });
    private sealed class PulseChart:FrameworkElement
    {
        internal LivePulseSnapshot? Snapshot;
        internal double? ViewMinimum,ViewMaximum;
        internal string? ReferenceLabel;
        private bool showGuides=true;
        internal bool ShowReferenceGuides { get=>showGuides; set { showGuides=value; if(!value) { referenceHits.Clear(); hoverTip.IsOpen=false; ToolTipService.SetIsEnabled(this,false); } } }
        private readonly List<(double X,string Text)> referenceHits=[];
        private readonly ToolTip hoverTip=new() { Placement=System.Windows.Controls.Primitives.PlacementMode.Mouse,HorizontalOffset=16,VerticalOffset=20,MaxWidth=600 };
        internal string? ReferenceDetailsAt(double x)=>!showGuides?null:string.Join("\n\n",referenceHits.Where(h=>Math.Abs(h.X-x)<7).Select(h=>h.Text).Distinct());
        internal int RenderedReferenceGuides=>referenceHits.Count;
        internal event Action<double,double>? ZoomRequested;
        internal event Action<double>? PanRequested;
        internal event Action? PanStarted;
        private Point? drag;
        private double dragWidth;
        private static readonly Brush[] colors=[Brushes.Blue,Brushes.Red,Brushes.Green,Brushes.DarkOrange,Brushes.Gray,Brushes.DarkGray];
        internal PulseChart()
        {
            ClipToBounds=true;
            ToolTip=hoverTip; ToolTipService.SetIsEnabled(this,false);
            MouseLeave+=(_,_)=>{ hoverTip.IsOpen=false; ToolTipService.SetIsEnabled(this,false); };
            MouseMove+=(_,e)=>
            {
                string? text=IsMouseCaptured?null:ReferenceDetailsAt(e.GetPosition(this).X);
                hoverTip.Content=new TextBlock { Text=text??"",TextWrapping=TextWrapping.Wrap }; ToolTipService.SetIsEnabled(this,!string.IsNullOrEmpty(text));
                if(string.IsNullOrEmpty(text)) hoverTip.IsOpen=false;
            };
            MouseWheel+=(_,e)=>{ ZoomRequested?.Invoke(e.Delta>0?.5:2,Math.Clamp((e.GetPosition(this).X-70)/Math.Max(1,ActualWidth-95),0,1)); e.Handled=true; };
            MouseLeftButtonDown+=(_,e)=>{ drag=e.GetPosition(this); dragWidth=Math.Max(1,ActualWidth-95); PanStarted?.Invoke(); CaptureMouse(); };
            MouseLeftButtonUp+=(_,_)=>{ drag=null; ReleaseMouseCapture(); }; LostMouseCapture+=(_,_)=>drag=null;
            MouseMove+=(_,e)=>{ if(IsMouseCaptured && drag is Point origin) PanRequested?.Invoke((origin.X-e.GetPosition(this).X)/dragWidth); };
        }
        protected override void OnRender(DrawingContext dc)
        {
            referenceHits.Clear();
            dc.DrawRectangle(Brushes.White,null,new Rect(RenderSize)); var data=Snapshot;
            void Text(string text,double x,double y,Brush? brush=null)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,brush??Brushes.DimGray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
            if(data==null) { Text("Enable diagnostics and explicitly start Monitor / Record. No decoder acceptance is inferred.",12,20); return; }
            double left=70,top=42,width=Math.Max(1,ActualWidth-95),height=Math.Max(1,ActualHeight-90);
            double viewMin=ViewMinimum??data.HistogramMinimumMicroseconds,viewMax=ViewMaximum??data.HistogramMaximumMicroseconds;
            double X(double us)=>left+width*(us-viewMin)/(viewMax-viewMin);
            string[] names=["SHORT High","SHORT Low","LONG High","LONG Low","unclassified High","unclassified Low"]; double legendX=left;
            for(int g=0;g<6;g++) { Text(names[g],legendX,4,colors[g]); legendX+=g<4?105:140; }
            Text($"{viewMin:F2} … {viewMax:F2} µs • {data.VisiblePulses:N0}/{data.MeasuredPulses:N0} visible • wheel: zoom / drag: pan • counts per bin",left,23);
            int peak=Math.Max(1,data.Histogram.Max(b=>b.Counts.Sum()));
            int lastLabel=-1;
            for(int i=0;i<=4;i++) { double y=top+height*i/4; dc.DrawLine(new Pen(Brushes.Gainsboro,1),new(left,y),new(left+width,y)); int label=(int)Math.Round(peak*(1-i/4.0)); if(label!=lastLabel) Text(label.ToString("N0"),2,y-6); lastLabel=label; }
            dc.PushClip(new RectangleGeometry(new Rect(left,top,width,height)));
            for(int i=0;i<data.Histogram.Count;i++)
            {
                var bin=data.Histogram[i];
                if(bin.MaximumMicroseconds<viewMin || bin.MinimumMicroseconds>viewMax) continue;
                double x=X(bin.MinimumMicroseconds),y=top+height;
                for(int g=0;g<6;g++) { double h=height*bin.Counts[g]/peak; if(h>0) { y-=h; dc.DrawRectangle(colors[g],null,new Rect(x,y,Math.Max(1,X(bin.MaximumMicroseconds)-x-1),h)); } }
            }
            dc.Pop();
            foreach(var guide in showGuides?data.Options.Reference.Guides:Array.Empty<LivePulseGuide>())
            {
                double x=X(guide.Microseconds*data.Options.ReferenceScale); if(x<left||x>left+width) continue;
                int group=(int)guide.Group;
                dc.PushOpacity(.35); dc.DrawLine(new Pen(colors[group],.8) { DashStyle=DashStyles.Dot },new(x,top),new(x,top+height)); dc.Pop();
                dc.DrawRectangle(colors[group],null,new Rect(x-2,top+height+2+group*3,4,3));
                string contextNote=data.Options.Reference.Guides.Count(g=>g.Group==guide.Group)>1?"\nROM LOW has separate leader, tape-mark and neighboring-data contexts.":"";
                referenceHits.Add((x,$"{ReferenceLabel??data.Options.Reference.Name}\n{names[group]}: {guide.Microseconds*data.Options.ReferenceScale:F3} µs • {guide.Context}{contextNote}\n{data.Options.Reference.Source}"));
            }
            for(int i=0;i<=4;i++) { double us=viewMin+(viewMax-viewMin)*i/4; Text($"{us:N2} µs",Math.Clamp(X(us)-20,0,Math.Max(0,ActualWidth-85)),top+height+17); }
        }
    }
}
