using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

/// <summary>Read-only inspector over the same Core snapshot and shared editor selection.</summary>
public sealed class DiskVisualWindow:Window
{
    private readonly DiskSession session;
    private readonly DiskMap left,right;
    private readonly TextBlock detail=new(){TextWrapping=Avalonia.Media.TextWrapping.Wrap};
    private Window? floating;
    private TableView? sectorRows,issueRows,trackRows;
    private sealed record Metric(string Property,string Value);
    public DiskVisualWindow(ShellViewModel model)
    {
        session=model.ActiveDisk??throw new InvalidOperationException("Open a disk first.");
        Title="Visual "+session.Snapshot.SourceFormat+" Inspector — read-only";Width=1260;Height=850;MinWidth=800;MinHeight=600;
        DataContext=model;var root=new DockPanel{Margin=new Thickness(10)};Content=root;
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Top);root.Children.Add(actions);
        left=new DiskMap{Session=session,Mode=1};right=new DiskMap{Session=session,Mode=2};
        var side=new ComboBox{ItemsSource=new[]{"All sides","Side 0","Side 1"},SelectedIndex=0,Width=100,Margin=new Thickness(4)};
        side.SelectionChanged+=(_,_)=>{left.IsVisible=side.SelectedIndex!=2;right.IsVisible=side.SelectedIndex!=1&&session.Snapshot.Sides>1;};actions.Children.Add(side);
        void Button(string caption,Action action){var b=new Button{Content=caption,Margin=new Thickness(4),Padding=new Thickness(8,3)};b.Click+=(_,_)=>action();actions.Children.Add(b);}
        Button("Zoom +",()=>Zoom(1.2));Button("Zoom −",()=>Zoom(1/1.2));Button("Fit",()=>{left.Fit();right.Fit();});Button("Selected sector",()=>{left.FitSelection();right.FitSelection();});
        var floatingCheck=new CheckBox{Content="Floating selection details",Margin=new Thickness(8,0)};actions.Children.Add(floatingCheck);
        floatingCheck.IsCheckedChanged+=(_,_)=>
        {
            if(floatingCheck.IsChecked!=true){floating?.Close();return;}
            if(floating is not null){floating.Activate();return;}
            var report=new ReportViewer{Text=session.Detail,Margin=new Thickness(10)};floating=new Window{Title="Selection details",Width=520,Height=600,Content=report};
            void Update(object? s,PropertyChangedEventArgs e)=>report.Text=session.Detail;
            session.PropertyChanged+=Update;floating.Closed+=(_,_)=>{session.PropertyChanged-=Update;floating=null;floatingCheck.IsChecked=false;};floating.Show(this);
        };
        foreach(string format in new[]{"txt","csv","json"})Button("Export "+format.ToUpperInvariant()+"…",async()=>
        {
            await model.Documents.ExecuteAsync(async ct=>
            {
                var dialogs=new DesktopDialogs(this);string? path=await dialogs.PickDiskOutputAsync("Export visual analysis","disk-analysis."+format);if(path is null)return false;
                await model.Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=System.Text.Encoding.UTF8.GetBytes(DskVisualReport.Export(session.Snapshot,format))},ct);return true;
            });
        });
        var bottom=new TextBlock{Text=session.Snapshot.SurfaceText,Margin=new Thickness(0,6,0,0),TextWrapping=Avalonia.Media.TextWrapping.Wrap};DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var tabs=new TabControl();root.Children.Add(tabs);
        var surfaces=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=8};surfaces.Children.Add(left);Grid.SetColumn(right,1);surfaces.Children.Add(right);right.IsVisible=session.Snapshot.Sides>1;
        if(session.Snapshot.Sides<2)surfaces.ColumnDefinitions[1].Width=new GridLength(0);
        side.SelectionChanged+=(_,_)=>{surfaces.ColumnDefinitions[0].Width=new GridLength(side.SelectedIndex==2?0:1,GridUnitType.Star);surfaces.ColumnDefinitions[1].Width=new GridLength(side.SelectedIndex==1||session.Snapshot.Sides<2?0:1,GridUnitType.Star);};
        tabs.Items.Add(new TabItem{Header="Disk Surface",Content=surfaces});
        var sectors=Table(session.Sectors,("Track","Track",65),("#","Descriptor",45),("CHRN","Chrn",120),("ST1/ST2","Status",80),("Bytes","Bytes",80),("Role","Role",150),("Files","Files",250));
        sectorRows=sectors;
        sectors.SelectionChanged+=(_,_)=>{if(sectors.SelectedItem is DiskSectorRow row)Select(row,KeyModifiers.None);};
        var trackPanel=new Grid{RowDefinitions=new RowDefinitions("*,Auto"),RowSpacing=8};trackPanel.Children.Add(sectors);Grid.SetRow(detail,1);trackPanel.Children.Add(detail);tabs.Items.Add(new TabItem{Header="Track / Sector",Content=trackPanel});
        var issues=Table(session.Findings,("Severity","Severity",90),("Code","Code",170),("Location","Location",130),("Finding","Description",600));issueRows=issues;tabs.Items.Add(new TabItem{Header="Issues",Content=issues});
        tabs.Items.Add(new TabItem{Header="Statistics",Content=Table(new[]{new Metric("Container",session.Snapshot.SourceFormat),new("Cylinders",session.Snapshot.Cylinders.ToString()),new("Sides",session.Snapshot.Sides.ToString()),new("Tracks",session.Snapshot.Tracks.Count.ToString()),new("Sectors",session.Sectors.Count.ToString()),new("Filesystem",session.Snapshot.FileSystem),new("Capacity",session.Capacity)},("Property","Property",220),("Value","Value",600))});
        trackRows=Table(session.Snapshot.Tracks,("Track","PhysicalIndex",80),("Cylinder","DeclaredCylinder",80),("Side","DeclaredSide",70),("Sectors","SectorCount",90),("Bytes","StoredBytes",100),("Issues","IssueCount",90),("Sector ID order","DescriptorSequence",500));tabs.Items.Add(new TabItem{Header="Tracks",Content=trackRows});
        var physical=new DiskPhysicalPanel{DataContext=model};tabs.Items.Add(new TabItem{Header="Unrolled MFM detail",Content=physical});
        left.SectorSelected+=Select;right.SectorSelected+=Select;
        session.PropertyChanged+=Changed;Closed+=(_,_)=>{session.PropertyChanged-=Changed;floating?.Close();};
    }
    private static TableView Table(System.Collections.IEnumerable rows,params (string Caption,string Path,int Width)[] columns)
    {
        var table=new TableView{ItemsSource=rows,Margin=new Thickness(0,8,0,0),SelectionMode=SelectionMode.Multiple};
        foreach(var c in columns)table.Columns.Add(new TableViewColumn{Header=c.Caption,Binding=new Binding(c.Path),Width=new GridLength(c.Width)});
        return table;
    }
    private void Zoom(double factor){left.Zoom(factor);right.InvalidateVisual();}
    private void Select(DiskSectorRow row,KeyModifiers modifiers)
    {
        session.SelectSector(row,modifiers.HasFlag(OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control),modifiers.HasFlag(KeyModifiers.Shift));
        left.InvalidateVisual();right.InvalidateVisual();detail.Text=session.Detail;
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {
        left.InvalidateVisual();right.InvalidateVisual();detail.Text=session.Detail;
        if(e.PropertyName==nameof(DiskSession.Sectors)){if(sectorRows is not null)sectorRows.ItemsSource=session.Sectors;if(trackRows is not null)trackRows.ItemsSource=session.Snapshot.Tracks;}
        if(e.PropertyName is nameof(DiskSession.Sectors) or nameof(DiskSession.Findings)){if(issueRows is not null)issueRows.ItemsSource=session.Findings;}
    }
}
