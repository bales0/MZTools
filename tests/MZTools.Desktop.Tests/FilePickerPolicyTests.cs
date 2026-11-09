using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Tests;

public sealed class FilePickerPolicyTests
{
    private static string[] Patterns(FilePickerOpenOptions options) => options.FileTypeFilter![0].Patterns!
        .Select(p => p.ToLowerInvariant()).Distinct().Order().ToArray();
    [Fact] public void OpenUnionExactlyMatchesLoaderAndHasUsefulCategories()
    {
        var options=FilePickerPolicy.Options(FilePickerPurpose.OpenMedia,"Title does not determine purpose");
        Assert.Equal(new[]{"*.dsk","*.l16","*.lep","*.flac","*.hfe","*.m12","*.mz0","*.mz7","*.mzf","*.mzq","*.mzt","*.qd","*.qdf","*.wav","*.wav.json"}.Order(),Patterns(options));
        Assert.Equal("All supported media",options.FileTypeFilter![0].Name);
        Assert.Equal(5,options.FileTypeFilter.Count);
        foreach(string path in new[]{"one.bin","one.com","one.json","one.mfi","one.m2i","one.mti"})
            Assert.False(FilePickerPolicy.Matches(options,path),path);
    }
    [Fact] public void AudioAndRecordImportAreDifferentContracts()
    {
        Assert.Equal(new[]{"*.flac","*.wav","*.wav.json"},Patterns(FilePickerPolicy.Options(FilePickerPurpose.OpenAudio,"Audio")));
        var options=FilePickerPolicy.Options(FilePickerPurpose.ImportTapeRecords,"Records",true,target:TapeSession.New(false));
        Assert.Contains("*.lep",Patterns(options));Assert.Contains("*.l16",Patterns(options));
        Assert.Contains("*.flac",Patterns(options));Assert.Contains("*.wav.json",Patterns(options));
        foreach(var format in FormatChoice.All)Assert.Contains("*"+format.Extension,Patterns(options));
    }
    [Theory][InlineData(".WAV")][InlineData(".FlAc")][InlineData(".WaV.JsOn")]
    public void AudioSuffixMatchingIsCaseInsensitiveAndManifestIsComplete(string suffix)
    {
        var options=FilePickerPolicy.Options(FilePickerPurpose.OpenAudio,"Audio");
        Assert.True(FilePickerPolicy.Matches(options,"recording"+suffix));
        Assert.True(options.FileTypeFilter!.SelectMany(g=>g.Patterns!).Count()<=6);
        Assert.False(FilePickerPolicy.Matches(options,"recording.json"));
        Assert.False(FilePickerPolicy.Matches(options,"recording.wav.json.backup"));
    }
    [Fact] public void EveryPurposeHasCompatibleFirstFilterAndOnlyRawHasWildcardFallback()
    {
        FilePickerPurpose[] raw=[FilePickerPurpose.ImportDiskFiles,FilePickerPurpose.ImportCpmDiskFiles,FilePickerPurpose.RawSectorImport,FilePickerPurpose.BootstrapImport];
        foreach(var purpose in Enum.GetValues<FilePickerPurpose>())
        {
            var options=FilePickerPolicy.Options(purpose,"Same title",batchExtensions:BatchMediaPolicy.AllExtensions);
            Assert.NotEmpty(options.FileTypeFilter!);Assert.NotEmpty(Patterns(options));
            Assert.DoesNotContain("*",Patterns(options));Assert.DoesNotContain("*.*",Patterns(options));
            Assert.Equal(raw.Contains(purpose),options.FileTypeFilter!.Skip(1).Any(g=>g.Patterns!.Contains("*")));
            if(raw.Contains(purpose))Assert.True(FilePickerPolicy.Matches(options,"arbitrary.unusual-extension"));
        }
    }
    [Fact] public void ComImportAndConversionDoNotPromiseTheSameInputs()
    {
        Assert.Equal(new[]{"*.m12","*.mzf"},Patterns(FilePickerPolicy.Options(FilePickerPurpose.ImportNativeComSource,"Import")));
        Assert.Equal(new[]{"*.m12","*.mz0","*.mz7","*.mzf"},Patterns(FilePickerPolicy.Options(FilePickerPurpose.ConvertNativeComSource,"Convert")));
        var ipl=Patterns(FilePickerPolicy.Options(FilePickerPurpose.ImportIplPrograms,"IPL"));
        Assert.Contains("*.m12",ipl);Assert.Contains("*.mzt",ipl);Assert.DoesNotContain("*.bin",ipl);
    }
    [Fact] public void BatchEveryOperationAndTargetUsesCorePolicyWithoutUnrestrictedFallback()
    {
        foreach(var op in Enum.GetValues<BatchOperation>())foreach(string target in BatchMediaPolicy.Targets)
        {
            var extensions=BatchMediaPolicy.Extensions(op,target);
            var options=FilePickerPolicy.Options(FilePickerPurpose.BatchInputs,"Batch",true,batchExtensions:extensions);
            Assert.Single(options.FileTypeFilter!);
            Assert.Equal(extensions.Split(',').Select(e=>"*"+e).Distinct().Order(),Patterns(options));
            foreach(string suffix in BatchMediaPolicy.AllExtensions.Split(','))
                Assert.Equal(BatchMediaPolicy.Supports(op,"file"+suffix,target),FilePickerPolicy.Matches(options,"file"+suffix));
        }
    }
    [Theory][InlineData(FilePickerPurpose.CopyFromDisk)][InlineData(FilePickerPurpose.CompareDisk)][InlineData(FilePickerPurpose.BootSystemSource)]
    public void DiskToolsOfferBothActualSupportedContainers(FilePickerPurpose purpose) =>
        Assert.Equal(new[]{"*.dsk","*.hfe"},Patterns(FilePickerPolicy.Options(purpose,"Disk")));
}

