using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using static QDTool.SharpMzEncoding;

public class MzfDisplayData : INotifyPropertyChanged
{
    private string loaderType = "NORMAL";
    private string speed = "1:1";

    public string MzfFtypeName { get; set; } = string.Empty;
    public string MzfFname { get; set; } = string.Empty;
    public ushort MzfSize { get; set; }
    public string MzfSizeHex
    {
        get { return $"0x{MzfSize:X4}"; }
    }
    public string MzfStartHex { get; set; } = string.Empty;
    public string MzfExecHex { get; set; } = string.Empty;
    public string MzfHeaderDescription { get; set; } = string.Empty;
    public string TrailingData { get; set; } = string.Empty;
    public string Compression { get; set; } = string.Empty;

    public IReadOnlyList<string> LoaderTypes => QDTool.TapeProfileComponents.LoaderTypes;

    public string LoaderType
    {
        get => loaderType;
        set
        {
            if (loaderType == value)
            {
                return;
            }
            loaderType = value;
            speed = QDTool.TapeProfileComponents.NormalizeSpeed(loaderType, speed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailableSpeeds));
            OnPropertyChanged(nameof(Speed));
        }
    }

    public IReadOnlyList<string> AvailableSpeeds =>
        QDTool.TapeProfileComponents.GetAvailableSpeeds(loaderType);

    public string Speed
    {
        get => speed;
        set
        {
            string normalized = QDTool.TapeProfileComponents.NormalizeSpeed(loaderType, value);
            if (speed == normalized)
            {
                return;
            }
            speed = normalized;
            OnPropertyChanged();
        }
    }

    internal void SetProfile(QDTool.TapeProfile profile)
    {
        (loaderType, speed) = QDTool.TapeProfileComponents.Split(profile);
        OnPropertyChanged(nameof(LoaderType));
        OnPropertyChanged(nameof(AvailableSpeeds));
        OnPropertyChanged(nameof(Speed));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

namespace QDTool
{
    internal static class ListReorder
    {
        internal static bool MoveItems<T>(IList<T> items, IEnumerable<int> sourceIndices, int insertionIndex)
        {
            int[] indices = sourceIndices
                .Where(index => index >= 0 && index < items.Count)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            if (indices.Length == 0)
            {
                return false;
            }

            insertionIndex = Math.Clamp(insertionIndex, 0, items.Count);
            List<T> movedItems = indices.Select(index => items[index]).ToList();
            List<T> reordered = items.ToList();
            for (int index = indices.Length - 1; index >= 0; index--)
            {
                reordered.RemoveAt(indices[index]);
            }

            int adjustedInsertionIndex = insertionIndex - indices.Count(index => index < insertionIndex);
            reordered.InsertRange(adjustedInsertionIndex, movedItems);
            if (items.SequenceEqual(reordered))
            {
                return false;
            }

            items.Clear();
            foreach (T item in reordered)
            {
                items.Add(item);
            }
            return true;
        }
    }

    internal static class FeatureModePolicy
    {
        public static string GetOpenFilter() =>
            "All supported files|*.mzt;*.mzf;*.mz0;*.mz7;*.mzq;*.qdf;*.qd;*.dsk;*.lep;*.l16;*.wav;*.flac|Quickdisk image (*.qd)|*.qd|MZ-800 IPL floppy (*.dsk)|*.dsk|Quickdisk file (*.mzq)|*.mzq|Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf;*.mz0;*.mz7)|*.mzf;*.mz0;*.mz7|Quickdisk file (*.qdf)|*.qdf|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav|FLAC audio (*.flac)|*.flac|All files (*.*)|*.*";

        public static string GetSaveFilter() =>
            "Quickdisk file (*.qdf)|*.qdf|Quickdisk file (*.mzq)|*.mzq|Quickdisk image - HxC (*.qd)|*.qd|Quickdisk image - FlashFloppy (*.qd)|*.qd|Quickdisk image - Sharp/MZ legacy (*.qd)|*.qd|Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf)|*.mzf|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav|MZ-800 multi-game IPL floppy (*.dsk)|*.dsk|All files (*.*)|*.*";

        public static string GetExportFilter(bool singleRecord = false) =>
            "Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf)|*.mzf" +
                (singleRecord
                    ? "|MZ-800 bootable IPL floppy (*.dsk)|*.dsk"
                    : "|MZ-800 multi-game IPL floppy (*.dsk)|*.dsk") +
                "|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav";
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string RowDragDataFormat = "QDTool.TapeRecords";

        private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private readonly TapeDocument document = new();
        private List<TapeRecord> mzfBlocks => document.Records;
        private string actFileName = string.Empty;
        private bool updatingProfileEditors;
        private Point rowDragStartPoint;
        private MzfDisplayData? rowDragStartItem;
        private bool rowDragInProgress;
        private DataGridRow? rowDropTarget;

        public ObservableCollection<MzfDisplayData> MzfDisplayDataCollection { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            MzfDisplayDataCollection = new ObservableCollection<MzfDisplayData>();
            MzfDataGrid.ItemsSource = MzfDisplayDataCollection;
            this.Title = "MZTools";
            viewButton.IsEnabled = false; // Zakážem některá tlačítka při spuštění
            moveUpButton.IsEnabled = false;
            moveDownButton.IsEnabled = false;
            exportButton.IsEnabled = false;
            deleteButton.IsEnabled = false;
            saveButton.IsEnabled = true;
            UpdateQuickDiskFeatureVisibility();
            UpdateStatus();
        }

        private static bool TryValidateBlock(
            TapeRecord block,
            int index,
            out string error)
        {
            MZQFileHeader header = block.Header;
            MZQFileBody body = block.Body;

            if (header.StartSign is null || header.StartSign.Length != 4 ||
                header.MzfFname is null || header.MzfFname.Length != 16 ||
                header.Unused1 is null || header.Unused1.Length != 2 ||
                header.MzfHeaderDescription is null || header.MzfHeaderDescription.Length != 104 ||
                header.Crc is null || header.Crc.Length != 3 ||
                body.StartSign is null || body.StartSign.Length != 4 ||
                body.MzfBody is null || body.Crc is null || body.Crc.Length != 3)
            {
                error = $"File {index + 1} has an invalid or incomplete structure.";
                return false;
            }

            if (body.MzfBody.Length != body.DataSize)
            {
                error = $"File {index + 1} declares {body.DataSize} data bytes but contains {body.MzfBody.Length}.";
                return false;
            }

            if (header.MzfSize != body.DataSize)
            {
                error = $"File {index + 1} has inconsistent header ({header.MzfSize}) and body ({body.DataSize}) sizes.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private bool TryValidateAllBlocks(out string error)
        {
            for (int i = 0; i < mzfBlocks.Count; i++)
            {
                if (!TryValidateBlock(mzfBlocks[i], i, out error))
                {
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private bool TryValidateOutputFormat(TapeDocumentFormat outputFormat, string extension, out string error)
        {
            if (mzfBlocks.Count == 0 && !SupportsEmptyDocument(outputFormat))
            {
                error = $"The {extension.ToUpperInvariant()} format requires one file.";
                return false;
            }

            bool quickDiskOutput = outputFormat is TapeDocumentFormat.Mzq or TapeDocumentFormat.Qdf or
                TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy;
            bool allowImportedNonStandard = CanPreserveImportedNonStandard(outputFormat);
            if (quickDiskOutput &&
                !QuickDiskLimits.TryValidateForSave(mzfBlocks.Count, allowImportedNonStandard, out error))
            {
                return false;
            }

            if (outputFormat == TapeDocumentFormat.Qdf)
            {
                long requiredSize = QDFFileReader.HeaderSize + mzfBlocks.Sum(block => QDFFileReader.FileOverhead + block.Body.DataSize);
                if (requiredSize > QDFFileReader.ImageSize)
                {
                    error = $"The selected files need {requiredSize} bytes, but a QDF image can contain only {QDFFileReader.ImageSize} bytes.";
                    return false;
                }
            }

            if (outputFormat == TapeDocumentFormat.QdSharpLegacy &&
                !SharpLegacyQdCodec.TryValidateCapacity(mzfBlocks, out error))
            {
                return false;
            }

            if (outputFormat is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy)
            {
                QdImageFormat physicalFormat = outputFormat == TapeDocumentFormat.QdHxc
                    ? QdImageFormat.HxcPhysical
                    : QdImageFormat.FlashFloppyPhysical;
                QuickDiskPhysicalProfile? profile = outputFormat == document.Format
                    ? document.QuickDiskProfile
                    : null;
                if (!QuickDiskPhysicalWriter.TryValidateCapacity(mzfBlocks, physicalFormat, out error, profile))
                {
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private bool CanPreserveImportedNonStandard(TapeDocumentFormat outputFormat) =>
            document.IsQuickDisk && !document.IsModified && outputFormat == document.Format;

        internal static bool SupportsEmptyDocument(TapeDocumentFormat format) => format is
            TapeDocumentFormat.Qdf or TapeDocumentFormat.Mzq or TapeDocumentFormat.Mzt or
            TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy;

        internal static int GetSaveFilterIndex(TapeDocumentFormat format) => format switch
        {
            TapeDocumentFormat.Qdf => 1,
            TapeDocumentFormat.Mzq => 2,
            TapeDocumentFormat.QdHxc => 3,
            TapeDocumentFormat.QdFlashFloppy => 4,
            TapeDocumentFormat.QdSharpLegacy => 5,
            TapeDocumentFormat.Mzt => 6,
            TapeDocumentFormat.Mzf => 7,
            _ => 1
        };

        internal static TapeDocumentFormat ResolveSaveFormat(string extension, int filterIndex)
        {
            if (extension == ".qd")
            {
                return filterIndex switch
                {
                    3 => TapeDocumentFormat.QdHxc,
                    4 => TapeDocumentFormat.QdFlashFloppy,
                    5 => TapeDocumentFormat.QdSharpLegacy,
                    _ => TapeDocumentFormat.None
                };
            }

            return extension switch
            {
                ".qdf" => TapeDocumentFormat.Qdf,
                ".mzq" => TapeDocumentFormat.Mzq,
                ".mzt" => TapeDocumentFormat.Mzt,
                ".mzf" => TapeDocumentFormat.Mzf,
                _ => TapeDocumentFormat.None
            };
        }

        private static string SanitizeExportFileName(string fileName)
        {
            char[] invalidCharacters = System.IO.Path.GetInvalidFileNameChars();
            string sanitized = new string(fileName
                .Select(character => invalidCharacters.Contains(character) ? '_' : character)
                .ToArray())
                .Trim()
                .TrimEnd('.');

            if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
            {
                sanitized = "unnamed";
            }

            string firstNameSegment = sanitized.Split('.')[0];
            if (ReservedWindowsFileNames.Contains(firstNameSegment))
            {
                sanitized = "_" + sanitized;
            }

            return sanitized;
        }

        private static string GetAvailableExportPath(
            string exportPath,
            string fileName,
            HashSet<string> reservedPaths)
        {
            string safeName = SanitizeExportFileName(fileName);

            for (int suffix = 1; ; suffix++)
            {
                string candidateName = suffix == 1
                    ? $"{safeName}.mzf"
                    : $"{safeName}_{suffix}.mzf";
                string candidatePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(exportPath, candidateName));

                if (!File.Exists(candidatePath) && reservedPaths.Add(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy; // Změňte ukazatel, aby uživatel věděl, že soubor může být zde upuštěn
            else if (e.Data.GetDataPresent(RowDragDataFormat))
                e.Effects = DragDropEffects.Move;
            else
                e.Effects = DragDropEffects.None; // Jinak neumožněte drop
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files != null && files.Length > 0)
                {
                    bool bindAsCurrent = document.Format == TapeDocumentFormat.None && mzfBlocks.Count == 0;
                    foreach (var file in files)
                    {
                        if (AddFile(file, bindAsCurrent))
                        {
                            bindAsCurrent = false;
                        }
                    }
                    saveButton.IsEnabled = true;
                    UpdateStatus();
                }
            }
        }
        private void LoadDataToGrid(IEnumerable<TapeRecord> records)
        {
            foreach (TapeRecord record in records)
            {
                MZQFileHeader header = record.Header;
                var displayData = new MzfDisplayData
                {
                    MzfFtypeName = ConvertFtypeToDescription(header.MzfFtype),
                    MzfFname = ConvertMzfNameToASCIIString(header.MzfFname),
                    MzfSize = header.MzfSize,
                    MzfStartHex = $"0x{header.MzfStart:X4}",
                    MzfExecHex = $"0x{header.MzfExec:X4}",
                    MzfHeaderDescription = ConvertMzfDescriptionToASCIIString(record.DescriptionRaw),
                    TrailingData = $"{record.Body.TrailingData?.Length ?? 0} B",
                    Compression = MzfLoaderBuilder.DetectCompression(record)
                };
                displayData.SetProfile(record.Profile);
                MzfDisplayDataCollection.Add(displayData);
            }
        }

        private void UpdateStatus()
        {
            long declaredSize = 0;
            long trailingSize = 0;
            long containerTrailingSize = document.ContainerTrailingData.Length;
            long sizeOnQDF = QDFFileReader.HeaderSize;
            long sizeOnMZQ = 8;
            long sizeOnQD = 16;

            foreach (TapeRecord record in mzfBlocks)
            {
                MZQFileBody body = record.Body;
                declaredSize += body.DataSize;
                trailingSize += body.TrailingData?.Length ?? 0;
                sizeOnQDF += QDFFileReader.FileOverhead + body.DataSize;
                sizeOnMZQ += 84 + body.DataSize;
                sizeOnQD += 84 + body.DataSize;
            }

            long occupiedSize = declaredSize + trailingSize + containerTrailingSize;
            string trailingInfo = trailingSize > 0 || containerTrailingSize > 0
                ? $" ({declaredSize} declared + {trailingSize} record trailing + {containerTrailingSize} container trailing)"
                : string.Empty;
            infoText.Content = $"Total {mzfBlocks.Count} files occupy {occupiedSize} bytes{trailingInfo}, est. {(float)sizeOnQDF / 819.36:F0}% QDF, {(float)sizeOnMZQ / 614.71:F0}% MZQ or {(float)sizeOnQD / 614.55:F0}% QD.";

            qdInfoText.Text = document.IplDskInfo is { } iplInfo
                ? GetIplDskAdvancedStatus(iplInfo)
                : GetQdAdvancedStatus(document.Format, mzfBlocks.Count);
            UpdateQuickDiskFeatureVisibility();
        }

        internal static string GetIplDskAdvancedStatus(Mz800IplDskInfo info) =>
            $"Disk image type: MZ-800 IPLPRO (Extended CPC DSK) | " +
            $"Free capacity: {info.FreeCapacityBytes} B ({info.FreeSectorCount} sectors) / " +
            $"{info.PayloadCapacityBytes} B payload area";

        internal static string GetQdAdvancedStatus(TapeDocumentFormat format, int fileCount)
        {
            string qdType = format switch
            {
                TapeDocumentFormat.QdSharpLegacy => "Sharp legacy logical",
                TapeDocumentFormat.QdHxc => "HxC physical",
                TapeDocumentFormat.QdFlashFloppy => "FlashFloppy physical",
                _ => string.Empty
            };
            if (qdType.Length == 0)
            {
                return "Quickdisk image type: —";
            }

            string warning = fileCount > QuickDiskLimits.StandardDirectoryEntries
                ? $" | Warning: exceeds the standard MZ-800 directory buffer ({QuickDiskLimits.StandardDirectoryEntries}); format supports up to {QuickDiskLimits.FormatMaximumFiles}."
                : string.Empty;
            return $"Quickdisk image type: {qdType} | Files: {fileCount} / {QuickDiskLimits.StandardDirectoryEntries}{warning}";
        }

        private void UpdateQuickDiskFeatureVisibility()
        {
            bool quickDiskAvailable = document.IsQdImage;
            bool diskInfoAvailable = quickDiskAvailable ||
                document.IplDskInfo != null;
            qdInfoText.Visibility = diskInfoAvailable ? Visibility.Visible : Visibility.Collapsed;
            formatQuickDiskButton.Visibility = quickDiskAvailable ? Visibility.Visible : Visibility.Collapsed;
            formatQuickDiskButton.IsEnabled = quickDiskAvailable;
        }

        private bool TryChooseTapeSaveOptions(
            string mainPath,
            TapeDocumentFormat format,
            int trailingBytes,
            out bool preserveTrailing,
            out bool generateSidecar)
        {
            preserveTrailing = false;
            generateSidecar = false;
            string sidecarPath = SidecarService.GetSidecarPath(mainPath);
            var dialog = new SaveOptionsDialog(
                format,
                trailingBytes,
                sidecarAlreadyExists: File.Exists(sidecarPath))
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            preserveTrailing = dialog.PreserveTrailing;
            generateSidecar = dialog.GenerateSidecar;
            return true;
        }

        private bool TryChooseTapeExportOptions(
            string mainPath,
            TapeDocumentFormat format,
            int trailingBytes,
            IReadOnlyList<(int Index, TapeRecord Record)> records,
            out bool preserveTrailing,
            out bool generateSidecar,
            out List<TapeRecord> packedRecords)
        {
            preserveTrailing = false;
            generateSidecar = false;
            packedRecords = records.Select(value => value.Record.DeepClone()).ToList();
            string sidecarPath = SidecarService.GetSidecarPath(mainPath);
            var dialog = new SaveOptionsDialog(
                format,
                trailingBytes,
                sidecarAlreadyExists: File.Exists(sidecarPath),
                format == TapeDocumentFormat.Mzt
                    ? CompressionTarget.MztTape
                    : CompressionTarget.MzfTape,
                records)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            preserveTrailing = dialog.PreserveTrailing;
            generateSidecar = dialog.GenerateSidecar;
            packedRecords = dialog.PackedRecords.ToList();
            return true;
        }

        private bool TryChooseWaveformSaveOptions(
            string extension,
            int recordCount,
            out SharpTapeMachine machine,
            out bool separateFiles)
        {
            machine = SharpTapeMachine.Mz800;
            separateFiles = false;
            var dialog = new SaveOptionsDialog(extension, recordCount)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            machine = dialog.SelectedMachine;
            separateFiles = dialog.SeparateFiles;
            return true;
        }

        private bool ExportWaveform(
            string selectedPath,
            IReadOnlyList<TapeRecord> records,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine,
            bool separateFiles)
        {
            try
            {
                if (!separateFiles || records.Count == 1)
                {
                    SharpTapeExporter.Export(selectedPath, records, format, machine);
                    return true;
                }

                IReadOnlyList<string> outputPaths =
                    SharpTapeExporter.GetSeparateOutputPaths(selectedPath, records);
                string[] existingPaths = outputPaths.Where(File.Exists).ToArray();
                bool overwrite = false;
                if (existingPaths.Length > 0)
                {
                    MessageBoxResult result = MessageBox.Show(
                        this,
                        $"{existingPaths.Length} separate output file(s) already exist. Overwrite them?",
                        "Overwrite separate files",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                    {
                        return false;
                    }
                    overwrite = true;
                }

                SharpTapeExporter.ExportSeparate(
                    selectedPath,
                    records,
                    format,
                    machine,
                    overwrite);
                MessageBox.Show(
                    this,
                    $"Created {outputPaths.Count} separate files in:\n{System.IO.Path.GetDirectoryName(outputPaths[0])}",
                    "Separate export complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error saving tape output", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private string GetOpenFilter() => FeatureModePolicy.GetOpenFilter();

        private string GetSaveFilter() => FeatureModePolicy.GetSaveFilter();

        private string GetExportFilter(int recordCount) =>
            FeatureModePolicy.GetExportFilter(recordCount == 1);

        private List<(int Index, TapeRecord Record)> GetSelectedRecordsInGridOrder()
        {
            return MzfDataGrid.SelectedItems
                .OfType<MzfDisplayData>()
                .Select(item => MzfDisplayDataCollection.IndexOf(item))
                .Where(index => index >= 0 && index < mzfBlocks.Count)
                .Distinct()
                .OrderBy(index => index)
                .Select(index => (index, mzfBlocks[index]))
                .ToList();
        }

        private void ExportRecords(
            IReadOnlyList<(int Index, TapeRecord Record)> selectedRecords,
            string suggestedFileName)
        {
            if (selectedRecords.Count == 0)
            {
                return;
            }

            foreach ((int index, TapeRecord record) in selectedRecords)
            {
                if (!TryValidateBlock(record, index, out string validationError))
                {
                    MessageBox.Show(validationError, "Cannot export", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            List<TapeRecord> records = selectedRecords
                .Select(value => value.Record)
                .ToList();

            bool multipleRecords = records.Count > 1;
            var saveFileDialog = new SaveFileDialog
            {
                Filter = GetExportFilter(records.Count),
                AddExtension = true,
                DefaultExt = multipleRecords ? ".mzt" : ".mzf",
                FilterIndex = !multipleRecords ? 2 : 1,
                FileName = suggestedFileName
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                return;
            }

            string filePath = saveFileDialog.FileName;
            string fileExtension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                if (fileExtension == ".dsk")
                {
                    if (records.Count == 1)
                    {
                        var dialog = new IplDskOptionsDialog(records[0]) { Owner = this };
                        if (dialog.ShowDialog() != true || dialog.PackedRecord == null)
                        {
                            return;
                        }
                        Mz800IplDskWriter.WriteFile(filePath, dialog.PackedRecord, dialog.BootName);
                    }
                    else
                    {
                        var dialog = new MultiGameIplDskOptionsDialog(records) { Owner = this };
                        if (dialog.ShowDialog() != true || dialog.BuildResult == null)
                        {
                            return;
                        }
                        File.WriteAllBytes(filePath, dialog.BuildResult.Image);
                    }
                    return;
                }

                if (fileExtension == ".mzt")
                {
                    if (!TryChooseTapeExportOptions(
                        filePath,
                        TapeDocumentFormat.Mzt,
                        trailingBytes: 0,
                        selectedRecords,
                        out _,
                        out bool generateSidecar,
                        out List<TapeRecord> exportRecords))
                    {
                        return;
                    }

                    TapeDocumentWriter.SaveMzt(
                        filePath,
                        exportRecords,
                        generateSidecar);
                    return;
                }

                if (fileExtension == ".mzf")
                {
                    if (records.Count == 1)
                    {
                        int trailingBytes = records[0].Body.TrailingData?.Length ?? 0;
                        if (!TryChooseTapeExportOptions(
                            filePath,
                            TapeDocumentFormat.Mzf,
                            trailingBytes,
                            selectedRecords,
                            out bool preserve,
                            out bool generateSidecar,
                            out List<TapeRecord> exportRecords))
                        {
                            return;
                        }

                        TapeRecord exportRecord = exportRecords[0];
                        TapeDocumentWriter.SaveMzf(
                            filePath,
                            exportRecord,
                            preserve,
                            generateSidecar);
                        return;
                    }

                    int totalTrailingBytes = records.Sum(record => record.Body.TrailingData?.Length ?? 0);
                    if (!TryChooseTapeExportOptions(
                        filePath,
                        TapeDocumentFormat.Mzf,
                        totalTrailingBytes,
                        selectedRecords,
                        out bool preserveMultiple,
                        out bool generateMultipleSidecars,
                        out List<TapeRecord> separateRecords))
                    {
                        return;
                    }

                    IReadOnlyList<string> outputPaths =
                        SharpTapeExporter.GetSeparateOutputPaths(filePath, records);
                    string[] existingPaths = outputPaths.Where(File.Exists).ToArray();
                    if (existingPaths.Length > 0)
                    {
                        MessageBoxResult overwrite = MessageBox.Show(
                            this,
                            $"{existingPaths.Length} separate MZF file(s) already exist. Overwrite them?",
                            "Overwrite separate files",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (overwrite != MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }

                    for (int index = 0; index < separateRecords.Count; index++)
                    {
                        TapeDocumentWriter.SaveMzf(
                            outputPaths[index],
                            separateRecords[index],
                            preserveMultiple,
                            generateMultipleSidecars);
                    }

                    MessageBox.Show(
                        this,
                        $"Created {outputPaths.Count} separate MZF files in:\n{System.IO.Path.GetDirectoryName(outputPaths[0])}",
                        "Separate export complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (fileExtension is ".lep" or ".l16" or ".wav")
                {
                    if (!TryChooseWaveformSaveOptions(
                        fileExtension,
                        records.Count,
                        out SharpTapeMachine machine,
                        out bool separateFiles))
                    {
                        return;
                    }

                    ExportWaveform(
                        filePath,
                        records,
                        SharpTapeExporter.GetFormat(fileExtension),
                        machine,
                        separateFiles);
                    return;
                }

                MessageBox.Show(
                    $"I do not know how to export a file with extension {fileExtension}.",
                    "Unknown file extension",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.GetBaseException().Message,
                    "Error exporting files",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void button_Click_Open(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = GetOpenFilter();

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;
                if (!AddFile(filePath, bindAsCurrent: true, showIplImportDialog: true))
                {
                    return;
                }

                saveButton.IsEnabled = true;

                UpdateStatus();
            }
        }

        private void button_Click_Save(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = GetSaveFilter();
            string filenameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(actFileName);
            saveFileDialog.FileName = filenameWithoutExtension;
            saveFileDialog.FilterIndex = GetSaveFilterIndex(document.Format);

            if (saveFileDialog.ShowDialog() == true)
            {
                string filePath = saveFileDialog.FileName;
                string fileExtension = System.IO.Path.GetExtension(filePath).ToLower();
                TapeDocumentFormat outputFormat = ResolveSaveFormat(fileExtension, saveFileDialog.FilterIndex);

                bool waveformOutput = fileExtension is ".lep" or ".l16" or ".wav";
                bool multiGameDskOutput = fileExtension == ".dsk";
                if (outputFormat == TapeDocumentFormat.None && !waveformOutput && !multiGameDskOutput)
                {
                    MessageBox.Show($"The selected filter does not define a writer for {fileExtension}.", "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!TryValidateAllBlocks(out string validationError))
                {
                    MessageBox.Show(validationError, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (waveformOutput && mzfBlocks.Count == 0)
                {
                    MessageBox.Show("Waveform output requires at least one file.", "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!waveformOutput && !multiGameDskOutput &&
                    !TryValidateOutputFormat(outputFormat, fileExtension, out validationError))
                {
                    MessageBox.Show(validationError, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    bool allowImportedNonStandard = CanPreserveImportedNonStandard(outputFormat);
                    if (multiGameDskOutput)
                    {
                        if (mzfBlocks.Count < 2)
                        {
                            MessageBox.Show(
                                "A multi-game IPL DSK requires at least two records. For one record, use Export and the single-program IPL target.",
                                "Cannot save",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                            return;
                        }

                        var optionsDialog = new MultiGameIplDskOptionsDialog(mzfBlocks)
                        {
                            Owner = this
                        };
                        if (optionsDialog.ShowDialog() != true || optionsDialog.BuildResult == null)
                        {
                            return;
                        }
                        File.WriteAllBytes(filePath, optionsDialog.BuildResult.Image);
                    }
                    else if (outputFormat == TapeDocumentFormat.Mzq)
                    {
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            MZQFileReader mzqf = new MZQFileReader();
                            mzqf.WriteMZQHeaderToFile(fileStream, checked((byte)(mzfBlocks.Count * 2)), allowImportedNonStandard);
                            foreach (TapeRecord record in mzfBlocks)
                            {
                                mzqf.WriteMZQFileHeaderToFile(fileStream, record.Header);
                                mzqf.WriteMZQFileBodyToFile(fileStream, record.Body);
                            }
                        }
                        DiscardMetadataNotStoredByCurrentFormat();
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Mzq, sidecarPath: null);
                    }
                    else if (outputFormat == TapeDocumentFormat.Qdf)
                    {
                        File.WriteAllBytes(filePath, QDFFileReader.BuildImage(mzfBlocks, allowImportedNonStandard));
                        DiscardMetadataNotStoredByCurrentFormat();
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Qdf, sidecarPath: null);
                    }
                    else if (outputFormat is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy or TapeDocumentFormat.QdSharpLegacy)
                    {
                        QdImageFormat qdFormat = outputFormat switch
                        {
                            TapeDocumentFormat.QdHxc => QdImageFormat.HxcPhysical,
                            TapeDocumentFormat.QdFlashFloppy => QdImageFormat.FlashFloppyPhysical,
                            _ => QdImageFormat.SharpLegacyLogical
                        };
                        QuickDiskPhysicalProfile? profile = outputFormat == document.Format
                            ? document.QuickDiskProfile
                            : null;
                        File.WriteAllBytes(filePath, QdImageReaderWriter.Write(
                            mzfBlocks,
                            qdFormat,
                            profile,
                            allowImportedNonStandard));
                        DiscardMetadataNotStoredByCurrentFormat();
                        SetCurrentDocumentAfterSave(
                            filePath,
                            outputFormat,
                            sidecarPath: null,
                            profile ?? (qdFormat is QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical
                                ? QuickDiskPhysicalProfile.For(qdFormat)
                                : null));
                    }
                    else if (outputFormat == TapeDocumentFormat.Mzf)
                    {
                        if (mzfBlocks.Count > 1)
                        {
                            MessageBox.Show("MZF file should contain only one tape file. Please use Export button or Save as MZT file.", "MZF file limitation", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                        int trailingBytes = mzfBlocks[0].Body.TrailingData?.Length ?? 0;
                        if (!TryChooseTapeSaveOptions(
                            filePath,
                            TapeDocumentFormat.Mzf,
                            trailingBytes,
                            out bool preserve,
                            out bool generateSidecar))
                        {
                            return;
                        }
                        TapeDocumentWriter.SaveMzf(filePath, mzfBlocks[0], preserve, generateSidecar);
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Mzf, GetExistingSidecarPath(filePath));
                    }
                    else if (outputFormat == TapeDocumentFormat.Mzt)
                    {
                        bool generateSidecar = false;
                        if (mzfBlocks.Count > 0 && !TryChooseTapeSaveOptions(
                            filePath,
                            TapeDocumentFormat.Mzt,
                            trailingBytes: 0,
                            out _,
                            out generateSidecar))
                        {
                            return;
                        }
                        TapeDocumentWriter.SaveMzt(filePath, mzfBlocks, generateSidecar);
                        document.ContainerTrailingData = Array.Empty<byte>();
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Mzt, GetExistingSidecarPath(filePath));
                    }
                    else if (fileExtension == ".lep" || fileExtension == ".l16" || fileExtension == ".wav")
                    {
                        if (!TryChooseWaveformSaveOptions(
                            fileExtension,
                            mzfBlocks.Count,
                            out SharpTapeMachine machine,
                            out bool separateFiles))
                        {
                            return;
                        }
                        ExportWaveform(
                            filePath,
                            mzfBlocks,
                            SharpTapeExporter.GetFormat(fileExtension),
                            machine,
                            separateFiles);
                    }
                    else
                    {
                        MessageBox.Show($"I do not know how to save file with extension {fileExtension} (yet).", "Unknown file extension", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error saving file", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static string? GetExistingSidecarPath(string filePath)
        {
            string sidecar = SidecarService.GetSidecarPath(filePath);
            return File.Exists(sidecar) ? sidecar : null;
        }

        private void SetCurrentDocumentAfterSave(
            string filePath,
            TapeDocumentFormat format,
            string? sidecarPath,
            QuickDiskPhysicalProfile? quickDiskProfile = null)
        {
            document.FilePath = System.IO.Path.GetFullPath(filePath);
            document.Format = format;
            document.SidecarPath = sidecarPath;
            document.QuickDiskProfile = quickDiskProfile;
            document.IplDskInfo = null;
            document.IsModified = false;
            actFileName = System.IO.Path.GetFileName(filePath);
            Title = $"MZTools - {actFileName}";
            RefreshGrid();
        }

        private void DiscardMetadataNotStoredByCurrentFormat()
        {
            foreach (TapeRecord record in mzfBlocks)
            {
                record.RemoveTrailingData();
                record.ResetMetadataToImplicit();
            }
            document.ContainerTrailingData = Array.Empty<byte>();
        }

        private void button_Click_View(object sender, RoutedEventArgs e)
        {
            HexBrowser hexBrowserWindow = new HexBrowser();

            int selectedIndex = MzfDataGrid.SelectedIndex; // Získání indexu vybraného řádku

            if (selectedIndex >= 0 && selectedIndex < mzfBlocks.Count)
            {
                TapeRecord selectedRecord = mzfBlocks[selectedIndex];
                hexBrowserWindow.ShowHexDump(selectedRecord, true);
            }

            hexBrowserWindow.ShowDialog(); // Zobrazí HexBrowser jako modální dialogové okno
        }

        private void MzfDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Tlačítko "View" bude aktivní pouze pokud je vybrán nějaký řádek
            viewButton.IsEnabled = MzfDataGrid.SelectedItem != null;
            exportButton.IsEnabled = MzfDataGrid.SelectedItem != null;
            deleteButton.IsEnabled = MzfDataGrid.SelectedItem != null;

            int[] selectedIndices = GetSelectedGridIndices();
            moveUpButton.IsEnabled = selectedIndices.Length > 0 && selectedIndices[0] > 0;
            moveDownButton.IsEnabled = selectedIndices.Length > 0 && selectedIndices[^1] < mzfBlocks.Count - 1;
        }

        private void MzfDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                MzfDataGrid.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && MzfDataGrid.SelectedItems.Count > 0)
            {
                button_Click_Delete(deleteButton, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void MzfDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            rowDragStartPoint = e.GetPosition(MzfDataGrid);
            DependencyObject? source = e.OriginalSource as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(source);
            bool editorClicked = FindVisualParent<ComboBox>(source) is not null;
            if (editorClicked && row is not null && !row.IsSelected &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                {
                    MzfDataGrid.SelectedItems.Clear();
                }
                row.IsSelected = true;
            }
            rowDragStartItem = editorClicked ? null : row?.Item as MzfDisplayData;
        }

        private void MzfDataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || rowDragStartItem is null || rowDragInProgress)
            {
                return;
            }

            Point currentPosition = e.GetPosition(MzfDataGrid);
            if (Math.Abs(currentPosition.X - rowDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - rowDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            List<MzfDisplayData> draggedDisplayItems = MzfDataGrid.SelectedItems
                .OfType<MzfDisplayData>()
                .Where(item => MzfDisplayDataCollection.Contains(item))
                .OrderBy(item => MzfDisplayDataCollection.IndexOf(item))
                .ToList();
            if (!draggedDisplayItems.Contains(rowDragStartItem))
            {
                draggedDisplayItems.Clear();
                draggedDisplayItems.Add(rowDragStartItem);
            }

            List<TapeRecord> draggedRecords = draggedDisplayItems
                .Select(item => MzfDisplayDataCollection.IndexOf(item))
                .Where(index => index >= 0 && index < mzfBlocks.Count)
                .Select(index => mzfBlocks[index])
                .ToList();
            if (draggedRecords.Count == 0)
            {
                return;
            }

            var dragData = new DataObject();
            dragData.SetData(RowDragDataFormat, draggedRecords);
            rowDragInProgress = true;
            try
            {
                DragDrop.DoDragDrop(MzfDataGrid, dragData, DragDropEffects.Move);
            }
            finally
            {
                ClearDropTargetIndicator();
                rowDragInProgress = false;
                rowDragStartItem = null;
            }
        }

        private void MzfDataGrid_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(RowDragDataFormat))
            {
                return;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            Point position = e.GetPosition(MzfDataGrid);
            UpdateDropTargetIndicator(position);
            ScrollDataGridDuringDrag(position);
        }

        private void MzfDataGrid_DragLeave(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(RowDragDataFormat))
            {
                ClearDropTargetIndicator();
            }
        }

        private void MzfDataGrid_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(RowDragDataFormat) ||
                e.Data.GetData(RowDragDataFormat) is not List<TapeRecord> draggedRecords)
            {
                return;
            }

            int insertionIndex = GetDropInsertionIndex(e.GetPosition(MzfDataGrid));
            ClearDropTargetIndicator();
            int[] sourceIndices = draggedRecords
                .Select(record => mzfBlocks.IndexOf(record))
                .Where(index => index >= 0)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();

            bool changed = ListReorder.MoveItems(mzfBlocks, sourceIndices, insertionIndex);
            if (changed)
            {
                document.IsModified = true;
                RefreshGrid();
            }
            RestoreGridSelection(draggedRecords);

            e.Effects = changed ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private int GetDropInsertionIndex(Point position)
        {
            DependencyObject? hit = MzfDataGrid.InputHitTest(position) as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(hit);
            if (row is null)
            {
                return mzfBlocks.Count;
            }

            Point positionInRow = MzfDataGrid.TranslatePoint(position, row);
            return row.GetIndex() + (positionInRow.Y >= row.ActualHeight / 2 ? 1 : 0);
        }

        private void UpdateDropTargetIndicator(Point position)
        {
            ClearDropTargetIndicator();

            DependencyObject? hit = MzfDataGrid.InputHitTest(position) as DependencyObject;
            DataGridRow? row = FindVisualParent<DataGridRow>(hit);
            bool insertAfter = false;
            if (row is not null)
            {
                Point positionInRow = MzfDataGrid.TranslatePoint(position, row);
                insertAfter = positionInRow.Y >= row.ActualHeight / 2;
            }
            else if (mzfBlocks.Count > 0)
            {
                row = MzfDataGrid.ItemContainerGenerator.ContainerFromIndex(mzfBlocks.Count - 1) as DataGridRow;
                insertAfter = true;
            }

            if (row is null)
            {
                return;
            }

            rowDropTarget = row;
            row.BorderBrush = SystemColors.HighlightBrush;
            row.BorderThickness = insertAfter
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

        private int[] GetSelectedGridIndices() => MzfDataGrid.SelectedItems
            .OfType<MzfDisplayData>()
            .Select(item => MzfDisplayDataCollection.IndexOf(item))
            .Where(index => index >= 0 && index < mzfBlocks.Count)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

        private void RestoreGridSelection(IEnumerable<TapeRecord> records)
        {
            int[] indices = records
                .Select(record => mzfBlocks.IndexOf(record))
                .Where(index => index >= 0)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            if (indices.Length == 0)
            {
                return;
            }

            MzfDataGrid.SelectedItems.Clear();
            foreach (int index in indices)
            {
                MzfDataGrid.SelectedItems.Add(MzfDisplayDataCollection[index]);
            }
            MzfDataGrid.ScrollIntoView(MzfDisplayDataCollection[indices[0]]);
            MzfDataGrid.Focus();
        }

        private void ScrollDataGridDuringDrag(Point position)
        {
            ScrollViewer? scrollViewer = FindVisualChild<ScrollViewer>(MzfDataGrid);
            if (scrollViewer is null)
            {
                return;
            }

            const double edgeSize = 24;
            if (position.Y < edgeSize)
            {
                scrollViewer.LineUp();
            }
            else if (position.Y > MzfDataGrid.ActualHeight - edgeSize)
            {
                scrollViewer.LineDown();
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

        private void LoaderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyProfileFromComboBox(sender, loaderChanged: true);
        }

        private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyProfileFromComboBox(sender, loaderChanged: false);
        }

        private void ProfileComboBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ComboBox comboBox &&
                comboBox.DataContext is MzfDisplayData displayData &&
                MzfDataGrid.SelectedItems.Count > 1 &&
                MzfDataGrid.SelectedItems.Contains(displayData))
            {
                // Keep an existing multi-selection while opening an editor in
                // one of its rows; the DataGrid would otherwise collapse it.
                e.Handled = true;
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
            }
        }

        private void ApplyProfileFromComboBox(object sender, bool loaderChanged)
        {
            if (updatingProfileEditors ||
                sender is not ComboBox comboBox ||
                !comboBox.IsKeyboardFocusWithin ||
                comboBox.DataContext is not MzfDisplayData source)
            {
                return;
            }

            string loaderType = loaderChanged
                ? comboBox.SelectedItem as string ?? source.LoaderType
                : source.LoaderType;
            string speed = loaderChanged
                ? TapeProfileComponents.NormalizeSpeed(loaderType, source.Speed)
                : comboBox.SelectedItem as string ?? source.Speed;
            TapeProfile profile = TapeProfileComponents.Combine(loaderType, speed);
            List<MzfDisplayData> targets = MzfDataGrid.SelectedItems
                .OfType<MzfDisplayData>()
                .ToList();
            if (!targets.Contains(source))
            {
                targets.Clear();
                targets.Add(source);
            }

            updatingProfileEditors = true;
            try
            {
                foreach (MzfDisplayData target in targets)
                {
                    int index = MzfDisplayDataCollection.IndexOf(target);
                    if (index < 0 || index >= mzfBlocks.Count)
                    {
                        continue;
                    }

                    TapeRecord record = mzfBlocks[index];
                    record.Profile = profile;
                    record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                    target.SetProfile(record.Profile);
                }
                if (targets.Count > 0)
                {
                    document.IsModified = true;
                }
            }
            finally
            {
                updatingProfileEditors = false;
            }
        }

        private void button_Click_Up(object sender, RoutedEventArgs e)
        {
            int[] selectedIndices = GetSelectedGridIndices();
            if (selectedIndices.Length > 0 && selectedIndices[0] > 0)
            {
                List<TapeRecord> selectedRecords = selectedIndices.Select(index => mzfBlocks[index]).ToList();
                if (ListReorder.MoveItems(mzfBlocks, selectedIndices, selectedIndices[0] - 1))
                {
                    document.IsModified = true;
                    RefreshGrid();
                    RestoreGridSelection(selectedRecords);
                }
            }
        }

        private void button_Click_Down(object sender, RoutedEventArgs e)
        {
            int[] selectedIndices = GetSelectedGridIndices();
            if (selectedIndices.Length > 0 && selectedIndices[^1] < mzfBlocks.Count - 1)
            {
                List<TapeRecord> selectedRecords = selectedIndices.Select(index => mzfBlocks[index]).ToList();
                if (ListReorder.MoveItems(mzfBlocks, selectedIndices, selectedIndices[^1] + 2))
                {
                    document.IsModified = true;
                    RefreshGrid();
                    RestoreGridSelection(selectedRecords);
                }
            }
        }

        private bool AddFile(
            string filePath,
            bool bindAsCurrent = false,
            bool showIplImportDialog = false,
            WavImportMode? selectedAudioImportMode = null)
        {
            string fileExtension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            WavImportMode wavImportMode = selectedAudioImportMode ?? WavImportMode.Standard;
            if ((fileExtension is ".wav" or ".flac") &&
                !selectedAudioImportMode.HasValue)
            {
                string sourceFormat = fileExtension == ".flac" ? "FLAC" : "WAV";
                var options = new WavImportOptionsWindow(sourceFormat) { Owner = this };
                if (options.ShowDialog() != true)
                {
                    return false;
                }
                wavImportMode = options.ImportMode;
            }
            var recordsToAdd = new List<TapeRecord>();
            byte[] containerTrailing = Array.Empty<byte>();
            string? loadedSidecar = null;
            QuickDiskPhysicalProfile? loadedQdProfile = null;
            Mz800IplDskInfo? loadedIplDskInfo = null;
            TapeDocumentFormat format;

            try
            {
                if (fileExtension == ".mzq")
                {
                    format = TapeDocumentFormat.Mzq;
                    recordsToAdd.AddRange(new MZQFileReader().ReadFile(filePath)
                        .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2)));
                }
                else if (fileExtension == ".qdf")
                {
                    format = TapeDocumentFormat.Qdf;
                    recordsToAdd.AddRange(new QDFFileReader().ReadFile(filePath)
                        .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2)));
                }
                else if (fileExtension == ".qd")
                {
                    QdReadResult result = QdImageReaderWriter.ReadFile(filePath);
                    format = result.DocumentFormat;
                    loadedQdProfile = result.PhysicalProfile;
                    recordsToAdd.AddRange(result.Records);
                }
                else if (fileExtension == ".dsk")
                {
                    Mz800IplDskReadResult result = Mz800IplDskReader.ReadFile(filePath);
                    format = TapeDocumentFormat.Mzf;
                    loadedIplDskInfo = result.DiskInfo;
                    recordsToAdd.Add(result.Record);
                }
                else if (fileExtension is ".mzf" or ".mz0" or ".mz7")
                {
                    format = TapeDocumentFormat.Mzf;
                    TapeRecord record = new MZTFileReader().ReadStandaloneMzf(filePath);
                    loadedSidecar = SidecarService.LoadForMzf(filePath, record);
                    recordsToAdd.Add(record);
                }
                else if (fileExtension == ".mzt")
                {
                    format = TapeDocumentFormat.Mzt;
                    MztReadResult result = new MZTFileReader().ReadMzt(filePath);
                    recordsToAdd.AddRange(result.Records);
                    containerTrailing = result.ContainerTrailingData;
                    loadedSidecar = SidecarService.LoadForMzt(filePath, recordsToAdd);
                }
                else if (fileExtension is ".wav" or ".flac" or ".lep" or ".l16")
                {
                    if ((fileExtension is ".wav" or ".flac") &&
                        wavImportMode == WavImportMode.Heuristic)
                    {
                        var progressWindow = new WavAnalysisProgressWindow(filePath) { Owner = this };
                        bool? analysisAccepted = progressWindow.ShowDialog();
                        bool standardFallbackUsed = false;
                        if (progressWindow.AnalysisError != null)
                        {
                            if (progressWindow.AnalysisError is InvalidDataException analysisError &&
                                analysisError.Message.Contains("no safe recovery", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    recordsToAdd.AddRange(SharpTapeImporter.ReadFile(filePath));
                                    MessageBox.Show(
                                        this,
                                        "Heuristic analysis was inconclusive, but the checksum-valid standard decoder imported the audio successfully.",
                                        "Audio imported with standard decoder",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Information);
                                    standardFallbackUsed = true;
                                }
                                catch (Exception standardException)
                                {
                                    throw new InvalidDataException(
                                        $"Heuristic audio analysis was inconclusive ({analysisError.Message}) " +
                                        $"and the standard checksum-valid decoder also failed: {standardException.Message}",
                                        standardException);
                                }
                            }
                            if (!standardFallbackUsed)
                            {
                                throw new InvalidDataException(
                                    $"Heuristic audio analysis failed: {progressWindow.AnalysisError.Message}",
                                    progressWindow.AnalysisError);
                            }
                        }
                        if (!standardFallbackUsed &&
                            (analysisAccepted != true || progressWindow.AnalysisResult == null))
                        {
                            return false;
                        }

                        if (!standardFallbackUsed)
                        {
                            WavHeuristicAnalysisResult analysis = progressWindow.AnalysisResult!;
                            recordsToAdd.AddRange(analysis.Records);
                            new WavAnalysisStatisticsWindow(analysis.Statistics)
                            {
                                Owner = this
                            }.ShowDialog();
                        }
                    }
                    else
                    {
                        recordsToAdd.AddRange(SharpTapeImporter.ReadFile(filePath));
                    }
                    format = recordsToAdd.Count == 1 ? TapeDocumentFormat.Mzf : TapeDocumentFormat.Mzt;
                }
                else
                {
                    MessageBox.Show($"I do not know how to process file with extension {fileExtension} (yet).", "Unknown file extension", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                if (!bindAsCurrent && document.IsQuickDisk &&
                    mzfBlocks.Count + recordsToAdd.Count > QuickDiskLimits.StandardDirectoryEntries)
                {
                    MessageBox.Show(
                        $"A standard MZ-800 QuickDisk can contain at most {QuickDiskLimits.StandardDirectoryEntries} directory entries.",
                        "QuickDisk file limit",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                if (bindAsCurrent)
                {
                    document.Clear();
                    document.FilePath = System.IO.Path.GetFullPath(filePath);
                    document.Format = format;
                    document.ContainerTrailingData = containerTrailing;
                    document.SidecarPath = loadedSidecar;
                    document.QuickDiskProfile = loadedQdProfile;
                    document.IplDskInfo = loadedIplDskInfo;
                    document.IsModified = false;
                    actFileName = System.IO.Path.GetFileName(filePath);
                    Title = $"MZTools - {actFileName}";
                }
                mzfBlocks.AddRange(recordsToAdd);
                if (!bindAsCurrent && recordsToAdd.Count > 0)
                {
                    document.IsModified = true;
                }
                RefreshGrid();
                if (fileExtension == ".dsk" && showIplImportDialog)
                {
                    MessageBox.Show(
                        this,
                        "The IPL payload was imported as a synthetic OBJ MZF record. The disk contains no original MZF header or tape metadata, and compressed payloads are not automatically decompressed.",
                        "MZ-800 IPL import",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else if (fileExtension == ".dsk")
                {
                    infoText.Content = $"{infoText.Content} Imported IPL payload as a synthetic OBJ MZF; original tape metadata is not recoverable and compressed data remains packed.";
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error reading file", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void RefreshGrid()
        {
            MzfDisplayDataCollection.Clear();
            LoadDataToGrid(mzfBlocks);
            UpdateStatus();
        }

        private void button_Click_Add(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = GetOpenFilter(),
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string[] filePaths = openFileDialog.FileNames;
                string[] audioPaths = filePaths
                    .Where(path => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".flac")
                    .ToArray();
                WavImportMode? audioImportMode = null;
                if (audioPaths.Length > 0)
                {
                    string sourceFormat = audioPaths.Length == 1
                        ? System.IO.Path.GetExtension(audioPaths[0]).TrimStart('.').ToUpperInvariant()
                        : $"Audio ({audioPaths.Length} files)";
                    var options = new WavImportOptionsWindow(sourceFormat) { Owner = this };
                    if (options.ShowDialog() != true)
                    {
                        return;
                    }
                    audioImportMode = options.ImportMode;
                }

                bool bindAsCurrent = document.Format == TapeDocumentFormat.None && mzfBlocks.Count == 0;
                bool addedAny = false;
                foreach (string filePath in filePaths)
                {
                    string extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                    WavImportMode? mode = extension is ".wav" or ".flac"
                        ? audioImportMode
                        : null;
                    if (!AddFile(
                        filePath,
                        bindAsCurrent,
                        selectedAudioImportMode: mode))
                    {
                        return;
                    }
                    bindAsCurrent = false;
                    addedAny = true;
                }

                if (addedAny)
                {
                    saveButton.IsEnabled = true;
                }
            }
        }

        private void button_Click_Export(object sender, RoutedEventArgs e)
        {
            List<(int Index, TapeRecord Record)> selectedRecords = GetSelectedRecordsInGridOrder();
            if (selectedRecords.Count == 0)
            {
                return;
            }

            string suggestedFileName = selectedRecords.Count == 1
                ? ConvertMzfNameToASCIIString(selectedRecords[0].Record.Header.MzfFname)
                : (!string.IsNullOrWhiteSpace(actFileName)
                    ? System.IO.Path.GetFileNameWithoutExtension(actFileName)
                    : "selected");

            ExportRecords(selectedRecords, suggestedFileName);
        }

        private void check_QDF_Files()
        {
            QDFFileReader qdfr = new QDFFileReader();

            string directoryPath = @"H:\Sharp\QDC\";
            string searchPattern = "*.qdf"; // Hledá všechny soubory s příponou .qdf
            string outFilePath = "output.txt";
            if (File.Exists(outFilePath))
            {
                File.Delete(outFilePath);
            }

            try
            {
                // Search for all .qdf files in subfolders
                string[] files = Directory.GetFiles(directoryPath, searchPattern, SearchOption.AllDirectories);

                foreach (string filePath in files)
                {
                    mzfBlocks.Clear();
                    qdfr.positions.Clear();
                    int cnt = 1;
                    long sumDta = 0;
                    long lastPos = 0;
                    bool firstPass = true;
                    int blocks = -1;

                    mzfBlocks.AddRange(qdfr.ReadFile(filePath)
                        .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2)));

                    using (StreamWriter writer = new StreamWriter(outFilePath, append: true))
                    {
                        writer.WriteLine(filePath);

                        foreach (long position in qdfr.positions)
                        {
                            if (cnt++ % 2 == 1)
                            {
                                writer.Write($"{position:X5} ");
                            }
                            else
                            {
                                writer.Write($"({position}) ");
                            }

                            if (firstPass)
                            {
                                firstPass = false;
                                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                                {
                                    fileStream.Seek(position, SeekOrigin.Begin);
                                    blocks = fileStream.ReadByte();
                                }
                                writer.Write($"[{blocks}] ");
                                if (blocks != mzfBlocks.Count * 2)
                                {
                                    writer.Write($"BAD BLOCK COUNT ");
                                }
                            }

                            if (cnt % 2 == 0)
                            {
                                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                                {
                                    fileStream.Seek(position - 1, SeekOrigin.Begin);
                                    byte blockStart = (byte)fileStream.ReadByte();
                                    if (blockStart != 0xA5) // this should never happen
                                        writer.Write($"BLOCKSTART=0x{blockStart:X2} ");
                                }
                            }
                        }
                        writer.WriteLine();

                        foreach (long position in qdfr.positions)
                        {
                            if (cnt++ % 2 == 1)
                            {
                                writer.Write(position - sumDta - lastPos);
                                lastPos = position - sumDta;
                                writer.Write(" ");
                            }
                            else
                            {
                                sumDta += position;
                            }
                        }
                        writer.WriteLine();
                    }
                }
                MessageBox.Show("Done");
            }
            catch (Exception ex)
            {
                // Zachytávání a zpracování výjimek
                Console.WriteLine("Došlo k chybě: " + ex.Message);
            }
        }

        private void button_Click_Delete(object sender, RoutedEventArgs e)
        {
            int[] selectedIndices = GetSelectedGridIndices();
            if (selectedIndices.Length == 0)
            {
                return;
            }
            int nextIndex = Math.Min(selectedIndices[0], mzfBlocks.Count - selectedIndices.Length - 1);
            for (int index = selectedIndices.Length - 1; index >= 0; index--)
            {
                mzfBlocks.RemoveAt(selectedIndices[index]);
            }
            document.IsModified = true;
            RefreshGrid();

            if (nextIndex >= 0 && nextIndex < MzfDisplayDataCollection.Count)
            {
                MzfDataGrid.SelectedItem = MzfDisplayDataCollection[nextIndex];
                MzfDataGrid.ScrollIntoView(MzfDisplayDataCollection[nextIndex]);
                MzfDataGrid.Focus();
            }
        }

        private void button_Click_NewQuickDisk(object sender, RoutedEventArgs e)
        {
            if ((document.Format != TapeDocumentFormat.None || document.IsModified || mzfBlocks.Count > 0) &&
                MessageBox.Show(
                    "Creating a new image will replace the current document. Continue?",
                    "New",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!TryChooseNewQuickDiskFormat(out TapeDocumentFormat format))
            {
                return;
            }

            document.Clear();
            document.Format = format;
            document.QuickDiskProfile = format switch
            {
                TapeDocumentFormat.QdHxc => QuickDiskPhysicalProfile.Hxc,
                TapeDocumentFormat.QdFlashFloppy => QuickDiskPhysicalProfile.FlashFloppy,
                _ => null
            };
            document.IsModified = true;
            actFileName = format == TapeDocumentFormat.Mzq ? "New.mzq" : "New.qd";
            Title = "MZTools - New";
            saveButton.IsEnabled = true;
            RefreshGrid();
        }

        private bool TryChooseNewQuickDiskFormat(out TapeDocumentFormat format)
        {
            format = TapeDocumentFormat.None;
            var selector = new ComboBox
            {
                ItemsSource = new[] { "Sharp legacy (.qd)", "HxC physical (.qd)", "FlashFloppy physical (.qd)", "MZQ (.mzq)" },
                SelectedIndex = 0,
                Margin = new Thickness(0, 8, 0, 12)
            };
            var dialog = new Window
            {
                Owner = this,
                Title = "New",
                Icon = this.Icon,
                Width = 320,
                Height = 165,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            ok.Click += (_, _) => dialog.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var content = new StackPanel { Margin = new Thickness(16) };
            content.Children.Add(new TextBlock { Text = "File type:" });
            content.Children.Add(selector);
            content.Children.Add(buttons);
            dialog.Content = content;

            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            format = selector.SelectedIndex switch
            {
                0 => TapeDocumentFormat.QdSharpLegacy,
                1 => TapeDocumentFormat.QdHxc,
                2 => TapeDocumentFormat.QdFlashFloppy,
                3 => TapeDocumentFormat.Mzq,
                _ => TapeDocumentFormat.None
            };
            return format != TapeDocumentFormat.None;
        }

        private void button_Click_FormatQuickDisk(object sender, RoutedEventArgs e)
        {
            if (!document.IsQdImage)
            {
                return;
            }
            if (MessageBox.Show(
                    "Formatting the Quickdisk image will remove all files from this image. Continue?",
                    "Format Quickdisk image",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            mzfBlocks.Clear();
            document.ContainerTrailingData = Array.Empty<byte>();
            document.IsModified = true;
            saveButton.IsEnabled = true;
            RefreshGrid();
        }

        private void button_Click_About(object sender, RoutedEventArgs e)
        {
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0]
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";

            var dialog = new Window
            {
                Owner = this,
                Title = "About",
                Icon = this.Icon,
                Width = 300,
                Height = 175,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var link = new Hyperlink(new Run("www.8bity.cz"))
            {
                NavigateUri = new Uri("https://www.8bity.cz")
            };
            link.RequestNavigate += (_, eventArgs) =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = eventArgs.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                eventArgs.Handled = true;
            };
            var ok = new Button
            {
                Content = "OK",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
                Width = 70,
                IsDefault = true
            };
            ok.Click += (_, _) => dialog.Close();

            var content = new StackPanel { Margin = new Thickness(10) };
            content.Children.Add(new TextBlock
            {
                Text = "MZTools",
                FontWeight = FontWeights.Bold,
                FontSize = 14
            });
            content.Children.Add(new TextBlock { Text = $"Version {version}" });
            content.Children.Add(new TextBlock { Text = "© 2026 Martin Lukasek" });
            content.Children.Add(new TextBlock { Text = "Simple app to convert SHARP MZ files." });
            var linkText = new TextBlock();
            linkText.Inlines.Add(link);
            content.Children.Add(linkText);
            content.Children.Add(ok);
            dialog.Content = content;
            dialog.ShowDialog();
        }
    }
}
