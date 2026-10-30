using System.Collections.ObjectModel;

namespace MZTools.Desktop.Documents;

public enum CloseDecision { Save, Discard, Cancel }

public interface IDocumentDialogs
{
    Task<string?> OpenAsync();
    Task<string?> SaveAsAsync(DocumentSession document);
    Task<bool> ConfirmOverwriteAsync(string path);
    Task<CloseDecision> ConfirmCloseAsync(DocumentSession document);
    async Task<SaveChoice?> ChooseSaveAsync(DocumentSession document)
    { var path = await SaveAsAsync(document); return path is null ? null : new(path); }
    Task<bool> ConfirmLossAsync(string message) => Task.FromResult(false);
}

public sealed class DocumentCoordinator : Observable
{
    private readonly IDocumentLoader loader;
    private readonly IDocumentDialogs dialogs;
    private readonly AtomicDocumentStore store;
    public DocumentCoordinator(IDocumentLoader loader, IDocumentDialogs dialogs, AtomicDocumentStore store)
    {
        this.loader = loader; this.dialogs = dialogs; this.store = store;
        Documents = new(documents);
    }
    private DocumentSession? active;
    private bool busy;
    private string? error;
    private string status = "Ready · Media editors will be connected in subsequent phases.";
    private CancellationTokenSource? operation;
    private readonly ObservableCollection<DocumentSession> documents = [];
    public ReadOnlyObservableCollection<DocumentSession> Documents { get; }
    public DocumentSession? Active { get => active; set { if (value is null || documents.Contains(value)) Set(ref active, value); } }
    private long operationGeneration;
    private bool hasProgress;
    private double progressFraction;
    public bool HasProgress {get=>hasProgress;private set=>Set(ref hasProgress,value);}
    public double ProgressFraction {get=>progressFraction;private set=>Set(ref progressFraction,value);}
    private void ReportProgress(string stage,double fraction,long generation)
    {if(!IsBusy||generation!=operationGeneration)return;HasProgress=true;ProgressFraction=double.IsFinite(fraction)?Math.Clamp(fraction,0,1):0;Status=stage+" · "+ProgressFraction.ToString("P0");}
    internal IProgress<WavAnalysisProgress> AudioProgress()
    {long generation=operationGeneration;return new Progress<WavAnalysisProgress>(p=>ReportProgress(p.Stage,p.Fraction,generation));}
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public string? Error { get => error; private set { Set(ref error, value); Changed(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public string Status { get => status; private set => Set(ref status, value); }
    public void ClearError() => Error = null;
    public void ReportError(string message) => Error = message;
    public void Cancel() => operation?.Cancel();
    internal Task<bool> ExecuteAsync(Func<CancellationToken, Task<bool>> action) => RunAsync(async ct =>
    {
        Status = "Working…"; bool result = await action(ct);
        if (Status == "Working…") Status = result ? "Ready." : "No changes applied.";
        return result;
    });
    internal void Add(DocumentSession document) { documents.Add(document); Active = document; }
    internal async Task WriteExportAsync(IReadOnlyDictionary<string, byte[]> outputs, CancellationToken cancellation,
        IReadOnlyDictionary<string,string>? sourceFiles = null,Action? beforeCommit=null)
    {
        var hashes = new Dictionary<string, string?>(AtomicFileTransaction.PathComparer);
        foreach (string path in outputs.Keys)
        {
            if (documents.Any(d => d.Path is not null && PathIdentity.Same(d.Path, path) ||
                d is IFileSetDocument group && group.SourceHashes.Keys.Any(p => PathIdentity.Same(p, path))))
                throw new IOException("Export must not replace an open source or its metadata: " + path);
            string? hash = await AtomicDocumentStore.FingerprintAsync(path, cancellation);
            if (hash is not null && !await dialogs.ConfirmOverwriteAsync(path)) throw new OperationCanceledException();
            hashes.Add(path, hash);
        }
        Status = "Writing export artifacts…";
        await Task.Run(() => AtomicFileTransaction.Write(outputs, hashes, cancellation,sourceFiles:sourceFiles,beforeCommit:beforeCommit), cancellation);
        Status = "Export completed. Active document binding unchanged.";
    }

    public Task<bool> OpenAsync() => RunAsync(async ct =>
    {
        var path = await dialogs.OpenAsync();
        if (path is null) return false;
        return await OpenPathCoreAsync(path,ct);
    });
    public Task<bool> OpenPathAsync(string path)=>RunAsync(ct=>OpenPathCoreAsync(path,ct));
    public bool IsOpenSource(string path)=>documents.Any(d=>d.Path is not null&&PathIdentity.Same(d.Path,path)||d is IFileSetDocument group&&group.SourceHashes.Keys.Any(p=>PathIdentity.Same(p,path)));
    private async Task<bool> OpenPathCoreAsync(string path,CancellationToken ct)
    {
        path = PathIdentity.Normalize(path);
        var existing = documents.FirstOrDefault(d => d.Path is not null && PathIdentity.Same(d.Path, path));
        if (existing is not null) { Active = existing; return true; }
        Status = "Opening " + System.IO.Path.GetFileName(path) + "…";
        var before = await AtomicDocumentStore.FingerprintAsync(path, ct) ?? throw new FileNotFoundException("File was not found.", path);
        if(loader is IProgressDocumentLoader progressLoader)
        {long generation=operationGeneration;progressLoader.Progress=new Progress<LoadProgress>(p=>ReportProgress(p.Stage,p.Fraction,generation));}
        var document = await loader.LoadAsync(path, ct);
        try
        {
        if (await AtomicDocumentStore.FingerprintAsync(path, ct) != before)
            throw new IOException("The source changed while opening. Please open it again.");
        ct.ThrowIfCancellationRequested();
        var readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        document.Loaded(path, before, readOnly);
        documents.Add(document); Active = document; Status = "Opened " + document.Title;
        return true;
        }
        catch {document.Dispose();throw;}
    }

    public Task<bool> SaveAsync(bool saveAs = false) => RunAsync(ct => SaveDocumentAsync(Active, saveAs, ct));

    private async Task<bool> SaveDocumentAsync(DocumentSession? document, bool saveAs, CancellationToken ct)
    {
        if (document is null || (saveAs || document.Path is null ? !document.CanSaveAs : !document.CanSave)) return false;
        if (document is DiskSession { HasDraft: true }) throw new InvalidOperationException("Apply or Cancel disk edits before saving.");
        if (document is TapeSession { HasDraft: true }) throw new InvalidOperationException("Apply or Cancel metadata edits before saving.");
        if (!saveAs && document.IsReadOnly) throw new IOException(document.SaveDisabledReason);
        if (!saveAs && !document.IsModified) return true;
        SaveChoice? choice = saveAs || document.Path is null ? await dialogs.ChooseSaveAsync(document)
            : document is IFileSetDocument current ? current.CurrentSaveChoice(document.Path) : new(document.Path);
        if (choice is null) return false;
        string destination = PathIdentity.Normalize(choice.Path);
        bool same = document.Path is not null && PathIdentity.Same(document.Path, destination);
        if (document.IsReadOnly && same) throw new IOException("Choose a different destination for the read-only source.");
        if (documents.Any(d => d != document && d.Path is not null && PathIdentity.Same(d.Path, destination)))
            throw new IOException("The destination belongs to another open document.");
        if (document is IFileSetDocument group)
        {
            long savedRevision = document.Revision;
            var plan = await group.PrepareSaveAsync(choice with { Path = destination }, ct);
            if (plan.LossWarning.Length != 0 && !await dialogs.ConfirmLossAsync(plan.LossWarning)) return false;
            var hashes = new Dictionary<string, string?>(AtomicFileTransaction.PathComparer);
            foreach (var path in plan.Outputs.Keys)
            {
                if (documents.Any(d => d != document && (d.Path is not null && PathIdentity.Same(d.Path, path) ||
                    d is IFileSetDocument other && other.SourceHashes.Keys.Any(p => PathIdentity.Same(p, path)))))
                    throw new IOException("An output belongs to another open document: " + path);
                bool source = group.SourceHashes.Keys.Any(p => PathIdentity.Same(p, path));
                if (saveAs && document.IsReadOnly && source) throw new IOException("Save As must not replace the read-only source or its metadata.");
                string? hash = source ? group.SourceHashes.First(p => PathIdentity.Same(p.Key, path)).Value
                    : await AtomicDocumentStore.FingerprintAsync(path, ct);
                if (plan.RequireNewDestination && (hash is not null || File.Exists(path) || Directory.Exists(path)))
                    throw new IOException("A repaired copy requires a new, nonexistent destination.");
                if (plan.RequireNewDestination) hash = null;
                if (hash is not null && (!source || saveAs) && !await dialogs.ConfirmOverwriteAsync(path)) return false;
                hashes[path] = hash;
            }
            Status = "Saving document and metadata…";
            using (plan.Protect?.Invoke())
                await Task.Run(() => AtomicFileTransaction.Write(plan.Outputs, hashes, ct, beforeCommit: plan.BeforeCommit), ct);
            plan.Commit(document.Revision == savedRevision);
            var hashMain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(plan.Outputs[destination]));
            document.Bind(destination, hashMain, savedRevision); Status = "Saved " + document.Title;
            return true;
        }
        string? expected = same ? document.SavedFingerprint : await AtomicDocumentStore.FingerprintAsync(destination, ct);
        if (saveAs && expected is not null && !await dialogs.ConfirmOverwriteAsync(destination)) return false;
        long revision = document.Revision;
        Status = "Saving " + System.IO.Path.GetFileName(destination) + "…";
        var fingerprint = await store.SaveAsync(document, destination, expected, ct);
        document.Bind(destination, fingerprint, revision);
        Status = "Saved " + document.Title;
        return true;
    }

    public Task<bool> CloseAsync() => RunAsync(async ct =>
    {
        if (Active is not { } document) return true;
        if (!await PrepareCloseAsync(document, ct)) return false;
        if(document is AudioSession audio)await audio.StopPreviewAsync();
        documents.Remove(document); document.Dispose(); Active = documents.LastOrDefault(); Status = "Document closed.";
        return true;
    });

    public Task<bool> CloseAllAsync() => RunAsync(async ct =>
    {
        // Keep every tab until every document agrees; Cancel cannot silently discard earlier tabs.
        foreach (var document in documents.ToArray())
            if (!await PrepareCloseAsync(document, ct)) return false;
        foreach(var audio in documents.OfType<AudioSession>())await audio.StopPreviewAsync();
        foreach(var document in documents) document.Dispose();
        documents.Clear(); Active = null; return true;
    });

    private async Task<bool> PrepareCloseAsync(DocumentSession document, CancellationToken ct)
    {
        if (!document.HasUnsavedChanges) return true;
        return await dialogs.ConfirmCloseAsync(document) switch
        {
            CloseDecision.Discard => true,
            CloseDecision.Save => await SaveOnCloseAsync(document, ct),
            _ => false
        };
    }
    private async Task<bool> SaveOnCloseAsync(DocumentSession document, CancellationToken cancellation)
    {
        if(document is AudioSession)throw new InvalidOperationException("Analysis drafts are not an audio writer. Export the desired result first, or Discard/Cancel close.");
        if (document is DiskSession { HasDraft: true } disk)
        {
            if(dialogs is not IDiskDialogs diskDialogs)throw new InvalidOperationException("Apply or Cancel disk edits before Save on close.");
            if(disk.HasPropertiesDraft&&disk.HasHexDraft)throw new InvalidOperationException("Apply properties and hex separately before closing.");
            long revision=disk.Revision;DskDocument candidate;string report;
            if(disk.HasHexDraft)
            {var preview=await Task.Run(disk.PrepareHex,cancellation);candidate=disk.Model.Clone();DskHexEditService.Apply(candidate,preview,true);report=preview.Report;}
            else
            {candidate=await Task.Run(disk.PrepareProperties,cancellation);report=new DskOperationPreview(disk.Model.Serialize(),candidate.Serialize(),"Apply properties before saving and closing.",disk.Model).Report;}
            if(!await diskDialogs.ConfirmDiskAsync("Unapplied disk edits",report))return false;
            cancellation.ThrowIfCancellationRequested();disk.ApplyProperties(candidate,revision);
        }
        if (document is TapeSession { HasDraft: true } tape)
        {
            tape.ApplyMetadata();
            if (tape.Validation.Length != 0) throw new InvalidDataException(tape.Validation);
        }
        return await SaveDocumentAsync(document, document.IsReadOnly || !document.CanSave, cancellation) && !document.HasUnsavedChanges;
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task<bool>> action)
    {
        if (IsBusy) return false;
        operationGeneration++;IsBusy = true;HasProgress=false;ProgressFraction=0; Error = null; operation = new();
        try { return await action(operation.Token); }
        catch (OperationCanceledException) { Status = "Cancelled. Document state retained."; return false; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Error = exception.Message; Status = "Operation failed. Document state retained."; return false; }
        finally { operation.Dispose(); operation = null; IsBusy = false;HasProgress=false;if(loader is IProgressDocumentLoader p)p.Progress=null; }
    }
}
