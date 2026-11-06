using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Platform;

namespace MZTools.Desktop.Views;

public sealed class BatchFileDetailsWindow : Window
{
    private sealed record ContentRow(int Number,string Name,long Size,string Type,string Load,string Exec,int? User,string Profile,string Compression,long Trailing,string Attributes);
    internal BatchFileDetailsWindow(BatchRow row)
    {
        Title="Batch File Details — "+Path.GetFileName(row.Input);Width=1120;Height=700;MinWidth=720;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;DesktopWindowSizing.FitToWorkingArea(this);
        var root=new Grid{RowDefinitions=new("Auto,*,Auto"),Margin=new Thickness(16),RowSpacing=12};Content=root;
        var header=new StackPanel{Spacing=4};root.Children.Add(header);header.Children.Add(new TextBlock{Text=Path.GetFileName(row.Input),FontSize=22,FontWeight=FontWeight.SemiBold});header.Children.Add(new TextBlock{Text=row.Input,TextWrapping=TextWrapping.Wrap});var badges=new WrapPanel();header.Children.Add(badges);
        foreach(string text in new[]{"Format: "+Display(row.DetectedFormat),"Status: "+Display(row.Result),"Integrity: "+row.Integrity,"Files / programs: "+(row.Catalog?.FileCount??row.Contents.Count)})badges.Children.Add(new Border{Padding=new Thickness(8,4),Margin=new Thickness(0,0,8,4),Background=Brushes.LightGray,Child=new TextBlock{Text=text}});
        header.Children.Add(new TextBlock{Text=row.Reason,TextWrapping=TextWrapping.Wrap});var tabs=new TabControl{Name="BatchDetailTabs"};Grid.SetRow(tabs,1);root.Children.Add(tabs);
        var contents=Table(row.Contents.Select(c=>new ContentRow(c.Number,c.Name,c.Size,c.Type,c.Load is{} load?$"0x{load:X4}":"—",c.Exec is{} exec?$"0x{exec:X4}":"—",c.User,c.Profile,c.Compression,c.TrailingBytes,c.Attributes)).ToArray(),"BatchContents",("#","Number",45),("Name","Name",200),("Size (B)","Size",100),("Type (hex)","Type",85),("Load (hex)","Load",95),("Exec (hex)","Exec",95));
        if(row.Contents.Any(c=>c.User!=null))Column(contents,"User","User",60);if(row.Contents.Any(c=>c.Profile.Length>0))Column(contents,"Tape profile","Profile",115);if(row.Contents.Any(c=>c.Compression.Length>0))Column(contents,"Compression","Compression",170);if(row.Contents.Any(c=>c.TrailingBytes>0))Column(contents,"Trailing (B)","Trailing",100);if(row.Contents.Any(c=>c.Attributes.Length>0))Column(contents,"Attributes","Attributes",160);
        tabs.Items.Add(new TabItem{Header="Contents",Content=row.Contents.Count>0?contents:new TextBlock{Text="No file/program list is available for this source. See Properties and Messages for the inspection result.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(16)}});
        var fields=new List<BatchDetailField>{new("Operation",Enum.TryParse<BatchOperation>(row.Operation,out var op)?BatchMediaPolicy.Label(op):row.Operation),new("Filesystem",Display(row.Filesystem)),new("Output",Display(row.Output))};
        if(row.Catalog is{} catalog)
        {
            fields.AddRange([new("Capacity",$"{catalog.Capacity:N0} B"),new("Used data",$"{catalog.UsedBytes:N0} B")]);
            if(catalog.Geometry.Length>0)fields.AddRange([new("Geometry (tracks × sides)",catalog.Geometry),new("Layout / DPB",Display(catalog.Variant)),new("Free space",$"{catalog.FreeBytes:N0} B"),new("Boot / system",Display(catalog.BootType)),new("Boot profile",Display(catalog.BootProfile)),new("Bootable state",catalog.BootableState),new("Decoded sectors",catalog.SectorCount.ToString()),new("CRC error sectors",catalog.CrcErrorSectors.ToString()),new("Weak bits",catalog.WeakBits?"Present":"None reported"),new("Bitrate / RPM",Display(catalog.BitrateRpm))]);
            fields.AddRange([new("Analyzer errors",catalog.AnalyzerErrors.ToString()),new("Unsafe findings",catalog.AnalyzerUnsafe.ToString()),new("Analyzer warnings",catalog.AnalyzerWarnings.ToString())]);
        }
        fields.AddRange(row.DetailFields);fields.Add(new("Source SHA-256",Display(row.Catalog?.Sha256??row.SourceHash)));
        tabs.Items.Add(new TabItem{Header="Properties",Content=Table(fields,"BatchProperties",("Property","Property",220),("Value","Value",650))});
        var messages=new List<BatchDetailField>();foreach(var (category,text) in new[]{("Result",row.Reason),("Warning",row.Warnings),("Error",row.Error)})foreach(string line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct())messages.Add(new(category,line));
        var messageTable=Table(messages,"BatchMessages",("Category","Property",100),("Message","Value",780));DskTableBehavior.WrapColumn(messageTable.Columns[1]);tabs.Items.Add(new TabItem{Header=$"Messages ({messages.Count})",Content=messageTable});
        var checks=Table(row.IntegrityChecks,"BatchIntegrityChecks",("File / scope","Subject",200),("Check","Check",180),("Result","Result",110),("Details / limits","Details",500));DskTableBehavior.WrapColumn(checks.Columns[3]);tabs.Items.Add(new TabItem{Header=$"Integrity checks ({row.IntegrityChecks.Count})",Content=row.IntegrityChecks.Count>0?checks:new TextBlock{Text="No individual integrity checks are available for this result. See Messages for the inspection outcome and its limits.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(16)}});
        tabs.Items.Add(new TabItem{Header="Technical details",Content=new ReportViewer{Text=row.DetailedReport}});
        tabs.SelectedIndex=row.Contents.Count==0?messages.Any(m=>m.Property=="Error")?2:1:row.IntegrityChecks.Count>0?3:0;
        var footer=new DockPanel();Grid.SetRow(footer,2);root.Children.Add(footer);var close=new Button{Content="Close",IsCancel=true,MinWidth=100};close.Click+=(_,_)=>Close();DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);footer.Children.Add(new TextBlock{Text="Click a column header to sort. Select rows and press Ctrl / Cmd+C to copy with headers.",TextWrapping=TextWrapping.Wrap});
    }
    private static string Display(string? text)=>string.IsNullOrWhiteSpace(text)?"—":text;
    private static void Column(TableView table,string title,string path,int width)=>table.Columns.Add(new(){Header=title,Binding=new Binding(path),Width=new GridLength(width)});
    private static TableView Table(IEnumerable rows,string name,params(string Title,string Path,int Width)[] columns)
    {
        var table=new TableView{Name=name,ItemsSource=rows,SelectionMode=SelectionMode.Multiple,AutoScrollToSelectedItem=true};foreach(var c in columns)Column(table,c.Title,c.Path,c.Width);DskTableBehavior.Configure(table);return table;
    }
}
