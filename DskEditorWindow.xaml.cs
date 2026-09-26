using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace QDTool
{
    public partial class DskEditorControl : UserControl
    {
        private const string MultiIplRowDragDataFormat = "QDTool.MultiIplEditorRows";
        private DskDocument? document;
        private readonly ObservableCollection<MultiGameIplRow> multiIplRows = new();
        private CancellationTokenSource? multiIplCancellation;
        private bool multiIplEditorMode;
        private bool singleIplEditorMode;
        private bool multiIplDraftModified;
        private bool multiIplLayoutValid;
        private bool suppressMultiIplChanges;
        private Point multiIplDragStartPoint;
        private MultiGameIplRow? multiIplDragStartItem;
        private bool multiIplDragInProgress;
        private DataGridRow? multiIplDropTarget;
        private List<MultiGameIplRow>? multiIplCompressionTargets;

        public DskEditorControl()
        {
            InitializeComponent();
            multiIplGrid.ItemsSource = multiIplRows;
        }

        internal event EventHandler? DocumentStateChanged;
        internal event EventHandler? CloseRequested;
        internal event EventHandler? OpenRequested;
        internal event EventHandler? NewQuickDiskRequested;
        internal event EventHandler? NewDskRequested;
        private bool IplEditorMode => multiIplEditorMode || singleIplEditorMode;
        internal bool HasDocument => document != null || IplEditorMode;
        private DskDocument CurrentDocument => document ?? throw new InvalidOperationException("No DSK document is loaded.");
        internal string DocumentTitle => document == null
            ? multiIplEditorMode
                ? $"MZTools - new multi-program IPL DSK{(multiIplDraftModified ? " *" : string.Empty)}"
                : singleIplEditorMode
                    ? $"MZTools - new single-program IPL DSK{(multiIplDraftModified ? " *" : string.Empty)}"
                    : "MZTools"
            : $"MZTools - {Path.GetFileName(document.FilePath) ?? "new image"}{(document.IsModified || IplEditorMode && multiIplDraftModified ? " *" : string.Empty)}";

        internal void LoadDocument(DskDocument value)
        {
            CancelMultiIplRefresh();
            warningText.Text = string.Empty;
            document = value;
            dskShowSectorsCheckBox.IsChecked = false;
            if (value.FileSystem is MultiGameIplFileSystem multiIpl && multiIpl.CanConfigure)
            {
                LoadMultiIplRows(multiIpl.GetInputs(), multiIpl.ReadDirectory());
                multiIplEditorMode = true;
                singleIplEditorMode = false;
                multiIplLayoutValid = true;
            }
            else if (value.FileSystem is SingleGameIplFileSystem singleIpl)
            {
                LoadSingleIplRow(singleIpl.GetRecord(), singleIpl.BootName, singleIpl.ReadDirectory()[0]);
                multiIplEditorMode = false;
                singleIplEditorMode = true;
                multiIplLayoutValid = true;
            }
            else
            {
                multiIplEditorMode = false;
                singleIplEditorMode = false;
                multiIplLayoutValid = false;
                multiIplRows.Clear();
            }
            multiIplDraftModified = false;
            ConfigureColumns();
            RefreshView();
        }

        internal void LoadNewMultiIpl()
        {
            CancelMultiIplRefresh();
            warningText.Text = string.Empty;
            document = null;
            multiIplEditorMode = true;
            singleIplEditorMode = false;
            multiIplDraftModified = true;
            multiIplLayoutValid = false;
            suppressMultiIplChanges = true;
            multiIplRows.Clear();
            suppressMultiIplChanges = false;
            ConfigureColumns();
            RefreshView();
        }

        internal void LoadNewSingleIpl()
        {
            CancelMultiIplRefresh();
            warningText.Text = string.Empty;
            document = null;
            multiIplEditorMode = false;
            singleIplEditorMode = true;
            multiIplDraftModified = true;
            multiIplLayoutValid = false;
            suppressMultiIplChanges = true;
            foreach (MultiGameIplRow row in multiIplRows) row.PropertyChanged -= MultiIplRow_PropertyChanged;
            multiIplRows.Clear();
            suppressMultiIplChanges = false;
            ConfigureColumns();
            RefreshView();
        }

        private void LoadSingleIplRow(TapeRecord record, string bootName, DskFileEntry entry)
        {
            suppressMultiIplChanges = true;
            foreach (MultiGameIplRow existing in multiIplRows) existing.PropertyChanged -= MultiIplRow_PropertyChanged;
            multiIplRows.Clear();
            var row = new MultiGameIplRow(1, record, bootName);
            row.PropertyChanged += MultiIplRow_PropertyChanged;
            row.ApplySingle(entry.StartBlock, entry.Blocks, entry.Notes);
            multiIplRows.Add(row);
            suppressMultiIplChanges = false;
        }

        private void LoadMultiIplRows(
            IReadOnlyList<MultiGameIplInput> inputs,
            IReadOnlyList<DskFileEntry> entries)
        {
            suppressMultiIplChanges = true;
            foreach (MultiGameIplRow row in multiIplRows) row.PropertyChanged -= MultiIplRow_PropertyChanged;
            multiIplRows.Clear();
            for (int index = 0; index < inputs.Count; index++)
            {
                var row = new MultiGameIplRow(index + 1, inputs[index]);
                row.PropertyChanged += MultiIplRow_PropertyChanged;
                DskFileEntry entry = entries[index];
                byte compressionCode = inputs[index].AppliedCompression.Algorithm switch
                {
                    MzfCompressionAlgorithm.Zx0 => 1,
                    MzfCompressionAlgorithm.Zx7 => 2,
                    _ => 0
                };
                row.Apply(new PreparedMultiGameIplEntry(
                    entry.Name,
                    checked((ushort)entry.StartBlock),
                    checked((ushort)entry.Size),
                    entry.LoadAddress,
                    entry.ExecuteAddress,
                    entry.Blocks,
                    compressionCode == 0 ? (byte)0 : (byte)1,
                    compressionCode,
                    entry.Notes,
                    inputs[index].OriginalSize,
                    inputs[index].Record.Body.MzfBody));
                multiIplRows.Add(row);
            }
            suppressMultiIplChanges = false;
        }

        private IReadOnlyList<DskFileEntry> SelectedEntries => directoryGrid.SelectedItems.Cast<DskFileEntry>().ToList();

        private void ConfigureColumns()
        {
            directoryGrid.Visibility = IplEditorMode ? Visibility.Collapsed : Visibility.Visible;
            multiIplGrid.Visibility = IplEditorMode ? Visibility.Visible : Visibility.Collapsed;
            if (document == null) return;
            directoryGrid.Columns.Clear();
            bool canRename = !document.IsReadOnly && document.FileSystem.Type is
                DskFileSystemType.Fsmz or DskFileSystemType.Cpm or DskFileSystemType.Mrs;
            void Add(string header, string property, bool editable = false) => directoryGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit },
                Width = DataGridLength.Auto,
                IsReadOnly = !editable,
                SortMemberPath = property
            });
            switch (document.FileSystem.Type)
            {
                case DskFileSystemType.SingleIpl:
                    Add("Program", nameof(DskFileEntry.Name)); Add("Size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress)); Add("Exec", nameof(DskFileEntry.ExecuteAddress));
                    Add("Start block", nameof(DskFileEntry.StartBlock)); Add("Blocks", nameof(DskFileEntry.Blocks)); Add("Type", nameof(DskFileEntry.Notes));
                    break;
                case DskFileSystemType.MultiIpl:
                    Add("Game", nameof(DskFileEntry.Name)); Add("Size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress)); Add("Exec", nameof(DskFileEntry.ExecuteAddress));
                    Add("Start block", nameof(DskFileEntry.StartBlock)); Add("Blocks", nameof(DskFileEntry.Blocks)); Add("Compression", nameof(DskFileEntry.Notes));
                    break;
                case DskFileSystemType.Fsmz:
                    Add("Name", nameof(DskFileEntry.Name), canRename); Add("Type", nameof(DskFileEntry.FileType)); Add("Size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress)); Add("Exec", nameof(DskFileEntry.ExecuteAddress)); Add("Start block", nameof(DskFileEntry.StartBlock)); Add("Locked", nameof(DskFileEntry.Locked));
                    break;
                case DskFileSystemType.Cpm:
                    Add("User", nameof(DskFileEntry.User)); Add("Name", nameof(DskFileEntry.Name), canRename); Add("Ext", nameof(DskFileEntry.Extension), canRename); Add("Size", nameof(DskFileEntry.Size));
                    Add("RO", nameof(DskFileEntry.ReadOnly)); Add("SYS", nameof(DskFileEntry.System)); Add("ARC", nameof(DskFileEntry.Archived)); Add("Extents", nameof(DskFileEntry.Extents)); Add("Blocks", nameof(DskFileEntry.Blocks));
                    break;
                case DskFileSystemType.Mrs:
                    Add("Name", nameof(DskFileEntry.Name), canRename); Add("Ext", nameof(DskFileEntry.Extension), canRename); Add("Blocks", nameof(DskFileEntry.Blocks)); Add("Approx. size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress)); Add("Exec", nameof(DskFileEntry.ExecuteAddress)); Add("File ID", nameof(DskFileEntry.StartBlock)); Add("Note", nameof(DskFileEntry.Notes));
                    break;
                case DskFileSystemType.BootOnly:
                    Add("Program / data", nameof(DskFileEntry.Name)); Add("Size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress)); Add("Exec", nameof(DskFileEntry.ExecuteAddress)); Add("Start block", nameof(DskFileEntry.StartBlock));
                    Add("Blocks", nameof(DskFileEntry.Blocks)); Add("Type", nameof(DskFileEntry.Notes));
                    break;
                default:
                    Add("Track", nameof(DskFileEntry.Name)); Add("Sector", nameof(DskFileEntry.Extension)); Add("Size", nameof(DskFileEntry.Size)); Add("Physical index", nameof(DskFileEntry.Blocks)); Add("Status", nameof(DskFileEntry.Notes));
                    break;
            }
        }

        private void RefreshView()
        {
            if (IplEditorMode)
            {
                RefreshMultiIplView();
                return;
            }
            if (document == null) return;
            IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();
            bool bootOnly = document.FileSystem.Type == DskFileSystemType.BootOnly;
            bool showRawSectors = dskShowSectorsCheckBox.IsChecked == true;
            directoryGrid.ItemsSource = bootOnly && !showRawSectors
                ? entries.Where(entry => !IsRawSectorEntry(entry)).ToList()
                : entries;
            dskShowSectorsCheckBox.Visibility = bootOnly ? Visibility.Visible : Visibility.Collapsed;
            DskImage image = document.Image;
            string sizes = string.Join(", ", image.Tracks.Where(track => track != null).SelectMany(track => track!.Sectors).Select(sector => sector.Data.Length).Distinct().Order());
            infoText.Text = $"Container: Extended CPC DSK | Creator: {image.Creator} | Tracks: {image.TrackCount} | Sides: {image.SideCount} | Physical tracks: {image.Tracks.Count} | Image: {image.Serialize().Length:N0} B\n" +
                $"Filesystem: {document.FileSystem.DisplayName} | Sector sizes: {sizes} B | Used: {document.FileSystem.UsedBytes:N0} B | Free: {document.FileSystem.FreeBytes:N0} B";
            warningText.Text = document.FileSystem.Warnings.Count == 0 ? string.Empty : string.Join("  ", document.FileSystem.Warnings);
            bool writable = !document.IsReadOnly;
            bool rawMode = document.FileSystem is RawDskFileSystem;
            MultiGameIplFileSystem? multiIpl = document.FileSystem as MultiGameIplFileSystem;
            dskAddButton.Content = multiIpl != null ? "Configure..." : "Add...";
            dskAddButton.IsEnabled = multiIpl?.CanConfigure == true || writable && (!bootOnly || showRawSectors);
            dskDeleteButton.IsEnabled = writable && !rawMode;
            dskMoveButtons.IsEnabled = false;
            dskSaveButton.IsEnabled = document.FilePath != null;
            dskSaveAsButton.IsEnabled = true;
            multiIplProgressBar.Visibility = Visibility.Collapsed;
            DocumentStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshMultiIplView()
        {
            directoryGrid.Visibility = Visibility.Collapsed;
            multiIplGrid.Visibility = Visibility.Visible;
            dskShowSectorsCheckBox.Visibility = Visibility.Collapsed;
            dskAddButton.Content = singleIplEditorMode && multiIplRows.Count > 0 ? "Replace..." : "Add...";
            dskAddButton.IsEnabled = true;
            dskDeleteButton.IsEnabled = multiIplGrid.SelectedItems.Count > 0;
            dskMoveButtons.IsEnabled = multiIplEditorMode;
            UpdateIplSaveButtons();
            if (document == null)
            {
                string kind = singleIplEditorMode ? "single-program" : "multi-program";
                infoText.Text = $"Container: Extended CPC DSK | New MZTools {kind} IPL | Capacity: {Mz800DskImage.LogicalSectorCount * Mz800DskImage.SectorSize:N0} B\n" +
                    "Add MZF programs, edit their menu names and choose compression directly in this table.";
                warningText.Text = multiIplRows.Count == 0
                    ? singleIplEditorMode ? "Add one program before saving the image." : "Add at least one program before saving the image."
                    : string.Empty;
            }
            else
            {
                DskImage image = document.Image;
                infoText.Text = $"Container: Extended CPC DSK | Creator: {image.Creator} | Tracks: {image.TrackCount} | Sides: {image.SideCount} | Image: {image.Serialize().Length:N0} B\n" +
                    $"Filesystem: {document.FileSystem.DisplayName} | Programs: {multiIplRows.Count} | Used: {document.FileSystem.UsedBytes:N0} B | Free: {document.FileSystem.FreeBytes:N0} B";
            }
            DocumentStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateIplSaveButtons(bool busy = false)
        {
            (bool save, bool saveAs) = GetIplSaveAvailability(
                busy,
                document != null,
                document?.FilePath != null,
                multiIplRows.Count,
                singleIplEditorMode,
                multiIplLayoutValid);
            dskSaveButton.IsEnabled = save;
            dskSaveAsButton.IsEnabled = saveAs;
        }

        internal static (bool Save, bool SaveAs) GetIplSaveAvailability(
            bool busy,
            bool hasDocument,
            bool hasPath,
            int programCount,
            bool single,
            bool layoutValid)
        {
            bool validCount = single ? programCount == 1 : programCount > 0;
            bool canSave = !busy && hasDocument && validCount && layoutValid;
            return (canSave && hasPath, canSave);
        }

        private void ShowSectors_Changed(object sender, RoutedEventArgs e)
        {
            if (document != null) RefreshView();
        }

        private static bool IsRawSectorEntry(DskFileEntry entry)
        {
            int separator = entry.Key.IndexOf(':');
            return separator > 0 && int.TryParse(entry.Key.AsSpan(0, separator), out _);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode)
            {
                AddIplPrograms();
                return;
            }
            if (document == null) return;
            if (!EnsureWritable()) return;
            if (document.FileSystem is RawDskFileSystem raw)
            {
                if (SelectedEntries.Count != 1) { ShowError("Select exactly one sector to replace."); return; }
                var sectorDialog = new OpenFileDialog { Filter = "Raw sector data|*.bin;*.*|All files|*.*" };
                if (sectorDialog.ShowDialog(OwnerWindow) != true) return;
                try { raw.ReplaceSector(SelectedEntries[0], File.ReadAllBytes(sectorDialog.FileName)); document.MarkModified(); RefreshView(); }
                catch (Exception exception) { ShowError(exception.Message); }
                return;
            }
            var dialog = new OpenFileDialog { Filter = "MZF and binary files|*.mzf;*.bin;*.*|All files|*.*", Multiselect = true };
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            try
            {
                foreach (string path in dialog.FileNames)
                {
                    ImportPath(path);
                    document.MarkModified();
                }
                RefreshView();
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private async void AddIplPrograms()
        {
            var dialog = new OpenFileDialog
            {
                Title = singleIplEditorMode ? "Select program for single-program IPL DSK" : "Add programs to multi-program IPL DSK",
                Filter = "MZF program (*.mzf;*.mz0;*.mz7)|*.mzf;*.mz0;*.mz7|All files (*.*)|*.*",
                Multiselect = multiIplEditorMode
            };
            if (dialog.ShowDialog(OwnerWindow) != true)
            {
                return;
            }
            try
            {
                if (singleIplEditorMode)
                {
                    foreach (MultiGameIplRow existing in multiIplRows) existing.PropertyChanged -= MultiIplRow_PropertyChanged;
                    multiIplRows.Clear();
                }
                foreach (string path in dialog.FileNames)
                {
                    TapeRecord record = new MZTFileReader().ReadStandaloneMzf(path);
                    string name = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);
                    var row = new MultiGameIplRow(multiIplRows.Count + 1, record, name);
                    row.PropertyChanged += MultiIplRow_PropertyChanged;
                    multiIplRows.Add(row);
                }
                multiIplDraftModified = true;
                await RebuildIplAsync();
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode)
            {
                MultiGameIplRow[] selected = multiIplGrid.SelectedItems
                    .OfType<MultiGameIplRow>()
                    .OrderBy(row => multiIplRows.IndexOf(row))
                    .ToArray();
                if (selected.Length == 0) return;
                if (OwnerWindow is not MainWindow mainWindow)
                {
                    ShowError("The IPL export service is unavailable.");
                    return;
                }

                mainWindow.ExportIplRecords(
                    selected.Select(row => row.GetPreparedRecord()).ToList(),
                    SanitizeFileName(selected[0].MenuName) + ".mzf");
                return;
            }

            if (document == null) return;
            IReadOnlyList<DskFileEntry> entries = SelectedEntries;
            if (entries.Count == 0) return;
            var dialog = new SaveFileDialog { Filter = "Raw file|*.bin|MZF file|*.mzf", FileName = SuggestedName(entries[0]) };
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            try
            {
                string folder = Path.GetDirectoryName(dialog.FileName)!;
                for (int index = 0; index < entries.Count; index++)
                {
                    DskFileEntry entry = entries[index];
                    string path = entries.Count == 1 ? dialog.FileName : Path.Combine(folder, SuggestedName(entry));
                    byte[] data = document.FileSystem.Extract(entry);
                    bool bootstrap = document.FileSystem.Type == DskFileSystemType.BootOnly && entry.Key == "iplpro";
                    if (Path.GetExtension(dialog.FileName).Equals(".mzf", StringComparison.OrdinalIgnoreCase) &&
                        (bootstrap || document.FileSystem.Type is DskFileSystemType.SingleIpl or DskFileSystemType.MultiIpl or DskFileSystemType.Fsmz or DskFileSystemType.Mrs))
                        File.WriteAllBytes(Path.ChangeExtension(path, ".mzf"), DskMzfConverter.Serialize(entry, data));
                    else File.WriteAllBytes(path, data);
                }
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode)
            {
                DeleteIplRows();
                return;
            }
            if (document == null) return;
            if (!EnsureWritable()) return;
            IReadOnlyList<DskFileEntry> entries = SelectedEntries;
            if (entries.Count == 0 || MessageBox.Show(OwnerWindow, $"Delete {entries.Count} selected item(s)?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { foreach (DskFileEntry entry in entries) { document.FileSystem.Delete(entry, force: true); document.MarkModified(); } RefreshView(); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private async void DeleteIplRows()
        {
            MultiGameIplRow[] selected = multiIplGrid.SelectedItems.OfType<MultiGameIplRow>().ToArray();
            if (selected.Length == 0) return;
            if (multiIplEditorMode && selected.Length == multiIplRows.Count)
            {
                ShowError("A multi-program IPL image must contain at least one program.");
                return;
            }
            foreach (MultiGameIplRow row in selected)
            {
                row.PropertyChanged -= MultiIplRow_PropertyChanged;
                multiIplRows.Remove(row);
            }
            UpdateMultiIplOrder();
            multiIplDraftModified = true;
            await RebuildIplAsync();
        }

        private async void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            int[] indices = GetSelectedMultiIplIndices();
            if (indices.Length == 0 || indices[0] == 0) return;
            MultiGameIplRow[] selected = indices.Select(index => multiIplRows[index]).ToArray();
            if (!MoveMultiIplRows(indices, indices[0] - 1)) return;
            UpdateMultiIplOrder();
            RestoreMultiIplSelection(selected);
            multiIplDraftModified = true;
            await RebuildIplAsync();
            RestoreMultiIplSelection(selected);
        }

        private async void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            int[] indices = GetSelectedMultiIplIndices();
            if (indices.Length == 0 || indices[^1] >= multiIplRows.Count - 1) return;
            MultiGameIplRow[] selected = indices.Select(index => multiIplRows[index]).ToArray();
            if (!MoveMultiIplRows(indices, indices[^1] + 2)) return;
            UpdateMultiIplOrder();
            RestoreMultiIplSelection(selected);
            multiIplDraftModified = true;
            await RebuildIplAsync();
            RestoreMultiIplSelection(selected);
        }

        private async void MultiIplRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (suppressMultiIplChanges ||
                (e.PropertyName == nameof(MultiGameIplRow.Compression) && multiIplCompressionTargets != null) ||
                e.PropertyName is not (nameof(MultiGameIplRow.MenuName) or nameof(MultiGameIplRow.Compression)))
            {
                return;
            }
            multiIplDraftModified = true;
            await RebuildIplAsync();
        }

        private async Task RebuildIplAsync()
        {
            CancelMultiIplRefresh();
            if (multiIplRows.Count == 0)
            {
                multiIplLayoutValid = false;
                multiIplProgressBar.Visibility = Visibility.Collapsed;
                RefreshMultiIplView();
                return;
            }
            if (singleIplEditorMode && multiIplRows.Count != 1)
            {
                multiIplLayoutValid = false;
                warningText.Text = "A single-program IPL image must contain exactly one program.";
                RefreshMultiIplView();
                return;
            }

            multiIplCancellation = new CancellationTokenSource();
            CancellationTokenSource cancellation = multiIplCancellation;
            CancellationToken token = cancellation.Token;
            UpdateIplSaveButtons(busy: true);
            multiIplLayoutValid = false;
            DocumentStateChanged?.Invoke(this, EventArgs.Empty);
            warningText.Text = $"Preparing 0/{multiIplRows.Count} programs...";
            multiIplProgressBar.Visibility = Visibility.Visible;
            try
            {
                var inputs = new List<MultiGameIplInput>(multiIplRows.Count);
                for (int index = 0; index < multiIplRows.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    MultiGameIplRow row = multiIplRows[index];
                    warningText.Text = $"Analyzing {index + 1}/{multiIplRows.Count}: {row.MenuName} ({row.Compression})...";
                    await Task.Yield();
                    MzfCompressionResult result = await row.PrepareAsync(token);
                    inputs.Add(new MultiGameIplInput(result.Record, row.MenuName, result.AppliedOptions, result.OriginalSize));
                }

                byte[] imageBytes;
                if (singleIplEditorMode)
                {
                    MultiGameIplInput input = inputs[0];
                    string bootName = Mz800IplDskWriter.NormalizeBootName(input.DisplayName);
                    imageBytes = await Task.Run(
                        () => Mz800IplDskWriter.Build(input.Record, bootName),
                        token);
                    token.ThrowIfCancellationRequested();
                    int sectors = (input.Record.Body.MzfBody.Length + Mz800IplDskWriter.SectorSize - 1) / Mz800IplDskWriter.SectorSize;
                    suppressMultiIplChanges = true;
                    multiIplRows[0].MenuName = bootName;
                    multiIplRows[0].ApplySingle(1, sectors, FormatAppliedCompression(input.AppliedCompression));
                    suppressMultiIplChanges = false;
                }
                else
                {
                    MultiGameIplBuildResult build = await Task.Run(() => Mz800MultiGameIplDskWriter.Build(inputs), token);
                    token.ThrowIfCancellationRequested();
                    imageBytes = build.Image;
                    suppressMultiIplChanges = true;
                    for (int index = 0; index < multiIplRows.Count; index++) multiIplRows[index].Apply(build.Entries[index]);
                    suppressMultiIplChanges = false;
                }

                if (document == null)
                {
                    document = DskDocument.Open(imageBytes);
                }
                document.ReplaceContents(imageBytes);
                multiIplDraftModified = true;
                multiIplLayoutValid = true;
                warningText.Text = string.Empty;
                RefreshMultiIplView();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    warningText.Text = exception.GetBaseException().Message;
                    UpdateIplSaveButtons();
                    DocumentStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                suppressMultiIplChanges = false;
                if (ReferenceEquals(multiIplCancellation, cancellation))
                {
                    multiIplCancellation = null;
                    multiIplProgressBar.Visibility = Visibility.Collapsed;
                    UpdateIplSaveButtons();
                    DocumentStateChanged?.Invoke(this, EventArgs.Empty);
                }
                cancellation.Dispose();
            }
        }

        private void CancelMultiIplRefresh()
        {
            multiIplCancellation?.Cancel();
            multiIplCancellation?.Dispose();
            multiIplCancellation = null;
            multiIplProgressBar.Visibility = Visibility.Collapsed;
        }

        private int[] GetSelectedMultiIplIndices() => multiIplGrid.SelectedItems
            .OfType<MultiGameIplRow>()
            .Select(row => multiIplRows.IndexOf(row))
            .Where(index => index >= 0)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

        private bool MoveMultiIplRows(IEnumerable<int> sourceIndices, int insertionIndex)
        {
            int[] indices = sourceIndices.Distinct().OrderBy(index => index).ToArray();
            List<MultiGameIplRow> desired = multiIplRows.ToList();
            if (!ListReorder.MoveItems(desired, indices, insertionIndex)) return false;
            for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
            {
                int currentIndex = multiIplRows.IndexOf(desired[targetIndex]);
                if (currentIndex != targetIndex) multiIplRows.Move(currentIndex, targetIndex);
            }
            return true;
        }

        private void UpdateMultiIplOrder()
        {
            suppressMultiIplChanges = true;
            for (int index = 0; index < multiIplRows.Count; index++) multiIplRows[index].Order = index + 1;
            suppressMultiIplChanges = false;
        }

        private void RestoreMultiIplSelection(IEnumerable<MultiGameIplRow> rows)
        {
            multiIplGrid.SelectedItems.Clear();
            foreach (MultiGameIplRow row in rows) multiIplGrid.SelectedItems.Add(row);
            if (multiIplGrid.SelectedItems.Count > 0)
            {
                multiIplGrid.ScrollIntoView(multiIplGrid.SelectedItems[0]);
                multiIplGrid.Focus();
            }
        }

        private void MultiIplGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IplEditorMode) dskDeleteButton.IsEnabled = multiIplGrid.SelectedItems.Count > 0;
        }

        private void MultiIplCompression_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ComboBox combo || combo.DataContext is not MultiGameIplRow clickedRow) return;
            List<MultiGameIplRow> selected = multiIplGrid.SelectedItems
                .OfType<MultiGameIplRow>()
                .Where(row => row.CanChangeCompression)
                .ToList();
            multiIplCompressionTargets = selected.Contains(clickedRow) && selected.Count > 0
                ? selected
                : clickedRow.CanChangeCompression ? [clickedRow] : null;
        }

        private async void MultiIplCompression_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressMultiIplChanges || sender is not ComboBox combo ||
                combo.DataContext is not MultiGameIplRow sourceRow || combo.SelectedItem is not string compression ||
                (!combo.IsKeyboardFocusWithin && multiIplCompressionTargets == null))
            {
                return;
            }

            List<MultiGameIplRow> targets = multiIplCompressionTargets ?? multiIplGrid.SelectedItems
                .OfType<MultiGameIplRow>()
                .Where(row => row.CanChangeCompression)
                .ToList();
            if (!targets.Contains(sourceRow) && sourceRow.CanChangeCompression) targets = [sourceRow];
            multiIplCompressionTargets = targets;
            suppressMultiIplChanges = true;
            foreach (MultiGameIplRow row in targets) row.Compression = compression;
            suppressMultiIplChanges = false;
            multiIplCompressionTargets = null;
            multiIplDraftModified = true;
            await RebuildIplAsync();
            RestoreMultiIplSelection(targets);
        }

        private void MultiIplCompression_DropDownClosed(object? sender, EventArgs e) =>
            multiIplCompressionTargets = null;

        private void MultiIplGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            multiIplDragStartPoint = e.GetPosition(multiIplGrid);
            DependencyObject? source = e.OriginalSource as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(source);
            bool editorClicked = FindVisualParent<ComboBox>(source) is not null ||
                FindVisualParent<TextBox>(source) is not null;
            multiIplDragStartItem = editorClicked ? null : row?.Item as MultiGameIplRow;
        }

        private void MultiIplGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!multiIplEditorMode || e.LeftButton != MouseButtonState.Pressed || multiIplDragStartItem == null || multiIplDragInProgress) return;
            Point current = e.GetPosition(multiIplGrid);
            if (Math.Abs(current.X - multiIplDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - multiIplDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            List<MultiGameIplRow> rows = multiIplGrid.SelectedItems
                .OfType<MultiGameIplRow>()
                .OrderBy(row => multiIplRows.IndexOf(row))
                .ToList();
            if (!rows.Contains(multiIplDragStartItem)) rows = [multiIplDragStartItem];
            var data = new DataObject();
            data.SetData(MultiIplRowDragDataFormat, rows);
            multiIplDragInProgress = true;
            try { DragDrop.DoDragDrop(multiIplGrid, data, DragDropEffects.Move); }
            finally
            {
                ClearMultiIplDropTarget();
                multiIplDragInProgress = false;
                multiIplDragStartItem = null;
            }
        }

        private void MultiIplGrid_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(MultiIplRowDragDataFormat))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            Point position = e.GetPosition(multiIplGrid);
            UpdateMultiIplDropTarget(position);
            ScrollMultiIplDuringDrag(position);
        }

        private void MultiIplGrid_DragLeave(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(MultiIplRowDragDataFormat)) ClearMultiIplDropTarget();
        }

        private async void MultiIplGrid_Drop(object sender, DragEventArgs e)
        {
            if (!multiIplEditorMode || !e.Data.GetDataPresent(MultiIplRowDragDataFormat) ||
                e.Data.GetData(MultiIplRowDragDataFormat) is not List<MultiGameIplRow> rows) return;
            // Mark the routed event handled before the first await. Otherwise it bubbles
            // to Control_Drop, which interprets it as a filesystem import into read-only QDMG.
            e.Handled = true;
            int insertionIndex = GetMultiIplDropInsertionIndex(e.GetPosition(multiIplGrid));
            ClearMultiIplDropTarget();
            int[] indices = rows.Select(row => multiIplRows.IndexOf(row))
                .Where(index => index >= 0).Distinct().OrderBy(index => index).ToArray();
            if (MoveMultiIplRows(indices, insertionIndex))
            {
                UpdateMultiIplOrder();
                RestoreMultiIplSelection(rows);
                multiIplDraftModified = true;
                await RebuildIplAsync();
                RestoreMultiIplSelection(rows);
            }
        }

        private int GetMultiIplDropInsertionIndex(Point position)
        {
            DependencyObject? hit = multiIplGrid.InputHitTest(position) as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(hit);
            if (row == null) return multiIplRows.Count;
            Point inRow = multiIplGrid.TranslatePoint(position, row);
            return row.GetIndex() + (inRow.Y >= row.ActualHeight / 2 ? 1 : 0);
        }

        private void UpdateMultiIplDropTarget(Point position)
        {
            ClearMultiIplDropTarget();
            DependencyObject? hit = multiIplGrid.InputHitTest(position) as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(hit);
            bool after = false;
            if (row != null)
            {
                Point inRow = multiIplGrid.TranslatePoint(position, row);
                after = inRow.Y >= row.ActualHeight / 2;
            }
            else if (multiIplRows.Count > 0)
            {
                row = multiIplGrid.ItemContainerGenerator.ContainerFromIndex(multiIplRows.Count - 1) as DataGridRow;
                after = true;
            }
            if (row == null) return;
            multiIplDropTarget = row;
            row.BorderBrush = SystemColors.HighlightBrush;
            row.BorderThickness = after ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0);
        }

        private void ClearMultiIplDropTarget()
        {
            if (multiIplDropTarget == null) return;
            multiIplDropTarget.ClearValue(Control.BorderBrushProperty);
            multiIplDropTarget.ClearValue(Control.BorderThicknessProperty);
            multiIplDropTarget = null;
        }

        private void ScrollMultiIplDuringDrag(Point position)
        {
            ScrollViewer? viewer = FindVisualChild<ScrollViewer>(multiIplGrid);
            if (viewer == null) return;
            const double edge = 24;
            if (position.Y < edge) viewer.LineUp();
            else if (position.Y > multiIplGrid.ActualHeight - edge) viewer.LineDown();
        }

        private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                T? descendant = FindVisualChild<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }

        private void DirectoryGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            e.Cancel = document == null || document.IsReadOnly ||
                e.Column.SortMemberPath is not (nameof(DskFileEntry.Name) or nameof(DskFileEntry.Extension));
        }

        private void DirectoryGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || document == null ||
                e.Column.SortMemberPath is not (nameof(DskFileEntry.Name) or nameof(DskFileEntry.Extension)) ||
                e.Row.Item is not DskFileEntry entry || e.EditingElement is not TextBox editor)
            {
                return;
            }

            string editedValue = editor.Text.Trim();
            string baseName = e.Column.SortMemberPath == nameof(DskFileEntry.Name) ? editedValue : entry.Name;
            string extension = e.Column.SortMemberPath == nameof(DskFileEntry.Extension) ? editedValue : entry.Extension;
            string newName = document.FileSystem.Type is DskFileSystemType.Cpm or DskFileSystemType.Mrs && extension.Length > 0
                ? $"{baseName}.{extension}"
                : baseName;
            try
            {
                document.FileSystem.Rename(entry, newName);
                document.MarkModified();
                Dispatcher.BeginInvoke(new Action(RefreshView));
            }
            catch (Exception exception)
            {
                e.Cancel = true;
                ShowError(exception.Message);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    directoryGrid.CancelEdit(DataGridEditingUnit.Cell);
                    RefreshView();
                }));
            }
        }

        private void DirectoryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F2 || directoryGrid.SelectedItem is not DskFileEntry entry)
            {
                return;
            }
            DataGridColumn? nameColumn = directoryGrid.Columns.FirstOrDefault(column =>
                !column.IsReadOnly && column.SortMemberPath == nameof(DskFileEntry.Name));
            if (nameColumn == null)
            {
                return;
            }
            directoryGrid.CurrentCell = new DataGridCellInfo(entry, nameColumn);
            e.Handled = directoryGrid.BeginEdit();
        }

        private void Hex_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode)
            {
                MultiGameIplRow[] selected = multiIplGrid.SelectedItems.OfType<MultiGameIplRow>().ToArray();
                if (selected.Length != 1) { ShowError("Select exactly one program for Hex view."); return; }
                var multiBrowser = new HexBrowser { Owner = OwnerWindow };
                multiBrowser.ShowRawData($"{selected[0].MenuName}.BIN", selected[0].GetPreparedPayload());
                multiBrowser.Show();
                return;
            }
            if (document == null) return;
            if (SelectedEntries.Count != 1) return;
            try
            {
                DskFileEntry entry = SelectedEntries[0]; byte[] logical = document.FileSystem.Extract(entry);
                var browser = new HexBrowser { Owner = OwnerWindow }; browser.ShowRawData(SuggestedName(entry), logical); browser.Show();
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode && !multiIplLayoutValid) { ShowError("Wait for a valid IPL layout before saving."); return; }
            if (document == null) { ShowError("Add at least one program before saving the image."); return; }
            if (document.FilePath == null) { SaveAs_Click(sender, e); return; }
            try { document.Save(); multiIplDraftModified = false; RefreshView(); } catch (Exception exception) { ShowError(exception.Message); }
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode && !multiIplLayoutValid) { ShowError("Wait for a valid IPL layout before saving."); return; }
            if (document == null) { ShowError("Add at least one program before saving the image."); return; }
            var dialog = new SaveFileDialog { Filter = "Extended CPC DSK|*.dsk", FileName = Path.GetFileName(document.FilePath) ?? "disk.dsk" };
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            try { document.Save(dialog.FileName); multiIplDraftModified = false; RefreshView(); } catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

        private void NewQuickDisk_Click(object sender, RoutedEventArgs e) => NewQuickDiskRequested?.Invoke(this, EventArgs.Empty);

        private void NewDsk_Click(object sender, RoutedEventArgs e) => NewDskRequested?.Invoke(this, EventArgs.Empty);

        private void Control_Drop(object sender, DragEventArgs e)
        {
            if (e.Handled || e.Data.GetDataPresent(MultiIplRowDragDataFormat))
            {
                e.Handled = true;
                return;
            }
            if (document == null) return;
            if (!EnsureWritable()) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (document.FileSystem is RawDskFileSystem) { ShowError("Use Add with one selected sector to replace raw sector data."); return; }
            try { foreach (string path in files) { ImportPath(path); document.MarkModified(); } RefreshView(); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void ImportPath(string path)
        {
            if (Path.GetExtension(path).Equals(".mzf", StringComparison.OrdinalIgnoreCase))
            {
                TapeRecord record = new MZTFileReader().ReadStandaloneMzf(path);
                string embeddedName = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);
                string importName = GetImportName(CurrentDocument.FileSystem.Type, path, embeddedName);
                CurrentDocument.FileSystem.Insert(importName, record.Body.MzfBody,
                    record.Header.MzfFtype, record.Header.MzfStart, record.Header.MzfExec);
            }
            else
            {
                CurrentDocument.FileSystem.Insert(Path.GetFileName(path), File.ReadAllBytes(path));
            }
        }

        internal static string GetImportName(DskFileSystemType fileSystemType, string path, string embeddedMzfName)
        {
            if (fileSystemType is not (DskFileSystemType.Cpm or DskFileSystemType.Mrs))
            {
                return embeddedMzfName.Trim();
            }

            string sourceName = Path.GetFileNameWithoutExtension(path);
            string sourceExtension = Path.GetExtension(path).TrimStart('.');
            string baseName = SanitizeCpmNamePart(sourceName, 8);
            string extension = SanitizeCpmNamePart(sourceExtension, 3);
            if (baseName.Length == 0) baseName = "FILE";
            return extension.Length == 0 ? baseName : $"{baseName}.{extension}";
        }

        private static string SanitizeCpmNamePart(string value, int maximumLength)
        {
            const string punctuation = "!#$%&'()-@^_`{}~";
            return new string(value.ToUpperInvariant()
                .Select(character => char.IsAsciiLetterOrDigit(character) || punctuation.Contains(character) ? character : '_')
                .Take(maximumLength)
                .ToArray())
                .Trim('_');
        }

        private bool EnsureWritable()
        {
            if (!CurrentDocument.IsReadOnly) return true;
            ShowError(CurrentDocument.FileSystem.Type == DskFileSystemType.MultiIpl
                ? "MZTools multi-game IPL images are currently read-only; individual games can be exported."
                : "This disk is read-only because structural inconsistencies were detected.");
            return false;
        }

        internal bool TryCloseDocument()
        {
            bool modified = document?.IsModified == true || IplEditorMode && multiIplDraftModified;
            if (!modified)
            {
                CancelMultiIplRefresh();
                return true;
            }
            MessageBoxResult result = MessageBox.Show(OwnerWindow, "Save changes to this DSK image?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes)
            {
                Save_Click(this, new RoutedEventArgs());
                bool saved = document != null && !document.IsModified && !multiIplDraftModified;
                if (saved) CancelMultiIplRefresh();
                return saved;
            }
            CancelMultiIplRefresh();
            return true;
        }

        private static string SuggestedName(DskFileEntry entry) => string.IsNullOrEmpty(entry.Extension) ? entry.Name : $"{entry.Name}.{entry.Extension}";

        private static string SanitizeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
            result = result.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(result) ? "PROGRAM" : result;
        }

        private static string FormatAppliedCompression(MzfCompressionOptions options)
        {
            if (options.Algorithm == MzfCompressionAlgorithm.None) return "None";
            string value = options.Algorithm.ToString().ToUpperInvariant();
            if (options.Zx0Quick) value += " quick";
            return value + (options.Direction == CompressionDirection.Backward ? " backward" : " forward");
        }
        private Window OwnerWindow => Window.GetWindow(this);
        private void ShowError(string message) => MessageBox.Show(OwnerWindow, message, "DSK editor", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    internal static class DskMzfConverter
    {
        internal static byte[] Serialize(DskFileEntry entry, byte[] data)
        {
            var header = new byte[128]; header[0] = entry.FileType == 0 ? (byte)1 : entry.FileType;
            byte[] name = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(entry.Name);
            name.AsSpan(0, Math.Min(16, name.Length)).CopyTo(header.AsSpan(1, 16)); header[17] = 0x0D;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18, 2), checked((ushort)Math.Min(data.Length, ushort.MaxValue)));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), entry.LoadAddress);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), entry.ExecuteAddress);
            return header.Concat(data.Take(ushort.MaxValue)).ToArray();
        }
    }

    internal static class TextPrompt
    {
        internal static string? Show(Window owner, string title, string label, string initial)
        {
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 5, 0, 10), MinWidth = 280 };
            var window = new Window { Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(12) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(box);
            var button = new Button { Content = "OK", IsDefault = true, MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right }; button.Click += (_, _) => window.DialogResult = true; panel.Children.Add(button); window.Content = panel;
            return window.ShowDialog() == true ? box.Text : null;
        }
    }

    internal static class ChoicePrompt
    {
        internal static string? Show(Window owner, string title, string label, IReadOnlyList<string> choices)
        {
            var selector = new ComboBox { ItemsSource = choices, SelectedIndex = 0, MinWidth = 310, Margin = new Thickness(0, 5, 0, 10) };
            var window = new Window { Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(12) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(selector);
            var button = new Button { Content = "Create", IsDefault = true, MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right }; button.Click += (_, _) => window.DialogResult = true; panel.Children.Add(button); window.Content = panel;
            return window.ShowDialog() == true ? selector.SelectedItem as string : null;
        }
    }
}
