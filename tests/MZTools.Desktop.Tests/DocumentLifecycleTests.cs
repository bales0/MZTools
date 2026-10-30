using System.Text;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

public sealed class TestDocument : DocumentSession
{
    public string Content { get; private set; } = "original";
    public bool FailWrite { get; set; }
    public bool FailValidation { get; set; }
    public Func<Task>? BeforeWrite { get; set; }
    public void Edit(string content) { Content = content; MarkModified(); }
    public override async Task WriteAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (BeforeWrite is not null) await BeforeWrite();
        await destination.WriteAsync(Encoding.UTF8.GetBytes(Content), cancellationToken);
        if (FailWrite) throw new IOException("Injected partial write failure.");
    }
    public override Task ValidateSavedAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        if (FailValidation) throw new InvalidDataException("Injected output validation failure.");
        return Task.CompletedTask;
    }
}

public sealed class TestDialogs : IDocumentDialogs
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public bool Overwrite { get; set; }
    public int OverwritePrompts { get; private set; }
    public Queue<CloseDecision> CloseDecisions { get; } = new();
    public Task<string?> OpenAsync() => Task.FromResult(OpenPath);
    public Task<string?> SaveAsAsync(DocumentSession document) => Task.FromResult(SavePath);
    public Task<bool> ConfirmOverwriteAsync(string path) { OverwritePrompts++; return Task.FromResult(Overwrite); }
    public Task<CloseDecision> ConfirmCloseAsync(DocumentSession document) => Task.FromResult(CloseDecisions.TryDequeue(out var value) ? value : CloseDecision.Cancel);
}

public sealed class TestLoader : IDocumentLoader
{
    public List<TestDocument> Created { get; } = [];
    public Func<string, CancellationToken, Task>? BeforeLoad { get; set; }
    public async Task<DocumentSession> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (BeforeLoad is not null) await BeforeLoad(path, cancellationToken);
        var document = new TestDocument(); Created.Add(document); return document;
    }
}

