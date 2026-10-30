using System.Runtime.InteropServices;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Tests;

internal sealed class DiskTestDialogs : IDocumentDialogs,IMediaDialogs,IDiskDialogs
{
    internal string? OpenPath;internal SaveChoice? Save;internal bool Accept=true;internal bool CancelForm;internal int Forms;
    internal CloseDecision Close=CloseDecision.Discard;
    internal IReadOnlyList<string> Inputs=[];internal string? Output {get;set;}
    internal Func<string,IReadOnlyList<DiskField>,DiskAnswer?>? Answer {get;set;}
    internal List<string> Reports {get;}=[];
    public Task<string?> OpenAsync()=>Task.FromResult(OpenPath);
    public Task<string?> SaveAsAsync(DocumentSession document)=>Task.FromResult(Save?.Path);
    public Task<SaveChoice?> ChooseSaveAsync(DocumentSession document)=>Task.FromResult(Save);
    public Task<bool> ConfirmOverwriteAsync(string path)=>Task.FromResult(Accept);
    public Task<bool> ConfirmLossAsync(string message)=>Task.FromResult(Accept);
    public Task<CloseDecision> ConfirmCloseAsync(DocumentSession document)=>Task.FromResult(Close);
    public Task<DiskAnswer?> DiskFormAsync(string title,IReadOnlyList<DiskField> fields,string explanation="")
    {Forms++;Reports.Add(explanation);return Task.FromResult(CancelForm?null:Answer is null?new DiskAnswer(fields.ToDictionary(f=>f.Key,f=>f.Value)):Answer(title,fields));}
    public Task<IReadOnlyList<string>> PickDiskFilesAsync(string title,bool multiple=false)=>Task.FromResult(Inputs);
    public Task<string?> PickDiskOutputAsync(string title,string suggestedName)=>Task.FromResult(Output);
    public Task<bool> ConfirmDiskAsync(string title,string report){Reports.Add(report);return Task.FromResult(Accept);}
    public Task<IReadOnlyList<string>> PickImportAsync()=>Task.FromResult(Inputs);
    public Task<CompressionChoice?> ChooseCompressionAsync()=>Task.FromResult<CompressionChoice?>(null);
    public Task<ExportChoice?> ChooseExportAsync(TapeSession session)=>Task.FromResult<ExportChoice?>(null);
    public Task<bool> ConfirmPreviewAsync(string title,string report)=>ConfirmDiskAsync(title,report);
}

