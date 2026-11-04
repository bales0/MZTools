using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Controls;

// Uses the same parsed report tables as WPF ReportView, including its export formats.
public sealed class DskReportView:UserControl
{
    private readonly TextBlock heading=new(){FontSize=14,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly TabControl tabs=new();
    private readonly TextBlock summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8)};
    private bool compactHeader=true;
    public bool CompactHeader {get=>compactHeader;set{compactHeader=value;heading.FontSize=value?14:18;summary.IsVisible=!value;}}
    private ReportPresentation presentation=ReportPresentation.FromText("");
    public static readonly StyledProperty<string?> TextProperty=AvaloniaProperty.Register<DskReportView,string?>(nameof(Text));
    public string? Text {get=>GetValue(TextProperty);set=>SetValue(TextProperty,value);}
    public DskReportView()
    {
        var root=new DockPanel();Content=root;var header=new DockPanel{Margin=new Thickness(4)};DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var export=new Button{Content="Export report…",Padding=new Thickness(8,3),Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Top};DockPanel.SetDock(export,Dock.Right);header.Children.Add(export);var labels=new StackPanel();labels.Children.Add(heading);labels.Children.Add(summary);header.Children.Add(labels);root.Children.Add(tabs);CompactHeader=true;
        export.Click+=async(_,_)=>await ExportAsync();PropertyChanged+=(_,e)=>{if(e.Property==TextProperty)Show();};Show();
    }
    private void Show()
    {
        string? selected=(tabs.SelectedItem as TabItem)?.Header as string;presentation=ReportPresentation.FromText(Text??"");heading.Text=presentation.Heading;summary.Text=presentation.Summary;ToolTip.SetTip(heading,presentation.Heading);tabs.Items.Clear();
        foreach(var table in presentation.Tables)
        {
            var grid=new TableView{ItemsSource=table.Rows,SelectionMode=SelectionMode.Multiple};
            for(int i=0;i<table.Columns.Count;i++){var column=new TableViewColumn{Header=table.Columns[i],Binding=new Binding($"[{i}]"),Width=compactHeader&&i==0&&table.Columns.Count<=3?new GridLength(220):table.Columns.Count>3?GridLength.Auto:i==table.Columns.Count-1?new GridLength(1,GridUnitType.Star):GridLength.Auto};DskTableBehavior.WrapColumn(column);grid.Columns.Add(column);}DskTableBehavior.Configure(grid,horizontalOnly:true);
            var tab=new TabItem{Header=table.Title,Content=grid};tabs.Items.Add(tab);if(table.Title==selected)tabs.SelectedItem=tab;
        }
        if(tabs.SelectedIndex<0)tabs.SelectedIndex=0;
    }
    internal string ExportContent(string format)
    {
        if(format=="txt")return presentation.ExportText;if(format=="json")return JsonSerializer.Serialize(presentation,new JsonSerializerOptions{WriteIndented=true});
        var output=new StringBuilder();static string Quote(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
        foreach(var table in presentation.Tables){output.AppendLine(string.Join(",",new[]{"Table"}.Concat(table.Columns).Select(Quote)));foreach(var row in table.Rows)output.AppendLine(string.Join(",",new[]{table.Title}.Concat(row).Select(Quote)));}return output.ToString();
    }
    private async Task ExportAsync()
    {
        if(TopLevel.GetTopLevel(this) is not Window owner||DataContext is not ShellViewModel model)return;
        await model.Documents.ExecuteAsync(async ct=>{string? path=await new DesktopDialogs(owner).PickDiskOutputAsync("Export report","report.txt");if(path is null)return false;string format=Path.GetExtension(path).ToLowerInvariant() switch{".json"=>"json",".csv"=>"csv",_=>"txt"};await model.Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=Encoding.UTF8.GetBytes(ExportContent(format))},ct);return true;});
    }
}