public sealed class DocumentLifecycleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MZTools-F3-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestLoader loader = new();
    private readonly TestDialogs dialogs = new();
    private readonly DocumentCoordinator coordinator;
    public DocumentLifecycleTests()
    {
        Directory.CreateDirectory(directory);
        coordinator = new(loader, dialogs, new());
    }
    private string Source(string name = "příliš-žluťoučký.mzt", string content = "original")
    { var path = Path.Combine(directory, name); File.WriteAllText(path, content); return path; }
    private async Task<TestDocument> Open(string? path = null)
    {
        dialogs.OpenPath = path ?? Source();
        Assert.True(await coordinator.OpenAsync());
        return Assert.IsType<TestDocument>(coordinator.Active);
    }
    private void NoTemporaryFiles() => Assert.Empty(Directory.GetFiles(directory, ".mztools-*.tmp"));

    [Fact] public async Task CancelOpenPreservesActiveAndSelectionContext()
    {
        var document = await Open(); document.Edit("edited"); dialogs.OpenPath = null;
        Assert.False(await coordinator.OpenAsync()); Assert.Same(document, coordinator.Active);
        Assert.True(document.IsModified); Assert.Single(coordinator.Documents);
    }
    [Fact] public async Task FailedOpenKeepsExistingDocument()
    {
        var document = await Open(); dialogs.OpenPath = Path.Combine(directory, "missing");
        Assert.False(await coordinator.OpenAsync()); Assert.Same(document, coordinator.Active); Assert.True(coordinator.HasError);
    }
    [Fact] public async Task ProductionLoaderRejectsUnsupportedExtensionWithoutChangingSource()
    {
        var path = Source("unsupported.xyz"); var production = new DocumentCoordinator(new MediaDocumentLoader(), dialogs, new());
        dialogs.OpenPath = path; Assert.False(await production.OpenAsync());
        Assert.Empty(production.Documents); Assert.Contains("Open supports", production.Error); Assert.Equal("original", File.ReadAllText(path));
    }
    [Fact] public async Task ReopeningPathActivatesExistingTab()
    {
        var first = await Open(); await Open(Source("second")); dialogs.OpenPath = first.Path;
        Assert.True(await coordinator.OpenAsync()); Assert.Same(first, coordinator.Active); Assert.Equal(2, coordinator.Documents.Count);
    }
    [Fact] public async Task SourceChangingDuringOpenIsRejected()
    {
        var first = await Open(); var path = Source("changing"); dialogs.OpenPath = path;
        loader.BeforeLoad = (p, _) => File.WriteAllTextAsync(p, "external change");
        Assert.False(await coordinator.OpenAsync()); Assert.Same(first, coordinator.Active); Assert.Single(coordinator.Documents);
    }
    [Fact] public async Task UnexpectedAdapterFailureRetainsActiveDocument()
    {
        var first = await Open(); dialogs.OpenPath = Source("adapter-failure");
        loader.BeforeLoad = (_, _) => throw new InvalidOperationException("Adapter failure");
        Assert.False(await coordinator.OpenAsync()); Assert.Same(first, coordinator.Active);
        Assert.Equal("Adapter failure", coordinator.Error); Assert.False(coordinator.IsBusy);
    }
    [Fact] public async Task SaveAsBindsCopyOnlyAfterSuccess()
    {
        var document = await Open(); var source = document.Path!; document.Edit("changed");
        dialogs.SavePath = Path.Combine(directory, "kopie.mzt");
        Assert.True(await coordinator.SaveAsync(true)); Assert.Equal(dialogs.SavePath, document.Path);
        Assert.Equal("original", File.ReadAllText(source)); Assert.Equal("changed", File.ReadAllText(document.Path!));
        Assert.False(document.IsModified); document.Edit("changed again"); Assert.True(await coordinator.SaveAsync());
        Assert.Equal("changed again", File.ReadAllText(document.Path!)); NoTemporaryFiles();
    }
    [Fact] public async Task CancelSaveAsPreservesPathDirtyAndBytes()
    {
        var document = await Open(); var path = document.Path; document.Edit("changed");
        Assert.False(await coordinator.SaveAsync(true)); Assert.Equal(path, document.Path);
        Assert.True(document.IsModified); Assert.Equal("original", File.ReadAllText(path!)); NoTemporaryFiles();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SaveAsRequiresExplicitOverwrite(bool overwrite)
    {
        var document = await Open(); var source = document.Path; document.Edit("replacement");
        dialogs.SavePath = Source("existing", "keep"); dialogs.Overwrite = overwrite;
        Assert.Equal(overwrite, await coordinator.SaveAsync(true)); Assert.Equal(1, dialogs.OverwritePrompts);
        Assert.Equal(overwrite ? "replacement" : "keep", File.ReadAllText(dialogs.SavePath));
        Assert.Equal(overwrite ? dialogs.SavePath : source, document.Path); Assert.Equal(!overwrite, document.IsModified);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PartialWriteOrValidationFailurePreservesOriginal(bool validation)
    {
        var document = await Open(); document.Edit("must not replace");
        document.FailValidation = validation; document.FailWrite = !validation;
        Assert.False(await coordinator.SaveAsync()); Assert.True(document.IsModified); Assert.True(coordinator.HasError);
        Assert.Equal("original", File.ReadAllText(document.Path!)); NoTemporaryFiles();
    }
    [Fact] public async Task ExternalSourceChangesBlockSave()
    {
        var document = await Open(); document.Edit("mine"); File.WriteAllText(document.Path!, "external");
        Assert.False(await coordinator.SaveAsync()); Assert.Equal("external", File.ReadAllText(document.Path!)); Assert.True(document.IsModified);
    }
    [Fact] public async Task DestinationChangesDuringSerializationBlockReplacement()
    {
        var document = await Open(); document.Edit("mine");
        document.BeforeWrite = () => File.WriteAllTextAsync(document.Path!, "external");
        Assert.False(await coordinator.SaveAsync()); Assert.Equal("external", File.ReadAllText(document.Path!)); NoTemporaryFiles();
    }
    [Fact] public async Task SaveAsCannotReplaceAnotherOpenDocument()
    {
        var first = await Open(); var second = await Open(Source("second")); second.Edit("bad");
        dialogs.SavePath = first.Path; dialogs.Overwrite = true;
        Assert.False(await coordinator.SaveAsync(true)); Assert.Equal("original", File.ReadAllText(first.Path!)); Assert.True(second.IsModified);
    }
    [Theory] [InlineData(CloseDecision.Cancel, false)] [InlineData(CloseDecision.Discard, true)] [InlineData(CloseDecision.Save, true)]
    public async Task CloseHonorsDecision(CloseDecision decision, bool closes)
    {
        var document = await Open(); document.Edit("changed"); dialogs.CloseDecisions.Enqueue(decision);
        Assert.Equal(closes, await coordinator.CloseAsync()); Assert.Equal(closes ? 0 : 1, coordinator.Documents.Count);
        Assert.Equal(decision == CloseDecision.Save ? "changed" : "original", File.ReadAllText(document.Path!));
    }
    [Fact] public async Task SaveFailurePreventsClose()
    {
        var document = await Open(); document.Edit("changed"); document.FailWrite = true;
        dialogs.CloseDecisions.Enqueue(CloseDecision.Save); Assert.False(await coordinator.CloseAsync()); Assert.Same(document, coordinator.Active);
    }
    [Fact] public async Task QuitCancelRetainsAllTabsIncludingEarlierDiscardDecision()
    {
        var first = await Open(); first.Edit("first"); var second = await Open(Source("second")); second.Edit("second");
        dialogs.CloseDecisions.Enqueue(CloseDecision.Discard); dialogs.CloseDecisions.Enqueue(CloseDecision.Cancel);
        Assert.False(await coordinator.CloseAllAsync()); Assert.Equal(2, coordinator.Documents.Count);
        Assert.True(first.IsModified); Assert.True(second.IsModified); Assert.Same(second, coordinator.Active);
    }
    [Fact] public async Task ReadOnlySourceCannotBeOverwrittenButCanBeCopied()
    {
        var path = Source(); File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var document = await Open(path); document.Edit("copy"); Assert.True(document.IsReadOnly);
            Assert.False(await coordinator.SaveAsync()); dialogs.SavePath = path; Assert.False(await coordinator.SaveAsync(true));
            dialogs.SavePath = Path.Combine(directory, "copy"); Assert.True(await coordinator.SaveAsync(true));
            Assert.False(document.IsReadOnly); Assert.Equal("original", File.ReadAllText(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
    [Fact] public async Task CancellationCleansTemporaryAndRetainsDirtyDocument()
    {
        var document = await Open(); document.Edit("changed");
        document.BeforeWrite = () => { coordinator.Cancel(); return Task.CompletedTask; };
        Assert.False(await coordinator.SaveAsync()); Assert.Equal("original", File.ReadAllText(document.Path!)); Assert.True(document.IsModified); NoTemporaryFiles();
    }
    [Fact] public async Task ConcurrentCommandsAreRejected()
    {
        var document = await Open(); document.Edit("changed"); var started = new TaskCompletionSource(); var release = new TaskCompletionSource();
        document.BeforeWrite = async () => { started.SetResult(); await release.Task; };
        var save = coordinator.SaveAsync(); await started.Task;
        Assert.True(coordinator.IsBusy); Assert.False(await coordinator.CloseAsync()); Assert.False(await coordinator.OpenAsync());
        release.SetResult(); Assert.True(await save); Assert.False(coordinator.IsBusy);
    }
    [Fact] public async Task ModificationDuringSaveIsNotMarkedClean()
    {
        var document = await Open(); document.Edit("first");
        document.BeforeWrite = () => { document.Edit("second"); return Task.CompletedTask; };
        Assert.True(await coordinator.SaveAsync()); Assert.True(document.IsModified);
    }
    [Fact] public async Task LinuxCaseDistinctNamesRemainSeparateDocuments()
    {
        var lower = Source("case"); var upper = Path.Combine(directory, "CASE");
        if (OperatingSystem.IsWindows()) { Assert.True(PathIdentity.Same(lower, upper)); return; }
        File.WriteAllText(upper, "upper"); await Open(lower); await Open(upper);
        Assert.Equal(2, coordinator.Documents.Count); Assert.False(PathIdentity.Same(lower, upper));
    }
    [Fact] public async Task LinuxSymbolicLinkSaveFailsClosed()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlink privilege is not assumed.
        var document = await Open(); document.Edit("changed"); var link = Path.Combine(directory, "link");
        File.CreateSymbolicLink(link, document.Path!); dialogs.SavePath = link; dialogs.Overwrite = true;
        Assert.False(await coordinator.SaveAsync(true)); Assert.Equal("original", File.ReadAllText(document.Path!)); NoTemporaryFiles();
    }

    public void Dispose()
    {
        // Only this fixture's resolved, randomly named temp directory is removed.
        var full = Path.GetFullPath(directory);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full);
        Directory.Delete(full, recursive: true);
    }
}
