using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MZTools.Desktop.Views;

// Restores manual, mouse-driven width adjustment between disk data and inspector.
public sealed partial class DiskWorkspace
{
    private GridSplitter? parityInspectorSplitter;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (parityInspectorSplitter is not null) return;
        parityInspectorSplitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse("#CBD5E1")),
            ShowsPreview = false
        };
        Grid.SetColumn(parityInspectorSplitter, 1);
        DiskGrid.Children.Add(parityInspectorSplitter);
        SizeChanged += (_, _) =>
            parityInspectorSplitter.IsVisible = DiskGrid.ColumnDefinitions[2].Width.Value > 0;
        parityInspectorSplitter.IsVisible = DiskGrid.ColumnDefinitions[2].Width.Value > 0;
    }
}
