using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MZTools;

internal sealed class QuickDiskVisualInspectorWindow : Window
{
    private QuickDiskVisualSnapshot snapshot;
    private readonly Action<IReadOnlyList<int>> selectFiles;
    private readonly QuickDiskVisualCanvas canvas=new();
    private readonly QuickDiskSurfaceCanvas surfaceCanvas=new();
    private readonly CheckBox showMfm=new() {Content="Unrolled MFM detail",IsChecked=true,Margin=new Thickness(6)};
    private readonly DataGrid regions=Table(),files=Table(),findings=Table();
    private readonly ReportView statistics=new() {CompactHeader=true};
    private readonly TextBlock summary=new() {TextWrapping=TextWrapping.Wrap},status=new();
    private readonly CheckBox floating=new() {Content="Floating selection details",IsChecked=true,Margin=new Thickness(6)};
    private readonly TabControl tabs=new();
    private readonly Button cancelExport=new() {Content="Cancel export",Margin=new Thickness(4),IsEnabled=false};
    private readonly HashSet<QuickDiskRegion> selection=new();
    private QuickDiskRegion? anchor,focus;
    private Window? detailWindow;
    private ReportView? detail;
    private bool updating,closed,available=true;
    private CancellationTokenSource? exportWork;
    internal QuickDiskVisualSnapshot Snapshot=>snapshot;
    internal IReadOnlyCollection<QuickDiskRegion> Selection=>selection;
    internal QuickDiskVisualInspectorWindow(Window owner,QuickDiskVisualSnapshot model,Action<IReadOnlyList<int>> selectFiles)
    {
        Owner=owner;this.selectFiles=selectFiles;snapshot=model;Title="Visual QuickDisk Inspector — read-only";
        Width=Math.Min(1150,SystemParameters.WorkArea.Width-24);Height=Math.Min(780,SystemParameters.WorkArea.Height-24);
        MinWidth=Math.Min(800,Width);MinHeight=Math.Min(480,Height);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel {Margin=new Thickness(10)};Content=root;
        DockPanel.SetDock(summary,Dock.Top);root.Children.Add(summary);
        var tools=new WrapPanel {Margin=new Thickness(0,8,0,8)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
        void Button(string text,Action action) {var button=new Button {Content=text,Margin=new Thickness(3),Padding=new Thickness(8,4,8,4)};button.Click+=(_,_)=>action();tools.Children.Add(button);}
        Button("Fit",()=>surfaceCanvas.Fit());Button("Selected regions",()=> {showMfm.IsChecked=true;canvas.FitSelection();});Button("Zoom +",()=>surfaceCanvas.ZoomBy(2));Button("Zoom −",()=>surfaceCanvas.ZoomBy(.5));
        Button("Hex selected region…",OpenHex);Button("Export report…",async()=>await Export());Button("Copy summary",()=>Clipboard.SetText(snapshot.Layout.Summary));
        cancelExport.Click+=(_,_)=>exportWork?.Cancel();tools.Children.Add(cancelExport);
        DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);root.Children.Add(tabs);
        void Tab(string name,UIElement content)=>tabs.Items.Add(new TabItem {Header=name,Content=content});
        Tab("Disk Surface",CreateSurfaceView());Tab("Regions",regions);Tab("Files",files);Tab("Findings",findings);Tab("Statistics",statistics);
        Columns(regions,("Role","Kind"),("Name","Name"),("File","FileNumber"),("Start","Offset"),("Length","Length"),("Payload B","Size"),("LOAD","LoadText"),("EXEC","ExecuteText"));
        Columns(files,("File","FileNumber"),("Name","Name"),("Payload B","Size"),("LOAD","LoadText"),("EXEC","ExecuteText"));
        Columns(findings,("Severity","Severity"),("Description","Description"),("Start","Start"),("End exclusive","End"));findings.Columns[1].Width=new DataGridLength(1,DataGridLengthUnitType.Star);
        regions.SelectionChanged+=(_,_)=> {if(!updating) SelectRegions(regions.SelectedItems.Cast<QuickDiskRegion>().ToArray(),regions.CurrentItem as QuickDiskRegion,true);};
        files.SelectionChanged+=(_,_)=> {if(!updating) SelectFiles(files.SelectedItems.Cast<QuickDiskRegion>().Select(r=>r.FileIndex).ToArray(),true);};
        regions.MouseDoubleClick+=(_,_)=> {showMfm.IsChecked=true;canvas.FitSelection();tabs.SelectedIndex=0;};
        files.MouseDoubleClick+=(_,_)=> {showMfm.IsChecked=true;canvas.FitSelection();tabs.SelectedIndex=0;};
        findings.SelectionChanged+=(_,_)=> {if(findings.SelectedItem is QuickDiskVisualFinding finding) ShowFinding(finding);};
        findings.MouseDoubleClick+=(_,_)=> {if(findings.SelectedItem is QuickDiskVisualFinding {Start:long start,End:long end}) {SelectRegions(snapshot.Layout.Regions.Where(r=>r.End>start && r.Start<end).ToArray(),null,false);canvas.FitSelection();tabs.SelectedIndex=0;}};
        void Gesture(QuickDiskRegion? region,ModifierKeys modifiers) {
            var next=modifiers.HasFlag(ModifierKeys.Control)?selection.ToHashSet():new HashSet<QuickDiskRegion>();
            if(region!=null) {
                if(modifiers.HasFlag(ModifierKeys.Shift) && anchor!=null) next.UnionWith(snapshot.Layout.Regions.Where(r=>r.Start>=Math.Min(anchor.Start,region.Start) && r.Start<=Math.Max(anchor.Start,region.Start)));
                else {if(!next.Add(region)) next.Remove(region);anchor=region;}
            }
            SelectRegions(next,region,true);
        }
        canvas.Selected+=Gesture;surfaceCanvas.Selected+=(region,modifiers)=> {Gesture(region,modifiers);canvas.FitSelection();};
        surfaceCanvas.ZoomChanged+=()=> {if(surfaceCanvas.Zoom>=6 && selection.Count>0) {showMfm.IsChecked=true;canvas.FitSelection();}};
        foreach(var element in new FrameworkElement[] {surfaceCanvas,canvas,regions}) {
            var menu=new ContextMenu();element.ContextMenu=menu;var hex=new MenuItem {Header="Hex selected region…"};hex.Click+=(_,_)=>OpenHex();menu.Items.Add(hex);
            var export=new MenuItem {Header="Export selected region…"};export.Click+=async(_,_)=>await ExportRegions();menu.Items.Add(export);
            menu.Opened+=(_,_)=> {hex.IsEnabled=available && focus!=null;export.IsEnabled=available && selection.Count>0 && exportWork==null;
                export.Header=selection.Count>1?"Export selected regions…":"Export selected region…";
                export.ToolTip="Decoded bytes or logical container bytes; undecoded physical areas use packed stored cells (LSB-first, final byte zero-padded).";
            };
        }
        floating.Checked+=(_,_)=> {if(selection.Count>0) UpdateDetails();};floating.Unchecked+=(_,_)=>detailWindow?.Hide();
        PreviewKeyDown+=(_,e)=> {if(e.Key==Key.Escape) {SelectRegions(Array.Empty<QuickDiskRegion>(),null,true);e.Handled=true;}};
        Closed+=(_,_)=> {closed=true;exportWork?.Cancel();detailWindow?.Close();};SetSnapshot(model);
    }
    private FrameworkElement CreateSurfaceView()
    {
        var view=new DockPanel();var tools=new WrapPanel();DockPanel.SetDock(tools,Dock.Top);view.Children.Add(tools);tools.Children.Add(showMfm);tools.Children.Add(floating);
        tools.Children.Add(new TextBlock {Text="Orange metadata · green data · gray gaps · blue selection · MFM source is identified below",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6)});
        var body=new Grid();body.RowDefinitions.Add(new() {Height=new GridLength(3,GridUnitType.Star),MinHeight=100});
        body.RowDefinitions.Add(new() {Height=new GridLength(6)});body.RowDefinitions.Add(new() {Height=new GridLength(2,GridUnitType.Star),MinHeight=180});
        body.Children.Add(surfaceCanvas);
        var divider=new GridSplitter {Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=SystemColors.ControlDarkBrush};Grid.SetRow(divider,1);body.Children.Add(divider);
        var panel=new DockPanel();Grid.SetRow(panel,2);body.Children.Add(panel);
        var controls=new WrapPanel();DockPanel.SetDock(controls,Dock.Top);panel.Children.Add(controls);
        void Button(string name,Action action) {var button=new Button {Content=name,Margin=new Thickness(3)};button.Click+=(_,_)=>action();controls.Children.Add(button);}
        Button("Fit track",canvas.Fit);Button("Selected region",canvas.FitSelection);Button("Zoom +",()=>canvas.Zoom(2));Button("Zoom −",()=>canvas.Zoom(.5));panel.Children.Add(canvas);
        void Visibility(bool visible) {
            divider.Visibility=panel.Visibility=visible?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
            body.RowDefinitions[1].Height=new GridLength(visible?6:0);
            body.RowDefinitions[2].MinHeight=visible?180:0;
            body.RowDefinitions[2].Height=visible?new GridLength(2,GridUnitType.Star):new GridLength(0);
        }
        showMfm.Checked+=(_,_)=>Visibility(true);showMfm.Unchecked+=(_,_)=>Visibility(false);
        view.Children.Add(body);return view;
    }
    private static DataGrid Table() {var grid=new DataGrid {AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Extended,ClipboardCopyMode=DataGridClipboardCopyMode.IncludeHeader,EnableRowVirtualization=true,EnableColumnVirtualization=true};MapInteraction.EmphasizeSelection(grid);return grid;}
    private static void Columns(DataGrid grid,params (string Name,string Path)[] columns) {foreach(var column in columns) grid.Columns.Add(new DataGridTextColumn {Header=column.Name,Binding=new Binding(column.Path),Width=column.Name=="Name"?new DataGridLength(1,DataGridLengthUnitType.Star):DataGridLength.Auto,MinWidth=70});}
    internal void SetSnapshot(QuickDiskVisualSnapshot value)
    {
        snapshot=value;anchor=null;available=true;tabs.IsEnabled=true;updating=true;
        try {canvas.SetSnapshot(value);surfaceCanvas.Snapshot=value;surfaceCanvas.Fit();regions.ItemsSource=value.Layout.Regions;files.ItemsSource=value.Layout.Regions.Where(r=>r.FileIndex>=0).GroupBy(r=>r.FileIndex).Select(g=>g.First()).ToArray();findings.ItemsSource=value.Findings;
            statistics.Show(new("QuickDisk statistics",value.Layout.Summary,value.Report().ExportText,new[] {new ReportTable("Properties",new[] {"Property","Value"},value.Statistics)}));
            summary.Text=(value.Layout.IsPreview?"Rebuilt preview · ":"Original image · ")+$"{QuickDiskFormatLabels.Display(value.Layout.Format)} · {value.Host} · {files.Items.Count} files · {value.Layout.Regions.Count} regions · {value.Findings.Count} findings";
            summary.ToolTip=value.Layout.Summary;
            ((TabItem)tabs.Items[3]).Header=$"Findings ({value.Findings.Count})";
        } finally {updating=false;}
        SelectRegions(Array.Empty<QuickDiskRegion>(),null,false);
    }
    internal void ShowUnavailable(string message) {available=false;summary.Text=message;status.Text="Previous snapshot is unavailable for navigation; source unchanged.";tabs.IsEnabled=false;detailWindow?.Hide();}
    internal void SelectFiles(IEnumerable<int> indices,bool notify=false)
    {var selected=indices.ToHashSet();SelectRegions(snapshot.Layout.Regions.Where(r=>selected.Contains(r.FileIndex)).ToArray(),null,notify);}
    internal void SelectRegions(IEnumerable<QuickDiskRegion> values,QuickDiskRegion? active,bool notify)
    {
        if(!available) return;
        selection.Clear();selection.UnionWith(values.Where(snapshot.Layout.Regions.Contains));focus=active!=null && selection.Contains(active)?active:selection.FirstOrDefault();
        updating=true;
        try {regions.UnselectAll();foreach(var region in selection) regions.SelectedItems.Add(region);
            var keys=selection.Where(r=>r.FileIndex>=0).Select(r=>r.FileIndex).ToHashSet();files.UnselectAll();foreach(var row in files.Items.Cast<QuickDiskRegion>().Where(r=>keys.Contains(r.FileIndex))) files.SelectedItems.Add(row);
            canvas.Selection=selection.ToArray();canvas.InvalidateVisual();status.Text=$"{selection.Count} selected regions · {keys.Count} related files · {snapshot.Layout.Unit}";
            surfaceCanvas.Selection=selection.ToArray();surfaceCanvas.InvalidateVisual();
            if(focus!=null) regions.ScrollIntoView(focus);
            if(notify) selectFiles(keys.Order().ToArray());
        } finally {updating=false;}
        UpdateDetails();
    }
    private void EnsureDetails()
    {
        if(detailWindow==null) {
            detail=new ReportView {CompactHeader=true,SingleLineHeading=true};
            detailWindow=new Window {Owner=this,Title="QuickDisk selection details — read-only",Content=detail,Width=Math.Min(620,SystemParameters.WorkArea.Width-20),Height=Math.Min(450,SystemParameters.WorkArea.Height-20),MinWidth=320,MinHeight=220,ShowActivated=false,ShowInTaskbar=false,ResizeMode=ResizeMode.CanResizeWithGrip,
                Left=Math.Max(SystemParameters.WorkArea.Left,Math.Min(Left+ActualWidth-640,SystemParameters.WorkArea.Right-640)),
                Top=Math.Max(SystemParameters.WorkArea.Top,Math.Min(Top+80,SystemParameters.WorkArea.Bottom-470))};
            detailWindow.Closed+=(_,_)=> {detailWindow=null;detail=null;floating.IsChecked=false;};
        }
        if(!detailWindow.IsVisible) detailWindow.Show();
    }
    private void UpdateDetails()
    {
        if(closed) return;
        if(floating.IsChecked==true && selection.Count>0) EnsureDetails();
        if(detail==null) return;
        detail.Text=focus==null?"No region selected":snapshot.Layout.Detail(focus)+$"\nSelected regions: {selection.Count}\nSource: {(snapshot.Layout.IsPreview?"Rebuilt preview":"Original image")}\nCoordinates: {snapshot.Layout.Unit}\nUnassigned areas are not certified free space.";
    }
    private void ShowFinding(QuickDiskVisualFinding finding) {EnsureDetails();floating.IsChecked=true;detail!.Text=$"{finding.Severity}: {finding.Description}\nStart: {finding.Start}\nEnd exclusive: {finding.End}";}
    private void OpenHex()
    {
        if(focus==null) return;
        if(!available) return;
        try {var browser=new HexBrowser {Owner=this};browser.ShowRawData($"{focus.Name} · {focus.Kind} · {focus.Offset}",snapshot.Layout.GetBlockBytes(focus));browser.Show();}
        catch(Exception ex) {MessageBox.Show(this,ex.Message,"QuickDisk HEX preview");}
    }
    private async Task Export()
    {
        if(exportWork!=null || !available) return;
        var picker=new SaveFileDialog {Filter="JSON report|*.json|CSV report|*.csv|Text report|*.txt",FileName="QuickDisk-visual-analysis"};if(picker.ShowDialog(this)!=true) return;
        var model=snapshot;using var cancellation=new CancellationTokenSource();exportWork=cancellation;
        cancelExport.IsEnabled=true;string format=picker.FilterIndex==1?"json":picker.FilterIndex==2?"csv":"txt";
        string temporary=picker.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";status.Text="Exporting report…";
        try {string text=await Task.Run(()=>model.Export(format),cancellation.Token);
            await File.WriteAllTextAsync(temporary,text,cancellation.Token);cancellation.Token.ThrowIfCancellationRequested();File.Move(temporary,picker.FileName,true);if(!closed) status.Text="Report saved.";
        } catch(OperationCanceledException) {if(!closed) status.Text="Export canceled.";}
        catch(Exception ex) {if(!closed) status.Text="Export failed: "+ex.Message;}
        finally {if(File.Exists(temporary)) File.Delete(temporary);exportWork=null;cancelExport.IsEnabled=false;}
    }
    private async Task ExportRegions()
    {
        if(!available || selection.Count==0 || exportWork!=null) return;
        var model=snapshot;var selected=selection.OrderBy(r=>r.Start).ThenBy(r=>r.Kind).ToArray();
        var destinations=new List<(QuickDiskRegion Region,string Path)>();bool overwrite=selected.Length==1;
        string Name(QuickDiskRegion region,int index)=>$"{index+1:D3}_{region.Kind}_{region.Start:X}.bin";
        if(selected.Length==1) {
            var picker=new SaveFileDialog {Filter="Binary data|*.bin|All files|*.*",FileName=Name(selected[0],0)};
            if(picker.ShowDialog(this)!=true) return;destinations.Add((selected[0],picker.FileName));
        } else {
            var picker=new OpenFolderDialog {Title="Export selected QD regions — one binary file per region"};
            if(picker.ShowDialog(this)!=true) return;
            for(int i=0;i<selected.Length;i++) {
                string name=Name(selected[i],i),path=Path.Combine(picker.FolderName,name);int suffix=2;
                while(File.Exists(path) || Directory.Exists(path)) path=Path.Combine(picker.FolderName,Path.GetFileNameWithoutExtension(name)+$"_{suffix++}.bin");
                destinations.Add((selected[i],path));
            }
        }
        using var cancellation=new CancellationTokenSource();exportWork=cancellation;cancelExport.IsEnabled=true;int completed=0;
        try {
            foreach(var item in destinations) {
                status.Text=$"Exporting QD region {completed+1}/{destinations.Count}…";
                await model.ExportRegionAsync(item.Region,item.Path,overwrite,cancellation.Token);completed++;
            }
            if(!closed) status.Text=$"Exported {completed} region(s) to binary files.";
        } catch(OperationCanceledException) {if(!closed) status.Text=$"Export canceled; {completed} completed file(s) retained.";}
        catch(Exception ex) {if(!closed) status.Text=$"Export failed after {completed} file(s): "+ex.Message;}
        finally {exportWork=null;cancelExport.IsEnabled=false;}
    }
}
