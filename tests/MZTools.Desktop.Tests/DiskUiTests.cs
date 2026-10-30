using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class DiskUiTests
{
    private sealed class Host : IDisposable
    {
        internal DiskDocumentTests Files {get;}=new();
        internal MainWindow Window {get;}
        internal Host(){Window=new(Files.Shell);Window.Show();Dispatcher.UIThread.RunJobs();}
        internal TableView Table=>Window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="DiskFiles");
        internal DiskMap Map=>Window.GetVisualDescendants().OfType<DiskMap>().Single();
        internal TabControl Tabs=>Window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="DiskTabs");
        internal async Task<DiskSession> Open(bool hfe=false)
        {
            var model=DskDocumentFactory.CreateFsmz(false);model.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);model.FileSystem.Insert("SECOND",[4,5,6],1,0x5000,0x5000);model.MarkModified();
            if(hfe)model=DiskFormatService.SetNewContainer(model,HfeVersion.V3);
            var session=await Files.Open(model,hfe?"image.hfe":"image.dsk");Dispatcher.UIThread.RunJobs();Window.UpdateLayout();return session;
        }
        public void Dispose(){Window.Close();Dispatcher.UIThread.RunJobs();Files.Dispose();}
    }
    private static void Capture(Window window,string name,double scale=1)
    {
        var output=Environment.GetEnvironmentVariable("MZTOOLS_F5_OUTPUT");if(output is null)return;Directory.CreateDirectory(output);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
        using var bitmap=new RenderTargetBitmap(new PixelSize((int)(window.Bounds.Width*scale),(int)(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
    }
    [AvaloniaFact]public async Task RealFilesTableSelectionPropertiesAndSaveAsReload()
    {
        using var host=new Host();var s=await host.Open();Assert.True(host.Files.Shell.ShowDisk);Assert.Equal(2,host.Table.ItemCount);
        host.Table.SelectedItem=s.Files[0];Dispatcher.UIThread.RunJobs();Assert.Equal("FIRST",s.SelectedFile!.Name);Assert.Contains(s.SelectedSector!.Sector.Owners,o=>o.FileKey==s.SelectedFile.Key);
        var input=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Disk filename");Assert.True(input.Focus());input.SelectAll();host.Window.KeyTextInput("RENAMED");Dispatcher.UIThread.RunJobs();
        Assert.True(s.HasDraft);Assert.True(input.IsFocused);Assert.False(host.Files.Shell.SaveCommand.CanExecute(null));
        host.Files.Shell.DiskApplyPropertiesCommand.Execute(null);await Until(()=>!host.Files.Coordinator.IsBusy);Assert.False(s.HasDraft);Assert.Contains(s.Files,f=>f.Name=="RENAMED");
        host.Files.Dialogs.Save=new(host.Files.FilePath("saved.dsk"),"Dsk",false);Assert.True(await host.Files.Coordinator.SaveAsync(true),host.Files.Coordinator.Error);Assert.Contains(DskDocument.Open(s.Path!).FileSystem.ReadDirectory(),f=>f.Name=="RENAMED");
        Capture(host.Window,"disk-files.png");
    }
    [AvaloniaFact]public async Task MapPointerSelectionCtrlShiftAndViewStateSurviveModuleSwitch()
    {
        using var host=new Host();var s=await host.Open();host.Tabs.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
        host.Map.Zoom(8);double zoom=s.MapZoom;
        var row=s.Sectors[0];var point=host.Map.TranslatePoint(new Point(70,30),host.Window)!.Value;
        host.Window.MouseDown(point,MouseButton.Left,RawInputModifiers.None);host.Window.MouseUp(point,MouseButton.Left,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();Assert.NotNull(s.SelectedSector);Assert.NotEmpty(s.SectorSelection);
        var primary=OperatingSystem.IsMacOS()?RawInputModifiers.Meta:RawInputModifiers.Control;
        host.Window.MouseDown(point,MouseButton.Left,primary);host.Window.MouseUp(point,MouseButton.Left,primary);Dispatcher.UIThread.RunJobs();Assert.Empty(s.SectorSelection);
        host.Files.Shell.NewTapeCommand.Execute(null);await Until(()=>!host.Files.Coordinator.IsBusy);host.Files.Coordinator.Active=s;Dispatcher.UIThread.RunJobs();Assert.Equal(zoom,s.MapZoom);Assert.Equal(1,s.ViewIndex);
        Capture(host.Window,"disk-map.png");
    }
    [AvaloniaFact]public async Task ActualHexTypingDraftCancelAndCoreApply()
    {
        using var host=new Host();var s=await host.Open();s.SelectedSector=s.Sectors.First(x=>(x.Sector.Role&DskSectorRole.Free)!=0);host.Tabs.SelectedIndex=3;Dispatcher.UIThread.RunJobs();
        var input=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="DiskHex");Assert.True(input.Focus());input.SelectAll();host.Window.KeyTextInput("00");Dispatcher.UIThread.RunJobs();Assert.True(s.HasHexDraft);
        host.Files.Shell.DiskApplyHexCommand.Execute(null);await Until(()=>!host.Files.Coordinator.IsBusy);Assert.NotNull(host.Files.Coordinator.Error);Assert.True(s.HasHexDraft);
        host.Files.Shell.DiskCancelDraftCommand.Execute(null);await Until(()=>!host.Files.Coordinator.IsBusy);Assert.False(s.HasDraft);
        var bytes=s.SelectedSector!.Sector.Data.ToArray();bytes[0]^=1;s.HexText=DskHexEditService.FormatHex(bytes);host.Files.Shell.DiskApplyHexCommand.Execute(null);await Until(()=>!host.Files.Coordinator.IsBusy);Assert.Null(host.Files.Coordinator.Error);Assert.False(s.HasDraft);
        Capture(host.Window,"disk-hex.png");
    }
    [AvaloniaFact]public async Task F6AndEnterOpenSharedInspectorWithContextCommands()
    {
        using var host=new Host();var s=await host.Open();host.Table.SelectedItem=s.Files[0];Dispatcher.UIThread.RunJobs();Assert.True(host.Table.ContainerFromIndex(0)!.Focus());
        host.Window.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);host.Window.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);Dispatcher.UIThread.RunJobs();
        var detail=Assert.Single(host.Window.OwnedWindows);Assert.IsType<DiskInspector>(detail.Content);detail.Close();
        var menu=host.Table.ContextMenu!;menu.Open(host.Table);Dispatcher.UIThread.RunJobs();try{Assert.Same(host.Files.Shell.DiskCopyCommand,menu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"Copy files")).Command);}finally{menu.Close();}
    }
    [AvaloniaFact]public async Task RoutedCrossDiskDropUsesCorePreviewAndKeepsSource()
    {
        using var host=new Host();var source=await host.Open();host.Table.SelectedItem=source.Files[0];Dispatcher.UIThread.RunJobs();byte[] before=source.SerializeContainer();var transfer=DiskWorkspace.CaptureDrag(source);
        var target=DiskSession.New(new(DskNewFormat.MzBasic,40,2));host.Files.Coordinator.Add(target);Dispatcher.UIThread.RunJobs();
        host.Table.RaiseEvent(new DragEventArgs(DragDrop.DropEvent,transfer,host.Table,new Point(60,100),KeyModifiers.None));await Until(()=>!host.Files.Coordinator.IsBusy);Assert.Null(host.Files.Coordinator.Error);Assert.Single(target.Files);Assert.Equal(before,source.SerializeContainer());
    }
    [AvaloniaFact]public async Task NativeHfeTrackViewLoadsExistingCellsAndShowsSourceEvidence()
    {
        using var host=new Host();var s=await host.Open(true);host.Tabs.SelectedIndex=4;Dispatcher.UIThread.RunJobs();host.Files.Shell.DiskLoadTrackCommand.Execute(null);await Until(()=>!host.Files.Coordinator.IsBusy);
        Assert.Null(host.Files.Coordinator.Error);Assert.NotNull(s.TrackData);Assert.Contains("Stored HFE",s.PhysicalReport);Capture(host.Window,"hfe-physical.png");
    }
    [AvaloniaFact]public async Task RepairOpenStateIsVisibleAndOriginalRemainsUntouched()
    {
        using var host=new Host();var s=await host.Files.Damaged();Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();Assert.True(s.RequiresRepairSaveAs);Assert.False(host.Files.Shell.SaveCommand.CanExecute(null));Assert.True(host.Files.Shell.SaveAsCommand.CanExecute(null));Assert.Contains("original is protected",s.SaveDisabledReason);Capture(host.Window,"disk-repair.png");
    }
    [AvaloniaFact]public async Task SurfaceHitZoomFitAndF6UseSharedSelectionAndInspector()
    {
        using var host=new Host();var session=await host.Open();host.Tabs.SelectedIndex=1;session.MapMode=1;Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();
        var map=host.Map;Assert.Equal(1,map.Mode);var band=new DskSurfaceRenderModel(session.Snapshot,0,true).Bands.First(b=>b.Sectors.Count>0);var span=band.Sectors[0];
        double angle=(span.Start+span.End)*Math.PI,r=(band.Inner+band.Outer)/2,radius=Math.Min(map.Bounds.Width,map.Bounds.Height-28)/2-8;
        var point=new Point(map.Bounds.Width/2+Math.Sin(angle)*r*radius,(map.Bounds.Height+24)/2-Math.Cos(angle)*r*radius);Assert.Equal(span.Address,map.Hit(point)!.Sector.Address);
        session.SelectSector(map.Hit(point)!);map.Zoom(1.5);Assert.Equal(1.5,session.SurfaceZoom);map.FitSelection();Assert.True(session.SurfaceZoom>1);map.Fit();Assert.Equal(1,session.SurfaceZoom);
        session.SelectedFile=session.Files[0];host.Tabs.SelectedIndex=0;Dispatcher.UIThread.RunJobs();var filter=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="DiskFilter");Assert.True(filter.Focus());
        host.Window.KeyPress(Key.F6,RawInputModifiers.None,PhysicalKey.F6,null);host.Window.KeyRelease(Key.F6,RawInputModifiers.None,PhysicalKey.F6,null);Dispatcher.UIThread.RunJobs();
        var input=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Disk filename");Assert.True(input.IsFocused);
        host.Tabs.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Capture(host.Window,"disk-surface.png");
    }
    [AvaloniaFact]public async Task FloatingPhysicalDetailUsesSameComponentAndLiveViewport()
    {
        using var host=new Host();var session=await host.Open();host.Tabs.SelectedIndex=4;Dispatcher.UIThread.RunJobs();host.Files.Shell.DiskLoadTrackCommand.Execute(null);await Until(()=>!host.Files.Coordinator.IsBusy);
        var button=host.Window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Open physical detail"));button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        var detail=Assert.Single(host.Window.OwnedWindows);Assert.IsType<DiskPhysicalPanel>(detail.Content);
        var embedded=host.Window.GetVisualDescendants().OfType<DiskTrackView>().Single();var floating=detail.GetVisualDescendants().OfType<DiskTrackView>().Single();
        Assert.Same(session,embedded.Session);Assert.Same(session,floating.Session);double before=session.CellLength;floating.Zoom(1.5);Assert.True(embedded.Session!.CellLength<before);detail.Close();
    }
    [AvaloniaFact]public async Task IplLocalRoutedDragReordersUsingCoreWriterAndKeepsPrograms()
    {
        using var host=new Host();string path=host.Files.FilePath("program.mzf");File.WriteAllBytes(path,MediaDocumentTests.Mzf("ONE"));var first=DiskOperations.ReadIplFiles([path],TestContext.Current.CancellationToken)[0];
        var model=DiskOperations.BuildIpl([first,first with{DisplayName="TWO"},first with{DisplayName="THREE"}],false);var session=await host.Files.Open(model);Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();
        host.Table.SelectedItem=session.Files[0];Dispatcher.UIThread.RunJobs();var transfer=DiskWorkspace.CaptureDrag(session);host.Table.RaiseEvent(new DragEventArgs(DragDrop.DropEvent,transfer,host.Table,new Point(60,host.Table.Bounds.Height-3),KeyModifiers.None));await Until(()=>!host.Files.Coordinator.IsBusy);
        Assert.Null(host.Files.Coordinator.Error);Assert.Equal(new[]{"TWO","THREE","ONE"},DiskOperations.IplInputs(session).Select(i=>i.DisplayName));Assert.True(session.IsModified);
    }
    [AvaloniaFact]public async Task SaveAsReadonlyCopyRefreshesActualEditorCapabilities()
    {
        using var host=new Host();var session=await host.Open();host.Table.SelectedItem=session.Files[0];session.Loaded(session.Path!,session.SavedFingerprint!,true);Dispatcher.UIThread.RunJobs();
        var input=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Disk filename");Assert.False(input.IsEffectivelyEnabled);
        host.Files.Dialogs.Save=new(host.Files.FilePath("editable-copy.dsk"),"Dsk",false);Assert.True(await host.Files.Coordinator.SaveAsync(true),host.Files.Coordinator.Error);Dispatcher.UIThread.RunJobs();Assert.True(input.IsEffectivelyEnabled);
    }
    [AvaloniaTheory][InlineData(640,480,1.0)][InlineData(1366,768,1.25)][InlineData(1366,768,1.5)][InlineData(1366,768,2.0)][InlineData(1920,1080,1.0)]
    public async Task ActualDiskViewportAndDpiMatrix(int width,int height,double scale)
    {
        using var host=new Host();await host.Open();host.Window.Width=width;host.Window.Height=height;Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();Assert.True(host.Tabs.IsEffectivelyVisible);Assert.True(host.Table.Bounds.Width>100);Capture(host.Window,FormattableString.Invariant($"disk-{width}x{height}-{scale:0.00}.png"),scale);
    }
    private static async Task Until(Func<bool> condition){for(int i=0;i<200&&!condition();i++){Dispatcher.UIThread.RunJobs();await Task.Delay(10,TestContext.Current.CancellationToken);}Assert.True(condition());}
}
