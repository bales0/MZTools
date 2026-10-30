using System.Buffers.Binary;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Smoke;

// Native application workflow. Controlled paths are private test fixtures, never user media.
internal static class DiskScenario
{
    internal static async Task Run(string output)
    {
        string root=Path.Combine(Path.GetTempPath(),"mztools-f5-native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var checks=new List<object>();var frames=new List<object>();var dialogs=new Dialogs();byte[]? clipboard=null;
        var model=new ShellViewModel(new(new MediaDocumentLoader(dialogs),dialogs,new()),_=>Task.CompletedTask,dialogs,b=>{clipboard=b;return Task.CompletedTask;},()=>Task.FromResult(clipboard));
        var window=new MainWindow(model);window.Show();
        void Check(bool ok,string name){checks.Add(new{name,pass=ok});if(!ok)throw new InvalidOperationException(name);}
        async Task Execute(AsyncCommand command)
        {
            if(!command.CanExecute(null))throw new InvalidOperationException("Unavailable command.");command.Execute(null);
            for(int i=0;i<1800&&model.Documents.IsBusy;i++)await Task.Delay(10);
            if(model.Documents.IsBusy||model.Documents.HasError)throw new InvalidOperationException(model.Documents.Error??"Command timeout.");await Task.Delay(80);
        }
        void Capture(string name)
        {
            window.UpdateLayout();double scale=window.RenderScaling;using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width*scale),(int)Math.Ceiling(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
            frames.Add(new{file=name,logicalWidth=window.Bounds.Width,logicalHeight=window.Bounds.Height,renderScaling=scale,pixelWidth=bitmap.PixelSize.Width,pixelHeight=bitmap.PixelSize.Height,origin="Skia visual render of a real native window; not desktop screen capture"});
        }
        TableView Files()=>window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="DiskFiles");
        TabControl Tabs()=>window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="DiskTabs");
        try
        {
            string path=Path.Combine(root,"FIRST.mzf");var source=Fixture();File.WriteAllBytes(path,source);
            await Execute(model.NewDiskCommand);Check(model.ActiveDisk is {IsModified:true,IsHfe:false},"Native New disk creates a Core DSK document");
            dialogs.Inputs=[path];await Execute(model.DiskImportCommand);Check(model.ActiveDisk!.Files.Count==1,"Native disk import inserts actual MZF data");
            var disk=model.ActiveDisk;Files().SelectedItem=disk.Files[0];await Task.Delay(80);disk.FileName="NATIVE";await Execute(model.DiskApplyPropertiesCommand);Check(disk.Files[0].Name=="NATIVE","Native inspector applies filename metadata");
            string saved=Path.Combine(root,"native.dsk");dialogs.Save=new(saved,"Dsk",false);await Execute(model.SaveAsCommand);Check(File.Exists(saved)&&disk.Path==saved&&!disk.IsModified,"Native disk Save As writes and rebinds only after success");Capture("native-disk-files.png");
            await Execute(model.CloseCommand);dialogs.Open=saved;await Execute(model.OpenCommand);disk=model.ActiveDisk!;Check(disk.Files.Single().Name=="NATIVE","Native DSK reopen reloads edited directory");
            Tabs().SelectedIndex=1;await Task.Delay(80);var map=window.GetVisualDescendants().OfType<DiskMap>().Single();map.Zoom(4);Check(disk.MapZoom>1,"Native disk map shares session zoom state");Capture("native-disk-map.png");
            Tabs().SelectedIndex=0;await Task.Delay(80);Files().SelectedItem=disk.Files[0];await Task.Delay(80);await Execute(model.DiskCopyCommand);Check(clipboard is {Length:>0},"Native disk copy uses the common typed transfer codec");
            dialogs.Profile="LecCpmDd";dialogs.Cylinders="80";await Execute(model.NewDiskCommand);await Execute(model.DiskPasteCommand);Check(model.ActiveDisk!.Files.Count==1,"Native cross-disk paste applies compatible CP/M transfer preview");
            dialogs.Profile="MzBasic";dialogs.Cylinders="40";dialogs.Container="HFE V3";await Execute(model.NewDiskCommand);await Execute(model.DiskImportCommand);var hfe=model.ActiveDisk!;dialogs.Save=new(Path.Combine(root,"native.hfe"),"Hfe",false);await Execute(model.SaveAsCommand);Check(File.Exists(hfe.Path!)&&hfe.IsHfe,"Native HFE creation and verified Save As complete");
            Tabs().SelectedIndex=4;await Task.Delay(80);await Execute(model.DiskLoadTrackCommand);Check(hfe.PhysicalReport.Contains("Stored HFE"),"Native physical view loads actual stored HFE cells");Capture("native-hfe-physical.png");
            byte[] damaged=File.ReadAllBytes(saved);damaged[0x34]=1;string bad=Path.Combine(root,"damaged.dsk");File.WriteAllBytes(bad,damaged);dialogs.Open=bad;await Execute(model.OpenCommand);var repaired=model.ActiveDisk!;Check(repaired.RequiresRepairSaveAs&&!model.SaveCommand.CanExecute(null),"Native damaged open requires explicit repair preview and Save As");Capture("native-disk-repair.png");
            dialogs.Save=new(Path.Combine(root,"repaired.dsk"),"Dsk",false);await Execute(model.SaveAsCommand);Check(!repaired.RequiresRepairSaveAs&&!repaired.IsModified,"Native repaired copy commits to a new clean binding");Check(File.ReadAllBytes(bad).AsSpan().SequenceEqual(damaged),"Native repair leaves damaged original byte-identical");
            window.Width=800;window.Height=600;await Task.Delay(120);Capture("native-disk-compact.png");Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(source),"Native disk workflows keep imported source unchanged");
            Check(await model.Documents.CloseAllAsync(),"Native disk documents close through shared lifecycle");window.Close();await Task.Delay(80);Check(!window.IsVisible,"Native disk window closes without leaked document ownership");
        }
        finally
        {
            File.WriteAllText(Path.Combine(output,"native-disk.json"),JsonSerializer.Serialize(new{os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,frames,checks,pickerMode="Controlled private fixture paths; native picker cancellation remains covered by root smoke",hardwareTested=false},new JsonSerializerOptions{WriteIndented=true}));
            if(window.IsVisible){await model.Documents.CloseAllAsync();window.Close();}
            string resolved=Path.GetFullPath(root);if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Refusing cleanup outside owned temporary directory.");Directory.Delete(resolved,true);
        }
    }
    private static byte[] Fixture()
    {
        var bytes=new byte[640];bytes[0]=1;System.Text.Encoding.ASCII.GetBytes("FIRST").CopyTo(bytes,1);bytes[17]=13;BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),512);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);for(int i=128;i<bytes.Length;i++)bytes[i]=(byte)(i%7);return bytes;
    }
    private sealed class Dialogs : IDocumentDialogs,IMediaDialogs,IDiskDialogs
    {
        internal string? Open;internal SaveChoice? Save;internal IReadOnlyList<string> Inputs=[];
        internal string Profile="MzBasic",Container="DSK",Cylinders="40";
        public Task<string?> OpenAsync()=>Task.FromResult(Open);
        public Task<string?> SaveAsAsync(DocumentSession document)=>Task.FromResult(Save?.Path);
        public Task<SaveChoice?> ChooseSaveAsync(DocumentSession document)=>Task.FromResult(Save);
        public Task<bool> ConfirmOverwriteAsync(string path)=>Task.FromResult(true);
        public Task<bool> ConfirmLossAsync(string message)=>Task.FromResult(true);
        public Task<CloseDecision> ConfirmCloseAsync(DocumentSession document)=>Task.FromResult(CloseDecision.Discard);
        public Task<IReadOnlyList<string>> PickImportAsync()=>Task.FromResult(Inputs);
        public Task<CompressionChoice?> ChooseCompressionAsync()=>Task.FromResult<CompressionChoice?>(null);
        public Task<ExportChoice?> ChooseExportAsync(TapeSession session)=>Task.FromResult<ExportChoice?>(null);
        public Task<bool> ConfirmPreviewAsync(string title,string report)=>Task.FromResult(true);
        public Task<IReadOnlyList<string>> PickDiskFilesAsync(string title,bool multiple=false)=>Task.FromResult(Inputs);
        public Task<string?> PickDiskOutputAsync(string title,string name)=>Task.FromResult<string?>(null);
        public Task<bool> ConfirmDiskAsync(string title,string report)=>Task.FromResult(true);
        public Task<DiskAnswer?> DiskFormAsync(string title,IReadOnlyList<DiskField> fields,string explanation="")
        {
            var values=fields.ToDictionary(f=>f.Key,f=>f.Value);if(title=="Disk format"){values["format"]=Profile;values["container"]=Container;values["tracks"]=Cylinders;}
            return Task.FromResult<DiskAnswer?>(new(values));
        }
    }
}
