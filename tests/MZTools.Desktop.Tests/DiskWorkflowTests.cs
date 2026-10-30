using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Tests;

public sealed class DiskWorkflowTests
{
    private static Task<bool> Tool(DiskDocumentTests h,DiskSession s,string tool)=>h.Coordinator.ExecuteAsync(ct=>h.Shell.RunDiskToolAsync(s,tool,ct));
    private static DskDocument WithFile(bool cpm=false)
    {
        var disk=cpm?DskDocumentFactory.CreateCpm(false):DskDocumentFactory.CreateFsmz(false);disk.FileSystem.Insert(cpm?"FIRST.BIN":"FIRST",[1,2,3],1,0x4000,0x4000);disk.MarkModified();return disk;
    }
    [Theory][InlineData("Defragment")][InlineData("Normalize container")][InlineData("Filesystem repair")]
    public async Task CancelledMutationToolsRetainBytesAndBinding(string tool)
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());byte[] before=s.SerializeContainer();string path=s.Path!;h.Dialogs.Accept=false;
        Assert.False(await Tool(h,s,tool));Assert.Equal(before,s.SerializeContainer());Assert.Equal(path,s.Path);Assert.False(s.IsModified);
    }
    [Theory][InlineData("Defragment")][InlineData("Filesystem repair")]
    public async Task ApplyToolsMatchesCorePreviewAndPreservesPayload(string tool)
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());byte[] payload=s.Model.FileSystem.Extract(s.Files.Single().Entry);
        var expected=s.Model.Clone();if(tool=="Defragment")DskDefragmentService.Preview(expected).Apply(expected);else {var action=DskFilesystemRepairService.For(expected);action.Apply(expected,action.Preview(expected));}
        Assert.True(await Tool(h,s,tool),h.Coordinator.Error);Assert.Equal(expected.Serialize(),s.Model.Serialize());Assert.Equal(payload,s.Model.FileSystem.Extract(s.Files.Single().Entry));DiskDocumentTests.Archive(tool,s.SerializeContainer());
    }
    [Fact]public async Task FormatPreviewCancelAndApplyReplaceCopyOnly()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());byte[] original=File.ReadAllBytes(s.Path!);h.Dialogs.Accept=false;
        Assert.False(await Tool(h,s,"Format disk"));Assert.Single(s.Files);h.Dialogs.Accept=true;Assert.True(await Tool(h,s,"Format disk"),h.Coordinator.Error);Assert.Empty(s.Files);Assert.True(s.IsModified);Assert.Equal(original,File.ReadAllBytes(s.Path!));
    }
    [Fact]public async Task PhysicalPropertiesPreviewPreservesPayloadAndOtherTracks()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());var before=s.Model.Serialize();
        h.Dialogs.Answer=(title,fields)=>{var values=fields.ToDictionary(f=>f.Key,f=>f.Value);if(title=="Physical track properties")values["creator"]="F5 PHYSICAL";return new(values);};
        Assert.True(await Tool(h,s,"Physical properties"),h.Coordinator.Error);Assert.Equal(before[256..],s.Model.Serialize()[256..]);Assert.Equal("F5 PHYSICAL",s.Model.Image.Creator);DiskDocumentTests.Archive("physical-properties",s.SerializeContainer());
    }
    [Fact]public async Task CompareAndDiffNavigationHaveRealCoreLocations()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());var other=s.Model.Clone();other.FileSystem.Rename(other.FileSystem.ReadDirectory().Single(),"SECOND");other.MarkModified();
        string path=h.FilePath("comparison.dsk");File.WriteAllBytes(path,other.Serialize());h.Dialogs.Inputs=[path];Assert.True(await Tool(h,s,"Compare image"),h.Coordinator.Error);Assert.NotEmpty(s.Differences);Assert.Contains("differ",s.ComparisonReport);Assert.False(s.IsModified);
    }
    [Fact]public async Task ConversionExportKeepsCurrentPathAndUsesCorePreflight()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());string original=s.Path!;h.Dialogs.Output=h.FilePath("converted.dsk");
        var profile=DskCapabilityService.GetAvailableConversions(s.Model)[0];var expected=DskFileTransferService.Preflight(s.Model,profile);Assert.True(expected.CanConvert);
        Assert.True(await Tool(h,s,"Convert filesystem"),h.Coordinator.Error);Assert.Equal(expected.OutputImage,File.ReadAllBytes(h.Dialogs.Output));Assert.Equal(original,s.Path);Assert.False(s.IsModified);DiskDocumentTests.Archive("filesystem-conversion",File.ReadAllBytes(h.Dialogs.Output));
    }
    [Theory][InlineData("HFE V1")][InlineData("HFE V2")][InlineData("HFE V3")]
    public async Task PhysicalContainerExportsMatchExistingCore(string format)
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());var expected=DiskOperations.ConvertContainer(s,format);
        Assert.Equal(MediaConversionService.DskToHfe(s.Model,Enum.Parse<HfeVersion>(format.Replace("HFE ",""))).Output,expected.Output);DiskDocumentTests.Archive("convert-"+format.Replace(' ','-'),expected.Output);
    }
    [Fact]public async Task HfeVariantConversionRetainsOriginalPhysicalCells()
    {
        using var h=new DiskDocumentTests();var model=DiskFormatService.SetNewContainer(WithFile(),HfeVersion.V1);var s=await h.Open(model,"physical.hfe");
        var converted=DiskOperations.ConvertContainer(s,"HFE V3");HfeImage.VerifyPhysical(s.PhysicalImage!,HfeImage.Parse(converted.Output));Assert.False(s.IsModified);DiskDocumentTests.Archive("hfe-version-conversion",converted.Output);
    }
    [Fact]public async Task RawExportImportAndPatchRoundtripUseSelectedAddresses()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());s.SelectedSector=s.Sectors.First(x=>(x.Sector.Role&DskSectorRole.Free)!=0);s.SectorSelection.Add(s.SelectedSector.Sector.Address);
        h.Dialogs.Output=h.FilePath("range.bin");Assert.True(await Tool(h,s,"Raw export"),h.Coordinator.Error);byte[] raw=File.ReadAllBytes(h.Dialogs.Output);Assert.Equal(s.SelectedSector.Sector.Data,raw);
        raw[5]^=1;File.WriteAllBytes(h.Dialogs.Output,raw);h.Dialogs.Inputs=[h.Dialogs.Output];Assert.True(await Tool(h,s,"Raw import"),h.Coordinator.Error);Assert.Equal(raw,s.SelectedSector!.Sector.Data);
        string baseline=h.FilePath("baseline.dsk");File.WriteAllBytes(baseline,File.ReadAllBytes(s.Path!));h.Dialogs.Inputs=[baseline];h.Dialogs.Output=h.FilePath("patch.json");Assert.True(await Tool(h,s,"Export patch"),h.Coordinator.Error);
        var target=await h.Open(DskDocument.Open(baseline),"target.dsk");h.Dialogs.Inputs=[h.Dialogs.Output];Assert.True(await Tool(h,target,"Apply patch"),h.Coordinator.Error);Assert.Equal(s.Model.Serialize(),target.Model.Serialize());DiskDocumentTests.Archive("raw-patch",target.SerializeContainer());
    }
    [Fact]public async Task CpmLayoutAndAppOwnedProfileRegistrationUsePrivateTestDirectory()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile(true));h.Shell.DiskProfileDirectory=h.FilePath("profiles");
        Assert.True(await Tool(h,s,"CP/M layout"),h.Coordinator.Error);Assert.NotNull(s.Model.AttachedDpb);
        Assert.True(await Tool(h,s,"Manage profiles"),h.Coordinator.Error);Assert.Single(UserProfileService.Layouts(h.Shell.DiskProfileDirectory),p=>!p.BuiltIn);
        h.Dialogs.Answer=(title,fields)=>{var values=fields.ToDictionary(f=>f.Key,f=>f.Value);if(title=="Profiles")values["action"]="Delete custom layout";return new(values);};
        Assert.True(await Tool(h,s,"Manage profiles"),h.Coordinator.Error);Assert.DoesNotContain(UserProfileService.Layouts(h.Shell.DiskProfileDirectory),p=>!p.BuiltIn);
    }
    [Fact]public async Task BootInstallUsesVerifiedBundledSystemAndPreservesOrdinaryFiles()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile(true));byte[] payload=s.Model.FileSystem.Extract(s.Files.Single().Entry);
        var bundled=BundledBootSystems.All.First(b=>DskCapabilityService.CanInstallBootSystem(s.Model,b.Open()).IsCompatible);
        h.Dialogs.Answer=(title,fields)=>{var values=fields.ToDictionary(f=>f.Key,f=>f.Value);if(title=="Install boot/system")values["source"]=bundled.DisplayName;return new(values);};
        Assert.True(await Tool(h,s,"Install boot/system"),h.Coordinator.Error);Assert.Equal(payload,s.Model.FileSystem.Extract(s.Model.FileSystem.ReadDirectory().Single(e=>e.Name=="FIRST")));DiskDocumentTests.Archive("boot-install",s.SerializeContainer());
    }
    [Fact]public async Task BootstrapExportImportAndMetadataUseDecodedCoreTrack()
    {
        using var h=new DiskDocumentTests();var model=DskDocumentFactory.CreatePersonalCpm80(false);var s=await h.Open(model);h.Dialogs.Output=h.FilePath("bootstrap.bin");
        Assert.True(await Tool(h,s,"Bootstrap export"),h.Coordinator.Error);Assert.Equal(BootstrapService.ExportBootstrap(s.Model),File.ReadAllBytes(h.Dialogs.Output));
        h.Dialogs.Inputs=[h.Dialogs.Output];Assert.True(await Tool(h,s,"Bootstrap import"),h.Coordinator.Error);
        h.Dialogs.Answer=(title,fields)=>{var values=fields.ToDictionary(f=>f.Key,f=>f.Value);if(title=="Bootstrap metadata")values["name"]="BOOT F5";return new(values);};
        Assert.True(await Tool(h,s,"Bootstrap metadata"),h.Coordinator.Error);Assert.Equal("BOOT F5",BootstrapService.Inspect(s.Model).Name);DiskDocumentTests.Archive("bootstrap-metadata",s.SerializeContainer());
    }
    [Fact]public async Task NativeComImportBuildsSameBytesAsCoreAndKeepsMZFSource()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile(true));string path=h.FilePath("native.mzf");File.WriteAllBytes(path,MediaDocumentTests.Mzf("GAME"));h.Dialogs.Inputs=[path];var original=File.ReadAllBytes(path);
        Assert.True(await Tool(h,s,"Import Native COM"),h.Coordinator.Error);var entry=s.Model.FileSystem.ReadDirectory().Single(e=>e.Extension=="COM");Assert.NotEmpty(s.Model.FileSystem.Extract(entry));Assert.Equal(original,File.ReadAllBytes(path));DiskDocumentTests.Archive("com-import",s.SerializeContainer());
    }
    [Fact]public async Task NativeDiskPacketPastesIntoTapeButMissingMetadataIsRefused()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());s.SelectedFile=s.Files.Single();var packet=WorkspaceDragCodec.Encode(DiskOperations.CapturePacket(s));
        var tape=TapeSession.New(false);h.Coordinator.Add(tape);await h.Shell.PasteAsync(packet);Assert.Single(tape.Rows);Assert.Equal(new byte[]{1,2,3},tape.Model.Records[0].Body.MzfBody);
        var cpm=await h.Open(WithFile(true),"cpm.dsk");cpm.SelectedFile=cpm.Files.Single();Assert.Throws<InvalidDataException>(()=>DiskOperations.RecordsFromPacket(WorkspaceDragCodec.Decode(WorkspaceDragCodec.Encode(DiskOperations.CapturePacket(cpm)))));
    }
    [Fact]public async Task RepairSourceChangeDuringStagingPreventsOutputCommit()
    {
        using var h=new DiskDocumentTests();var s=await h.Damaged();string output=h.FilePath("copy.dsk");var plan=await ((IFileSetDocument)s).PrepareSaveAsync(new(output,"Dsk",false),TestContext.Current.CancellationToken);
        var source=File.ReadAllBytes(s.Path!);source[^1]^=1;File.WriteAllBytes(s.Path!,source);
        Assert.Throws<IOException>(()=>AtomicFileTransaction.Write(plan.Outputs,new Dictionary<string,string?>{[output]=null},TestContext.Current.CancellationToken,beforeCommit:plan.BeforeCommit));Assert.False(File.Exists(output));Assert.True(s.RequiresRepairSaveAs);
    }
    [Fact]public async Task CloseSaveShowsDraftPreviewThenPersistsAndCloses()
    {
        using var h=new DiskDocumentTests();var s=await h.Open(WithFile());s.SelectedFile=s.Files.Single();s.FileName="CLOSED";h.Dialogs.Close=CloseDecision.Save;
        Assert.True(await h.Coordinator.CloseAsync(),h.Coordinator.Error);Assert.Empty(h.Coordinator.Documents);Assert.Equal("CLOSED",DskDocument.Open(s.Path!).FileSystem.ReadDirectory().Single().Name);
    }
    [Fact]public async Task RepairTrailingTruncateIsSeparateAndRequiresNewCopy()
    {
        using var h=new DiskDocumentTests();var model=WithFile();byte[] trailing=model.Serialize().Concat(new byte[]{1,2,3}).ToArray();string path=h.FilePath("trailing.dsk");File.WriteAllBytes(path,trailing);h.Dialogs.OpenPath=path;
        Assert.True(await h.Coordinator.OpenAsync(),h.Coordinator.Error);var s=Assert.IsType<DiskSession>(h.Coordinator.Active);Assert.False(s.RequiresRepairSaveAs);
        h.Dialogs.Accept=false;Assert.False(await Tool(h,s,"Container repair"));Assert.Equal(trailing,s.SerializeContainer());
        h.Dialogs.Accept=true;Assert.True(await Tool(h,s,"Container repair"),h.Coordinator.Error);Assert.True(s.RequiresRepairSaveAs);Assert.Equal(trailing,File.ReadAllBytes(path));Assert.Equal(model.Serialize(),s.SerializeContainer());
    }
}
