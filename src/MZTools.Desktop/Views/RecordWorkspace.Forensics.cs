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
    private QuickDiskForensicView? qdForensic;
    private StackPanel? forensicHost;
    private ScrollBar? quickDiskScroll;
    private bool syncQuickDiskScroll;
    private GridSplitter? recordsInspectorSplitter;
    private ShellViewModel? forensicModel;
    private TapeSession? forensicObserved;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (qdForensic is null && MediaMap.Parent is StackPanel parent)
        {
            qdForensic = new QuickDiskForensicView { Height = 330 };
            var bar = new WrapPanel { Orientation = Orientation.Horizontal };
            var zoomIn = new Button { Content = "Bit zoom +" };
            var zoomOut = new Button { Content = "Bit zoom −", Margin = new Avalonia.Thickness(5, 0) };
            var fit = new Button { Content = "Fit", Margin = new Avalonia.Thickness(0, 0, 5, 0) };
            var selection = new Button { Content = "Selected region" };
            zoomIn.Click += (_, _) => qdForensic.Zoom(1.5);
            zoomOut.Click += (_, _) => qdForensic.Zoom(1 / 1.5);
            fit.Click += (_, _) => qdForensic.Fit();
            selection.Click += (_, _) => qdForensic.FitSelected();
            bar.Children.Add(zoomIn); bar.Children.Add(zoomOut); bar.Children.Add(fit); bar.Children.Add(selection);
            forensicHost = new StackPanel { Spacing = 5 };
            forensicHost.Children.Add(new TextBlock { Text = "Disk surface and MFM detail", FontWeight = Avalonia.Media.FontWeight.SemiBold });
            forensicHost.Children.Add(bar);
            forensicHost.Children.Add(qdForensic);
            quickDiskScroll = new ScrollBar
            {
                Orientation = Orientation.Horizontal, Visibility = ScrollBarVisibility.Visible,
                AllowAutoHide = false, Minimum = 0, Height = 18,
                Margin = new Thickness(0, 3, 0, 4)
            };
            quickDiskScroll.PropertyChanged += (_, change) =>
            {
                if (change.Property == RangeBase.ValueProperty && !syncQuickDiskScroll && qdForensic is not null)
                    qdForensic.ScrollTo(quickDiskScroll.Value);
            };
            qdForensic.ViewportChanged += SyncQuickDiskScroll;
            forensicHost.Children.Add(quickDiskScroll);
            parent.Children.Insert(parent.Children.IndexOf(MediaMap) + 1, forensicHost);
        }
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
            qdForensic?.InvalidateVisual();
            MediaMap.InvalidateVisual();
        }
        if (e.PropertyName == nameof(TapeSession.MapSummary)) qdForensic?.Fit();
    }
    private void RefreshForensic()
    {
        if (qdForensic is null) return;
        var tape = Model?.ActiveTape;
        if(RecordTabs.Items[1] is TabItem mapTab)mapTab.IsVisible=tape?.IsQuickDisk==true;
        if (forensicObserved != tape)
        {
            if (forensicObserved is not null) forensicObserved.PropertyChanged -= ForensicSessionChanged;
            forensicObserved = tape;
            if (forensicObserved is not null) forensicObserved.PropertyChanged += ForensicSessionChanged;
        }
        forensicHost!.IsVisible = tape?.IsQuickDisk == true;
        qdForensic.Bind(tape?.IsQuickDisk == true ? tape : null);
        SyncQuickDiskScroll();
    }

    private void SyncQuickDiskScroll()
    {
        if (qdForensic is null || quickDiskScroll is null) return;
        syncQuickDiskScroll = true;
        try
        {
            quickDiskScroll.IsVisible = forensicHost?.IsVisible == true;
            quickDiskScroll.Maximum = Math.Max(0, qdForensic.TotalCells - qdForensic.ViewportLength);
            quickDiskScroll.ViewportSize = qdForensic.ViewportLength;
            quickDiskScroll.SmallChange = Math.Max(1, qdForensic.ViewportLength / 100);
            quickDiskScroll.LargeChange = Math.Max(1, qdForensic.ViewportLength * 0.75);
            quickDiskScroll.Value = Math.Clamp(qdForensic.ViewportStart, 0, quickDiskScroll.Maximum);
        }
        finally { syncQuickDiskScroll = false; }
    }
}
