using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MZTools;

internal static class Program
{
    static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var descendant in Visuals(child))yield return descendant;}}
    static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    [STAThread]static int Main(string[] args)
    {
        if(args.Length!=1)return 2;
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);int exit=1;var frames=new List<object>();
        var app=new MZTools.App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        async Task Capture(Window window,string name)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(window);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,name)))encoder.Save(stream);
            frames.Add(new{file=name,logicalWidth=window.ActualWidth,logicalHeight=window.ActualHeight,scale=dpi.DpiScaleX,origin="Unmodified render of the retained WPF frontend"});
        }
        app.Startup+=async(_,_)=>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var main=(MainWindow)app.MainWindow;await Capture(main,"wpf-original-empty.png");
                byte[] bytes=new byte[640];bytes[0]=1;System.Text.Encoding.ASCII.GetBytes("RENAMED").CopyTo(bytes,1);bytes[17]=13;BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),512);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);for(int i=24;i<128;i++)bytes[i]=(byte)i;for(int i=128;i<bytes.Length;i++)bytes[i]=(byte)(i%7);
                string path=Path.Combine(output,"reference.mzf");File.WriteAllBytes(path,bytes);var document=Field<TapeDocument>(main,"document");document.Records.Add(new MZTFileReader().ReadStandaloneMzf(path));document.Format=TapeDocumentFormat.Mzf;document.FilePath=path;document.IsModified=false;Call(main,"RefreshGrid");await Capture(main,"wpf-original-tape.png");
                var batchReference=new BatchProcessDialog(main);batchReference.Show();await Capture(batchReference,"wpf-original-batch-empty.png");
                var operationBox=Visuals(batchReference).OfType<System.Windows.Controls.ComboBox>().Single(c=>c.Name=="BatchOperation");
                foreach(var choice in operationBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()){operationBox.SelectedItem=choice;await Capture(batchReference,"wpf-original-batch-operation-"+choice.Tag+".png");}
                var stateMatrix=new List<object>();var targetBox=Visuals(batchReference).OfType<System.Windows.Controls.ComboBox>().Single(c=>c.Name=="ConversionTarget");var extractBox=Visuals(batchReference).OfType<System.Windows.Controls.ComboBox>().Single(c=>c.Name=="ExtractFormat");
                FrameworkElement Parameter(string name)=>name switch{"BootSource"=>Visuals(batchReference).OfType<System.Windows.Controls.TextBox>().Single(c=>c.Parent is System.Windows.Controls.Grid g&&g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t=>t.Text=="Boot source / profile")),"OutputFolder"=>Visuals(batchReference).OfType<System.Windows.Controls.TextBox>().Single(c=>c.Parent is System.Windows.Controls.Grid g&&g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t=>t.Text=="Output folder")),_=>Visuals(batchReference).OfType<FrameworkElement>().Single(c=>c.Name==name)};
                var matrixControls=new[]{"ConversionTarget","ExtractFormat","AudioSampleRate","UseHeuristicAnalysis","BatchCompression","BootSource","OutputFolder","SelectedFilesMode","InputFolderMode"}.ToDictionary(name=>name,Parameter);
                foreach(var choice in operationBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())foreach(string mode in new[]{"SelectedFilesMode","InputFolderMode"})foreach(string targetValue in targetBox.Items.Cast<string>().Distinct())foreach(string extractValue in extractBox.Items.Cast<string>())
                {
                    operationBox.SelectedItem=choice;((System.Windows.Controls.RadioButton)matrixControls[mode]).IsChecked=true;targetBox.SelectedItem=targetValue;extractBox.SelectedItem=extractValue;
                    stateMatrix.Add(new{operation=choice.Tag.ToString(),mode,target=targetValue,extract=extractValue,controls=new[]{"ConversionTarget","ExtractFormat","AudioSampleRate","UseHeuristicAnalysis","BatchCompression","BootSource","OutputFolder"}.Select(name=>{var c=matrixControls[name];return new{name,enabled=c.IsEnabled,visible=c.IsVisible};}).ToArray()});
                }
                File.WriteAllText(Path.Combine(output,"wpf-batch-state-matrix.json"),JsonSerializer.Serialize(stateMatrix));
                batchReference.Close();
                var batchRow=BatchProcessService.Preview([path],new(BatchOperation.TestIntegrity,output)).Rows[0];var batchDetails=new BatchFileDetailsDialog(main,batchRow);batchDetails.Show();await Capture(batchDetails,"wpf-original-batch-details.png");var batchTabs=Visuals(batchDetails).OfType<System.Windows.Controls.TabControl>().Single(t=>t.Name=="BatchDetailTabs");for(int i=0;i<5;i++){batchTabs.SelectedIndex=i;await Capture(batchDetails,"wpf-original-batch-details-"+i+".png");}batchDetails.Close();
                var colorBytes=new byte[164];Array.Copy(bytes,colorBytes,128);Array.Clear(colorBytes,1,16);System.Text.Encoding.ASCII.GetBytes("COLORED").CopyTo(colorBytes,1);BinaryPrimitives.WriteUInt16LittleEndian(colorBytes.AsSpan(18),33);for(int i=128;i<161;i++)colorBytes[i]=(byte)(i%7);colorBytes[161]=0x91;colorBytes[162]=0x80;colorBytes[163]=0x0D;string colorPath=Path.Combine(output,"hex-colors.mzf");File.WriteAllBytes(colorPath,colorBytes);
                var colorRecord=new MZTFileReader().ReadStandaloneMzf(colorPath);var hexReference=new HexBrowser();hexReference.ShowHexDump(colorRecord,true);hexReference.Show();await Capture(hexReference,"wpf-original-hex-viewer.png");hexReference.Close();
                document.Records.Clear();document.Format=TapeDocumentFormat.QdFlashFloppy;document.QuickDiskProfile=QuickDiskPhysicalProfile.FlashFloppy;document.IsModified=true;document.FilePath=null;Call(main,"RefreshGrid");Field<System.Windows.Controls.TabControl>(main,"tapeViews").SelectedIndex=1;
                await Capture(main,"wpf-original-quickdisk-map.png");
                var qdVisual=new QuickDiskVisualInspectorWindow(main,QuickDiskVisualSnapshot.Capture(document),_=>{});qdVisual.Show();await Capture(qdVisual,"wpf-original-quickdisk-visual.png");qdVisual.Close();
                string wave=Path.Combine(output,"reference-tone.wav");const int toneFrames=8229;using(var writer=new BinaryWriter(File.Create(wave)))
                {
                    writer.Write("RIFF"u8);writer.Write(36+toneFrames*4);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(toneFrames*4);
                    for(int i=0;i<toneFrames;i++)for(int c=0;c<2;c++)writer.Write((short)Math.Round(16384*Math.Sin(2*Math.PI*(c==0?1500:3000)*i/48000)));
                }
                var audio=new AudioSignalAnalyzerWindow(main);audio.Show();await audio.Analyze(wave);Field<AudioSignalChart>(audio,"waveform").ShowRange(100,400);await Task.Delay(400);await Capture(audio,"wpf-original-audio-waveform.png");
                string flac=Path.Combine(output,"reference-tone.flac");CorePlatformServices.EncodeFlac(wave,flac,CancellationToken.None);await audio.Analyze(flac);Field<AudioSignalChart>(audio,"waveform").ShowRange(100,400);await Task.Delay(400);await Capture(audio,"wpf-original-audio-flac.png");
                var recovery=new AudioRegionRecoveryWindow(main,Field<AudioSignalAnalysis>(audio,"analysis"),111,999);recovery.Show();await Task.Delay(400);await Capture(recovery,"wpf-original-selected-analysis.png");recovery.Close();audio.Close();
                var disk=DskDocumentFactory.CreateFsmz(false);disk.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);disk.MarkModified();
                var editor=new DskEditorControl();var diskWindow=new Window{Title="WPF reference · DSK",Width=1320,Height=600,Content=editor};diskWindow.Show();editor.LoadDocument(disk);await Capture(diskWindow,"wpf-original-disk.png");
                Field<System.Windows.Controls.TabControl>(editor,"dskViews").SelectedIndex=1;await Capture(diskWindow,"wpf-original-dsk-map.png");
                var dskVisual=new DskVisualInspectorWindow(diskWindow,new DskVisualSnapshot(disk.Image,DskAnalyzer.Analyze(disk)),(_,_)=>true,_=>{},_=>{},(_,_)=>true);dskVisual.Show();await Capture(dskVisual,"wpf-original-dsk-surface.png");
                Field<System.Windows.Controls.CheckBox>(dskVisual,"autoFloating").IsChecked=false;
                var issueDisk=disk.Clone();issueDisk.Image.Tracks[0]!.Sectors[1].FdcStatus1=0x20;issueDisk.MarkModified();dskVisual.SetSnapshot(new DskVisualSnapshot(issueDisk.Image,DskAnalyzer.Analyze(issueDisk)));dskVisual.Request(0,new DskSectorAddress(0,1),false);
                foreach(var view in new[]{("Track","track"),("Issues","issues"),("Statistics","statistics"),("Tracks","tracks")}){dskVisual.SetVerificationDetails(ImageVerificationService.Hash(issueDisk.SerializeContainer()),ContainerNormalizeService.Candidate(issueDisk).AsSpan().SequenceEqual(issueDisk.SerializeContainer()));dskVisual.ShowView(view.Item1);await Capture(dskVisual,"wpf-original-dsk-"+view.Item2+".png");}
                dskVisual.SetSnapshot(new DskVisualSnapshot(disk.Image,DskAnalyzer.Analyze(disk)));dskVisual.Request(0,new DskSectorAddress(0,0),false);dskVisual.ShowView("Surface");Field<System.Windows.Controls.CheckBox>(dskVisual,"showMfm").IsChecked=true;await Task.Delay(500);var mfm=Field<DskMfmDetailControl>(dskVisual,"mfmDetail");if(mfm.Track==null)throw new InvalidOperationException("WPF MFM reference did not generate.");mfm.FitSector(0);mfm.ZoomBy(80);await Capture(dskVisual,"wpf-original-dsk-mfm.png");dskVisual.Close();
                int hardwareRequests=0;var recorder=new AudioRecorderWindow(null,_=>{hardwareRequests++;throw new InvalidOperationException("Physical input forbidden in reference smoke.");},true,()=>true,()=>false,_=>false);recorder.Show();await Capture(recorder,"wpf-original-recorder.png");
                recorder.Monitor.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Task.Delay(300);await Capture(recorder,"wpf-original-recorder-live.png");await recorder.Model.StopAsync();
                if(hardwareRequests!=0)throw new InvalidOperationException("Reference rendering requested hardware.");
                recorder.Close();diskWindow.Close();document.IsModified=false;main.Close();exit=0;
            }
            catch(Exception e){Console.Error.WriteLine(e);}
            finally{File.WriteAllText(Path.Combine(output,"wpf-reference.json"),JsonSerializer.Serialize(new{exit,frames,hardwareTested=false},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        };
        app.Run();Console.WriteLine("WPF visual reference exit="+exit);return exit;
    }
}
