using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

internal sealed class AudioImportTestDialogs : IAudioImportDialogs
{
    internal AudioImportChoice? Choice=new();
    internal int Calls;internal bool ManifestOnly;internal IReadOnlyList<string> Paths=[];
    internal List<string> Reports=[];
    public Task<AudioImportChoice?> ChooseAudioImportAsync(IReadOnlyList<string> paths,bool manifestOnly)
    {Calls++;Paths=paths;ManifestOnly=manifestOnly;return Task.FromResult(Choice);}
    public Task ShowAudioImportReportAsync(string report){Reports.Add(report);return Task.CompletedTask;}
}

public sealed class Phase5WorkflowTests
{
    private static string Signal(MediaDocumentTests files,string extension,int count=1)
    {
        string input=files.FilePath("record.mzf");File.WriteAllBytes(input,MediaDocumentTests.Mzf("PROGRAM",16));
        Assert.Equal("C573DA754A538237FC6F8B73491537FF8ECE71D900542688921668639CB8BCA1",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input))));
        var record=TapeDocumentIO.Read(input).Records[0];var records=Enumerable.Range(0,count).Select(_=>record.DeepClone()).ToArray();
        string wave=files.FilePath("signal.wav");
        SharpTapeExporter.Export(wave,records,SharpTapeOutputFormat.Wav,cancellationToken:TestContext.Current.CancellationToken);
        string path=files.FilePath("signal"+extension);
        if(extension==".flac")
        {MZTools.Platform.Audio.NativeAudioCodecs.Configure();CorePlatformServices.EncodeFlac(wave,path,TestContext.Current.CancellationToken);}
        else if(extension is ".lep" or ".l16")SharpTapeExporter.Export(path,records,extension==".lep"?SharpTapeOutputFormat.Lep:SharpTapeOutputFormat.L16,cancellationToken:TestContext.Current.CancellationToken);
        else if(extension==".wav.json")
        {
            using var reader=new WavPcmStreamReader(wave);var f=reader.Format;
            File.WriteAllText(path,JsonSerializer.Serialize(new{schema="mztools-live-capture-v1",status="Complete",recording=new{savedFrames=f.FrameCount,conversion=new{output=new{channels=f.Channels,sampleRate=f.SampleRate,bitsPerSample=f.BitsPerSample}},segments=new[]{new{path=Path.GetFileName(wave),published=true,headerFinalized=true,pcmMatchesWrittenBytes=true,firstFrame=0,frames=f.FrameCount,dataBytes=f.DataLength,fileBytes=new FileInfo(wave).Length,fileSha256=AtomicFileTransaction.Hash(wave)}}}}));
        }
        if(Environment.GetEnvironmentVariable("MZTOOLS_PHASE5_EVIDENCE") is {} evidence)
        {
            Directory.CreateDirectory(evidence);
            var sources=new[]{input,path,wave}.Distinct().ToDictionary(p=>Path.GetFileName(p),p=>new{bytes=new FileInfo(p).Length,sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))});
            File.WriteAllText(Path.Combine(evidence,extension.Replace('.','_')+"-"+count+".json"),JsonSerializer.Serialize(new{format=extension,programs=count,expectedLoad=0x4000,expectedExec=0x4000,sources},new JsonSerializerOptions{WriteIndented=true}));
        }
        return path;
    }
    [Theory][InlineData(".wav",1)][InlineData(".wav",2)][InlineData(".flac",1)][InlineData(".lep",1)][InlineData(".l16",1)][InlineData(".wav.json",1)]
    public async Task MainOpenProducesProgramsAndSafeSaveAsWhileAnalysisStaysExplicit(string extension,int count)
    {
        using var files=new MediaDocumentTests();string input=Signal(files,extension,count);var before=File.ReadAllBytes(input);var ui=new AudioImportTestDialogs();
        if(extension==".wav.json")ui.Choice=new(ProgramImportMode.Heuristic);
        var coordinator=new DocumentCoordinator(new MediaDocumentLoader(audioDialogs:ui),files.Dialogs,new());
        Assert.True(await coordinator.OpenPathAsync(input),coordinator.Error);var tape=Assert.IsType<TapeSession>(coordinator.Active);
        Assert.Equal(count,tape.Rows.Count);Assert.Equal(count==1?"Mzf":"Mzt",tape.Format);Assert.False(tape.CanSave);Assert.True(tape.CanSaveAs);
        Assert.All(tape.Model.Records,r=>{Assert.Equal(MediaDocumentTests.Mzf("PROGRAM",16)[128..],r.Body.MzfBody);Assert.Equal(0x4000,r.Header.MzfStart);Assert.Equal(0x4000,r.Header.MzfExec);});
        Assert.Equal(extension is ".lep" or ".l16"?0:1,ui.Calls);Assert.Equal(extension==".wav.json",ui.ManifestOnly);
        files.Dialogs.Save=new(files.FilePath(count==1?"decoded.mzf":"decoded.mzt"),count==1?"Mzf":"Mzt",false);
        Assert.True(await coordinator.SaveAsync(true),coordinator.Error);Assert.Equal(before,File.ReadAllBytes(input));
        Assert.Equal(count,TapeDocumentIO.Read(files.Dialogs.Save.Path).Records.Count);
        if(extension is ".wav" or ".flac" or ".wav.json")
        {using var analysis=await new MediaDocumentLoader(audioOnly:true).LoadAsync(input,TestContext.Current.CancellationToken);Assert.IsType<AudioSession>(analysis);}
        foreach(var doc in coordinator.Documents)doc.Dispose();
    }
    [Theory][InlineData(ProgramImportMode.Standard)][InlineData(ProgramImportMode.Heuristic)]
    public async Task AudioAddUsesOneChoiceForMixedInputsAndPreservesPreviewAndSources(ProgramImportMode mode)
    {
        using var files=new MediaDocumentTests();var target=await files.Open();string audio=Signal(files,".flac"),other=files.FilePath("extra.mzf");File.WriteAllBytes(other,MediaDocumentTests.Mzf("EXTRA",16));
        var before=File.ReadAllBytes(audio);var ui=new AudioImportTestDialogs{Choice=new(mode,ProgramImportReport.Detailed)};
        var preview=await MediaOperations.ImportAsync(target,[other,audio],TestContext.Current.CancellationToken,ui);
        Assert.Equal(1,ui.Calls);Assert.Single(ui.Paths);Assert.Single(target.Rows);Assert.Equal(3,preview.Records.Count);
        Assert.Contains(mode.ToString(),Assert.Single(ui.Reports));Assert.Equal(before,File.ReadAllBytes(audio));
        AudioProgramImport.Verify(preview.Sources,TestContext.Current.CancellationToken);target.ApplyRecords(preview.Records);Assert.Equal(3,target.Rows.Count);
    }
    [Fact] public async Task MultiAudioChoiceAndLateFailureNeverPartiallyApply()
    {
        using var files=new MediaDocumentTests();var target=await files.Open();string first=Signal(files,".wav"),second=files.FilePath("second.wav");File.Copy(first,second);
        var ui=new AudioImportTestDialogs{Choice=new(ProgramImportMode.Standard,ProgramImportReport.None)};
        var preview=await MediaOperations.ImportAsync(target,[first,second],TestContext.Current.CancellationToken,ui);
        Assert.Equal(2,ui.Paths.Count);Assert.Equal(1,ui.Calls);Assert.Equal(3,preview.Records.Count);Assert.Empty(ui.Reports);Assert.Single(target.Rows);
        string bad=files.FilePath("bad.flac");File.WriteAllBytes(bad,[0,1,2]);long revision=target.Revision;
        await Assert.ThrowsAnyAsync<Exception>(()=>MediaOperations.ImportAsync(target,[first,bad],TestContext.Current.CancellationToken,ui));
        Assert.Single(target.Rows);Assert.Equal(revision,target.Revision);
    }
    [Fact] public async Task OptionsCancelAndNoValidProgramKeepExistingDocumentAndNoOutput()
    {
        using var files=new MediaDocumentTests();var previous=await files.Open();string input=Signal(files,".wav");var before=File.ReadAllBytes(input);var ui=new AudioImportTestDialogs{Choice=null};
        var coordinator=new DocumentCoordinator(new MediaDocumentLoader(audioDialogs:ui),files.Dialogs,new());coordinator.Add(previous);
        Assert.False(await coordinator.OpenPathAsync(input));Assert.Same(previous,coordinator.Active);Assert.Single(coordinator.Documents);Assert.Equal(before,File.ReadAllBytes(input));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>MediaOperations.ImportAsync(previous,[input],TestContext.Current.CancellationToken,ui));
        using var audio=new AudioDocumentTests();string noProgram=audio.Wave(frames:512);ui.Choice=new();
        Assert.False(await coordinator.OpenPathAsync(noProgram));Assert.Same(previous,coordinator.Active);Assert.Contains("complete Sharp",coordinator.Error);
    }
    [Fact] public async Task ManifestSegmentsRemainProtectedUntilSaveAsAndChangedSourceCannotApply()
    {
        using var files=new MediaDocumentTests();string manifest=Signal(files,".wav.json");
        var coordinator=new DocumentCoordinator(new MediaDocumentLoader(),files.Dialogs,new());Assert.True(await coordinator.OpenPathAsync(manifest),coordinator.Error);
        var tape=Assert.IsType<TapeSession>(coordinator.Active);var hashes=((IFileSetDocument)tape).SourceHashes;Assert.Equal(2,hashes.Count);
        string segment=files.FilePath("signal.wav");await Assert.ThrowsAsync<IOException>(()=>coordinator.WriteExportAsync(new Dictionary<string,byte[]>{[segment]=[1]},TestContext.Current.CancellationToken));
        var target=TapeSession.New(false);var preview=await MediaOperations.ImportAsync(target,[segment],TestContext.Current.CancellationToken);
        File.AppendAllText(segment,"changed");Assert.Throws<IOException>(()=>AudioProgramImport.Verify(preview.Sources,TestContext.Current.CancellationToken));Assert.Empty(target.Rows);
    }
    [Fact] public async Task ValidSingleIplOpensAsDiskLikeCurrentWpfAndDamagedBootOnlyDoesNotBecomeTape()
    {
        using var files=new DiskDocumentTests();string mzf=files.FilePath("source.mzf");File.WriteAllBytes(mzf,MediaDocumentTests.Mzf("IPL",16));
        var input=DiskOperations.ReadIplFiles([mzf],TestContext.Current.CancellationToken);string path=files.FilePath("ipl.dsk");File.WriteAllBytes(path,DiskOperations.BuildIpl(input,true).Serialize());var before=File.ReadAllBytes(path);
        var reference=DskDocument.Open(path);Assert.Equal(DskFileSystemType.SingleIpl,reference.FileSystem.Type);
        // MainWindow.AddFile only attempts a tape import for BootOnly, not SingleIpl.
        Assert.True(await files.Coordinator.OpenPathAsync(path),files.Coordinator.Error);var disk=Assert.IsType<DiskSession>(files.Coordinator.Active);
        Assert.Single(disk.Files);Assert.Equal(input[0].Record.Body.MzfBody,Mz800IplDskReader.ReadFile(path).Record.Body.MzfBody);Assert.Equal(before,File.ReadAllBytes(path));
        var damaged=(byte[])before.Clone();damaged[0x30]=1;string bad=files.FilePath("damaged.dsk");File.WriteAllBytes(bad,damaged);files.Dialogs.CancelForm=true;
        Assert.False(await files.Coordinator.OpenPathAsync(bad));Assert.Same(disk,files.Coordinator.Active);Assert.Equal(damaged,File.ReadAllBytes(bad));
    }
    [Fact] public void FolderScanIntersectsOperationAndTargetWithoutChangingExplicitInputPolicy()
    {
        using var files=new MediaDocumentTests();Directory.CreateDirectory(files.FilePath("nested"));
        foreach(string name in new[]{"one.dsk","noise.flac","nested/two.DSK","nested/ignore.flac"})File.WriteAllBytes(files.FilePath(name),[1]);
        var selected=BatchInputSelection.Scan(files.DirectoryPath,true,".dsk,.flac",BatchOperation.Defragment,"HFEv3",TestContext.Current.CancellationToken);
        Assert.Equal(2,selected.Length);Assert.All(selected,p=>Assert.Equal(".dsk",Path.GetExtension(p).ToLowerInvariant()));
        Assert.Single(BatchInputSelection.Scan(files.DirectoryPath,false,".dsk,.flac",BatchOperation.Defragment,"HFEv3",TestContext.Current.CancellationToken));
        Assert.Empty(BatchInputSelection.Scan(files.DirectoryPath,true,".flac",BatchOperation.Defragment,"HFEv3",TestContext.Current.CancellationToken));
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>BatchInputSelection.Scan(files.DirectoryPath,true,".dsk",BatchOperation.Defragment,"HFEv3",cancel.Token));
        Assert.False(BatchMediaPolicy.Supports(BatchOperation.Defragment,"noise.flac","HFEv3"));
    }
    [Fact] public void PickerPatternsHaveBoundedBudgetWhileValidationAcceptsMixedCase()
    {
        foreach(var purpose in new[]{FilePickerPurpose.OpenMedia,FilePickerPurpose.OpenAudio,FilePickerPurpose.ApplyDiskPatch})
        {
            var o=FilePickerPolicy.Options(purpose,"Instrumentation");int count=o.FileTypeFilter!.Sum(f=>f.Patterns!.Count);
            Assert.InRange(count,1,64);Assert.DoesNotContain(o.FileTypeFilter!.SelectMany(f=>f.Patterns!),p=>p is "*" or "*.*");
        }
        FilePickerPolicy.Validate(FilePickerPolicy.Options(FilePickerPurpose.ApplyDiskPatch,"Patch"),["test.MZPATCH.JSON"]);
        FilePickerPolicy.Validate(FilePickerPolicy.Options(FilePickerPurpose.OpenAudio,"Audio"),["test.WaV.JsOn"]);
    }
    [AvaloniaFact] public async Task ActualAudioOptionsDefaultsAndCancelUseWpfChoices()
    {
        var owner=new Window();owner.Show();try
        {
            var dialogs=new DesktopDialogs(owner);var pending=dialogs.ChooseAudioImportAsync(["one.wav","two.flac"],false);Dispatcher.UIThread.RunJobs();var form=Assert.Single(owner.OwnedWindows);
            Assert.False(form.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked);
            Assert.Equal(0,form.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex);
            form.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Null(await pending);
        }
        finally{owner.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact] public async Task ActualBatchFolderPreviewExcludesUnsupportedButManualSelectionKeepsItVisible()
    {
        using var files=new MediaDocumentTests();string disk=files.FilePath("source.dsk"),noise=files.FilePath("noise.flac");
        File.WriteAllBytes(disk,DskDocumentFactory.CreateCpm(false).Serialize());File.WriteAllBytes(noise,[1,2,3]);var before=File.ReadAllBytes(disk);
        var window=new BatchProcessWindow();window.Show();Dispatcher.UIThread.RunJobs();
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        try
        {
            var operations=Find<ComboBox>("BatchOperation");operations.SelectedItem=operations.Items.Cast<BatchProcessWindow.OperationChoice>().Single(o=>o.Value==BatchOperation.Defragment);
            Find<TextBox>("InputFolder").Text=files.DirectoryPath;Find<TextBox>("ExtensionFilter").Text=".dsk,.flac";
            window.GetVisualDescendants().OfType<CheckBox>().Single(c=>c.Content?.ToString()?.StartsWith("Strict preflight",StringComparison.Ordinal)==true||c.Content?.ToString()?.StartsWith("Strict all-or-nothing",StringComparison.Ordinal)==true).IsChecked=true;
            Dispatcher.UIThread.RunJobs();await window.PreviewAsync();Assert.Null(window.Model.Documents.Error);
            Assert.Equal(disk,Assert.Single(window.Session!.Rows).Input);Assert.Equal(before,File.ReadAllBytes(disk));
            Find<TextBox>("ExtensionFilter").Text=".flac";Dispatcher.UIThread.RunJobs();await window.PreviewAsync();Assert.Contains("No compatible",window.Model.Documents.Error);
            window.AddInputs([noise]);Assert.Equal("Unsupported input",Assert.IsType<BatchRowView>(Assert.Single(window.InputTable.Items)).Result);
            Assert.Equal(before,File.ReadAllBytes(disk));Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(noise));
        }
        finally{window.Close();await window.Model.Documents.WaitForIdleAsync();Dispatcher.UIThread.RunJobs();}
    }

    [Fact] public async Task QuickDiskCapacityFailureAndChangedSidecarCannotPartiallyApply()
    {
        using var files=new MediaDocumentTests();var tape=await files.Open();string source=files.FilePath("many.mzt");
        var model=new TapeDocument{Format=TapeDocumentFormat.Mzt};model.Records.AddRange(Enumerable.Range(0,35).Select(_=>tape.Model.Records[0].DeepClone()));File.WriteAllBytes(source,TapeDocumentIO.Serialize(model,TapeDocumentFormat.Mzt,true));
        var target=TapeSession.New(true);var preview=await MediaOperations.ImportAsync(target,[source],TestContext.Current.CancellationToken);
        Assert.ThrowsAny<Exception>(()=>target.ApplyRecords(preview.Records));Assert.Empty(target.Rows);
        var single=await MediaOperations.ImportAsync(target,[tape.Path!],TestContext.Current.CancellationToken);string sidecar=SidecarService.GetSidecarPath(tape.Path!);File.WriteAllText(sidecar,"changed after preview");
        Assert.Throws<IOException>(()=>AudioProgramImport.Verify(single.Sources,TestContext.Current.CancellationToken));Assert.Empty(target.Rows);
    }

}
