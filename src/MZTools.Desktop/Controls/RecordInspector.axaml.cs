using Avalonia.Controls;
using Avalonia.Interactivity;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Controls;

public sealed partial class RecordInspector : UserControl
{
    public RecordInspector() => InitializeComponent();
    private async void EditMzfHex(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not ShellViewModel {ActiveTape:{} session}||session.SelectedRow is not {} row||!session.CanEditMzfBytes||TopLevel.GetTopLevel(this) is not Window owner)return;
        var original=row.Record;
        await new MZTools.Desktop.Views.HexViewWindow(original,bytes=>{session.ApplyMzfBytes(original,bytes);original=session.Model.Records[0];},24).ShowDialog(owner);
    }
    private void CancelEdits(object? sender, RoutedEventArgs e) => (DataContext as ShellViewModel)?.ActiveTape?.ResetMetadata();
}
