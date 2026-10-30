using Avalonia.Controls;
using Avalonia.Interactivity;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Controls;

public sealed partial class RecordInspector : UserControl
{
    public RecordInspector() => InitializeComponent();
    private void CancelEdits(object? sender, RoutedEventArgs e) => (DataContext as ShellViewModel)?.ActiveTape?.ResetMetadata();
}
