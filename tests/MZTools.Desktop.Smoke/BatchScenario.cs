using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Threading;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Smoke;

internal static class BatchScenario
{
    internal static async Task Run(string output)
    {
        var checks=new List<object>();var frames=new List<object>();var states=new List<object>();var matrix=new List<object>();string? fixtureSha256=null;var window=new BatchProcessWindow();window.Show();
        string directory=Path.Combine(Path.GetTempPath(),"mztools-native-batch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        void Check(bool value,string name){checks.Add(new{name,pass=value});if(!value)throw new InvalidOperationException(name);}
        async Task Capture(Window target,string name)
        {
            await Task.Delay(80);target.UpdateLayout();double scale=target.RenderScaling;using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(target.Bounds.Width*scale),(int)Math.Ceiling(target.Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(target);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);frames.Add(new{file=name,width=target.Bounds.Width,height=target.Bounds.Height,scale,origin="Unmodified Skia render of a native window"});
        }
        async Task CancelPicker(Button button,string name)
        {
            Check(button.IsEffectivelyEnabled,name+" is reachable");bool seen=false;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
            timer.Tick+=(_,_)=>{foreach(var dialog in window.OwnedWindows.ToArray()){seen=true;dialog.Close();}if(OperatingSystem.IsWindows()&&NativePicker.CancelOwnedDialog(Environment.ProcessId))seen=true;};
            timer.Start();try{button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));for(int i=0;i<300&&!seen;i++)await Task.Delay(50);Check(seen,name+" opens a real picker and cancels");await Task.Delay(100);}finally{timer.Stop();}
        }
        Button Browse(string label)=>window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Browse...")&&b.Parent is Grid row&&row.Children.OfType<TextBlock>().Any(t=>t.Text==label));
        try
        {
            await Task.Delay(100);Check(window.OwnedWindows.Count()==0&&window.Operation.ToString()=="TestIntegrity","Batch opens without a modal form and defaults to Info / test integrity");await Capture(window,"native-batch-empty.png");
            var operation=Find<ComboBox>("BatchOperation");foreach(var choice in operation.Items.Cast<BatchProcessWindow.OperationChoice>())
            {
                operation.SelectedItem=choice;await Task.Delay(20);states.Add(new{operation=choice.Value.ToString(),controls=window.GetVisualDescendants().OfType<Control>().Where(c=>c.Name is "ConversionTarget" or "ExtractFormat" or "AudioSampleRate" or "UseHeuristicAnalysis" or "BatchCompression" or "BootSource" or "OutputFolder").Select(c=>new{name=c.Name,enabled=c.IsEffectivelyEnabled,visible=c.IsEffectivelyVisible}).ToArray()});await Capture(window,"native-batch-operation-"+choice.Value+".png");
            }
            await CancelPicker(window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Add files...")),"Batch Add files");await CancelPicker(Browse("Input folder"),"Batch input folder");
            operation.SelectedItem=operation.Items.Cast<BatchProcessWindow.OperationChoice>().Single(c=>c.Value.ToString()=="InstallBootSystem");await CancelPicker(Browse("Boot source / profile"),"Batch boot/profile");await CancelPicker(Browse("Output folder"),"Batch output folder");
            var matrixControls=new[]{"ConversionTarget","ExtractFormat","AudioSampleRate","UseHeuristicAnalysis","BatchCompression","BootSource","OutputFolder","SelectedFilesMode","InputFolderMode"}.ToDictionary(name=>name,name=>Find<Control>(name));
            foreach(var choice in operation.Items.Cast<BatchProcessWindow.OperationChoice>().Where(c=>c.Value.ToString()!="Analyze"))foreach(string mode in new[]{"SelectedFilesMode","InputFolderMode"})foreach(string targetValue in Find<ComboBox>("ConversionTarget").Items.Cast<string>())foreach(string extractValue in Find<ComboBox>("ExtractFormat").Items.Cast<string>())
            {
                operation.SelectedItem=choice;((RadioButton)matrixControls[mode]).IsChecked=true;((ComboBox)matrixControls["ConversionTarget"]).SelectedItem=targetValue;((ComboBox)matrixControls["ExtractFormat"]).SelectedItem=extractValue;
                matrix.Add(new{operation=choice.Value.ToString(),mode,target=targetValue,extract=extractValue,controls=new[]{"ConversionTarget","ExtractFormat","AudioSampleRate","UseHeuristicAnalysis","BatchCompression","BootSource","OutputFolder"}.Select(name=>{var c=matrixControls[name];return new{name,enabled=c.IsEffectivelyEnabled,visible=c.IsEffectivelyVisible};}).ToArray()});
            }
            operation.SelectedItem=operation.Items.Cast<BatchProcessWindow.OperationChoice>().Single(c=>c.Value.ToString()=="TestIntegrity");
            string reference=Path.GetFullPath(Path.Combine(output,"../../wpf-reference/reference.mzf"));byte[] bytes;
            if(File.Exists(reference))bytes=File.ReadAllBytes(reference);else{bytes=new byte[640];bytes[0]=1;System.Text.Encoding.ASCII.GetBytes("RENAMED").CopyTo(bytes,1);bytes[17]=13;System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),512);System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000);System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);for(int i=24;i<128;i++)bytes[i]=(byte)i;for(int i=128;i<bytes.Length;i++)bytes[i]=(byte)(i%7);}
            fixtureSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));string source=Path.Combine(directory,"reference.mzf");File.WriteAllBytes(source,bytes);window.AddInputs([source,source]);Check(window.InputTable.Items.Count==1,"Repeated Add deduplicates selected paths");
            window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Preview / dry-run")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.Model.Documents.WaitForIdleAsync();Check(window.Model.Documents.Error is null&&window.Session?.Rows.Count==1,"Native Preview populates the same window from Core");await Capture(window,"native-batch-preview.png");
            await CancelPicker(window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Export TXT / CSV / JSON report...")),"Batch report Save");
            window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Selected details...")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(60);var detail=window.OwnedWindows.Single();var tabs=detail.GetVisualDescendants().OfType<TabControl>().Single();Check(tabs.Items.Count==5,"Selected details exposes all five WPF tabs");
            for(int i=0;i<5;i++){tabs.SelectedIndex=i;await Capture(detail,"native-batch-details-"+i+".png");}detail.Close();
            Find<TextBox>("NamingTemplate").Text="{name}_new{ext}";await Task.Delay(50);Check(window.Session?.CanExecute==false,"Changing settings invalidates the prepared snapshot");
            operation.SelectedItem=operation.Items.Cast<BatchProcessWindow.OperationChoice>().Single(c=>c.Value.ToString()=="ExtractAll");Find<ComboBox>("ExtractFormat").SelectedItem="MZF";Find<TextBox>("OutputFolder").Text=Path.Combine(directory,"out");await Task.Delay(50);await window.PreviewAsync();Check(!Directory.Exists(Path.Combine(directory,"out")),"Preview creates no user output");
            var execution=window.ExecuteAsync();await Task.Delay(50);var confirmation=window.OwnedWindows.Single();confirmation.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Continue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await execution;
            Check(window.Model.Documents.Error is null&&window.Session!.Rows[0].Result=="Completed","Native explicit Execute completes prepared extraction");Check(File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes),"Batch keeps source bytes unchanged");Check(File.ReadAllBytes(Directory.GetFiles(window.Session!.Rows[0].Output,"*.mzf").Single()).AsSpan().SequenceEqual(bytes),"Reopened extracted MZF is byte exact");
            foreach(var(width,height)in new[]{(1440,960),(1366,768),(920,640),(640,480)}){window.MinWidth=window.MinHeight=0;window.Width=width;window.Height=height;await Capture(window,$"native-batch-{width}x{height}.png");var stop=Find<Button>("StopBatch");var point=stop.TranslatePoint(default,window)!.Value;Check(point.Y>=0&&point.Y+stop.Bounds.Height<=window.Bounds.Height+1,$"Footer reachable at {width}x{height}");}
            window.Close();await window.Model.Documents.WaitForIdleAsync();await Task.Delay(50);Check(!window.IsVisible,"Native Batch closes after cleanup");
        }
        finally
        {
            File.WriteAllText(Path.Combine(output,"native-batch.json"),JsonSerializer.Serialize(new{checks,frames,states,fixtureSha256},new JsonSerializerOptions{WriteIndented=true}));File.WriteAllText(Path.Combine(output,"batch-state-matrix.json"),JsonSerializer.Serialize(matrix));window.Close();await window.Model.Documents.WaitForIdleAsync();string resolved=Path.GetFullPath(directory);if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Unsafe test cleanup path");Directory.Delete(resolved,true);
        }
    }
}
