using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MZTools.Desktop.Controls;

namespace MZTools.Desktop.Views;

public sealed partial class DiskVisualWindow
{
    private readonly DskMfmDetailView mfmDetail=new();
    private readonly CheckBox showMfm=new() {Content="Unrolled MFM detail",Margin=new Thickness(6)};
    private readonly CheckBox autoFloating=new() {Content="Floating selection details",IsChecked=true,Margin=new Thickness(6)};
    private Grid surfaceBody=null!;
    private Control mfmPanel=null!, mfmDivider=null!;
    private Window? floatingDetails;
    private DskReportView? floatingReport;
    private DskGeneratedTrackCache? generatedCache;
    private CancellationTokenSource? detailWork;
    private int mfmTrack=-1;
    private Control CreateSurfaceView()
    {
        var view=new DockPanel(); var tools=new WrapPanel(); DockPanel.SetDock(tools,Dock.Top); view.Children.Add(tools);
        DockPanel.SetDock(cards,Dock.Top); view.Children.Insert(0,cards);
        tools.Children.Add(showMfm); tools.Children.Add(autoFloating);
        tools.Children.Add(new TextBlock {Text=snapshot.IsPhysical?"Stored HFE cells, not measured flux.":"MFM is generated from DSK, not captured flux.",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6)});
        surfaceBody=new Grid(); surfaceBody.RowDefinitions.Add(new() {Height=new GridLength(3,GridUnitType.Star),MinHeight=60});
        surfaceBody.RowDefinitions.Add(new() {Height=new GridLength(0)}); surfaceBody.RowDefinitions.Add(new() {Height=new GridLength(0)});
        surfaceBody.Children.Add(surfaceCanvas);
        var panel=new DockPanel(); var controls=new WrapPanel(); DockPanel.SetDock(controls,Dock.Top); panel.Children.Add(controls);
        void Button(string text,Action action) { var button=new Button {Content=text,Margin=new Thickness(3)}; button.Click+=(_,_)=>action(); controls.Children.Add(button); }
        Button("Fit track",()=>mfmDetail.Fit()); Button("Selected sector",()=> { if(selected!=null) mfmDetail.FitSector(selected.Sector); });
        Button("Zoom +",()=>mfmDetail.ZoomBy(2)); Button("Zoom −",()=>mfmDetail.ZoomBy(.5));
        panel.Children.Add(mfmDetail); mfmPanel=panel; panel.IsVisible=false; Grid.SetRow(panel,2); surfaceBody.Children.Add(panel);
        var splitter=new GridSplitter {Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=Brushes.DarkGray,IsVisible=false};
        mfmDivider=splitter; Grid.SetRow(splitter,1); surfaceBody.Children.Add(splitter);
        view.Children.Add(surfaceBody); return view;
    }
    private void InitializeFloatingAndMfm()
    {
        details.PropertyChanged+=(_,e)=>{if(e.Property.Name==nameof(DskReportView.Text)&&floatingReport!=null)floatingReport.Text=details.Text;};
        autoFloating.IsCheckedChanged+=(_,_)=> { if(autoFloating.IsChecked==true&&selected!=null)OpenFloatingDetails();else if(autoFloating.IsChecked!=true)floatingDetails?.Hide(); };

        showMfm.IsCheckedChanged+=(_,_)=>{bool visible=showMfm.IsChecked==true;SetMfmVisibility(visible);if(visible)_=LoadMfmDetail();else detailWork?.Cancel();};

        surfaceCanvas.ZoomChanged+=()=> { if(surfaceCanvas.Zoom>=6 && selected!=null) showMfm.IsChecked=true; };
        mfmDetail.Selected+=(address,modifiers)=>RequestGesture(address.Track,address,false,modifiers);
    }
    private void SetMfmVisibility(bool visible)
    {
        surfaceBody.RowDefinitions[1].Height=new GridLength(visible?6:0);
        surfaceBody.RowDefinitions[2].Height=visible?new GridLength(2,GridUnitType.Star):new GridLength(0);
        surfaceBody.RowDefinitions[2].MinHeight=visible?140:0;
        mfmPanel.IsVisible=mfmDivider.IsVisible=visible;
    }
    private void OpenFloatingDetails()
    {
        if(closed||!IsVisible) return;
        if(floatingDetails==null) {
            floatingReport=new DskReportView {Text=details.Text};
            var content=new DockPanel(); var actions=new WrapPanel(); DockPanel.SetDock(actions,Dock.Top); content.Children.Add(actions);
            var hexButton=new Button {Content="Open Hex…",Margin=new Thickness(4)}; hexButton.Click+=(_,_)=>OpenHex(); actions.Children.Add(hexButton);
            var ownerButton=new Button {Content="Select owner file",Margin=new Thickness(4)};
            ownerButton.Click+=(_,_)=> {
                var sector=CurrentSector(); if(sector==null || sector.Owners.Count==0) return;
                var menu=new ContextMenu();
                foreach(var owner in sector.Owners.DistinctBy(o=>o.FileKey)) {
                    var item=new MenuItem {Header=DskVisualSector.OwnerLabel(owner)}; item.Click+=(_,_)=>file(owner.FileKey); menu.Items.Add(item);
                }
                menu.Open(ownerButton);
            }; actions.Children.Add(ownerButton); content.Children.Add(floatingReport);
            floatingDetails=new Window {Title=snapshot.SourceFormat+" selection details — read-only",Content=content,DataContext=model,
                Width=620,Height=460,MinWidth=320,MinHeight=220,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            floatingDetails.Closed+=(_,_)=> { floatingDetails=null; floatingReport=null; autoFloating.IsChecked=false; };
        }
        autoFloating.IsChecked=true;
        if(!floatingDetails.IsVisible)floatingDetails.Show(this);
    }
    private void RefreshFloatingAndMfm()
    {
        if(closed) return;
        if(selected!=null && autoFloating.IsChecked==true && tabs.SelectedIndex==0) OpenFloatingDetails();
        if(showMfm.IsChecked==true && (mfmTrack!=selectedTrack || mfmDetail.Track==null)) _=LoadMfmDetail();
    }
    private async Task LoadMfmDetail()
    {
        if(closed || showMfm.IsChecked!=true) return;
        detailWork?.Cancel(); var cancellation=new CancellationTokenSource(); detailWork=cancellation;
        int version=revision, track=selectedTrack; var cache=generatedCache;
        mfmDetail.SetTrack(null,track); mfmTrack=-1;
        mfmDetail.Stored=snapshot.IsPhysical; mfmDetail.Message=snapshot.IsPhysical?"Loading stored cells…":"Generating MFM for selected track…"; mfmDetail.InvalidateVisual();
        try {
            await Task.Delay(80,cancellation.Token);
            var result=await cache!.GetAsync(track,cancellation.Token);
            if(closed || cancellation.IsCancellationRequested || version!=revision || track!=selectedTrack) return;
            mfmTrack=track; mfmDetail.SetTrack(result,track,selected?.Sector);
            surfaceCanvas.Generated=result; surfaceCanvas.Refresh();
        } catch(OperationCanceledException) { }
        catch(Exception ex) { if(!closed && !cancellation.IsCancellationRequested) { mfmDetail.Message="Track detail unavailable: "+ex.Message; mfmDetail.InvalidateVisual(); } }
        finally { if(ReferenceEquals(detailWork,cancellation)) detailWork=null; cancellation.Dispose(); }
    }
}