public sealed class OpenImportPickerTests
{
    [AvaloniaFact] public async Task ActualDialogMethodsPassOptionsBeforeSelectionAndCancelCleanly()
    {
        FilePickerOpenOptions? captured=null;
        var dialogs=new DesktopDialogs(new Window()){InputPicker=o=>{captured=o;return Task.FromResult<IReadOnlyList<string>>([]);}};
        Assert.Null(await dialogs.OpenAsync());Assert.Equal("All supported media",captured!.FileTypeFilter![0].Name);Assert.False(captured.AllowMultiple);
        Assert.Empty(await dialogs.PickImportAsync(TapeSession.New(false)));Assert.True(captured.AllowMultiple);Assert.True(FilePickerPolicy.Matches(captured,"x.flac"));
        Assert.Empty(await dialogs.PickDiskFilesAsync(FilePickerPurpose.CompareDisk,"Misleading audio title"));Assert.True(FilePickerPolicy.Matches(captured,"x.hfe"));
        Assert.Empty(await dialogs.PickBatchFilesAsync("Batch",BatchMediaPolicy.Extensions(BatchOperation.Defragment)));
        Assert.False(FilePickerPolicy.Matches(captured,"x.mzf"));Assert.True(captured.AllowMultiple);
        var audio=new DesktopDialogs(new Window()){AudioOnly=true,InputPicker=dialogs.InputPicker};
        Assert.Null(await audio.OpenAsync());Assert.True(FilePickerPolicy.Matches(captured!,"x.WaV.JsOn"));
    }
    [AvaloniaFact] public async Task ManuallyEnteredUnsupportedPathIsRejectedEvenIfProviderReturnsIt()
    {
        var dialogs=new DesktopDialogs(new Window()){InputPicker=_=>Task.FromResult<IReadOnlyList<string>>(["unsupported.bin"])};
        await Assert.ThrowsAsync<InvalidDataException>(()=>dialogs.OpenAsync());
        Assert.Single(await dialogs.PickDiskFilesAsync(FilePickerPurpose.RawSectorImport,"Raw"));
    }
    [AvaloniaFact] public async Task ReadOnlyHostNeverStartsImportPicker()
    {
        var read=QdImageReaderWriter.Read(QdImageReaderWriter.Write([],QdImageFormat.HxcPhysical));
        // Explicitly unknown host, independent of a large external fixture.
        read=read with{Analysis=QuickDiskHostDetector.Detect(new byte[256])};
        var model=new TapeDocument{Format=TapeDocumentFormat.QdHxc,QuickDiskReadResult=read};
        var tape=new TapeSession(model);Assert.True(model.IsReadOnlyQuickDisk);
        int calls=0;var dialogs=new DesktopDialogs(new Window()){InputPicker=_=>{calls++;return Task.FromResult<IReadOnlyList<string>>([]);}};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>dialogs.PickImportAsync(tape));Assert.Equal(0,calls);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MediaOperations.ImportAsync(tape,[],TestContext.Current.CancellationToken));
    }
    [Theory][InlineData("invalid.flac")][InlineData("invalid.mzf")][InlineData("invalid.wav.json")]
    public async Task FailedMultiImportPreservesDocumentSourceMetadataAndSidecar(string badName)
    {
        using var files=new MediaDocumentTests();var tape=await files.Open();string source=tape.Path!;var bytes=File.ReadAllBytes(source);long revision=tape.Revision;
        string good=files.FilePath("good.MZF"),bad=files.FilePath(badName);File.WriteAllBytes(good,MediaDocumentTests.Mzf("GOOD"));File.WriteAllBytes(bad,[1,2,3]);
        string sidecar=SidecarService.GetSidecarPath(source);File.WriteAllText(sidecar,"unchanged marker");
        await Assert.ThrowsAnyAsync<Exception>(()=>MediaOperations.ImportAsync(tape,[good,bad],TestContext.Current.CancellationToken));
        Assert.Equal(revision,tape.Revision);Assert.Single(tape.Model.Records);Assert.Equal(source,tape.Path);Assert.Equal(bytes,File.ReadAllBytes(source));
        Assert.Equal("unchanged marker",File.ReadAllText(sidecar));Assert.Equal(MediaDocumentTests.Mzf("GOOD"),File.ReadAllBytes(good));
    }
    [Fact] public async Task ImportCancelAndSuccessfulUppercaseMzfLeaveSourceUntouched()
    {
        using var files=new MediaDocumentTests();var tape=await files.Open();long revision=tape.Revision;
        var shell=new ShellViewModel(files.Coordinator,_=>Task.CompletedTask,files.Dialogs);
        files.Dialogs.ImportPaths=[];shell.ImportCommand.Execute(null);while(files.Coordinator.IsBusy)await Task.Delay(5,TestContext.Current.CancellationToken);Assert.Equal(revision,tape.Revision);
        string path=files.FilePath("import.MZF");var bytes=MediaDocumentTests.Mzf("IMPORTED");File.WriteAllBytes(path,bytes);files.Dialogs.ImportPaths=[path];
        files.Dialogs.Apply=false;shell.ImportCommand.Execute(null);while(files.Coordinator.IsBusy)await Task.Delay(5,TestContext.Current.CancellationToken);Assert.Equal(revision,tape.Revision);Assert.Single(tape.Model.Records);
        files.Dialogs.Apply=true;shell.ImportCommand.Execute(null);while(files.Coordinator.IsBusy)await Task.Delay(5,TestContext.Current.CancellationToken);Assert.Equal(2,tape.Model.Records.Count);Assert.Equal(bytes,File.ReadAllBytes(path));
    }
    [Fact] public async Task UnsupportedOpenAndMalformedDiskKeepPreviousDocument()
    {
        using var files=new MediaDocumentTests();var tape=await files.Open();
        foreach(string name in new[]{"wrong.bin","bad.dsk","bad.wav.json"})
        {
            string path=files.FilePath(name);File.WriteAllBytes(path,[1,2,3]);files.Dialogs.OpenPath=path;
            Assert.False(await files.Coordinator.OpenAsync());Assert.Same(tape,files.Coordinator.Active);Assert.Single(files.Coordinator.Documents);
        }
    }
    [Fact] public async Task DiskImportPreviewUsesRawNonstandardSuffixAndCancelPreservesBytes()
    {
        using var files=new DiskDocumentTests();var disk=await files.Open();var before=disk.SerializeContainer();
        string path=files.FilePath("RAW.ODD");File.WriteAllBytes(path,[1,2,3]);files.Dialogs.Inputs=[path];files.Dialogs.Accept=false;
        files.Shell.DiskImportCommand.Execute(null);while(files.Coordinator.IsBusy)await Task.Delay(5,TestContext.Current.CancellationToken);Assert.Equal(before,disk.SerializeContainer());
        var preview=DiskOperations.Import(disk,[path],TestContext.Current.CancellationToken);Assert.Equal(before,disk.SerializeContainer());
        disk.Apply(preview.Candidate,preview.Revision);Assert.NotEqual(before,disk.SerializeContainer());Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(path));
    }
    [Fact] public async Task IplAcceptsM12AndMultipleMztRecordsWithoutChangingSources()
    {
        using var files=new MediaDocumentTests();var tape=await files.Open();
        string m12=files.FilePath("ipl.M12"),mzt=files.FilePath("ipl.MZT");File.WriteAllBytes(m12,MediaDocumentTests.Mzf());
        var model=new TapeDocument{Format=TapeDocumentFormat.Mzt};model.Records.AddRange([tape.Model.Records[0].DeepClone(),tape.Model.Records[0].DeepClone()]);
        File.WriteAllBytes(mzt,TapeDocumentIO.Serialize(model,TapeDocumentFormat.Mzt,true));
        Assert.Equal(3,DiskOperations.ReadIplFiles([m12,mzt],TestContext.Current.CancellationToken).Length);
        Assert.Equal(MediaDocumentTests.Mzf(),File.ReadAllBytes(m12));
    }
    [Theory][InlineData("WAV")][InlineData("FLAC")][InlineData("JSON")]
    public async Task ShortAudioAndCompleteManifestOpenThroughRealLoader(string format)
    {
        using var files=new AudioDocumentTests();string path;
        if(format=="JSON")
        {
            path=files.Manifest();string renamed=path[..^4]+"JSON";File.Move(path,renamed);path=renamed;
        }
        else
        {
            path=files.Wave("short.WAV",frames:1024);
            if(format=="FLAC")
            {
                MZTools.Platform.Audio.NativeAudioCodecs.Configure();string flac=files.FilePath("short.FLAC");
                CorePlatformServices.EncodeFlac(path,flac,TestContext.Current.CancellationToken);path=flac;
            }
        }
        var before=File.ReadAllBytes(path);var session=await files.Open(path);
        Assert.True(session.IsReadOnly);Assert.Equal(before,File.ReadAllBytes(path));
    }
    [Fact] public async Task SymlinkInputWithSupportedSuffixPreservesTarget()
    {
        using var files=new MediaDocumentTests();string target=files.FilePath("target.mzf"),link=files.FilePath("alias.MZF");
        var before=MediaDocumentTests.Mzf();File.WriteAllBytes(target,before);
        try{File.CreateSymbolicLink(link,target);}
        catch(Exception error) when(OperatingSystem.IsWindows()&&error is IOException or UnauthorizedAccessException)
        {Assert.Skip("Windows symlink creation unavailable: "+error.Message);return;}
        var loaded=await new MediaDocumentLoader().LoadAsync(link,TestContext.Current.CancellationToken);using(loaded)Assert.IsType<TapeSession>(loaded);
        Assert.Equal(before,File.ReadAllBytes(target));
    }
    [Theory][InlineData("Fsmz",FilePickerPurpose.ImportDiskFiles)][InlineData("Cpm",FilePickerPurpose.ImportCpmDiskFiles)][InlineData("Raw",FilePickerPurpose.RawSectorImport)]
    public async Task ShellDiskImportPassesFilesystemPurposeEvenOnCancel(string filesystem,FilePickerPurpose expected)
    {
        using var files=new DiskDocumentTests();var model=filesystem switch{"Cpm"=>DskDocumentFactory.CreateCpm(false),"Raw"=>DskDocumentFactory.CreateRaw(3,1,2,512,1,0x4E,0xE5,"raw"),_=>DskDocumentFactory.CreateFsmz(false)};
        var disk=await files.Open(model);var before=disk.SerializeContainer();files.Dialogs.Inputs=[];
        files.Shell.DiskImportCommand.Execute(null);while(files.Coordinator.IsBusy)await Task.Delay(5,TestContext.Current.CancellationToken);
        Assert.Equal(expected,files.Dialogs.LastPickerPurpose);Assert.Equal(before,disk.SerializeContainer());
    }
    [Fact] public void InvalidPatchJsonCannotBeAppliedJustBecauseSuffixMatches()
    {
        using var files=new MediaDocumentTests();string path=files.FilePath("wrong.mzpatch.json");File.WriteAllText(path,"{}");
        Assert.True(FilePickerPolicy.Matches(FilePickerPolicy.Options(FilePickerPurpose.ApplyDiskPatch,"Patch"),path));
        var disk=DskDocumentFactory.CreateFsmz(false);var before=disk.Serialize();
        Assert.Throws<InvalidDataException>(()=>DskPatchService.Preview(disk,DskPatchService.Read(path)));Assert.Equal(before,disk.Serialize());Assert.Equal("{}",File.ReadAllText(path));
    }

    [Theory][InlineData(".mzf")][InlineData(".M12")][InlineData(".mz0")][InlineData(".MZ7")]
    public void StandaloneComConversionAliasesAreActuallyReadable(string suffix)
    {
        using var files=new MediaDocumentTests();string path=files.FilePath("native"+suffix);var bytes=MediaDocumentTests.Mzf();File.WriteAllBytes(path,bytes);
        var parsed=MzfNativeImageParser.Read(path);Assert.NotNull(parsed);Assert.Equal(bytes,File.ReadAllBytes(path));
    }

}
