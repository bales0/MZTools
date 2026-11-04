using System.ComponentModel;
using Avalonia.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Controls;

// The file metadata editor should not occupy the screen for an empty disk.
public sealed partial class DiskInspector : UserControl
{
    private ShellViewModel? model;
    private DiskSession? session;

    public DiskInspector()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
        AttachedToVisualTree += (_, _) => Bind();
        DetachedFromVisualTree += (_, _) =>
        {
            if (model is not null) model.PropertyChanged -= ModelChanged;
            if (session is not null) session.PropertyChanged -= SessionChanged;
            model = null;
            session = null;
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.ActiveDisk)) Bind();
    }

    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiskSession.SelectedFile) or nameof(DiskSession.Files))
            MetadataFields.IsVisible = session?.SelectedFile is not null;
    }

    private void Bind()
    {
        if (model != DataContext)
        {
            if (model is not null) model.PropertyChanged -= ModelChanged;
            model = DataContext as ShellViewModel;
            if (model is not null) model.PropertyChanged += ModelChanged;
        }
        if (session != model?.ActiveDisk)
        {
            if (session is not null) session.PropertyChanged -= SessionChanged;
            session = model?.ActiveDisk;
            if (session is not null) session.PropertyChanged += SessionChanged;
        }
        MetadataFields.IsVisible = session?.SelectedFile is not null;
    }
}
