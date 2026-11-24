using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

public sealed class TapeExportGapControl : StackPanel
{
    internal readonly TextBox Start=new(){Text="1000",Width=100},Between=new(){Text="2000",Width=100},End=new(){Text="1000",Width=100};
    public TapeExportGapControl()
    {
        Children.Add(new TextBlock{Text="Gaps / Pauses",Margin=new Thickness(0,10,0,4)});
        var fields=new WrapPanel{Margin=new Thickness(0,6,0,0)};Children.Add(fields);
        void Field(string label,TextBox input){var panel=new StackPanel{Margin=new Thickness(0,0,10,0)};panel.Children.Add(new TextBlock{Text=label});panel.Children.Add(input);fields.Children.Add(panel);}
        Field("Start (ms)",Start);Field("Between programs (ms)",Between);Field("End (ms)",End);
        Children.Add(new TextBlock{Text="Approximate LOW pauses around complete programs. Loader timing is unchanged.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)});
    }
    internal TapeExportGapOptions Read()
    {
        int Value(TextBox input)=>int.TryParse(input.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int value)&&value>=0&&value<=TapeExportGapOptions.MaximumMilliseconds?value:throw new InvalidOperationException("Enter whole milliseconds between 0 and 60 000 for each gap.");
        return new(Value(Start),Value(Between),Value(End));
    }
}
