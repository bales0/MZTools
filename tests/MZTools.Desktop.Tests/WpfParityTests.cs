using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class WpfParityTests
{
    [AvaloniaFact]public async Task ReadonlyInspectorsShareCoreSelectionAndNeverModifyTheSource()
    {
        using var files=new DiskDocumentTests();var session=await files.Open(DskDocumentFactory.CreateFsmz(false));byte[] before=session.SerializeContainer();
        var owner=new MainWindow(files.Shell);owner.Show();var inspector=new DiskVisualWindow(files.Shell);inspector.Show(owner);
        try
        {
            Dispatcher.UIThread.RunJobs();Assert.Equal("Visual DSK Inspector — read-only",inspector.Title);
            var tabs=inspector.GetVisualDescendants().OfType<TabControl>().Single();Assert.Equal(new[]{"Disk Surface","Track / Sector","Issues","Statistics","Tracks","Unrolled MFM detail"},tabs.Items.OfType<TabItem>().Select(t=>t.Header));
            var maps=inspector.GetVisualDescendants().OfType<MZTools.Desktop.Controls.DiskMap>().ToArray();Assert.Equal(2,maps.Length);Assert.All(maps,m=>Assert.Same(session,m.Session));
            session.SelectSector(session.Sectors[0]);maps[0].Zoom(1.2);Assert.Equal(before,session.SerializeContainer());Assert.False(session.IsModified);
            Capture(inspector,"wpf-disk-inspector.png");
        }finally{inspector.Close();owner.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task HexBrowserIncludesTheEntireRecordAndTrailingBytes()
    {
        using var files=new MediaDocumentTests();byte[] bytes=MediaDocumentTests.Mzf("FULL HEX",4097).Concat(new byte[]{0xFA,0xFB,0xFC}).ToArray();File.WriteAllBytes(files.FilePath("hex.mzf"),bytes);var tape=await files.Open("hex.mzf");
        var window=new HexViewWindow(tape.Rows[0]);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();var table=Assert.IsType<TableView>(window.Content);Assert.Equal((bytes.Length+15)/16,table.ItemCount);
            var last=table.ItemsSource!.Cast<object>().Last();Assert.EndsWith("FA FB FC",(string)last.GetType().GetProperty("Hex")!.GetValue(last)!);
            Assert.Equal(bytes,File.ReadAllBytes(tape.Path!));Assert.False(tape.IsModified);Capture(window,"wpf-hex-browser.png");
        }finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task QuickDiskInspectorUsesTheSameSnapshotAndRegionSelection()
    {
        using var files=new MediaDocumentTests();var model=new ShellViewModel(files.Coordinator,_=>Task.CompletedTask,files.Dialogs);model.NewQuickDiskCommand.Execute(null);var tape=model.ActiveTape!;
        var inspector=new QuickDiskVisualWindow(model);inspector.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();Assert.Equal("Visual QuickDisk Inspector — read-only",inspector.Title);
            var tabs=inspector.GetVisualDescendants().OfType<TabControl>().Single();Assert.Equal(new[]{"Disk Surface","Regions","Files","Findings","Statistics"},tabs.Items.OfType<TabItem>().Select(t=>t.Header));
            Assert.Same(tape,inspector.GetVisualDescendants().OfType<MZTools.Desktop.Controls.QuickDiskForensicView>().Single().Session);Capture(inspector,"wpf-quickdisk-inspector.png");
        }finally{inspector.Close();Dispatcher.UIThread.RunJobs();}
        await Task.CompletedTask;
    }
    [AvaloniaTheory][InlineData(900,480)][InlineData(1320,600)]
    public async Task DiskMenusRetainEveryCoreToolAndTablesFitTheWpfViewport(int width,int height)
    {
        using var files=new DiskDocumentTests();var session=await files.Open(DskDocumentFactory.CreateFsmz(false));var window=new MainWindow(files.Shell){Width=width,Height=height};window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var editor=window.GetVisualDescendants().OfType<DiskWorkspace>().Single();
            IEnumerable<MenuItem> Walk(IEnumerable<MenuItem> items)=>items.SelectMany(item=>new[]{item}.Concat(Walk(item.Items.OfType<MenuItem>())));
            var tags=editor.GetVisualDescendants().OfType<Button>().Where(b=>b.ContextMenu is not null).SelectMany(b=>Walk(b.ContextMenu!.Items.OfType<MenuItem>())).Select(m=>m.Tag).OfType<string>().ToHashSet();
            Assert.All(files.Shell.DiskTools,tool=>Assert.Contains(tool,tags));
            foreach(string tableName in new[]{"DiskFiles","DiskSectors"})
            {
                session.ViewIndex=tableName=="DiskFiles"?0:1;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
                var table=editor.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name==tableName);Assert.True(table.Bounds.Height>0);
                var origin=table.TranslatePoint(default,window)!.Value;Assert.True(origin.Y+table.Bounds.Height<=window.Bounds.Height+1,$"{tableName} is outside {width}x{height}");
            }
            Capture(window,$"wpf-disk-{width}x{height}.png");
        }finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task IndependentAudioAnalysisLeavesBrowserDocumentOpen()
    {
        using var source=new AudioDocumentTests();var session=await source.Open();
        var window=new AnalysisWindow();window.Show();
        try
        {
            Assert.Equal("Visual Media Analysis — WAV / FLAC",window.Title);Assert.Empty(window.Model.Documents.Documents);
            Assert.True(await window.OpenPathAsync(session.Path!),window.Model.Documents.Error);Dispatcher.UIThread.RunJobs();
            Assert.NotSame(session,window.Model.ActiveAudio);Assert.Same(session,source.Coordinator.Active);Assert.False(source.Coordinator.IsBusy);
            Capture(window,"wpf-audio-independent.png");
        }finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    private static void Capture(Window window,string name)
    {
        string root=Environment.GetEnvironmentVariable("MZTOOLS_WPF_PARITY_OUTPUT")??Path.Combine(Path.GetTempPath(),"mztools-wpf-parity");Directory.CreateDirectory(root);
        window.UpdateLayout();using var image=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));image.Render(window);image.Save(Path.Combine(root,name),PngBitmapEncoderOptions.Default);
    }
    [AvaloniaFact]public async Task WpfToolbarColumnsAndInlineNameUseOriginalBytesAndSafeSaveState()
    {
        using var files=new MediaDocumentTests();files.Dialogs.CloseDecision=CloseDecision.Discard;
        var tape=await files.Open();byte[] original=File.ReadAllBytes(tape.Path!);var model=new ShellViewModel(files.Coordinator,_=>Task.CompletedTask,files.Dialogs);var window=new MainWindow(model);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();var toolbar=window.FindControl<WrapPanel>("CommandBar")!;
            Assert.Equal(new[]{"Tools ▾","Open...","Add...","Export...","View","Delete","Compression...","Save","Save As...","Close","↑","↓"},toolbar.Children.OfType<Button>().Where(b=>b.IsVisible).Select(b=>b.Content));
            Assert.All(toolbar.Children.OfType<Button>().Where(b=>b.IsVisible),b=>Assert.Equal(28,b.Height));
            var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="RecordsTable");
            Assert.Equal(new[]{"Type","File name","Size","Size(Hex)","Trailing","Start","Exec","Compression","Description","Loader","Speed"},table.Columns.Select(c=>c.Header));
            var input=table.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="File name");
            Assert.True(input.Focus());input.SelectAll();window.KeyTextInput("RENAMED");window.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);window.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);Dispatcher.UIThread.RunJobs();
            Assert.Equal("RENAMED",tape.Rows[0].Name);Assert.False(tape.HasDraft);Assert.True(tape.IsModified);Assert.True(model.SaveCommand.CanExecute(null));
            Assert.Null(model.Documents.Error);
            Assert.Equal(original,File.ReadAllBytes(tape.Path!));Assert.Equal(original[24..],tape.Model.Records[0].GetSerializedHeader()[24..].Concat(tape.Model.Records[0].Body.MzfBody));
            Assert.False(window.FindControl<ListBox>("Navigation")!.IsEffectivelyVisible);Assert.False(window.GetVisualDescendants().OfType<Border>().Single(t=>t.Name=="RecordInspectorPanel").IsEffectivelyVisible);
            Capture(window,"wpf-tape-inline.png");
        }finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task ActualLoaderAndSpeedCellsApplyToTheSelectedRecordsUsingCoreComposition()
    {
        using var files=new MediaDocumentTests();files.Dialogs.CloseDecision=CloseDecision.Discard;
        string path=files.FilePath("multi.mzt");File.WriteAllBytes(path,MediaDocumentTests.Mzf("A").Concat(MediaDocumentTests.Mzf("B")).ToArray());var tape=await files.Open("multi.mzt");
        var model=new ShellViewModel(files.Coordinator,_=>Task.CompletedTask,files.Dialogs);var window=new MainWindow(model);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="RecordsTable");table.SelectedItems!.Clear();table.SelectedItems.Add(tape.Rows[0]);table.SelectedItems.Add(tape.Rows[1]);Dispatcher.UIThread.RunJobs();
            var loader=table.GetVisualDescendants().OfType<ComboBox>().First(b=>AutomationProperties.GetName(b)=="Tape loader");loader.SelectedItem="IC";Dispatcher.UIThread.RunJobs();
            Assert.All(tape.Model.Records,r=>Assert.Equal(TapeProfileComponents.Combine("IC","1:1"),r.Profile));
            var speed=table.GetVisualDescendants().OfType<ComboBox>().First(b=>AutomationProperties.GetName(b)=="Tape speed");speed.SelectedItem="1:3";Dispatcher.UIThread.RunJobs();
            Assert.All(tape.Model.Records,r=>Assert.Equal(TapeProfileComponents.Combine("IC","1:3"),r.Profile));
            Assert.False(tape.HasDraft);Capture(window,"wpf-loader-speed.png");
        }finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [Theory][InlineData(false)][InlineData(true)]public async Task CopyFromDiskMatchesCoreAndNeverRewritesEitherInput(bool accept)
    {
        using var h=new DiskDocumentTests();var source=DskDocumentFactory.CreateFsmz(false);source.FileSystem.Insert("SOURCE",[4,5,6],1,0x4000,0x4000);source.MarkModified();
        var target=await h.Open(DskDocumentFactory.CreateCpm(false));string path=h.FilePath("source.dsk");File.WriteAllBytes(path,source.Serialize());byte[] sourceBefore=File.ReadAllBytes(path),targetBefore=File.ReadAllBytes(target.Path!);
        var expected=target.Model.Clone();if(accept)CrossDiskTransferService.Preview(source,expected,source.FileSystem.ReadDirectory().Select(e=>new DiskCopyRequest(e.Key)).ToArray(),DiskCollisionPolicy.Cancel).Operation.Apply(expected);
        h.Dialogs.Inputs=[path];h.Dialogs.Accept=accept;Assert.Equal(accept,await h.Shell.CopyFromDiskAsync());Assert.Null(h.Coordinator.Error);
        Assert.Equal(expected.Serialize(),target.Model.Serialize());Assert.Equal(sourceBefore,File.ReadAllBytes(path));Assert.Equal(targetBefore,File.ReadAllBytes(target.Path!));Assert.Equal(accept,target.IsModified);
    }
    [Fact]public async Task ChangedCopySourceIsRejectedBeforeTargetMutation()
    {
        using var h=new DiskDocumentTests();var source=DskDocumentFactory.CreateFsmz(false);source.FileSystem.Insert("SOURCE",[1,2,3]);source.MarkModified();var target=await h.Open(DskDocumentFactory.CreateCpm(false));
        string path=h.FilePath("source.dsk");File.WriteAllBytes(path,source.Serialize());byte[] before=target.Model.Serialize();h.Dialogs.Inputs=[path];h.Dialogs.Answer=(_,fields)=>{File.AppendAllText(path,"changed");return new(fields.ToDictionary(f=>f.Key,f=>f.Value));};
        Assert.False(await h.Shell.CopyFromDiskAsync());Assert.Contains("changed",h.Coordinator.Error);Assert.Equal(before,target.Model.Serialize());Assert.False(target.IsModified);
    }
}
