using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace QDTool
{
    public partial class MultiGameIplDskOptionsDialog : Window
    {
        private const string RowDragDataFormat = "QDTool.MultiGameIplRows";
        private CancellationTokenSource? previewCancellation;
        private bool updatingRows;
        private Point rowDragStartPoint;
        private MultiGameIplRow? rowDragStartItem;
        private bool rowDragInProgress;
        private DataGridRow? rowDropTarget;

        internal MultiGameIplDskOptionsDialog(IReadOnlyList<TapeRecord> records)
        {
            InitializeComponent();
            InitializeEntries(records.Select((record, index) =>
            {
                string name = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);
                return new MultiGameIplRow(index + 1, record.DeepClone(), name);
            }));
        }

        internal MultiGameIplDskOptionsDialog(IReadOnlyList<MultiGameIplInput> inputs)
        {
            InitializeComponent();
            InitializeEntries(inputs.Select((input, index) => new MultiGameIplRow(index + 1, input)));
        }

        private void InitializeEntries(IEnumerable<MultiGameIplRow> rows)
        {
            Entries = new ObservableCollection<MultiGameIplRow>(rows);
            foreach (MultiGameIplRow row in Entries)
            {
                row.PropertyChanged += Row_PropertyChanged;
            }
            entriesGrid.ItemsSource = Entries;
            DataContext = this;
            capacityTextBlock.Text =
                $"Logical disk capacity: {Mz800DskImage.LogicalSectorCount} sectors / " +
                $"{Mz800DskImage.LogicalSectorCount * Mz800DskImage.SectorSize} B. " +
                $"Per-entry payload limit: {Mz800IplDskWriter.MaxIplStagedSize} B.";
            Loaded += async (_, _) => await RefreshPreviewAsync();
        }

        internal ObservableCollection<MultiGameIplRow> Entries { get; private set; } = new();

        internal MultiGameIplBuildResult? BuildResult { get; private set; }

        private async void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!updatingRows &&
                e.PropertyName is nameof(MultiGameIplRow.MenuName) or nameof(MultiGameIplRow.Compression))
            {
                await RefreshPreviewAsync();
            }
        }

        private async Task RefreshPreviewAsync()
        {
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            previewCancellation = new CancellationTokenSource();
            CancellationTokenSource cancellation = previewCancellation;
            CancellationToken token = cancellation.Token;
            BuildResult = null;
            saveButton.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            analysisTextBlock.Foreground = SystemColors.ControlTextBrush;
            analysisTextBlock.Text = $"Preparing 0/{Entries.Count} programs...";

            if (Entries.Count == 0)
            {
                analysisTextBlock.Text = "Add MZF programs to create the multi-program IPL disk.";
                capacityTextBlock.Text =
                    $"Logical disk capacity: {Mz800DskImage.LogicalSectorCount} sectors / " +
                    $"{Mz800DskImage.LogicalSectorCount * Mz800DskImage.SectorSize:N0} B.";
                progressBar.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var inputs = new List<MultiGameIplInput>(Entries.Count);
                for (int index = 0; index < Entries.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    MultiGameIplRow row = Entries[index];
                    analysisTextBlock.Text =
                        $"Analyzing {index + 1}/{Entries.Count}: {row.MenuName} ({row.Compression})...";
                    await Task.Yield();
                    MzfCompressionResult result = await row.PrepareAsync(token);
                    inputs.Add(new MultiGameIplInput(
                        result.Record,
                        row.MenuName,
                        result.AppliedOptions,
                        result.OriginalSize));
                }

                analysisTextBlock.Text = "Building layout and checking DSK capacity...";
                MultiGameIplBuildResult build = await Task.Run(
                    () => Mz800MultiGameIplDskWriter.Build(inputs),
                    token);
                token.ThrowIfCancellationRequested();

                updatingRows = true;
                for (int index = 0; index < Entries.Count; index++)
                {
                    Entries[index].Apply(build.Entries[index]);
                }
                updatingRows = false;
                BuildResult = build;
                int payloadBytes = build.Entries.Sum(entry => entry.Size);
                int contentBytes = Mz800DskImage.SectorSize + build.MenuByteSize + payloadBytes;
                int paddingBytes = build.UsedBytes - contentBytes;
                double usedPercent = 100.0 * build.UsedSectorCount / Mz800DskImage.LogicalSectorCount;
                analysisTextBlock.Text = $"Ready: {build.Entries.Count} programs analyzed.";
                capacityTextBlock.Text =
                    $"Disk allocation: {build.UsedSectorCount} / {Mz800DskImage.LogicalSectorCount} sectors " +
                    $"({build.UsedBytes} / {Mz800DskImage.LogicalSectorCount * Mz800DskImage.SectorSize} B, {usedPercent:F1}%). " +
                    $"Free: {build.FreeSectorCount} sectors / {build.FreeBytes} B.\n" +
                    $"Content: IPL {Mz800DskImage.SectorSize} B + menu {build.MenuByteSize} B + programs {payloadBytes} B " +
                    $"= {contentBytes} B; sector padding {paddingBytes} B. " +
                    $"Per-entry maximum: {Mz800IplDskWriter.MaxIplStagedSize} B.";
                saveButton.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    analysisTextBlock.Foreground = Brushes.Firebrick;
                    analysisTextBlock.Text = exception.GetBaseException().Message;
                }
            }
            finally
            {
                updatingRows = false;
                if (ReferenceEquals(previewCancellation, cancellation))
                {
                    progressBar.Visibility = Visibility.Collapsed;
                }
            }
        }

        private async void UpButton_Click(object sender, RoutedEventArgs e)
        {
            int[] indices = GetSelectedIndices();
            if (indices.Length == 0 || indices[0] <= 0)
            {
                return;
            }
            List<MultiGameIplRow> selected = indices.Select(index => Entries[index]).ToList();
            ListReorder.MoveItems(Entries, indices, indices[0] - 1);
            UpdateOrder();
            RestoreSelection(selected);
            await RefreshPreviewAsync();
        }

        private async void DownButton_Click(object sender, RoutedEventArgs e)
        {
            int[] indices = GetSelectedIndices();
            if (indices.Length == 0 || indices[^1] >= Entries.Count - 1)
            {
                return;
            }
            List<MultiGameIplRow> selected = indices.Select(index => Entries[index]).ToList();
            ListReorder.MoveItems(Entries, indices, indices[^1] + 2);
            UpdateOrder();
            RestoreSelection(selected);
            await RefreshPreviewAsync();
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add programs to multi-program IPL DSK",
                Filter = "MZF program (*.mzf;*.mz0;*.mz7)|*.mzf;*.mz0;*.mz7|All files (*.*)|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                foreach (string path in dialog.FileNames)
                {
                    TapeRecord record = new MZTFileReader().ReadStandaloneMzf(path);
                    string name = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);
                    var row = new MultiGameIplRow(Entries.Count + 1, record, name);
                    row.PropertyChanged += Row_PropertyChanged;
                    Entries.Add(row);
                }
                await RefreshPreviewAsync();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Multi-program IPL DSK", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            MultiGameIplRow[] selected = entriesGrid.SelectedItems.OfType<MultiGameIplRow>().ToArray();
            if (selected.Length == 0)
            {
                return;
            }
            foreach (MultiGameIplRow row in selected)
            {
                row.PropertyChanged -= Row_PropertyChanged;
                Entries.Remove(row);
            }
            UpdateOrder();
            await RefreshPreviewAsync();
        }

        private void UpdateOrder()
        {
            for (int index = 0; index < Entries.Count; index++)
            {
                Entries[index].Order = index + 1;
            }
        }

        private int[] GetSelectedIndices() => entriesGrid.SelectedItems
            .OfType<MultiGameIplRow>()
            .Select(row => Entries.IndexOf(row))
            .Where(index => index >= 0)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

        private void RestoreSelection(IEnumerable<MultiGameIplRow> rows)
        {
            MultiGameIplRow[] selected = rows.ToArray();
            entriesGrid.SelectedItems.Clear();
            foreach (MultiGameIplRow row in selected)
            {
                entriesGrid.SelectedItems.Add(row);
            }
            if (selected.Length > 0)
            {
                entriesGrid.ScrollIntoView(selected[0]);
                entriesGrid.Focus();
            }
        }

        private void EntriesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            rowDragStartPoint = e.GetPosition(entriesGrid);
            DependencyObject? source = e.OriginalSource as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(source);
            bool editorClicked = FindVisualParent<ComboBox>(source) is not null ||
                FindVisualParent<TextBox>(source) is not null;
            rowDragStartItem = editorClicked ? null : row?.Item as MultiGameIplRow;
        }

        private void EntriesGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || rowDragStartItem is null || rowDragInProgress)
            {
                return;
            }
            Point current = e.GetPosition(entriesGrid);
            if (Math.Abs(current.X - rowDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - rowDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            List<MultiGameIplRow> rows = entriesGrid.SelectedItems
                .OfType<MultiGameIplRow>()
                .OrderBy(row => Entries.IndexOf(row))
                .ToList();
            if (!rows.Contains(rowDragStartItem))
            {
                rows = [rowDragStartItem];
            }

            var data = new DataObject();
            data.SetData(RowDragDataFormat, rows);
            rowDragInProgress = true;
            try
            {
                DragDrop.DoDragDrop(entriesGrid, data, DragDropEffects.Move);
            }
            finally
            {
                ClearDropTargetIndicator();
                rowDragInProgress = false;
                rowDragStartItem = null;
            }
        }

        private void EntriesGrid_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(RowDragDataFormat))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            Point position = e.GetPosition(entriesGrid);
            UpdateDropTargetIndicator(position);
            ScrollDuringDrag(position);
        }

        private void EntriesGrid_DragLeave(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(RowDragDataFormat))
            {
                ClearDropTargetIndicator();
            }
        }

        private async void EntriesGrid_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(RowDragDataFormat) ||
                e.Data.GetData(RowDragDataFormat) is not List<MultiGameIplRow> rows)
            {
                return;
            }
            int insertionIndex = GetDropInsertionIndex(e.GetPosition(entriesGrid));
            ClearDropTargetIndicator();
            int[] indices = rows
                .Select(row => Entries.IndexOf(row))
                .Where(index => index >= 0)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            if (ListReorder.MoveItems(Entries, indices, insertionIndex))
            {
                UpdateOrder();
                RestoreSelection(rows);
                await RefreshPreviewAsync();
            }
            e.Handled = true;
        }

        private int GetDropInsertionIndex(Point position)
        {
            DependencyObject? hit = entriesGrid.InputHitTest(position) as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(hit);
            if (row is null)
            {
                return Entries.Count;
            }
            Point inRow = entriesGrid.TranslatePoint(position, row);
            return row.GetIndex() + (inRow.Y >= row.ActualHeight / 2 ? 1 : 0);
        }

        private void UpdateDropTargetIndicator(Point position)
        {
            ClearDropTargetIndicator();
            DependencyObject? hit = entriesGrid.InputHitTest(position) as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(hit);
            bool after = false;
            if (row is not null)
            {
                Point inRow = entriesGrid.TranslatePoint(position, row);
                after = inRow.Y >= row.ActualHeight / 2;
            }
            else if (Entries.Count > 0)
            {
                row = entriesGrid.ItemContainerGenerator.ContainerFromIndex(Entries.Count - 1) as DataGridRow;
                after = true;
            }
            if (row is null)
            {
                return;
            }
            rowDropTarget = row;
            row.BorderBrush = SystemColors.HighlightBrush;
            row.BorderThickness = after
                ? new Thickness(0, 0, 0, 3)
                : new Thickness(0, 3, 0, 0);
        }

        private void ClearDropTargetIndicator()
        {
            if (rowDropTarget is null)
            {
                return;
            }
            rowDropTarget.ClearValue(Control.BorderBrushProperty);
            rowDropTarget.ClearValue(Control.BorderThicknessProperty);
            rowDropTarget = null;
        }

        private void ScrollDuringDrag(Point position)
        {
            ScrollViewer? viewer = FindVisualChild<ScrollViewer>(entriesGrid);
            if (viewer is null)
            {
                return;
            }
            const double edge = 24;
            if (position.Y < edge)
            {
                viewer.LineUp();
            }
            else if (position.Y > entriesGrid.ActualHeight - edge)
            {
                viewer.LineDown();
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
        {
            while (element is not null)
            {
                if (element is T match)
                {
                    return match;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                {
                    return match;
                }
                T? descendant = FindVisualChild<T>(child);
                if (descendant is not null)
                {
                    return descendant;
                }
            }
            return null;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (BuildResult != null)
            {
                DialogResult = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            base.OnClosed(e);
        }
    }

    internal sealed class MultiGameIplRow : INotifyPropertyChanged
    {
        private static readonly string[] Choices = ["None", "ZX0", "ZX7", "Auto"];
        private int order;
        private string menuName;
        private string compression = "None";
        private string? preparedChoice;
        private MzfCompressionResult? preparedResult;
        private MzfCompressionResult? lastPreparedResult;
        private TapeRecord exportFallback;
        private int packedSize;
        private string compressionRatio = "100.0%";
        private string loadHex;
        private string execHex;
        private int startBlock;
        private int sectors;
        private string appliedCompression = "—";

        public MultiGameIplRow(int order, TapeRecord source, string menuName)
            : this(order, CreateImportedState(source), menuName)
        {
        }

        private MultiGameIplRow(int order, ImportedState imported, string menuName)
            : this(order, imported.Source, menuName, imported.OriginalSize)
        {
            exportFallback = imported.OriginalImported.DeepClone();
            if (imported.DetectedChoice == null)
            {
                return;
            }

            compression = imported.DetectedChoice;
            AppliedCompression = imported.AppliedCompression;
            if (imported.PreparedResult != null)
            {
                preparedChoice = compression;
                preparedResult = imported.PreparedResult;
                lastPreparedResult = imported.PreparedResult;
                PackedSize = imported.PreparedResult.PackedSize;
                LoadHex = $"${imported.PreparedResult.Record.Header.MzfStart:X4}";
                ExecHex = $"${imported.PreparedResult.Record.Header.MzfExec:X4}";
            }
        }

        internal MultiGameIplRow(int order, MultiGameIplInput input)
            : this(order, input.Record.DeepClone(), input.DisplayName, input.OriginalSize)
        {
            compression = input.AppliedCompression.Algorithm switch
            {
                MzfCompressionAlgorithm.Zx0 => "ZX0",
                MzfCompressionAlgorithm.Zx7 => "ZX7",
                _ => "None"
            };
            preparedChoice = compression;
            preparedResult = new MzfCompressionResult(Source, input.AppliedCompression, input.OriginalSize);
            lastPreparedResult = preparedResult;
            AppliedCompression = compression;
            CanChangeCompression = false;
        }

        private MultiGameIplRow(int order, TapeRecord source, string menuName, int originalSize)
        {
            this.order = order;
            Source = source;
            this.menuName = Mz800MultiGameIplDskWriter.NormalizeMenuName(menuName);
            exportFallback = source.DeepClone();
            OriginalSize = originalSize;
            packedSize = OriginalSize;
            loadHex = $"${source.Header.MzfStart:X4}";
            execHex = $"${source.Header.MzfExec:X4}";
        }

        internal TapeRecord Source { get; }

        public IReadOnlyList<string> CompressionChoices => Choices;

        public bool CanChangeCompression { get; } = true;

        public int Order
        {
            get => order;
            set => SetField(ref order, value);
        }

        public string MenuName
        {
            get => menuName;
            set => SetField(ref menuName, value);
        }

        public int OriginalSize { get; }

        public int MaxPayloadSize => Mz800IplDskWriter.MaxIplStagedSize;

        public int PackedSize
        {
            get => packedSize;
            private set
            {
                if (SetField(ref packedSize, value))
                {
                    CompressionRatio = OriginalSize == 0
                        ? "—"
                        : (100.0 * value / OriginalSize).ToString("F1", CultureInfo.InvariantCulture) + "%";
                }
            }
        }

        public string CompressionRatio
        {
            get => compressionRatio;
            private set => SetField(ref compressionRatio, value);
        }

        public string LoadHex
        {
            get => loadHex;
            private set => SetField(ref loadHex, value);
        }

        public string ExecHex
        {
            get => execHex;
            private set => SetField(ref execHex, value);
        }

        public string Compression
        {
            get => compression;
            set
            {
                if (SetField(ref compression, value))
                {
                    preparedChoice = null;
                    preparedResult = null;
                }
            }
        }

        public int StartBlock
        {
            get => startBlock;
            private set => SetField(ref startBlock, value);
        }

        public int Sectors
        {
            get => sectors;
            private set => SetField(ref sectors, value);
        }

        public string AppliedCompression
        {
            get => appliedCompression;
            private set => SetField(ref appliedCompression, value);
        }

        internal async Task<MzfCompressionResult> PrepareAsync(CancellationToken token)
        {
            string requestedCompression = Compression;
            if (preparedResult != null && preparedChoice == requestedCompression)
            {
                return preparedResult;
            }

            MzfCompressionOptions options = requestedCompression switch
            {
                "None" => new(MzfCompressionAlgorithm.None),
                "ZX0" => new(MzfCompressionAlgorithm.Zx0),
                "ZX7" => new(MzfCompressionAlgorithm.Zx7),
                "Auto" => new(MzfCompressionAlgorithm.Auto),
                _ => throw new InvalidOperationException($"Unsupported compression choice: {Compression}.")
            };
            MzfCompressionResult result = await MzfCompressionService.CompressAsync(
                Source,
                options,
                CompressionTarget.IplDsk,
                token);
            if (Compression == requestedCompression)
            {
                preparedChoice = requestedCompression;
                preparedResult = result;
                lastPreparedResult = result;
            }
            return result;
        }

        internal void Apply(PreparedMultiGameIplEntry entry)
        {
            MenuName = entry.DisplayName;
            PackedSize = entry.Size;
            LoadHex = $"${entry.Load:X4}";
            ExecHex = $"${entry.Exec:X4}";
            StartBlock = entry.StartBlock;
            Sectors = entry.SectorCount;
            AppliedCompression = entry.Compression;
        }

        internal void ApplySingle(int startBlock, int sectors, string appliedCompression)
        {
            PackedSize = preparedResult?.PackedSize ?? Source.Body.MzfBody.Length;
            TapeRecord prepared = preparedResult?.Record ?? Source;
            LoadHex = $"${prepared.Header.MzfStart:X4}";
            ExecHex = $"${prepared.Header.MzfExec:X4}";
            StartBlock = startBlock;
            Sectors = sectors;
            AppliedCompression = appliedCompression;
        }

        internal byte[] GetPreparedPayload() =>
            (byte[])(preparedResult?.Record.Body.MzfBody ?? lastPreparedResult?.Record.Body.MzfBody ?? exportFallback.Body.MzfBody).Clone();

        internal TapeRecord GetPreparedRecord() =>
            (preparedResult?.Record ?? lastPreparedResult?.Record ?? exportFallback).DeepClone();

        private static ImportedState CreateImportedState(TapeRecord source)
        {
            ArgumentNullException.ThrowIfNull(source);
            TapeRecord imported = source.DeepClone();
            if (!MzfLoaderBuilder.TryGetCompressionInfo(imported, out MzfCompressionInfo? info) || info == null)
            {
                return new ImportedState(imported, imported, imported.Body.MzfBody.Length, null, null, "None / unknown");
            }

            MzfDecompressionResult decompressed = MzfDecompressionService.Decompress(imported);
            int originalSize = decompressed.Record.Body.MzfBody.Length;
            var options = new MzfCompressionOptions(
                info.Algorithm,
                info.Direction,
                Zx7EmbeddedLoader: info.EmbeddedLoader);

            // An embedded ZX7 decoder lives in the MZF header, which a direct
            // IPL loader does not copy. Rebuild it as ordinary ZX7 on preview.
            if (info.EmbeddedLoader)
            {
                return new ImportedState(
                    decompressed.Record,
                    imported,
                    originalSize,
                    "ZX7",
                    null,
                    info.DisplayName + " (converting for IPL)");
            }

            return new ImportedState(
                decompressed.Record,
                imported,
                originalSize,
                info.Algorithm == MzfCompressionAlgorithm.Zx0 ? "ZX0" : "ZX7",
                new MzfCompressionResult(imported, options, originalSize),
                info.DisplayName);
        }

        private sealed record ImportedState(
            TapeRecord Source,
            TapeRecord OriginalImported,
            int OriginalSize,
            string? DetectedChoice,
            MzfCompressionResult? PreparedResult,
            string AppliedCompression);

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
