using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class ShellPresentationTests
{
    private static void Pump(Window window){Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    [AvaloniaFact]public async Task OpenPickerWaitIsNotWorkingAndRetainsTheOperationLock()
    {
        using var files=new DiskDocumentTests();var session=await files.Open();var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Pump(window);var chosen=new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs=new DesktopDialogs(window){InputPicker=options=>chosen.Task};
            var work=files.Coordinator.ExecuteAsync(async ct=>{var paths=await dialogs.OpenInputPickerAsync(FilePickerPolicy.Options(FilePickerPurpose.OpenMedia,"Open media"));return paths.Count>0;});
            try
            {
                Pump(window);Assert.True(files.Coordinator.IsBusy);Assert.True(files.Coordinator.IsAwaitingInput);Assert.False(files.Coordinator.IsWorking);
                Assert.DoesNotContain("Working",files.Shell.WpfStatus);Assert.False(files.Shell.SaveAsCommand.CanExecute(null));
                Assert.False(window.GetVisualDescendants().OfType<ProgressBar>().Single().IsEffectivelyVisible);
                Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="CancelOperation").IsEffectivelyVisible);
            }
            finally{chosen.TrySetResult([]);}
            Assert.False(await work);Assert.False(files.Coordinator.IsBusy);Assert.False(files.Coordinator.IsAwaitingInput);Assert.Same(session,files.Coordinator.Active);
            Assert.True(await files.Coordinator.ExecuteAsync(ct=>{Assert.True(files.Coordinator.IsWorking);return Task.FromResult(true);}));
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaTheory][InlineData(980)][InlineData(1500)]
    public async Task MixedDocumentTabsStayAboveEveryToolbarAndSelectedNamesStayReadable(int width)
    {
        using var files=new DiskDocumentTests();var disk=await files.Open(name:"AFTER-2.dsk");var second=await files.Open(name:"SECOND.dsk");var qd=TapeSession.New(true);files.Coordinator.Add(qd);
        var window=new MainWindow(files.Shell){Width=width,Height=700};window.Show();
        try
        {
            Pump(window);var tabs=window.GetVisualDescendants().OfType<ListBox>().Single(c=>c.Name=="DocumentTabs");double y=tabs.TranslatePoint(default,window)!.Value.Y;
            foreach(var active in new DocumentSession[]{disk,qd,second,disk,qd})
            {
                files.Coordinator.Active=active;Pump(window);Assert.Same(active,tabs.SelectedItem);Assert.Equal(y,tabs.TranslatePoint(default,window)!.Value.Y,3);
                foreach(var doc in files.Coordinator.Documents)
                {
                    var label=tabs.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Text==doc.Title);Assert.True(label.Bounds.Width>0);
                    Assert.Equal(doc==active?Colors.White:Colors.Black,Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
                }
                var bar=window.GetVisualDescendants().OfType<Button>().First(b=>Equals(b.Content,active is DiskSession?"Disk ▾":"Tools ▾")&&b.IsEffectivelyVisible);
                Assert.True(tabs.TranslatePoint(default,window)!.Value.Y+tabs.Bounds.Height<=bar.TranslatePoint(default,window)!.Value.Y);
            }
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaFact]public void CreationLivesOnTheToolbarAndRecordPropertiesHaveAnUnambiguousName()
    {
        using var files=new DiskDocumentTests();var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Pump(window);var tools=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ToolsButton").ContextMenu!;
            Assert.DoesNotContain(tools.Items.OfType<MenuItem>(),i=>i.Header?.ToString()?.StartsWith("New",StringComparison.Ordinal)==true);
            Assert.DoesNotContain(tools.Items.OfType<MenuItem>(),i=>i.Header?.ToString()?.Contains("properties",StringComparison.OrdinalIgnoreCase)==true);
            var create=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NewDocumentButton");Assert.True(create.IsEffectivelyVisible);
            Assert.Equal(new[]{files.Shell.NewTapeCommand,files.Shell.NewQuickDiskCommand,files.Shell.NewDiskCommand},create.ContextMenu!.Items.OfType<MenuItem>().Select(i=>i.Command));
            var tape=TapeSession.New(true);string path=files.FilePath("properties.mzf");File.WriteAllBytes(path,MediaDocumentTests.Mzf("PROPERTY",16));tape.ApplyRecords(TapeDocumentIO.Read(path).Records);files.Coordinator.Add(tape);tape.SelectedRow=tape.Rows[0];Pump(window);Assert.True(create.IsEffectivelyVisible);
            window.GetVisualDescendants().OfType<RecordWorkspace>().Single().ShowInspector(window);Pump(window);var properties=Assert.Single(window.OwnedWindows);Assert.Equal("Properties",properties.Title);properties.Close();
        }
        finally{window.Close();Pump(window);}
    }
}
