using System.Text;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    public BatchSession? ActiveBatch=>Documents.Active as BatchSession;
    public bool ShowBatch=>ActiveBatch is not null&&SelectedModule?.Name=="Batch process";
    public AsyncCommand BatchPreviewCommand {get;private set;}=null!;
    public AsyncCommand BatchExecuteCommand {get;private set;}=null!;
    public AsyncCommand BatchReportCommand {get;private set;}=null!;
    private void InitializeBatch()
    {
        BatchPreviewCommand=new(async()=>await Documents.ExecuteAsync(async ct=>
        {
            var dialogs=DiskDialogs!;var answer=await dialogs.DiskFormAsync("Batch process · preflight",[
                new("operation","Operation","Analyze",Enum.GetNames<BatchOperation>()),new("input","Input selection","Choose files",["Choose files","Scan folder"]),new("folder","Input folder (for scan)",""),new("recursive","Include subfolders","No",["No","Yes"]),new("extensions","Scan extensions","dsk;hfe;mzf;mzt;mzq;qdf;qd;wav;flac;lep;l16"),
                new("output","Output directory",Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),new("target","Conversion target","HFEv3",new[]{"HFEv3","HFEv2","HFE","DSK"}.Concat(BatchTapeService.Targets).Concat(Enum.GetNames<DskConversionTarget>()).Distinct().ToArray()),new("template","Output name template","{name}_processed{ext}"),
                new("strict","Strict preflight","Yes",["Yes","No"]),new("replace","Replace originals (separate explicit confirmation follows)","No",["No","Yes"]),new("backup","Back up replaced originals","Yes",["Yes","No"]),new("auxiliary","Verified boot/system source (InstallBootSystem)",""),new("heuristic","Use heuristic audio analysis","No",["No","Yes"]),new("rate","WAV output sample rate","44100",["22050","44100","48000","88200","96000"]),new("extract","Extract format","Native",["Native","Mzf","M12","Bin"])],"Preflight reads inputs and prepares a reviewable plan. No output is written before Execute and confirmation. Changed inputs are rejected. Apply Patch is not a batch operation.");if(answer is null)return false;
            var operation=Enum.Parse<BatchOperation>(answer["operation"]);string[] inputs=answer["input"]=="Scan folder"?await Task.Run(()=>BatchProcessService.Scan(answer["folder"],answer["recursive"]=="Yes",answer["extensions"],ct),ct):(await dialogs.PickDiskFilesAsync("Batch inputs",true)).ToArray();if(inputs.Length==0)return false;
            MzfCompressionOptions? compression=null;
            if(operation==BatchOperation.CompressPrograms)
            {
                if(MediaDialogs is null)throw new InvalidOperationException("Compression dialog is unavailable.");var choice=await MediaDialogs.ChooseCompressionAsync();if(choice is null)return false;compression=new(Enum.Parse<MzfCompressionAlgorithm>(choice.Mode),Enum.Parse<CompressionDirection>(choice.Direction),choice.Quick,choice.Embedded,choice.Skip);
            }
            var options=new BatchOptions(operation,answer["output"],answer["target"],answer["template"],string.IsNullOrWhiteSpace(answer["auxiliary"])?null:answer["auxiliary"],answer["strict"]=="Yes",answer["replace"]=="Yes",answer["backup"]=="Yes",answer["input"]=="Scan folder"?answer["folder"]:null,compression,answer.Number("rate"),answer["heuristic"]=="Yes",answer["extract"]);
            var preview=await Task.Run(()=>BatchProcessService.Preview(inputs,options,cancellationToken:ct),ct);ct.ThrowIfCancellationRequested();Documents.Add(new BatchSession(preview));return true;
        }),()=>!Documents.IsBusy&&DiskDialogs is not null);
        BatchExecuteCommand=new(async()=>
        {
            var session=ActiveBatch;if(session is null)return;await Documents.ExecuteAsync(async ct=>
            {
                session.VerifySources();foreach(var row in session.Preview.Rows.Where(r=>r.Result=="Will modify"))
                {
                    foreach(var document in Documents.Documents.Where(d=>d!=session))
                    {
                        var paths=document is IFileSetDocument group?group.SourceHashes.Keys:document.Path is { } path?[path]:Enumerable.Empty<string>();
                        if(paths.Any(p=>PathIdentity.Same(p,row.Output)||row.ExtractedFiles is not null&&p.StartsWith(Path.GetFullPath(row.Output)+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)))throw new IOException("A batch output belongs to another open document: "+row.Output);
                    }
                }
                if(!await DiskDialogs!.ConfirmDiskAsync("Execute prepared batch",session.Report+"\n\n"+(session.Preview.Options.ReplaceOriginals?"This operation replaces original files; backups: "+session.Preview.Options.BackupOriginals:"Separate outputs; originals remain preserved.")))return false;
                session.VerifySources();await Task.Run(()=>BatchProcessService.Execute(session.Preview,true,session.Preview.Options.ReplaceOriginals,cancellationToken:ct),ct);session.Refresh();if(session.Preview.Cancelled)throw new OperationCanceledException(ct);return true;
            });
        },()=>!Documents.IsBusy&&DiskDialogs is not null&&ActiveBatch?.CanExecute==true);
        BatchReportCommand=new(async()=>
        {
            var session=ActiveBatch;if(session is null)return;await Documents.ExecuteAsync(async ct=>
            {
                var answer=await DiskDialogs!.DiskFormAsync("Export batch report",[new("format","Format","txt",["txt","csv","json"])]);if(answer is null)return false;var path=await DiskDialogs.PickDiskOutputAsync("Export batch report","batch."+answer["format"]);if(path is null)return false;
                await Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=Encoding.UTF8.GetBytes(BatchProcessService.Report(session.Preview,answer["format"]))},ct);return true;
            });
        },()=>!Documents.IsBusy&&DiskDialogs is not null&&ActiveBatch is not null);
    }
    private void RefreshBatchCommands()
    {BatchPreviewCommand.Refresh();BatchExecuteCommand.Refresh();BatchReportCommand.Refresh();Changed(nameof(ActiveBatch));Changed(nameof(ShowBatch));}
}
