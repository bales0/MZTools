using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MZTools.Desktop.Views;

public sealed partial class DiskWorkspace
{
    private async void WpfCopyFromDisk(object? sender,RoutedEventArgs e)
    {if(Model is not null)await Model.CopyFromDiskAsync();}
    private void OpenWpfMenu(object? sender,RoutedEventArgs e)
    {if(sender is Button {ContextMenu:{} menu} button)menu.Open(button);}
    private void WpfDiskTool(object? sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem {Tag:string tool}||Model is null)return;
        Model.DiskTool=tool;
        if(Model.DiskToolCommand.CanExecute(null))Model.DiskToolCommand.Execute(null);
    }
    private void WpfDiskProperties(object? sender,RoutedEventArgs e)
    {if(TopLevel.GetTopLevel(this) is Window owner)ShowInspector(owner);}
    private DiskVisualWindow? visualInspector;
    private void WpfDiskAnalysis(object? sender,RoutedEventArgs e)=>OpenWpfVisualInspector();
    internal void OpenWpfVisualInspector()
    {
        if(Model?.ActiveDisk is null||TopLevel.GetTopLevel(this) is not Window owner)return;
        if(visualInspector is not null){visualInspector.Activate();return;}
        visualInspector=new DiskVisualWindow(Model);visualInspector.Closed+=(_,_)=>visualInspector=null;visualInspector.Show(owner);
    }
    private void WpfDiskStructure(object? sender,RoutedEventArgs e)
    {if(Model?.ActiveDisk is{} disk)disk.ViewIndex=6;}
}
