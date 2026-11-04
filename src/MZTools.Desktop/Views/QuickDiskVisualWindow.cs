using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

public sealed class QuickDiskVisualWindow:Window
{
    internal TapeSession Session {get;}
    private sealed record Metric(string Property,string Value);
    public QuickDiskVisualWindow(ShellViewModel model)
    {
        Session=model.ActiveTape??throw new InvalidOperationException("Open a QuickDisk first.");
        if(Session.Snapshot is null)throw new InvalidOperationException("No physical/logical QuickDisk snapshot is available.");
        Title="Visual QuickDisk Inspector — read-only";Width=1150;Height=780;MinWidth=800;MinHeight=480;
        var root=new DockPanel{Margin=new Thickness(10)};Content=root;
        var toolbar=new WrapPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        var graph=new QuickDiskVisualCanvas();graph.SetSnapshot(Session.Snapshot);
        var spiral=new QuickDiskSurfaceCanvas{Snapshot=Session.Snapshot};
        string Summary()=> (Session.Snapshot!.Layout.IsPreview?"Rebuilt preview · ":"Original image · ")+$"{QuickDiskFormatLabels.Display(Session.Snapshot.Layout.Format)} · {Session.Snapshot.Host} · {Session.Snapshot.Layout.Regions.Where(r=>r.FileIndex>=0).Select(r=>r.FileIndex).Distinct().Count()} files · {Session.Snapshot.Layout.Regions.Count} regions · {Session.Snapshot.Findings.Count} findings";
        var summary=new TextBlock{Text=Summary(),TextWrapping=Avalonia.Media.TextWrapping.Wrap};DockPanel.SetDock(summary,Dock.Top);root.Children.Insert(0,summary);
        var showMfm=new CheckBox{Content="Unrolled MFM detail",IsChecked=true,Margin=new Thickness(6)};
        var floating=new CheckBox{Content="Floating selection details",IsChecked=false,Margin=new Thickness(6)};
        var selection=new HashSet<QuickDiskRegion>();QuickDiskRegion? anchor=null;bool selecting=false;Action? synchronizeTables=null;
        Window? detailWindow=null;TextBlock? floatingText=null;bool ownerClosing=false;
        void UpdateSelection(IEnumerable<QuickDiskRegion> values,QuickDiskRegion? active,bool notify)
        {
            if(selecting||ownerClosing)return;selecting=true;
            try
            {
                selection.Clear();selection.UnionWith(values);
                graph.Selection=spiral.Selection=selection.ToArray();graph.InvalidateVisual();spiral.InvalidateVisual();
                if(notify)Session.SetSelection(Session.Rows.Where(r=>selection.Any(region=>region.FileIndex==r.Index)));
                Session.SelectRegion(active??selection.FirstOrDefault());synchronizeTables?.Invoke();

                if(selection.Count>0&&floating.IsChecked==true)
                {
                    if(detailWindow is null){floatingText=new TextBlock{Margin=new Thickness(8),TextWrapping=Avalonia.Media.TextWrapping.Wrap};detailWindow=new Window{Title="QuickDisk selection details — read-only",Width=620,Height=450,MinWidth=320,MinHeight=220,ShowActivated=false,ShowInTaskbar=false,Content=new ScrollViewer{Content=floatingText}};detailWindow.Closed+=(_,_)=>{detailWindow=null;floatingText=null;if(!ownerClosing)floating.IsChecked=false;};}
                    floatingText!.Text=Session.MapDetail+$"\nSelected regions: {selection.Count}\nCoordinates: {Session.Snapshot!.Layout.Unit}\nUnassigned areas are not certified free space.";
                    if(!detailWindow.IsVisible)detailWindow.Show(this);
                }
                else detailWindow?.Hide();
            }
            finally{selecting=false;}
        }
        void Gesture(QuickDiskRegion? region,Avalonia.Input.KeyModifiers keys)
        {
            var next=keys.HasFlag(OperatingSystem.IsMacOS()?Avalonia.Input.KeyModifiers.Meta:Avalonia.Input.KeyModifiers.Control)?selection.ToHashSet():new HashSet<QuickDiskRegion>();
            if(region is not null){if(keys.HasFlag(Avalonia.Input.KeyModifiers.Shift)&&anchor is not null)next.UnionWith(Session.Snapshot!.Layout.Regions.Where(r=>r.Start>=Math.Min(region.Start,anchor.Start)&&r.Start<=Math.Max(region.Start,anchor.Start)));else{if(!next.Add(region))next.Remove(region);anchor=region;}}
            UpdateSelection(next,region,true);
        }
        graph.Selected+=Gesture;spiral.Selected+=(region,keys)=>{Gesture(region,keys);graph.FitSelection();};
        floating.IsCheckedChanged+=(_,_)=>UpdateSelection(selection.ToArray(),Session.SelectedRegion,false);
        KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Escape){UpdateSelection([],null,true);e.Handled=true;}};
        Closing+=(_,_)=>ownerClosing=true;
        Closed+=(_,_)=>detailWindow?.Close();
        void Button(string title,Action action){var button=new Button{Content=title,Margin=new Thickness(4),Padding=new Thickness(8,3)};button.Click+=(_,_)=>action();toolbar.Children.Add(button);}
        Button("Fit",spiral.Fit);Button("Selected regions",()=>{showMfm.IsChecked=true;graph.FitSelection();});Button("Zoom +",()=>spiral.ZoomBy(2));Button("Zoom −",()=>spiral.ZoomBy(.5));
        Button("Hex selected region…",()=>{if(Session.SelectedRegion is{} r)new HexViewWindow(Session.Snapshot!.Layout.GetBlockBytes(r)).Show(this);});
        Button("Export report…",async()=>
        {
            var snapshot=Session.Snapshot!;
            await model.Documents.ExecuteAsync(async ct=>
            {
                var dialogs=new DesktopDialogs(this);string? path=await dialogs.PickDiskOutputAsync("Export QuickDisk report (JSON, CSV, TXT)","QuickDisk-visual-analysis.json");if(path is null)return false;
                string format=System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();if(format is not("json" or "csv" or "txt"))throw new InvalidOperationException("Choose a .json, .csv or .txt filename.");
                var bytes=await Task.Run(()=>System.Text.Encoding.UTF8.GetBytes(snapshot.Export(format)),ct);
                await model.Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=bytes},ct);return true;
            });
        });
        Button("Copy summary",async()=>{if(Clipboard is{} clipboard)await clipboard.SetTextAsync(Session.Snapshot!.Layout.Summary);});Button("Cancel export",model.Documents.Cancel);
        async Task ExportSelected()
        {
            var snapshot=Session.Snapshot!;var chosen=selection.OrderBy(r=>r.Start).ToArray();if(chosen.Length==0)return;
            await model.Documents.ExecuteAsync(async ct=>
            {
                var outputs=new Dictionary<string,byte[]>();
                if(chosen.Length==1)
                {
                    string? path=await new DesktopDialogs(this).PickDiskOutputAsync("Export selected region",$"001_{chosen[0].Kind}_{chosen[0].Start:X}.bin");if(path is null)return false;
                    outputs.Add(path,snapshot.Layout.GetBlockBytes(chosen[0]));
                }
                else
                {
                    var folders=await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions{Title="Export selected QD regions — one binary file per region",AllowMultiple=false});
                    if(folders.Count==0)return false;string folder=folders[0].TryGetLocalPath()??throw new NotSupportedException("A local directory is required.");
                    for(int i=0;i<chosen.Length;i++){ct.ThrowIfCancellationRequested();var region=chosen[i];string name=$"{i+1:D3}_{region.Kind}_{region.Start:X}",path=System.IO.Path.Combine(folder,name+".bin");int suffix=2;while(File.Exists(path)||Directory.Exists(path))path=System.IO.Path.Combine(folder,name+$"_{suffix++}.bin");outputs.Add(path,snapshot.Layout.GetBlockBytes(region));}
                }
                await model.Documents.WriteExportAsync(outputs,ct);return true;
            });
        }
        var detail=new TextBlock{Text=Session.MapDetail,TextWrapping=Avalonia.Media.TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)};DockPanel.SetDock(detail,Dock.Bottom);root.Children.Add(detail);
        var tabs=new TabControl();root.Children.Add(tabs);
        var surface=new DockPanel();var choices=new WrapPanel();DockPanel.SetDock(choices,Dock.Top);surface.Children.Add(choices);choices.Children.Add(showMfm);choices.Children.Add(floating);
        choices.Children.Add(new TextBlock{Text="Orange metadata · green data · gray gaps · blue selection · MFM source is identified below",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6)});
        var body=new Grid{RowDefinitions=new RowDefinitions("3*,6,2*")};surface.Children.Add(body);body.Children.Add(spiral);
        var divider=new GridSplitter{Height=6,Background=Avalonia.Media.Brushes.DarkGray,ResizeDirection=GridResizeDirection.Rows};Grid.SetRow(divider,1);body.Children.Add(divider);
        var panel=new DockPanel{MinHeight=180};Grid.SetRow(panel,2);body.Children.Add(panel);var navigation=new WrapPanel();DockPanel.SetDock(navigation,Dock.Top);panel.Children.Add(navigation);
        void Nav(string text,Action action){var button=new Button{Content=text,Margin=new Thickness(3)};button.Click+=(_,_)=>action();navigation.Children.Add(button);}
        Nav("Fit track",graph.Fit);Nav("Selected region",graph.FitSelection);Nav("Zoom +",()=>graph.Zoom(2));Nav("Zoom −",()=>graph.Zoom(.5));panel.Children.Add(graph);
        showMfm.IsCheckedChanged+=(_,_)=>{bool visible=showMfm.IsChecked==true;panel.IsVisible=divider.IsVisible=visible;body.RowDefinitions[1].Height=new GridLength(visible?6:0);body.RowDefinitions[2].Height=visible?new GridLength(2,GridUnitType.Star):new GridLength(0);};
        spiral.ZoomChanged+=()=>{if(spiral.Zoom>=6&&selection.Count>0){showMfm.IsChecked=true;graph.FitSelection();}};
        tabs.Items.Add(new TabItem{Header="Disk Surface",Content=surface});
        var regions=Table(Session.Snapshot.Layout.Regions,("Role","Kind",100),("Name","Name",230),("File","FileNumber",65),("Start","Offset",100),("Length","Length",90),("Payload B","Size",80),("LOAD","LoadText",75),("EXEC","ExecuteText",75));
        regions.SelectionChanged+=(_,_)=>{if(!selecting)UpdateSelection(regions.SelectedItems!.OfType<QuickDiskRegion>().ToArray(),regions.SelectedItem as QuickDiskRegion,true);};tabs.Items.Add(new TabItem{Header="Regions",Content=regions});
        QuickDiskRegion[] FileRegions()=>Session.Snapshot!.Layout.Regions.Where(r=>r.FileIndex>=0).GroupBy(r=>r.FileIndex).Select(g=>g.First()).ToArray();
        var files=Table(FileRegions(),("File","FileNumber",65),("Name","Name",220),("Payload B","Size",90),("LOAD","LoadText",100),("EXEC","ExecuteText",100));tabs.Items.Add(new TabItem{Header="Files",Content=files});
        files.SelectionChanged+=(_,_)=>{if(!selecting){var indices=files.SelectedItems!.OfType<QuickDiskRegion>().Select(r=>r.FileIndex).ToHashSet();UpdateSelection(Session.Snapshot!.Layout.Regions.Where(r=>indices.Contains(r.FileIndex)).ToArray(),null,true);}};
        synchronizeTables=()=>
        {
            regions.SelectedItems!.Clear();foreach(var region in selection)regions.SelectedItems.Add(region);
            var keys=selection.Select(r=>r.FileIndex).ToHashSet();files.SelectedItems!.Clear();foreach(var region in FileRegions().Where(r=>keys.Contains(r.FileIndex)))files.SelectedItems.Add(region);
        };
        foreach(Control control in new Control[]{spiral,graph,regions})
        {
            var hex=new MenuItem{Header="Hex selected region…"};hex.Click+=(_,_)=>{if(Session.SelectedRegion is{} region)new HexViewWindow(Session.Snapshot!.Layout.GetBlockBytes(region)).Show(this);};
            var export=new MenuItem{Header="Export selected region…"};export.Click+=async(_,_)=>await ExportSelected();control.ContextMenu=new ContextMenu{ItemsSource=new[]{hex,export}};
        }
        foreach(var table in new[]{regions,files})table.DoubleTapped+=(_,_)=>{showMfm.IsChecked=true;graph.FitSelection();tabs.SelectedIndex=0;};
        var findings=Table(Session.Snapshot.Findings,("Severity","Severity",100),("Description","Description",600),("Start","Start",100),("End exclusive","End",100));tabs.Items.Add(new TabItem{Header=$"Findings ({Session.Snapshot.Findings.Count})",Content=findings});
        var statistics=Table(Session.Snapshot.Statistics.Select(row=>new Metric(row[0],string.Join(" · ",row.Skip(1)))),("Property","Property",250),("Value","Value",650));tabs.Items.Add(new TabItem{Header="Statistics",Content=statistics});
        void Changed(object? sender,PropertyChangedEventArgs e)
        {
            detail.Text=Session.MapDetail;graph.InvalidateVisual();spiral.InvalidateVisual();
            if(selecting)return;
            if(e.PropertyName==nameof(TapeSession.HasRegion)||e.PropertyName==nameof(TapeSession.SelectedRow))
                UpdateSelection(Session.SelectedRegion is{} active?[active]:Session.Snapshot!.Layout.Regions.Where(r=>Session.Selection.Contains(r.FileIndex)).ToArray(),Session.SelectedRegion,false);
            if(e.PropertyName!=nameof(TapeSession.Rows)||Session.Snapshot is not{} snapshot)return;
            regions.ItemsSource=snapshot.Layout.Regions;files.ItemsSource=FileRegions();findings.ItemsSource=snapshot.Findings;
            statistics.ItemsSource=snapshot.Statistics.Select(row=>new Metric(row[0],string.Join(" · ",row.Skip(1))));graph.SetSnapshot(snapshot);spiral.Snapshot=snapshot;spiral.Fit();summary.Text=Summary();((TabItem)tabs.Items[3]!).Header=$"Findings ({snapshot.Findings.Count})";
        }
        findings.SelectionChanged+=(_,_)=>{if(findings.SelectedItem is QuickDiskVisualFinding{Start:long a,End:long b})UpdateSelection(Session.Snapshot!.Layout.Regions.Where(r=>r.End>a&&r.Start<b).ToArray(),null,false);};
        findings.DoubleTapped+=(_,_)=>{showMfm.IsChecked=true;graph.FitSelection();tabs.SelectedIndex=0;};
        Session.PropertyChanged+=Changed;Closed+=(_,_)=>Session.PropertyChanged-=Changed;
    }
    private static TableView Table(System.Collections.IEnumerable rows,params (string Caption,string Path,int Width)[] columns)
    {
        var table=new TableView{ItemsSource=rows,Margin=new Thickness(0,8,0,0),SelectionMode=SelectionMode.Multiple};
        foreach(var c in columns)table.Columns.Add(new TableViewColumn{Header=c.Caption,Binding=new Binding(c.Path),Width=new GridLength(c.Width)});
        return table;
    }
}
