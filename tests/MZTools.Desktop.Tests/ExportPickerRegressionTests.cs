using System.Collections.Specialized;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class ExportPickerRegressionTests
{
    private static void Pump(Window window){Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    private static async Task<TapeSession> Multi(DiskDocumentTests files)
    {
        string path=files.FilePath("multi.mzt");File.WriteAllBytes(path,Enumerable.Range(0,32).SelectMany(i=>MediaDocumentTests.Mzf("R"+i,8)).ToArray());
        Assert.True(await files.Coordinator.OpenPathAsync(path));return Assert.IsType<TapeSession>(files.Coordinator.Active);
    }
    private static void Continue(Window owner,string format)
    {
        var options=owner.OwnedWindows.Single(w=>w.Title=="Export options");
        var box=options.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="Output format");box.SelectedItem=box.Items.OfType<FormatChoice>().Single(f=>f.Key==format);
        options.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Continue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    [AvaloniaTheory][InlineData("Wav",0)][InlineData("Lep",0)][InlineData("L16",0)][InlineData("Mzf",0)][InlineData("Mzt",0)][InlineData("Wav",1)][InlineData("Lep",2)]
    public async Task NativeSaveReturnDoesNotRebindOrLoseMultiSelection(string format,int failure)
    {
        using var files=new DiskDocumentTests();var tape=await Multi(files);var window=new MainWindow(files.Shell){Width=1600,Height=850};window.Show();
        try
        {
            Pump(window);var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="RecordsTable");
            foreach(var row in tape.Rows)if(!table.SelectedItems!.Contains(row))table.SelectedItems.Add(row);Pump(window);
            var before=tape.Selection.ToArray();Assert.Equal(32,before.Length);long revision=tape.Revision;var source=File.ReadAllBytes(tape.Path!);
            int mutations=0;NotifyCollectionChangedEventHandler handler=(_,_)=>mutations++;((INotifyCollectionChanged)table.SelectedItems!).CollectionChanged+=handler;
            var chosen=new TaskCompletionSource<IStorageFile?>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
            var dialogs=new DesktopDialogs(window){OutputPicker=_=>{calls++;return chosen.Task;}};
            var work=files.Coordinator.ExecuteAsync(async ct=>{var choice=await dialogs.ChooseExportAsync(tape);if(choice is not null)throw new InvalidOperationException("No encoder may be called without a destination.");return false;});
            Continue(window,format);Pump(window);Assert.Equal(1,calls);Assert.True(files.Coordinator.IsAwaitingInput);Assert.True(files.Coordinator.IsBusy);Assert.False(files.Coordinator.IsWorking);
            if(failure==1)chosen.SetException(new OperationCanceledException());else if(failure==2)chosen.SetException(new IOException("Injected picker failure"));else chosen.SetResult(null);
            Assert.False(await work);Pump(window);
            Assert.False(files.Coordinator.IsBusy);Assert.False(files.Coordinator.IsWorking);Assert.False(files.Coordinator.IsAwaitingInput);Assert.Equal(failure==2,files.Coordinator.HasError);
            Assert.Equal(before,tape.Selection);Assert.Equal(32,table.SelectedItems!.Count);Assert.Equal(0,mutations);Assert.Equal(revision,tape.Revision);Assert.Equal(source,File.ReadAllBytes(tape.Path!));Assert.True(files.Shell.ExportCommand.CanExecute(null));Assert.True(window.IsEnabled);
            ((INotifyCollectionChanged)table.SelectedItems!).CollectionChanged-=handler;
        }
        finally{foreach(var child in window.OwnedWindows.ToArray())child.Close();window.Close();Pump(window);}
    }
    [Theory][InlineData("Wav")][InlineData("Lep")][InlineData("L16")]
    public async Task CancellingPreparedStandardPreviewRemovesOnlyItsTemporaryFiles(string format)
    {
        using var files=new MediaDocumentTests();var tape=await files.Open();string source=tape.Path!;byte[] before=File.ReadAllBytes(source);long revision=tape.Revision;
        string destination=files.FilePath("output."+format.ToLowerInvariant());string[] temporary;
        using(var preview=await MediaOperations.ExportAsync(tape,new(destination,format,Gaps:TapeExportGapOptions.Standard),TestContext.Current.CancellationToken))
        {
            temporary=preview.SourceFiles.Values.ToArray();Assert.All(temporary,p=>Assert.True(File.Exists(p)));Assert.False(File.Exists(destination));Assert.Contains("Gaps (approximate LOW pauses)",preview.Report);
        }
        Assert.All(temporary,p=>Assert.False(File.Exists(p)));Assert.False(File.Exists(destination));Assert.Equal(before,File.ReadAllBytes(source));Assert.Equal(revision,tape.Revision);Assert.False(tape.IsModified);
    }
    [AvaloniaFact]public async Task OptionsCancelDoesNotInvokeSavePicker()
    {
        using var files=new DiskDocumentTests();var tape=await Multi(files);var owner=new MainWindow(files.Shell);owner.Show();int picker=0;
        try
        {
            var dialogs=new DesktopDialogs(owner){OutputPicker=_=>{picker++;return Task.FromResult<IStorageFile?>(null);}};
            var work=files.Coordinator.ExecuteAsync(async _=>await dialogs.ChooseExportAsync(tape) is not null);Assert.Single(owner.OwnedWindows).Close();Assert.False(await work);Assert.Equal(0,picker);Assert.False(files.Coordinator.IsBusy);Assert.False(files.Coordinator.IsAwaitingInput);
        }
        finally{owner.Close();Pump(owner);}
    }
    [AvaloniaFact]public void GapControlsAreDirectlyEditableAndValidateMilliseconds()
    {
        var controls=new ExportGapControls();Assert.Empty(controls.GetLogicalDescendants().OfType<ComboBox>());Assert.Equal(TapeExportGapOptions.Standard,controls.Read());Assert.True(controls.Start.IsEnabled&&controls.Between.IsEnabled&&controls.End.IsEnabled);
        controls.Start.Value=0;controls.Between.Value=0;controls.End.Value=0;Assert.Equal(TapeExportGapOptions.Legacy,controls.Read());controls.Start.Value=100;controls.Between.Value=200;controls.End.Value=300;Assert.Equal(new(100,200,300),controls.Read());controls.Start.Value=null;Assert.Throws<InvalidOperationException>(()=>controls.Read());
    }
    [AvaloniaFact]public async Task InteractiveWaveOptionsPassExplicitStandardAndHideGapsForOtherFormats()
    {
        using var files=new DiskDocumentTests();var tape=await Multi(files);var owner=new MainWindow(files.Shell);owner.Show();
        try
        {
            string destination=Path.Combine(files.Root,"standard.wav");File.WriteAllBytes(destination,Array.Empty<byte>());
            var storage=await owner.StorageProvider.TryGetFileFromPathAsync(new Uri(destination));Assert.NotNull(storage);
            var dialogs=new DesktopDialogs(owner){OutputPicker=_=>Task.FromResult<IStorageFile?>(storage)};
            var choice=dialogs.ChooseExportAsync(tape);Continue(owner,"Wav");Assert.Equal(TapeExportGapOptions.Standard,(await choice)!.Gaps);
            choice=dialogs.ChooseExportAsync(tape);var options=owner.OwnedWindows.Single(w=>w.Title=="Export options");Assert.False(options.GetVisualDescendants().OfType<ExportGapControls>().Single().IsVisible);options.Close();Assert.Null(await choice);
        }
        finally{owner.Close();Pump(owner);}
    }
}
