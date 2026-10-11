using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MZTools;

// Text is a compatibility/export property, never a text-mode report surface.
public sealed class ReportView : UserControl
{
    private readonly TextBlock heading=new() { FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock summary=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8) };
    private readonly TabControl tabs=new();
    private bool compactHeader;
    public bool SingleLineHeading {
        get=>heading.TextWrapping==TextWrapping.NoWrap;
        set { heading.TextWrapping=value?TextWrapping.NoWrap:TextWrapping.Wrap; heading.TextTrimming=value?TextTrimming.CharacterEllipsis:TextTrimming.None; }
    }
    public bool CompactHeader {
        get=>compactHeader;
        set { compactHeader=value; heading.FontSize=value?14:18; summary.Visibility=value?Visibility.Collapsed:Visibility.Visible; Show(model); }
    }
    private ReportPresentation model=ReportPresentation.FromText("");
    public string Text { get=>model.ExportText; set=>Show(ReportPresentation.FromText(value)); }
    public void Clear()=>Text="";
    public ReportView()
    {
        var root=new DockPanel(); Content=root;
        var header=new DockPanel { Margin=new Thickness(4) }; DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
        var export=new Button { Content="Export report…",Padding=new Thickness(8,3,8,3),Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Top };
        DockPanel.SetDock(export,Dock.Right); header.Children.Add(export); export.Click+=(_,_)=>Export();
        var labels=new StackPanel(); labels.Children.Add(heading); labels.Children.Add(summary); header.Children.Add(labels);
        root.Children.Add(tabs); Show(model);
    }
    internal ReportPresentation Model=>model;
    internal event Action<ReportPresentation>? ReportChanged;
    internal void Show(ReportPresentation report)
    {
        string? old=(tabs.SelectedItem as TabItem)?.Header as string;
        model=report; heading.Text=report.Heading; heading.ToolTip=report.Heading; summary.Text=report.Summary;
        tabs.Items.Clear();
        foreach(var table in report.Tables) {
            var grid=new DataGrid { ItemsSource=table.Rows,AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,
                CanUserResizeColumns=true,CanUserSortColumns=true,SelectionMode=DataGridSelectionMode.Extended,
                SelectionUnit=DataGridSelectionUnit.CellOrRowHeader,ClipboardCopyMode=DataGridClipboardCopyMode.IncludeHeader,
                EnableRowVirtualization=true,EnableColumnVirtualization=true,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility=DataGridHeadersVisibility.Column,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            grid.Resources[SystemColors.InactiveSelectionHighlightBrushKey]=SystemColors.HighlightBrush;
            grid.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=SystemColors.HighlightTextBrush;
            var rowStyle=new Style(typeof(DataGridRow));
            foreach(var color in new[] {("Warning",Colors.DarkOrange),("Attention",Colors.Firebrick)}) {
                var trigger=new DataTrigger { Binding=new Binding("[0]"),Value=color.Item1 };
                trigger.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.SemiBold));
                trigger.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(color.Item2)));
                trigger.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(3,0,0,0))); rowStyle.Triggers.Add(trigger);
            }
            grid.RowStyle=rowStyle;
            for(int i=0;i<table.Columns.Count;i++) {
                var style=new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));
                style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(3)));
                grid.Columns.Add(new DataGridTextColumn { Header=table.Columns[i],Binding=new Binding($"[{i}]"),MinWidth=80,
                    Width=compactHeader && i==0 && table.Columns.Count<=3?new DataGridLength(220):table.Columns.Count>3?DataGridLength.Auto:i==table.Columns.Count-1?new DataGridLength(1,DataGridLengthUnitType.Star):DataGridLength.SizeToHeader,ElementStyle=style });
            }
            var tab=new TabItem { Header=table.Title,Content=grid }; tabs.Items.Add(tab);
            if(table.Title==old) tabs.SelectedItem=tab;
        }
        if(tabs.SelectedIndex<0) tabs.SelectedIndex=0;
        ReportChanged?.Invoke(report);
    }
    private void Export()
    {
        var dialog=new SaveFileDialog { Filter="Text report|*.txt|CSV report|*.csv|JSON report|*.json",FileName="report.txt",AddExtension=true };
        if(dialog.ShowDialog(Window.GetWindow(this))!=true) return;
        try { File.WriteAllText(dialog.FileName,ExportContent(dialog.FilterIndex)); }
        catch(Exception ex) { MessageBox.Show(Window.GetWindow(this),ex.Message,"Export report",MessageBoxButton.OK,MessageBoxImage.Error); }
    }
    internal string ExportContent(int format)
    {
        if(format==1) return model.ExportText;
        if(format==3) return JsonSerializer.Serialize(model,new JsonSerializerOptions { WriteIndented=true });
        var output=new StringBuilder();
        static string Quote(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
        foreach(var table in model.Tables) {
            output.AppendLine(string.Join(",",new[] {"Table"}.Concat(table.Columns).Select(Quote)));
            foreach(var row in table.Rows) output.AppendLine(string.Join(",",new[] {table.Title}.Concat(row).Select(Quote)));
        }
        return output.ToString();
    }
}
