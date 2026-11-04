using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
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
        Title="Visual QuickDisk Inspector — read-only";Width=1200;Height=820;MinWidth=800;MinHeight=600;
        var root=new DockPanel{Margin=new Thickness(10)};Content=root;
        var toolbar=new WrapPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        var graph=new QuickDiskForensicView();graph.Bind(Session);
        void Button(string title,Action action){var button=new Button{Content=title,Margin=new Thickness(4),Padding=new Thickness(8,3)};button.Click+=(_,_)=>action();toolbar.Children.Add(button);}
        Button("Zoom +",()=>graph.Zoom(1.5));Button("Zoom −",()=>graph.Zoom(1/1.5));Button("Fit",graph.Fit);Button("Selected region",graph.FitSelected);
        foreach(string format in new[]{"txt","csv","json"})Button("Export "+format.ToUpperInvariant()+"…",async()=>
        {
            await model.Documents.ExecuteAsync(async ct=>
            {
                var dialogs=new DesktopDialogs(this);string? path=await dialogs.PickDiskOutputAsync("Export QuickDisk analysis","quickdisk-analysis."+format);if(path is null)return false;
                await model.Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=System.Text.Encoding.UTF8.GetBytes(Session.Snapshot!.Export(format))},ct);return true;
            });
        });
        Button("Export selected region…",async()=>
        {
            await model.Documents.ExecuteAsync(async ct=>
            {
                if(Session.SelectedRegion is not{} region)throw new InvalidOperationException("Select a region first.");
                var dialogs=new DesktopDialogs(this);string? path=await dialogs.PickDiskOutputAsync("Export selected region","region.bin");if(path is null)return false;
                await model.Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=Session.Snapshot!.Layout.GetBlockBytes(region)},ct);return true;
            });
        });
        var detail=new TextBlock{Text=Session.MapDetail,TextWrapping=Avalonia.Media.TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)};DockPanel.SetDock(detail,Dock.Bottom);root.Children.Add(detail);
        var tabs=new TabControl();root.Children.Add(tabs);
        var surface=new Grid{RowDefinitions=new RowDefinitions("*,Auto")};surface.Children.Add(graph);
        var scroll=new ScrollBar{Orientation=Orientation.Horizontal,AllowAutoHide=false,Height=18};Grid.SetRow(scroll,1);surface.Children.Add(scroll);bool syncing=false;
        void UpdateScroll(){syncing=true;try{scroll.Maximum=Math.Max(0,graph.TotalCells-graph.ViewportLength);scroll.ViewportSize=graph.ViewportLength;scroll.SmallChange=Math.Max(1,graph.ViewportLength/100);scroll.LargeChange=Math.Max(1,graph.ViewportLength*.75);scroll.Value=graph.ViewportStart;}finally{syncing=false;}}
        scroll.PropertyChanged+=(_,e)=>{if(!syncing&&e.Property==RangeBase.ValueProperty)graph.ScrollTo(scroll.Value);};graph.ViewportChanged+=UpdateScroll;UpdateScroll();
        tabs.Items.Add(new TabItem{Header="Disk Surface",Content=surface});
        var regions=Table(Session.Snapshot.Layout.Regions,("Kind","Kind",100),("Offset","Offset",110),("Length","Length",100),("File","FileNumber",65),("Name","Name",230),("Size","Size",80),("Load","LoadText",90),("Exec","ExecuteText",90));
        regions.SelectionChanged+=(_,_)=>{if(regions.SelectedItem is QuickDiskRegion region)Session.SelectRegion(region);};tabs.Items.Add(new TabItem{Header="Regions",Content=regions});
        var files=Table(Session.Rows,("Type","TypeLabel",65),("Name","Name",220),("Size","SizeDecimal",90),("Start","StartLabel",100),("Exec","ExecLabel",100));tabs.Items.Add(new TabItem{Header="Files",Content=files});
        files.SelectionChanged+=(_,_)=>{if(files.SelectedItem is RecordRow row)Session.SetSelection([row]);};
        var findings=Table(Session.Snapshot.Findings,("Severity","Severity",100),("Finding","Description",700));tabs.Items.Add(new TabItem{Header="Findings",Content=findings});
        var statistics=Table(Session.Snapshot.Statistics.Select(row=>new Metric(row[0],string.Join(" · ",row.Skip(1)))),("Property","Property",250),("Value","Value",650));tabs.Items.Add(new TabItem{Header="Statistics",Content=statistics});
        void Changed(object? sender,PropertyChangedEventArgs e)
        {
            detail.Text=Session.MapDetail;graph.InvalidateVisual();
            if(e.PropertyName!=nameof(TapeSession.Rows)||Session.Snapshot is not{} snapshot)return;
            regions.ItemsSource=snapshot.Layout.Regions;files.ItemsSource=Session.Rows;findings.ItemsSource=snapshot.Findings;
            statistics.ItemsSource=snapshot.Statistics.Select(row=>new Metric(row[0],string.Join(" · ",row.Skip(1))));graph.Bind(Session);
        }
        Session.PropertyChanged+=Changed;Closed+=(_,_)=>Session.PropertyChanged-=Changed;
    }
    private static TableView Table(System.Collections.IEnumerable rows,params (string Caption,string Path,int Width)[] columns)
    {
        var table=new TableView{ItemsSource=rows,Margin=new Thickness(0,8,0,0),SelectionMode=SelectionMode.Multiple};
        foreach(var c in columns)table.Columns.Add(new TableViewColumn{Header=c.Caption,Binding=new Binding(c.Path),Width=new GridLength(c.Width)});
        return table;
    }
}
