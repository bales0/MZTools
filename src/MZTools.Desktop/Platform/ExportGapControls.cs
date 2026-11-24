using Avalonia.Controls;
using Avalonia.Layout;

namespace MZTools.Desktop.Platform;

internal sealed class ExportGapControls : StackPanel
{
    internal NumericUpDown Start {get;}=Number("StartGapMs",1000);
    internal NumericUpDown Between {get;}=Number("BetweenGapMs",2000);
    internal NumericUpDown End {get;}=Number("EndGapMs",1000);
    internal ExportGapControls()
    {
        Spacing=5;Children.Add(new TextBlock{Text="Gaps / Pauses"});
        var fields=new WrapPanel{Orientation=Orientation.Horizontal};Children.Add(fields);
        void Field(string label,NumericUpDown value){var panel=new StackPanel{Margin=new Avalonia.Thickness(0,0,8,0)};panel.Children.Add(new TextBlock{Text=label});panel.Children.Add(value);fields.Children.Add(panel);}
        Field("Start (ms)",Start);Field("Between programs (ms)",Between);Field("End (ms)",End);
        Children.Add(new TextBlock{Text="Approximate LOW pauses around complete programs. Loader timing is unchanged.",TextWrapping=Avalonia.Media.TextWrapping.Wrap});
    }
    private static NumericUpDown Number(string name,int value)=>new(){Name=name,Minimum=0,Maximum=TapeExportGapOptions.MaximumMilliseconds,Value=value,Increment=100,FormatString="0",Width=125};
    internal TapeExportGapOptions Read()
    {
        int ReadValue(NumericUpDown input)=>input.Value is decimal n&&n>=0&&n<=TapeExportGapOptions.MaximumMilliseconds&&decimal.Truncate(n)==n?(int)n:throw new InvalidOperationException("Enter whole milliseconds between 0 and 60 000 for each gap.");
        return new(ReadValue(Start),ReadValue(Between),ReadValue(End));
    }
}
