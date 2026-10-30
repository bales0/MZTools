using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; Refresh();
        try { await execute(); }
        finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record ModuleStatus(string Name, string Phase, string State, string Description);

public sealed partial class ShellViewModel : Observable
{
    public DocumentCoordinator Documents { get; }
    public IReadOnlyList<ModuleStatus> Modules { get; } = new[]
    {
        new ModuleStatus("Tape", "F4", "Implemented", "Tape, MZF, M12 and MZT record editing, compression and conversion."),
        new ModuleStatus("QuickDisk", "F4", "Implemented", "SHARP logical editor and region map. Non-SHARP/unknown hosts remain read-only."),
        new ModuleStatus("Disks / HFE", "F5", "Implemented", "DSK/HFE, filesystems, maps, hex, safe repair, boot/system and IPL workflows. Platform validation is recorded separately."),
        new ModuleStatus("Audio analysis", "F6", "Implemented", "Read-only WAV/recording analysis, shared pulse graphs, FFT, recovery and safe exports. Platform validation is recorded separately."),
        new ModuleStatus("Recorder", "F7–F8", "Implemented; hardware validation separate", "Explicit capture commands and independent sessions. No device is started by this shell."),
        new ModuleStatus("Batch process", "F4–F9", "Implemented", "Preflight, explicit execution, safe outputs and reports using the shared Core services."),
        new ModuleStatus("Reports", "F3", "Implemented", "Shell readiness report. Media reports arrive with their module adapters.")
    };
    public ObservableCollection<ModuleStatus> VisibleModules { get; } = [];
    public bool IsFilterEmpty => VisibleModules.Count == 0;
    private ModuleStatus? selectedModule;
    private string filter = "";
    private bool inspectorVisible = true;
    public ModuleStatus? SelectedModule
    {
        get => selectedModule;
        set
        {
            if (!Set(ref selectedModule, value)) return;
            if (value?.Name is "Tape" or "QuickDisk")
            {
                var document = ActiveTape is not null && ActiveTape.IsQuickDisk == (value.Name == "QuickDisk") ? ActiveTape
                    : Documents.Documents.OfType<TapeSession>().LastOrDefault(d => d.IsQuickDisk == (value.Name == "QuickDisk"));
                if (document is not null) Documents.Active = document;
            }
            if(value?.Name=="Audio analysis" && ActiveAudio is null && Documents.Documents.OfType<AudioSession>().LastOrDefault() is { } audio)Documents.Active=audio;
            if(value?.Name=="Batch process"&&ActiveBatch is null&&Documents.Documents.OfType<BatchSession>().LastOrDefault() is { } batch)Documents.Active=batch;
            Changed(nameof(ShowBatch));
            Changed(nameof(ShowAudio));
            if(value?.Name=="Disks / HFE" && ActiveDisk is null && Documents.Documents.OfType<DiskSession>().LastOrDefault() is { } disk) Documents.Active=disk;
            Changed(nameof(ShowDisk)); Changed(nameof(ShowReadiness));
            Changed(nameof(Heading)); Changed(nameof(Description)); Changed(nameof(ShowMedia));
        }
    }
    public string Heading => SelectedModule?.Name == "Reports" ? "Shell readiness report" : (SelectedModule?.Name ?? "Workspace") + " workspace";
    public string Description => ShowBatch ? ActiveBatch!.Summary : ShowAudio ? ActiveAudio!.Summary : ShowDisk ? ActiveDisk!.Summary : ShowMedia ? ActiveTape!.Summary : SelectedModule?.Description ?? "Select a workspace.";
    public string Filter { get => filter; set { if (Set(ref filter, value)) ApplyFilter(); } }
    public bool InspectorVisible { get => inspectorVisible; set => Set(ref inspectorVisible, value); }
    public bool HasDocument => Documents.Active is not null;
    public string DocumentReason => Documents.Active?.SaveDisabledReason ?? "No open document. Create Tape/QuickDisk or Open a supported medium.";
    public string Report => ShowBatch ? ActiveBatch!.Report : ShowAudio ? ActiveAudio!.Report : ShowDisk ? ActiveDisk!.Report : ShowMedia ? ActiveTape!.MediaReport : "MZTools · Module readiness\n\n" + string.Join("\n", Modules.Select(m => $"{m.Name} | {m.Phase} | {m.State}\n{m.Description}\n"))
        + "\nShared Core and Capture services; platform evidence is recorded separately.\nNo capture device is opened by shell construction.\nPlatform test results are documented separately; this report is not test certification.";
    public event Action? RecorderRequested;
    public AsyncCommand RecorderCommand {get;}
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand SaveAsCommand { get; }
    public AsyncCommand CloseCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand CopyReportCommand { get; }
    public AsyncCommand InspectorCommand { get; }

