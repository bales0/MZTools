using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Views;

public sealed class HexViewWindow:Window
{
    internal static readonly FontFamily DumpFont=new("Consolas, Cascadia Mono, Courier New, DejaVu Sans Mono");
    internal static readonly IBrush PrefixBrush=new SolidColorBrush(Color.FromArgb(0x55,0xff,0x55,0x55));
    internal static readonly IBrush CrcBrush=new SolidColorBrush(Color.FromArgb(0x55,0x42,0x85,0xf4));
    internal static readonly IBrush TrailingBrush=new SolidColorBrush(Color.FromRgb(0x99,0x99,0x99));
    internal sealed record Row(byte[] Bytes,int Offset,int Count,string Section)
    {
        public string Address=>$"{Offset:X8}";
        public string Hex=>BitConverter.ToString(Bytes,Offset,Count).Replace('-',' ');
        public string Ascii=>Characters(false);
        public string Shascii=>Characters(true);
        public bool IsQdf=>Section=="QDF header (derived)";
        private string Characters(bool sharp)=>new(Enumerable.Range(Offset,Count).Select(i=>Display(sharp?SharpMzEncoding.FromSHASCII(Bytes[i]):Bytes[i])).ToArray());
        public IBrush Foreground=>Section=="Trailing"?TrailingBrush:Brushes.Black;
        internal IBrush ByteBackground(int index)=>IsQdf&&index<4?PrefixBrush:IsQdf&&index>=Bytes.Length-2?CrcBrush:Brushes.Transparent;
    }
    private static char Display(byte value)=>char.IsControl((char)value)?'.':(char)value;
    internal static Row[] RecordRows(TapeRecord record)
    {
        byte[] header=record.GetSerializedHeader(),data=record.Body.MzfBody,trailing=record.Body.TrailingData??[];
        byte[] bytes=header.Concat(data).Concat(trailing).ToArray();
        return Rows(bytes,0,header.Length,"MZF header").Concat(Rows(bytes,header.Length,data.Length,"Data"))
            .Concat(Rows(bytes,header.Length+data.Length,trailing.Length,"Trailing")).ToArray();
    }
    internal static Row[] DisplayRows(TapeRecord record)
    {
        var recordRows=RecordRows(record);byte[] qdf=SharpQdFrameCodec.EncodeHeaderFrame(record.Header);
        return recordRows.Where(r=>r.Section=="MZF header").Concat(Rows(qdf,0,qdf.Length,"QDF header (derived)"))
            .Concat(recordRows.Where(r=>r.Section!="MZF header")).ToArray();
    }
    private static IEnumerable<Row> Rows(byte[] bytes,int start,int length,string section)
    {for(int offset=start;offset<start+length;offset+=16)yield return new(bytes,offset,Math.Min(16,start+length-offset),section);}
    internal HexViewWindow(RecordRow row):this([])
    {
        var panel=new DockPanel();var legend=new TextBlock{FontFamily=DumpFont,FontSize=12,Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap};
        legend.Inlines!.Add(new Run("QDF (derived): "));legend.Inlines.Add(new Run(" prefix / block type / length "){Background=PrefixBrush});legend.Inlines.Add(new Run("  "));legend.Inlines.Add(new Run(" CRC "){Background=CrcBrush});legend.Inlines.Add(new Run("  Trailing data"){Foreground=TrailingBrush});
        ToolTip.SetTip(legend,"QDF prefix: bytes 0–3; calculated header CRC: bytes 68–69. Trailing bytes follow the declared MZF payload; no source bytes are changed.");
        DockPanel.SetDock(legend,Dock.Top);panel.Children.Add(legend);panel.Children.Add(Table(DisplayRows(row.Record)));Content=panel;
    }
    internal HexViewWindow(byte[] bytes)
    {
        Title="Hex Browser";Width=1000;Height=500;MinWidth=500;MinHeight=250;
        MZTools.Desktop.Platform.DesktopWindowSizing.FitToWorkingArea(this);
        Content=Table(Rows(bytes,0,bytes.Length,"Raw"));
    }
    internal static TextBlock ByteCell(Row row,int representation)
    {
        var text=new TextBlock{FontFamily=DumpFont,FontSize=12,TextWrapping=TextWrapping.NoWrap,Foreground=row.Foreground};
        for(int column=0;column<16;column++)
        {
            int index=row.Offset+column;bool present=column<row.Count;
            string value=representation==0?present?$"{row.Bytes[index]:X2} ":"   ":present?Display(representation==2?SharpMzEncoding.FromSHASCII(row.Bytes[index]):row.Bytes[index]).ToString():" ";
            text.Inlines!.Add(new Run(value){Background=present?row.ByteBackground(index):Brushes.Transparent,Foreground=row.Foreground});
            if(representation==0&&column%4==3)text.Inlines.Add(new Run(" "){Foreground=row.Foreground});
        }
        ToolTip.SetTip(text,row.Section=="Trailing"?"Data beyond the size declared in the MZF header. Clear Preserve trailing data before Save As to remove it.":row.IsQdf?"Derived QDF header: red prefix bytes 0–3; blue CRC bytes 68–69.":row.Section);
        return text;
    }
    private static TableView Table(IEnumerable<Row> rows)
    {
        const double glyph=8;var table=new TableView{Name="HexDumpTable",FontFamily=DumpFont,FontSize=12,Margin=new Thickness(8),ItemsSource=rows.ToArray(),CanUserResizeColumns=false};
        table.Columns.Add(new(){Header="Region",Binding=new Binding("Section"),Width=new GridLength(150),CellTemplate=new FuncDataTemplate<Row>((row,_)=>new TextBlock{Text=row?.Section,FontFamily=DumpFont,FontSize=12,Foreground=row?.Foreground??Brushes.Black,TextWrapping=TextWrapping.NoWrap})});
        table.Columns.Add(new(){Header="Offset",Binding=new Binding("Address"),Width=new GridLength(90),CellTemplate=new FuncDataTemplate<Row>((row,_)=>new TextBlock{Text=row?.Address,FontFamily=DumpFont,FontSize=12,Foreground=row?.Foreground??Brushes.Black})});
        foreach(var(column,title,path,characters)in new[]{(0,"Hex","Hex",52),(1,"ASCII","Ascii",16),(2,"SHASCII (EU)","Shascii",16)})
            table.Columns.Add(new(){Header=title,Binding=new Binding(path),Width=new GridLength(characters*glyph+20),CellTemplate=new FuncDataTemplate<Row>((row,_)=>row is null?new TextBlock():ByteCell(row,column))});
        ScrollViewer.SetHorizontalScrollBarVisibility(table,Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return table;
    }
}
