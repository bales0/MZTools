using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed class MzfHexViewControl : DockPanel
{
    private TapeRecord record;
    private readonly MzfHexBuffer buffer;
    private Action<byte[]>? apply;
    private readonly ListBox rows=new(){BorderThickness=new(0),Background=Brushes.White};
    private readonly Button edit=new(){Content="Edit"},undo=new(){Content="Undo"},revert=new(){Content="Revert"},applyButton=new(){Content="Apply to document"},cancel=new(){Content="Cancel edits"};
    internal readonly TextBlock Position=new(){Margin=new(6),FontFamily=new("Consolas"),FontSize=12};
    internal readonly TextBlock Status=new(){Margin=new(6),TextWrapping=TextWrapping.Wrap};
    private int selected,inputOffset,payload;private ushort load;private bool lowNibble;
    internal bool IsEditing {get;private set;}
    internal bool Applied {get;private set;}
    internal byte[] EditedBytes=>(byte[])buffer.Bytes.Clone();
    internal MzfHexViewControl(TapeRecord record)
    {
        this.record=record;buffer=new(record);LastChildFill=true;Margin=new(8);
        var header=new StackPanel();SetDock(header,Dock.Top);Children.Add(header);
        var tools=new WrapPanel();header.Children.Add(tools);
        foreach(var button in new[]{edit,undo,revert,applyButton,cancel}){button.Padding=new(8,4,8,4);button.Margin=new(0,0,6,5);tools.Children.Add(button);}
        edit.Click+=(_,_)=>BeginEdit();undo.Click+=(_,_)=>Undo();revert.Click+=(_,_)=>Revert();applyButton.Click+=(_,_)=>Apply();cancel.Click+=(_,_)=>CancelEdit();
        var legend=new WrapPanel();header.Children.Add(legend);
        foreach(var field in Enum.GetValues<MzfHexField>())legend.Children.Add(new TextBlock{Text=" "+MzfHexPresentation.Label(field)+" ",Background=FieldBrush(field),Margin=new(0,0,5,3)});
        header.Children.Add(new TextBlock{Text=MzfDescriptionEditing.Inspect(record).Warning,Foreground=Brushes.DarkOrange,TextWrapping=TextWrapping.Wrap});
        var headings=new Grid();foreach(int width in new[]{140,90,80,430,140,140})headings.ColumnDefinitions.Add(new(){Width=new(width)});
        foreach(var item in new[]{("Region",0),("Offset",1),("RAM",2),("HEX",3),("ASCII",4),("MZASCII",5)}){var text=new TextBlock{Text=item.Item1,Margin=new(0,8,0,5)};Grid.SetColumn(text,item.Item2);headings.Children.Add(text);}header.Children.Add(headings);
        var footer=new StackPanel();SetDock(footer,Dock.Bottom);Children.Add(footer);footer.Children.Add(Position);footer.Children.Add(Status);
        var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0)));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));rows.ItemContainerStyle=style;
        var factory=new FrameworkElementFactory(typeof(HexLine));rows.ItemTemplate=new DataTemplate{VisualTree=factory};
        VirtualizingStackPanel.SetIsVirtualizing(rows,true);VirtualizingStackPanel.SetVirtualizationMode(rows,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(rows,true);ScrollViewer.SetHorizontalScrollBarVisibility(rows,ScrollBarVisibility.Auto);Children.Add(rows);
        rows.PreviewTextInput+=(_,e)=>{if(IsEditing){e.Handled=true;TypeHex(e.Text);}};
        rows.PreviewKeyDown+=(_,e)=>
        {
            int delta=e.Key switch{Key.Left=>-1,Key.Right=>1,Key.Up=>-16,Key.Down=>16,_=>0};
            if(delta!=0){e.Handled=true;SelectOffset(Math.Clamp(selected+delta,0,buffer.Bytes.Length-1));}
            else if(IsEditing&&e.Key==Key.Z&&Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){e.Handled=true;Undo();}
            else if(IsEditing&&e.Key==Key.Escape){e.Handled=true;CancelEdit();}
            else if(IsEditing&&e.Key is Key.Delete or Key.Back or Key.Insert)e.Handled=true;
        };
        rows.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,(_,e)=>{e.Handled=true;try{if(Clipboard.ContainsText())PasteHex(Clipboard.GetText());}catch(System.Runtime.InteropServices.ExternalException ex){Status.Text=ex.Message;}},(_,e)=>{e.CanExecute=IsEditing;e.Handled=true;}));
        Rebuild();SelectOffset(0);State();
    }
    internal void Enable(Action<byte[]> action){apply=action;State();}
    internal void BeginEdit(){if(apply==null)return;IsEditing=true;inputOffset=selected;lowNibble=false;State();rows.Focus();}
    internal void SelectOffset(int offset)
    {
        selected=Math.Clamp(offset,0,buffer.Bytes.Length-1);inputOffset=selected;lowNibble=false;
        Position.Text=MzfHexPresentation.Location(buffer.Bytes,selected);InvalidateLines(rows);rows.Focus();
    }
    private void State()
    {
        edit.Visibility=apply!=null&&!IsEditing?Visibility.Visible:Visibility.Collapsed;
        foreach(var button in new[]{undo,revert,applyButton,cancel})button.Visibility=IsEditing?Visibility.Visible:Visibility.Collapsed;
        Status.Text=IsEditing?$"Editing · {buffer.Changes} changed byte(s). Type HEX or paste complete bytes. Fixed-size overwrite; Apply changes memory, Save writes the file.":"Read-only view · the same fields and colors are used while editing.";
    }
    private void Rebuild()
    {
        payload=MzfHexPresentation.Payload(buffer.Bytes);load=MzfHexPresentation.Load(buffer.Bytes);
        var lines=new List<Line>();void Add(int start,int count,string section){for(int offset=start;offset<start+count;offset+=16)lines.Add(new(this,offset,Math.Min(16,start+count-offset),section));}
        int data=Math.Min(payload,buffer.Bytes.Length-128);Add(0,128,"MZF header");Add(128,data,"Data");Add(128+data,buffer.Bytes.Length-128-data,"Trailing");rows.ItemsSource=lines;
    }
    private void Refresh()
    {
        if(payload!=MzfHexPresentation.Payload(buffer.Bytes)||load!=MzfHexPresentation.Load(buffer.Bytes))Rebuild();
        SelectOffset(selected);State();
    }
    internal bool TypeHex(string text)
    {
        if(!IsEditing||!buffer.Type(inputOffset,lowNibble,text,out int next,out bool low))return false;
        Refresh();SelectOffset(Math.Min(next,buffer.Bytes.Length-1));inputOffset=next;lowNibble=low;return true;
    }
    internal bool PasteHex(string text)
    {
        if(!IsEditing||!buffer.Paste(selected,text,out int next)){Status.Text="Paste complete HEX bytes within the existing file.";return false;}
        Refresh();SelectOffset(Math.Min(next,buffer.Bytes.Length-1));inputOffset=next;return true;
    }
    internal void SetBytes(byte[] bytes){if(!IsEditing)throw new InvalidOperationException("Enable Edit first.");buffer.Set(bytes);Refresh();}
    internal void Undo(){if(!IsEditing)return;buffer.Undo();Refresh();}
    internal void Revert(){if(!IsEditing)return;buffer.Revert();Refresh();}
    internal void CancelEdit(){if(!IsEditing)return;buffer.Revert();buffer.Accept();IsEditing=false;Refresh();}
    internal void Apply()
    {
        if(!IsEditing||apply==null)return;
        try{var parsed=MzfRecordEditing.ParseEdited(record,buffer.Bytes);apply((byte[])buffer.Bytes.Clone());record=parsed;buffer.Accept();Applied=true;IsEditing=false;Refresh();Status.Text="Applied to document memory. Save to write the file.";}
        catch(Exception ex)when(ex is System.IO.InvalidDataException or InvalidOperationException or System.IO.EndOfStreamException){Status.Text=ex.Message;}
    }
    private static Brush FieldBrush(MzfHexField field)=>new SolidColorBrush(Color.FromArgb(0xff,(byte)(MzfHexPresentation.Color(field)>>16),(byte)(MzfHexPresentation.Color(field)>>8),(byte)MzfHexPresentation.Color(field)));
    private static void InvalidateLines(DependencyObject parent)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is HexLine line)line.InvalidateVisual();else InvalidateLines(child);}}
    private sealed record Line(MzfHexViewControl Owner,int Start,int Count,string Section);
    public sealed class HexLine : FrameworkElement
    {
        private static readonly Typeface Font=new("Consolas");
        public HexLine(){Height=21;Width=1020;HorizontalAlignment=HorizontalAlignment.Left;DataContextChanged+=(_,_)=>InvalidateVisual();MouseLeftButtonDown+=Click;}
        private double Glyph=>new FormattedText("0",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Font,12,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        private void Click(object sender,MouseButtonEventArgs e)
        {
            if(DataContext is not Line line)return;double x=e.GetPosition(this).X,g=Glyph;int column=x>=880?(int)((x-880)/g):x>=740?(int)((x-740)/g):x>=310?Enumerable.Range(0,16).LastOrDefault(c=>c*3+c/4<=(x-310)/g):0;
            line.Owner.SelectOffset(line.Start+Math.Clamp(column,0,line.Count-1));e.Handled=true;
        }
        protected override void OnRender(DrawingContext dc)
        {
            if(DataContext is not Line line)return;var owner=line.Owner;double glyph=Glyph;
            void Text(string text,double x,Brush brush)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Font,12,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,2));
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,Width,Height));Text(line.Section,0,Brushes.Black);Text($"{line.Start:X8}",140,Brushes.Black);
            Text(line.Section=="Data"?$"0x{(owner.load+line.Start-128)&0xffff:X4}":"—",230,Brushes.Black);
            for(int column=0;column<line.Count;column++)
            {
                int offset=line.Start+column;byte value=owner.buffer.Bytes[offset];bool selected=offset==owner.selected;
                Brush background=selected?Brushes.DodgerBlue:FieldBrush(MzfHexPresentation.Field(offset,owner.payload));Brush foreground=selected?Brushes.White:line.Section=="Trailing"?Brushes.DimGray:Brushes.Black;
                double hex=310+(column*3+column/4)*glyph;
                foreach(var item in new[]{(hex,3*glyph),(740+column*glyph,glyph),(880+column*glyph,glyph)})dc.DrawRectangle(background,null,new Rect(item.Item1,0,item.Item2,Height));
                Text($"{value:X2} ",hex,foreground);char ascii=char.IsControl((char)value)?'.':(char)value;byte sharp=SharpMzEncoding.FromSHASCII(value);
                Text(ascii.ToString(),740+column*glyph,foreground);Text((char.IsControl((char)sharp)?'.':(char)sharp).ToString(),880+column*glyph,foreground);
            }
        }
    }
}