    public ShellViewModel(DocumentCoordinator documents, Func<string, Task> copyText, IMediaDialogs? media = null,
        Func<byte[], Task>? copyPacket = null, Func<Task<byte[]?>>? readPacket = null)
    {
        Documents = documents;
        RecorderCommand=new(()=>{RecorderRequested?.Invoke();return Task.CompletedTask;});
        OpenCommand = new(async () => { await documents.OpenAsync(); }, () => !documents.IsBusy);
        SaveCommand = new(async () => { await documents.SaveAsync(); }, () => !documents.IsBusy && documents.Active is { CanSave: true, IsReadOnly: false, IsModified: true } && ActiveTape?.HasDraft != true && ActiveDisk?.HasDraft != true);
        SaveAsCommand = new(async () => { await documents.SaveAsync(true); }, () => !documents.IsBusy && documents.Active is { CanSaveAs: true } && ActiveTape?.HasDraft != true && ActiveDisk?.HasDraft != true);
        CloseCommand = new(async () => { await documents.CloseAsync(); }, () => !documents.IsBusy && HasDocument);
        CancelCommand = new(() => { documents.Cancel(); return Task.CompletedTask; }, () => documents.IsBusy);
        CopyReportCommand = new(async () =>
        {
            try { await copyText(Report); }
            catch (Exception e) when (e is not OutOfMemoryException)
            { documents.ReportError("Clipboard unavailable: " + e.Message); }
        });
        InspectorCommand = new(() => { InspectorVisible = !InspectorVisible; return Task.CompletedTask; });
        InitializeMedia(media, copyPacket, readPacket);
        InitializeDisk(media as IDiskDialogs,copyPacket,readPacket);
        InitializeAudio();
        InitializeBatch();
        documents.PropertyChanged += OnDocumentStateChanged;
        SelectedModule = Modules[0]; ApplyFilter();
    }

    private DocumentSession? observed;
    private void OnDocumentStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentCoordinator.Active))
        {
            if (observed is not null) observed.PropertyChanged -= OnActiveChanged;
            observed = Documents.Active;
            if (observed is not null) observed.PropertyChanged += OnActiveChanged;
            if(ActiveBatch is not null)SelectedModule=Modules[5];
            if (ActiveAudio is not null)SelectedModule=Modules[3];
            if (ActiveDisk is not null) SelectedModule=Modules[2];
            if (ActiveTape is not null) SelectedModule = Modules[ActiveTape.IsQuickDisk ? 1 : 0];
        }
        RefreshCommands();
    }
    private void OnActiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TapeSession.Format) && ActiveTape is not null) SelectedModule = Modules[ActiveTape.IsQuickDisk ? 1 : 0];
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { OpenCommand, SaveCommand, SaveAsCommand, CloseCommand, CancelCommand }) command.Refresh();
        RefreshMediaCommands();
        RefreshDiskCommands();
        RefreshAudioCommands();
        RefreshBatchCommands();
        Changed(nameof(HasDocument)); Changed(nameof(DocumentReason)); Changed(nameof(ActiveTape)); Changed(nameof(ShowMedia)); Changed(nameof(Description)); Changed(nameof(Report));
    }
    private void ApplyFilter()
    {
        VisibleModules.Clear();
        foreach (var module in Modules.Where(m => (m.Name + " " + m.Phase + " " + m.State).Contains(Filter, StringComparison.OrdinalIgnoreCase)))
            VisibleModules.Add(module);
        Changed(nameof(IsFilterEmpty));
    }
}
