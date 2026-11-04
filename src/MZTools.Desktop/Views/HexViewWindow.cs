using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Views;

public sealed class HexViewWindow:Window
{
    private sealed record Row(byte[] Bytes,int Offset)
    {
        public string Address=>$"{Offset:X8}";
        public string Hex=>BitConverter.ToString(Bytes,Offset,Math.Min(16,Bytes.Length-Offset)).Replace('-',' ');
        public string Ascii
        {
            get{var chars=new char[Math.Min(16,Bytes.Length-Offset)];for(int i=0;i<chars.Length;i++){byte b=Bytes[Offset+i];chars[i]=b is >=32 and <127?(char)b:'.';}return new string(chars);}
        }
    }
    internal HexViewWindow(RecordRow row)
    {
        Title="Hex Browser";Width=800;Height=450;MinWidth=600;MinHeight=300;
        byte[] bytes=row.Record.GetSerializedHeader().Concat(row.Record.Body.MzfBody).Concat(row.Record.Body.TrailingData??[]).ToArray();
        var table=new TableView{FontFamily=new FontFamily("Consolas, DejaVu Sans Mono"),Margin=new Thickness(8),ItemsSource=Enumerable.Range(0,(bytes.Length+15)/16).Select(i=>new Row(bytes,i*16)).ToArray()};
        table.Columns.Add(new(){Header="Offset",Binding=new Binding("Address"),Width=new GridLength(90)});
        table.Columns.Add(new(){Header="Hex",Binding=new Binding("Hex"),Width=new GridLength(3,GridUnitType.Star)});
        table.Columns.Add(new(){Header="ASCII",Binding=new Binding("Ascii"),Width=new GridLength(1,GridUnitType.Star)});
        Content=table;
    }
}
