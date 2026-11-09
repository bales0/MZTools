using System.Text;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;

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
            var dialogs=DiskDialogs!;var answer=await dialogs.DiskFormAsync("Batch process · preflight",BatchProcessForm.Fields(),"Preflight validates each input and operation using the shared Core policy. Custom folder extensions restrict scanning; unsupported combinations appear as incompatible rows. Preview writes no outputs. Execute, data loss and replacement each require their own confirmation.");if(answer is null)return false;
            var operation=Enum.Parse<BatchOperation>(answer["operation"]);string[] inputs=answer["input"]=="Scan folder"?await Task.Run(()=>BatchInputSelection.Scan(answer["folder"],answer["recursive"]=="Yes",answer["extensions"],operation,answer["target"],ct),ct):(await dialogs.PickBatchFilesAsync("Batch inputs",BatchMediaPolicy.Extensions(operation,answer["target"]))).ToArray();if(inputs.Length==0)return false;
            MzfCompressionOptions? compression=null;
            if(operation==BatchOperation.CompressPrograms)
            {
                if(MediaDialogs is null)throw new InvalidOperationException("Compression dialog is unavailable.");var choice=await MediaDialogs.ChooseCompressionAsync();if(choice is null)return false;compression=new(Enum.Parse<MzfCompressionAlgorithm>(choice.Mode),Enum.Parse<CompressionDirection>(choice.Direction),choice.Quick,choice.Embedded,choice.Skip);
            }
            bool writes=!BatchMediaPolicy.ReadOnly(operation),replace=writes&&operation!=BatchOperation.ExtractAll&&answer["replace"]=="Yes";
            bool audioOutput=BatchProcessForm.ProducesAudio(operation,answer["target"],answer["extract"]);
            var options=new BatchOptions(operation,writes?answer["output"]:"",answer["target"],answer["template"],operation==BatchOperation.InstallBootSystem&&!string.IsNullOrWhiteSpace(answer["auxiliary"])?answer["auxiliary"]:null,answer["strict"]=="Yes",replace,answer["backup"]=="Yes",answer["input"]=="Scan folder"?answer["folder"]:null,compression,audioOutput?answer.Number("rate"):SharpTapeExporter.WavSampleRate,BatchProcessForm.SupportsAudioInput(operation,answer["target"])&&inputs.Any(p=>Path.GetExtension(p).ToLowerInvariant() is ".wav" or ".flac")&&answer["heuristic"]=="Yes",operation==BatchOperation.ExtractAll?answer["extract"]:"Native");
            var progress=Documents.BatchProgress();var preview=await Task.Run(()=>BatchProcessService.Preview(inputs,options,progress,ct),ct);Documents.Add(new BatchSession(preview));if(preview.Cancelled)throw new OperationCanceledException(ct);return true;
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
                bool acceptLoss=false,acceptReplace=false;
                var lossy=session.Preview.Rows.Where(r=>r.Result=="Will modify"&&r.RequiresLossConfirmation).ToArray();
                if(lossy.Length>0){acceptLoss=await DiskDialogs.ConfirmDiskAsync("Accept batch data loss",string.Join("\n\n",lossy.Select(r=>r.Input+"\n"+r.Warnings+"\n"+r.DetailedReport)));if(!acceptLoss)return false;}
                if(session.Preview.Options.ReplaceOriginals){acceptReplace=await DiskDialogs.ConfirmDiskAsync("Replace original batch inputs","Replace the original input files listed in this preview? Backups: "+session.Preview.Options.BackupOriginals+"\n\n"+session.Report);if(!acceptReplace)return false;}
                session.VerifySources();var progress=Documents.BatchProgress();
                try{await Task.Run(()=>BatchProcessService.Execute(session.Preview,acceptLoss,acceptReplace,progress,ct),ct);}
                finally{session.Refresh();}
                if(session.Preview.Cancelled)throw new OperationCanceledException(ct);return true;
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
