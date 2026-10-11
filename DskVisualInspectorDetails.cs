using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MZTools;

internal sealed partial class DskVisualInspectorWindow
{
    private readonly DskMfmDetailControl mfmDetail=new();
    private readonly CheckBox showMfm=new() {Content="Unrolled MFM detail",Margin=new Thickness(6)};
    private readonly CheckBox autoFloating=new() {Content="Floating selection details",IsChecked=true,Margin=new Thickness(6)};
    private Grid surfaceBody=null!;
    private FrameworkElement mfmPanel=null!, mfmDivider=null!;
    private Window? floatingDetails;
    private ReportView? floatingReport;
    private DskGeneratedTrackCache? generatedCache;
    private CancellationTokenSource? detailWork;
    private int mfmTrack=-1;
    private FrameworkElement CreateSurfaceView()
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
        panel.Children.Add(mfmDetail); mfmPanel=panel; panel.Visibility=Visibility.Collapsed; Grid.SetRow(panel,2); surfaceBody.Children.Add(panel);
        var splitter=new GridSplitter {Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=SystemColors.ControlDarkBrush,Visibility=Visibility.Collapsed};
        mfmDivider=splitter; Grid.SetRow(splitter,1); surfaceBody.Children.Add(splitter);
        view.Children.Add(surfaceBody); return view;
    }
    private void InitializeFloatingAndMfm()
    {
        details.ReportChanged+=report=>floatingReport?.Show(report);
        autoFloating.Checked+=(_,_)=> { if(selected!=null) OpenFloatingDetails(); };
        autoFloating.Unchecked+=(_,_)=>floatingDetails?.Hide();
        showMfm.Checked+=(_,_)=> { SetMfmVisibility(true); _=LoadMfmDetail(); };
        showMfm.Unchecked+=(_,_)=> { detailWork?.Cancel(); SetMfmVisibility(false); };
        surfaceCanvas.ZoomChanged+=()=> { if(surfaceCanvas.Zoom>=6 && selected!=null) showMfm.IsChecked=true; };
        mfmDetail.Selected+=address=>RequestGesture(address.Track,address,false,Keyboard.Modifiers);
    }
    private void SetMfmVisibility(bool visible)
    {
        surfaceBody.RowDefinitions[1].Height=new GridLength(visible?6:0);
        surfaceBody.RowDefinitions[2].Height=visible?new GridLength(2,GridUnitType.Star):new GridLength(0);
        surfaceBody.RowDefinitions[2].MinHeight=visible?140:0;
        mfmPanel.Visibility=mfmDivider.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
    }
    private void OpenFloatingDetails()
    {
        if(closed) return;
        if(floatingDetails==null) {
            floatingReport=new ReportView {CompactHeader=true}; floatingReport.Show(details.Model);
            var content=new DockPanel(); var actions=new WrapPanel(); DockPanel.SetDock(actions,Dock.Top); content.Children.Add(actions);
            var hexButton=new Button {Content="Open Hex…",Margin=new Thickness(4)}; hexButton.Click+=(_,_)=>OpenHex(); actions.Children.Add(hexButton);
            var ownerButton=new Button {Content="Select owner file",Margin=new Thickness(4)};
            ownerButton.Click+=(_,_)=> {
                var sector=CurrentSector(); if(sector==null || sector.Owners.Count==0) return;
                var menu=new ContextMenu();
                foreach(var owner in sector.Owners.DistinctBy(o=>o.FileKey)) {
                    var item=new MenuItem {Header=DskVisualSector.OwnerLabel(owner)}; item.Click+=(_,_)=>SelectOwnerFile(owner.FileKey); menu.Items.Add(item);
                }
                menu.PlacementTarget=ownerButton; menu.IsOpen=true;
            }; actions.Children.Add(ownerButton); content.Children.Add(floatingReport);
            floatingDetails=new Window {Owner=this,Title=snapshot.SourceFormat+" selection details — read-only",Content=content,
                Width=Math.Min(620,SystemParameters.WorkArea.Width-20),Height=Math.Min(460,SystemParameters.WorkArea.Height-20),
                MinWidth=320,MinHeight=220,ShowActivated=false,ShowInTaskbar=false,ResizeMode=ResizeMode.CanResizeWithGrip,
                Left=Math.Max(SystemParameters.WorkArea.Left,Math.Min(Left+ActualWidth-640,SystemParameters.WorkArea.Right-640)),
                Top=Math.Max(SystemParameters.WorkArea.Top,Math.Min(Top+100,SystemParameters.WorkArea.Bottom-480))};
            floatingDetails.Closed+=(_,_)=> { floatingDetails=null; floatingReport=null; autoFloating.IsChecked=false; };
        }
        autoFloating.IsChecked=true;
        if(!floatingDetails.IsVisible) floatingDetails.Show();
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
