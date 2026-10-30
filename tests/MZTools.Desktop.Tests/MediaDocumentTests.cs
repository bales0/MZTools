using System.Buffers.Binary;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

internal sealed class MediaTestDialogs : IDocumentDialogs, IMediaDialogs
{
    public string? OpenPath { get; set; }
    public SaveChoice? Save { get; set; }
    public bool Overwrite { get; set; } = true;
    public bool AcceptLoss { get; set; } = true;
    public List<string> OverwritePaths { get; } = [];
    public IReadOnlyList<string> ImportPaths { get; set; } = [];
    public bool Apply { get; set; } = true;
    public CloseDecision CloseDecision { get; set; } = CloseDecision.Cancel;
    public ExportChoice? Export { get; set; }
    public CompressionChoice? Compression { get; set; }
    public Task<string?> OpenAsync() => Task.FromResult(OpenPath);
    public Task<string?> SaveAsAsync(DocumentSession document) => Task.FromResult(Save?.Path);
    public Task<SaveChoice?> ChooseSaveAsync(DocumentSession document) => Task.FromResult(Save);
    public Task<bool> ConfirmOverwriteAsync(string path) { OverwritePaths.Add(path); return Task.FromResult(Overwrite); }
    public Task<bool> ConfirmLossAsync(string message) => Task.FromResult(AcceptLoss);
    public Task<CloseDecision> ConfirmCloseAsync(DocumentSession document) => Task.FromResult(CloseDecision);
    public Task<IReadOnlyList<string>> PickImportAsync() => Task.FromResult(ImportPaths);
    public Task<CompressionChoice?> ChooseCompressionAsync() => Task.FromResult(Compression);
    public Task<ExportChoice?> ChooseExportAsync(TapeSession session) => Task.FromResult(Export);
    public Task<bool> ConfirmPreviewAsync(string title, string report) => Task.FromResult(Apply);
}

