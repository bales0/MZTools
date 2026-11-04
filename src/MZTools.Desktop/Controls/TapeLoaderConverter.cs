using System.Globalization;
using Avalonia.Data.Converters;

namespace MZTools.Desktop.Controls;

public sealed class TapeLoaderConverter:IValueConverter
{
    public object? Convert(object? value,Type targetType,object? parameter,CultureInfo culture)=>value is string loader?TapeProfileNames.LoaderDisplayName(loader):value;
    public object ConvertBack(object? value,Type targetType,object? parameter,CultureInfo culture)=>Avalonia.Data.BindingOperations.DoNothing;
}
