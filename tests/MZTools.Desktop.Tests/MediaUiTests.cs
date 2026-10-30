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
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class MediaUiTests
{
    private sealed class Host : IDisposable
    {
        internal MediaDocumentTests Files { get; } = new();
        internal ShellViewModel Model { get; }
        internal MainWindow Window { get; }
        internal byte[]? Packet;
        internal TableView Table => Window.GetVisualDescendants().OfType<TableView>().Single(t => t.Name == "RecordsTable");
        internal Host()
        {
            Files.Dialogs.CloseDecision = CloseDecision.Discard;
            Model = new(Files.Coordinator,_=>Task.CompletedTask,Files.Dialogs,
                bytes=>{ Packet=bytes;return Task.CompletedTask; },()=>Task.FromResult(Packet));
            Window=new(Model);Window.Show(); Dispatcher.UIThread.RunJobs();
        }
        internal async Task<TapeSession> Open(bool quick=false)
        {
            byte[] bytes=MediaDocumentTests.Mzf("FIRST").Concat(MediaDocumentTests.Mzf("SECOND")).Concat(MediaDocumentTests.Mzf("THIRD")).ToArray();
            string path=Files.FilePath(quick?"flappy.qdf":"tape.mzt");
            if (quick)
            {
                string source=Files.FilePath("tape.mzt");File.WriteAllBytes(source,bytes);
                bytes=TapeDocumentIO.Serialize(TapeDocumentIO.Read(source),TapeDocumentFormat.Qdf,true);
            }
            File.WriteAllBytes(path,bytes);Files.Dialogs.OpenPath=path;
            Assert.True(await Model.Documents.OpenAsync(),Model.Documents.Error);
            Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
            return Model.ActiveTape!;
        }
        public void Dispose() { Window.Close(); Dispatcher.UIThread.RunJobs(); Files.Dispose(); }
    }
    private static void Capture(Window window,string name,double scale=1)
    {
        string? output=Environment.GetEnvironmentVariable("MZTOOLS_F4_OUTPUT");if(output is null)return;
        Directory.CreateDirectory(output);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
        using var bitmap=new RenderTargetBitmap(new PixelSize((int)(window.Bounds.Width*scale),(int)(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));
        bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact] public void NewCommandsCreateIndependentRealEmptyDocuments()
    {
        using var host=new Host();host.Model.NewTapeCommand.Execute(null);
        var tape=host.Model.ActiveTape!;Assert.Equal("Mzt",tape.Format);Assert.True(tape.IsModified);Assert.Empty(tape.Rows);
        host.Model.NewQuickDiskCommand.Execute(null);var disk=host.Model.ActiveTape!;Assert.Equal("Mzq",disk.Format);Assert.True(disk.IsQuickDisk);
        Assert.Equal(2,host.Model.Documents.Documents.Count);Assert.NotSame(tape,disk);Assert.True(host.Model.ShowMedia);
    }
    [AvaloniaFact] public async Task ActualTableSelectionAndMetadataApplyChangeCoreAndKeepRawDescription()
    {
        using var host=new Host();var tape=await host.Open();Assert.Equal(3,host.Table.ItemCount);
        host.Table.SelectedItem=tape.Rows[1];Dispatcher.UIThread.RunJobs();Assert.Equal("SECOND",tape.Name);
        var name=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Record name");
        name.Text="RENAMED";Dispatcher.UIThread.RunJobs();Assert.True(tape.HasDraft);Assert.False(host.Model.SaveCommand.CanExecute(null));
        host.Model.ApplyMetadataCommand.Execute(null);Dispatcher.UIThread.RunJobs();Assert.Empty(tape.Validation);
        Assert.Equal("RENAMED",new RecordRow(tape.Model.Records[1],1).Name);Assert.True(tape.IsModified);Assert.True(host.Model.SaveCommand.CanExecute(null));
        Assert.Equal(MediaDocumentTests.Mzf()[24..128],tape.Model.Records[1].GetSerializedHeader()[24..]);
    }
    [AvaloniaFact] public async Task MetadataValidationAppearsInRealInspectorWithoutApplyingBadFields()
    {
        using var host=new Host();var tape=await host.Open();tape.Name="ž";host.Model.ApplyMetadataCommand.Execute(null);Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(tape.Validation);Assert.False(tape.IsModified);
        Assert.Contains(host.Window.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text==tape.Validation&&t.IsEffectivelyVisible);
        Capture(host.Window,"metadata-validation.png");
    }
    [AvaloniaFact] public async Task ImportCancelThenApplyUsesRealFilesAndKeepsOriginalPath()
    {
        using var host=new Host();var tape=await host.Open();var source=tape.Path;
        string path=host.Files.FilePath("add.mzf");File.WriteAllBytes(path,MediaDocumentTests.Mzf("ADDED"));
        host.Files.Dialogs.ImportPaths=[path];host.Files.Dialogs.Apply=false;host.Model.ImportCommand.Execute(null);
        await Until(()=>!host.Model.Documents.IsBusy);Assert.Equal(3,tape.Model.Records.Count);
        host.Files.Dialogs.Apply=true;host.Model.ImportCommand.Execute(null);await Until(()=>!host.Model.Documents.IsBusy);
        Assert.Equal(4,tape.Model.Records.Count);Assert.Equal(source,tape.Path);Assert.True(tape.IsModified);
    }
    [AvaloniaFact] public async Task CopyPasteAcrossDocumentsUsesCodecAndKeepsSourceUntouched()
    {
        using var host=new Host();var source=await host.Open();host.Table.SelectedItems!.Clear();host.Table.SelectedItems.Add(source.Rows[0]);host.Table.SelectedItems.Add(source.Rows[2]);
        Dispatcher.UIThread.RunJobs();host.Model.CopyRecordsCommand.Execute(null);await Until(()=>host.Packet is not null);
        Assert.Equal(2,WorkspaceDragCodec.OpenRecords(WorkspaceDragCodec.Decode(host.Packet!)).Length);
        host.Model.NewQuickDiskCommand.Execute(null);host.Model.PasteRecordsCommand.Execute(null);await Until(()=>!host.Model.Documents.IsBusy);
        Assert.Equal(2,host.Model.ActiveTape!.Rows.Count);Assert.Equal(3,source.Model.Records.Count);Assert.False(source.IsModified);
    }
    [AvaloniaFact] public async Task SelectingOlderTapeTabFromQuickDiskKeepsTheRequestedDocument()
    {
        using var host=new Host();var first=await host.Open();host.Model.NewTapeCommand.Execute(null);var second=host.Model.ActiveTape;
        host.Model.NewQuickDiskCommand.Execute(null);host.Model.Documents.Active=first;Dispatcher.UIThread.RunJobs();
        Assert.Same(first,host.Model.ActiveTape);Assert.NotSame(second,host.Model.ActiveTape);Assert.Equal("Tape",host.Model.SelectedModule!.Name);
    }
    [AvaloniaFact] public async Task MapZoomAndSelectionSurviveDocumentSwitch()
    {
        using var host=new Host();var tape=await host.Open(true);
        host.Window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="RecordTabs").SelectedIndex=1;Dispatcher.UIThread.RunJobs();
        var map=host.Window.GetVisualDescendants().OfType<RegionMap>().Single();map.Zoom(1.5);double length=tape.MapLength;
        host.Model.NewTapeCommand.Execute(null);host.Model.Documents.Active=tape;Dispatcher.UIThread.RunJobs();Assert.Equal(length,tape.MapLength);
        map.Fit();Assert.Equal(0,tape.MapLength);Assert.NotNull(tape.Snapshot);Assert.Contains("bytes",tape.MapSummary,StringComparison.OrdinalIgnoreCase);
    }
    [AvaloniaFact] public async Task RealPointerMapSelectionUsesCoreRangesAndCtrlTogglesMembership()
    {
        using var host=new Host();var tape=await host.Open(true);
        host.Window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="RecordTabs").SelectedIndex=1;Dispatcher.UIThread.RunJobs();
        var map=host.Window.GetVisualDescendants().OfType<RegionMap>().Single();
        tape.Selection.Clear();tape.Selection.Add(0);map.FitSelection();Dispatcher.UIThread.RunJobs();
        var region=tape.Snapshot!.Layout.Regions.First(r=>r.FileIndex==0&&r.Kind==QuickDiskRegionKind.Payload);
        double x=(region.Start+region.Length/2d-tape.MapStart)/tape.MapLength*map.Bounds.Width;
        var point=map.TranslatePoint(new Point(x,40),host.Window)!.Value;
        host.Window.MouseDown(point,MouseButton.Left,RawInputModifiers.None);host.Window.MouseUp(point,MouseButton.Left,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
        Assert.Equal(0,tape.SelectedRow!.Index);Assert.Contains(0,tape.Selection);Assert.True(tape.HasRegion);
        var primary=OperatingSystem.IsMacOS()?RawInputModifiers.Meta:RawInputModifiers.Control;
        host.Window.MouseDown(point,MouseButton.Left,primary);host.Window.MouseUp(point,MouseButton.Left,primary);Dispatcher.UIThread.RunJobs();
        Assert.Empty(tape.Selection);Assert.Null(tape.SelectedRow);
    }
    [AvaloniaFact] public async Task ActualMetadataTypingRetainsFocusAndDoesNotAffectSourceUntilApply()
    {
        using var host=new Host();var tape=await host.Open();
        var input=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Record name");
        Assert.True(input.Focus());input.SelectAll();host.Window.KeyTextInput("NEW");host.Window.KeyTextInput(" NAME");Dispatcher.UIThread.RunJobs();
        Assert.True(input.IsFocused);Assert.Equal("NEW NAME",tape.Name);Assert.Equal("FIRST",tape.Rows[0].Name);Assert.True(tape.HasDraft);
    }
    [AvaloniaFact] public async Task SaveAsConversionChangesModuleAndKeepsOriginalSource()
    {
        using var host=new Host();var tape=await host.Open();string original=tape.Path!;byte[] bytes=File.ReadAllBytes(original);
        host.Files.Dialogs.Save=new(host.Files.FilePath("converted.qdf"),"Qdf");
        Assert.True(await host.Model.Documents.SaveAsync(true),host.Model.Documents.Error);Dispatcher.UIThread.RunJobs();
        Assert.Equal("QuickDisk",host.Model.SelectedModule!.Name);Assert.True(tape.IsQuickDisk);Assert.Equal(bytes,File.ReadAllBytes(original));
    }
    [AvaloniaFact] public async Task RoutedDropReordersActualTableAndKeepsMovedRecordsSelected()
    {
        using var host=new Host();var tape=await host.Open();
        var transfer=RecordWorkspace.CaptureDrag(tape);
        host.Table.RaiseEvent(new DragEventArgs(DragDrop.DropEvent,transfer,host.Table,new Point(50,host.Table.Bounds.Height-5),KeyModifiers.None));
        await Until(()=>!host.Model.Documents.IsBusy);Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[]{"SECOND","THIRD","FIRST"},tape.Model.Records.Select((r,i)=>new RecordRow(r,i).Name));
        Assert.Equal("FIRST",tape.SelectedRow!.Name);Assert.Contains(2,tape.Selection);Assert.True(tape.IsModified);
    }
    [AvaloniaFact] public async Task RoutedDropAcrossDocumentsCopiesCodecPacketWithoutEditingSource()
    {
        using var host=new Host();var source=await host.Open();var transfer=RecordWorkspace.CaptureDrag(source);
        host.Model.NewQuickDiskCommand.Execute(null);Dispatcher.UIThread.RunJobs();
        host.Table.RaiseEvent(new DragEventArgs(DragDrop.DropEvent,transfer,host.Table,new Point(50,100),KeyModifiers.None));
        await Until(()=>!host.Model.Documents.IsBusy);Dispatcher.UIThread.RunJobs();
        Assert.Single(host.Model.ActiveTape!.Rows);Assert.Equal("FIRST",host.Model.ActiveTape.Rows[0].Name);
        Assert.Equal(3,source.Rows.Count);Assert.False(source.IsModified);
    }
    [AvaloniaFact] public async Task F6ReachesActualInspectorAndContextMenuUsesSharedCommands()
    {
        using var host=new Host();await host.Open();
        var filter=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="RecordFilter");Assert.True(filter.Focus());
        host.Window.KeyPress(Key.F6,RawInputModifiers.None,PhysicalKey.F6,null);host.Window.KeyRelease(Key.F6,RawInputModifiers.None,PhysicalKey.F6,null);Dispatcher.UIThread.RunJobs();
        var name=host.Window.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Record name");Assert.True(name.IsFocused);
        var menu=host.Table.ContextMenu!;menu.Open(host.Table);Dispatcher.UIThread.RunJobs();
        try { Assert.Same(host.Model.CopyRecordsCommand,menu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"Copy records")).Command); }
        finally {menu.Close();}
    }
    [AvaloniaFact] public async Task EnterOnRecordOpensTheSharedDetailComponent()
    {
        using var host=new Host();await host.Open();Assert.True(host.Table.ContainerFromIndex(0)!.Focus());
        host.Window.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);host.Window.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);Dispatcher.UIThread.RunJobs();
        var detail=Assert.Single(host.Window.OwnedWindows);Assert.Equal("Record inspector",detail.Title);Assert.IsType<RecordInspector>(detail.Content);detail.Close();
    }
    [AvaloniaFact] public async Task DeleteCancelAndMoveCommandsRespectSelection()
    {
        using var host=new Host();var tape=await host.Open();host.Table.SelectedItem=tape.Rows[1];Dispatcher.UIThread.RunJobs();
        host.Model.MoveUpCommand.Execute(null);await Until(()=>!host.Model.Documents.IsBusy);Assert.Equal("SECOND",new RecordRow(tape.Model.Records[0],0).Name);
        host.Files.Dialogs.Apply=false;host.Model.DeleteRecordsCommand.Execute(null);await Until(()=>!host.Model.Documents.IsBusy);Assert.Equal(3,tape.Rows.Count);
        host.Files.Dialogs.Apply=true;host.Model.DeleteRecordsCommand.Execute(null);await Until(()=>!host.Model.Documents.IsBusy);Assert.Equal(2,tape.Rows.Count);
    }
    [AvaloniaFact] public async Task UnknownHostIsVisibleButCannotBeEditedOrSaved()
    {
        using var host=new Host();var track=new byte[4096];new Random(123).NextBytes(track);
        var profile=new QuickDiskPhysicalProfile(QdImageFormat.HxcPhysical,4096,4096,0,4096,0xAA,203389,5120);
        string path=host.Files.FilePath("unknown.qd");File.WriteAllBytes(path,HxcFlashFloppyQdContainer.Write(track,profile));host.Files.Dialogs.OpenPath=path;
        Assert.True(await host.Model.Documents.OpenAsync(),host.Model.Documents.Error);Dispatcher.UIThread.RunJobs();var tape=host.Model.ActiveTape!;
        Assert.False(tape.CanEdit);Assert.False(host.Model.SaveAsCommand.CanExecute(null));Assert.False(host.Model.ImportCommand.CanExecute(null));Assert.NotNull(tape.Snapshot);
        var tabs=host.Window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="RecordTabs");tabs.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Capture(host.Window,"unknown-host.png");
    }
    [AvaloniaTheory] [InlineData(false,1440,960,1.0)] [InlineData(true,1440,960,1.0)]
    [InlineData(false,1366,768,1.0)] [InlineData(true,920,640,1.0)] [InlineData(false,640,480,1.0)]
    [InlineData(false,1366,768,1.5)] [InlineData(true,1366,768,2.0)]
    public async Task ActualMediaLayoutsAndSkiaReviewFrames(bool quick,int width,int height,double scale)
    {
        using var host=new Host();var tape=await host.Open(quick);host.Window.Width=width;host.Window.Height=height;
        Dispatcher.UIThread.RunJobs();host.Window.UpdateLayout();
        Assert.True(host.Model.ShowMedia);Assert.True(host.Table.IsEffectivelyVisible);Assert.True(host.Table.Bounds.Height>0);Assert.Equal(3,host.Table.ItemCount);
        var origin=host.Table.TranslatePoint(default,host.Window)!.Value;Assert.InRange(origin.X,0,width);Assert.True(origin.X+host.Table.Bounds.Width<=width+1);
        Capture(host.Window,FormattableString.Invariant($"{(quick?"quickdisk":"tape")}-{width}x{height}-{scale:0.0}.png"),scale);
        if(quick)
        {
            host.Window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="RecordTabs").SelectedIndex=1;Dispatcher.UIThread.RunJobs();
            Capture(host.Window,FormattableString.Invariant($"quickdisk-map-{width}x{height}-{scale:0.0}.png"),scale);
        }
    }
    private static async Task Until(Func<bool> condition)
    {for(int i=0;i<300&&!condition();i++){Dispatcher.UIThread.RunJobs();await Task.Delay(10,TestContext.Current.CancellationToken);}Assert.True(condition());}
}
