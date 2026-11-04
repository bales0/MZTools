using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Views;

// A classic, draggable WPF-like horizontal scrollbar beneath the waveform.
// It never changes the visible range length (zoom); only the range's start.
public sealed partial class AudioWorkspace
{
    private ScrollBar? waveformScrollBar;
    private bool synchronizingWaveformBar;
    private DocumentCoordinator? waveformCoordinator;
    private AudioSession? waveformScrollSession;
    private GridSplitter? waveformInspectorSplitter;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InstallWaveformScrollBar();
        InstallAudioInspectorSplitter();
        DataContextChanged += WaveformContextChanged;
        BindWaveformScrollBar();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= WaveformContextChanged;
        if (waveformCoordinator is not null) waveformCoordinator.PropertyChanged -= WaveformCoordinatorChanged;
        if (waveformScrollSession is not null) waveformScrollSession.PropertyChanged -= WaveformSessionChanged;
        waveformCoordinator = null;
        waveformScrollSession = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void InstallAudioInspectorSplitter()
    {
        if (waveformInspectorSplitter is not null) return;
        waveformInspectorSplitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CBD5E1")),
            ShowsPreview = false
        };
        Grid.SetColumn(waveformInspectorSplitter, 1);
        AudioGrid.Children.Add(waveformInspectorSplitter);
        SizeChanged += (_, _) =>
            waveformInspectorSplitter.IsVisible = AudioGrid.ColumnDefinitions[2].Width.Value > 0;
        waveformInspectorSplitter.IsVisible = AudioGrid.ColumnDefinitions[2].Width.Value > 0;
    }

    private void InstallWaveformScrollBar()
    {
        if (waveformScrollBar is not null || Waveform.Parent is not Grid parent) return;
        // Insert a narrow row between waveform and its existing zoom toolbar.
        parent.RowDefinitions.Insert(2, new RowDefinition(GridLength.Auto));
        foreach (var child in parent.Children.ToArray())
            if (child != Waveform && Grid.GetRow(child) >= 2) Grid.SetRow(child, Grid.GetRow(child) + 1);
        waveformScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Visibility = ScrollBarVisibility.Visible,
            AllowAutoHide = false,
            Height = 18,
            Margin = new Thickness(0, 2, 0, 0),
            Minimum = 0,
            SmallChange = 1,
            LargeChange = 128,
            IsVisible = false
        };
        waveformScrollBar.PropertyChanged += (_, changed) =>
        {
            if (changed.Property != RangeBase.ValueProperty || synchronizingWaveformBar) return;
            if (waveformScrollSession is not { } audio) return;
            long span = audio.ViewEnd - audio.ViewStart;
            long first = Math.Clamp((long)Math.Round(waveformScrollBar.Value), 0, Math.Max(0, audio.Model.FrameCount - span));
            if (first != audio.ViewStart) audio.ShowRange(first, first + span);
        };
        Grid.SetRow(waveformScrollBar, 2);
        parent.Children.Add(waveformScrollBar);
    }

    private void WaveformContextChanged(object? sender, EventArgs e) => BindWaveformScrollBar();
    private void WaveformCoordinatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentCoordinator.Active)) BindWaveformScrollBar();
    }
    private void WaveformSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioSession.ViewStart) or nameof(AudioSession.ViewEnd)) UpdateWaveformScrollBar();
    }
    private void BindWaveformScrollBar()
    {
        var coordinator = Model?.Documents;
        if (waveformCoordinator != coordinator)
        {
            if (waveformCoordinator is not null) waveformCoordinator.PropertyChanged -= WaveformCoordinatorChanged;
            waveformCoordinator = coordinator;
            if (waveformCoordinator is not null) waveformCoordinator.PropertyChanged += WaveformCoordinatorChanged;
        }
        var audio = Model?.ActiveAudio;
        if (waveformScrollSession != audio)
        {
            if (waveformScrollSession is not null) waveformScrollSession.PropertyChanged -= WaveformSessionChanged;
            waveformScrollSession = audio;
            if (waveformScrollSession is not null) waveformScrollSession.PropertyChanged += WaveformSessionChanged;
        }
        UpdateWaveformScrollBar();
    }
    private void UpdateWaveformScrollBar()
    {
        if (waveformScrollBar is null) return;
        var audio = waveformScrollSession;
        synchronizingWaveformBar = true;
        try
        {
            waveformScrollBar.IsVisible = audio is not null;
            if (audio is null) return;
            long span = Math.Max(1, audio.ViewEnd - audio.ViewStart);
            waveformScrollBar.Maximum = Math.Max(0, audio.Model.FrameCount - span);
            waveformScrollBar.ViewportSize = span;
            waveformScrollBar.SmallChange = Math.Max(1, span / 100.0);
            waveformScrollBar.LargeChange = Math.Max(1, span * 0.75);
            waveformScrollBar.Value = Math.Clamp(audio.ViewStart, 0, waveformScrollBar.Maximum);
        }
        finally { synchronizingWaveformBar = false; }
    }
}
