using System.Buffers.Binary;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class MzfDescriptionEditingTests
{
    internal static byte[] FileBytes(byte[]? description=null,int length=64)
    {
        var bytes=new byte[128+length+3];bytes[0]=1;"TEST"u8.CopyTo(bytes.AsSpan(1));bytes[17]=13;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),(ushort)length);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);
        if(description!=null)description.CopyTo(bytes,24);
        for(int i=0;i<length;i++)bytes[128+i]=(byte)(i%7);bytes[^3]=0xa5;bytes[^2]=0;bytes[^1]=0xff;return bytes;
    }
    private static TapeRecord Record(byte[] bytes)
    {
        using var stream=new MemoryStream(bytes);using var reader=new BinaryReader(stream);var record=new MZTFileReader().ReadMzfRecord(reader);
        var body=record.Body;body.TrailingData=reader.ReadBytes((int)(stream.Length-stream.Position));record.Body=body;return record;
    }
    private static async Task<TapeSession> Open(MediaDocumentTests files,byte[] bytes,string name="original.mzf")
    {File.WriteAllBytes(files.FilePath(name),bytes);return await files.Open(name);}
    [Theory][InlineData(0)][InlineData(1)][InlineData(104)]
    public void TextEncoding_HasIndependentExactBytesAndZeroPadding(int count)
    {
        var bytes=MzfDescriptionEditing.EncodeText(new string('A',count));Assert.Equal(104,bytes.Length);
        Assert.Equal(Enumerable.Repeat((byte)0x41,count).Concat(Enumerable.Repeat((byte)0,104-count)),bytes);
    }
    [Theory][InlineData("é")][InlineData("ABC\nDEF")][InlineData("😀")][InlineData("^")]
    public void UnsupportedCharactersAreNotSilentlyReplaced(string text)=>Assert.Throws<InvalidDataException>(()=>MzfDescriptionEditing.EncodeText(text));
    [Fact]public void EncodedOverflowIsRejectedInsteadOfTruncated()=>Assert.Throws<InvalidDataException>(()=>MzfDescriptionEditing.EncodeText(new string('A',105)));
    [Fact]public void DecodePreservesTextSpacesAndValidSharpCharactersAreNotCode()
    {
        var raw=MzfDescriptionEditing.EncodeText("A a  ");Assert.Equal("A a  ",MzfDescriptionEditing.DecodeText(raw));
        Assert.Equal(0xA1,raw[2]);Assert.Equal(MzfDescriptionKind.Text,MzfDescriptionEditing.Inspect(Record(FileBytes(raw))).Kind);
    }
    [Fact]public void OneLetterTextCorrectionChangesOnlyItsByteIncludingOriginalTrailingSpaces()
    {
        var source=FileBytes(MzfDescriptionEditing.EncodeText("ABCDEF  "));var record=Record(source);
        var changed=MzfDescriptionEditing.Replace(record,MzfDescriptionEditing.EncodeText("ABXDEF  "));var result=TapeDocumentWriter.SerializeMzf(changed,true);
        Assert.Equal(new[]{26},Enumerable.Range(0,source.Length).Where(i=>source[i]!=result[i]).ToArray());Assert.Equal((byte)'X',result[26]);
    }
    [Fact]public async Task NoOpAndOtherMetadata_PreserveEveryOriginalDescriptionByte()
    {
        using var files=new MediaDocumentTests();var raw=Enumerable.Range(0,104).Select(i=>(byte)(i*83)).ToArray();raw[^1]=32;raw[^2]=32;
        var source=FileBytes(raw);var session=await Open(files,source);Assert.False(session.HasDraft);session.ApplyMetadata();
        Assert.False(session.IsModified);Assert.Equal(source,TapeDocumentWriter.SerializeMzf(session.Model.Records[0],true));
        session.Name="RENAMED";session.LoadAddress="4200";session.ExecuteAddress="4300";session.ApplyMetadata();
        Assert.Empty(session.Validation);Assert.Equal(raw,session.Model.Records[0].DescriptionRaw);Assert.Equal(source.AsSpan(128).ToArray(),TapeDocumentWriter.SerializeMzf(session.Model.Records[0],true).AsSpan(128).ToArray());
        files.Dialogs.Save=new(files.FilePath("copy.mzf"),"Mzf",false);Assert.True(await files.Coordinator.SaveAsync(true),files.Coordinator.Error);
        Assert.Equal(raw,TapeDocumentIO.Read(files.FilePath("copy.mzf")).Records[0].DescriptionRaw);Assert.Equal(source,File.ReadAllBytes(files.FilePath("original.mzf")));
    }
    [Fact]public async Task TextEdit_IsDraftUntilApplyAndSavePreservesHeaderPayloadTrailingAndSidecar()
    {
        using var files=new MediaDocumentTests();var raw=MzfDescriptionEditing.EncodeText("ORIGINAL");var source=FileBytes(raw);string input=files.FilePath("original.mzf");File.WriteAllBytes(input,source);
        byte[] side="# preserve comments and original line endings\r\nTYPE=IC\r\nSPEED=1:2\r\n"u8.ToArray();File.WriteAllBytes(files.FilePath("original.mfi"),side);
        var session=await files.Open();session.Description="EDITED";Assert.True(session.HasDraft);Assert.False(session.IsModified);Assert.Equal(raw,session.Model.Records[0].DescriptionRaw);
        session.ApplyMetadata();Assert.Empty(session.Validation);Assert.False(session.HasDraft);Assert.True(session.IsModified);
        byte[] expected=(byte[])source.Clone();Array.Clear(expected,24,104);"EDITED"u8.CopyTo(expected.AsSpan(24));
        Assert.Equal(expected,TapeDocumentWriter.SerializeMzf(session.Model.Records[0],true));Assert.Equal(source,File.ReadAllBytes(input));
        files.Dialogs.Save=new(files.FilePath("copy.mzf"),"Mzf",true);Assert.True(await files.Coordinator.SaveAsync(true),files.Coordinator.Error);
        Assert.Equal(expected,File.ReadAllBytes(files.FilePath("copy.mzf")));Assert.Equal(side,File.ReadAllBytes(files.FilePath("copy.mfi")));
        var reopened=TapeDocumentIO.Read(files.FilePath("copy.mzf")).Records[0];Assert.Equal("EDITED",MzfDescriptionEditing.DecodeText(reopened.DescriptionRaw));Assert.Equal(raw,source.AsSpan(24,104).ToArray());
        Assert.Equal(reopened.DescriptionRaw,reopened.Header.MzfHeaderDescription);Assert.Equal(reopened.DescriptionRaw,reopened.RawHeader.AsSpan(24,104).ToArray());
    }
    [Fact]public async Task InvalidTextAndCancel_DoNotMutateModelOrSource()
    {
        using var files=new MediaDocumentTests();var source=FileBytes();var session=await Open(files,source);
        foreach(string text in new[]{new string('X',105),"é","A\nB"})
        {session.Description=text;session.ApplyMetadata();Assert.NotEmpty(session.Validation);Assert.False(session.IsModified);Assert.Equal(source,TapeDocumentWriter.SerializeMzf(session.Model.Records[0],true));session.ResetMetadata();Assert.False(session.HasDraft);}
        session.Description="CANCEL";session.ResetMetadata();Assert.Equal(source,File.ReadAllBytes(session.Path!));Assert.False(session.IsModified);
    }
    [Fact]public async Task FilteringRetainsDescriptionDraftAndUnsupportedHostsCannotApplyIt()
    {
        using var files=new MediaDocumentTests();var session=await Open(files,FileBytes());session.Description="DRAFT";session.Filter="hidden";Assert.Equal("DRAFT",session.Description);Assert.True(session.HasDraft);session.Filter="";session.ResetMetadata();
        foreach(var format in new[]{TapeDocumentFormat.Mzt,TapeDocumentFormat.M12,TapeDocumentFormat.Qdf,TapeDocumentFormat.Mzq,TapeDocumentFormat.QdSharpLegacy})
        {session.Model.Format=format;Assert.False(session.CanEditDescription);session.Description="NO";session.ApplyMetadata();Assert.NotEmpty(session.Validation);Assert.False(session.IsModified);session.ResetMetadata();}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void EmbeddedZx7WarningDoesNotBlockExplicitEditingAndSurvivesClone(bool backward)
    {
        var original=Record(FileBytes(length:2048));var packed=MzfCompressionService.Compress(original,new(MzfCompressionAlgorithm.Zx7,backward?CompressionDirection.Backward:CompressionDirection.Forward,Zx7EmbeddedLoader:true),CompressionTarget.MzfTape,TestContext.Current.CancellationToken).Record;
        var before=TapeDocumentWriter.SerializeMzf(packed,true);var evidence=MzfDescriptionEditing.Inspect(packed);
        Assert.Equal(MzfDescriptionKind.ContainsRecognizedLoaderOrCode,evidence.Kind);Assert.True(evidence.Warn);Assert.Equal(24,evidence.Offset);Assert.Contains("Zx7",evidence.Warning);
        var edited=MzfDescriptionEditing.Replace(packed,MzfDescriptionEditing.EncodeText("MY TEXT"));Assert.Equal("MY TEXT",MzfDescriptionEditing.DecodeText(edited.DescriptionRaw));
        Assert.True(MzfDescriptionEditing.Inspect(edited.DeepClone()).Warn);Assert.Equal(before,TapeDocumentWriter.SerializeMzf(packed,true));
        edited.DescriptionEditRisk=null;Assert.False(MzfDescriptionEditing.Inspect(edited).Warn);
    }
    [Fact]public async Task WarningRemainsThroughApplyAndSelectionAndClearsOnlyAfterSuccessfulSave()
    {
        using var files=new MediaDocumentTests();var desc=Enumerable.Repeat((byte)0xF3,104).ToArray();var session=await Open(files,FileBytes(desc));Assert.True(session.HasDescriptionWarning);
        session.Description="SAFE TEXT";Assert.True(session.HasDescriptionWarning);session.ApplyMetadata();Assert.True(session.HasDescriptionWarning);
        session.SelectedRow=null;session.SelectedRow=session.Rows[0];Assert.True(session.HasDescriptionWarning);
        files.Dialogs.Save=new(files.FilePath("copy.mzf"),"Mzf",false);Assert.True(await files.Coordinator.SaveAsync(true),files.Coordinator.Error);Assert.False(session.HasDescriptionWarning);
    }
    [Fact]public void BinaryHeuristicIsReadOnlyAndExplainsItsUncertainty()
    {
        var bytes=FileBytes(Enumerable.Repeat((byte)0xff,104).ToArray());var record=Record(bytes);var hash=SHA256.HashData(bytes);var risk=MzfDescriptionEditing.Inspect(record);
        Assert.Equal(MzfDescriptionKind.SuspectedBinaryOrCode,risk.Kind);Assert.Contains("may contain",risk.Warning);Assert.Equal(hash,SHA256.HashData(TapeDocumentWriter.SerializeMzf(record,true)));
        var single=new byte[104];single[7]=0xff;Assert.Equal(MzfDescriptionKind.Unknown,MzfDescriptionEditing.Inspect(Record(FileBytes(single))).Kind);
    }
    [Fact]public void FullHexEditor_CanChangeHeaderDescriptionPayloadAndTrailingWithoutOtherDifferences()
    {
        var bytes=FileBytes();var record=Record(bytes);var changed=(byte[])bytes.Clone();changed[1]=(byte)'X';changed[20]=0x10;changed[24]=0xC3;changed[128]=0xF3;changed[^1]=0x76;
        var edited=MzfRecordEditing.ParseEdited(record,changed);Assert.Equal(changed,TapeDocumentWriter.SerializeMzf(edited,true));Assert.Equal(bytes,TapeDocumentWriter.SerializeMzf(record,true));
        Assert.Equal(0x4010,edited.Header.MzfStart);Assert.Equal((byte)0xC3,edited.DescriptionRaw[0]);Assert.Equal((byte)0xF3,edited.Body.MzfBody[0]);Assert.Equal((byte)0x76,edited.Body.TrailingData[^1]);
    }
    [Fact]public void HexLengthAndInvalidDeclaredPayloadAreRejectedAndReclassificationIsByteExact()
    {
        var bytes=FileBytes();var record=Record(bytes);Assert.Throws<InvalidDataException>(()=>MzfRecordEditing.ParseEdited(record,bytes[..^1]));
        var invalid=(byte[])bytes.Clone();invalid[18]=0xff;invalid[19]=0xff;Assert.Throws<EndOfStreamException>(()=>MzfRecordEditing.ParseEdited(record,invalid));
        var shorter=(byte[])bytes.Clone();shorter[18]=32;shorter[19]=0;var edited=MzfRecordEditing.ParseEdited(record,shorter);Assert.Equal(32,edited.Body.DataSize);Assert.Equal(shorter,TapeDocumentWriter.SerializeMzf(edited,true));
    }
    [Fact]public async Task StaleHexCannotOverwriteNewerPropertiesAndNoOpDoesNotDirty()
    {
        using var files=new MediaDocumentTests();var bytes=FileBytes();var session=await Open(files,bytes);var original=session.Model.Records[0];session.ApplyMzfBytes(original,bytes);Assert.False(session.IsModified);
        session.Description="NEWER";session.ApplyMetadata();var changed=(byte[])bytes.Clone();changed[128]=99;Assert.Throws<InvalidOperationException>(()=>session.ApplyMzfBytes(original,changed));Assert.Equal("NEWER",session.Description);
        session.Description="DRAFT";Assert.Throws<InvalidOperationException>(()=>session.ApplyMzfBytes(session.Model.Records[0],changed));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExternalMZFOrSidecarChangeBlocksSave(bool sidecar)
    {
        using var files=new MediaDocumentTests();var session=await Open(files,FileBytes());session.Description="TEXT";session.ApplyMetadata();
        string target=sidecar?files.FilePath("original.mfi"):session.Path!;File.WriteAllBytes(target,[9,8,7]);
        files.Dialogs.Save=new(files.FilePath("copy.mzf"),"Mzf",false);Assert.False(await files.Coordinator.SaveAsync(true));Assert.False(File.Exists(files.FilePath("copy.mzf")));Assert.Equal(new byte[]{9,8,7},File.ReadAllBytes(target));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void LegacyWriterAlsoProtectsSourceDuringSaveAs(bool sidecar)
    {
        using var files=new MediaDocumentTests();string path=files.FilePath("source.mzf");File.WriteAllBytes(path,FileBytes());var record=TapeDocumentIO.Read(path).Records[0];
        var edited=MzfDescriptionEditing.Replace(record,MzfDescriptionEditing.EncodeText("TEXT"));string changed=sidecar?files.FilePath("source.mfi"):path;File.WriteAllBytes(changed,[4,5,6]);
        Assert.Throws<IOException>(()=>TapeDocumentWriter.SaveMzf(files.FilePath("target.mzf"),edited,true));Assert.False(File.Exists(files.FilePath("target.mzf")));
    }
    [Theory][InlineData(2,false,false,false)][InlineData(2,true,false,false)][InlineData(2,false,true,false)][InlineData(2,true,true,false)]
    [InlineData(3,false,false,false)][InlineData(3,true,false,false)][InlineData(3,false,false,true)][InlineData(3,true,false,true)]
    public void CompressionTransformsRoundTripAndRetainOriginalSourceProtection(int algorithm,bool backward,bool quick,bool embedded)
    {
        using var files=new MediaDocumentTests();string path=files.FilePath("original.mzf");var bytes=FileBytes(length:2048);File.WriteAllBytes(path,bytes);
        var record=TapeDocumentIO.Read(path).Records[0];var packed=MzfCompressionService.Compress(record,new((MzfCompressionAlgorithm)algorithm,backward?CompressionDirection.Backward:CompressionDirection.Forward,Zx0Quick:quick,Zx7EmbeddedLoader:embedded),CompressionTarget.MzfTape,TestContext.Current.CancellationToken).Record;
        Assert.NotNull(packed.SourceFingerprints);var restored=MzfDecompressionService.Decompress(packed).Record;Assert.NotNull(restored.SourceFingerprints);
        Assert.Equal(bytes,TapeDocumentWriter.SerializeMzf(restored,true));File.WriteAllBytes(path,[9,8,7]);
        Assert.Throws<IOException>(()=>TapeDocumentWriter.SaveMzf(files.FilePath("copy.mzf"),restored,true));Assert.False(File.Exists(files.FilePath("copy.mzf")));
    }
    [Fact]public async Task ReadOnlySourceDisablesBothEditorsAndHexSaveReopensExactly()
    {
        using var files=new MediaDocumentTests();var source=FileBytes();string path=files.FilePath("original.mzf");File.WriteAllBytes(path,source);
        File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.ReadOnly);
        try
        {
            var readOnly=await files.Open();Assert.False(readOnly.CanEditDescription);Assert.False(readOnly.CanEditMzfBytes);
            Assert.Throws<InvalidOperationException>(()=>readOnly.ApplyMzfBytes(readOnly.Model.Records[0],source));
        }
        finally{File.SetAttributes(path,FileAttributes.Normal);}
        using var writableFiles=new MediaDocumentTests();var session=await Open(writableFiles,source);var changed=(byte[])source.Clone();changed[24]=0xC3;changed[128]=0xF3;changed[^1]=0x76;
        session.ApplyMzfBytes(session.Model.Records[0],changed);writableFiles.Dialogs.Save=new(writableFiles.FilePath("copy.mzf"),"Mzf",false);
        Assert.True(await writableFiles.Coordinator.SaveAsync(true),writableFiles.Coordinator.Error);
        Assert.Equal(changed,File.ReadAllBytes(writableFiles.FilePath("copy.mzf")));Assert.Equal(changed,TapeDocumentWriter.SerializeMzf(TapeDocumentIO.Read(writableFiles.FilePath("copy.mzf")).Records[0],true));
        Assert.Equal(source,File.ReadAllBytes(writableFiles.FilePath("original.mzf")));
    }
    [AvaloniaFact]public void HexTypingOverwritesTheHighlightedByteAndSynchronizesAllThreeColumns()
    {
        var source=FileBytes();var editor=new HexViewWindow(Record(source),_=>{});editor.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();editor.SelectOffset(24);Dispatcher.UIThread.RunJobs();
            Assert.Equal(24,editor.SelectedOffset);
            editor.KeyTextInput("C3");Dispatcher.UIThread.RunJobs();Assert.Equal(0xC3,editor.EditedBytes[24]);Assert.Equal(source[25],editor.EditedBytes[25]);
            editor.SelectOffset(128);editor.KeyTextInput("F3");Dispatcher.UIThread.RunJobs();Assert.Equal(0xF3,editor.EditedBytes[128]);
            Assert.Equal(source,TapeDocumentWriter.SerializeMzf(Record(source),true));editor.Undo();Assert.Equal(source[128],editor.EditedBytes[128]);
        }
        finally{editor.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task PropertiesExposeInlineWarningAndHexEditorUpdatesEveryRegionWithUndo()
    {
        using var files=new MediaDocumentTests();var bytes=FileBytes(Enumerable.Repeat((byte)0xff,104).ToArray());var session=await Open(files,bytes);
        var owner=new Window{Width=500,Height=600,Content=new RecordInspector{DataContext=new MZTools.Desktop.ViewModels.ShellViewModel(files.Coordinator,_=>Task.CompletedTask,files.Dialogs)}};owner.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();var inspector=owner.GetVisualDescendants().OfType<RecordInspector>().Single();var field=inspector.FindControl<TextBox>("RecordDescription")!;
            Assert.False(field.IsReadOnly);Assert.Contains(inspector.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text==session.DescriptionWarning&&t.IsVisible);
            var original=session.Model.Records[0];var editor=new HexViewWindow(original,data=>session.ApplyMzfBytes(original,data),24);editor.Show(owner);Dispatcher.UIThread.RunJobs();
            try
            {
                var changed=(byte[])bytes.Clone();changed[24]=0xC3;changed[128]=0x76;changed[^1]=0xF3;editor.SetBytes(changed);Assert.Equal(bytes,TapeDocumentWriter.SerializeMzf(original,true));
                editor.Undo();Assert.Equal(bytes,editor.EditedBytes);editor.SetBytes(changed);
                if(Environment.GetEnvironmentVariable("MZTOOLS_DESCRIPTION_EVIDENCE") is {} output)
                {
                    Directory.CreateDirectory(output);editor.UpdateLayout();using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1130,640),new Vector(96,96));bitmap.Render(editor);bitmap.Save(Path.Combine(output,"avalonia-mzf-full-hex.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                editor.Apply();Assert.True(editor.Applied);Assert.Equal(changed,TapeDocumentWriter.SerializeMzf(session.Model.Records[0],true));
                Assert.True(session.IsModified);Assert.Equal(bytes,File.ReadAllBytes(session.Path!));Assert.True(session.HasDescriptionWarning);
            }finally{editor.Close();}
        }finally{owner.Close();Dispatcher.UIThread.RunJobs();}
    }
}
