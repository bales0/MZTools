using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MZTools.Desktop.Controls;

internal static class DskTableBehavior
{
    internal static void Configure(TableView table,Action<Action>? update=null,bool horizontalOnly=false)
    {
        table.Styles.Add(new Style(s=>s.OfType<TableViewCell>()){Setters={new Setter(TableViewCell.BorderBrushProperty,Brushes.Black),new Setter(TableViewCell.BorderThicknessProperty,new Thickness(0,0,horizontalOnly?0:1,1)),new Setter(TableViewCell.PaddingProperty,new Thickness(3,0))}});
        table.Styles.Add(new Style(s=>s.OfType<TableViewColumnHeader>()){Setters={new Setter(TableViewColumnHeader.PaddingProperty,new Thickness(3,0))}});
        table.Styles.Add(new Style(s=>s.OfType<TableViewRow>()){Setters={new Setter(TableViewRow.PaddingProperty,default(Thickness)),new Setter(TableViewRow.MinHeightProperty,20d)}});
        var automatic=table.Columns.Where(c=>c.Width.IsAuto).ToArray();var measured=new Dictionary<TableViewColumn,double>();
        void SizeColumns()
        {
            double Width(string? text)=>new FormattedText(text??"",System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(table.FontFamily),table.FontSize,Brushes.Black).WidthIncludingTrailingWhitespace;
            foreach(var column in automatic)
            {
                // Retain a manual resize; WPF Auto measures both the header and cell contents.
                if(!column.Width.IsAuto&&(!measured.TryGetValue(column,out double prior)||Math.Abs(column.Width.Value-prior)>.01))continue;
                double width=Math.Max(65,Math.Ceiling(Math.Max(Width(column.Header?.ToString()),table.Items.Cast<object>().Select(row=>Width(Value(row,BindingPath(column))?.ToString())).DefaultIfEmpty().Max())+10));
                measured[column]=width;column.Width=new GridLength(width);
            }
        }
        table.PropertyChanged+=(_,e)=>{if(e.Property.Name==nameof(TableView.ItemsSource))SizeColumns();};SizeColumns();
        TableViewColumn? sorted=null;bool descending=false;
        table.AddHandler(InputElement.PointerReleasedEvent,(_,e)=>
        {
            if(e.InitialPressMouseButton!=MouseButton.Left)return;
            if(e.Source is not Visual visual)return;var header=(visual as TableViewColumnHeader)??visual.GetVisualAncestors().OfType<TableViewColumnHeader>().FirstOrDefault();
            if(header?.Column is not{} column||BindingPath(column) is not{} path)return;
            descending=sorted==column&&!descending;sorted=column;var rows=table.Items.Cast<object>().ToArray();var selection=table.SelectedItems!.Cast<object>().ToArray();
            var comparer=Comparer<object?>.Create((a,b)=>a is null?b is null?0:-1:b is null?1:a is IComparable c?c.CompareTo(b):StringComparer.CurrentCulture.Compare(a.ToString(),b.ToString()));
            var ordered=descending?rows.OrderByDescending(row=>Value(row,path),comparer):rows.OrderBy(row=>Value(row,path),comparer);
            void Apply(){table.ItemsSource=ordered.ToArray();foreach(var row in selection)table.SelectedItems!.Add(row);}
            if(update is null)Apply();else update(Apply);
        },RoutingStrategies.Bubble);
        table.KeyDown+=async(_,e)=>
        {
            var primary=OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control;if(e.Key!=Key.C||!e.KeyModifiers.HasFlag(primary)||TopLevel.GetTopLevel(table)?.Clipboard is not{} clipboard)return;
            e.Handled=true;try{await clipboard.SetTextAsync(CopyText(table));}catch(Exception error)when(error is not OutOfMemoryException){ToolTip.SetTip(table,"Clipboard unavailable: "+error.Message);}
        };
    }
    internal static object? Value(object row,string? path)
    {
        if(path is null)return null;if(path.StartsWith('[')&&row is string[] values&&int.TryParse(path.Trim('[',']'),out int index))return values.ElementAtOrDefault(index);
        return row.GetType().GetProperty(path)?.GetValue(row);
    }
    internal static string CopyText(TableView table)
    {
        var columns=table.Columns.Where(c=>c.IsVisible).ToArray();string Cell(object row,TableViewColumn column)=>Value(row,BindingPath(column))?.ToString()??"";
        return string.Join(Environment.NewLine,new[]{string.Join('\t',columns.Select(c=>c.Header?.ToString()))}.Concat(table.SelectedItems!.Cast<object>().Select(row=>string.Join('\t',columns.Select(c=>Cell(row,c))))));
    }
    internal static void WrapColumn(TableViewColumn column,bool wrap=true)
    {
        string? path=BindingPath(column);if(path is null)return;
        column.CellTemplate=new FuncDataTemplate<object>((row,_)=>new TextBlock{Text=row is null?"":Value(row,path)?.ToString(),MinWidth=65,Margin=new Thickness(3),TextWrapping=wrap?TextWrapping.Wrap:TextWrapping.NoWrap});
    }
    private static string? BindingPath(TableViewColumn column)=>column.Binding switch{Binding b=>b.Path,ReflectionBinding b=>b.Path,_=>null};
}
