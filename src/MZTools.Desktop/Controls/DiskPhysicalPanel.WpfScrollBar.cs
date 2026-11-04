using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Controls;

// Draggable scrollbar for physical MFM/HFE bitcell range. Embedded and floating
// views retain the shared DiskSession viewport and the current zoom.
public sealed partial class DiskPhysicalPanel
{
    private ScrollBar? physicalScrollBar;
    private DiskSession? barSession;
    private ShellViewModel? barModel;
    private bool synchronizingBar;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InstallPhysicalScrollBar();
        DataContextChanged += PhysicalContextChanged;
        BindPhysicalScrollBar();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= PhysicalContextChanged;
        if (barModel is not null) barModel.PropertyChanged -= PhysicalModelChanged;
        if (barSession is not null) barSession.PropertyChanged -= PhysicalSessionChanged;
        barModel = null; barSession = null;
        base.OnDetachedFromVisualTree(e);
    }
    private void InstallPhysicalScrollBar()
    {
        if (physicalScrollBar is not null || Cells.Parent is not Grid grid) return;
        grid.RowDefinitions.Insert(2, new RowDefinition(GridLength.Auto));
        foreach (var child in grid.Children.ToArray())
            if (child != Cells && Grid.GetRow(child) >= 2) Grid.SetRow(child, Grid.GetRow(child) + 1);
        physicalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Visibility = ScrollBarVisibility.Visible,
            AllowAutoHide = false,
            Height = 18,
            Minimum = 0,
            SmallChange = 1,
            LargeChange = 128,
            IsVisible = false
        };
        physicalScrollBar.PropertyChanged += (_, changed) =>
        {
            if (changed.Property != RangeBase.ValueProperty || synchronizingBar) return;
            if (barSession is not { TrackData: { } track } current) return;
            double start = Math.Clamp(physicalScrollBar.Value, 0, Math.Max(0, track.BitCellCount - current.CellLength));
            if (Math.Abs(current.CellStart - start) < 0.5) return;
            current.CellStart = start;
            Cells.InvalidateVisual();
        };
        Grid.SetRow(physicalScrollBar, 2);
        grid.Children.Add(physicalScrollBar);
    }
    private void PhysicalContextChanged(object? sender, EventArgs e) => BindPhysicalScrollBar();
    private void PhysicalModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.ActiveDisk) or nameof(ShellViewModel.ShowDisk)) BindPhysicalScrollBar();
    }
    private void PhysicalSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiskSession.CellStart) or nameof(DiskSession.CellLength) or nameof(DiskSession.TrackIndex)) UpdatePhysicalScrollBar();
    }
    private void BindPhysicalScrollBar()
    {
        var nextModel = DataContext as ShellViewModel;
        if (barModel != nextModel)
        {
            if (barModel is not null) barModel.PropertyChanged -= PhysicalModelChanged;
            barModel = nextModel;
            if (barModel is not null) barModel.PropertyChanged += PhysicalModelChanged;
        }
        var nextSession = barModel?.ActiveDisk;
        if (barSession != nextSession)
        {
            if (barSession is not null) barSession.PropertyChanged -= PhysicalSessionChanged;
            barSession = nextSession;
            if (barSession is not null) barSession.PropertyChanged += PhysicalSessionChanged;
        }
        UpdatePhysicalScrollBar();
    }
    private void UpdatePhysicalScrollBar()
    {
        if (physicalScrollBar is null) return;
        synchronizingBar = true;
        try
        {
            var current = barSession;
            physicalScrollBar.IsVisible = current?.TrackData is { BitCellCount: > 0 };
            if (current?.TrackData is not { } track) return;
            double span = Math.Clamp(current.CellLength, 1, track.BitCellCount);
            physicalScrollBar.Maximum = Math.Max(0, track.BitCellCount - span);
            physicalScrollBar.ViewportSize = span;
            physicalScrollBar.SmallChange = Math.Max(1, span / 100);
            physicalScrollBar.LargeChange = Math.Max(1, span * 0.75);
            physicalScrollBar.Value = Math.Clamp(current.CellStart, 0, physicalScrollBar.Maximum);
        }
        finally { synchronizingBar = false; }
    }
}
