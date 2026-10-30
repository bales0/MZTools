using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;
namespace MZTools.Desktop.Controls;

// Embedded and floating physical views share the same component and session viewport.
public sealed partial class DiskPhysicalPanel : UserControl
{
    private ShellViewModel? model;
    private DiskSession? session;
    public DiskPhysicalPanel()
    {
        InitializeComponent();DataContextChanged+=(_,_)=>Bind();AttachedToVisualTree+=(_,_)=>Bind();
        DetachedFromVisualTree+=(_,_)=>{if(model is not null)model.PropertyChanged-=ModelChanged;if(session is not null)session.PropertyChanged-=SessionChanged;model=null;session=null;};
    }
    private void ModelChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(ShellViewModel.ActiveDisk))Bind();}
    private void SessionChanged(object? sender,PropertyChangedEventArgs e)=>Cells.InvalidateVisual();
    private void Bind()
    {
        if(model!=DataContext){if(model is not null)model.PropertyChanged-=ModelChanged;model=DataContext as ShellViewModel;if(model is not null)model.PropertyChanged+=ModelChanged;}
        if(session!=model?.ActiveDisk){if(session is not null)session.PropertyChanged-=SessionChanged;session=model?.ActiveDisk;if(session is not null)session.PropertyChanged+=SessionChanged;}
        Cells.Session=session;Cells.InvalidateVisual();
    }
    private void CellsZoomIn(object? sender,RoutedEventArgs e)=>Cells.Zoom(1.5);
    private void CellsZoomOut(object? sender,RoutedEventArgs e)=>Cells.Zoom(1/1.5);
    private void CellsFit(object? sender,RoutedEventArgs e)=>Cells.Fit();
}
