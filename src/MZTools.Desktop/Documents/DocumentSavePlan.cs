namespace MZTools.Desktop.Documents;

public sealed record SaveChoice(string Path, string? Format = null, bool GenerateSidecar = true, bool PreserveTrailing = true);

internal sealed record DocumentSavePlan(IReadOnlyDictionary<string, byte[]> Outputs, string LossWarning, Action<bool> Commit, bool RequireNewDestination = false, Func<IDisposable>? Protect = null, Action? BeforeCommit = null);

internal interface IFileSetDocument
{
    IReadOnlyDictionary<string, string?> SourceHashes { get; }
    SaveChoice CurrentSaveChoice(string path);
    Task<DocumentSavePlan> PrepareSaveAsync(SaveChoice choice, CancellationToken cancellation);
}
