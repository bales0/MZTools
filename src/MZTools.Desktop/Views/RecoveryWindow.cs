using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

/// <summary>Independent preview of one captured interval, matching AudioRegionRecoveryWindow.</summary>
public sealed class RecoveryWindow : Window
{
    public ShellViewModel Model {get;}
    internal AudioSession Session {get;}
    private readonly Dictionary<string,Control> inputs=[];
    private readonly TextBox from=new(){Name="RecoveryFromSeconds",Width=115},to=new(){Name="RecoveryToSeconds",Width=115},milliseconds=new(){Width=80};
    private readonly DispatcherTimer previewTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
    private readonly AudioChart waveform;
    private CancellationTokenSource? previewWork;
    private Task previewTask=Task.CompletedTask;
    private Button? cancelButton;
    private readonly TabControl tabs=new(){Name="SelectedAnalysisTabs"};
    private readonly TextBlock scope=new(){Margin=new Thickness(5),TextWrapping=Avalonia.Media.TextWrapping.Wrap};
    private bool approved,closing,syncShift,refreshingHistogram;
    private string? completedSettings;
    private int referenceScopeIndex;
    private readonly ComboBox measurementChannel=new(){Width=100,Margin=new Thickness(4)},measurementPolarity=new(){ItemsSource=new[]{"Normal","Inverted"},SelectedIndex=0,Width=100,Margin=new Thickness(4)};
    private readonly TextBlock measurement=new(){Margin=new Thickness(5),TextWrapping=Avalonia.Media.TextWrapping.Wrap};
    private sealed record Message(string Category,string Reason,double StartSeconds,double EndSeconds);

