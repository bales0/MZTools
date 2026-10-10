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

namespace MZTools
{
    public partial class DskEditorControl : UserControl
    {
        private const string MultiIplRowDragDataFormat = "MZTools.MultiIplEditorRows";
        private DskDocument? document;
        private readonly Guid fileDragOwner = Guid.NewGuid();
        private Point fileDragStart;
        private DskFileEntry[]? fileDragSelection;
        private bool synchronizingEditSelectors;
        private sealed record HexBufferChoice(int? Block, string Label);
        private DskSectorAddress? activeHexSector;
        private int? activeHexBlock;
        // The host belongs to the separate, modeless window, not the Disk Map layout.
        private readonly ContentControl editHexHost = new();
        private Window? hexWindow;
        internal Func<bool>? ConfirmDiscardHexChanges { get; set; }
        private DskLayoutModel? diskLayout;
        private DskSectorLayout? selectedMapSector;
        private readonly HashSet<DskSectorAddress> relatedMapSectors = new();
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
        private DskFileEntry[]? propertyEditTargets;
        private string? propertyEditAnchor;

        public DskEditorControl()
        {
            InitializeComponent();
            MapInteraction.EmphasizeSelection(mapBlocksGrid);
            MapInteraction.EmphasizeSelection(directoryGrid);
            mapBlocksGrid.LoadingRow += (_, e) => MapInteraction.SetIsRelated(e.Row,
                e.Row.Item is DskSectorLayout sector && relatedMapSectors.Contains(sector.Address));
            multiIplGrid.ItemsSource = multiIplRows;
            diskMap.SectorSelected += SelectLayoutSector;
        }

