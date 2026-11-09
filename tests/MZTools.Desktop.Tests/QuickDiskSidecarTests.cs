using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

public sealed class QuickDiskSidecarTests
{
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    private static async Task<TapeSession> Source(MediaDocumentTests files)
    {
        string path=files.FilePath("source.mzt");var bytes=MediaDocumentTests.Mzf("ONE",16).Concat(MediaDocumentTests.Mzf("TWO",32)).ToArray();Array.Clear(bytes,62,66);Array.Clear(bytes,128+16+62,66);File.WriteAllBytes(path,bytes);
        var tape=await files.Open("source.mzt");var records=tape.Model.Records.Select(r=>r.DeepClone()).ToArray();records[0].Profile=TapeProfile.Normal1_3;records[1].Profile=TapeProfile.Ic1_2;tape.ApplyRecords(records);return tape;
    }
    [Theory][InlineData("Mzq")][InlineData("Qdf")][InlineData("QdSharpLegacy")][InlineData("QdHxc")][InlineData("QdFlashFloppy")]
    public async Task QuickDiskSaveAsAndSavePersistMqiProfilesWithoutChangingTheSource(string format)
    {
        using var files=new MediaDocumentTests();var tape=await Source(files);string source=tape.Path!;byte[] before=File.ReadAllBytes(source);var target=Enum.Parse<TapeDocumentFormat>(format);string output=files.FilePath("copy"+TapeSavePolicy.Extension(target));string side=files.FilePath("copy.mqi");
        var expected=tape.Model.Records.Select(r=>r.Profile).ToArray();byte[] expectedSide=SidecarService.SerializeMti(tape.Model.Records);files.Dialogs.Save=new(output,format,true);
        Assert.True(await files.Coordinator.SaveAsync(true),files.Coordinator.Error);Assert.Equal(expectedSide,File.ReadAllBytes(side));Assert.False(File.Exists(files.FilePath("copy.mzi")));Assert.Equal(before,File.ReadAllBytes(source));Assert.Equal(side,tape.Model.SidecarPath);
        Assert.All(tape.Model.Records,r=>Assert.Equal(MetadataOrigin.LoadedFromMqi,r.MetadataOrigin));Assert.Equal(expected,TapeDocumentIO.Read(output).Records.Select(r=>r.Profile));
        Assert.True(await files.Coordinator.CloseAsync());Assert.True(await files.Coordinator.OpenPathAsync(output));tape=Assert.IsType<TapeSession>(files.Coordinator.Active);Assert.Equal(expected,tape.Model.Records.Select(r=>r.Profile));Assert.True(files.Coordinator.IsOpenSource(side));
        var main=File.ReadAllBytes(output);tape.EditInline(tape.Rows[0],speed:"1:2");Assert.True(await files.Coordinator.SaveAsync(),files.Coordinator.Error);Assert.Equal(main,File.ReadAllBytes(output));Assert.Equal(TapeProfile.Normal1_2,TapeDocumentIO.Read(output).Records[0].Profile);Assert.Equal(before,File.ReadAllBytes(source));
        tape.SetSelection([tape.Rows[0]]);tape.Move(1);var reordered=tape.Model.Records.Select(r=>(r.Profile,new RecordRow(r,0).Name)).ToArray();Assert.True(await files.Coordinator.SaveAsync(),files.Coordinator.Error);Assert.Equal(reordered,TapeDocumentIO.Read(output).Records.Select(r=>(r.Profile,new RecordRow(r,0).Name)));
    }
    [Fact]public async Task ExternalMqiChangeBlocksBothFilesAndImportTracksMqi()
    {
        using var files=new MediaDocumentTests();var tape=await Source(files);string output=files.FilePath("copy.qdf"),side=files.FilePath("copy.mqi");files.Dialogs.Save=new(output,"Qdf",true);Assert.True(await files.Coordinator.SaveAsync(true),files.Coordinator.Error);
        var import=await MediaOperations.ImportAsync(TapeSession.New(false),[output],Token);Assert.Contains(side,import.Sources.Keys);Assert.Equal(new[]{TapeProfile.Normal1_3,TapeProfile.Ic1_2},import.Records.Select(r=>r.Profile));
        byte[] main=File.ReadAllBytes(output);File.AppendAllText(side,"# external edit\n");byte[] changed=File.ReadAllBytes(side);tape.EditInline(tape.Rows[0],speed:"1:2");
        Assert.False(await files.Coordinator.SaveAsync());Assert.Equal(main,File.ReadAllBytes(output));Assert.Equal(changed,File.ReadAllBytes(side));Assert.True(tape.IsModified);
        Assert.Throws<IOException>(()=>AudioProgramImport.Verify(import.Sources,Token));
    }
    [Fact]public async Task MqiCollisionAndStaleSidecarCannotWriteHalfAPair()
    {
        using var files=new MediaDocumentTests();var tape=await Source(files);string old=tape.Path!;byte[] source=File.ReadAllBytes(old);string target=files.FilePath("copy.qdf"),side=files.FilePath("copy.mqi");
        File.WriteAllText(side,"keep");files.Dialogs.Overwrite=false;files.Dialogs.Save=new(target,"Qdf",true);Assert.False(await files.Coordinator.SaveAsync(true));Assert.False(File.Exists(target));Assert.Equal("keep",File.ReadAllText(side));Assert.Equal(old,tape.Path);
        files.Dialogs.Save=new(target,"Qdf",false);Assert.False(await files.Coordinator.SaveAsync(true));Assert.Contains("stale metadata",files.Coordinator.Error);Assert.False(File.Exists(target));Assert.Equal(source,File.ReadAllBytes(old));
        string failed=files.FilePath("failed.qdf");Directory.CreateDirectory(files.FilePath("failed.mqi"));files.Dialogs.Overwrite=true;files.Dialogs.Save=new(failed,"Qdf",true);Assert.False(await files.Coordinator.SaveAsync(true));Assert.False(File.Exists(failed));Assert.Equal(old,tape.Path);Assert.Equal(source,File.ReadAllBytes(old));
    }
    [Fact]public async Task ExportAndBatchWriteMqiAndBatchTracksItsInputMetadata()
    {
        using var files=new MediaDocumentTests();var tape=await Source(files);string input=files.FilePath("input.qdf");
        using var export=await MediaOperations.ExportAsync(tape,new(input,"Qdf",SelectedOnly:false),Token);Assert.Contains(files.FilePath("input.mqi"),export.Outputs.Keys);
        foreach(var pair in export.Outputs)File.WriteAllBytes(pair.Key,pair.Value);
        var preview=BatchProcessService.Preview([input],new(BatchOperation.ConvertFormat,files.FilePath("out"),"MZQ"),cancellationToken:Token);var row=Assert.Single(preview.Rows);
        Assert.Equal("",row.Error);Assert.False(row.RequiresLossConfirmation);Assert.Contains(files.FilePath("input.mqi"),row.Dependencies.Keys);Assert.All(row.Contents,c=>Assert.NotEmpty(c.Profile));Assert.NotNull(row.SidecarBytes);
        BatchProcessService.Execute(preview,cancellationToken:Token);Assert.Equal("Completed",row.Result);Assert.True(File.Exists(Path.ChangeExtension(row.Output,".mqi")));Assert.Equal(tape.Model.Records.Select(r=>r.Profile),TapeDocumentIO.Read(row.Output).Records.Select(r=>r.Profile));
        var stale=BatchProcessService.Preview([input],new(BatchOperation.ConvertFormat,files.FilePath("stale-out"),"MZQ"),cancellationToken:Token);File.AppendAllText(files.FilePath("input.mqi"),"# changed after preview\n");BatchProcessService.Execute(stale,cancellationToken:Token);var rejected=Assert.Single(stale.Rows);Assert.Equal("Error",rejected.Result);Assert.Contains("sidecar changed",rejected.Error);Assert.False(File.Exists(rejected.Output));Assert.False(File.Exists(Path.ChangeExtension(rejected.Output,".mqi")));
    }
}
