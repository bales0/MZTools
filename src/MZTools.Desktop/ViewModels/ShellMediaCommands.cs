using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    public TapeSession? ActiveTape => Documents.Active as TapeSession;
    public bool ShowMedia => ActiveTape is not null && SelectedModule?.Name is "Tape" or "QuickDisk";
    public bool CanEditMedia => ActiveTape?.CanEdit == true && !Documents.IsBusy;
    public bool CanEditMetadata => CanEditMedia && ActiveTape?.SelectedRow is not null;
    private bool CanMutateRecords => CanEditMedia && ActiveTape?.HasDraft != true;
    public AsyncCommand NewTapeCommand { get; private set; } = null!;
    public AsyncCommand NewQuickDiskCommand { get; private set; } = null!;
    public AsyncCommand ImportCommand { get; private set; } = null!;
    public AsyncCommand ExportCommand { get; private set; } = null!;
    public AsyncCommand ApplyMetadataCommand { get; private set; } = null!;
    public AsyncCommand DeleteRecordsCommand { get; private set; } = null!;
    public AsyncCommand MoveUpCommand { get; private set; } = null!;
    public AsyncCommand MoveDownCommand { get; private set; } = null!;
    public AsyncCommand CompressCommand { get; private set; } = null!;
    public AsyncCommand CopyRecordsCommand { get; private set; } = null!;
    public AsyncCommand PasteRecordsCommand { get; private set; } = null!;
    internal IMediaDialogs? MediaDialogs { get; private set; }

    private void InitializeMedia(IMediaDialogs? dialogs, Func<byte[], Task>? copyPacket, Func<Task<byte[]?>>? readPacket)
    {
        MediaDialogs = dialogs;
        AsyncCommand Operation(Func<CancellationToken, Task<bool>> work, Func<bool> allowed) => new(async () => { await Documents.ExecuteAsync(work); }, allowed);
        bool Selected() => ActiveTape is { } tape && (tape.Selection.Count != 0 || tape.SelectedRow is not null);
        NewTapeCommand = Operation(_ => { Documents.Add(TapeSession.New(false)); return Task.FromResult(true); }, () => !Documents.IsBusy);
        NewQuickDiskCommand = Operation(_ => { Documents.Add(TapeSession.New(true)); return Task.FromResult(true); }, () => !Documents.IsBusy);
        ApplyMetadataCommand = Operation(_ => { ActiveTape!.ApplyMetadata(); return Task.FromResult(ActiveTape.Validation.Length == 0); }, () => CanEditMedia && Selected());
        MoveUpCommand = Operation(_ => { ActiveTape!.Move(-1); return Task.FromResult(true); }, () => CanMutateRecords && Selected());
        MoveDownCommand = Operation(_ => { ActiveTape!.Move(1); return Task.FromResult(true); }, () => CanMutateRecords && Selected());
        DeleteRecordsCommand = Operation(async _ =>
        {
            var tape = ActiveTape!; var rows = tape.Selection.Count != 0 ? tape.Selection.ToArray() : new[] { tape.SelectedRow!.Index };
            long revision = tape.Revision;
            if (!await dialogs!.ConfirmPreviewAsync("Delete records?", $"Remove {rows.Length} selected records from this document? Files on disk remain unchanged until Save.")) return false;
            if (tape.Revision != revision) throw new InvalidOperationException("The document changed. Review the selection again.");
            tape.ApplyRecords(tape.Model.Records.Where((_, index) => !rows.Contains(index)).ToArray()); return true;
        }, () => dialogs is not null && CanMutateRecords && Selected());
        ImportCommand = Operation(async ct =>
        {
            var tape = ActiveTape!; var paths = await dialogs!.PickImportAsync(); if (paths.Count == 0) return false;
            var preview = await MediaOperations.ImportAsync(tape, paths, ct);
            if (!await dialogs.ConfirmPreviewAsync("Import preview", preview.Report)) return false;
            return ApplyPreview(tape, preview);
        }, () => dialogs is not null && CanMutateRecords);
        CompressCommand = Operation(async ct =>
        {
            var tape = ActiveTape!; var choice = await dialogs!.ChooseCompressionAsync(); if (choice is null) return false;
            var preview = await MediaOperations.CompressAsync(tape, choice, ct);
            if (!await dialogs.ConfirmPreviewAsync("Compression preview", preview.Report)) return false;
            return ApplyPreview(tape, preview);
        }, () => dialogs is not null && CanMutateRecords && Selected());
        ExportCommand = Operation(async ct =>
        {
            var tape = ActiveTape!; var choice = await dialogs!.ChooseExportAsync(tape); if (choice is null) return false;
            using var plan = await MediaOperations.ExportAsync(tape, choice, ct);
            if (!await dialogs.ConfirmPreviewAsync("Export preview", plan.Report)) return false;
            await Documents.WriteExportAsync(plan.Outputs, ct, plan.SourceFiles); return true;
        }, () => dialogs is not null && !Documents.IsBusy && ActiveTape is { CanSaveAs: true, HasDraft:false } tape && tape.Model.Records.Count != 0);
        CopyRecordsCommand = Operation(async _ =>
        {
            var packet = WorkspaceDragCodec.Encode(WorkspaceDragCodec.Capture(ActiveTape!.SelectedRecords()));
            await copyPacket!(packet); return true;
        }, () => copyPacket is not null && !Documents.IsBusy && Selected());
        PasteRecordsCommand = Operation(async _ =>
        {
            byte[] packet = await readPacket!() ?? throw new InvalidDataException("Clipboard does not contain supported MZTools records.");
            await PasteAsync(packet); return true;
        }, () => readPacket is not null && CanMutateRecords);
    }
    private static bool ApplyPreview(TapeSession tape, RecordPreview preview)
    {
        if (tape.Revision != preview.Revision) throw new InvalidOperationException("The document changed. Generate a fresh preview.");
        if (tape.Model.Records.Count == preview.Records.Count && tape.Model.Records.Zip(preview.Records).All(p =>
            p.First.GetSerializedHeader().AsSpan().SequenceEqual(p.Second.GetSerializedHeader()) && p.First.Body.MzfBody.AsSpan().SequenceEqual(p.Second.Body.MzfBody) &&
            p.First.Body.TrailingData.AsSpan().SequenceEqual(p.Second.Body.TrailingData) && p.First.Profile == p.Second.Profile && p.First.MetadataOrigin == p.Second.MetadataOrigin)) return false;
        tape.ApplyRecords(preview.Records); return true;
    }
    internal async Task PasteAsync(byte[] bytes)
    {
        var tape = ActiveTape ?? throw new InvalidOperationException("Open a Tape/QuickDisk target.");
        if (tape.HasDraft) throw new InvalidOperationException("Apply or Cancel metadata before a transfer.");
        var records = DiskOperations.RecordsFromPacket(WorkspaceDragCodec.Decode(bytes));
        long revision = tape.Revision;
        if (MediaDialogs is null || !await MediaDialogs.ConfirmPreviewAsync("Transfer preview", $"Copy {records.Length} records to this document? The source remains unchanged.")) return;
        if (tape.Revision != revision) throw new InvalidOperationException("The target changed. Try the transfer again.");
        tape.ApplyRecords(tape.Model.Records.Concat(records).ToArray());
    }
    private void RefreshMediaCommands()
    {
        foreach (var command in new[] { NewTapeCommand, NewQuickDiskCommand, ImportCommand, ExportCommand, ApplyMetadataCommand,
            DeleteRecordsCommand, MoveUpCommand, MoveDownCommand, CompressCommand, CopyRecordsCommand, PasteRecordsCommand }) command.Refresh();
        Changed(nameof(CanEditMedia));
        Changed(nameof(CanEditMetadata));
    }
}