        internal event EventHandler? DocumentStateChanged;
        internal event EventHandler? CloseRequested;
        internal event EventHandler? OpenRequested;
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
            editHexHost.Content = null;
            activeHexSector = null;
            activeHexBlock = null;
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
            editHexHost.Content = null; activeHexSector = null; activeHexBlock = null;
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
            editHexHost.Content = null; activeHexSector = null; activeHexBlock = null;
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
            bool canEditProperties = DskCapabilityService.GetFilePropertyKind(document) != DskFilePropertyKind.None;
            void Add(string header, string property, bool editable = false) => directoryGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
                    StringFormat = property is nameof(DskFileEntry.LoadAddress) or nameof(DskFileEntry.ExecuteAddress) ? "0x{0:X4}" : null },
                Width = DataGridLength.Auto,
                IsReadOnly = !editable,
                SortMemberPath = property
            });
            void AddFlag(string header, string property)
            {
                var checkbox = new FrameworkElementFactory(typeof(CheckBox));
                checkbox.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.OneWay });
                checkbox.SetValue(FrameworkElement.TagProperty, property);
                checkbox.SetValue(UIElement.IsEnabledProperty, canEditProperties);
                checkbox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                checkbox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                checkbox.SetValue(FrameworkElement.ToolTipProperty, "Click to toggle " + header + ". The change is validated before applying.");
                checkbox.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(FileFlag_Click));
                directoryGrid.Columns.Add(new DataGridTemplateColumn { Header = header, SortMemberPath = property,
                    IsReadOnly = true, Width = 48, CellTemplate = new DataTemplate { VisualTree = checkbox } });
            }
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
                    Add("Name", nameof(DskFileEntry.Name), canRename); Add("Type", nameof(DskFileEntry.FileType), canEditProperties); Add("Size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress), canEditProperties); Add("Exec", nameof(DskFileEntry.ExecuteAddress), canEditProperties); Add("Start block", nameof(DskFileEntry.StartBlock)); AddFlag("Locked", nameof(DskFileEntry.Locked));
                    break;
                case DskFileSystemType.Cpm:
                    Add("User", nameof(DskFileEntry.User), canEditProperties); Add("Name", nameof(DskFileEntry.Name), canRename); Add("Ext", nameof(DskFileEntry.Extension), canRename); Add("Size", nameof(DskFileEntry.Size));
                    AddFlag("RO", nameof(DskFileEntry.ReadOnly)); AddFlag("SYS", nameof(DskFileEntry.System)); AddFlag("ARC", nameof(DskFileEntry.Archived)); Add("Extents", nameof(DskFileEntry.Extents)); Add("Blocks", nameof(DskFileEntry.Blocks));
                    break;
                case DskFileSystemType.Mrs:
                    Add("Name", nameof(DskFileEntry.Name), canRename); Add("Ext", nameof(DskFileEntry.Extension), canRename); Add("Blocks", nameof(DskFileEntry.Blocks)); Add("Approx. size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress), canEditProperties); Add("Exec", nameof(DskFileEntry.ExecuteAddress), canEditProperties); Add("File ID", nameof(DskFileEntry.StartBlock)); Add("Note", nameof(DskFileEntry.Notes));
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
            dskMoveButtons.Visibility = multiIplEditorMode ? Visibility.Visible : Visibility.Collapsed;
            UpdateFilePropertiesCommand();
            verifyImageMenu.IsEnabled = document != null && (!IplEditorMode || multiIplLayoutValid);
            normalizeContainerMenu.IsEnabled = document != null && !IplEditorMode;
            crossDiskCopyMenu.IsEnabled = !IplEditorMode && CrossDiskTransferService.Supports(document);
            bootProfilesMenu.IsEnabled = document?.FileSystem is CpmFileSystem && !IplEditorMode;
            foreach (var menu in new[] { verifyImageMenu, normalizeContainerMenu, crossDiskCopyMenu, bootProfilesMenu,
                cpmLayoutMenu, physicalPropertiesMenu, defragmentMenu, filesystemRepairMenu, bootstrapMenu, rawRangeMenu, compareDiskMenu })
            {
                System.Windows.Controls.ToolTipService.SetShowOnDisabled(menu, true);
                menu.ToolTip = "Requires a compatible document and valid layout; finish pending IPL layout changes first.";
            }
            cpmLayoutMenu.IsEnabled = document != null && !IplEditorMode && document.FileSystem.Type is DskFileSystemType.Raw or DskFileSystemType.Cpm or DskFileSystemType.BootOnly;
            physicalPropertiesMenu.IsEnabled = document != null && !IplEditorMode && document.Image.Tracks.Any(t => t != null);
            defragmentMenu.IsEnabled = document != null && !IplEditorMode && !document.IsReadOnly && document.FileSystem is FsmzFileSystem or CpmFileSystem or MrsFileSystem;
            filesystemRepairMenu.IsEnabled = document?.FileSystem is FsmzFileSystem or MrsFileSystem or CpmFileSystem && !IplEditorMode;
            bootstrapMenu.IsEnabled = document != null && !IplEditorMode && document.Image.Tracks.Count > 1;
            rawRangeMenu.IsEnabled = document != null && !IplEditorMode;
            compareDiskMenu.IsEnabled = document != null && (!IplEditorMode || multiIplLayoutValid);
            structureInspectorMenu.IsEnabled = DskCapabilityService.CanInspectStructure(document) && !IplEditorMode;
            structureInspectorMenu.ToolTip = structureInspectorMenu.IsEnabled
                ? "Read-only filesystem, directory, allocation and raw structures."
                : "Structure decoding is available for recognized FSMZ, CP/M and MRS filesystems only.";
            convertFormatMenu.IsEnabled = document != null && !IplEditorMode;
            convertFormatMenu.ToolTip = convertFormatMenu.IsEnabled ? "Create a separate image; inspect capacity and metadata changes before saving." : "No safe conversion is available for this detected filesystem/layout.";
            installBootSystemMenu.IsEnabled = document != null && DskCapabilityService.GetAvailableBootSystems(document).Count > 0;
            installBootSystemMenu.ToolTip = DskCapabilityService.BootSystemAvailabilityReason(document);
            if (IplEditorMode)
            {
                RefreshMultiIplView();
                return;
            }
            RefreshDiskLayout();
            if (document == null) return;
            IReadOnlyList<DskFileEntry> entries = document.FileSystem.ReadDirectory();
            bool bootOnly = document.FileSystem.Type == DskFileSystemType.BootOnly;
            bool showRawSectors = dskShowSectorsCheckBox.IsChecked == true;
            directoryGrid.ItemsSource = bootOnly && !showRawSectors
                ? entries.Where(entry => !IsRawSectorEntry(entry)).ToList()
                : entries;
            RefreshEditSelectors();
            dskShowSectorsCheckBox.Visibility = bootOnly ? Visibility.Visible : Visibility.Collapsed;
            DskImage image = document.Image;
            string sizes = string.Join(", ", image.Tracks.Where(track => track != null).SelectMany(track => track!.Sectors).Select(sector => sector.Data.Length).Distinct().Order());
            infoText.Text = $"Container: Extended CPC DSK | Creator: {image.Creator} | Tracks: {image.TrackCount} | Sides: {image.SideCount} | Physical tracks: {image.Tracks.Count} | Image: {image.Serialize().Length:N0} B\n" +
                $"Filesystem: {document.FileSystem.DisplayName} | Sector sizes: {sizes} B | Used: {document.FileSystem.UsedBytes:N0} B | Free: {document.FileSystem.FreeBytes:N0} B";
            if (document.FileSystem is MultiGameIplFileSystem mappedMulti)
                infoText.Text += "\n" + mappedMulti.MetadataLocationDescription;
            infoText.Text += "\n" + DskBootInfo.Inspect(document).Summary;
            if (document.LastSavedSha256 is { } savedHash) infoText.Text += "\nLast saved output SHA-256: " + savedHash;
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
            dskMoveButtons.Visibility = multiIplEditorMode ? Visibility.Visible : Visibility.Collapsed;
            RefreshDiskLayout();
            RefreshEditSelectors();
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
                    "Add MZF programs, edit their menu names and choose compression directly in this table.\n" +
                    "Bootable: Not yet — image has not been generated | System: None (IPL program loader will be generated)";
                warningText.Text = multiIplRows.Count == 0
                    ? singleIplEditorMode ? "Add one program before saving the image." : "Add at least one program before saving the image."
                    : string.Empty;
            }
            else
            {
                DskImage image = document.Image;
                infoText.Text = $"Container: Extended CPC DSK | Creator: {image.Creator} | Tracks: {image.TrackCount} | Sides: {image.SideCount} | Image: {image.Serialize().Length:N0} B\n" +
                    $"Filesystem: {document.FileSystem.DisplayName} | Programs: {multiIplRows.Count} | Used: {document.FileSystem.UsedBytes:N0} B | Free: {document.FileSystem.FreeBytes:N0} B";
                if (document.FileSystem is MultiGameIplFileSystem mappedMulti)
                    infoText.Text += "\n" + mappedMulti.MetadataLocationDescription;
                infoText.Text += "\n" + DskBootInfo.Inspect(document).Summary;
                if (document.LastSavedSha256 is { } savedHash) infoText.Text += "\nLast saved output SHA-256: " + savedHash;
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

        private void RefreshDiskLayout()
        {
            diskLayout = document == null ? null : DskAnalyzer.Analyze(document);
            selectedMapSector = null;
            diskMap.SetLayout(diskLayout);
            mapBlocksGrid.ItemsSource = diskLayout?.Sectors.ToArray();
            analysisSummaryText.Text = diskLayout?.Summary ?? "Add programs to generate the IPL image before analysis.";
            UpdateAnalysisCounts();
            sectorDetailText.Text = string.Empty;
            logicalMapOption.IsEnabled = diskLayout?.Sectors.Any(s => s.LogicalBlocks.Count != 0) == true;
            if (!logicalMapOption.IsEnabled) mapOrderBox.SelectedIndex = 0;
            UpdateAnalysisFilter();
            HighlightSelectedFiles();
        }

        private bool synchronizingMapSelection;
        private void SelectLayoutSector(DskSectorLayout? sector)
        {
            if (sector == null) { ClearMapSelection(); return; }
            if (activeHexSector != sector.Address && !ConfirmHexSwitch())
            {
                synchronizingMapSelection = true;
                try
                {
                    var retained = diskLayout?.Sectors.FirstOrDefault(s => s.Address == activeHexSector);
                    selectedMapSector = retained; diskMap.SelectSector(retained); mapBlocksGrid.SelectedItem = retained;
                }
                finally { synchronizingMapSelection = false; }
                return;
            }
            if (!synchronizingMapSelection)
            {
                synchronizingMapSelection = true;
                try { issuesGrid.SelectedItem = null; }
                finally { synchronizingMapSelection = false; }
            }
            selectedMapSector = sector;
            sectorDetailText.Text = sector.Detail;
            diskMap.SelectSector(sector);
            if (!ReferenceEquals(mapBlocksGrid.SelectedItem, sector))
            {
                bool wasSynchronizing = synchronizingMapSelection;
                synchronizingMapSelection = true;
                try { mapBlocksGrid.SelectedItem = sector; }
                finally { synchronizingMapSelection = wasSynchronizing; }
            }
            UpdateMapSelectionButtons();
            UpdateEditHexChoices();
            if (hexWindow != null || editHexHost.Content is DskHexEditorControl)
                OpenWritableHex(activeHexSector == sector.Address ? activeHexBlock : null);
            mapBlocksGrid.ScrollIntoView(sector);
        }

        private void UpdateMapSelectionButtons()
        {
            if (dskClearSelectionButton == null) return;
            dskClearSelectionButton.IsEnabled = diskLayout != null &&
                (selectedMapSector != null || issuesGrid?.SelectedItem != null ||
                 directoryGrid?.SelectedItems.Count > 0 || multiIplGrid?.SelectedItems.Count > 0);
        }

        private void ClearMapSelection(bool clearIssue = true)
        {
            if (synchronizingMapSelection) return;
            synchronizingMapSelection = true;
            try
            {
                selectedMapSector = null;
                diskMap.SelectSector(null);
                diskMap.HighlightFiles(Array.Empty<string>());
                UpdateRelatedMapRows(Array.Empty<string>());
                mapBlocksGrid.SelectedItem = null;
                if (clearIssue) issuesGrid.SelectedItem = null;
                directoryGrid.SelectedItems.Clear();
                multiIplGrid.SelectedItems.Clear();
                sectorDetailText.Text = string.Empty;
            }
            finally { synchronizingMapSelection = false; UpdateMapSelectionButtons(); RefreshEditSelectors(); }
        }
        private void ClearMapSelection_Click(object sender, RoutedEventArgs e) => ClearMapSelection();
        private void MapBackground_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DiskMapVisuals.IsBlankClick(e.OriginalSource as DependencyObject))
            { directoryGrid.UnselectAll(); multiIplGrid.UnselectAll(); ClearMapSelection(); }
        }
        private void MapBlocks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (synchronizingMapSelection) return;
            SelectLayoutSector(mapBlocksGrid.SelectedItem as DskSectorLayout);
        }

        private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            HighlightSelectedFiles();
            UpdateFilePropertiesCommand();
        }

        private void UpdateFilePropertiesCommand()
        {
            filePropertiesMenu.IsEnabled = !IplEditorMode && directoryGrid.SelectedItems.Count == 1 &&
                DskCapabilityService.GetFilePropertyKind(document) != DskFilePropertyKind.None;
            filePropertiesMenu.ToolTip = "Select one file. Properties belong to the current filesystem; attributes lost during cross-format copying cannot be restored here.";
            ToolTipService.SetShowOnDisabled(filePropertiesMenu, true);
        }

        private void FileProperties_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !filePropertiesMenu.IsEnabled || directoryGrid.SelectedItem is not DskFileEntry entry) return;
            try
            {
                var values = DskFilePropertiesDialog.Show(OwnerWindow, DskCapabilityService.GetFilePropertyKind(document), entry);
                if (values == null) return;
                var updated = DskFilePropertyService.Apply(document, entry, values);
                RefreshView();
                directoryGrid.SelectedItem = directoryGrid.Items.OfType<DskFileEntry>().FirstOrDefault(f => f.Key == updated.Key);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void RefreshCopiedFiles(IReadOnlyList<string> keys)
        {
            if (document == null) return;
            LoadDocument(document);
            foreach (var entry in directoryGrid.Items.OfType<DskFileEntry>().Where(f => keys.Contains(f.Key)))
                directoryGrid.SelectedItems.Add(entry);
            UpdateFilePropertiesCommand();
        }
        private void HighlightSelectedFiles()
        {
            if (diskMap == null || directoryGrid == null) return;
            if (!synchronizingMapSelection)
            {
                synchronizingMapSelection = true;
                try
                {
                    selectedMapSector = null;
                    diskMap.SelectSector(null);
                    mapBlocksGrid.SelectedItem = null;
                    issuesGrid.SelectedItem = null;
                    sectorDetailText.Text = string.Empty;
                }
                finally { synchronizingMapSelection = false; }
            }
            var keys = (IplEditorMode
                ? multiIplGrid.SelectedItems.OfType<MultiGameIplRow>().Select(row => multiIplRows.IndexOf(row).ToString())
                : directoryGrid.SelectedItems.OfType<DskFileEntry>().Select(entry => entry.Key)).ToHashSet();
            diskMap.HighlightFiles(keys);
            UpdateRelatedMapRows(keys);
            UpdateMapSelectionButtons();
            if (!synchronizingMapSelection && selectedMapSector == null) RefreshEditSelectors();
        }
        private void MapOrder_Changed(object sender, SelectionChangedEventArgs e) => diskMap?.SetLogical(mapOrderBox.SelectedIndex == 1);

        private void UpdateRelatedMapRows(IEnumerable<string> selectedKeys)
        {
            var keys = selectedKeys.ToHashSet();
            relatedMapSectors.Clear();
            if (diskLayout != null)
                foreach (var sector in diskLayout.Sectors.Where(sector => sector.Owners.Any(owner => keys.Contains(owner.FileKey)) ||
                    keys.Contains($"{sector.Track}:{sector.PhysicalIndex}")))
                    relatedMapSectors.Add(sector.Address);
            // Existing rows and future virtualized rows use exactly the map's file
            // highlight predicate. Do not change the single active sector selection.
            foreach (var sector in mapBlocksGrid.Items.OfType<DskSectorLayout>())
                if (mapBlocksGrid.ItemContainerGenerator.ContainerFromItem(sector) is DataGridRow row)
                    MapInteraction.SetIsRelated(row, relatedMapSectors.Contains(sector.Address));
        }

        private void MapBlocks_KeyDown(object sender, KeyEventArgs e)
        {
            if (MapInteraction.MoveGridSelection(mapBlocksGrid, e.Key)) e.Handled = true;
        }
        private void RefreshEditSelectors()
        {
            UpdateEditHexChoices();
            if (selectedMapSector == null && !(editHexHost.Content is DskHexEditorControl editor && editor.HasPendingChanges))
            {
                editHexHost.Content = null; activeHexSector = null; activeHexBlock = null;
                if (hexWindow != null) hexWindow.Title = "Hex Editor — no sector selected";
            }
            if (editHexHost.Content == null)
                editHexHost.Content = new TextBlock { Text = "Click a sector in Disk Map to display its bytes. Unlock editing and preview changes before Apply.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        }

        private void UpdateEditHexChoices()
        {
            if (editBlockBox == null) return;
            var sector = selectedMapSector;
            hexWindowButton.IsEnabled = sector != null;
            applyPatchButton.IsEnabled = document != null && !IplEditorMode;
            synchronizingEditSelectors = true;
            try
            {
                var choices = new List<HexBufferChoice>();
                if (sector != null)
                {
                    choices.Add(new(null, "Selected physical sector"));
                    if (document?.FileSystem is CpmFileSystem && !IplEditorMode)
                        choices.AddRange(sector.AllocationBlocks.Distinct().Order().Select(b => new HexBufferChoice(b, $"CP/M allocation block {b}")));
                }
                editBlockBox.ItemsSource = choices;
                editBlockBox.SelectedItem = choices.FirstOrDefault(c => c.Block == (activeHexSector == sector?.Address ? activeHexBlock : null)) ?? choices.FirstOrDefault();
                editBlockBox.IsEnabled = choices.Count > 0;
            }
            finally { synchronizingEditSelectors = false; }
            editHexReason.Text = IplEditorMode
                ? "Dedicated IPL images are edited in Files; writable raw hex is unavailable for this mode."
                : sector == null ? "Select a sector in the map or Blocks list."
                : $"Track {sector.Track}, descriptor {sector.PhysicalIndex}, R={sector.R}; role: {sector.Role}.";
        }

        private bool ConfirmHexSwitch() => editHexHost.Content is not DskHexEditorControl pending || !pending.HasPendingChanges ||
            (ConfirmDiscardHexChanges?.Invoke() ?? MessageBox.Show(OwnerWindow, "Discard the unapplied hex changes and select another buffer?", "Hex changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

        private void HexBuffer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (synchronizingEditSelectors || editBlockBox.SelectedItem is not HexBufferChoice choice) return;
            if (!ConfirmHexSwitch()) { UpdateEditHexChoices(); return; }
            if (hexWindow != null || editHexHost.Content is DskHexEditorControl) OpenWritableHex(choice.Block);
        }

        private void ApplyPatch_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !applyPatchButton.IsEnabled) return;
            if (editHexHost.Content is DskHexEditorControl pending && pending.HasPendingChanges)
            { ShowError("Apply or cancel the pending hex edit before applying a patch."); return; }
            var picker = new OpenFileDialog { Filter = "MZTools sector patch|*.mzpatch.json;*.json", Title = "Preview DSK patch" };
            if (picker.ShowDialog(OwnerWindow) != true) return;
            try
            {
                var preview = DskPatchService.Preview(document, DskPatchService.Read(picker.FileName));
                var dialog = new DskPatchPreviewWindow(OwnerWindow, preview.Report, () => DskPatchService.Apply(document, preview));
                dialog.ShowDialog();
                if (dialog.Applied) { LoadDocument(document); dskViews.SelectedItem = mapTab; }
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }
        private void OpenWritableHex(int? block)
        {
            if (document == null || selectedMapSector is not DskSectorLayout sector) return;
            if (activeHexSector == sector.Address && activeHexBlock == block && editHexHost.Content is DskHexEditorControl) return;
            if (IplEditorMode)
            {
                editHexHost.Content = new TextBox { Text = DskHexEditService.FormatHex(sector.Data), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                ShowHexWindow($"Track {sector.Track}, R={sector.R} — read-only");
                return;
            }
            try
            {
                var session = block is int selectedBlock
                    ? DskHexEditService.OpenCpmBlock(document, selectedBlock)
                    : DskHexEditService.OpenSector(document, sector.Address);
                var editor = new DskHexEditorControl(document, session);
                editHexHost.Content = editor;
                activeHexSector = sector.Address; activeHexBlock = block;
                ShowHexWindow(session.Title);
                editor.Completed += (_, _) =>
                {
                    if (!editor.Applied) { CloseHexWindow(); return; }
                    var address = sector.Address;
                    LoadDocument(document);
                    dskViews.SelectedItem = mapTab;
                    SelectLayoutSector(diskLayout?.Sectors.FirstOrDefault(s => s.Address == address));
                    OpenWritableHex(block);
                    warningText.Text = "Hex edit applied. Review Disk Map issues; saving remains a separate operation." +
                        (document.FileSystem.Type is DskFileSystemType.Raw or DskFileSystemType.BootOnly
                            ? " The current image has no recognized data filesystem; its bytes remain available for Save As and raw inspection." : "") +
                        (warningText.Text.Length == 0 ? "" : " " + warningText.Text);
                };
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }
        private void HexWindow_Click(object sender, RoutedEventArgs e)
        {
            if (selectedMapSector == null) return;
            OpenWritableHex((editBlockBox.SelectedItem as HexBufferChoice)?.Block);
            ShowHexWindow(null);
            hexWindow?.Activate();
        }
        private void ShowHexWindow(string? title)
        {
            // Detached controls in tests have no owner and must not create desktop windows.
            if (Window.GetWindow(this) is not Window owner) return;
            if (hexWindow == null)
            {
                hexWindow = new Window { Owner = owner.IsVisible ? owner : null, Width = Math.Min(1320, SystemParameters.WorkArea.Width - 40), Height = 740, MinWidth = 760, MinHeight = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = editHexHost };
                hexWindow.Closing += (_, e) => { if (!ConfirmHexSwitch()) e.Cancel = true; };
                hexWindow.Closed += (_, _) =>
                {
                    hexWindow = null; editHexHost.Content = null; activeHexSector = null; activeHexBlock = null;
                };
            }
            if (title != null) hexWindow.Title = "Hex Editor — " + title;
            if (owner.IsVisible && !hexWindow.IsVisible) { hexWindow.Owner = owner; hexWindow.Show(); }
        }
        private void AnalysisFilter_Changed(object sender, SelectionChangedEventArgs e) => UpdateAnalysisFilter();
        private void UpdateAnalysisCounts()
        {
            analysisAllButton.Content = $"All\n{diskLayout?.Issues.Count ?? 0}";
            analysisErrorsButton.Content = $"Errors\n{diskLayout?.Errors ?? 0}";
            analysisUnsafeButton.Content = $"Unsafe\n{diskLayout?.Unsafe ?? 0}";
            analysisWarningsButton.Content = $"Warn.\n{diskLayout?.Warnings ?? 0}";
            analysisInfoButton.Content = $"Info\n{diskLayout?.Information ?? 0}";
            analysisAllButton.ToolTip = DskIssueHelp.FilterMeaning(0);
            analysisErrorsButton.ToolTip = DskIssueHelp.FilterMeaning(1);
            analysisUnsafeButton.ToolTip = DskIssueHelp.FilterMeaning(4);
            analysisWarningsButton.ToolTip = DskIssueHelp.FilterMeaning(2);
            analysisInfoButton.ToolTip = DskIssueHelp.FilterMeaning(3);
        }
        private void AnalysisCount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out int filter)) return;
            analysisFilterBox.SelectedIndex = filter;
            mapInspectorTabs.SelectedIndex = 1;
        }
        private void UpdateAnalysisFilter()
        {
            if (issuesGrid == null || analysisFilterBox == null) return;
            analysisSeverityText.Text = DskIssueHelp.FilterMeaning(analysisFilterBox.SelectedIndex);
            issuesGrid.ItemsSource = diskLayout?.Issues.Where(i => analysisFilterBox.SelectedIndex switch {
                1 => i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe,
                2 => i.Severity == DskIssueSeverity.Warning, 3 => i.Severity == DskIssueSeverity.Info,
                4 => i.Severity == DskIssueSeverity.Unsafe, _ => true }).ToArray();
        }
        private void Issue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (synchronizingMapSelection) return;
            if (issuesGrid.SelectedItem is not DskAnalysisIssue issue) { ClearMapSelection(false); return; }
            ClearMapSelection(false);
            synchronizingMapSelection = true;
            try
            {
                var sector = issue.Track is int track
                    ? diskLayout?.Sectors.FirstOrDefault(s => s.Track == track && (issue.Sector == null || s.PhysicalIndex == issue.Sector))
                    : issue.FileKey != null ? diskLayout?.Sectors.FirstOrDefault(s => s.Owners.Any(o => o.FileKey == issue.FileKey)) : null;
                if (sector != null)
                {
                    SelectLayoutSector(sector);
                    mapBlocksGrid.ScrollIntoView(sector);
                }
                if (issue.FileKey != null)
                {
                    if (IplEditorMode && int.TryParse(issue.FileKey, out int index) && index >= 0 && index < multiIplRows.Count)
                        multiIplGrid.SelectedItem = multiIplRows[index];
                    else
                    {
                        var file = directoryGrid.Items.OfType<DskFileEntry>().FirstOrDefault(f => f.Key == issue.FileKey);
                        if (file != null) directoryGrid.SelectedItem = file;
                    }
                }
                sectorDetailText.Text = issue.Explanation +
                    (sector == null ? "" : $"\n\n{sector.Detail}");
            }
            finally { synchronizingMapSelection = false; UpdateMapSelectionButtons(); }
        }
        private void CopyAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (diskLayout == null) return;
            try { Clipboard.SetText(AnalysisReportWithCapabilities()); } catch (Exception exception) { ShowError(exception.Message); }
        }

        private string AnalysisReportWithCapabilities() => (diskLayout?.Report() ?? string.Empty) +
            (document == null ? string.Empty : "\n\n" + DskCapabilityService.Summary(document));

        private void DiskMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void VerifyImage_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !verifyImageMenu.IsEnabled) return;
            new DskPatchPreviewWindow(OwnerWindow, ImageVerificationService.Verify(document.Serialize(), document).Report, title: "Verify Image").ShowDialog();
        }

        private void NormalizeContainer_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !normalizeContainerMenu.IsEnabled) return;
            try
            {
                var result = ContainerNormalizeService.Preview(document);
                var dialog = new DskPatchPreviewWindow(OwnerWindow, result.Report, () => result.Apply(document), "Normalize Container Preview", "Apply normalization");
                dialog.ShowDialog(); if (dialog.Applied) LoadDocument(document);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void CrossDiskCopy_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !crossDiskCopyMenu.IsEnabled) return;
            var dialog = new CrossDiskTransferDialog(OwnerWindow, document); dialog.ShowDialog(); if (dialog.Applied) RefreshCopiedFiles(dialog.CopiedFileKeys);
        }

        private void BootProfiles_Click(object sender, RoutedEventArgs e) => ShowProfiles(true);
        private void ShowProfiles(bool boot)
        {
            if (document == null || IplEditorMode) return;
            try { var dialog = new UserProfilesDialog(OwnerWindow, document, boot); dialog.ShowDialog(); if (dialog.Applied) LoadDocument(document); }
            catch (Exception exception) { ShowError(exception.Message); }
        }
        private void NativeCom_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new NativeComDialog(OwnerWindow, target: document);
            dialog.ShowDialog();
            if (dialog.ImportedFileKey is { } key) RefreshCopiedFiles([key]);
        }

        private void CpmLayout_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !cpmLayoutMenu.IsEnabled) return;
            var dialog = new CpmLayoutDialog(OwnerWindow, document);
            dialog.ShowDialog();
            if (dialog.Applied) LoadDocument(document);
        }

        private void ToolsMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null) { button.ContextMenu.PlacementTarget = button; button.ContextMenu.IsOpen = true; }
        }

        private void BatchProcess_Click(object sender, RoutedEventArgs e) => new BatchProcessDialog(OwnerWindow).ShowDialog();

        private void PhysicalProperties_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !physicalPropertiesMenu.IsEnabled) return;
            var dialog = new DskPhysicalPropertiesDialog(OwnerWindow, document);
            dialog.ShowDialog(); if (dialog.Applied) LoadDocument(document);
        }

        private void RepairContainer_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode) { ShowError("Close the IPL editor before repairing a container."); return; }
            try
            {
                byte[] source;
                string? path = document?.FilePath;
                if (document != null) source = document.Serialize();
                else
                {
                    var picker = new OpenFileDialog { Filter = "Extended CPC DSK|*.dsk", Title = "Select a DSK to inspect for repair" };
                    if (picker.ShowDialog(OwnerWindow) != true) return;
                    path = picker.FileName; source = File.ReadAllBytes(path);
                }
                var dialog = new DskRepairDialog(OwnerWindow, source); dialog.ShowDialog();
                if (dialog.Result == null) return;
                if (document != null)
                {
                    if (!document.Serialize().AsSpan().SequenceEqual(source)) throw new InvalidOperationException("The document changed since preview.");
                    document.ReplaceContents(dialog.Result); LoadDocument(document);
                }
                else
                {
                    var repaired = DskDocument.Open(dialog.Result, path);
                    repaired.ReplaceContents(dialog.Result); LoadDocument(repaired);
                }
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Defragment_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !defragmentMenu.IsEnabled) return;
            try
            {
                var result = DskDefragmentService.Preview(document);
                var dialog = new DskPatchPreviewWindow(OwnerWindow, result.Report, () => result.Apply(document), "Defragment Preview", "Apply");
                dialog.ShowDialog(); if (dialog.Applied) LoadDocument(document);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void RepairFilesystem_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !filesystemRepairMenu.IsEnabled) return;
            try
            {
                var action = DskFilesystemRepairService.For(document); var result = action.Preview(document);
                var dialog = new DskPatchPreviewWindow(OwnerWindow, result.Report, () => action.Apply(document, result), "Safe Filesystem Repair Preview", "Apply repair");
                dialog.ShowDialog(); if (dialog.Applied) LoadDocument(document);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Bootstrap_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !bootstrapMenu.IsEnabled) return;
            try { var dialog = new BootstrapDialog(OwnerWindow, document); dialog.ShowDialog(); if (dialog.Applied) LoadDocument(document); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void RawRange_Click(object sender, RoutedEventArgs e)
        {
            if (document == null) return;
            var dialog = new DskRawRangeDialog(OwnerWindow, document); dialog.ShowDialog();
            if (dialog.Applied) LoadDocument(document);
        }

        private void ConvertFormat_Click(object sender, RoutedEventArgs e)
        {
            if (document == null) return;
            var result = DskConversionDialog.Show(OwnerWindow, document);
            if (result?.CanConvert != true) return;
            var save = new SaveFileDialog { Filter = "DSK image|*.dsk", FileName = "converted.dsk", Title = "Convert and Save As — original remains unchanged" };
            if (save.ShowDialog(OwnerWindow) != true) return;
            if (document.FilePath != null && Path.GetFullPath(save.FileName).Equals(Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase))
            { ShowError("Conversion must be saved under a different path; the source image cannot be overwritten."); return; }
            try
            {
                DskFileTransferService.SaveConvertedImage(result, save.FileName, document.FilePath);
                MessageBox.Show(OwnerWindow, $"Converted image saved to:\n{save.FileName}\n\nThe original document remains open and unchanged.", "Convert Format", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void StructureInspector_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !structureInspectorMenu.IsEnabled) return;
            try { new DskStructureWindow(OwnerWindow, DskStructureService.Build(document), SelectStructureItem).ShowDialog(); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void SelectStructureItem(DskStructureItem? item)
        {
            ClearMapSelection();
            if (item == null || diskLayout == null) return;
            dskViews.SelectedItem = mapTab;
            SelectLayoutSector(diskLayout.Sectors.FirstOrDefault(s => item.Addresses.Contains(s.Address)));
            diskMap.HighlightFiles(item.Addresses.Select(a => $"{a.Track}:{a.Sector}"));
            sectorDetailText.Text = $"{item.Name}: {item.Value}\n{item.Detail}";
        }

        private void CompareDisk_Click(object sender, RoutedEventArgs e)
        {
            if (document == null || !compareDiskMenu.IsEnabled) return;
            var picker = new OpenFileDialog { Filter = "DSK image|*.dsk", Title = "Compare current DSK snapshot with..." };
            if (picker.ShowDialog(OwnerWindow) != true) return;
            try
            {
                var comparison = DskCompareService.Compare(document, DskDocument.Open(picker.FileName));
                new DskCompareWindow(OwnerWindow, comparison, SelectComparisonItem).ShowDialog();
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void SelectComparisonItem(DskDiffItem? item)
        {
            ClearMapSelection();
            if (item == null || diskLayout == null) return;
            dskViews.SelectedItem = mapTab;
            if (item.LeftFileKey != null)
            {
                if (IplEditorMode && int.TryParse(item.LeftFileKey, out int index) && index >= 0 && index < multiIplRows.Count)
                    multiIplGrid.SelectedItem = multiIplRows[index];
                else directoryGrid.SelectedItem = directoryGrid.Items.OfType<DskFileEntry>().FirstOrDefault(e => e.Key == item.LeftFileKey);
                diskMap.HighlightFiles([item.LeftFileKey]);
            }
            else if (item.LeftAddress != null) SelectLayoutSector(diskLayout.Sectors.FirstOrDefault(s => s.Address == item.LeftAddress));
            sectorDetailText.Text = item.Detail;
        }

        private void InstallBootSystem_Click(object sender, RoutedEventArgs e)
            => ShowBootSystemInstaller();

        internal void ShowBootSystemInstaller()
        {
            if (document == null || !installBootSystemMenu.IsEnabled) return;
            try
            {
                var dialog = new CpmSystemBuilderDialog(OwnerWindow, document);
                dialog.SystemInstalled += (_, _) => RefreshView();
                dialog.ShowDialog();
                if (dialog.Installed) RefreshView();
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }
        private void SaveAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (diskLayout == null) return;
            var dialog = new SaveFileDialog { Filter = "Text report|*.txt", FileName = "dsk-analysis.txt" };
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            try { File.WriteAllText(dialog.FileName, AnalysisReportWithCapabilities()); } catch (Exception exception) { ShowError(exception.Message); }
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
            => ExportFiles();

        private void ContextExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: DskFileEntry entry }) ExportFiles([entry]);
            else if (sender is MenuItem { Tag: MultiGameIplRow row }) ExportFiles(iplSelection: [row]);
        }

        private void ExportFiles(IReadOnlyList<DskFileEntry>? fileSelection = null, MultiGameIplRow[]? iplSelection = null)
        {
            if (IplEditorMode)
            {
                MultiGameIplRow[] selected = iplSelection ?? multiIplGrid.SelectedItems
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
            IReadOnlyList<DskFileEntry> entries = fileSelection ?? SelectedEntries;
            if (entries.Count == 0) return;
            DskFileSystemType fileSystemType = document.FileSystem.Type;
            bool canExportMzf = entries.All(entry => DskExportSupport.CanExportMzf(fileSystemType, entry));
            var dialog = new SaveFileDialog
            {
                Filter = DskExportSupport.GetFilter(fileSystemType, canExportMzf),
                Title = entries.Count == 1 ? $"Export {SuggestedName(entries[0])}" : $"Export {entries.Count} selected files — choose output folder and format",
                FileName = SuggestedName(entries[0]),
                AddExtension = canExportMzf,
                DefaultExt = canExportMzf ? ".bin" : string.Empty,
                OverwritePrompt = entries.Count == 1
            };
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            try
            {
                bool exportMzf = canExportMzf && dialog.FilterIndex == 2;
                string folder = Path.GetDirectoryName(dialog.FileName)
                    ?? throw new InvalidOperationException("The selected output path has no directory.");
                IReadOnlyList<string> names = entries.Count == 1
                    ? [Path.GetFileName(exportMzf ? Path.ChangeExtension(dialog.FileName, ".mzf") : dialog.FileName)]
                    : DskExportSupport.BuildBatchFileNames(entries, fileSystemType, exportMzf);
                var outputs = new List<(string Path, byte[] Data)>(entries.Count);
                for (int index = 0; index < entries.Count; index++)
                {
                    DskFileEntry entry = entries[index];
                    byte[] data = document.FileSystem.Extract(entry);
                    outputs.Add((Path.Combine(folder, names[index]), exportMzf
                        ? DskMzfConverter.Serialize(entry, data)
                        : data));
                }

                string[] existingPaths = outputs
                    .Select(output => output.Path)
                    .Where(File.Exists)
                    .ToArray();
                if (existingPaths.Length > 0 && entries.Count > 1)
                {
                    MessageBoxResult overwrite = MessageBox.Show(
                        OwnerWindow,
                        $"{existingPaths.Length} target file(s) already exist. Overwrite them?",
                        "Confirm batch export",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (overwrite != MessageBoxResult.Yes) return;
                }
                if (fileSystemType == DskFileSystemType.Mrs)
                {
                    MessageBox.Show(
                        OwnerWindow,
                        "MRS stores only a count of 512-byte blocks. Exported files may include padding from the final block.",
                        "MRS export length",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                foreach ((string path, byte[] data) in outputs) File.WriteAllBytes(path, data);
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
            HighlightSelectedFiles();
        }

        private void MultiIplCompressionSettings_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Button { DataContext: MultiGameIplRow clickedRow }) return;
            List<MultiGameIplRow> selected = multiIplGrid.SelectedItems
                .OfType<MultiGameIplRow>()
                .Where(row => row.CanChangeCompression)
                .ToList();
            multiIplCompressionTargets = selected.Contains(clickedRow) && selected.Count > 0
                ? selected
                : clickedRow.CanChangeCompression ? [clickedRow] : null;
        }

        private async void MultiIplCompressionSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: MultiGameIplRow clicked } || !clicked.CanChangeCompression) return;
            var rows = multiIplCompressionTargets ?? multiIplGrid.SelectedItems.OfType<MultiGameIplRow>().Where(r => r.CanChangeCompression).ToList();
            multiIplCompressionTargets = null;
            if (!rows.Contains(clicked)) rows = [clicked];
            var dialog = new CompressionSettingsDialog(OwnerWindow, rows);
            if (dialog.ShowDialog() != true || dialog.SelectedOptions == null) return;
            suppressMultiIplChanges = true;
            try { for (int i = 0; i < rows.Count; i++) rows[i].SetCompressionOptions(dialog.SelectedOptions, dialog.PreparedResults[i]); }
            finally { suppressMultiIplChanges = false; }
            multiIplDraftModified = true;
            await RebuildIplAsync();
            RestoreMultiIplSelection(rows);
        }

        private void MultiIplGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            multiIplDragStartPoint = e.GetPosition(multiIplGrid);
            DependencyObject? source = e.OriginalSource as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(source);
            bool editorClicked = FindVisualParent<ComboBox>(source) is not null ||
                FindVisualParent<Button>(source) is not null ||
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
                (e.Column.SortMemberPath is not (nameof(DskFileEntry.Name) or nameof(DskFileEntry.Extension)) &&
                 !IsEditableProperty(e.Column.SortMemberPath));
        }

        private void DirectoryGrid_PropertyMouseDown(object sender, MouseButtonEventArgs e)
        {
            fileDragSelection = null;
            if (e.OriginalSource is not DependencyObject source) return;
            var cell = FindVisualParent<DataGridCell>(source);
            if (cell?.IsEditing == true) return;
            if (cell?.DataContext is DskFileEntry dragEntry && CrossDiskTransferService.Supports(document) &&
                FindVisualParent<CheckBox>(source) == null && FindVisualParent<TextBox>(source) == null)
            {
                fileDragStart = e.GetPosition(directoryGrid);
                var keys = directoryGrid.SelectedItems.Contains(dragEntry)
                    ? directoryGrid.SelectedItems.OfType<DskFileEntry>().Select(f => f.Key).ToHashSet()
                    : new HashSet<string> { dragEntry.Key };
                fileDragSelection = document!.FileSystem.ReadDirectory().Where(f => keys.Contains(f.Key)).ToArray();
            }
            propertyEditTargets = null; propertyEditAnchor = null;
            if (cell?.DataContext is DskFileEntry entry && IsEditableProperty(cell.Column.SortMemberPath) &&
                directoryGrid.SelectedItems.Contains(entry) && Keyboard.Modifiers == ModifierKeys.None)
            {
                propertyEditTargets = directoryGrid.SelectedItems.OfType<DskFileEntry>().ToArray();
                propertyEditAnchor = entry.Key;
                if (propertyEditTargets.Length > 1)
                {
                    // DataGrid's ordinary click would collapse a multi-selection
                    // before the checkbox/editor receives it. Keep the group intact.
                    e.Handled = true;
                    if (FindVisualParent<CheckBox>(source) is { } checkbox)
                    {
                        checkbox.Focus();
                        checkbox.SetCurrentValue(CheckBox.IsCheckedProperty, checkbox.IsChecked != true);
                        FileFlag_Click(checkbox, new RoutedEventArgs(CheckBox.ClickEvent));
                    }
                    else
                    {
                        directoryGrid.CurrentCell = new DataGridCellInfo(entry, cell.Column);
                        directoryGrid.BeginEdit();
                    }
                }
            }
        }

        private IReadOnlyList<DskFileEntry> PropertyTargets(DskFileEntry entry)
        {
            var targets = propertyEditAnchor == entry.Key ? propertyEditTargets : null;
            propertyEditTargets = null; propertyEditAnchor = null;
            return targets ?? (directoryGrid.SelectedItems.Contains(entry)
                ? directoryGrid.SelectedItems.OfType<DskFileEntry>().ToArray() : [entry]);
        }

        private void RefreshPropertySelection(IReadOnlyList<DskFileEntry> updated)
        {
            RefreshView();
            var keys = updated.Select(file => file.Key).ToHashSet();
            foreach (var file in directoryGrid.Items.OfType<DskFileEntry>().Where(file => keys.Contains(file.Key)))
                directoryGrid.SelectedItems.Add(file);
        }
        private void FileFlag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: DskFileEntry entry, Tag: string property } checkbox) return;
            try
            {
                var updated = ApplyInlineProperties(PropertyTargets(entry), property, checkbox.IsChecked == true ? "True" : "False");
                RefreshPropertySelection(updated);
            }
            catch (Exception exception) { RefreshView(); ShowError(exception.Message); }
            e.Handled = true;
        }

        private bool IsEditableProperty(string property) => DskCapabilityService.GetFilePropertyKind(document) switch
        {
            DskFilePropertyKind.Cpm => property is nameof(DskFileEntry.User) or nameof(DskFileEntry.ReadOnly) or nameof(DskFileEntry.System) or nameof(DskFileEntry.Archived),
            DskFilePropertyKind.Mrs => property is nameof(DskFileEntry.LoadAddress) or nameof(DskFileEntry.ExecuteAddress),
            DskFilePropertyKind.Fsmz => property is nameof(DskFileEntry.LoadAddress) or nameof(DskFileEntry.ExecuteAddress) or nameof(DskFileEntry.FileType) or nameof(DskFileEntry.Locked),
            _ => false
        };

        internal DskFileEntry ApplyInlineProperty(DskFileEntry entry, string property, string text)
            => ApplyInlineProperties([entry], property, text)[0];

        internal IReadOnlyList<DskFileEntry> ApplyInlineProperties(IReadOnlyList<DskFileEntry> entries, string property, string text)
        {
            if (!IsEditableProperty(property)) throw new InvalidOperationException("This property is read-only for the current filesystem.");
            static ushort Address(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.Parse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
                : ushort.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            static bool Flag(string value) => value switch { "1" => true, "0" => false, _ => bool.Parse(value) };
            var changes = entries.Select(entry =>
            {
                var values = DskFileProperties.From(entry);
                values = property switch
                {
                    nameof(DskFileEntry.User) => values with { User = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) },
                    nameof(DskFileEntry.FileType) => values with { FileType = checked((byte)Address(text)) },
                    nameof(DskFileEntry.Locked) => values with { Locked = Flag(text) },
                    nameof(DskFileEntry.ReadOnly) => values with { ReadOnly = Flag(text) },
                    nameof(DskFileEntry.System) => values with { System = Flag(text) },
                    nameof(DskFileEntry.Archived) => values with { Archived = Flag(text) },
                    nameof(DskFileEntry.LoadAddress) => values with { Load = Address(text) },
                    nameof(DskFileEntry.ExecuteAddress) => values with { Execute = Address(text) },
                    _ => throw new InvalidOperationException("Unknown property.")
                };
                return (entry, values);
            }).ToArray();
            return DskFilePropertyService.ApplyMany(CurrentDocument, changes);
        }

        private void DirectoryGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || document == null ||
                e.Row.Item is not DskFileEntry entry || e.EditingElement is not TextBox editor)
            {
                return;
            }

            string editedValue = editor.Text.Trim();
            if (IsEditableProperty(e.Column.SortMemberPath))
            {
                try
                {
                    var updated = ApplyInlineProperties(PropertyTargets(entry), e.Column.SortMemberPath, editedValue);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RefreshPropertySelection(updated);
                    }));
                }
                catch (Exception exception)
                {
                    e.Cancel = true; ShowError(exception.Message);
                    Dispatcher.BeginInvoke(new Action(() => { directoryGrid.CancelEdit(DataGridEditingUnit.Cell); RefreshView(); }));
                }
                return;
            }
            if (e.Column.SortMemberPath is not (nameof(DskFileEntry.Name) or nameof(DskFileEntry.Extension))) return;
            string baseName = e.Column.SortMemberPath == nameof(DskFileEntry.Name) ? editedValue : entry.Name;
            string extension = e.Column.SortMemberPath == nameof(DskFileEntry.Extension) ? editedValue : entry.Extension;
            string newName = document.FileSystem.Type is DskFileSystemType.Cpm or DskFileSystemType.Mrs && extension.Length > 0
                ? $"{baseName}.{extension}"
                : baseName;
            try
            {
                string currentName = document.FileSystem.Type is DskFileSystemType.Cpm or DskFileSystemType.Mrs && entry.Extension.Length > 0
                    ? $"{entry.Name}.{entry.Extension}"
                    : entry.Name;
                if (currentName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.BeginInvoke(new Action(RefreshView));
                    return;
                }
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
            if (e.OriginalSource is DependencyObject source && FindVisualParent<TextBox>(source) != null) return;
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.C or Key.V)
            {
                e.Handled = true;
                if (e.Key == Key.C) CopyFiles_Click(sender, e); else PasteFiles_Click(sender, e);
                return;
            }
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
            try { CanonicalizeMultiIplForSave(); document.Save(); multiIplDraftModified = false; RefreshView(); } catch (Exception exception) { ShowError(exception.Message); }
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode && !multiIplLayoutValid) { ShowError("Wait for a valid IPL layout before saving."); return; }
            if (document == null) { ShowError("Add at least one program before saving the image."); return; }
            var dialog = new SaveFileDialog { Filter = "Extended CPC DSK|*.dsk", FileName = Path.GetFileName(document.FilePath) ?? "disk.dsk" };
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            try { CanonicalizeMultiIplForSave(); document.Save(dialog.FileName); multiIplDraftModified = false; RefreshView(); } catch (Exception exception) { ShowError(exception.Message); }
        }

        private void CanonicalizeMultiIplForSave()
        {
            if (document?.FileSystem is not MultiGameIplFileSystem multi ||
                multi.Metadata.Layout != MultiGameMetadataLayout.IplProComment || !IplEditorMode) return;
            byte[] canonical = Mz800MultiGameIplDskWriter.Build(multi.GetInputs()).Image;
            document.ReplaceContents(canonical);
            LoadDocument(document);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

        private void DirectoryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var row = FindVisualParent<DataGridRow>(source);
            contextExportMenu.Tag = row?.Item as DskFileEntry;
            if (row?.Item is DskFileEntry && !row.IsSelected)
            {
                directoryGrid.SelectedItems.Clear();
                row.IsSelected = true;
            }
        }

        private void DirectoryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            UpdateFilePropertiesCommand();
            bool selected = SelectedEntries.Count > 0;
            contextAddMenu.IsEnabled = dskAddButton.IsEnabled;
            if (e?.CursorLeft < 0) contextExportMenu.Tag = directoryGrid.CurrentItem as DskFileEntry;
            var exportEntry = contextExportMenu.Tag as DskFileEntry;
            contextExportMenu.IsEnabled = exportEntry != null;
            contextExportMenu.Header = exportEntry == null ? "Export..." : $"Export “{SuggestedName(exportEntry)}”…";
            contextExportSelectedMenu.Header = $"Export selected ({SelectedEntries.Count})…";
            contextExportSelectedMenu.Visibility = SelectedEntries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            contextRenameMenu.IsEnabled = SelectedEntries.Count == 1 && document is { IsReadOnly: false } &&
                document.FileSystem.Type is DskFileSystemType.Fsmz or DskFileSystemType.Cpm or DskFileSystemType.Mrs;
            contextDeleteMenu.IsEnabled = selected && dskDeleteButton.IsEnabled && document?.FileSystem.Type is
                DskFileSystemType.Fsmz or DskFileSystemType.Cpm or DskFileSystemType.Mrs;
            contextPropertiesMenu.IsEnabled = filePropertiesMenu.IsEnabled;
            copyFilesMenu.IsEnabled = !IplEditorMode && CrossDiskTransferService.Supports(document) && directoryGrid.SelectedItems.OfType<DskFileEntry>().Any();
            copyFilesMenu.ToolTip = copyFilesMenu.IsEnabled ? "Copy selected files; the source remains unchanged." : "Select files in an FSMZ, CP/M or MRS filesystem.";
            pasteFilesMenu.IsEnabled = false;
            pasteFilesMenu.ToolTip = "Copy files from a disk in MZTools first.";
            try
            {
                if (!IplEditorMode && CrossDiskTransferService.Supports(document) && Clipboard.GetDataObject() is { } data && WorkspaceDragTransfer.IsPresent(data))
                {
                    var packet = WorkspaceDragTransfer.Read(data);
                    pasteFilesMenu.IsEnabled = packet.Disk != null;
                    pasteFilesMenu.ToolTip = packet.Disk != null ? "Preview copied files, name collisions and metadata changes before applying." : "The clipboard contains tape records, not disk files.";
                }
            }
            catch (Exception exception) { pasteFilesMenu.ToolTip = exception.Message; }
        }

        private void RenameSelected_Click(object sender, RoutedEventArgs e)
        {
            var grid = IplEditorMode ? multiIplGrid : directoryGrid;
            if (grid.SelectedItems.Count != 1) return;
            var column = grid.Columns.FirstOrDefault(c => Equals(c.Header, IplEditorMode ? "Menu name" : "Name") && !c.IsReadOnly);
            if (column == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                grid.Focus(); grid.CurrentCell = new DataGridCellInfo(grid.SelectedItem, column); grid.BeginEdit();
            }));
        }

        private void MultiIplGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var row = FindVisualParent<DataGridRow>(source);
            iplContextExportMenu.Tag = row?.Item as MultiGameIplRow;
            if (row?.Item is MultiGameIplRow && !row.IsSelected)
            { multiIplGrid.SelectedItems.Clear(); row.IsSelected = true; }
        }

        private void MultiIplGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var selected = multiIplGrid.SelectedItems.OfType<MultiGameIplRow>().ToArray();
            if (e?.CursorLeft < 0) iplContextExportMenu.Tag = multiIplGrid.CurrentItem as MultiGameIplRow;
            var exportRow = iplContextExportMenu.Tag as MultiGameIplRow;
            iplContextExportMenu.IsEnabled = exportRow != null;
            iplContextExportMenu.Header = exportRow == null ? "Export..." : $"Export “{exportRow.MenuName}”…";
            iplContextExportSelectedMenu.Header = $"Export selected ({selected.Length})…";
            iplContextExportSelectedMenu.Visibility = selected.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            iplContextRenameMenu.IsEnabled = selected.Length == 1;
            iplContextDeleteMenu.IsEnabled = selected.Length > 0 && (singleIplEditorMode || selected.Length < multiIplRows.Count);
            iplContextCompressionMenu.IsEnabled = selected.Length > 0 && selected.All(r => r.CanChangeCompression);
            iplContextCompressionMenu.ToolTip = selected.FirstOrDefault(r => !r.CanChangeCompression)?.CompressionHint;
            ToolTipService.SetShowOnDisabled(iplContextCompressionMenu, true);
        }

        private void IplContextCompression_Click(object sender, RoutedEventArgs e)
        {
            if (multiIplGrid.SelectedItem is MultiGameIplRow row)
                MultiIplCompressionSettings_Click(new Button { DataContext = row }, e);
        }

        private void CopyFiles_Click(object sender, RoutedEventArgs e)
        {
            if (IplEditorMode || !CrossDiskTransferService.Supports(document)) return;
            var selected = directoryGrid.SelectedItems.OfType<DskFileEntry>().ToArray();
            if (selected.Length == 0) return;
            try { Clipboard.SetDataObject(WorkspaceDragTransfer.Create(fileDragOwner, WorkspaceDragTransfer.Capture(document!, selected)), true); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void PasteFiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.GetDataObject() is not { } data || !WorkspaceDragTransfer.IsPresent(data)) return;
                PreviewWorkspaceCopy(data);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private bool PreviewWorkspaceCopy(IDataObject data)
        {
            if (IplEditorMode || !CrossDiskTransferService.Supports(document))
                throw new InvalidOperationException("Copy target must be a writable FSMZ, CP/M or MRS disk.");
            var packet = WorkspaceDragTransfer.Read(data);
            var source = WorkspaceDragTransfer.OpenDisk(packet);
            var dialog = new CrossDiskTransferDialog(OwnerWindow, document!, source, packet.Keys);
            dialog.ShowDialog();
            if (dialog.Applied) RefreshCopiedFiles(dialog.CopiedFileKeys);
            return dialog.Applied;
        }

        private void DirectoryGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { fileDragSelection = null; return; }
            if (fileDragSelection is not { Length: > 0 } selected || document == null) return;
            Point position = e.GetPosition(directoryGrid);
            if (Math.Abs(position.X - fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            fileDragSelection = null;
            if (directoryGrid.IsKeyboardFocusWithin && directoryGrid.CurrentCell.Column != null &&
                FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject)?.IsEditing == true) return;
            try
            {
                var data = WorkspaceDragTransfer.Create(fileDragOwner, WorkspaceDragTransfer.Capture(document, selected));
                DragDrop.DoDragDrop(directoryGrid, data, DragDropEffects.Copy);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Control_DragOver(object sender, DragEventArgs e)
        {
            if (!WorkspaceDragTransfer.IsPresent(e.Data)) return;
            e.Handled = true;
            e.Effects = !WorkspaceDragTransfer.IsLocal(e.Data, fileDragOwner) && !IplEditorMode &&
                CrossDiskTransferService.Supports(document) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void Control_Drop(object sender, DragEventArgs e)
        {
            if (e.Handled) return;
            if (WorkspaceDragTransfer.IsPresent(e.Data))
            {
                e.Handled = true; e.Effects = DragDropEffects.None;
                if (WorkspaceDragTransfer.IsLocal(e.Data, fileDragOwner)) return;
                try
                {
                    if (PreviewWorkspaceCopy(e.Data)) e.Effects = DragDropEffects.Copy;
                }
                catch (Exception exception) { ShowError(exception.Message); }
                return;
            }
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
            if (editHexHost.Content is DskHexEditorControl pending && pending.HasPendingChanges &&
                MessageBox.Show(OwnerWindow, "Discard unapplied hex edits and close this disk?", "Unapplied hex changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
            bool modified = document?.IsModified == true || IplEditorMode && multiIplDraftModified;
            if (!modified)
            {
                CancelMultiIplRefresh();
                CloseHexWindow();
                return true;
            }
            MessageBoxResult result = MessageBox.Show(OwnerWindow, "Save changes to this DSK image?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes)
            {
                Save_Click(this, new RoutedEventArgs());
                bool saved = document != null && !document.IsModified && !multiIplDraftModified;
                if (saved) { CancelMultiIplRefresh(); CloseHexWindow(); }
                return saved;
            }
            CancelMultiIplRefresh();
            CloseHexWindow();
            return true;
        }
        private void CloseHexWindow()
        {
            if (editHexHost.Content is DskHexEditorControl editor) editor.Revert();
            hexWindow?.Close();
            editHexHost.Content = null; activeHexSector = null; activeHexBlock = null;
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
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length > ushort.MaxValue)
                throw new InvalidDataException("MZF payloads cannot exceed 65535 bytes; no output file was created.");
            var header = new byte[128]; header[0] = entry.FileType;
            byte[] name = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(entry.Name);
            name.AsSpan(0, Math.Min(16, name.Length)).CopyTo(header.AsSpan(1, 16)); header[17] = 0x0D;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18, 2), checked((ushort)data.Length));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), entry.LoadAddress);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), entry.ExecuteAddress);
            return header.Concat(data).ToArray();
        }
    }

    internal static class DskExportSupport
    {
        internal static bool CanExportMzf(DskFileSystemType type, DskFileEntry entry) =>
            type is DskFileSystemType.Fsmz or DskFileSystemType.SingleIpl ||
            type == DskFileSystemType.BootOnly && entry.Key == "iplpro";

        internal static string GetFilter(DskFileSystemType type, bool canExportMzf)
        {
            if (type is DskFileSystemType.Cpm or DskFileSystemType.Mrs)
                return "Original binary file|*.*";
            if (!canExportMzf) return "Raw payload|*.bin";
            string mzfLabel = type == DskFileSystemType.Fsmz
                ? "Native MZF file"
                : type == DskFileSystemType.BootOnly
                    ? "Reconstructed IPL bootstrap MZF"
                    : "Reconstructed MZF from IPL metadata";
            return $"Raw payload|*.bin|{mzfLabel}|*.mzf";
        }

        internal static IReadOnlyList<string> BuildBatchFileNames(
            IReadOnlyList<DskFileEntry> entries,
            DskFileSystemType type,
            bool exportMzf)
        {
            string[] naturalNames = entries
                .Select(entry => SanitizeFileName(exportMzf
                    ? Path.ChangeExtension(SuggestedName(entry), ".mzf")
                    : SuggestedName(entry)))
                .ToArray();

            if (type == DskFileSystemType.Cpm)
            {
                HashSet<string> duplicates = naturalNames
                    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < naturalNames.Length; index++)
                {
                    if (duplicates.Contains(naturalNames[index]))
                        naturalNames[index] = $"U{entries[index].User:D2}_{naturalNames[index]}";
                }
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < naturalNames.Length; index++)
            {
                string candidate = naturalNames[index];
                string stem = Path.GetFileNameWithoutExtension(candidate);
                string extension = Path.GetExtension(candidate);
                int suffix = 2;
                while (!used.Add(candidate)) candidate = $"{stem}_{suffix++}{extension}";
                naturalNames[index] = candidate;
            }
            return naturalNames;
        }

        private static string SuggestedName(DskFileEntry entry) => string.IsNullOrEmpty(entry.Extension)
            ? entry.Name
            : $"{entry.Name}.{entry.Extension}";

        private static string SanitizeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
            result = result.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(result) ? "PROGRAM" : result;
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