    public RecoveryWindow(AudioSession source,long first,long last)
    {
        Title="Selected audio — deeper analysis / recovery preview";Width=1200;Height=900;MinWidth=850;MinHeight=650;
        Session=source.CreateSelectedPreview(first,last);
        var dialogs=new DesktopDialogs(this){AudioOnly=true};
        Model=new ShellViewModel(new(new MediaDocumentLoader(dialogs,true),dialogs,new()),async text=>{if(Clipboard is{} clipboard)await clipboard.SetTextAsync(text);},dialogs);
        Model.Documents.Add(Session);DataContext=Model;
        var root=new DockPanel{Margin=new Thickness(12)};Content=root;
        var top=new StackPanel{Spacing=3};var topScroll=new ScrollViewer{Content=top,MaxHeight=450,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};DockPanel.SetDock(topScroll,Dock.Top);root.Children.Add(topScroll);
        top.Children.Add(new TextBlock{Text=System.IO.Path.GetFileName(Session.Model.Source),FontSize=20,FontWeight=Avalonia.Media.FontWeight.SemiBold,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        top.Children.Add(new TextBlock{Text="Choose one or more detectors and classification tests. All checked detectors run automatically with the selected tests. Adaptive classification can be off when reference, fixed timing or fuzzy tests are selected. The common ±0.5-sample option tests pulse lengths without moving PCM or timestamps. Include leader, header and payload for a complete program; supply the expected length to search for a payload without a header. Checksums remain mandatory for valid blocks.",Margin=new Thickness(5),TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        var settings=new StackPanel{Spacing=3};settings.Bind(IsEnabledProperty,new Binding("!Documents.IsBusy"));top.Children.Add(settings);
        var fields=Model.RecoveryFields(Session);
        foreach(var field in fields)
        {
            Control input;
            if(field.Multiple)
            {
                var list=new ListBox{ItemsSource=field.Choices,SelectionMode=SelectionMode.Multiple,Height=110,MinWidth=400};
                foreach(string selected in JsonSerializer.Deserialize<string[]>(field.Value)??[])list.SelectedItems!.Add(selected);
                input=list;
            }
            else if(field.Choices is{} choices&&choices.SequenceEqual(new[]{"Yes","No"}))input=new CheckBox{Content=Label(field.Key,field.Label),IsChecked=field.Value=="Yes",Margin=new Thickness(6)};
            else if(field.Choices is{} options)input=new ComboBox{ItemsSource=options,SelectedItem=field.Value,MinWidth=field.Key=="scope"?210:120,ItemTemplate=new FuncDataTemplate<string>((value,_)=>new TextBlock{Text=field.Key switch{"scope"=>value=="Payload only"?"Payload only (no header)":"Standard MZF record","channel"=>value=="All"?"All channels":value=="Mix"?"Mix (L + R) / 2":value,_=>value}})};
            else input=new TextBox{Text=field.Value,Width=field.Key is "thresholds" or "times"?145:field.Key=="length"?85:75};
            input.Name="Recovery_"+field.Key;inputs.Add(field.Key,input);
        }
        var decode=new WrapPanel();settings.Children.Add(decode);Field(decode,"Decode scope",inputs["scope"]);decode.Children.Add(inputs["unknown"]);decode.Children.Add(inputs["diagnostics"]);
        var tests=new WrapPanel();settings.Children.Add(tests);
        foreach(string key in new[]{"adaptive","fixed","fuzzy","zero","schmitt","peak","normal","inverted","half"})tests.Children.Add(inputs[key]);
        tests.Children.Add(inputs["channel"]);
        foreach(string key in new[]{"thresholds","deadband","short","times","fuzzyTolerance"})Field(tests,Label(key,fields.Single(f=>f.Key==key).Label),inputs[key]);
        settings.Children.Add(new TextBlock{Text="Fixed timing uses SHORT × time scale and LONG = 2 × SHORT for the classifier's physical LOW half (HIGH after polarity inversion). Fuzzy matching chooses the nearer SHORT/LONG duration within the tolerance; it also relaxes leader matching. Time scale and fuzzy are separate test families. Lists accept semicolons or spaces.",TextWrapping=Avalonia.Media.TextWrapping.Wrap,Margin=new Thickness(5)});
        var references=new StackPanel();var refTools=new WrapPanel();references.Children.Add(refTools);refTools.Children.Add(inputs["referenceTests"]);
        var refScope=new ComboBox{ItemsSource=new[]{"All profiles","Selected profiles"},SelectedIndex=0,Width=170};refScope.SelectionChanged+=(_,_)=>{referenceScopeIndex=refScope.SelectedIndex;RefreshHistogram();};refTools.Children.Add(refScope);Field(refTools,"Tolerance (%)",inputs["referenceTolerance"]);references.Children.Add(inputs["refs"]);
        tests.Children.Add(new Expander{Header="Reference test settings / profile selection",Content=references,MaxWidth=1000,Margin=new Thickness(5)});
        ((ListBox)inputs["refs"]).SelectionChanged+=(_,_)=>{((CheckBox)inputs["referenceTests"]).IsChecked=true;refScope.SelectedIndex=1;};
        var alignment=new WrapPanel();Field(alignment,"Right shift (samples)",inputs["rightShift"]);Field(alignment,"ms (+ = right)",milliseconds);
        void Step(int count){var command=count switch{-10=>Model.AudioShiftRightMinusTenCommand,-1=>Model.AudioShiftRightMinusOneCommand,1=>Model.AudioShiftRightPlusOneCommand,_=>Model.AudioShiftRightPlusTenCommand};if(command.CanExecute(null))command.Execute(null);previewTimer.Stop();}
        Button(alignment,"← 1 sample",()=>Step(-1));Button(alignment,"1 sample →",()=>Step(1));
        Button(alignment,"Reset",()=>{if(Model.AudioResetCommand.CanExecute(null))Model.AudioResetCommand.Execute(null);});alignment.Children.Add(inputs["invertRight"]);
        alignment.IsEnabled=Session.CanAlign;
        ((ComboBox)inputs["channel"]).SelectedItem=Session.Mix?"Mix":Read("channel");
        ((ComboBox)inputs["channel"]).SelectionChanged+=(_,_)=>{Session.Mix=Read("channel")=="Mix";QueuePreview();RefreshHistogram();};
        ((TextBox)inputs["rightShift"]).Bind(TextBox.TextProperty,new Binding("ActiveAudio.RightShift"){Mode=BindingMode.TwoWay});
        ((CheckBox)inputs["invertRight"]).Bind(CheckBox.IsCheckedProperty,new Binding("ActiveAudio.InvertRight"){Mode=BindingMode.TwoWay});
        ((CheckBox)inputs["mixChannels"]).Bind(CheckBox.IsCheckedProperty,new Binding("ActiveAudio.Mix"){Mode=BindingMode.TwoWay});
        var range=new WrapPanel();top.Children.Add(range);Field(range,"From (s)",from);Field(range,"To (s)",to);Field(range,"Expected payload (B, blank = unknown)",inputs["length"]);
        ToolTip.SetTip(inputs["length"],"For Payload only, blank automatically uses unknown length / framing boundaries. Unknown length ignores any previously entered size.");
        from.Bind(IsEnabledProperty,new Binding("!Documents.IsBusy"));to.Bind(IsEnabledProperty,new Binding("!Documents.IsBusy"));
        from.Text=Seconds(first);to.Text=Seconds(last);
        var scan=Button(range,"Run deeper analysis",async()=>await ScanAsync(refScope.SelectedIndex==0),"RunDeeperAnalysis");scan.Bind(IsEnabledProperty,new Binding("!Documents.IsBusy"));
        var cancel=new Button{Content="Cancel analysis",Margin=new Thickness(5),IsEnabled=false};cancelButton=cancel;
        cancel.Click+=(_,_)=>{previewTimer.Stop();previewWork?.Cancel();Model.Documents.Cancel();};range.Children.Add(cancel);
        Model.Documents.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DocumentCoordinator.IsBusy))UpdateCancelState();};
        top.Children.Add(alignment);
        var progress=new ProgressBar{Height=6,Minimum=0,Maximum=1,Foreground=Avalonia.Media.Brushes.Green};progress.Bind(ProgressBar.ValueProperty,new Binding("Documents.ProgressFraction"));top.Children.Add(progress);
        var error=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap,Foreground=Avalonia.Media.Brushes.Firebrick,Margin=new Thickness(5)};error.Bind(TextBlock.TextProperty,new Binding("Documents.Error"));error.Bind(IsVisibleProperty,new Binding("Documents.HasError"));top.Children.Add(error);
        var status=new TextBlock{Text="Results are a separate preview. The source recording and current analysis remain unchanged. Raw candidates may have a failed checksum and are not complete MZF programs.",TextWrapping=Avalonia.Media.TextWrapping.Wrap,Margin=new Thickness(5)};top.Children.Add(status);
        Model.Documents.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DocumentCoordinator.Status)&&Model.Documents.IsBusy)status.Text=Model.Documents.Status;};
        var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var saveProgram=Button(footer,"Save selected program as MZF…",async()=>await SaveProgramAsync());
        footer.Children.Add(new Button{Content="Save selected candidate as raw bytes…",Command=Model.AudioCandidateExportCommand,Margin=new Thickness(5)});
        var hex=Button(footer,"Hex view…",()=>{if(Session.SelectedCandidate is{} candidate)new HexViewWindow(candidate.Value.Data).Show(this);});
        footer.Children.Add(new Button{Content="Create MZF with supplied metadata…",Command=Model.AudioSyntheticCommand,Margin=new Thickness(5)});
        footer.Children.Add(new Button{Content="Export selection to WAV…",Command=Model.AudioWaveExportCommand,Margin=new Thickness(5)});Button(footer,"Close",Close);
        tabs.Bind(IsEnabledProperty,new Binding("!Documents.IsBusy"));root.Children.Add(tabs);
        var visual=new DockPanel();var nav=new WrapPanel();DockPanel.SetDock(nav,Dock.Top);visual.Children.Add(nav);
        waveform=new AudioChart{Kind=AudioPlotKind.Waveform,Session=Session,ShowIssueMarkers=true,Name="SelectedAnalysisWaveform"};
        waveform.RangeSelected+=(a,b)=>{if(Model.Documents.IsBusy)return;from.Text=Seconds(a);to.Text=Seconds(b);QueuePreview();};
        Button(nav,"Fit selected interval",Session.Fit);Button(nav,"Zoom +",()=>waveform.Zoom(2));Button(nav,"Zoom −",()=>waveform.Zoom(.5));
        Field(nav,"Gain",BoundCombo("ActiveAudio.GainChoices","ActiveAudio.Gain",70));
        var auto=new CheckBox{Content="Auto scale",Margin=new Thickness(4)};auto.Bind(CheckBox.IsCheckedProperty,new Binding("ActiveAudio.AutoScale"){Mode=BindingMode.TwoWay});nav.Children.Add(auto);
        measurementChannel.ItemsSource=Session.CanAlign?new[]{"Left","Right"}:new[]{"Mono"};measurementChannel.SelectedIndex=0;
        Field(nav,"Histogram channel",measurementChannel);Field(nav,"Histogram polarity",measurementPolarity);
        measurementChannel.SelectionChanged+=(_,_)=>RefreshHistogram();measurementPolarity.SelectionChanged+=(_,_)=>RefreshHistogram();
        DockPanel.SetDock(measurement,Dock.Top);visual.Children.Add(measurement);
        var visualTabs=new TabControl{Name="SelectedAnalysisVisualTabs"};visual.Children.Add(visualTabs);visualTabs.Items.Add(new TabItem{Header="Waveform",Content=waveform});
        visualTabs.Items.Add(new TabItem{Header="Histogram",Content=Histogram()});tabs.Items.Add(new TabItem{Header="Visual analysis",Content=visual});
        // Reuse the complete existing result editors and commands, with this window's
        // independent DataContext. No result from the parent analysis is imported.
        var editors=new AudioWorkspace{DataContext=Model};editors.BindSession();
        var results=editors.FindControl<TabControl>("RecoveryTabs")!;
        Control Result(int index){var tab=(TabItem)results.Items[index]!;var content=(Control)tab.Content!;tab.Content=null;content.DataContext=Model;return content;}
        void Tab(string name,Control content)=>tabs.Items.Add(new TabItem{Header=name,Content=content});
        Tab("Programs",Result(0));Tab("Block candidates",Result(1));Tab("Scan passes",Result(2));
        var messages=Table(new object[]{},("Category","Category",140),("Reason","Reason",600),("Start seconds","StartSeconds",120),("End seconds","EndSeconds",120));Tab("Messages",messages);
        var problems=Table(Session.PreviewIssues,("Severity","Severity",85),("Code","Code",180),("Description","Description",600),("Start","Start",100),("End","End",100));problems.Bind(ItemsControl.ItemsSourceProperty,new Binding("ActiveAudio.PreviewIssues"));Tab("Problems",problems);
        problems.SelectionChanged+=(_,_)=>{if(problems.SelectedItem is AudioIssueRow issue)Session.SelectedIssue=issue;};problems.DoubleTapped+=(_,_)=>{if(problems.SelectedItem is AudioIssueRow issue){Session.ShowRange(issue.Start-Session.Model.SampleRate/100,issue.End+Session.Model.SampleRate/100);tabs.SelectedIndex=0;visualTabs.SelectedIndex=0;}};
        Tab("Candidates / Recovery",Result(3));Tab("Pass comparison",Result(6));
        void UpdateOutputs(){hex.IsEnabled=!Model.Documents.IsBusy&&Session.SelectedCandidate is not null;saveProgram.IsEnabled=!Model.Documents.IsBusy&&Session.SelectedProgram is not null;}
        UpdateOutputs();Model.Documents.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DocumentCoordinator.IsBusy))UpdateOutputs();};
        Session.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(AudioSession.SelectedCandidate) or nameof(AudioSession.SelectedProgram))UpdateOutputs();
            if(e.PropertyName is nameof(AudioSession.RightShift) or nameof(AudioSession.InvertRight) or nameof(AudioSession.Mix))
            {SyncMilliseconds();QueuePreview();}
            if(e.PropertyName==nameof(AudioSession.RecoverySummary))
            {
                messages.ItemsSource=(Session.Recovery?.Failures.Select(f=>new Message("Unresolved",f.Reason,f.StartSample/(double)Session.Model.SampleRate,f.EndSample/(double)Session.Model.SampleRate))??[])
                    .Concat(Session.Recovery?.RejectedHeaders.Select(h=>new Message("Header rejected",h.Reason,h.Sample/(double)Session.Model.SampleRate,h.Sample/(double)Session.Model.SampleRate))??[]).ToArray();
                status.Text=Session.RecoverySummary+(Read("scope")=="Payload only"?"\nPayload-only results are in Block candidates; complete MZF programs require a header.":"");RefreshHistogram();
            }
        };
        milliseconds.TextChanged+=(_,_)=>{if(syncShift)return;if(TryNumber(milliseconds.Text,out double ms)){double frames=Math.Round(ms*Session.Model.SampleRate/1000);if(Math.Abs(ms)>2000||Math.Abs(frames)>int.MaxValue){Model.Documents.ReportError("Right shift must be within ±2 seconds.");return;}Session.RightShift=((int)frames).ToString(CultureInfo.InvariantCulture);}};
        from.TextChanged+=(_,_)=>QueuePreview();to.TextChanged+=(_,_)=>QueuePreview();
        void Settings()
        {
            bool payload=Read("scope")=="Payload only";inputs["unknown"].IsEnabled=payload;inputs["length"].IsEnabled=!Model.Documents.IsBusy&&!(payload&&Read("unknown")=="Yes");
            inputs["thresholds"].IsEnabled=Read("schmitt")=="Yes"||Read("peak")=="Yes";inputs["deadband"].IsEnabled=Read("peak")=="Yes";
            inputs["times"].IsEnabled=inputs["short"].IsEnabled=Read("fixed")=="Yes";inputs["fuzzyTolerance"].IsEnabled=Read("fuzzy")=="Yes";
        }
        Model.Documents.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DocumentCoordinator.IsBusy)){Settings();alignment.IsEnabled=Session.CanAlign&&!Model.Documents.IsBusy;}};
        foreach(var check in inputs.Values.OfType<CheckBox>())check.IsCheckedChanged+=(_,_)=>{Settings();RefreshHistogram();};((ComboBox)inputs["scope"]).SelectionChanged+=(_,_)=>Settings();Settings();SyncMilliseconds();UpdateScope();RefreshHistogram();
        previewTimer.Tick+=(_,_)=>
        {
            previewTimer.Stop();if(closing||Model.Documents.IsBusy)return;
            if(!TryRange(out long a,out long b))return;
            previewWork?.Cancel();var request=new CancellationTokenSource();previewWork=request;UpdateCancelState();
            previewTask=RefreshPcmPreviewAsync(request,a,b);
        };
        Opened+=(_,_)=>QueuePreview();
        Closing+=async(_,e)=>
        {
            if(approved)return;e.Cancel=true;if(closing)return;closing=true;previewTimer.Stop();Model.Documents.Cancel();previewWork?.Cancel();await previewTask;
            while(Model.Documents.IsBusy)await Task.Delay(20);
            Session.ResetDraft();Session.MarkExported();if(await Model.Documents.CloseAllAsync()){approved=true;Close();}
        };
        Closed+=(_,_)=>{previewTimer.Stop();editors.DataContext=null;};
    }
    private static string Label(string key,string fallback)=>key switch
    {
        "adaptive"=>"Adaptive","fixed"=>"Fixed time scale","fuzzy"=>"Fuzzy pulse length","peak"=>"Adaptive zero crossing (peak midpoint)","half"=>"Test each pulse at ±0.5 sample","diagnostics"=>"Recovery diagnostics (bounded)","unknown"=>"Unknown length / framing boundaries",
        "zero"=>"Zero crossing","schmitt"=>"Hysteresis (Schmitt)","referenceTests"=>"Reference profile tests",
        "thresholds"=>"Gating scales (; separated)","short"=>"SHORT classifier (µs)","times"=>"Time scales (; separated)","deadband"=>"Ignore near zero (±% FS)","invertRight"=>"Invert right polarity","mixChannels"=>"Mix (L + R) / 2",_=>fallback
    };
    private string Seconds(long frame)=>(frame/(double)Session.Model.SampleRate).ToString("0.#########",CultureInfo.CurrentCulture);
    private static bool TryNumber(string? text,out double value)=>(double.TryParse(text,NumberStyles.Float,CultureInfo.CurrentCulture,out value)||double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value))&&double.IsFinite(value);
    private bool TryRange(out long first,out long last)
    {
        first=last=0;if(!TryNumber(from.Text,out double a)||!TryNumber(to.Text,out double b)||a<0||b<=a||b>Session.Model.DurationSeconds+.5/Session.Model.SampleRate)return false;
        first=(long)Math.Round(a*Session.Model.SampleRate);last=(long)Math.Round(b*Session.Model.SampleRate);return first<last&&last<=Session.Model.FrameCount;
    }
    private string Read(string key)=>inputs[key] switch{CheckBox c=>c.IsChecked==true?"Yes":"No",ComboBox c=>c.SelectedItem?.ToString()??"",TextBox t=>t.Text??"",ListBox l=>JsonSerializer.Serialize(l.SelectedItems!.Cast<string>().ToArray()),_=>""};
    private void QueuePreview(){previewTimer.Stop();if(!closing&&!Model.Documents.IsBusy)previewTimer.Start();}
    private void SyncMilliseconds(){syncShift=true;try{if(int.TryParse(Session.RightShift,out int value))milliseconds.Text=(value*1000d/Session.Model.SampleRate).ToString("0.########",CultureInfo.CurrentCulture);}finally{syncShift=false;}}
    private void UpdateScope()
    {
        scope.Text=$"Frames [{Session.PreviewStart:N0}, {Session.PreviewEnd:N0}) · {(Session.PreviewEnd-Session.PreviewStart)/(double)Session.Model.SampleRate:0.#########} s · separate preview; source unchanged";
        ToolTip.SetTip(from,scope.Text);ToolTip.SetTip(to,scope.Text);
    }
    private Task<bool> SaveProgramAsync()=>Model.Documents.ExecuteAsync(async ct=>
    {
        if(Session.SelectedProgram is null)return false;
        string? path=await new DesktopDialogs(this).PickDiskOutputAsync("Save selected program as MZF","recovered.mzf");if(path is null)return false;
        if(!System.IO.Path.GetExtension(path).Equals(".mzf",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Choose a .mzf destination.");
        using var plan=await AudioOperations.ProgramsAsync(Session,path,"Mzf",false,ct);
        await Model.Documents.WriteExportAsync(plan.Outputs,ct,plan.SourceFiles,Session.VerifySources);Session.MarkExported();return true;
    });
    private async Task RefreshPcmPreviewAsync(CancellationTokenSource request,long first,long last)
    {
        try
        {
            if(Session.PreviewStart!=first||Session.PreviewEnd!=last)Session.SetPreviewBounds(first,last);
            UpdateScope();bool applied=await Session.ApplyTransformKeepingViewportAsync(request.Token);
            if(applied&&!closing&&!Model.Documents.IsBusy&&previewWork==request)Model.Documents.ClearError();
        }
        catch(OperationCanceledException) { }
        catch(Exception error)when(error is not OutOfMemoryException)
        {if(!closing&&!Model.Documents.IsBusy&&previewWork==request)Model.Documents.ReportError(error.Message);}
        finally{if(previewWork==request)previewWork=null;request.Dispose();UpdateCancelState();}
    }
    private void UpdateCancelState(){if(cancelButton is not null)cancelButton.IsEnabled=Model.Documents.IsBusy||previewWork is not null;}
    private string SettingsKey()=>JsonSerializer.Serialize(inputs.Keys.ToDictionary(key=>key,Read))+"|"+from.Text+"|"+to.Text+"|"+referenceScopeIndex;
    private void RefreshHistogram()
    {
        if(refreshingHistogram)return;refreshingHistogram=true;
        try
        {
            if(Session.Recovery is not{} result||result.Passes.Count==0)
            {
                measurement.Text="Histogram: run Deep analysis to collect pulse measurements. Waveform preview is independent.";
                Session.UseSelectedRecoveryMeasurement(null);return;
            }
            var pass=result.Passes.OrderByDescending(p=>p.ValidHeaders+p.ValidPayloads).First();string detector=AudioRecoveryHistogram.Detector(result,pass);
            var streams=pass.Measurements.Where(m=>m.Block.DecodeDetector==detector).ToArray();
            string firstLabel=pass.Channels=="Mix"?"Mix":Session.CanAlign?"Left":"Mono";
            if(!Equals(measurementChannel.ItemsSource!.Cast<string>().First(),firstLabel))
            {int old=measurementChannel.SelectedIndex;measurementChannel.ItemsSource=Session.CanAlign?new[]{firstLabel,"Right"}:new[]{firstLabel};measurementChannel.SelectedIndex=Math.Max(0,old);}
            measurementChannel.IsEnabled=streams.Any(m=>m.Block.Channel==1)&&streams.Any(m=>m.Block.Channel==2);
            int channel=measurementChannel.IsEnabled?measurementChannel.SelectedIndex+1:streams.FirstOrDefault()?.Block.Channel??1;
            if(!measurementChannel.IsEnabled)measurementChannel.SelectedIndex=channel-1;
            measurementPolarity.IsEnabled=streams.Any(m=>m.Block.Inverted)&&streams.Any(m=>!m.Block.Inverted);
            bool polarity=measurementPolarity.IsEnabled?measurementPolarity.SelectedIndex==1:streams.FirstOrDefault()?.Block.Inverted??false;
            if(!measurementPolarity.IsEnabled)measurementPolarity.SelectedIndex=polarity?1:0;
            var selected=streams.FirstOrDefault(m=>m.Block.Channel==channel&&m.Block.Inverted==polarity);Session.UseSelectedRecoveryMeasurement(selected);
            measurement.Text=(completedSettings!=SettingsKey()?"OUTDATED — settings changed; run Deep analysis again. ":"")+$"{pass.Test} · {detector} · {pass.AudioTransform}\n[{result.StartSample:N0}, {result.EndSample:N0}) · channel {channel} · {(polarity?"inverted":"normal")} · {selected?.Block.Distributions.Sum(d=>d.Count)??0:N0} measured pulse halves. One consumed stream is shown; repeated passes are not added.";
        }
        finally{refreshingHistogram=false;}
    }
    internal async Task<bool> ScanAsync(bool allProfiles=false)
    {
        previewTimer.Stop();if(Model.Documents.IsBusy||closing)return false;
        if(!TryRange(out long first,out long last)){Model.Documents.ReportError("Choose a nonempty From / To interval within the source recording.");return false;}
        var values=inputs.Keys.ToDictionary(key=>key,Read);values["rightShift"]=Session.RightShift;values["invertRight"]=Session.InvertRight?"Yes":"No";values["mixChannels"]=Session.Mix?"Yes":"No";
        if(allProfiles)values["refs"]=JsonSerializer.Serialize(((ListBox)inputs["refs"]).ItemsSource!.Cast<string>().ToArray());
        previewWork?.Cancel();
        return await Model.Documents.ExecuteAsync(async ct=>
        {
            await previewTask;ct.ThrowIfCancellationRequested();Session.SetPreviewBounds(first,last);UpdateScope();
            bool result=await Model.AnalyzeRecoveryAsync(Session,new(values),first,last,ct);
            if(result){completedSettings=SettingsKey();RefreshHistogram();if(values["scope"]=="Payload only")tabs.SelectedIndex=2;}return result;
        });
    }
    private Control Histogram()
    {
        var panel=new DockPanel();var top=new StackPanel{Spacing=5};DockPanel.SetDock(top,Dock.Top);panel.Children.Add(top);var bar=new WrapPanel();top.Children.Add(bar);
        var chart=new AudioChart{Session=Session,Kind=AudioPlotKind.Histogram};Button(bar,"Fit histogram",chart.Fit);Button(bar,"Zoom +",()=>chart.Zoom(2));Button(bar,"Zoom −",()=>chart.Zoom(.5));
        foreach(var(key,label)in new[]{("ShortHigh","SHORT High"),("ShortLow","SHORT Low"),("LongHigh","LONG High"),("LongLow","LONG Low")}){var check=new CheckBox{Content=label,Margin=new Thickness(6)};check.Bind(CheckBox.IsCheckedProperty,new Binding("ActiveAudio."+key){Mode=BindingMode.TwoWay});bar.Children.Add(check);}
        var references=new WrapPanel();top.Children.Add(references);var reference=new ComboBox{MinWidth=180};reference.Bind(ItemsControl.ItemsSourceProperty,new Binding("ActiveAudio.ReferenceNames"));reference.Bind(ComboBox.SelectedIndexProperty,new Binding("ActiveAudio.ReferenceIndex"){Mode=BindingMode.TwoWay});references.Children.Add(reference);
        var overlay=new CheckBox{Content="Show reference",Margin=new Thickness(6)};overlay.Bind(CheckBox.IsCheckedProperty,new Binding("ActiveAudio.ShowReference"){Mode=BindingMode.TwoWay});references.Children.Add(overlay);references.Children.Add(new Button{Content="User reference…",Command=Model.AudioReferenceCommand});

        var stats=Table(Session.Distributions,("Group","Group",120),("Count","Count",100),("Mean µs","Mean",100),("Median µs","Median",100),("Stddev µs","StdDev",100),("Reference µs","Reference",100));stats.Height=110;stats.Bind(ItemsControl.ItemsSourceProperty,new Binding("ActiveAudio.Distributions"));top.Children.Add(new Expander{Header="Pulse statistics",Content=stats});panel.Children.Add(chart);return panel;
    }
    private ComboBox BoundCombo(string items,string selected,double width){var control=new ComboBox{Width=width};control.Bind(ItemsControl.ItemsSourceProperty,new Binding(items));control.Bind(ComboBox.SelectedItemProperty,new Binding(selected){Mode=BindingMode.TwoWay});return control;}
    private static void Field(Panel panel,string label,Control input){var pair=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(4)};pair.Children.Add(new TextBlock{Text=label,Margin=new Thickness(4),VerticalAlignment=VerticalAlignment.Center});pair.Children.Add(input);panel.Children.Add(pair);}
    private static Button Button(Panel panel,string label,Action action,string? name=null){var button=new Button{Content=label,Margin=new Thickness(4),Padding=new Thickness(7,3)};if(name is not null)button.Name=name;button.Click+=(_,_)=>action();panel.Children.Add(button);return button;}
    private static TableView Table(System.Collections.IEnumerable rows,params(string Title,string Path,int Width)[] columns){var table=new TableView{ItemsSource=rows,Margin=new Thickness(4)};foreach(var c in columns)table.Columns.Add(new(){Header=c.Title,Binding=new Binding(c.Path),Width=new GridLength(c.Width)});return table;}
}
