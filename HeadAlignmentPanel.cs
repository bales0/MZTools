using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MZTools.Capture;

namespace MZTools;

internal sealed class HeadAlignmentPanel : DockPanel
{
    internal readonly HeadAlignmentComparisonModel Comparison=new();
    internal readonly TextBox Passage=new() { Width=190 },Tone=new() { Text="1000",Width=65 };
    internal readonly CheckBox Confirm=new() { Content="Same passage replayed; unchanged gain / EQ / input chain",Margin=new(5) },KnownTone=new() { Content="Known stereo tone (Hz)",Margin=new(5) };
    internal readonly Button SaveA=Button("Save A"),SaveB=Button("Save B");
    internal readonly ComboBox WindowSize=new() { ItemsSource=new[]{"1024","4096","16384","32768"},SelectedIndex=1,Margin=new(3) };
    private readonly TextBox bandLow=new() { Text="300",Width=60 },bandSplit=new() { Text="3000",Width=60 },bandHigh=new() { Text="12000",Width=65 };
    private readonly TextBlock scope=Text("Start Monitor or Record to measure."),status=Text(""),comparisonStatus=Text("Not assessable: save A and B."),saved=Text(""),validation=Text("");
    private readonly DataGrid channels=Table(),stereo=Table();
    private readonly ComparisonBars bars=new();
    internal readonly HeadEdgeAdjustmentControl Adjustment=new();
    internal readonly HeadStereoIndicator StereoIndicator=new();
    private HeadAlignmentMeasurement? current;
    private AudioCaptureStreamInfo? stream;
    private HeadAlignmentOptions? displayedOptions;
    internal event Action? Changed;
    internal HeadAlignmentPanel()
    {
        var header=new StackPanel(); SetDock(header,Dock.Top); Children.Add(header);
        var warning=Text(HeadAlignmentMeasurement.Limitation); warning.FontWeight=FontWeights.SemiBold; warning.Foreground=Brushes.DarkSlateBlue; header.Children.Add(warning);
        var options=new WrapPanel(); Add(options,"Band low / split / high (Hz)",bandLow); options.Children.Add(bandSplit); options.Children.Add(bandHigh); Add(options,"Window N",WindowSize); options.Children.Add(KnownTone); options.Children.Add(Tone); header.Children.Add(options); header.Children.Add(validation); header.Children.Add(new Expander { Header="Measurement interval / method",Content=scope });
        validation.Visibility=Visibility.Collapsed;
        var tabs=new TabControl(); Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header="Match this tape",Content=Adjustment });
        var indicators=new Grid(); indicators.RowDefinitions.Add(new() { Height=new(135),MinHeight=110 }); indicators.RowDefinitions.Add(new() { Height=new(5) }); indicators.RowDefinitions.Add(new() { Height=new(1,GridUnitType.Star),MinHeight=100 });
        var mono=new DockPanel(); var explanation=Text("Individual indicators — AC RMS, band-energy fraction, slope and median P5–P95 10–90% transitions. Gain/EQ and changing data content affect every comparison."); SetDock(explanation,Dock.Top); mono.Children.Add(explanation); mono.Children.Add(channels); indicators.Children.Add(mono);
        var splitter=new GridSplitter { Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,Background=Brushes.LightGray }; Grid.SetRow(splitter,1); indicators.Children.Add(splitter);
        var stereoPanel=new DockPanel(); Grid.SetRow(stereoPanel,2); SetDock(status,Dock.Top); stereoPanel.Children.Add(status); stereoPanel.Children.Add(stereo); indicators.Children.Add(stereoPanel);
        tabs.Items.Add(new TabItem { Header="Indicators",Content=indicators });
        var comparison=new DockPanel(); var actions=new StackPanel(); SetDock(actions,Dock.Top); comparison.Children.Add(actions);
        var passage=new WrapPanel(); Add(passage,"Reference passage ID",Passage); passage.Children.Add(Confirm); actions.Children.Add(passage);
        var buttons=new WrapPanel(); buttons.Children.Add(SaveA); buttons.Children.Add(SaveB); var clear=Button("Clear A/B"); buttons.Children.Add(clear); actions.Children.Add(buttons); actions.Children.Add(saved); actions.Children.Add(comparisonStatus);
        var key=Text("A = blue • B = orange • HF: 0–100%; FS/coherence: 0–1; other rows scaled separately (signed values centered at zero). Δ = B−A. Higher HF/slope or shorter transitions may indicate more bandwidth only for the same passage; no combined score."); SetDock(key,Dock.Bottom); comparison.Children.Add(key); comparison.Children.Add(new ScrollViewer { Content=bars,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto });
        tabs.Items.Add(new TabItem { Header="Compare A/B",Content=comparison });
        tabs.Items.Add(new TabItem { Header="Stereo L/R",Content=StereoIndicator });
        AddColumn(channels,"Channel","Channel",75); AddColumn(channels,"AC RMS FS","AcRms",95,"F4"); AddColumn(channels,"DC FS","Dc",85,"F4"); AddColumn(channels,"High band %","HighBandPercent",100,"F2"); AddColumn(channels,"Slope 1/ms","NormalizedSlopePerMs",100,"F3"); AddColumn(channels,"Rise µs","RiseMicroseconds",85,"F2"); AddColumn(channels,"Fall µs","FallMicroseconds",85,"F2"); AddColumn(channels,"Rise count","RiseCount",85,"N0"); AddColumn(channels,"Fall count","FallCount",85,"N0"); AddColumn(channels,"Clipped","ClippedSamples",80,"N0"); AddColumn(channels,"Quality","Quality",500);
        AddColumn(stereo,"Metric","Metric",220); AddColumn(stereo,"Value","Value",155); AddColumn(stereo,"Meaning","Meaning",650);
        foreach(var field in new[]{bandLow,bandSplit,bandHigh,Tone}) field.TextChanged+=(_,_)=>SettingsChanged();
        KnownTone.Click+=(_,_)=>SettingsChanged();
        WindowSize.SelectionChanged+=(_,_)=>SettingsChanged();
        SaveA.Click+=(_,_)=>Save(true); SaveB.Click+=(_,_)=>Save(false); clear.Click+=(_,_)=>{ Comparison.Clear(); UpdateComparison(); };
        UpdateSaveButtons();
    }
    internal HeadAlignmentOptions? Options(int rate)
    {
        validation.Visibility=Visibility.Visible;
        bool Read(TextBox field,out double value)=>double.TryParse(field.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out value)&&double.IsFinite(value);
        if(!Read(bandLow,out double low)||!Read(bandSplit,out double split)||!Read(bandHigh,out double high)||KnownTone.IsChecked==true&&!Read(Tone,out _)) { validation.Text="Enter finite numeric band/tone values. Measurements are unavailable until corrected."; return null; }
        double? tone=KnownTone.IsChecked==true?double.Parse(Tone.Text,CultureInfo.CurrentCulture):null;
        var result=new HeadAlignmentOptions { BandLowHz=low,BandSplitHz=split,BandHighHz=high,KnownToneHz=tone };
        // Mirror public option constraints here so invalid UI input never interrupts capture or preview.
        if(low<=0||split<=low||Math.Min(high,rate*.5)<=split||high<=split||tone.HasValue&&(tone<=0||tone>=rate*.5)) { validation.Text="Require 0 < low < split < high (limited to Nyquist), and 0 < tone < Nyquist."; return null; }
        validation.Text=""; validation.Visibility=Visibility.Collapsed; return result;
    }
    internal void SetCaptureActive(bool active) { Adjustment.SetActive(active); if(!active) StereoIndicator.Update(null,false); }
    internal void Update(HeadAlignmentMeasurement? measurement,AudioCaptureStreamInfo? source,long discontinuities=0,bool active=true)
    {
        Adjustment.Update(measurement,source,discontinuities,active);
        StereoIndicator.Update(measurement,active);
        current=measurement; stream=source; displayedOptions=measurement?.Options;
        if(measurement==null) { channels.ItemsSource=null; stereo.ItemsSource=null; status.Text="Not assessable: no current alignment measurement."; scope.Text="Open this tab while monitoring/recording; a full FFT window is required."; UpdateSaveButtons(); return; }
        channels.ItemsSource=measurement.Channels;
        scope.Text=$"Delivered {measurement.Format.SampleRate:N0} Hz / {measurement.Format.BitsPerSample} bit / {measurement.Format.Channels} ch • samples [{measurement.StartFrame:N0}, {measurement.EndFrame:N0}) • {(measurement.EndFrame-measurement.StartFrame)/(double)measurement.Format.SampleRate:F4} s • upper band ≤ {Math.Min(measurement.Options.BandHighHz,measurement.Format.SampleRate*.5):N0} Hz";
        var s=measurement.Stereo; status.Text=s.Status;
        string Value(double? value,string format="F3")=>value?.ToString(format,CultureInfo.CurrentCulture)??"Not assessable";
        stereo.ItemsSource=new[] {
            new StereoRow("R−L signal phase",s.SignalPhaseDegrees.HasValue?$"{s.SignalPhaseDegrees:F2}°":"Not assessable","Known tone only; modulo one cycle. This is not mechanical head angle."),
            new StereoRow("Right delay / µs",s.DelaySamples.HasValue?$"{s.DelaySamples:F3} samples / {s.DelayMicroseconds:F2} µs":"Not assessable","Positive = Right lags Left. Suppressed for ambiguous/unstable/weak peaks."),
            new StereoRow("Magnitude-squared coherence",Value(s.Coherence),KnownTone.IsChecked==true?"Four fitted known-tone phasors; 0–1, not a confidence probability.":"Four non-overlapping Hann segments; energy-weighted occupied-band coherence, 0–1."),
            new StereoRow("R/L AC level difference",Value(s.LevelDifferenceDb,"F2")+" dB","20 log10(R AC RMS / L AC RMS); delivered samples, not physical input voltage."),
            new StereoRow("L/R zero-lag correlation",Value(s.ZeroLagCorrelation),"DC-removed correlation at zero delay; polarity may make it negative."),
            new StereoRow("Best segment correlation",Value(s.BestCorrelation),"Mean of four normalized segment correlations at the best signed lag."),
            new StereoRow("Tone purity / phase stability",s.TonePurity.HasValue?$"{s.TonePurity:F4} / {s.PhaseStability:F4}":"Not assessable","Minimum fitted AC energy fraction / circular phase concentration across four windows."),
            new StereoRow("Lag spread / unique peak",s.LagSpreadSamples.HasValue?$"{s.LagSpreadSamples:F1} samples / {s.DelayPeakUnique}":"Not assessable","Spread of four segment peak lags; maximum search ±48 samples.") };
        UpdateSaveButtons();
    }
    private void SettingsChanged() { current=null; displayedOptions=null; Adjustment.Reset("Analysis settings changed — set a new zero."); UpdateSaveButtons(); status.Text="Settings changed — waiting for a new measurement."; Changed?.Invoke(); }
    private void UpdateSaveButtons() => SaveA.IsEnabled=SaveB.IsEnabled=current!=null&&stream!=null&&current.Channels.All(c=>c.Assessable)&&displayedOptions!=null;
    internal void Save(bool reference)
    {
        if(current==null||stream==null||!current.Channels.All(c=>c.Assessable)) return;
        Comparison.Save(reference,current,new(stream.Device.Id,stream.Processing?.ToString()??stream.ProcessingDescription,Passage.Text,Confirm.IsChecked==true)); UpdateComparison();
    }
    private void UpdateComparison()
    {
        string Describe(HeadAlignmentSaved? value)=>value==null?"not saved":$"{value.SavedUtc:HH:mm:ss} UTC • {value.Context.PassageId} • [{value.Measurement.StartFrame:N0}, {value.Measurement.EndFrame:N0})";
        saved.Text=$"A: {Describe(Comparison.A)}\nB: {Describe(Comparison.B)}";
        var result=Comparison.Compare(); comparisonStatus.Text=result.Status; bars.Comparison=result; bars.InvalidateMeasure(); bars.InvalidateVisual();
    }
    private sealed record StereoRow(string Metric,string Value,string Meaning);
    private static Button Button(string text)=>new() { Content=text,Margin=new(4),Padding=new(9,4,9,4) };
    private static TextBlock Text(string text)=>new() { Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(5) };
    private static void Add(Panel panel,string label,FrameworkElement field) { panel.Children.Add(Text(label)); panel.Children.Add(field); }
    private static DataGrid Table()
    {
        var table=new DataGrid { AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserResizeColumns=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,Margin=new(4) };
        var style=new Style(typeof(DataGridCell)); var trigger=new Trigger { Property=DataGridCell.IsSelectedProperty,Value=true }; trigger.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.RoyalBlue)); trigger.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White)); style.Triggers.Add(trigger); table.CellStyle=style; return table;
    }
    private static void AddColumn(DataGrid table,string title,string property,int width,string? format=null)=>table.Columns.Add(new DataGridTextColumn { Header=title,Width=width,Binding=new Binding(property) { StringFormat=format,TargetNullValue="Not assessable" } });
    private sealed class ComparisonBars:FrameworkElement
    {
        internal HeadAlignmentComparison? Comparison;
        protected override Size MeasureOverride(Size available)=>new(double.IsInfinity(available.Width)?800:Math.Max(700,available.Width),Math.Max(100,(Comparison?.Differences.Count??0)*44));
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White,null,new Rect(RenderSize));
            if(Comparison?.Assessable!=true) return;
            void Text(string text,double x,double y)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
            double width=Math.Max(100,ActualWidth-600); int row=0;
            foreach(var difference in Comparison.Differences)
            {
                double y=row++*44,max=difference.Unit=="%"?100:difference.Unit is "FS" or "0–1"?1:Math.Max(1e-12,Math.Max(Math.Abs(difference.A),Math.Abs(difference.B)));
                bool signed=difference.Unit.StartsWith("°",StringComparison.Ordinal)||difference.Metric=="Right delay";
                Text(difference.Channel+" • "+difference.Metric,5,y+9);
                double origin=225+(signed?width*.5:0),scale=signed?width*.5:width;
                void Bar(double value,double top,Brush color) { double length=scale*Math.Min(1,Math.Abs(value)/max); dc.DrawRectangle(color,null,new Rect(origin-(signed&&value<0?length:0),top,length,10)); }
                Bar(difference.A,y+5,Brushes.RoyalBlue); Bar(difference.B,y+19,Brushes.DarkOrange);
                if(signed) dc.DrawLine(new Pen(Brushes.Gray,1),new(origin,y+3),new(origin,y+32));
                Text($"{difference.A:G4} → {difference.B:G4} {difference.Unit}   Δ {difference.Delta:+0.####;-0.####;0}",235+width,y+9);
                dc.DrawLine(new Pen(Brushes.Gainsboro,1),new(0,y+40),new(ActualWidth,y+40));
            }
        }
    }
}

