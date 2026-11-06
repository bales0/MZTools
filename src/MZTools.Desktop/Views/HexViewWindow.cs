using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Views;

public sealed class HexViewWindow:Window
{
    internal static readonly FontFamily DumpFont=new("Consolas, Cascadia Mono, Courier New, DejaVu Sans Mono");
    internal static readonly IBrush PrefixBrush=new SolidColorBrush(Color.FromArgb(0x55,0xff,0x55,0x55));
    internal static readonly IBrush CrcBrush=new SolidColorBrush(Color.FromArgb(0x55,0x42,0x85,0xf4));
    internal static readonly IBrush TrailingBrush=new SolidColorBrush(Color.FromRgb(0x99,0x99,0x99));
    internal sealed record Row(byte[] Bytes,int Offset,int Count,string Section,ushort? Load=null,int PayloadOffset=0)
    {
        public string Address=>$"{Offset:X8}";
        public string RamAddress=>HexPositions.Ram(Load,PayloadOffset);
        public string Hex=>BitConverter.ToString(Bytes,Offset,Count).Replace('-',' ');
        public string Ascii=>Characters(false);
        public string Shascii=>Characters(true);
        public bool IsQdf=>Section=="QDF header (derived)";
        private string Characters(bool sharp)=>new(Enumerable.Range(Offset,Count).Select(i=>HexPositions.Character(Bytes[i],sharp)).ToArray());
        public IBrush Foreground=>Section=="Trailing"?TrailingBrush:Brushes.Black;
        internal IBrush ByteBackground(int index)=>IsQdf&&index<4?PrefixBrush:IsQdf&&index>=Bytes.Length-2?CrcBrush:Brushes.Transparent;
    }
    internal static Row[] RecordRows(TapeRecord record)
    {
        byte[] header=record.GetSerializedHeader(),data=record.Body.MzfBody,trailing=record.Body.TrailingData??[];
        byte[] bytes=header.Concat(data).Concat(trailing).ToArray();
        return Rows(bytes,0,header.Length,"MZF header").Concat(Rows(bytes,header.Length,data.Length,"Data",record.Header.MzfStart))
            .Concat(Rows(bytes,header.Length+data.Length,trailing.Length,"Trailing")).ToArray();
    }
    internal static Row[] DisplayRows(TapeRecord record,bool showQdfHeader=false)
    {
        var recordRows=RecordRows(record);if(!showQdfHeader)return recordRows;byte[] qdf=SharpQdFrameCodec.EncodeHeaderFrame(record.Header);
        return recordRows.Where(r=>r.Section=="MZF header").Concat(Rows(qdf,0,qdf.Length,"QDF header (derived)"))
            .Concat(recordRows.Where(r=>r.Section!="MZF header")).ToArray();
    }
    private static IEnumerable<Row> Rows(byte[] bytes,int start,int length,string section,ushort? load=null)
    {for(int offset=start;offset<start+length;offset+=16)yield return new(bytes,offset,Math.Min(16,start+length-offset),section,load,offset-start);}
    private readonly TextBlock position=new(){Name="HexCursorPosition",Margin=new Thickness(8),FontFamily=DumpFont,FontSize=12,Text="Select a byte in HEX / ASCII / MZASCII."};
    private readonly HashSet<(Row Row,int Representation,TextBlock Text)> visible=[];
    internal static HexViewWindow Region(TapeSession session,QuickDiskRegion region)
    {
        var layout=session.Snapshot?.Layout??throw new InvalidOperationException("No QuickDisk snapshot.");
        ushort? load=region.Kind==QuickDiskRegionKind.Payload&&region.FileIndex>=0?region.Load:null;
        var window=new HexViewWindow(layout.GetBlockBytes(region),load,layout.IsPhysical?0:region.Start);
        window.Title+=$" — {region.Kind}, {region.Offset} {layout.Unit}";return window;
    }
    private Row? selectedRow;private int selectedByte;private Row[] displayed=[];
    internal void SelectByte(Row row,int column)
    {
        if(row.Count==0)return;selectedRow=row;selectedByte=Math.Clamp(column,0,row.Count-1);
        position.Text=$"Offset 0x{row.Offset+selectedByte:X8} · RAM {HexPositions.Ram(row.Load,row.PayloadOffset+selectedByte)} · Byte 0x{row.Bytes[row.Offset+selectedByte]:X2} · HEX / ASCII / MZASCII";
        foreach(var cell in visible.ToArray())Paint(cell.Row,cell.Representation,cell.Text);
    }
    private void Paint(Row row,int representation,TextBlock text)
    {
        int column=0,index=0;foreach(var run in text.Inlines!.OfType<Run>())
        {
            if(representation==0&&index++%5==4)continue;
            bool selected=ReferenceEquals(row,selectedRow)&&column==selectedByte;
            run.Background=selected?Brushes.DodgerBlue:column<row.Count?row.ByteBackground(row.Offset+column):Brushes.Transparent;
            run.Foreground=selected?Brushes.White:row.Foreground;column++;
        }
    }
    private Control Present(IEnumerable<Row> rows,Control? legend=null)
    {
        displayed=rows.ToArray();var root=new DockPanel();DockPanel.SetDock(position,Dock.Bottom);root.Children.Add(position);
        if(legend is not null){DockPanel.SetDock(legend,Dock.Top);root.Children.Add(legend);}root.Children.Add(Table(displayed));return root;
    }
    internal HexViewWindow(RecordRow row,bool showQdfHeader=false):this()
    {
        if(!showQdfHeader){Content=Present(RecordRows(row.Record));return;}
        var legend=new TextBlock{FontFamily=DumpFont,FontSize=12,Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap};
        legend.Inlines!.Add(new Run("QDF (derived): "));legend.Inlines.Add(new Run(" prefix / block type / length "){Background=PrefixBrush});legend.Inlines.Add(new Run("  "));legend.Inlines.Add(new Run(" CRC "){Background=CrcBrush});legend.Inlines.Add(new Run("  Trailing data"){Foreground=TrailingBrush});
        ToolTip.SetTip(legend,"QDF prefix: bytes 0–3; calculated header CRC: bytes 68–69. Trailing bytes follow the declared MZF payload; no source bytes are changed.");
        Content=Present(DisplayRows(row.Record,true),legend);
    }
    private HexViewWindow()
    {
        Title="Hex Browser";Width=1100;Height=500;MinWidth=500;MinHeight=250;
        MZTools.Desktop.Platform.DesktopWindowSizing.FitToWorkingArea(this);
    }
    internal HexViewWindow(byte[] bytes,ushort? load=null,long sourceOffset=0):this()
    {
        Content=Present(Rows(bytes,0,bytes.Length,"Raw",load));
        if(sourceOffset!=0)Title+=$" — region at 0x{sourceOffset:X}";
    }
    internal static TextBlock ByteCell(Row row,int representation)
    {
        var text=new TextBlock{FontFamily=DumpFont,FontSize=12,TextWrapping=TextWrapping.NoWrap,Foreground=row.Foreground};
        for(int column=0;column<16;column++)
        {
            int index=row.Offset+column;bool present=column<row.Count;
            string value=representation==0?present?$"{row.Bytes[index]:X2} ":"   ":present?HexPositions.Character(row.Bytes[index],representation==2).ToString():" ";
            text.Inlines!.Add(new Run(value){Background=present?row.ByteBackground(index):Brushes.Transparent,Foreground=row.Foreground});
            if(representation==0&&column%4==3)text.Inlines.Add(new Run(" "){Foreground=row.Foreground});
        }
        ToolTip.SetTip(text,row.Section=="Trailing"?"Data beyond the size declared in the MZF header. Clear Preserve trailing data before Save As to remove it.":row.IsQdf?"Derived QDF header: red prefix bytes 0–3; blue CRC bytes 68–69.":row.Section);
        return text;
    }
    private TableView Table(IEnumerable<Row> rows)
    {
        const double glyph=8;var table=new TableView{Name="HexDumpTable",FontFamily=DumpFont,FontSize=12,Margin=new Thickness(8),ItemsSource=rows.ToArray(),CanUserResizeColumns=false};
        table.Columns.Add(new(){Header="Region",Binding=new Binding("Section"),Width=new GridLength(150),CellTemplate=new FuncDataTemplate<Row>((row,_)=>new TextBlock{Text=row?.Section,FontFamily=DumpFont,FontSize=12,Foreground=row?.Foreground??Brushes.Black,TextWrapping=TextWrapping.NoWrap})});
        table.Columns.Add(new(){Header="Offset",Binding=new Binding("Address"),Width=new GridLength(90),CellTemplate=new FuncDataTemplate<Row>((row,_)=>new TextBlock{Text=row?.Address,FontFamily=DumpFont,FontSize=12,Foreground=row?.Foreground??Brushes.Black})});
        table.Columns.Add(new(){Header="RAM",Binding=new Binding("RamAddress"),Width=new GridLength(80),CellTemplate=new FuncDataTemplate<Row>((row,_)=>new TextBlock{Text=row?.RamAddress,FontFamily=DumpFont,FontSize=12})});
        foreach(var(column,title,path,characters)in new[]{(0,"Hex","Hex",52),(1,"ASCII","Ascii",16),(2,"MZASCII","Shascii",16)})
            table.Columns.Add(new(){Header=title,Binding=new Binding(path),Width=new GridLength(characters*glyph+20),CellTemplate=new FuncDataTemplate<Row>((row,_)=>
            {
                if(row is null)return new TextBlock();var text=ByteCell(row,column);var cell=(row,column,text);
                text.AttachedToVisualTree+=(_,_)=>{visible.Add(cell);Paint(row,column,text);};text.DetachedFromVisualTree+=(_,_)=>visible.Remove(cell);
                text.PointerPressed+=(_,e)=>
                {
                    double width=new FormattedText("0",System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(DumpFont),12,Brushes.Black).WidthIncludingTrailingWhitespace;
                    int character=Math.Max(0,(int)(e.GetPosition(text).X/width));int selected=column==0?Enumerable.Range(0,16).LastOrDefault(i=>i*3+i/4<=character):character;
                    SelectByte(row,selected);table.SelectedItem=row;table.Focus();e.Handled=true;
                };return text;
            })});
        table.KeyDown+=(_,e)=>
        {
            if(selectedRow is null&&displayed.Length>0)SelectByte(displayed[0],0);if(selectedRow is null)return;
            int rowIndex=Array.IndexOf(displayed,selectedRow);int change=e.Key switch{Key.Left=>-1,Key.Right=>1,Key.Up=>-16,Key.Down=>16,_=>0};
            if(change==0)return;int next=selectedByte+change;int targetRow=Math.Clamp(rowIndex+(next<0?-1:next>=selectedRow.Count?1:0),0,displayed.Length-1);
            SelectByte(displayed[targetRow],targetRow==rowIndex?next:(next+16)%16);table.SelectedItem=selectedRow;e.Handled=true;
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(table,Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return table;
    }
}
