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

internal static class AudioScenario
{
    internal static async Task Run(string output)
    {
        string root=Path.Combine(Path.GetTempPath(),"mztools-f6-native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var checks=new List<object>();var frames=new List<object>();var dialogs=new Dialogs();
        var model=new ShellViewModel(new(new MediaDocumentLoader(dialogs),dialogs,new()),_=>Task.CompletedTask,dialogs);var window=new MainWindow(model);window.Show();
        void Check(bool ok,string name){checks.Add(new{name,pass=ok});if(!ok)throw new InvalidOperationException(name);}
        async Task Execute(AsyncCommand command)
        {
            if(!command.CanExecute(null))throw new InvalidOperationException("Unavailable audio command.");command.Execute(null);
            for(int i=0;i<3000&&model.Documents.IsBusy;i++)await Task.Delay(10);
            if(model.Documents.IsBusy||model.Documents.HasError)throw new InvalidOperationException(model.Documents.Error??"Audio command timeout.");await Task.Delay(80);
        }
        void Capture(string name)
        {
            window.UpdateLayout();double scale=window.RenderScaling;using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width*scale),(int)Math.Ceiling(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
            frames.Add(new{file=name,logicalWidth=window.Bounds.Width,logicalHeight=window.Bounds.Height,renderScaling=scale,origin="Skia visual render of real native window; not desktop capture"});
        }
        TabControl Tabs()=>window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="AudioTabs");
        try
        {
            string mzf=Path.Combine(root,"original.mzf"),wave=Path.Combine(root,"generated.wav");var bytes=new byte[160];bytes[0]=1;"NATIVE"u8.CopyTo(bytes.AsSpan(1));bytes[17]=13;BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),32);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);for(int i=128;i<160;i++)bytes[i]=(byte)i;File.WriteAllBytes(mzf,bytes);
            dialogs.Open=mzf;await Execute(model.OpenCommand);dialogs.Export=new(wave,"Wav",SelectedOnly:false);await Execute(model.ExportCommand);Check(File.Exists(wave),"Native F4 export creates actual decodable audio fixture");var original=File.ReadAllBytes(wave);
            dialogs.Open=wave;await Execute(model.OpenCommand);var s=model.ActiveAudio!;Check(model.ShowAudio&&s.IsReadOnly&&!s.CanSave&&!s.CanSaveAs,"Native WAV opens in read-only Audio workspace");Check(s.Programs.Count==1&&s.Blocks.Count>0,"Native initial analysis recovers the actual MZF program");Capture("native-audio-records.png");
            var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="AudioBlocks");table.SelectedItem=s.Blocks[0];await Task.Delay(80);Check(s.SelectionEnd==s.Blocks[0].End,"Native record table selects exact Core block frames");await Execute(model.AudioMeasureCommand);Check(s.Distributions.Count>0,"Native Measure uses existing pulse statistics");Tabs().SelectedIndex=1;await Task.Delay(100);Capture("native-audio-pulses.png");
            s.SetSelection([(0,original.Length-44)]); // Generated F4 WAV is PCM8 mono: one byte per frame.
            await Execute(model.AudioSpectrumCommand);await Execute(model.AudioSpectrogramCommand);Tabs().SelectedIndex=4;await Task.Delay(100);Capture("native-audio-spectrum.png");var kinds=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="SpectralKind");kinds.SelectedIndex=1;await Task.Delay(80);Capture("native-audio-spectrogram.png");Check(s.SpectralSummary.Contains("Periodic Hann")&&s.SpectralSummary.Contains("FFT windows"),"Native FFT views retain method and exact-window provenance");
            s.SetSelection([(100,1000)]);dialogs.Output=Path.Combine(root,"selection.wav");await Execute(model.AudioWaveExportCommand);Check(File.Exists(dialogs.Output)&&new FileInfo(dialogs.Output).Length==944,"Native selected PCM export writes 900 actual PCM8 frames");Check(s.Path==wave&&File.ReadAllBytes(wave).AsSpan().SequenceEqual(original),"Native WAV export preserves original bytes and binding");
            s.SetSelection([(0,original.Length-44)]);await Execute(model.AudioRecoverCommand);Check(s.Programs.Count==1&&s.Passes.Count>0&&s.Candidates.Any(c=>c.CanSynthesize),"Native deeper analysis retains complete programs and checksum candidates");Capture("native-audio-recovery.png");s.SelectedPass=s.Passes[0];Check(s.RecoveryStreams.Count>0,"Native recovery exposes actual consumed detector streams");await Execute(model.AudioComparePassesCommand);Check(s.PassComparisonSummary.Length>0,"Native retained passes support evidence comparison");
            await Execute(model.AudioSendTapeCommand);Check(model.ActiveTape is {IsQuickDisk:false,IsModified:true}&&model.ActiveTape.Rows.Count==1,"Native recovered program transfers into F4 Tape document");model.Documents.Active=s;await Execute(model.AudioSendQuickDiskCommand);Check(model.ActiveTape is {IsQuickDisk:true,IsModified:true}&&model.ActiveTape.Rows.Count==1,"Native recovered program transfers into F4 QuickDisk document");model.Documents.Active=s;
            window.Width=800;window.Height=600;await Task.Delay(100);Capture("native-audio-compact.png");Check(File.ReadAllBytes(wave).AsSpan().SequenceEqual(original)&&File.ReadAllBytes(mzf).AsSpan().SequenceEqual(bytes),"Native audio workflows keep all source media byte-identical");Check(await model.Documents.CloseAllAsync(),"Native audio document lifecycle closes all temporary drafts");window.Close();Check(!window.IsVisible,"Native audio window closes after preview cancellation");
        }
        finally
        {
            File.WriteAllText(Path.Combine(output,"native-audio.json"),JsonSerializer.Serialize(new{os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,frames,checks,hardwareTested=false,pickerMode="Controlled private fixtures; real picker cancellation covered by root smoke"},new JsonSerializerOptions{WriteIndented=true}));
            if(window.IsVisible){await model.Documents.CloseAllAsync();window.Close();}
            string resolved=Path.GetFullPath(root);if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Refusing cleanup outside owned temporary directory.");Directory.Delete(resolved,true);
        }
    }
    private sealed class Dialogs : IDocumentDialogs,IMediaDialogs,IDiskDialogs
    {
        internal string? Open;internal SaveChoice? Save {get;set;}internal IReadOnlyList<string> Inputs=[];
        internal string? Output;internal ExportChoice? Export;
        public Task<string?> OpenAsync()=>Task.FromResult(Open);
        public Task<string?> SaveAsAsync(DocumentSession document)=>Task.FromResult(Save?.Path);
        public Task<SaveChoice?> ChooseSaveAsync(DocumentSession document)=>Task.FromResult(Save);
        public Task<bool> ConfirmOverwriteAsync(string path)=>Task.FromResult(true);
        public Task<bool> ConfirmLossAsync(string message)=>Task.FromResult(true);
        public Task<CloseDecision> ConfirmCloseAsync(DocumentSession document)=>Task.FromResult(CloseDecision.Discard);
        public Task<IReadOnlyList<string>> PickImportAsync()=>Task.FromResult(Inputs);
        public Task<CompressionChoice?> ChooseCompressionAsync()=>Task.FromResult<CompressionChoice?>(null);
        public Task<ExportChoice?> ChooseExportAsync(TapeSession session)=>Task.FromResult(Export);
        public Task<bool> ConfirmPreviewAsync(string title,string report)=>Task.FromResult(true);
        public Task<IReadOnlyList<string>> PickDiskFilesAsync(string title,bool multiple=false)=>Task.FromResult(Inputs);
        public Task<string?> PickDiskOutputAsync(string title,string name)=>Task.FromResult(Output);
        public Task<bool> ConfirmDiskAsync(string title,string report)=>Task.FromResult(true);
        public Task<DiskAnswer?> DiskFormAsync(string title,IReadOnlyList<DiskField> fields,string explanation="")
        {
            var values=fields.ToDictionary(f=>f.Key,f=>f.Value);if(title=="Deeper audio analysis")values["thresholds"]="1";
            return Task.FromResult<DiskAnswer?>(new(values));
        }
    }
}