// Presentation of existing qualified stereo measurements; never changes PCM or detector settings.
internal sealed class HeadStereoIndicator:FrameworkElement
{
    internal double? Correlation { get; private set; }
    internal double? Delay { get; private set; }
    internal double? Phase { get; private set; }
    private double delayLimit=1000;
    private double? tone;
    private bool stereo;
    internal HeadStereoIndicator() { ClipToBounds=true; }
    internal void Update(HeadAlignmentMeasurement? measurement,bool active)
    {
        static double? Finite(double? value)=>value.HasValue&&double.IsFinite(value.Value)?value:null;
        stereo=active&&measurement?.Format.Channels==2;
        tone=measurement?.Options.KnownToneHz;
        var result=measurement?.Stereo;
        Correlation=stereo?Finite(result?.ZeroLagCorrelation):null;
        Delay=stereo&&result?.Assessable==true&&!tone.HasValue?Finite(result.DelayMicroseconds):null;
        Phase=stereo&&result?.Assessable==true&&tone.HasValue?Finite(result.SignalPhaseDegrees):null;
        if(measurement!=null) delayLimit=measurement.Options.MaximumLagSamples*1e6/measurement.Format.SampleRate;
        string information=!active?"Stopped / no live measurement":!stereo?"Stereo input required":result?.Status??"No current stereo measurement";
        string values=$"Zero-lag correlation {Correlation?.ToString("F3",CultureInfo.CurrentCulture)??"unavailable"}; delay {Delay?.ToString("F2",CultureInfo.CurrentCulture)??"unavailable"} µs; phase {Phase?.ToString("F2",CultureInfo.CurrentCulture)??"unavailable"} degrees.";
        ToolTip=new ToolTip { Content=new TextBlock { Text=information+"\n"+values,TextWrapping=TextWrapping.Wrap },MaxWidth=620,Placement=System.Windows.Controls.Primitives.PlacementMode.Mouse,HorizontalOffset=16,VerticalOffset=20 };
        System.Windows.Automation.AutomationProperties.SetName(this,"Stereo L/R. "+information+". "+values);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        double width=ActualWidth,height=ActualHeight; if(width<100||height<100) return;
        void Text(string value,double x,double y,double size=13,Brush? color=null)=>dc.DrawText(new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,color??Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        double card=(height-42)/3;
        void Meter(int row,string title,double? value,double limit,string unit,string missing,Brush brush)
        {
            double top=8+row*card;
            dc.DrawRoundedRectangle(Brushes.WhiteSmoke,new Pen(Brushes.LightGray,1),new Rect(8,top,width-16,Math.Max(1,card-8)),8,8);
            Text(title,20,top+8);
            Text(value.HasValue?$"{value.Value:+0.00;-0.00;0.00}{unit}":"—  "+missing,20,top+29,Math.Min(23,Math.Max(14,card*.18)),value.HasValue?brush:Brushes.DimGray);
            double left=24,right=width-24,y=top+card-35,center=(left+right)/2;
            dc.DrawLine(new Pen(Brushes.LightGray,7),new(left,y),new(right,y));
            dc.DrawLine(new Pen(Brushes.Gray,1),new(center,y-9),new(center,y+9));
            if(value.HasValue)
            {
                double position=center+Math.Clamp(value.Value/limit,-1,1)*(right-left)/2;
                dc.DrawLine(new Pen(brush,7),new(center,y),new(position,y)); dc.DrawEllipse(brush,new Pen(Brushes.White,1),new(position,y),6,6);
            }
            Text((-limit).ToString("G4",CultureInfo.CurrentCulture)+unit,left,y+9,11,Brushes.DimGray); Text("0",center-4,y+9,11,Brushes.DimGray);
            Text("+"+limit.ToString("G4",CultureInfo.CurrentCulture)+unit,right-65,y+9,11,Brushes.DimGray);
        }
        Meter(0,"L/R correlation at zero lag",Correlation,1,"",stereo?"No current usable correlation":"Stereo / live input required",Brushes.Teal);
        Meter(1,"Right delay relative to Left • positive = Right lags",Delay,delayLimit," µs",tone.HasValue?"Periodic tone: absolute delay is ambiguous":"Unique, stable correlation peak required",Brushes.RoyalBlue);
        Meter(2,"Signal phase R−L"+(tone.HasValue?$" • declared tone {tone:G} Hz":" • known tone required"),Phase,180,"°","Qualified known stereo tone required",Brushes.DarkOrange);
        Text("Correlation is not phase. Phase is modulo one cycle; no mechanical head angle. Details: hover.",14,height-25,12,Brushes.DimGray);
    }
}
