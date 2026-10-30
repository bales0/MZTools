using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class BatchDocumentTests
{
    private static async Task Execute(AsyncCommand command,DocumentCoordinator coordinator)
    {Assert.True(command.CanExecute(null));command.Execute(null);for(int i=0;i<3000&&coordinator.IsBusy;i++)await Task.Delay(10,TestContext.Current.CancellationToken);Assert.False(coordinator.IsBusy);}
    private static void Setup(AudioDocumentTests files,string operation="Analyze")
    {
        string input=files.FilePath("source.mzf");File.WriteAllBytes(input,MediaDocumentTests.Mzf("BATCH",32));files.Dialogs.Inputs=[input];files.Dialogs.Answer=(_,fields)=>new(fields.ToDictionary(f=>f.Key,f=>f.Key switch{"operation"=>operation,"output"=>files.FilePath("output"),"target"=>"MZT",_=>f.Value}));
    }
    [Fact]public async Task PreflightIsReadOnlyAndAnalyzeUsesExistingCoreReport()
    {
        using var f=new AudioDocumentTests();Setup(f);var before=File.ReadAllBytes(f.Dialogs.Inputs[0]);await Execute(f.Shell.BatchPreviewCommand,f.Coordinator);Assert.Null(f.Coordinator.Error);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);Assert.True(f.Shell.ShowBatch);Assert.False(f.Shell.ShowReadiness);Assert.False(Directory.Exists(f.FilePath("output")));Assert.Equal(BatchProcessService.Report(s.Preview,"txt"),s.Report);await Execute(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.Null(f.Coordinator.Error);Assert.Equal("Analyzed",s.Rows[0].Result);Assert.Equal(before,File.ReadAllBytes(f.Dialogs.Inputs[0]));Assert.False(s.CanSaveAs);
    }
    [Fact]public async Task ConversionRequiresExecuteAndMatchesOriginalCoreOutputBytes()
    {
        using var f=new AudioDocumentTests();Setup(f,"ConvertFormat");await Execute(f.Shell.BatchPreviewCommand,f.Coordinator);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);Assert.False(File.Exists(s.Rows[0].Output));var expected=s.Preview.Rows[0].ImageBytes!.ToArray();await Execute(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.Null(f.Coordinator.Error);Assert.Equal("Completed",s.Rows[0].Result);Assert.Equal(expected,File.ReadAllBytes(s.Rows[0].Output));Assert.Single(TapeDocumentIO.Read(s.Rows[0].Output).Records);
    }
    [Fact]public async Task DeclinedExecutionAndChangedSourceKeepOutputAbsent()
    {
        using var f=new AudioDocumentTests();Setup(f,"ConvertFormat");await Execute(f.Shell.BatchPreviewCommand,f.Coordinator);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);f.Dialogs.Accept=false;await Execute(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.False(File.Exists(s.Rows[0].Output));f.Dialogs.Accept=true;File.AppendAllText(f.Dialogs.Inputs[0],"changed");await Execute(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.Contains("changed",f.Coordinator.Error);Assert.False(File.Exists(s.Rows[0].Output));
    }
    [Fact]public async Task BatchReportExportCannotReplaceAnInput()
    {
        using var f=new AudioDocumentTests();Setup(f);await Execute(f.Shell.BatchPreviewCommand,f.Coordinator);var before=File.ReadAllBytes(f.Dialogs.Inputs[0]);f.Dialogs.Output=f.Dialogs.Inputs[0];await Execute(f.Shell.BatchReportCommand,f.Coordinator);Assert.NotNull(f.Coordinator.Error);Assert.Equal(before,File.ReadAllBytes(f.Dialogs.Inputs[0]));
    }
    [AvaloniaFact]public async Task ActualBatchTableRetainsSelectedDetailsAndUsesSharedReportView()
    {
        using var f=new AudioDocumentTests();Setup(f);var window=new MainWindow(f.Shell);window.Show();try{await Execute(f.Shell.BatchPreviewCommand,f.Coordinator);Dispatcher.UIThread.RunJobs();window.UpdateLayout();var session=Assert.IsType<BatchSession>(f.Coordinator.Active);var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="BatchRows");Assert.True(table.IsEffectivelyVisible);Assert.Single(session.Rows);table.SelectedItem=session.Rows[0];Dispatcher.UIThread.RunJobs();Assert.NotNull(session.Selected);Assert.Contains("source.mzf",session.Detail);}finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
}