public sealed class MediaDocumentTests : IDisposable
{
    internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "mztools-f4-" + Guid.NewGuid().ToString("N"));
    internal readonly MediaTestDialogs Dialogs = new();
    internal readonly DocumentCoordinator Coordinator;
    public MediaDocumentTests() { Directory.CreateDirectory(DirectoryPath); Coordinator = new(new MediaDocumentLoader(), Dialogs, new()); }
    internal string FilePath(string name) => Path.Combine(DirectoryPath,name);
    internal static byte[] Mzf(string name = "RECORD", int length = 512)
    {
        var bytes = new byte[128 + length]; bytes[0] = 1; System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes,1); bytes[17] = 13;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);
        for(int i=24;i<128;i++) bytes[i]=(byte)i;
        for(int i=128;i<bytes.Length;i++) bytes[i]=(byte)(i%7);
        return bytes;
    }
    internal async Task<TapeSession> Open(string name = "original.mzf")
    {
        string path = FilePath(name); if (!File.Exists(path)) File.WriteAllBytes(path,Mzf());
        Dialogs.OpenPath = path; Assert.True(await Coordinator.OpenAsync(),Coordinator.Error);
        return Assert.IsType<TapeSession>(Coordinator.Active);
    }
    [Theory]
    [InlineData("Mzf")] [InlineData("M12")] [InlineData("Mzt")] [InlineData("Mzq")]
    [InlineData("Qdf")] [InlineData("QdSharpLegacy")] [InlineData("QdHxc")] [InlineData("QdFlashFloppy")]
    public async Task EveryBindableFormatUsesCoreBytesAndRebindsCopy(string format)
    {
        var tape = await Open(); string original = tape.Path!; var originalBytes = File.ReadAllBytes(original);
        var target = Enum.Parse<TapeDocumentFormat>(format); var expected = TapeDocumentIO.Serialize(tape.Model,target,true);
        Dialogs.Save = new(FilePath("copy"+TapeSavePolicy.Extension(target)),format);
        Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error);
        ArchiveBinary(format,File.ReadAllBytes(Dialogs.Save.Path));
        Assert.Equal(expected,File.ReadAllBytes(Dialogs.Save.Path)); Assert.Equal(originalBytes,File.ReadAllBytes(original));
        Assert.Equal(Dialogs.Save.Path,tape.Path); Assert.False(tape.IsModified);
        var read = TapeDocumentIO.Read(tape.Path!); Assert.Single(read.Records); Assert.Equal(tape.Model.Records[0].Body.MzfBody,read.Records[0].Body.MzfBody);
        tape.Name="CHANGED"; tape.ApplyMetadata(); Assert.Equal("",tape.Validation);
        Assert.True(await Coordinator.SaveAsync(),Coordinator.Error); Assert.Equal(originalBytes,File.ReadAllBytes(original));
        Assert.Equal("CHANGED",new RecordRow(TapeDocumentIO.Read(tape.Path!).Records[0],0).Name);
        ArchiveBinary(format+"-edited",File.ReadAllBytes(tape.Path!));
    }
    [Theory] [InlineData("Mzf",".mfi")] [InlineData("M12",".m2i")] [InlineData("Mzt",".mti")]
    public async Task MetadataProfilePersistsWithCorrectSidecar(string format,string sidecar)
    {
        var tape=await Open(); tape.Profile=TapeProfileNames.ToDisplayName(TapeProfile.Ic1_3); tape.ApplyMetadata();
        Dialogs.Save=new(FilePath("copy"+TapeSavePolicy.Extension(Enum.Parse<TapeDocumentFormat>(format))),format);
        Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error); Assert.True(File.Exists(FilePath("copy"+sidecar)));
        Assert.Equal(TapeProfile.Ic1_3,TapeDocumentIO.Read(tape.Path!).Records[0].Profile);
    }
    [Fact] public async Task FailedSidecarWritePreservesEntireOriginalSetAndBinding()
    {
        var tape=await Open(); var original=tape.Path!; var bytes=File.ReadAllBytes(original);
        tape.Name="EDIT"; tape.ApplyMetadata(); var target=FilePath("copy.mzf"); Directory.CreateDirectory(FilePath("copy.mfi"));
        Dialogs.Save=new(target,"Mzf"); Assert.False(await Coordinator.SaveAsync(true));
        Assert.Equal(original,tape.Path); Assert.True(tape.IsModified); Assert.False(File.Exists(target)); Assert.Equal(bytes,File.ReadAllBytes(original));
    }
    [Fact] public async Task ExternalSidecarChangeBlocksSave()
    {
        var tape=await Open(); File.WriteAllText(FilePath("original.mfi"),"externally created"); tape.Name="EDIT"; tape.ApplyMetadata();
        Assert.False(await Coordinator.SaveAsync()); Assert.Equal(Mzf(),File.ReadAllBytes(tape.Path!)); Assert.True(tape.IsModified);
    }
    [Fact] public async Task DeclinedMetadataCollisionDoesNotWriteMainFile()
    {
        var tape=await Open(); File.WriteAllText(FilePath("copy.mfi"),"keep"); Dialogs.Save=new(FilePath("copy.mzf"),"Mzf"); Dialogs.Overwrite=false;
        Assert.False(await Coordinator.SaveAsync(true)); Assert.False(File.Exists(Dialogs.Save.Path)); Assert.Equal("keep",File.ReadAllText(FilePath("copy.mfi")));
    }
    [Fact] public async Task ExistingSidecarCannotBeSilentlyLeftStale()
    {
        await Open(); File.WriteAllText(FilePath("copy.mfi"),"keep"); Dialogs.Save=new(FilePath("copy.mzf"),"Mzf",false);
        Assert.False(await Coordinator.SaveAsync(true)); Assert.Contains("stale metadata",Coordinator.Error); Assert.False(File.Exists(Dialogs.Save.Path));
    }
    [Fact] public async Task AmbiguousQdAndWrongExtensionAreNeverGuessed()
    {
        var tape=await Open(); Dialogs.Save=new(FilePath("wrong.qd"),"Mzt"); Assert.False(await Coordinator.SaveAsync(true));
        Assert.False(File.Exists(Dialogs.Save.Path)); Assert.EndsWith("original.mzf",tape.Path);
    }
    [Fact] public async Task InvalidMetadataDoesNotMutateRawHeaderOrPayload()
    {
        var tape=await Open(); var original=TapeDocumentWriter.SerializeMzf(tape.Model.Records[0],true);
        tape.Name="žluťoučký"; tape.ApplyMetadata(); Assert.NotEmpty(tape.Validation);
        Assert.Equal(original,TapeDocumentWriter.SerializeMzf(tape.Model.Records[0],true)); Assert.True(tape.HasDraft); Assert.False(tape.IsModified);
        Assert.False(await Coordinator.SaveAsync(true)); tape.ResetMetadata(); Assert.False(tape.HasDraft);
    }
    [Fact] public async Task MetadataEditPreservesAllUnrelatedRawDescriptionBytes()
    {
        var tape=await Open(); byte[] description=tape.Model.Records[0].GetSerializedHeader()[24..];
        tape.Name="RENAMED"; tape.LoadAddress="4200"; tape.ApplyMetadata(); Assert.Empty(tape.Validation);
        Assert.Equal(description,tape.Model.Records[0].GetSerializedHeader()[24..]); Assert.Equal((ushort)0x4200,tape.Model.Records[0].Header.MzfStart);
    }
    [Fact] public async Task ImportAndCompressionPreviewLeaveSourceUntouchedUntilApply()
    {
        var tape=await Open(); string imported=FilePath("import.mzf"); File.WriteAllBytes(imported,Mzf("SECOND"));
        var preview=await MediaOperations.ImportAsync(tape,[imported],TestContext.Current.CancellationToken); Assert.Single(tape.Model.Records);
        tape.ApplyRecords(preview.Records); Assert.Equal(2,tape.Model.Records.Count);
        var packed=await MediaOperations.CompressAsync(tape,new("Zx0"),TestContext.Current.CancellationToken); Assert.Equal(512,tape.Model.Records[0].Body.DataSize);
        tape.ApplyRecords(packed.Records); Assert.True(MzfLoaderBuilder.TryGetCompressionInfo(tape.Model.Records[0],out _));
        var restored=await MediaOperations.CompressAsync(tape,new("None"),TestContext.Current.CancellationToken); Assert.Equal(Mzf()[128..],restored.Records[0].Body.MzfBody);
        Assert.Equal(Mzf("SECOND"),File.ReadAllBytes(imported));
    }
    [Fact] public async Task LossPreviewCancelPreservesTrailingDataAndPath()
    {
        string source=FilePath("trailing.mzf"); File.WriteAllBytes(source,Mzf().Concat(new byte[]{7,8,9}).ToArray());
        var tape=await Open("trailing.mzf"); Dialogs.Save=new(FilePath("copy.mzt"),"Mzt"); Dialogs.AcceptLoss=false;
        Assert.False(await Coordinator.SaveAsync(true)); Assert.Equal(source,tape.Path); Assert.Equal(new byte[]{7,8,9},tape.Model.Records[0].Body.TrailingData); Assert.False(File.Exists(Dialogs.Save.Path));
    }
    [Theory] [InlineData("Wav",".wav")] [InlineData("Lep",".lep")] [InlineData("L16",".l16")]
    public async Task SignalExportMatchesExistingWriterAndDoesNotRebind(string format,string extension)
    {
        var tape=await Open(); var original=tape.Path;
        using var output=await MediaOperations.ExportAsync(tape,new(FilePath("export"+extension),format),TestContext.Current.CancellationToken);
        string expected=FilePath("expected"+extension); SharpTapeExporter.Export(expected,tape.Model.Records,Enum.Parse<SharpTapeOutputFormat>(format),cancellationToken:TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllBytes(expected),File.ReadAllBytes(output.SourceFiles.Values.Single()));
        Assert.Empty(output.Outputs.Single().Value);Assert.Single(output.SourceFiles);
        Assert.Equal(original,tape.Path); Assert.False(tape.IsModified);
        ArchiveBinary(format,File.ReadAllBytes(expected));
    }
    [Fact] public async Task ExportCannotOverwriteCurrentMediaOrSidecar()
    {
        var tape=await Open(); var outputs=new Dictionary<string,byte[]> {[tape.Path!]=new byte[]{1}};
        await Assert.ThrowsAsync<IOException>(()=>Coordinator.WriteExportAsync(outputs,TestContext.Current.CancellationToken)); Assert.Equal(Mzf(),File.ReadAllBytes(tape.Path!));
    }
    [Theory] [InlineData("Wav",".wav")] [InlineData("Lep",".lep")] [InlineData("L16",".l16")]
    public async Task ExportedSignalImportsBackThroughExistingCoreDecoder(string format,string extension)
    {
        var source=await Open();string path=FilePath("roundtrip"+extension);
        using var export=await MediaOperations.ExportAsync(source,new(path,format),TestContext.Current.CancellationToken);
        File.Copy(export.SourceFiles[path],path);
        var target=TapeSession.New(false);
        var import=await MediaOperations.ImportAsync(target,[path],TestContext.Current.CancellationToken);
        Assert.Single(import.Records);Assert.Equal(source.Model.Records[0].Body.MzfBody,import.Records[0].Body.MzfBody);
        Assert.Empty(target.Rows);
    }
    [Theory] [InlineData("Mzt")] [InlineData("Mzq")] [InlineData("Qdf")] [InlineData("QdSharpLegacy")] [InlineData("QdHxc")] [InlineData("QdFlashFloppy")]
    public async Task SupportedEmptyDocumentsRoundTrip(string format)
    {
        var tape=TapeSession.New(false); Coordinator.Add(tape); Dialogs.Save=new(FilePath("empty"+TapeSavePolicy.Extension(Enum.Parse<TapeDocumentFormat>(format))),format);
        Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error); Assert.Empty(TapeDocumentIO.Read(tape.Path!).Records);
        Assert.True(tape.CanEdit);
    }
    [Fact] public async Task NativeComExportMatchesExistingCoreBuilder()
    {
        var tape=await Open(); var choice=new ExportChoice(FilePath("export.com"),"Com");
        var preview=await MediaOperations.ExportAsync(tape,choice,TestContext.Current.CancellationToken);
        var expected=NativeComConversionService.Build(MzfNativeImageParser.Parse(tape.Model.Records[0]),CpmTargetProfile.BuiltIns[0],NativeMachineMode.Mz800Monitor,null,cancellationToken:TestContext.Current.CancellationToken);
        Assert.Equal(expected.Bytes,preview.Outputs.Single().Value);Assert.Contains(expected.Report,preview.Report);Assert.False(tape.IsModified);
        ArchiveBinary("Com",preview.Outputs.Single().Value);
    }
    [Fact] public async Task FilteringPreservesUnappliedMetadataAndSourceSelection()
    {
        var tape=await Open();tape.Name="DRAFT";tape.Filter="no match";
        Assert.True(tape.HasDraft);Assert.Equal("DRAFT",tape.Name);Assert.True(tape.SelectedHidden);
        tape.Filter="";Assert.True(tape.HasDraft);Assert.Equal("DRAFT",tape.Name);tape.ResetMetadata();Assert.False(tape.HasDraft);
    }
    [Fact] public async Task PackedExtensionAndMultiRecordSingleFileRequireSaveAsButStillAllowConversion()
    {
        var tape=await Open("packed.mz0");Assert.False(tape.CanSave);Assert.True(tape.CanSaveAs);
        tape.ApplyRecords(tape.Model.Records.Concat(tape.Model.Records.Select(r=>r.DeepClone())).ToArray());
        Dialogs.Save=new(FilePath("converted.mzt"),"Mzt");Assert.True(await Coordinator.SaveAsync(true),Coordinator.Error);
        Assert.True(tape.CanSave);Assert.Equal(2,TapeDocumentIO.Read(tape.Path!).Records.Count);
    }
    [Fact] public async Task StreamedSignalCommitPreservesBytesAndDisposesPrivateStaging()
    {
        var tape=await Open();string output=FilePath("streamed.lep");string staging;
        using(var plan=await MediaOperations.ExportAsync(tape,new(output,"Lep"),TestContext.Current.CancellationToken))
        {
            staging=plan.SourceFiles[output];var hash=AtomicFileTransaction.Hash(staging);
            await Coordinator.WriteExportAsync(plan.Outputs,TestContext.Current.CancellationToken,plan.SourceFiles);
            Assert.Equal(hash,AtomicFileTransaction.Hash(output));Assert.False(tape.IsModified);Assert.True(File.Exists(staging));
        }
        Assert.False(File.Exists(staging));
    }
    [Fact] public void GroupRollbackRestoresBothExistingFilesAfterInstallFailure()
    {
        string main=FilePath("main"),side=FilePath("side"); File.WriteAllText(main,"old-main"); File.WriteAllText(side,"old-side");
        var outputs=new Dictionary<string,byte[]> {[main]=[1],[side]=[2]};
        Assert.Throws<IOException>(()=>AtomicFileTransaction.Write(outputs,cancellation:TestContext.Current.CancellationToken,afterInstall:count=>{if(count==2)throw new IOException("Injected second install failure");}));
        Assert.Equal("old-main",File.ReadAllText(main)); Assert.Equal("old-side",File.ReadAllText(side)); Assert.Empty(Directory.GetFiles(DirectoryPath,".mztools-*"));
    }
    [Fact] public void FailedRollbackRetainsLastRecoverableBackup()
    {
        string main=FilePath("main"); File.WriteAllText(main,"recover-me");
        var error=Assert.Throws<IOException>(()=>AtomicFileTransaction.Write(new Dictionary<string,byte[]> {[main]=[1]},cancellation:TestContext.Current.CancellationToken,afterInstall:_=>
        {File.Delete(main);Directory.CreateDirectory(main);throw new IOException("Injected commit failure");}));
        Assert.Contains("Recovery backups retained",error.Message);
        Assert.Equal("recover-me",File.ReadAllText(Assert.Single(Directory.GetFiles(DirectoryPath,"*.bak"))));
    }
    private static void ArchiveBinary(string kind,byte[] bytes)
    {
        string? output=Environment.GetEnvironmentVariable("MZTOOLS_F4_OUTPUT");if(output is null)return;
        var directory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"binary-checks");Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,kind+".json"),System.Text.Json.JsonSerializer.Serialize(new
        {kind,length=bytes.LongLength,sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}));
    }
    public void Dispose() { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(DirectoryPath)); Directory.Delete(DirectoryPath,true); }
}
