namespace MZTools.Desktop.Documents;

public sealed class BatchRowView
{
    internal BatchRow Value {get;}
    internal BatchRowView(BatchRow value)=>Value=value;
    public string Input=>Value.Input;public string Output=>Value.Output;public string Operation=>Value.Operation;public string Format=>Value.DetectedFormat;public string Filesystem=>Value.Filesystem;public string Result=>Value.Result;public string Reason=>Value.Reason;public string Severity=>Value.Severity.ToString();public string Integrity=>Value.Integrity;
    public string Detail=>$"{Value.Input}\n{Value.Output}\n{Value.Result}: {Value.Reason}\n{Value.Validation}\n{Value.Warnings}\n{Value.Error}\n{Value.DetailedReport}\n\n"+string.Join("\n",Value.DetailFields.Select(f=>f.Property+": "+f.Value))+"\n\n"+string.Join("\n",Value.IntegrityChecks.Select(c=>$"{c.Subject}: {c.Check} · {c.Result} · {c.Details}"));
    public IReadOnlyList<BatchContentView> Contents=>Value.Contents.Select(c=>new BatchContentView(c.Number,c.Name,c.Size,c.Type,c.Load?.ToString("X4")??"",c.Exec?.ToString("X4")??"",c.Profile,c.Compression,c.Attributes)).ToArray();
}
public sealed record BatchContentView(int Number,string Name,long Size,string Type,string Load,string Execute,string Profile,string Compression,string Attributes);

public sealed class BatchSession : DocumentSession,IFileSetDocument
{
    internal BatchPreview Preview {get;}
    private readonly IReadOnlyDictionary<string,string?> sources;
    IReadOnlyDictionary<string,string?> IFileSetDocument.SourceHashes=>sources;
    internal BatchSession(BatchPreview preview)
    {Preview=preview;sources=preview.Rows.SelectMany(r=>r.Dependencies.Keys.Append(r.Input)).Distinct(AtomicFileTransaction.PathComparer).ToDictionary(p=>p,AtomicFileTransaction.Hash,AtomicFileTransaction.PathComparer);Refresh();}
    protected override string UntitledName=>"Batch · "+Preview.Options.Operation;
    public override bool CanSave=>false;public override bool CanSaveAs=>false;
    public override string SaveDisabledReason=>"Batch uses Preview → explicit Execute. Export report creates a separate artifact.";
    public IReadOnlyList<BatchRowView> Rows {get;private set;}=[];
    private BatchRowView? selected;
    public BatchRowView? Selected {get=>selected;set{if(Set(ref selected,value)){Changed(nameof(Detail));Changed(nameof(Contents));}}}
    public string Detail=>Selected?.Detail??Preview.Summary;
    public IReadOnlyList<BatchContentView> Contents=>Selected?.Contents??[];
    public string Summary=>Preview.Summary;
    private string report="";
    public string Report=>report;
    public bool CanExecute=>!Preview.Cancelled&&(!Preview.Options.StrictPreflight||!Preview.BlocksStrict)&&Preview.Rows.Any(r=>r.Result is "Will modify" or "Will analyze");
    internal void Refresh(){report=BatchProcessService.Report(Preview,"txt");int index=selected is null?-1:Rows.ToList().IndexOf(selected);Rows=Preview.Rows.Select(r=>new BatchRowView(r)).ToArray();Changed(nameof(Rows));Selected=index>=0&&index<Rows.Count?Rows[index]:Rows.FirstOrDefault();foreach(var name in new[]{nameof(Summary),nameof(Report),nameof(CanExecute)})Changed(name);}
    internal void VerifySources(){foreach(var pair in sources)if(AtomicFileTransaction.Hash(pair.Key)!=pair.Value)throw new IOException("Batch source or dependency changed after preview: "+pair.Key);}
    public override Task WriteAsync(Stream destination,CancellationToken cancellationToken)=>throw new NotSupportedException(SaveDisabledReason);
    public override Task ValidateSavedAsync(string temporaryPath,CancellationToken cancellationToken)=>throw new NotSupportedException(SaveDisabledReason);
    SaveChoice IFileSetDocument.CurrentSaveChoice(string path)=>throw new NotSupportedException(SaveDisabledReason);
    Task<DocumentSavePlan> IFileSetDocument.PrepareSaveAsync(SaveChoice choice,CancellationToken cancellation)=>throw new NotSupportedException(SaveDisabledReason);
}
