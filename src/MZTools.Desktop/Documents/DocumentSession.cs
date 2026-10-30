using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MZTools.Desktop.Documents;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}

/// <summary>UI lifecycle only. Domain adapters own parsing, validation and serialization.</summary>
public abstract class DocumentSession : Observable, IDisposable
{
    private string? path;
    private bool modified;
    private long revision;
    public Guid Id { get; } = Guid.NewGuid();
    public string? Path => path;
    protected virtual string UntitledName => "Untitled";
    public string Title => (path is null ? UntitledName : System.IO.Path.GetFileName(path)) + (HasUnsavedChanges ? " *" : "");
    public virtual bool HasUnsavedChanges => IsModified;
    public bool IsModified => modified;
    public bool IsReadOnly { get; protected set; }
    public long Revision => revision;
    public virtual bool CanSave => true;
    public virtual bool CanSaveAs => CanSave;
    public virtual string SaveDisabledReason => !CanSave ? "This document has no save adapter."
        : IsReadOnly ? "Source is read-only. Use Save As to create a copy."
        : !IsModified ? "No unsaved changes." : "";
    public string? SavedFingerprint { get; private set; }

    protected void MarkModified()
    {
        revision++; modified = true; Changed(nameof(IsModified)); Changed(nameof(Title)); Changed(nameof(SaveDisabledReason));
    }

    internal void Bind(string destination, string fingerprint, long savedRevision)
    {
        path = destination; SavedFingerprint = fingerprint;
        modified = revision != savedRevision; IsReadOnly = false; OnBound();
        Changed(nameof(Path)); Changed(nameof(Title)); Changed(nameof(IsModified));
        Changed(nameof(IsReadOnly)); Changed(nameof(SaveDisabledReason));
    }

    internal void Loaded(string source, string fingerprint, bool readOnly)
    {
        Bind(source, fingerprint, revision); IsReadOnly = readOnly; OnLoaded(); Changed(nameof(IsReadOnly));
    }

    public virtual void Dispose() { }
    protected virtual void OnBound() { }
    protected virtual void OnLoaded() { }

    public abstract Task WriteAsync(Stream destination, CancellationToken cancellationToken);
    public abstract Task ValidateSavedAsync(string temporaryPath, CancellationToken cancellationToken);
}

public interface IDocumentLoader
{
    Task<DocumentSession> LoadAsync(string path, CancellationToken cancellationToken);
}


internal sealed record LoadProgress(string Stage,double Fraction);
internal interface IProgressDocumentLoader
{
    IProgress<LoadProgress>? Progress {get;set;}
}
