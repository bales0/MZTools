using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class Sha256AndParityTests
{
    [Fact] public void StrictSegmentOpenCannotReuseMetadataAfterByteChange()
    {
        using var files=new AudioDocumentTests();string manifest=files.Manifest();
        using(var first=new SegmentedPcmStreamReader(manifest))Assert.True(first.Format.FrameCount>0);
        string path=files.FilePath("joined_002.wav");var stamp=File.GetLastWriteTimeUtc(path);byte[] bytes=File.ReadAllBytes(path);bytes[100]^=0x40;File.WriteAllBytes(path,bytes);File.SetLastWriteTimeUtc(path,stamp);
        Assert.Throws<InvalidDataException>(()=>new SegmentedPcmStreamReader(manifest));
    }
    [Fact] public async Task MetadataPreviewAndCryptographicExportHaveExplicitDifferentGuarantees()
    {
        using var files=new AudioDocumentTests();var s=await files.Open(files.Manifest());string path=files.FilePath("joined_002.wav");
        var stamp=File.GetLastWriteTimeUtc(path);using(var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite)){stream.Position=100;int value=stream.ReadByte();stream.Position=100;stream.WriteByte((byte)(value^0x40));}File.SetLastWriteTimeUtc(path,stamp);
        // Same-size in-place modification with restored mtime is outside metadata-only preview's guarantee.
        AudioRangeInspection.VerifySource(s.Model);
        Assert.Throws<IOException>(s.VerifySources);
        await Assert.ThrowsAsync<IOException>(()=>AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken));
    }
    [Fact] public async Task SegmentMetadataChangeIsRejectedByPreview()
    {
        using var files=new AudioDocumentTests();var s=await files.Open(files.Manifest());string path=files.FilePath("joined_002.wav");
        File.SetLastWriteTimeUtc(path,File.GetLastWriteTimeUtc(path).AddSeconds(10));Assert.Throws<IOException>(()=>AudioRangeInspection.VerifySource(s.Model));
    }
    [Fact] public async Task ChangedSourceAfterStagingCannotPublishDestination()
    {
        using var files=new AudioDocumentTests();var s=await files.Open();using var stage=await AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken);
        string target=files.FilePath("export.wav");byte[] original=[1,2,3];File.WriteAllBytes(target,original);File.AppendAllText(s.Path!,"changed");
        Assert.False(await files.Coordinator.ExecuteAsync(async ct=>{await files.Coordinator.WriteExportAsync(new Dictionary<string,byte[]>{[target]=[]},ct,new Dictionary<string,string>{[target]=stage.Path},s.VerifySources);return true;}));
        Assert.Equal(original,File.ReadAllBytes(target));Assert.Empty(Directory.GetFiles(files.Root,".mztools-*.tmp"));
    }
    [Fact] public void StreamingHashMatchesShaAndHonorsCancellation()
    {
        using var files=new AudioDocumentTests();string path=files.Wave();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),AtomicFileTransaction.HashCancellable(path,TestContext.Current.CancellationToken));
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>AtomicFileTransaction.HashCancellable(path,cancel.Token));
    }
    private sealed class CallbackProgress(Action<WavAnalysisProgress> callback):IProgress<WavAnalysisProgress>
    {public void Report(WavAnalysisProgress value)=>callback(value);}
    [Fact] public async Task SegmentChangeBetweenAnalysisAndFinalVerificationRejectsOpen()
    {
        using var files=new AudioDocumentTests();string manifest=files.Manifest();bool changed=false;
        var progress=new CallbackProgress(p=>{if(p.Stage=="Verifying source after analysis"){File.AppendAllText(files.FilePath("joined_002.wav"),"changed");changed=true;}});
        await Assert.ThrowsAsync<IOException>(()=>AudioSession.LoadAsync(manifest,progress,TestContext.Current.CancellationToken));Assert.True(changed);
    }
    [Fact] public async Task ConcurrentWriterCannotProduceAPublishedWave()
    {
        using var files=new AudioDocumentTests();var session=await files.Open();byte[] original=File.ReadAllBytes(session.Path!);bool attempted=false;
        var progress=new CallbackProgress(p=>{if(p.Stage=="Export selection to WAV"&&!attempted){attempted=true;File.AppendAllText(session.Path!,"changed");}});
        await Assert.ThrowsAsync<IOException>(()=>AudioOperations.StageWaveAsync(session,progress,TestContext.Current.CancellationToken));Assert.True(attempted);
        if(OperatingSystem.IsWindows())Assert.Equal(original,File.ReadAllBytes(session.Path!)); // FileShare.Read denies the concurrent writer
    }
    [Fact] public async Task MissingSourceIsRejectedInsteadOfUsingStalePreviewForExport()
    {
        using var files=new AudioDocumentTests();var session=await files.Open();File.Delete(session.Path!);Assert.Throws<FileNotFoundException>(()=>AudioRangeInspection.VerifySource(session.Model));
        await Assert.ThrowsAsync<IOException>(()=>AudioOperations.StageWaveAsync(session,null,TestContext.Current.CancellationToken));
    }
    [Fact] public async Task CancellationDuringLargeStreamingHashStopsTheRead()
    {
        using var files=new AudioDocumentTests();string path=files.FilePath("cancel-hash.bin");using(var file=File.Create(path))file.SetLength(256L*1024*1024);
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);cancel.CancelAfter(TimeSpan.FromMilliseconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Task.Run(()=>AtomicFileTransaction.HashCancellable(path,cancel.Token),TestContext.Current.CancellationToken));
    }
    [Theory][InlineData(1,false)][InlineData(2,false)][InlineData(1,true)][InlineData(2,true)]
    public async Task WaveAndFlac24BitExportPreserveEveryPcmByte(int channels,bool flac)
    {
        using var files=new AudioDocumentTests();string wave=files.FilePath("pcm24.wav");int[] values=[-8388608,-1,0,1,8388607];const int frames=128;
        using(var writer=new BinaryWriter(File.Create(wave)))
        {int align=channels*3;writer.Write("RIFF"u8);writer.Write(36+frames*align);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)channels);writer.Write(48000);writer.Write(48000*align);writer.Write((short)align);writer.Write((short)24);writer.Write("data"u8);writer.Write(frames*align);for(int i=0;i<frames;i++)for(int ch=0;ch<channels;ch++){int value=values[(i+ch)%values.Length];writer.Write((byte)value);writer.Write((byte)(value>>8));writer.Write((byte)(value>>16));}}
        string path=wave;if(flac){MZTools.Platform.Audio.NativeAudioCodecs.Configure();path=files.FilePath("pcm24.flac");CorePlatformServices.EncodeFlac(wave,path,TestContext.Current.CancellationToken);}
        var session=await files.Open(path);using var stage=await AudioOperations.StageWaveAsync(session,null,TestContext.Current.CancellationToken,"Stereo");Assert.Equal(File.ReadAllBytes(wave),File.ReadAllBytes(stage.Path));
    }
    [Fact] public async Task MatchingRecoveryOverlaysSurviveRefreshAndNeverAttachToChangedPcm()
    {
        using var files=new AudioDocumentTests();var mono=await files.TapeWave();string stereo=files.FilePath("stereo-tape.wav");
        using(var pcm=AudioSignalAnalysisService.OpenReader(mono.Path!,cancellation:TestContext.Current.CancellationToken))
        using(var writer=new BinaryWriter(File.Create(stereo)))
        {
            var f=pcm.Format;int data=checked((int)f.FrameCount*4);writer.Write("RIFF"u8);writer.Write(36+data);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(f.SampleRate);writer.Write(f.SampleRate*4);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(data);pcm.ReadFrames((_,left,_)=>{writer.Write((short)(left>>8));writer.Write((short)(left>>8));});
        }
        var parent=await files.Open(stereo);using var s=parent.CreateSelectedPreview(0,parent.Model.FrameCount);
        Assert.True(await s.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));
        Assert.True(await s.RecoverAsync(new(){ZeroCrossing=true,Schmitt=false,ThresholdScales=[1]},null,null,TestContext.Current.CancellationToken));
        var measurement=s.Recovery!.Passes.SelectMany(p=>p.Measurements).First();s.UseSelectedRecoveryMeasurement(measurement);
        var expected=s.DisplayRegions.ToArray();Assert.NotEmpty(expected);Assert.True(s.SelectedRecoveryMatchesPreview);
        Assert.True(await s.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));Assert.Equal(expected,s.DisplayRegions);
        s.RightShift="1";Assert.True(await s.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));Assert.False(s.SelectedRecoveryMatchesPreview);
        s.UseSelectedRecoveryMeasurement(measurement);Assert.DoesNotContain(s.DisplayRegions,r=>r.Evidence.Contains("block candidate"));
        s.RightShift="0";Assert.True(await s.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));Assert.Equal(expected,s.DisplayRegions);
        s.SetPreviewBounds(1,s.Model.FrameCount);Assert.False(s.SelectedRecoveryMatchesPreview);Assert.DoesNotContain(s.DisplayRegions,r=>r.Evidence.Contains("block candidate"));
    }
    [Fact] public async Task ActualChecksumFailureSurvivesMatchingPreviewRefresh()
    {
        using var files=new AudioDocumentTests();string path=files.FilePath("bad-checksum.wav");var pcm=new List<short>();
        void Pulse(bool longer){for(int i=0;i<(longer?20:10);i++)pcm.Add(-16000);for(int i=0;i<(longer?20:10);i++)pcm.Add(16000);}
        void Byte(byte value){for(int bit=7;bit>=0;bit--)Pulse((value&(1<<bit))!=0);Pulse(true);}
        for(int i=0;i<64;i++)Pulse(false);for(int i=0;i<16;i++)Pulse(true);for(int i=0;i<16;i++)Pulse(false);Pulse(true);Pulse(true);
        for(int i=0;i<32;i++)Byte(0xAA);Byte(0);Byte(129); // data has 128 set bits: checksum 129 is deliberately wrong
        pcm.AddRange(new short[4800]);
        using(var writer=new BinaryWriter(File.Create(path)))
        {writer.Write("RIFF"u8);writer.Write(36+pcm.Count*4);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(pcm.Count*4);foreach(short value in pcm){writer.Write(value);writer.Write(value);}}
        var parent=await files.Open(path);using var selected=parent.CreateSelectedPreview(0,parent.Model.FrameCount);
        Assert.True(await selected.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));
        Assert.True(await selected.RecoverAsync(new(){PayloadOnly=true,ZeroCrossing=true,Schmitt=false,Normal=true,Inverted=false,ThresholdScales=[1]},32,null,TestContext.Current.CancellationToken));
        Assert.Contains(selected.Recovery!.Candidates,c=>c.ChecksumAvailable&&!c.ChecksumValid);
        selected.UseSelectedRecoveryMeasurement(selected.Recovery.Passes.SelectMany(p=>p.Measurements).FirstOrDefault());
        Assert.Contains(selected.PreviewIssues,i=>i.Code=="AUDIO_CHECKSUM_FAILURE");
        Assert.True(await selected.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));Assert.Contains(selected.PreviewIssues,i=>i.Code=="AUDIO_CHECKSUM_FAILURE");
        selected.InvertRight=true;Assert.True(await selected.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));Assert.DoesNotContain(selected.PreviewIssues,i=>i.Code=="AUDIO_CHECKSUM_FAILURE");
    }
    [Theory][InlineData("MzBasic",40,320)][InlineData("IplDisk",40,320)][InlineData("PersonalCpm",40,320)][InlineData("Sds400",40,400)][InlineData("LecCpmDd",80,720)][InlineData("LecCpmHd",80,1440)][InlineData("Mrs",80,720)][InlineData("Lemmings",80,720)]
    public void ProfileDefaultsCreateExpectedCapacity(string name,int tracks,int kib)
    {
        var profile=Enum.Parse<DskNewFormat>(name);Assert.Equal(tracks,DiskFormatForm.DefaultTracks(profile));Assert.Equal(kib*1024L,DiskFormatForm.Capacity(profile,tracks,2));
        var disk=DiskFormatService.Create(new(profile,tracks,2));Assert.Equal(tracks*2,disk.Image.Tracks.Count(t=>t is not null)); // native boot tracks may store fewer bytes than nominal geometry
        Assert.Equal(disk.SerializeContainer(),DskDocument.Open(disk.SerializeContainer()).SerializeContainer());
    }
    [AvaloniaFact] public void ChangingProfileUpdatesGeometryAndShowsCustomFields()
    {
        var values=new Dictionary<string,Control>{["format"]=new ComboBox{ItemsSource=Enum.GetNames<DskNewFormat>(),SelectedItem="MzBasic"}};
        foreach(string key in new[]{"tracks","sides","sectors","size","filler","order","ids"})values[key]=new TextBox{Text=key=="sectors"?"16":key=="size"?"256":""};
        values["order"]=new ComboBox{ItemsSource=Enum.GetNames<DskDocumentFactory.RawSectorOrder>(),SelectedItem="Normal"};
        var rows=values.ToDictionary(p=>p.Key,p=>(Control)new StackPanel());var controls=new List<Control>();DiskFormatForm.Attach(values,rows,controls);
        ((ComboBox)values["format"]).SelectedItem="LecCpmDd";Assert.Equal("80",((TextBox)values["tracks"]).Text);Assert.Contains("720",((TextBlock)controls[0]).Text);Assert.False(rows["size"].IsVisible);
        ((ComboBox)values["format"]).SelectedItem="PersonalCpm";Assert.False(values["tracks"].IsEnabled);Assert.Equal("40",((TextBox)values["tracks"]).Text);
        ((ComboBox)values["format"]).SelectedItem="CustomRaw";Assert.True(values["tracks"].IsEnabled);Assert.True(rows["size"].IsVisible);
    }
    [AvaloniaFact] public async Task ActualDiskDialogUsesProfileGeometryAndCancelPreservesDocuments()
    {
        var owner=new Window{Width=900,Height=700};owner.Show();var dialogs=new DesktopDialogs(owner);
        var coordinator=new DocumentCoordinator(new MediaDocumentLoader(dialogs),dialogs,new());var shell=new ShellViewModel(coordinator,_=>Task.CompletedTask,dialogs);
        try
        {
            shell.NewDiskCommand.Execute(null);Dispatcher.UIThread.RunJobs();var dialog=Assert.Single(owner.OwnedWindows);
            var profile=dialog.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="Filesystem/profile");profile.SelectedItem="LecCpmHd";Dispatcher.UIThread.RunJobs();
            Assert.Equal("80",dialog.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Cylinders (profile-dependent)").Text);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("1440")==true);
            Capture(dialog,"disk-format-lec-hd.png");
            dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle(coordinator);Assert.Empty(coordinator.Documents);
            shell.NewDiskCommand.Execute(null);Dispatcher.UIThread.RunJobs();dialog=Assert.Single(owner.OwnedWindows);
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="Filesystem/profile").SelectedItem="Mrs";
            dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Continue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle(coordinator);
            var disk=Assert.IsType<DiskSession>(coordinator.Active);Assert.Equal(160,disk.Model.Image.Tracks.Count(t=>t is not null));Assert.Null(coordinator.Error);
        }finally{foreach(var dialog in owner.OwnedWindows.ToArray())dialog.Close();owner.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact] public async Task HexBrowserRendersDerivedQdfHeaderAndCompleteRecord()
    {
        using var files=new MediaDocumentTests();File.WriteAllBytes(files.FilePath("hex-parity.mzf"),MediaDocumentTests.Mzf("SHASCII",32).Concat(new byte[]{0x91,0x80,0x0D}).ToArray());var tape=await files.Open("hex-parity.mzf");var window=new HexViewWindow(tape.Rows[0]);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();Capture(window,"hex-record.png");Assert.Empty(window.GetVisualDescendants().OfType<TabControl>());
            var table=window.GetVisualDescendants().OfType<TableView>().Single();var rows=table.ItemsSource!.Cast<HexViewWindow.Row>().Where(r=>r.IsQdf).ToArray();Assert.Equal(70,rows.Sum(r=>r.Count));Assert.Equal(0,rows[0].Offset);Assert.Equal(64,rows[^1].Offset);Assert.True(SharpQdFrameCodec.HasValidCrc(rows[0].Bytes));
        }finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    private static async Task Idle(DocumentCoordinator coordinator)
    {for(int i=0;i<3000&&coordinator.IsBusy;i++){Dispatcher.UIThread.RunJobs();await Task.Delay(10,TestContext.Current.CancellationToken);}Assert.False(coordinator.IsBusy);Dispatcher.UIThread.RunJobs();}
    private static void Capture(Window window,string name)
    {
        string output=Environment.GetEnvironmentVariable("MZTOOLS_SHA256_UI_OUTPUT")??Path.Combine(Path.GetTempPath(),"mztools-sha256-parity-ui");Directory.CreateDirectory(output);window.UpdateLayout();
        using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
    }
    [Fact] public async Task RenameCancelAndInvalidNamePreserveOriginalThenValidRenameApplies()
    {
        using var files=new DiskDocumentTests();var disk=DskDocumentFactory.CreateFsmz(false);disk.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);disk.MarkModified();var s=await files.Open(disk);s.SelectedFile=s.Files.Single();byte[] original=s.SerializeContainer();
        Assert.True(await files.Shell.RenameDiskFileAsync(s,"FIRST"));Assert.False(s.IsModified);
        files.Dialogs.Accept=false;Assert.False(await files.Shell.RenameDiskFileAsync(s,"SECOND"));Assert.Equal(original,s.SerializeContainer());Assert.False(s.HasDraft);
        files.Dialogs.Accept=true;Assert.False(await files.Shell.RenameDiskFileAsync(s,""));Assert.Equal(original,s.SerializeContainer());Assert.False(s.HasDraft);
        Assert.True(await files.Shell.RenameDiskFileAsync(s,"SECOND"));Assert.Equal("SECOND",s.Files.Single().Name);Assert.Equal(original,File.ReadAllBytes(s.Path!));
    }
    [AvaloniaFact] public async Task F2EditsTheSelectedNameAndEscapeRestoresIt()
    {
        using var files=new DiskDocumentTests();var image=DskDocumentFactory.CreateFsmz(false);image.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);image.MarkModified();var disk=await files.Open(image);var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="DiskFiles");table.SelectedItem=disk.Files.Single();Dispatcher.UIThread.RunJobs();Assert.True(table.ContainerFromIndex(0)!.Focus());
            window.KeyPress(Key.F2,RawInputModifiers.None,PhysicalKey.F2,null);window.KeyRelease(Key.F2,RawInputModifiers.None,PhysicalKey.F2,null);Dispatcher.UIThread.RunJobs();
            var input=window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="DiskInlineRename");input.Text="CANCELLED";
            window.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null);window.KeyRelease(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null);Dispatcher.UIThread.RunJobs();Assert.Equal("FIRST",disk.Files.Single().Name);Assert.False(disk.HasDraft);
            table.ContainerFromIndex(0)!.Focus();window.KeyPress(Key.F2,RawInputModifiers.None,PhysicalKey.F2,null);window.KeyRelease(Key.F2,RawInputModifiers.None,PhysicalKey.F2,null);Dispatcher.UIThread.RunJobs();
            input=window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="DiskInlineRename");input.Text="SECOND";Capture(window,"disk-f2-inline.png");
            window.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);window.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);await Idle(files.Coordinator);Assert.Equal("SECOND",disk.Files.Single().Name);
        }finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact] public void BatchParametersFollowOperationAndInputMode()
    {
        var values=new Dictionary<string,Control>();
        foreach(var(key,choices,selected)in new[]{("operation",Enum.GetNames<BatchOperation>(),"Analyze"),("input",new[]{"Choose files","Scan folder"},"Choose files"),("target",new[]{"DSK","Wav"},"DSK"),("replace",new[]{"No","Yes"},"No")})values[key]=new ComboBox{ItemsSource=choices,SelectedItem=selected};
        var rows=new[]{"folder","recursive","extensions","output","template","replace","backup","target","auxiliary","extract","heuristic","rate"}.ToDictionary(k=>k,k=>(Control)new StackPanel());
        BatchProcessForm.Attach(values,rows);Assert.False(rows["folder"].IsVisible);Assert.False(rows["target"].IsVisible);Assert.False(rows["output"].IsVisible);
        ((ComboBox)values["operation"]).SelectedItem="ConvertFormat";((ComboBox)values["target"]).SelectedItem="Wav";((ComboBox)values["replace"]).SelectedItem="Yes";Assert.True(rows["target"].IsVisible);Assert.True(rows["rate"].IsVisible);Assert.True(rows["backup"].IsVisible);
        ((ComboBox)values["input"]).SelectedItem="Scan folder";Assert.True(rows["recursive"].IsVisible);
        ((ComboBox)values["operation"]).SelectedItem="ExtractAll";Assert.True(rows["extract"].IsVisible);Assert.False(rows["replace"].IsVisible);Assert.Equal("No",((ComboBox)values["replace"]).SelectedItem);
    }
    [Fact] public void HexBrowserRetainsEveryTrailingByteAndSharedShascii()
    {
        using var stream=new MemoryStream(MediaDocumentTests.Mzf("HEX",32));using var reader=new BinaryReader(stream);var record=new MZTFileReader().ReadMzfRecord(reader);
        var rows=HexViewWindow.RecordRows(record);Assert.Equal(record.GetSerializedHeader().Concat(record.Body.MzfBody).Concat(record.Body.TrailingData??[]),rows.SelectMany(r=>r.Bytes.AsSpan(r.Offset,r.Count).ToArray()));
        var row=new HexViewWindow.Row([0x91,0x80,0x0d],0,3,"Raw");Assert.Equal(" }.",row.Shascii);
    }
}
