using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Documents;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;

namespace MZTools.Desktop.Views;

public sealed partial class RecordWorkspace
{
    private GridSplitter? recordsInspectorSplitter;
    private ShellViewModel? forensicModel;
    private TapeSession? forensicObserved;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InstallRecordsInspectorSplitter();
        DataContextChanged += ForensicContextChanged;
        RecordTabs.SelectionChanged += ForensicTabChanged;
        ForensicContextChanged(this, EventArgs.Empty);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (forensicModel is not null) forensicModel.PropertyChanged -= ForensicModelChanged;
        forensicModel = null;
        if (forensicObserved is not null) forensicObserved.PropertyChanged -= ForensicSessionChanged;
        forensicObserved = null;
        DataContextChanged -= ForensicContextChanged;
        RecordTabs.SelectionChanged -= ForensicTabChanged;
        base.OnDetachedFromVisualTree(e);
    }
    private void InstallRecordsInspectorSplitter()
    {
        if (recordsInspectorSplitter is not null) return;
        recordsInspectorSplitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CBD5E1")),
            ShowsPreview = false
        };
        Grid.SetColumn(recordsInspectorSplitter, 1);
        EditorGrid.Children.Add(recordsInspectorSplitter);
        SizeChanged += (_, _) => recordsInspectorSplitter.IsVisible = EditorGrid.ColumnDefinitions[2].Width.Value > 0;
        recordsInspectorSplitter.IsVisible = EditorGrid.ColumnDefinitions[2].Width.Value > 0;
    }

    private void ForensicTabChanged(object? sender, SelectionChangedEventArgs e) => RefreshForensic();
    private void ForensicContextChanged(object? sender, EventArgs e)
    {
        if (forensicModel != Model)
        {
            if (forensicModel is not null) forensicModel.PropertyChanged -= ForensicModelChanged;
            forensicModel = Model;
            if (forensicModel is not null) forensicModel.PropertyChanged += ForensicModelChanged;
        }
        RefreshForensic();
    }
    private void ForensicModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.ActiveTape) or nameof(ShellViewModel.ShowMedia)) RefreshForensic();
    }
    private void ForensicSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TapeSession.MapDetail) or nameof(TapeSession.MapSummary)
            or nameof(TapeSession.SelectedRow) or nameof(TapeSession.HasRegion))
        {

            MediaMap.InvalidateVisual();
            if (!Equals(QuickDiskBlocks.SelectedItem, forensicObserved?.SelectedRegion)) QuickDiskBlocks.SelectedItem = forensicObserved?.SelectedRegion;
        }

    }
    private void RefreshForensic()
    {

        var tape = Model?.ActiveTape;
        if(RecordTabs.Items[1] is TabItem mapTab)mapTab.IsVisible=tape?.IsQuickDisk==true;
        if (forensicObserved != tape)
        {
            if (forensicObserved is not null) forensicObserved.PropertyChanged -= ForensicSessionChanged;
            forensicObserved = tape;
            if (forensicObserved is not null) forensicObserved.PropertyChanged += ForensicSessionChanged;
        }
    }
}