public sealed class DiskDocumentTests : IDisposable
{
    internal string Root {get;}=Path.Combine(Path.GetTempPath(),"mztools-f5-"+Guid.NewGuid().ToString("N"));
    internal DiskTestDialogs Dialogs {get;}=new();
    internal DocumentCoordinator Coordinator {get;}
    internal ShellViewModel Shell {get;}
    internal byte[]? Clipboard;
    public DiskDocumentTests()
    {
        Directory.CreateDirectory(Root);Coordinator=new(new MediaDocumentLoader(Dialogs),Dialogs,new());
        Shell=new(Coordinator,_=>Task.CompletedTask,Dialogs,b=>{Clipboard=b;return Task.CompletedTask;},()=>Task.FromResult(Clipboard));
    }
    internal string FilePath(string name)=>Path.Combine(Root,name);
    internal async Task<DiskSession> Open(DskDocument? model=null,string name="original.dsk")
    {
        string path=FilePath(name);File.WriteAllBytes(path,(model??DskDocumentFactory.CreateFsmz(false)).SerializeContainer());Dialogs.OpenPath=path;
        Assert.True(await Coordinator.OpenAsync(),Coordinator.Error);return Assert.IsType<DiskSession>(Coordinator.Active);
    }
    internal async Task<DiskSession> Damaged()
    {
        byte[] bytes=DskImage.CreateUniform(3,1,2,512,1,0x4E,0xE5,"source").Serialize();bytes[0x34]=1;
        Dialogs.OpenPath=FilePath("damaged.dsk");File.WriteAllBytes(Dialogs.OpenPath,bytes);
        Assert.True(await Coordinator.OpenAsync(),Coordinator.Error);return Assert.IsType<DiskSession>(Coordinator.Active);
    }
    [Theory][InlineData("MzBasic")][InlineData("IplDisk")][InlineData("PersonalCpm")][InlineData("Sds400")][InlineData("LecCpmDd")][InlineData("LecCpmHd")][InlineData("Mrs")][InlineData("Lemmings")][InlineData("CustomRaw")]
    public async Task EveryNewProfileSaveAsAndReloadUsesExactCoreBytes(string profile)
    {
        var options=new DskNewOptions(Enum.Parse<DskNewFormat>(profile),profile is "MzBasic" or "IplDisk"?40:80,2);
        var session=DiskSession.New(options);Coordinator.Add(session);var expected=DiskFormatService.CreateNew(options).SerializeContainer();
        Dialogs.Save=new(FilePath(profile+".dsk"),"Dsk",false);
        Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error);Assert.False(session.IsModified);Assert.Equal(Dialogs.Save.Path,session.Model.FilePath);
        Assert.Equal(expected,File.ReadAllBytes(Dialogs.Save.Path));Archive(profile,expected);
        var reopened=DskDocument.Open(Dialogs.Save.Path);Assert.Equal(session.Model.FileSystem.Type,reopened.FileSystem.Type);
    }
    [Theory][InlineData("V1")][InlineData("V2")][InlineData("V3")]
    public async Task HfeMetadataEditKeepsOriginalAndCorePhysicalSerialization(string version)
    {
        var baseDisk=DskDocumentFactory.CreateFsmz(false);baseDisk.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);baseDisk.MarkModified();
        var model=DiskFormatService.SetNewContainer(baseDisk,Enum.Parse<HfeVersion>(version));
        var session=await Open(model,version+".hfe");var original=File.ReadAllBytes(session.Path!);
        session.SelectedFile=session.Files.Single();session.FileName="EDITED";var candidate=session.PrepareProperties();var expected=candidate.SerializeContainer();
        session.ApplyProperties(candidate,session.Revision);Dialogs.Save=new(FilePath(version+"-copy.hfe"),"Hfe",false);
        Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error);Assert.False(session.HasUnsavedChanges);Assert.Equal(expected,File.ReadAllBytes(session.Path!));
        Assert.Equal(original,File.ReadAllBytes(FilePath(version+".hfe")));Assert.Equal("EDITED",DskDocument.Open(session.Path!).FileSystem.ReadDirectory().Single().Name);Archive("edited-"+version,expected);
    }
    [Fact] public async Task HealthyOpenHasNoPromptAndUnknownRawRemainsInspectable()
    {
        var session=await Open();Assert.Equal(0,Dialogs.Forms);Assert.Same(session,Shell.ActiveDisk);Assert.True(Shell.ShowDisk);Assert.False(Shell.ShowReadiness);
        Assert.NotEmpty(session.Sectors);Assert.NotNull(session.Layout);Assert.Contains("FSMZ",session.Summary,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task DamagedOpenCancelLeavesExistingTabsAndBytes()
    {
        var first=await Open();byte[] damaged=DskImage.CreateUniform(3,1,2,512,1,0x4E,0xE5,"test").Serialize();damaged[0x34]=1;
        string path=FilePath("bad.dsk");File.WriteAllBytes(path,damaged);Dialogs.OpenPath=path;Dialogs.CancelForm=true;
        Assert.False(await Coordinator.OpenAsync());Assert.Same(first,Coordinator.Active);Assert.Single(Coordinator.Documents);Assert.Equal(damaged,File.ReadAllBytes(path));
    }
    [Fact] public async Task RepairRequiresNewSaveAsAndRebindsOnlyAfterVerifiedCommit()
    {
        var session=await Damaged();var path=session.Path!;var original=File.ReadAllBytes(path);Assert.True(session.RequiresRepairSaveAs);Assert.True(session.IsModified);Assert.False(session.CanSave);
        Dialogs.Save=new(path,"Dsk",false);Assert.False(await Coordinator.SaveAsync(true));Assert.True(session.IsModified);
        Dialogs.Save=new(FilePath("existing.dsk"),"Dsk",false);File.WriteAllBytes(Dialogs.Save.Path,[7]);Assert.False(await Coordinator.SaveAsync(true));Assert.Equal(new byte[]{7},File.ReadAllBytes(Dialogs.Save.Path));
        Dialogs.Save=new(FilePath("repaired.dsk"),"Dsk",false);Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error);
        Assert.False(session.RequiresRepairSaveAs);Assert.False(session.IsModified);Assert.Equal(original,File.ReadAllBytes(path));Assert.Equal(session.Path,session.Model.FilePath);
        Assert.Equal(session.Model.SerializeContainer(),File.ReadAllBytes(session.Path!));Archive("repair",File.ReadAllBytes(session.Path!));
    }
    [Fact] public async Task RepairSourceReplacementWithIdenticalBytesIsDetectedByIdentity()
    {
        var session=await Damaged();var bytes=File.ReadAllBytes(session.Path!);string replacement=FilePath("replacement");File.WriteAllBytes(replacement,bytes);File.Move(replacement,session.Path!,true);
        Dialogs.Save=new(FilePath("copy.dsk"),"Dsk",false);Assert.False(await Coordinator.SaveAsync(true));Assert.False(File.Exists(Dialogs.Save.Path));Assert.True(session.RequiresRepairSaveAs);Assert.Contains("replaced",Coordinator.Error);
    }
    [Fact] public async Task ChangedSourceBlocksOrdinarySaveAsAndRetainsBinding()
    {
        var session=await Open();string path=session.Path!;File.AppendAllText(path,"changed");Dialogs.Save=new(FilePath("copy.dsk"),"Dsk",false);
        Assert.False(await Coordinator.SaveAsync(true));Assert.Equal(path,session.Path);Assert.False(File.Exists(Dialogs.Save.Path));
    }
    [Fact] public async Task RepairHardlinkDestinationCannotOverwriteOriginal()
    {
        var session=await Damaged();var original=File.ReadAllBytes(session.Path!);string link=FilePath("hardlink.dsk");
        if(OperatingSystem.IsWindows())Assert.True(CreateHardLink(link,session.Path!,IntPtr.Zero));else Assert.Equal(0,linkUnix(session.Path!,link));
        Dialogs.Save=new(link,"Dsk",false);Assert.False(await Coordinator.SaveAsync(true));Assert.Equal(original,File.ReadAllBytes(link));Assert.Equal(original,File.ReadAllBytes(session.Path!));
    }
    [Fact] public async Task RepairDestinationCreatedAfterPreviewCannotBeReplaced()
    {
        var session=await Damaged();string destination=FilePath("raced.dsk");var plan=await ((IFileSetDocument)session).PrepareSaveAsync(new(destination,"Dsk",false),TestContext.Current.CancellationToken);
        using(plan.Protect!.Invoke())
        {
            File.WriteAllBytes(destination,[4]);Assert.Throws<IOException>(()=>AtomicFileTransaction.Write(plan.Outputs,new Dictionary<string,string?>{[destination]=null},TestContext.Current.CancellationToken));
        }
        Assert.Equal(new byte[]{4},File.ReadAllBytes(destination));Assert.True(session.RequiresRepairSaveAs);
    }
    [Fact] public async Task ImportAllOrNothingAndCancelPreviewLeaveImageUntouched()
    {
        var session=await Open();byte[] before=session.SerializeContainer();string one=FilePath("ONE.mzf"),two=FilePath("TWO.mzf");File.WriteAllBytes(one,MediaDocumentTests.Mzf("ONE"));File.WriteAllBytes(two,MediaDocumentTests.Mzf("TWO"));
        var preview=DiskOperations.Import(session,[one,two],TestContext.Current.CancellationToken);Assert.Equal(before,session.SerializeContainer());Dialogs.Accept=false;
        Assert.False(await Shell.AcceptDiskAsync(session,preview,TestContext.Current.CancellationToken));Assert.Empty(session.Files);Assert.False(session.IsModified);
        Dialogs.Accept=true;Assert.True(await Shell.AcceptDiskAsync(session,preview,TestContext.Current.CancellationToken));Assert.Equal(2,session.Files.Count);
        byte[] imported=session.SerializeContainer();Assert.ThrowsAny<Exception>(()=>DiskOperations.Import(session,[one],TestContext.Current.CancellationToken));Assert.Equal(imported,session.SerializeContainer());
    }
    [Fact] public async Task PropertiesDraftValidationAndRawHeaderIndependentSave()
    {
        var session=await Open();var candidate=session.Model.Clone();candidate.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);candidate.MarkModified();session.Apply(candidate,session.Revision);session.SelectedFile=session.Files.Single();
        session.FileName="SECOND";session.LoadAddress="0x4321";Assert.True(session.HasDraft);Assert.False(Shell.SaveCommand.CanExecute(null));
        var edited=session.PrepareProperties();session.ApplyProperties(edited,session.Revision);Assert.False(session.HasDraft);Assert.Equal("SECOND",session.Files.Single().Name);Assert.Equal((ushort)0x4321,session.Files.Single().Entry.LoadAddress);
        session.LoadAddress="wrong";Assert.Throws<FormatException>(session.PrepareProperties);session.ResetDraft();Assert.False(session.HasDraft);
    }
    [Fact] public async Task CpmPropertyEditPreservesPaddedPayload()
    {
        var model=DskDocumentFactory.CreateCpm(false);model.FileSystem.Insert("FIRST.COM",[1,2,3]);model.MarkModified();var session=await Open(model);
        session.SelectedFile=session.Files.Single();var payload=model.FileSystem.Extract(model.FileSystem.ReadDirectory().Single());session.User="3";session.FileReadOnly=true;var candidate=session.PrepareProperties();
        session.ApplyProperties(candidate,session.Revision);Assert.Equal(3,session.Files.Single().Entry.User);Assert.True(session.Files.Single().Entry.ReadOnly);Assert.Equal(payload,session.Model.FileSystem.Extract(session.Files.Single().Entry));
    }
    [Fact] public async Task StalePreviewCannotReplaceNewerDocument()
    {
        var s=await Open();var preview=DiskOperations.Preview(s,c=>{c.FileSystem.Insert("ONE",[1]);c.MarkModified();},"one");
        var next=DiskOperations.Preview(s,c=>{c.FileSystem.Insert("TWO",[2]);c.MarkModified();},"two");s.Apply(next.Candidate,next.Revision);
        Assert.Throws<InvalidOperationException>(()=>s.Apply(preview.Candidate,preview.Revision));Assert.Equal("TWO",s.Files.Single().Name);
    }
    [Fact] public async Task HexDraftApplyUsesCorePreviewAndRetainsOtherSectorBytes()
    {
        var s=await Open(DskDocumentFactory.CreateRaw(3,1,2,512,1,0x4E,0xE5,"test"));s.SelectedSector=s.Sectors[2];byte[] before=s.Model.Serialize();
        var bytes=s.SelectedSector.Sector.Data.ToArray();bytes[20]^=1;s.HexText=DskHexEditService.FormatHex(bytes);Assert.True(s.HasHexDraft);
        var preview=s.PrepareHex();var candidate=s.Model.Clone();DskHexEditService.Apply(candidate,preview,true);s.ApplyProperties(candidate,s.Revision);
        Assert.False(s.HasDraft);Assert.Equal(bytes,s.Model.Image.Tracks[1]!.Sectors[0].Data);Assert.Equal(before[256..768],s.Model.Serialize()[256..768]);Archive("hex",s.SerializeContainer());
    }
    [Fact] public async Task HexDraftBlocksSwitchAndCloseSaveWithoutLosingBuffer()
    {
        var s=await Open();s.SelectedSector=s.Sectors[0];string original=s.HexText;s.HexText="00";s.SelectedSector=s.Sectors[1];Assert.Equal(s.Sectors[0],s.SelectedSector);Assert.Equal("00",s.HexText);
        Dialogs.Close=CloseDecision.Save;Assert.False(await Coordinator.CloseAsync());Assert.Same(s,Coordinator.Active);s.ResetDraft();Assert.Equal(original,s.HexText);
    }
    [Fact] public async Task CrossDiskCopyAndTapeImportKeepSourceAndExportBinding()
    {
        var s=await Open();var source=DskDocumentFactory.CreateFsmz(false);source.FileSystem.Insert("FIRST",[3,4],1,0x4000,0x4000);source.MarkModified();byte[] before=source.Serialize();
        var packet=WorkspaceDragCodec.Encode(WorkspaceDragCodec.Capture(source,source.FileSystem.ReadDirectory()));var preview=DiskOperations.Paste(s,packet,DiskCollisionPolicy.Cancel);s.Apply(preview.Candidate,preview.Revision);
        Assert.Equal(before,source.Serialize());Assert.Equal("FIRST",s.Files.Single().Name);
        s.SelectedFile=s.Files.Single();var outputs=DiskOperations.ExportFiles(s,FilePath("out.mzf"),true,false);
        string binding=s.Path!;await Coordinator.WriteExportAsync(outputs,TestContext.Current.CancellationToken);Assert.Equal(binding,s.Path);Assert.Equal(new byte[]{3,4},File.ReadAllBytes(FilePath("out.mzf"))[128..]);
    }
    [Fact] public async Task ReadOnlySourceCannotMutateButSaveAsPreservesContent()
    {
        var s=await Open();string original=s.Path!;s.Loaded(original,s.SavedFingerprint!,true);Assert.False(s.CanEdit);
        Assert.Throws<InvalidOperationException>(()=>DiskOperations.Preview(s,c=>c.MarkModified(),"edit"));Dialogs.Save=new(FilePath("copy.dsk"),"Dsk",false);Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error);Assert.Equal(File.ReadAllBytes(original),File.ReadAllBytes(s.Path!));Assert.True(s.CanEdit);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task IplRebuildRenameAndReorderUseExistingWriter(bool single)
    {
        var path=FilePath("one.mzf");File.WriteAllBytes(path,MediaDocumentTests.Mzf("ONE"));var input=DiskOperations.ReadIplFiles([path],TestContext.Current.CancellationToken)[0];
        var inputs=single?new[]{input}:new[]{input,input with{DisplayName="TWO"}};
        var s=await Open(DiskOperations.BuildIpl(inputs,single));var changed=single?new[]{input with{DisplayName="RENAMED"}}:inputs.Reverse().ToArray();
        var preview=DiskOperations.RebuildIpl(s,changed);s.Apply(preview.Candidate,preview.Revision);Assert.Equal(DiskOperations.BuildIpl(changed,single).Serialize(),s.Model.Serialize());Archive(single?"ipl-single":"ipl-multi",s.SerializeContainer());
        Dialogs.Save=new(FilePath("ipl-copy.dsk"),"Dsk",false);Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error);Assert.Equal(s.Model.FileSystem.Type,DskDocument.Open(s.Path!).FileSystem.Type);
    }
    [Fact] public async Task PhysicalTrackCacheLoadsGeneratedAndStoredCellsWithoutMutatingDocument()
    {
        var s=await Open(DskDocumentFactory.CreateRaw(3,1,2,512,1,0x4E,0xE5,"test"));byte[] before=s.SerializeContainer();await s.LoadTrackAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(s.TrackData);Assert.Contains("GeneratedFrom=DSK",s.PhysicalReport);Assert.Equal(before,s.SerializeContainer());Assert.False(s.IsModified);
    }
    [Fact]public async Task RawSectorImportUsesExactCoreReplacementAndCannotPartiallyApply()
    {
        var model=DskDocumentFactory.CreateRaw(3,1,2,512,1,0x4E,0xE5,"test");var s=await Open(model);s.SelectedFile=s.Files[0];string path=FilePath("sector.bin");var bytes=Enumerable.Repeat((byte)0x42,512).ToArray();File.WriteAllBytes(path,bytes);
        var preview=DiskOperations.Import(s,[path],TestContext.Current.CancellationToken);Assert.NotEqual(bytes,s.Model.FileSystem.Extract(s.SelectedFile.Entry));s.Apply(preview.Candidate,preview.Revision);Assert.Equal(bytes,s.Model.FileSystem.Extract(s.Files[0].Entry));
        File.WriteAllBytes(path,[0]);var before=s.SerializeContainer();Assert.ThrowsAny<Exception>(()=>DiskOperations.Import(s,[path],TestContext.Current.CancellationToken));Assert.Equal(before,s.SerializeContainer());
    }
    [Fact]public async Task CrossDiskTargetNamesAreExplicitAndPayloadIsPreserved()
    {
        var target=await Open(DskDocumentFactory.CreateCpm(false));var source=DskDocumentFactory.CreateFsmz(false);source.FileSystem.Insert("LONG NAME",[1,2,3],1,0x4000,0x4000);source.MarkModified();var entry=source.FileSystem.ReadDirectory().Single();var packet=WorkspaceDragCodec.Encode(WorkspaceDragCodec.Capture(source,[entry]));
        Assert.ThrowsAny<Exception>(()=>DiskOperations.Paste(target,packet,DiskCollisionPolicy.Cancel));var preview=DiskOperations.Paste(target,packet,DiskCollisionPolicy.Cancel,new Dictionary<string,string>{[entry.Key]="SHORT.BIN"});target.Apply(preview.Candidate,preview.Revision);Assert.Equal("SHORT.BIN",target.Files.Single().Name);Assert.Equal(new byte[]{1,2,3},target.Model.FileSystem.Extract(target.Files.Single().Entry)[..3]);
    }
    internal static void Archive(string kind,byte[] bytes)
    {
        var output=Environment.GetEnvironmentVariable("MZTOOLS_F5_OUTPUT");if(output is null)return;string dir=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"binary-checks");Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,kind+".json"),System.Text.Json.JsonSerializer.Serialize(new{kind,length=bytes.LongLength,sha256=ImageVerificationService.Hash(bytes)}));
    }
    public void Dispose(){foreach(var d in Coordinator.Documents)d.Dispose();Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(Root));Directory.Delete(Root,true);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLink(string name,string existing,IntPtr security);
    [DllImport("libc",EntryPoint="link",SetLastError=true)]private static extern int linkUnix(string existing,string name);
}
