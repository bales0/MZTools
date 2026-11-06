using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Views;

public sealed partial class RecordWorkspace
{
    private void MapRegionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Model?.ActiveTape is { } tape) tape.SelectRegion(QuickDiskBlocks.SelectedItem as QuickDiskRegion);
    }
    private void MapClear(object? sender, RoutedEventArgs e)
    {
        QuickDiskBlocks.SelectedItem = null;
        Model?.ActiveTape?.SelectRegion(null);
        if (Model?.ActiveTape is { } tape) { tape.SetSelection([]); RestoreSelection(); }
    }
    private void MapHex(object? sender, RoutedEventArgs e)
    {
        if (Model?.ActiveTape is not { Snapshot: { } snapshot, SelectedRegion: { } region } || TopLevel.GetTopLevel(this) is not Window owner) return;
        HexViewWindow.Region(Model.ActiveTape,region).Show(owner);
    }
    private async void MapCopy(object? sender, RoutedEventArgs e)
    {
        if(Model?.ActiveTape is{} tape && TopLevel.GetTopLevel(this)?.Clipboard is{} clipboard)await clipboard.SetTextAsync(tape.WpfMapReport);
    }
    private async void MapReport(object? sender, RoutedEventArgs e)
    {
        if (Model is not { ActiveTape: { Snapshot: { } snapshot } } model || TopLevel.GetTopLevel(this) is not Window owner) return;
        await model.Documents.ExecuteAsync(async ct =>
        {
            var path = await new Platform.DesktopDialogs(owner).PickDiskOutputAsync("Save QuickDisk report", "QuickDisk-map.txt");
            if (path is null) return false;
            await model.Documents.WriteExportAsync(new Dictionary<string, byte[]> { [path] = System.Text.Encoding.UTF8.GetBytes(model.ActiveTape!.WpfMapReport) }, ct);
            return true;
        });
    }
}
