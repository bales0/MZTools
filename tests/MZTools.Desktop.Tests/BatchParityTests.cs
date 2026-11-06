using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Tests;

public sealed class BatchParityTests
{
    [AvaloniaTheory]
    [InlineData("BIN")][InlineData("MZF")][InlineData("M12")][InlineData("MZT")]
    [InlineData("WAV")][InlineData("FLAC")][InlineData("LEP")][InlineData("L16")]
    public async Task ActualPreflightFormPassesSelectedFormatToExecution(string format)
    {
        using var f=new AudioDocumentTests();f.Dialogs.Inputs=[Tape(f)];var owner=new Window{Width=900,Height=700};owner.Show();var ui=new DesktopDialogs(owner);
        try
        {
            var pending=ui.DiskFormAsync("Batch process · preflight",BatchProcessForm.Fields());Dispatcher.UIThread.RunJobs();var dialog=Assert.Single(owner.OwnedWindows);
            T Find<T>(string key) where T:Control=>dialog.GetVisualDescendants().OfType<T>().Single(c=>AutomationProperties.GetName(c)==BatchProcessForm.Fields().Single(p=>p.Key==key).Label);
            Find<ComboBox>("operation").SelectedItem="ExtractAll";Find<ComboBox>("extract").SelectedItem=format;Find<ComboBox>("rate").SelectedItem="22050";Find<TextBox>("output").Text=f.FilePath("out");Dispatcher.UIThread.RunJobs();
            Assert.Equal(format is "WAV" or "FLAC",Find<ComboBox>("rate").IsEffectivelyVisible);
            if(Environment.GetEnvironmentVariable("MZTOOLS_BATCH_UI_OUTPUT") is{} output)
            {
                Directory.CreateDirectory(output);dialog.UpdateLayout();
                using var bitmap=new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width,(int)dialog.Bounds.Height),new Vector(96,96));bitmap.Render(dialog);bitmap.Save(Path.Combine(output,"batch-extract-"+format+".png"),PngBitmapEncoderOptions.Default);
            }
            dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Continue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var answer=Assert.IsType<DiskAnswer>(await pending);f.Dialogs.Answer=(_,_)=>answer;await Run(f.Shell.BatchPreviewCommand,f.Coordinator);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);Assert.Equal(format,s.Preview.Options.ExtractFormat);await Run(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.Equal("Completed",s.Rows.Single().Result);
            pending=ui.DiskFormAsync("Batch process · preflight",BatchProcessForm.Fields());Dispatcher.UIThread.RunJobs();dialog=Assert.Single(owner.OwnedWindows);Find<ComboBox>("operation").Focus();dialog.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null);Dispatcher.UIThread.RunJobs();Assert.Null(await pending);Assert.Same(s,f.Coordinator.Active);
        }
        finally{foreach(var dialog in owner.OwnedWindows.ToArray())dialog.Close();owner.Close();Dispatcher.UIThread.RunJobs();}
    }
    private static async Task Run(AsyncCommand command,DocumentCoordinator coordinator)
    {
        Assert.True(command.CanExecute(null));command.Execute(null);
        for(int i=0;i<6000&&coordinator.IsBusy;i++)await Task.Delay(10,TestContext.Current.CancellationToken);
        Assert.False(coordinator.IsBusy);Assert.Null(coordinator.Error);
    }
    private static void Configure(AudioDocumentTests f,string operation,params (string Key,string Value)[] overrides)
    {
        var answers=overrides.ToDictionary(p=>p.Key,p=>p.Value);answers["operation"]=operation;answers.TryAdd("output",f.FilePath("out"));
        f.Dialogs.Answer=(_,fields)=>new(fields.ToDictionary(p=>p.Key,p=>answers.GetValueOrDefault(p.Key,p.Value)));
    }
    private static string Tape(AudioDocumentTests f,string name="source.mzf")
    {
        var bytes=MediaDocumentTests.Mzf("BATCH",32);Array.Clear(bytes,24,104);string path=f.FilePath(name);File.WriteAllBytes(path,bytes);return path;
    }
    [Theory]
    [InlineData("Native")][InlineData("BIN")][InlineData("MZF")][InlineData("M12")][InlineData("MZT")]
    [InlineData("WAV")][InlineData("FLAC")][InlineData("LEP")][InlineData("L16")]
    public async Task EveryOfferedExtractionFormatRunsThroughShellAndReopens(string format)
    {
        using var f=new AudioDocumentTests();string source=Tape(f);byte[] before=File.ReadAllBytes(source);f.Dialogs.Inputs=[source];
        Configure(f,"ExtractAll",("extract",format),("rate","22050"));
        await Run(f.Shell.BatchPreviewCommand,f.Coordinator);var session=Assert.IsType<BatchSession>(f.Coordinator.Active);
        Assert.Equal(format,session.Preview.Options.ExtractFormat);Assert.Equal(BatchMediaPolicy.Extensions(BatchOperation.ExtractAll,"HFEv3"),f.Dialogs.BatchFilter);
        Assert.False(Directory.Exists(f.FilePath("out")));Assert.Equal("Will modify",session.Rows.Single().Result);
        await Run(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.Equal("Completed",session.Rows.Single().Result);
        string extension=format=="Native"?".mzf":"."+format.ToLowerInvariant();string result=Assert.Single(Directory.GetFiles(session.Rows[0].Output,"*"+extension));
        if(format=="BIN")Assert.Equal(before[128..],File.ReadAllBytes(result));
        else if(format is "Native" or "MZF" or "M12")Assert.Equal(before,File.ReadAllBytes(result));
        else if(format=="MZT")Assert.Equal(before[128..],Assert.Single(TapeDocumentIO.Read(result).Records).Body.MzfBody);
        else
        {
            var records=SharpTapeImporter.ReadFile(result,cancellationToken:TestContext.Current.CancellationToken);Assert.Equal(before[128..],Assert.Single(records).Body.MzfBody);
            if(format is "WAV" or "FLAC"){using var reader=AudioSignalAnalysisService.OpenReader(result,cancellation:TestContext.Current.CancellationToken);Assert.Equal(22050u,reader.Format.SampleRate);}
        }
        Assert.Equal(before,File.ReadAllBytes(source));Assert.Equal(new[]{"Execute prepared batch"},f.Dialogs.Confirmations);
    }
    [AvaloniaFact]
    public void FormTracksPolicyAudioAndCustomFolderRestriction()
    {
        var fields=BatchProcessForm.Fields();var values=fields.ToDictionary(f=>f.Key,f=>f.Choices is{} choices?(Control)new ComboBox{ItemsSource=choices,SelectedItem=f.Value}:new TextBox{Text=f.Value});
        var rows=fields.ToDictionary(f=>f.Key,_=>(Control)new StackPanel());BatchProcessForm.Attach(values,rows);
        void Select(string key,string value)=>((ComboBox)values[key]).SelectedItem=value;
        var filter=(TextBox)values["extensions"];foreach(string extension in new[]{".m12",".mz0",".mz7"})Assert.Contains(extension,filter.Text);
        Select("input","Scan folder");Assert.True(rows["extensions"].IsVisible);Assert.True(rows["extensions"].IsEnabled);
        Select("operation","ConvertFormat");Select("target","FLAC");Assert.True(rows["rate"].IsVisible);Assert.True(rows["rate"].IsEnabled);
        Assert.Equal(BatchMediaPolicy.Extensions(BatchOperation.ConvertFormat,"FLAC"),filter.Text);
        filter.Text=".m12,.mz7";Select("target","MZT");Assert.Equal(".m12,.mz7",filter.Text);Assert.False(rows["rate"].IsVisible);Assert.False(rows["rate"].IsEnabled);
        Select("operation","ExtractAll");foreach(string format in BatchProcessForm.ExtractionFormats){Select("extract",format);Assert.Equal(format is "WAV" or "FLAC",rows["rate"].IsVisible);}
        Assert.Equal(new[]{"22050","44100"},((ComboBox)values["rate"]).Items.Cast<string>());
        foreach(var operation in Enum.GetValues<BatchOperation>()){Select("operation",operation.ToString());Assert.Equal(BatchMediaPolicy.Supports(operation,"input.wav","MZT"),rows["heuristic"].IsVisible);}
        Dispatcher.UIThread.RunJobs();
    }
    [Fact]
    public async Task HiddenOptionsDoNotLeakIntoAnalyzeAndFolderScanIncludesLegacyExtensions()
    {
        using var f=new AudioDocumentTests();foreach(string extension in new[]{"m12","mz0","mz7"})Tape(f,"source."+extension);
        Configure(f,"Analyze",("input","Scan folder"),("folder",f.Root),("recursive","Yes"),("rate","22050"),("replace","Yes"),("extract","FLAC"),("auxiliary","stale.dsk"),("heuristic","Yes"));
        await Run(f.Shell.BatchPreviewCommand,f.Coordinator);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);Assert.Equal(3,s.Rows.Count);
        Assert.False(s.Preview.Options.ReplaceOriginals);Assert.Null(s.Preview.Options.AuxiliaryPath);Assert.Equal("Native",s.Preview.Options.ExtractFormat);Assert.Equal(44100,s.Preview.Options.WavSampleRate);Assert.False(s.Preview.Options.UseHeuristicAnalysis);
        await Run(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.All(s.Rows,r=>Assert.Equal("Analyzed",r.Result));Assert.False(Directory.Exists(f.FilePath("out")));
    }
    [Fact]
    public async Task DataLossNeedsItsOwnAnswerAndDecliningWritesNothing()
    {
        using var f=new AudioDocumentTests();string source=f.FilePath("raw.dsk");File.WriteAllBytes(source,DskDocumentFactory.CreateRaw(1,1,8,512,1,0x4E,0xE5,"test").Serialize());byte[] before=File.ReadAllBytes(source);
        string lossless=f.FilePath("existing.hfe");File.WriteAllBytes(lossless,HfeImage.FromDsk(DskDocumentFactory.CreateFsmz(tracks:4)).Serialize(false));byte[] hfeBefore=File.ReadAllBytes(lossless);
        f.Dialogs.Inputs=[source,lossless];Configure(f,"ConvertFormat",("target","HFEv3"));await Run(f.Shell.BatchPreviewCommand,f.Coordinator);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);Assert.True(s.Preview.Rows[0].RequiresLossConfirmation);Assert.False(s.Preview.Rows[1].RequiresLossConfirmation);
        f.Dialogs.Confirm=title=>title!="Accept batch data loss";await Run(f.Shell.BatchExecuteCommand,f.Coordinator);
        Assert.Equal(new[]{"Execute prepared batch","Accept batch data loss"},f.Dialogs.Confirmations);Assert.All(s.Rows,r=>Assert.False(File.Exists(r.Output)));Assert.Equal(before,File.ReadAllBytes(source));
        f.Dialogs.Confirm=_=>true;await Run(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.All(s.Rows,r=>Assert.Equal("Completed",r.Result));Assert.True(HfeImage.Parse(File.ReadAllBytes(s.Rows[0].Output)).IsV3);Assert.Equal(before,File.ReadAllBytes(source));Assert.Equal(hfeBefore,File.ReadAllBytes(lossless));
    }
    [Fact]
    public async Task ReplacementNeedsSeparateConsentAndKeepsOriginalBackup()
    {
        using var f=new AudioDocumentTests();var disk=DskDocumentFactory.CreateFsmz();disk.FileSystem.Insert("FIRST",new byte[512]);disk.FileSystem.Insert("SECOND",[1,2,3]);disk.FileSystem.Delete(disk.FileSystem.ReadDirectory().Single(r=>r.Name=="FIRST"));disk.MarkModified();
        string source=f.FilePath("fragmented.dsk");File.WriteAllBytes(source,disk.Serialize());byte[] before=File.ReadAllBytes(source);f.Dialogs.Inputs=[source];Configure(f,"Defragment",("replace","Yes"));await Run(f.Shell.BatchPreviewCommand,f.Coordinator);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);
        f.Dialogs.Confirm=title=>title!="Replace original batch inputs";await Run(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.Equal(before,File.ReadAllBytes(source));Assert.False(File.Exists(source+".bak"));Assert.Contains("Replace original batch inputs",f.Dialogs.Confirmations);
        f.Dialogs.Confirm=_=>true;await Run(f.Shell.BatchExecuteCommand,f.Coordinator);Assert.Equal("Completed",s.Rows[0].Result);Assert.Equal(before,File.ReadAllBytes(source+".bak"));
    }
    [AvaloniaFact]
    public async Task CancellationRefreshesRowsAndKeepsOnlyCompletedOutputPairs()
    {
        using var f=new AudioDocumentTests();f.Dialogs.Inputs=[Tape(f,"one.mzf"),Tape(f,"two.mzf")];Configure(f,"ConvertFormat",("target","M12"));await Run(f.Shell.BatchPreviewCommand,f.Coordinator);var s=Assert.IsType<BatchSession>(f.Coordinator.Active);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();var verify=s.Preview.Rows[1].VerifyOutput!;
        s.Preview.Rows[1].VerifyOutput=(bytes,ct)=>{verify(bytes,ct);entered.TrySetResult();release.Wait(ct);};
        f.Shell.BatchExecuteCommand.Execute(null);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(30),TestContext.Current.CancellationToken);Dispatcher.UIThread.RunJobs();Assert.True(f.Coordinator.IsBusy);Assert.True(f.Coordinator.HasProgress);Assert.Contains("two.mzf",f.Coordinator.Status);f.Coordinator.Cancel();}
        finally{release.Set();}
        for(int i=0;i<3000&&f.Coordinator.IsBusy;i++)await Task.Delay(10,TestContext.Current.CancellationToken);
        Assert.False(f.Coordinator.IsBusy);Assert.Null(f.Coordinator.Error);Assert.True(s.Preview.Cancelled);Assert.False(s.CanExecute);Assert.Equal(new[]{"Completed","Cancelled"},s.Rows.Select(r=>r.Result));
        Assert.True(File.Exists(s.Rows[0].Output));Assert.True(File.Exists(SidecarService.GetSidecarPath(s.Rows[0].Output)));Assert.False(File.Exists(s.Rows[1].Output));Assert.False(File.Exists(SidecarService.GetSidecarPath(s.Rows[1].Output)));Assert.Empty(Directory.GetFiles(f.FilePath("out"),"*.tmp"));Assert.Contains("Cancelled",f.Coordinator.Status);
    }
}
