using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed class MzfDescriptionWindow : Window
{
    internal readonly TextBox Description=new(){TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,MinHeight=70,MaxHeight=140};
    internal readonly TextBlock Status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)};
    internal readonly TextBlock Warning=new(){Foreground=Brushes.DarkOrange,TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)};
    private readonly TapeRecord original;
    private readonly string initial;
    private readonly Action<TapeRecord> apply;
    internal bool Applied {get;private set;}
    internal MzfDescriptionWindow(Window owner,TapeRecord record,Action<TapeRecord> apply)
    {
        Owner=owner;original=record;this.apply=apply;Title="Properties — MZF Description";Width=530;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel{Margin=new(20)};Content=root;
        root.Children.Add(new TextBlock{Text="Description · physical header offsets 0x18–0x7F · at most 104 encoded bytes",TextWrapping=TextWrapping.Wrap});
        initial=MzfDescriptionEditing.DecodeText(record.DescriptionRaw);Description.Text=initial;root.Children.Add(Description);
        root.Children.Add(new TextBlock{Text="An explicit text edit replaces the entire 104-byte field and pads unused bytes with 00. Unchanged text preserves every original byte. HEX can edit individual bytes throughout the complete file.",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)});
        Warning.Text=MzfDescriptionEditing.Inspect(record).Warning;root.Children.Add(Warning);root.Children.Add(Status);
        var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};root.Children.Add(buttons);
        void Button(string text,Action action){var b=new Button{Content=text,Margin=new(4),Padding=new(8,4,8,4)};b.Click+=(_,_)=>action();buttons.Children.Add(b);}
        Button("Edit entire MZF in HEX…",()=>
        {
            if(Description.Text!=initial){Status.Text="Apply or cancel the text draft before HEX editing.";return;}
            var hex=HexBrowser.MzfEditor(this,record,bytes=>{apply(MzfRecordEditing.ParseEdited(record,bytes));Applied=true;},24);
            hex.ShowDialog();if(hex.Applied)Close();
        });
        Button("Apply metadata",Apply);Button("Cancel edits",()=>Close());
        Description.TextChanged+=(_,_)=>{try{_=MzfDescriptionEditing.EncodeText(Description.Text);Status.Text=$"{Description.Text.Length}/104 encoded B";}catch(InvalidDataException e){Status.Text=e.Message;}};
    }
    internal void Apply()
    {
        try{if(Description.Text!=initial){apply(MzfDescriptionEditing.Replace(original,MzfDescriptionEditing.EncodeText(Description.Text)));Applied=true;}Close();}
        catch(Exception e)when(e is InvalidDataException or InvalidOperationException){Status.Text=e.Message;}
    }
}
