using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

public sealed partial class AudioWorkspace : UserControl
{
    private ShellViewModel? Model=>DataContext as ShellViewModel;
    private AudioSession? observed;
    private bool syncing;
    private Window? inspectorWindow;
    private readonly DispatcherTimer debounce=new(){Interval=TimeSpan.FromMilliseconds(150)};
    internal Control WorkspaceFocusRegion=>AudioTabs.SelectedIndex==0?AudioBlocks:AudioTabs;
    internal Control InspectorFocusRegion=>Inspector;
    public AudioWorkspace()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=>BindSession();
        AttachedToVisualTree+=(_,_)=>BindSession();
        DetachedFromVisualTree+=(_,_)=>{debounce.Stop();if(observed is not null)observed.PropertyChanged-=Changed;observed=null;};
        SizeChanged+=(_,_)=>Reflow();AudioTabs.SelectionChanged+=(_,_)=>Reflow();
        AudioBlocks.SelectionChanged+=BlocksSelected;AudioRegions.SelectionChanged+=RegionsSelected;
        debounce.Tick+=async (_,_)=>{debounce.Stop();if(observed is { } session)await session.RefreshViewportAsync();};
    }
    internal void BindSession()
    {
        if(observed!=Model?.ActiveAudio)
        {
            debounce.Stop();if(observed is not null)observed.PropertyChanged-=Changed;
            observed=Model?.ActiveAudio;if(observed is not null){observed.PropertyChanged+=Changed;Schedule();}
            RestoreSelection();
        }
        Reflow();
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(AudioSession.ViewStart) or nameof(AudioSession.ChannelMode))Schedule();
        if(e.PropertyName is nameof(AudioSession.SelectedBlock) or nameof(AudioSession.SelectedRegion))RestoreSelection();
    }
    private void Schedule(){debounce.Stop();debounce.Start();}
    private void BlocksSelected(object? sender,SelectionChangedEventArgs e)
    {
        if(syncing||observed is null)return;
        var rows=AudioBlocks.SelectedItems!.OfType<AudioBlockRow>().ToArray();if(rows.Length==0)return;
        syncing=true;try{observed.SelectedBlock=AudioBlocks.SelectedItem as AudioBlockRow;observed.SetSelection(rows.Select(r=>(r.Start,r.End)));}finally{syncing=false;}
    }
    private void RegionsSelected(object? sender,SelectionChangedEventArgs e)
    {
        if(syncing||observed is null)return;
        var rows=AudioRegions.SelectedItems!.OfType<AudioRegionRow>().ToArray();if(rows.Length==0)return;
        syncing=true;try{observed.SelectedRegion=AudioRegions.SelectedItem as AudioRegionRow;observed.SetSelection(rows.Select(r=>(r.Start,r.End)));}finally{syncing=false;}
    }
    private void RestoreSelection()
    {
        if(syncing)return;syncing=true;
        try
        {
            if(AudioBlocks.SelectedItem!=observed?.SelectedBlock){AudioBlocks.SelectedItems!.Clear();if(observed?.SelectedBlock is { } b)AudioBlocks.SelectedItems.Add(b);}
            if(AudioRegions.SelectedItem!=observed?.SelectedRegion){AudioRegions.SelectedItems!.Clear();if(observed?.SelectedRegion is { } r)AudioRegions.SelectedItems.Add(r);}
        }
        finally{syncing=false;}
    }
    private void Reflow()
    {
        MinHeight=AudioTabs.SelectedIndex==1?1120:AudioTabs.SelectedIndex==4?950:800;
        bool show=Bounds.Width>=900&&Model?.InspectorVisible==true;AudioInspectorPanel.IsVisible=show;
        AudioGrid.ColumnDefinitions[1].Width=new GridLength(show?16:0);AudioGrid.ColumnDefinitions[2].Width=new GridLength(show?288:0);
    }
    internal void ShowInspector(Window owner)
    {
        if(inspectorWindow is not null){inspectorWindow.Activate();return;}
        inspectorWindow=new Window{Title="Audio inspector",Width=440,Height=760,Content=new AudioInspector{DataContext=Model,Margin=new Thickness(20)},WindowStartupLocation=WindowStartupLocation.CenterOwner};
        inspectorWindow.Closed+=(_,_)=>inspectorWindow=null;inspectorWindow.Show(owner);
    }
    private void WaveZoomIn(object? sender,RoutedEventArgs e)=>Waveform.Zoom(1.5);
    private void WaveZoomOut(object? sender,RoutedEventArgs e)=>Waveform.Zoom(1/1.5);
    private void WaveFit(object? sender,RoutedEventArgs e)=>Waveform.Fit();
    private void WaveFitSelection(object? sender,RoutedEventArgs e)=>observed?.FitSelection();
    private void SelectedRecord(object? sender,RoutedEventArgs e){if(observed?.SelectedBlock is { } block){observed.SetSelection(observed.Blocks.Where(b=>b.Record==block.Record).Select(b=>(b.Start,b.End)));observed.FitSelection();}}
    private void PulseKindChanged(object? sender,SelectionChangedEventArgs e){if(PulseChart is not null)PulseChart.Kind=(AudioPlotKind)(1+Math.Max(0,PulseKind.SelectedIndex));}
    private void SpectralKindChanged(object? sender,SelectionChangedEventArgs e){if(SpectrumChart is not null)SpectrumChart.Kind=SpectralKind.SelectedIndex==1?AudioPlotKind.Spectrogram:AudioPlotKind.Spectrum;}
    private void PulseZoomIn(object? sender,RoutedEventArgs e)=>PulseChart.Zoom(1.5);
    private void PulseZoomOut(object? sender,RoutedEventArgs e)=>PulseChart.Zoom(1/1.5);
    private void PulseFit(object? sender,RoutedEventArgs e)=>PulseChart.Fit();
    private void SpectralZoomIn(object? sender,RoutedEventArgs e)=>SpectrumChart.Zoom(1.5);
    private void SpectralZoomOut(object? sender,RoutedEventArgs e)=>SpectrumChart.Zoom(1/1.5);
    private void SpectralFit(object? sender,RoutedEventArgs e)=>SpectrumChart.Fit();
    private void OpenGraph(object? sender,RoutedEventArgs e)=>ShowGraph(AudioPlotKind.Waveform);
    private void OpenPulseGraph(object? sender,RoutedEventArgs e)=>ShowGraph(PulseChart.Kind);
    private void OpenSpectralGraph(object? sender,RoutedEventArgs e)=>ShowGraph(SpectrumChart.Kind);
    private void ShowGraph(AudioPlotKind kind)
    {
        if(observed is null||TopLevel.GetTopLevel(this) is not Window owner)return;
        var chart=new AudioChart{Session=observed,Kind=kind};var toolbar=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=8};
        foreach(var (label,action) in new (string,Action)[]{("Zoom +",()=>chart.Zoom(1.5)),("Zoom −",()=>chart.Zoom(1/1.5)),("Fit",chart.Fit)})
        {var button=new Button{Content=label};button.Click+=(_,_)=>action();toolbar.Children.Add(button);}
        var grid=new Grid{RowDefinitions=new RowDefinitions("Auto,*"),RowSpacing=12,Margin=new Thickness(20)};grid.Children.Add(toolbar);Grid.SetRow(chart,1);grid.Children.Add(chart);
        var window=new Window{Title="Audio graph · "+kind,Width=1100,Height=680,Content=grid,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var documents=Model!.Documents.Documents;var session=observed;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler removed=(_,_)=>{if(!documents.Contains(session))window.Close();};
        ((System.Collections.Specialized.INotifyCollectionChanged)documents).CollectionChanged+=removed;
        window.Closed+=(_,_)=>{chart.Session=null;((System.Collections.Specialized.INotifyCollectionChanged)documents).CollectionChanged-=removed;};window.Show(owner);
    }
    private void LevelSelected(object? sender,SelectionChangedEventArgs e){if((sender as TableView)?.SelectedItem is AudioLevelRow row)observed?.SetError(row.Detail);}
    private void DiagnosticSelected(object? sender,SelectionChangedEventArgs e){if((sender as TableView)?.SelectedItem is AudioDiagnosticRow row)SelectPoint(row.Frame);}
    private void FailureSelected(object? sender,SelectionChangedEventArgs e){if((sender as TableView)?.SelectedItem is AudioFailureRow row)SelectRange(row.Start,row.End);}
    private void DifferenceSelected(object? sender,SelectionChangedEventArgs e){if((sender as TableView)?.SelectedItem is AudioDifferenceRow{Frame:{ } frame})SelectPoint(frame);}
    private void SelectPoint(long frame){if(observed is not null)SelectRange(Math.Clamp(frame,0,observed.Model.FrameCount-1),Math.Clamp(frame+1,1,observed.Model.FrameCount));}
    private void SelectRange(long first,long last){if(observed is not null&&first>=0&&last>first&&last<=observed.Model.FrameCount){observed.SetSelection([(first,last)]);observed.FitSelection();}}
}
