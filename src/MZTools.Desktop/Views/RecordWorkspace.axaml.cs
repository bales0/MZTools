using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

public sealed partial class RecordWorkspace : UserControl
{
    internal static DataFormat<byte[]> PacketFormat { get; } = DataFormat.CreateBytesApplicationFormat(WorkspaceDragCodec.Format);
    private static DataFormat<ReorderRequest> ReorderFormat { get; } = DataFormat.CreateInProcessFormat<ReorderRequest>("MZTools.RecordReorder");
    private sealed record ReorderRequest(TapeSession Source, long Revision, int[] Indices);
    private ShellViewModel? Model => DataContext as ShellViewModel;
    private TapeSession? observed;
    private bool syncing;
    private PointerPressedEventArgs? pressed;
    private Point pressPoint;
    private Window? detail;
    internal Control WorkspaceFocusRegion => RecordTabs.SelectedIndex == 0 ? RecordFilter : RecordTabs.SelectedIndex == 1 ? MediaMap : RecordTabs;
    internal Control InspectorFocusRegion => RecordInspector;

    public RecordWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindSession();
        SizeChanged += (_, _) => Reflow();
        RecordsTable.AddHandler(PointerPressedEvent, RememberPress, RoutingStrategies.Tunnel);
        RecordsTable.PointerMoved += DragMove;
        RecordsTable.PointerReleased += (_, _) => pressed = null;
        RecordsTable.DoubleTapped += (_,_) => { if(observed?.SelectedRow is not null && TopLevel.GetTopLevel(this) is Window owner) ShowInspector(owner); };
        RecordsTable.AddHandler(DragDrop.DragOverEvent, DragOver);
        RecordsTable.AddHandler(DragDrop.DropEvent, Drop);
        AddHandler(KeyDownEvent, RecordKey, RoutingStrategies.Tunnel);
        MediaMap.FileSelected += MapSelect;
        AttachedToVisualTree += (_, _) => BindSession();
        DetachedFromVisualTree += (_, _) => { if (observed is not null) observed.PropertyChanged -= SessionChanged; observed = null; };
    }
    internal void BindSession()
    {
        if (observed is not null) observed.PropertyChanged -= SessionChanged;
        observed = Model?.ActiveTape;
        if (observed is not null) observed.PropertyChanged += SessionChanged;
        RestoreSelection(); Reflow();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(TapeSession.Rows)) Dispatcher.UIThread.Post(RestoreSelection); }
    private void RestoreSelection()
    {
        if (observed is null) return;
        syncing = true;
        try
        {
            RecordsTable.SelectedItems!.Clear();
            foreach (var row in observed.Rows.Where(r => observed.Selection.Contains(r.Index))) RecordsTable.SelectedItems.Add(row);
            if (RecordsTable.SelectedItems.Count == 0 && observed.SelectedRow is { } selected && observed.Rows.Contains(selected)) RecordsTable.SelectedItems.Add(selected);
        }
        finally { syncing = false; }
        MediaMap.InvalidateVisual();
    }
    private async void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing || observed is null || !RecordsTable.IsEffectivelyVisible) return;
        var selected = RecordsTable.SelectedItems!.OfType<RecordRow>().ToArray();
        if (selected.Length == 0) return; // ItemsSource refresh is followed by explicit restoration.
        if (observed.HasDraft && !selected.Any(r => r.Index == observed.SelectedRow?.Index))
        {
            var current = observed;
            if (Model?.MediaDialogs is null || !await Model.MediaDialogs.ConfirmPreviewAsync("Unapplied metadata", "Discard unapplied metadata edits before changing the selection?")) { RestoreSelection(); return; }
            if (current != observed) return; current.ResetMetadata();
        }
        observed.SetSelection(selected); MediaMap.InvalidateVisual();
    }
    private void Reflow()
    {
        bool show = Bounds.Width >= 850 && Model?.InspectorVisible == true;
        RecordInspectorPanel.IsVisible = show;
        EditorGrid.ColumnDefinitions[1].Width = new GridLength(show ? 16 : 0);
        EditorGrid.ColumnDefinitions[2].Width = new GridLength(show ? 272 : 0);
    }
    internal void ShowInspector(Window owner)
    {
        if (detail is not null) { detail.Activate(); return; }
        detail = new Window { Title = "Record inspector", Width = 380, Height = 640,
            Content = new RecordInspector { DataContext = Model, Margin = new Thickness(20) }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        detail.Closed += (_, _) => detail = null; detail.Show(owner);
    }
    private void ZoomIn(object? sender, RoutedEventArgs e) => MediaMap.Zoom(1.5);
    private void ZoomOut(object? sender, RoutedEventArgs e) => MediaMap.Zoom(1/1.5);
    private void Fit(object? sender, RoutedEventArgs e) => MediaMap.Fit();
    private void FitSelection(object? sender, RoutedEventArgs e) => MediaMap.FitSelection();
    private async void MapSelect(int index, KeyModifiers modifiers)
    {
        if (observed is null) return;
        var session = observed;
        if (session.HasDraft && session.SelectedRow?.Index != index)
        {
            if (Model?.MediaDialogs is null || !await Model.MediaDialogs.ConfirmPreviewAsync("Unapplied metadata", "Discard unapplied edits before selecting another record?")) return;
            if (session != observed) return; session.ResetMetadata();
        }
        var indices = modifiers.HasFlag(KeyModifiers.Shift)
            ? Enumerable.Range(Math.Min(session.MapAnchor,index),Math.Abs(session.MapAnchor-index)+1).ToList()
            : modifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) ? session.Selection.ToList() : [];
        if (!indices.Contains(index)) indices.Add(index); else if (!modifiers.HasFlag(KeyModifiers.Shift)) indices.Remove(index);
        if (!modifiers.HasFlag(KeyModifiers.Shift)) session.MapAnchor = index;
        session.Selection.Clear(); session.Selection.AddRange(indices);
        int selectedIndex = indices.Contains(index) ? index : indices.LastOrDefault(-1);
        session.SelectedRow = selectedIndex < 0 ? null : session.SelectedRow?.Index == selectedIndex ? session.SelectedRow : new RecordRow(session.Model.Records[selectedIndex],selectedIndex); RestoreSelection();
    }
    private void RecordKey(object? sender, KeyEventArgs e)
    {
        if (Model is null || e.Source is TextBox || e.Source is Control c && c.GetVisualAncestors().Any(a => a is TextBox)) return;
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var command = e.Key == Key.Delete ? Model.DeleteRecordsCommand : e.KeyModifiers.HasFlag(primary) && e.Key == Key.C ? Model.CopyRecordsCommand
            : e.KeyModifiers.HasFlag(primary) && e.Key == Key.V ? Model.PasteRecordsCommand : null;
        if (e.Key is Key.F2 or Key.Enter && observed?.SelectedRow is not null &&
            e.Source is Visual source && (source == RecordsTable || source.GetVisualAncestors().Contains(RecordsTable))) { ShowInspector((Window)TopLevel.GetTopLevel(this)!); e.Handled = true; return; }
        if (command?.CanExecute(null) == true) { command.Execute(null); e.Handled = true; }
    }
    private void RememberPress(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !(source is TableViewRow || source.GetVisualAncestors().Any(v => v is TableViewRow))) return;
        if (e.GetCurrentPoint(RecordsTable).Properties.IsLeftButtonPressed) { pressed = e; pressPoint = e.GetPosition(RecordsTable); }
    }
    private async void DragMove(object? sender, PointerEventArgs e)
    {
        if (pressed is not { } start || observed is null || Model?.Documents.IsBusy != false ||
            Math.Abs(e.GetPosition(RecordsTable).X-pressPoint.X) + Math.Abs(e.GetPosition(RecordsTable).Y-pressPoint.Y) < 4 || observed.SelectedRecords().Count == 0) return;
        pressed = null;
        try
        {
            var transfer = CaptureDrag(observed);
            await DragDrop.DoDragDropAsync(start, transfer, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Model.Documents.ReportError(exception.Message); }
    }
    internal static DataTransfer CaptureDrag(TapeSession session)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(PacketFormat,WorkspaceDragCodec.Encode(WorkspaceDragCodec.Capture(session.SelectedRecords()))));
        transfer.Add(DataTransferItem.Create(ReorderFormat,new ReorderRequest(session,session.Revision,
            session.Selection.Count != 0 ? session.Selection.ToArray() : new[] {session.SelectedRow!.Index})));
        return transfer;
    }
    private void DragOver(object? sender, DragEventArgs e)
    {
        try { e.DragEffects = Model?.CanEditMedia == true && e.DataTransfer.Contains(PacketFormat) ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.None; }
        catch (Exception error) when (error is not OutOfMemoryException) { e.DragEffects = DragDropEffects.None; Model?.Documents.ReportError(error.Message); }
        e.Handled = true;
    }
    private async void Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true; if (Model is null || observed is null || !Model.CanEditMedia) return;
        ReorderRequest? local; byte[]? packet;
        try { local = e.DataTransfer.TryGetValue(ReorderFormat); packet = e.DataTransfer.TryGetValue(PacketFormat); }
        catch (Exception error) when (error is not OutOfMemoryException) { Model.Documents.ReportError(error.Message); return; }
        if (packet is null) return;
        var session = observed; var position = e.GetPosition(RecordsTable);
        await Model.Documents.ExecuteAsync(async _ =>
        {
            if (local?.Source == session && !e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
            {
                if (session.Filter.Length != 0 || session.HasDraft) throw new InvalidOperationException("Clear the filter and Apply/Cancel metadata before dragging record order.");
                if (local.Revision != session.Revision) throw new InvalidOperationException("Drag source changed. Try again.");
                var row = RecordsTable.GetVisualDescendants().OfType<TableViewRow>().FirstOrDefault(r => r.TranslatePoint(default,RecordsTable) is { } p && position.Y >= p.Y && position.Y < p.Y+r.Bounds.Height);
                int insertion = row?.DataContext is RecordRow record ? record.Index + (position.Y > row.TranslatePoint(default,RecordsTable)!.Value.Y+row.Bounds.Height/2 ? 1 : 0) : session.Model.Records.Count;
                session.Reorder(local.Indices,insertion);
            }
            else await Model.PasteAsync(packet);
            return true;
        });
    }
    private async void ExportRegion(object? sender, RoutedEventArgs e)
    {
        if (Model is null || observed?.Snapshot is not { } snapshot || observed.SelectedRegion is not { } region) return;
        var owner = TopLevel.GetTopLevel(this)!;
        await Model.Documents.ExecuteAsync(async ct =>
        {
            var bytes = snapshot.Layout.GetBlockBytes(region);
            if (Model.MediaDialogs is null || !await Model.MediaDialogs.ConfirmPreviewAsync("Region export", snapshot.Layout.Detail(region) + $"\n{bytes.Length:N0} output bytes. Decoded blocks and raw gaps are distinct; this does not rewrite the medium.")) return false;
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export decoded/raw region", SuggestedFileName = "region.bin", ShowOverwritePrompt = false });
            if (file is null) return false;
            await Model.Documents.WriteExportAsync(new Dictionary<string,byte[]> { [file.TryGetLocalPath() ?? throw new NotSupportedException("A local path is required.")] = bytes },ct); return true;
        });
    }
}
