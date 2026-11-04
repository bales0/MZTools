using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia;
using Avalonia.LogicalTree;
using MZTools.Desktop.Documents;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MZTools.Desktop.Views;

public sealed partial class AudioWorkspace
{
    private void ConfigureWpfAudioLayout()
    {
        AudioGrid.MinHeight=0;
        ConfigureWpfTable(AudioBlocks, row => ((AudioBlockRow)row).Value,
            ["Record","Name","Block","Profile","Channel","ChecksumValid","Recovered","StartSample","EndSample","Inverted","DecodeDetector"]);
        foreach (var table in this.GetLogicalDescendants().OfType<TableView>())
        {
            switch (Avalonia.Automation.AutomationProperties.GetName(table))
            {
                case "Measured PCM levels": ConfigureWpfTable(table,row=>((AudioLevelRow)row).Value);break;
                case "Decoder agreement": ConfigureWpfTable(table,row=>((AudioAgreementRow)row).Value);break;
                case "Pulse duration statistics": ConfigureWpfTable(table,row=>((AudioDistributionRow)row).Value);break;
                case "Audio regions": ConfigureWpfTable(table,row=>((AudioRegionRow)row).Value);break;
                case "Audio findings": ConfigureWpfTable(table,row=>((AudioIssueRow)row).Value);break;
            }
        }
        // The waveform stays visible while long analysis/detail panes scroll independently.
        foreach(var tab in AudioTabs.Items.OfType<TabItem>())
        {
            if(tab.Content is not Control content)continue;
            int minimum=tab.Header?.ToString() switch {"Pulse statistics"=>540,"Recovery"=>650,"Spectrum"=>560,_=>0};
            if(minimum==0)continue;
            content.MinHeight=minimum;tab.Content=null;
            tab.Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        }
    }

    internal static void ConfigureWpfTable<T>(TableView table,Func<object,T> value,string[]? order=null)
    {
        var properties=typeof(T).GetProperties().Where(p=>
        {
            var type=Nullable.GetUnderlyingType(p.PropertyType)??p.PropertyType;
            return type.IsPrimitive || type.IsEnum || type==typeof(string) || type==typeof(decimal);
        }).ToArray();
        order??=[];
        properties=properties.OrderBy(p=>Array.IndexOf(order,p.Name) is int index && index>=0?index:order.Length+Array.IndexOf(properties,p)).ToArray();
        table.Columns.Clear();
        foreach(var property in properties)
        {
            var column=new TableViewColumn{Header=Regex.Replace(property.Name,"(?<=[a-z])(?=[A-Z])"," "),Width=new GridLength(property.Name is "Description" or "Evidence" or "Definition" or "PulseMeasurement" or "ReferenceName"?320:property.Name=="Name"?150:110)};
            column.CellTemplate=new FuncDataTemplate<object>((row,_)=>
            {
                object? cell=property.GetValue(value(row));
                if(cell is bool flag)return new CheckBox{IsChecked=flag,Focusable=false,IsHitTestVisible=false,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center};
                string text=cell is double or float?Convert.ToDouble(cell,CultureInfo.CurrentCulture).ToString("0.####",CultureInfo.CurrentCulture):cell is long or int or ushort?Convert.ToInt64(cell,CultureInfo.CurrentCulture).ToString("N0",CultureInfo.CurrentCulture):cell?.ToString()??"";
                return new TextBlock{Text=text,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center};
            });
            table.Columns.Add(column);
        }
    }
}
