using System.Buffers.Binary;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Smoke;

// Real native-window workflow with controlled picker results. The separate shell smoke uses real OS pickers.
internal static class MediaScenario
{
    internal static async Task Run(string output)
    {
        var checks = new List<object>();
        var frames = new List<object>();
        string directory = Path.Combine(Path.GetTempPath(),"mztools-f4-native-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dialogs = new ScenarioDialogs(); byte[]? clipboard = null;
        var model = new ShellViewModel(new(new MediaDocumentLoader(),dialogs,new()),_=>Task.CompletedTask,dialogs,
            bytes=>{clipboard=bytes;return Task.CompletedTask;},()=>Task.FromResult(clipboard));
        var window = new MainWindow(model); window.Show();
        void Check(bool value,string name) { checks.Add(new {name,pass=value}); if(!value)throw new InvalidOperationException(name); }
        async Task Execute(AsyncCommand command)
        {
            if(!command.CanExecute(null))throw new InvalidOperationException("Command unavailable.");
            command.Execute(null);
            for(int i=0;i<1200&&model.Documents.IsBusy;i++)await Task.Delay(10);
            if(model.Documents.IsBusy||model.Documents.HasError)throw new InvalidOperationException(model.Documents.Error??"Command timeout.");
            await Task.Delay(60);
        }
        void Capture(string name)
        {
            window.UpdateLayout();double scale=window.RenderScaling;
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width*scale),(int)Math.Ceiling(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));
            bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
            frames.Add(new {file=name,logicalWidth=window.Bounds.Width,logicalHeight=window.Bounds.Height,
                renderScaling=scale,pixelWidth=bitmap.PixelSize.Width,pixelHeight=bitmap.PixelSize.Height,
                origin="Skia visual render of a real native window; not desktop screen capture"});
        }
        try
        {
            string first=Path.Combine(directory,"first.mzf"),second=Path.Combine(directory,"second.mzf");
            var bytes=Fixture("FIRST");File.WriteAllBytes(first,bytes);File.WriteAllBytes(second,Fixture("SECOND"));
            await Execute(model.NewTapeCommand);Check(model.ActiveTape is { Format:"Mzt",IsModified:true },"Native New Tape creates a real document");
            dialogs.Imports=[first,second];await Execute(model.ImportCommand);Check(model.ActiveTape!.Rows.Count==2,"Native import applies two actual MZF files");
            var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="RecordsTable");
            table.SelectedItem=model.ActiveTape.Rows[1];await Task.Delay(60);
            model.ActiveTape.Name="NATIVE EDIT";await Execute(model.ApplyMetadataCommand);Check(model.ActiveTape.Rows[1].Name=="NATIVE EDIT","Native metadata Apply updates the selected record");
            dialogs.Save=new(Path.Combine(directory,"saved.mzt"),"Mzt");await Execute(model.SaveAsCommand);
            Check(File.Exists(dialogs.Save.Path)&&File.Exists(Path.ChangeExtension(dialogs.Save.Path,".mti")),"Native Save As writes media and sidecar");
            Check(model.ActiveTape.Path==dialogs.Save.Path&&!model.ActiveTape.IsModified,"Native Save As rebinds clean document");
            Check(File.ReadAllBytes(first).AsSpan().SequenceEqual(bytes),"Native edits keep the imported source unchanged");
            Capture("native-tape.png");
            await Execute(model.CloseCommand);dialogs.OpenPath=dialogs.Save.Path;await Execute(model.OpenCommand);
            Check(model.ActiveTape!.Rows[1].Name=="NATIVE EDIT","Native reopen reads the persisted edit");
            table.SelectedItem=model.ActiveTape.Rows[1];await Task.Delay(60);await Execute(model.CopyRecordsCommand);
            await Execute(model.NewQuickDiskCommand);await Execute(model.PasteRecordsCommand);
            Check(model.ActiveTape is {IsQuickDisk:true} disk&&disk.Rows.Count==1,"Native codec transfer copies into a separate QuickDisk document");
            Check(model.ActiveTape!.Rows[0].Name=="NATIVE EDIT","Native transfer retains the selected record identity");
            dialogs.Save=new(Path.Combine(directory,"saved.qdf"),"Qdf");await Execute(model.SaveAsCommand);
            Check(File.Exists(dialogs.Save.Path),"Native QuickDisk Save As writes a real QDF");
            Capture("native-quickdisk.png");
            var tabs=window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="RecordTabs");tabs.SelectedIndex=1;await Task.Delay(60);
            Capture("native-quickdisk-map.png");
            var activePath=model.ActiveTape!.Path;
            dialogs.Export=new(Path.Combine(directory,"export.lep"),"Lep",SelectedOnly:false);await Execute(model.ExportCommand);
            Check(File.Exists(dialogs.Export.Path)&&new FileInfo(dialogs.Export.Path).Length>0,"Native export uses the real LEP writer");
            Check(model.ActiveTape.Path==activePath&&!model.ActiveTape.IsModified,"Native Export preserves document binding");
            window.Width=640;window.Height=480;await Task.Delay(120);Capture("native-media-compact.png");
            Check(window.IsVisible&&window.FindControl<Button>("OpenButton")!.IsEffectivelyVisible,"Native compact window keeps document commands reachable");
            await model.Documents.CloseAllAsync();window.Close();
            Check(!window.IsVisible,"Native media window closes through the document coordinator");
        }
        finally
        {
            File.WriteAllText(Path.Combine(output,"native-media.json"),JsonSerializer.Serialize(new
            {os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,frames,
                pickerMode="Controlled test paths; real platform picker cancellation is covered by native-smoke.json",checks},new JsonSerializerOptions{WriteIndented=true}));
            if(window.IsVisible){await model.Documents.CloseAllAsync();window.Close();}
            string resolved=Path.GetFullPath(directory);
            if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned temporary root.");
            Directory.Delete(resolved,true);
        }
    }
    private static byte[] Fixture(string name)
    {
        var bytes=new byte[640];bytes[0]=1;System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes,1);bytes[17]=13;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),512);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);
        for(int i=128;i<bytes.Length;i++)bytes[i]=(byte)(i%7);return bytes;
    }
    private sealed class ScenarioDialogs : IDocumentDialogs,IMediaDialogs
    {
        internal string? OpenPath;internal SaveChoice? Save;internal IReadOnlyList<string> Imports=[];internal ExportChoice? Export;
        public Task<string?> OpenAsync()=>Task.FromResult(OpenPath);
        public Task<string?> SaveAsAsync(DocumentSession document)=>Task.FromResult(Save?.Path);
        public Task<SaveChoice?> ChooseSaveAsync(DocumentSession document)=>Task.FromResult(Save);
        public Task<bool> ConfirmOverwriteAsync(string path)=>Task.FromResult(true);
        public Task<CloseDecision> ConfirmCloseAsync(DocumentSession document)=>Task.FromResult(CloseDecision.Discard);
        public Task<bool> ConfirmLossAsync(string message)=>Task.FromResult(true);
        public Task<IReadOnlyList<string>> PickImportAsync()=>Task.FromResult(Imports);
        public Task<CompressionChoice?> ChooseCompressionAsync()=>Task.FromResult<CompressionChoice?>(null);
        public Task<ExportChoice?> ChooseExportAsync(TapeSession session)=>Task.FromResult(Export);
        public Task<bool> ConfirmPreviewAsync(string title,string report)=>Task.FromResult(true);
    }
}
