using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static MZTools.SharpMzEncoding;

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

    public IReadOnlyList<string> LoaderTypes => MZTools.TapeProfileComponents.LoaderTypes;

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
            speed = MZTools.TapeProfileComponents.NormalizeSpeed(loaderType, speed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailableSpeeds));
            OnPropertyChanged(nameof(Speed));
        }
    }

    public IReadOnlyList<string> AvailableSpeeds =>
        MZTools.TapeProfileComponents.GetAvailableSpeeds(loaderType);

    public string Speed
    {
        get => speed;
        set
        {
            string normalized = MZTools.TapeProfileComponents.NormalizeSpeed(loaderType, value);
            if (speed == normalized)
            {
                return;
            }
            speed = normalized;
            OnPropertyChanged();
        }
    }

    internal void SetProfile(MZTools.TapeProfile profile)
    {
        (loaderType, speed) = MZTools.TapeProfileComponents.Split(profile);
        OnPropertyChanged(nameof(LoaderType));
        OnPropertyChanged(nameof(AvailableSpeeds));
        OnPropertyChanged(nameof(Speed));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

namespace MZTools
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
            "All supported files|*.mzt;*.mzf;*.m12;*.mz0;*.mz7;*.mzq;*.qdf;*.qd;*.dsk;*.hfe;*.lep;*.l16;*.wav;*.flac|Quickdisk image (*.qd)|*.qd|MZ-800 IPL floppy (*.dsk)|*.dsk|HFE physical floppy (*.hfe)|*.hfe|Quickdisk file (*.mzq)|*.mzq|Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf;*.m12;*.mz0;*.mz7)|*.mzf;*.m12;*.mz0;*.mz7|Quickdisk file (*.qdf)|*.qdf|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav|FLAC audio (*.flac)|*.flac|All files (*.*)|*.*";

        public static string GetSaveFilter() =>
            "Quickdisk file (*.qdf)|*.qdf|Quickdisk file (*.mzq)|*.mzq|Quickdisk image - HxC (*.qd)|*.qd|Quickdisk image - FlashFloppy (*.qd)|*.qd|Quickdisk image - Sharp/MZ legacy (*.qd)|*.qd|Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf)|*.mzf|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav|MZ-800 bootable IPL floppy (single / multi automatic) (*.dsk)|*.dsk|Single tape file (*.m12)|*.m12|All files (*.*)|*.*";

        public static string GetExportFilter(bool singleRecord = false) =>
            "Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf)|*.mzf" +
                (singleRecord
                    ? "|MZ-800 bootable IPL floppy (*.dsk)|*.dsk"
                    : "|MZ-800 multi-game IPL floppy (*.dsk)|*.dsk") +
                "|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav|Single tape file (*.m12)|*.m12";

        public static string GetIplExportFilter() =>
            "Raw prepared program (*.bin)|*.bin|Reconstructed MZF from prepared IPL record (*.mzf)|*.mzf|" +
            "LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav|Reconstructed M12 from prepared IPL record (*.m12)|*.m12";
    }

    internal static class AudioImportPolicy
    {
        public static bool ShouldTryStandardDecoder(WavHeuristicAnalysisResult analysis) =>
            ShouldTryStandardDecoder(analysis.Failures.Count);

        internal static bool ShouldTryStandardDecoder(int failureCount) => failureCount > 0;
    }

    internal sealed class AudioFileImportResult
    {
        internal required string SourceFile { get; init; }
        internal required IReadOnlyList<TapeRecord> Records { get; init; }
        internal WavHeuristicAnalysisResult? Analysis { get; init; }
        internal bool StandardFallbackUsed { get; init; }
        internal bool Cancelled { get; init; }
        internal Exception? Error { get; init; }
        internal AudioReportMode ReportMode { get; init; } = AudioReportMode.Summary;
        internal bool Imported => !Cancelled && Error == null && Records.Count > 0;
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Guid dragOwner = Guid.NewGuid();
        private List<TapeRecord>? localDraggedRecords;
        private const string ApplicationName = "MZTools";

        private static readonly string ApplicationCaption = CreateApplicationCaption();

        private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private readonly TapeDocument document = new();
        private QuickDiskLayout? quickDiskLayout;
        private bool quickDiskMapDirty = true;
        private bool synchronizingQuickDiskSelection;
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
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            MapInteraction.EmphasizeSelection(quickDiskBlocks);
            MapInteraction.EmphasizeSelection(MzfDataGrid);
            MzfDisplayDataCollection = new ObservableCollection<MzfDisplayData>();
            MzfDataGrid.ItemsSource = MzfDisplayDataCollection;
            quickDiskMap.RegionSelected += region =>
            {
                if (ReferenceEquals(quickDiskBlocks.SelectedItem, region)) SynchronizeQuickDiskBlockSelection();
                else quickDiskBlocks.SelectedItem = region;
                if (region != null) quickDiskBlocks.ScrollIntoView(region);
            };
            Title = BuildWindowTitle();
            viewButton.IsEnabled = false; // Zakážem některá tlačítka při spuštění
            moveUpButton.IsEnabled = false;
            moveDownButton.IsEnabled = false;
            exportButton.IsEnabled = false;
            deleteButton.IsEnabled = false;
            saveButton.IsEnabled = false;
            saveAsButton.IsEnabled = false;
            closeButton.IsEnabled = false;
            dskEditorControl.DocumentStateChanged += (_, _) => Title = BuildWindowTitle(dskEditorControl.DocumentTitle);
            dskEditorControl.CloseRequested += (_, _) => TryLeaveDskMode();
            dskEditorControl.OpenRequested += (_, _) => button_Click_Open(openButton, new RoutedEventArgs());
            UpdateQuickDiskFeatureVisibility();
            UpdateStatus();
        }

        private static string CreateApplicationCaption()
        {
            string? version = typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(version))
            {
                version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
            }

            int metadataSeparator = version?.IndexOf('+') ?? -1;
            if (metadataSeparator >= 0)
            {
                version = version![..metadataSeparator];
            }

            return string.IsNullOrWhiteSpace(version)
                ? ApplicationName
                : $"{ApplicationName}  {version}";
        }

        private static string BuildWindowTitle(string? documentTitle = null)
        {
            if (string.IsNullOrWhiteSpace(documentTitle) || documentTitle == ApplicationName)
            {
                return ApplicationCaption;
            }

            return documentTitle.StartsWith(ApplicationName, StringComparison.Ordinal)
                ? ApplicationCaption + documentTitle[ApplicationName.Length..]
                : $"{ApplicationCaption} - {documentTitle}";
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
            if (document.IsReadOnlyQuickDisk)
            {
                error = "Non-SHARP/unknown QuickDisk is read-only. Use the inspector's decoded-data export.";
                return false;
            }
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
            TapeDocumentFormat.M12 => 12,
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
                ".m12" => TapeDocumentFormat.M12,
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
            else if (WorkspaceDragTransfer.IsPresent(e.Data))
                e.Effects = WorkspaceDragTransfer.IsLocal(e.Data, dragOwner) ? DragDropEffects.Move : DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None; // Jinak neumožněte drop
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Handled) return;
            if (WorkspaceDragTransfer.IsPresent(e.Data))
            {
                e.Handled = true;
                if (!WorkspaceDragTransfer.IsLocal(e.Data, dragOwner)) PreviewRecordCopy(e, mzfBlocks.Count);
                return;
            }
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files != null && files.Length > 0)
                {
                    string[] audioPaths = files
                        .Where(path => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".flac")
                        .ToArray();
                    WavImportMode? audioImportMode = null;
                    AudioReportMode audioReportMode = AudioReportMode.Summary;
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
                        audioReportMode = options.ReportMode;
                    }

                    bool bindAsCurrent = document.Format == TapeDocumentFormat.None && mzfBlocks.Count == 0;
                    var audioResults = new List<AudioFileImportResult>();
                    int audioIndex = 0;
                    foreach (var file in files)
                    {
                        bool isAudio = System.IO.Path.GetExtension(file).ToLowerInvariant() is ".wav" or ".flac";
                        if (isAudio)
                        {
                            audioIndex++;
                        }
                        if (AddFile(
                            file,
                            bindAsCurrent,
                            selectedAudioImportMode: isAudio ? audioImportMode : null,
                            selectedAudioReportMode: isAudio ? audioReportMode : null,
                            audioResultSink: audioResults.Add,
                            deferAudioErrors: audioPaths.Length > 1,
                            audioFileIndex: audioIndex,
                            audioFileCount: audioPaths.Length))
                        {
                            bindAsCurrent = false;
                        }
                    }
                    ShowAudioImportReports(audioResults);
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
            bool documentOpen = document.Format != TapeDocumentFormat.None;
            tapeViews.Visibility = documentOpen ? Visibility.Visible : Visibility.Collapsed;
            MzfDataGrid.Visibility = documentOpen && !document.IsReadOnlyQuickDisk ? Visibility.Visible : Visibility.Collapsed;
            quickDiskInspectorHost.Visibility = document.IsReadOnlyQuickDisk ? Visibility.Visible : Visibility.Collapsed;
            statusBorder.Visibility = documentOpen ? Visibility.Visible : Visibility.Collapsed;
            addButton.IsEnabled = documentOpen && !document.IsReadOnlyQuickDisk;
            saveButton.IsEnabled = CanSaveTapeDocumentInPlace();
            saveAsButton.IsEnabled = documentOpen && !document.IsReadOnlyQuickDisk;
            closeButton.IsEnabled = documentOpen;
            MzfDataGrid.IsReadOnly = document.IsReadOnlyQuickDisk;
            if (document.IsReadOnlyQuickDisk)
            {
                viewButton.IsEnabled = exportButton.IsEnabled = deleteButton.IsEnabled = false;
                moveUpButton.IsEnabled = moveDownButton.IsEnabled = false;
            }
            if (!documentOpen)
            {
                viewButton.IsEnabled = false;
                exportButton.IsEnabled = false;
                deleteButton.IsEnabled = false;
                moveUpButton.IsEnabled = false;
                moveDownButton.IsEnabled = false;
            }

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
            if (document.QuickDiskReadResult is { } qdResult && !document.IsModified)
            {
                var identity = qdResult.Analysis.Identification;
                qdInfoText.Text = $"Container: {QuickDiskFormatLabels.Display(qdResult.Format)} | Host: {identity.DisplayName} | Confidence: {identity.Confidence} | Probable origin: {identity.Origin.ProbableDevice ?? "Unknown"} | Native SHARP MZ: {(identity.IsNativeSharpMz ? "Yes" : "No / not detected")}";
                if (document.IsReadOnlyQuickDisk)
                    infoText.Content = "Read-only physical QuickDisk inspection. This is not an identified native SHARP MZ-800 format; MZF editing and SHARP rebuilding are disabled.";
            }
            UpdateQuickDiskFeatureVisibility();
            quickDiskMapDirty = true;
            quickDiskMapTab.Visibility = document.IsQuickDisk ? Visibility.Visible : Visibility.Collapsed;
            if (!document.IsQuickDisk)
            {
                tapeViews.SelectedIndex = 0;
                quickDiskLayout = null;
                quickDiskMap.SetLayout(null);
                quickDiskBlocks.ItemsSource = null;
                quickDiskMapSummary.Text = string.Empty;
            }
            else if (quickDiskMapTab.IsSelected) RefreshQuickDiskMap();
            UpdateQuickDiskSelectionButtons();
        }

        private void ToolsMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null) { button.ContextMenu.PlacementTarget = button; button.ContextMenu.IsOpen = true; }
        }

        private void BatchProcess_Click(object sender, RoutedEventArgs e) => new BatchProcessDialog(this).ShowDialog();

        private void AnalyzeAudio_Click(object sender, RoutedEventArgs e) => new AudioSignalAnalyzerWindow(this).Show();

        private void RepairDskFile_Click(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFileDialog { Filter = "Extended CPC DSK|*.dsk", Title = "Inspect / Repair DSK Container" };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                var repair = new DskRepairDialog(this, File.ReadAllBytes(picker.FileName)); repair.ShowDialog();
                if (repair.Result == null) return;
                var candidate = DskDocument.Open(repair.Result, picker.FileName); candidate.ReplaceContents(repair.Result);
                ShowDskDocument(candidate);
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "DSK Container Repair", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void TapeViews_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(e.Source, tapeViews) && quickDiskMapTab?.IsSelected == true)
                RefreshQuickDiskMap();
        }

        private void RefreshQuickDiskMap()
        {
            if (!document.IsQuickDisk || !quickDiskMapDirty || quickDiskMap == null) return;
            quickDiskMapDirty = false;
            quickDiskMapDetail.Text = "* Length is in the units shown above. Gaps are not necessarily free space.";
            try
            {
                quickDiskLayout = QuickDiskLayoutBuilder.Build(document);
                quickDiskMap.SetLayout(quickDiskLayout);
                quickDiskBlocks.ItemsSource = quickDiskLayout.Regions;
                quickDiskMapSummary.Text = quickDiskLayout.Summary;
                quickDiskMap.HighlightFiles(GetSelectedGridIndices());
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
            {
                quickDiskLayout = null;
                quickDiskMap.SetLayout(null);
                quickDiskBlocks.ItemsSource = null;
                quickDiskMapSummary.Text = $"Disk Map unavailable: {ex.Message}";
                quickDiskMapDetail.Text = "The document has not been changed. Resolve capacity or format errors to view a rebuilt layout.";
            }
            UpdateQuickDiskSelectionButtons();
        }

        private void QuickDiskBlocks_KeyDown(object sender, KeyEventArgs e)
        {
            if (MapInteraction.MoveGridSelection(quickDiskBlocks, e.Key)) e.Handled = true;
        }

        private void QuickDiskBlocks_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            SynchronizeQuickDiskBlockSelection();

        private void SynchronizeQuickDiskBlockSelection()
        {
            if (synchronizingQuickDiskSelection) return;
            synchronizingQuickDiskSelection = true;
            try
            {
                var region = quickDiskBlocks.SelectedItem as QuickDiskRegion;
                quickDiskMap.Select(region);
                quickDiskMapDetail.Text = region != null && quickDiskLayout != null ? quickDiskLayout.Detail(region) :
                    "* Length is in the units shown above. Gaps are not necessarily free space.";
                MzfDataGrid.SelectedItems.Clear();
                if (region != null && region.FileIndex >= 0 && region.FileIndex < MzfDisplayDataCollection.Count)
                {
                    MzfDataGrid.SelectedItem = MzfDisplayDataCollection[region.FileIndex];
                    MzfDataGrid.ScrollIntoView(MzfDataGrid.SelectedItem);
                }
                quickDiskMap.HighlightFiles(GetSelectedGridIndices());
            }
            finally { synchronizingQuickDiskSelection = false; UpdateQuickDiskSelectionButtons(); }
        }

        private void UpdateQuickDiskSelectionButtons()
        {
            if (quickDiskClearSelectionButton == null || quickDiskHexButton == null) return;
            bool blockSelected = quickDiskBlocks?.SelectedItem is QuickDiskRegion;
            quickDiskHexButton.IsEnabled = document.IsQuickDisk && quickDiskLayout != null && blockSelected;
            quickDiskClearSelectionButton.IsEnabled = document.IsQuickDisk &&
                (blockSelected || MzfDataGrid.SelectedItems.Count > 0);
        }

        private void ClearQuickDiskSelection()
        {
            quickDiskBlocks.SelectedItem = null;
            SynchronizeQuickDiskBlockSelection();
        }
        private void ClearQuickDiskSelection_Click(object sender, RoutedEventArgs e) => ClearQuickDiskSelection();

        private string QuickDiskReport() => document.IsQdImage
            ? QuickDiskAnalysisReport.Build(QdImageReaderWriter.Read(document.IsModified || document.QuickDiskSourceImage == null
                ? QuickDiskLayoutBuilder.BuildPreviewImage(document) : document.QuickDiskSourceImage))
            : quickDiskLayout?.Summary ?? "No QuickDisk image.";
        private void QuickDiskCopyReport_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(QuickDiskReport()); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "QuickDisk report", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void QuickDiskSaveReport_Click(object sender, RoutedEventArgs e)
        {
            var picker = new SaveFileDialog { Filter = "Text report|*.txt", FileName = "quickdisk-analysis.txt" };
            if (picker.ShowDialog(this) != true) return;
            try { File.WriteAllText(picker.FileName, QuickDiskReport()); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "QuickDisk report", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void MapBackground_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (quickDiskMapTab?.IsSelected != true && DiskMapVisuals.IsBlankClick(e.OriginalSource as DependencyObject))
                MzfDataGrid.UnselectAll();
            if (quickDiskMapTab?.IsSelected == true && DiskMapVisuals.IsBlankClick(e.OriginalSource as DependencyObject))
                ClearQuickDiskSelection();
        }
        internal (string Title, byte[] Bytes)? GetSelectedQuickDiskHexBlock()
        {
            if (quickDiskLayout == null || quickDiskBlocks.SelectedItem is not QuickDiskRegion region) return null;
            string encoding = !quickDiskLayout.IsPhysical ? "image bytes" :
                region.Kind is QuickDiskRegionKind.Gap or QuickDiskRegionKind.OutsideWindow ? "raw track bitcells packed LSB-first" : "decoded MFM bytes";
            return ($"{quickDiskLayout.Detail(region)}\n{encoding}; {(quickDiskLayout.IsPreview ? "rebuilt preview" : "original image")}", quickDiskLayout.GetBlockBytes(region));
        }
        private void QuickDiskHex_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedQuickDiskHexBlock() is not { } block) return;
            var browser = new HexBrowser { Owner = this };
            browser.ShowRawData(block.Title, block.Bytes);
            browser.Show();
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
            qdCompressionButton.Visibility = tapeContextCompression.Visibility = document.Format != TapeDocumentFormat.None ? Visibility.Visible : Visibility.Collapsed;
            qdCompressionButton.IsEnabled = tapeContextCompression.IsEnabled = document.Format != TapeDocumentFormat.None && !document.IsReadOnlyQuickDisk && MzfDataGrid.SelectedItems.Count > 0;
            tapeLoaderColumn.Visibility = tapeSpeedColumn.Visibility = document.IsQuickDisk ? Visibility.Collapsed : Visibility.Visible;
            // DSK/HFE tools belong to the disk editor or the empty workspace.
            // QD containers (including MZQ/QDF) share the QuickDisk workspace.
            bool emptyWorkspace = document.Format == TapeDocumentFormat.None && dskEditorControl.Visibility != Visibility.Visible;
            batchProcessMenu.Visibility = repairDskFileMenu.Visibility = emptyWorkspace ? Visibility.Visible : Visibility.Collapsed;
            toolsButton.Visibility = !document.IsQuickDisk ? Visibility.Visible : Visibility.Collapsed;
            newQuickDiskButton.Visibility = newDskButton.Visibility = emptyWorkspace ? Visibility.Visible : Visibility.Collapsed;
            bool quickDiskAvailable = document.IsQdImage;
            bool diskInfoAvailable = quickDiskAvailable ||
                document.IplDskInfo != null;
            qdInfoText.Visibility = diskInfoAvailable ? Visibility.Visible : Visibility.Collapsed;
            formatQuickDiskButton.Visibility = quickDiskAvailable ? Visibility.Visible : Visibility.Collapsed;
            formatQuickDiskButton.IsEnabled = quickDiskAvailable && (!document.IsReadOnlyQuickDisk || document.QuickDiskReadResult?.Analysis.Identification.IsBlank == true);
        }

        private void NativeCom_Click(object sender, RoutedEventArgs e)
        {
            TapeRecord? selected = MzfDataGrid.SelectedIndex is int index && index >= 0 && index < mzfBlocks.Count ? mzfBlocks[index] : null;
            new NativeComDialog(this, selected).ShowDialog();
        }

        private void QuickDiskCompression_Click(object sender, RoutedEventArgs e)
        {
            if (document.Format == TapeDocumentFormat.None || document.IsReadOnlyQuickDisk) return;
            int[] indices = GetSelectedGridIndices();
            if (indices.Length == 0) return;
            var records = indices.Select(index => (Index: index, Record: mzfBlocks[index])).ToArray();
            var target = document.IsQuickDisk ? CompressionTarget.QuickDisk : document.Format == TapeDocumentFormat.Mzt ? CompressionTarget.MztTape : CompressionTarget.MzfTape;
            var dialog = new SaveOptionsDialog(document.Format, 0, false, target, records, applyToDocument: true) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var proposed = mzfBlocks.ToList();
                for (int i = 0; i < indices.Length; i++) proposed[indices[i]] = dialog.PackedRecords[i];
                bool allowImported = CanPreserveImportedNonStandard(document.Format);
                if (document.Format == TapeDocumentFormat.Qdf) QDFFileReader.BuildImage(proposed, allowImported);
                else if (document.IsQdImage)
                    QdImageReaderWriter.Write(proposed, document.Format switch {
                        TapeDocumentFormat.QdHxc => QdImageFormat.HxcPhysical,
                        TapeDocumentFormat.QdFlashFloppy => QdImageFormat.FlashFloppyPhysical,
                        _ => QdImageFormat.SharpLegacyLogical }, document.QuickDiskProfile, allowImported);
                for (int i = 0; i < indices.Length; i++) mzfBlocks[indices[i]] = dialog.PackedRecords[i];
                document.IsModified = true;
                RefreshGrid();
                foreach (int index in indices) MzfDataGrid.SelectedItems.Add(MzfDisplayDataCollection[index]);
                Title = BuildWindowTitle();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Program compression", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            out bool separateFiles,
            out int wavSampleRate)
        {
            machine = SharpTapeMachine.Mz800;
            separateFiles = false;
            wavSampleRate = SharpTapeExporter.WavSampleRate;
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
            wavSampleRate = dialog.WavSampleRate;
            return true;
        }

        private bool ExportWaveform(
            string selectedPath,
            IReadOnlyList<TapeRecord> records,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine,
            bool separateFiles,
            int wavSampleRate)
        {
            try
            {
                if (!separateFiles || records.Count == 1)
                {
                    SharpTapeExporter.Export(selectedPath, records, format, machine, wavSampleRate);
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
                    overwrite,
                    wavSampleRate);
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

        internal void ExportIplRecords(IReadOnlyList<TapeRecord> records, string suggestedFileName)
        {
            ArgumentNullException.ThrowIfNull(records);
            ExportRecords(
                records.Select((record, index) => (index, record)).ToList(),
                suggestedFileName,
                iplOnly: true);
        }

        private void ExportRecords(
            IReadOnlyList<(int Index, TapeRecord Record)> selectedRecords,
            string suggestedFileName,
            bool iplOnly = false)
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
            string[] formats = (iplOnly ? FeatureModePolicy.GetIplExportFilter() : GetExportFilter(records.Count)).Split('|');
            var formatDialog = new Window { Owner = this, Title = "Export format", Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var formatPanel = new StackPanel { Margin = new Thickness(16) }; formatDialog.Content = formatPanel;
            formatPanel.Children.Add(new TextBlock { Text = $"Export {records.Count} selected program(s) as:", Margin = new Thickness(0, 0, 0, 8) });
            var formatChoice = new ComboBox { ItemsSource = Enumerable.Range(0, formats.Length / 2).Select(index => formats[index * 2]).ToArray(), SelectedIndex = iplOnly || !multipleRecords ? 1 : 0 };
            formatPanel.Children.Add(formatChoice);
            var next = new Button { Content = "Next", IsDefault = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            formatPanel.Children.Add(next); next.Click += (_, _) => formatDialog.DialogResult = true;
            if (formatDialog.ShowDialog() != true) return;
            string selectedExtension = formats[formatChoice.SelectedIndex * 2 + 1].TrimStart('*');
            bool preserve = true, generateSidecar = false;
            List<TapeRecord> exportRecords = records;
            SharpTapeMachine machine = SharpTapeMachine.Mz800;
            bool separateFiles = false;
            int wavSampleRate = SharpTapeExporter.WavSampleRate;
            byte[]? iplImage = null;
            try
            {
                if (!iplOnly && selectedExtension is ".mzf" or ".m12" or ".mzt")
                {
                    var format = selectedExtension == ".mzt" ? TapeDocumentFormat.Mzt : selectedExtension == ".m12" ? TapeDocumentFormat.M12 : TapeDocumentFormat.Mzf;
                    if (!TryChooseTapeExportOptions(suggestedFileName + selectedExtension, format, records.Sum(record => record.Body.TrailingData?.Length ?? 0), selectedRecords, out preserve, out generateSidecar, out exportRecords)) return;
                }
                else if (selectedExtension is ".lep" or ".l16" or ".wav")
                {
                    if (!TryChooseWaveformSaveOptions(selectedExtension, records.Count, out machine, out separateFiles, out wavSampleRate)) return;
                }
                else if (selectedExtension == ".dsk")
                {
                    iplImage = PrepareIplImage(records);
                    if (iplImage == null) return;
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.GetBaseException().Message, "Export preparation", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (multipleRecords && (selectedExtension is ".mzf" or ".m12" or ".bin" || separateFiles))
            {
                var destination = new SeparateExportDialog(this, exportRecords, selectedExtension);
                if (destination.ShowDialog() != true) return;
                try
                {
                    string[] paths = destination.OutputPaths.ToArray();
                    string[] allPaths = selectedExtension is ".mzf" or ".m12"
                        ? paths.SelectMany(path => new[] { path, SidecarService.GetSidecarPath(path) }).ToArray() : paths;
                    if (allPaths.Any(Directory.Exists)) throw new IOException("An output filename or sidecar is already used by a directory.");
                    int existing = allPaths.Count(File.Exists);
                    if (existing > 0 && MessageBox.Show(this, $"{existing} output file(s) / sidecar(s) already exist. Overwrite them?", "Overwrite separate files", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    for (int index = 0; index < paths.Length; index++)
                    {
                        if (selectedExtension == ".bin") File.WriteAllBytes(paths[index], exportRecords[index].Body.MzfBody);
                        else if (selectedExtension is ".mzf" or ".m12") TapeDocumentWriter.SaveMzf(paths[index], exportRecords[index], preserve, generateSidecar);
                        else SharpTapeExporter.Export(paths[index], [exportRecords[index]], SharpTapeExporter.GetFormat(selectedExtension), machine, wavSampleRate);
                    }
                    MessageBox.Show(this, $"Created {paths.Length} files in:\n{System.IO.Path.GetDirectoryName(paths[0])}", "Separate export complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.GetBaseException().Message, "Error exporting files", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }
            var saveFileDialog = new SaveFileDialog
            {
                Filter = formats[formatChoice.SelectedIndex * 2] + "|" + formats[formatChoice.SelectedIndex * 2 + 1],
                Title = multipleRecords ? $"Export {records.Count} selected programs" : $"Export {suggestedFileName}",
                AddExtension = true,
                DefaultExt = selectedExtension,
                FilterIndex = 1,
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
                if (fileExtension != selectedExtension)
                    throw new InvalidOperationException($"The filename must use {selectedExtension}, matching the selected export format.");
                if (iplOnly && fileExtension == ".bin")
                {
                    if (records.Count == 1)
                    {
                        File.WriteAllBytes(filePath, records[0].Body.MzfBody);
                        return;
                    }

                    IReadOnlyList<string> outputPaths =
                        SharpTapeExporter.GetSeparateOutputPaths(filePath, records);
                    string[] existingPaths = outputPaths.Where(File.Exists).ToArray();
                    if (existingPaths.Length > 0)
                    {
                        MessageBoxResult overwrite = MessageBox.Show(
                            this,
                            $"{existingPaths.Length} separate BIN file(s) already exist. Overwrite them?",
                            "Overwrite separate files",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (overwrite != MessageBoxResult.Yes) return;
                    }
                    for (int index = 0; index < records.Count; index++)
                    {
                        File.WriteAllBytes(outputPaths[index], records[index].Body.MzfBody);
                    }
                    MessageBox.Show(
                        this,
                        $"Created {outputPaths.Count} separate BIN files in:\n{System.IO.Path.GetDirectoryName(outputPaths[0])}",
                        "Separate export complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (iplOnly && (fileExtension is ".mzf" or ".m12"))
                {
                    if (records.Count == 1)
                    {
                        TapeDocumentWriter.SaveMzf(
                            filePath,
                            records[0],
                            preserveTrailing: true,
                            createSidecar: false);
                        return;
                    }

                    IReadOnlyList<string> outputPaths =
                        SharpTapeExporter.GetSeparateOutputPaths(filePath, records);
                    string[] existingPaths = outputPaths.Where(File.Exists).ToArray();
                    if (existingPaths.Length > 0)
                    {
                        MessageBoxResult overwrite = MessageBox.Show(
                            this,
                            $"{existingPaths.Length} separate {fileExtension.TrimStart('.').ToUpperInvariant()} file(s) already exist. Overwrite them?",
                            "Overwrite separate files",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (overwrite != MessageBoxResult.Yes) return;
                    }
                    for (int index = 0; index < records.Count; index++)
                    {
                        TapeDocumentWriter.SaveMzf(
                            outputPaths[index],
                            records[index],
                            preserveTrailing: true,
                            createSidecar: false);
                    }
                    MessageBox.Show(
                        this,
                        $"Created {outputPaths.Count} separate {fileExtension.TrimStart('.').ToUpperInvariant()} files in:\n{System.IO.Path.GetDirectoryName(outputPaths[0])}",
                        "Separate export complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (fileExtension == ".dsk")
                {
                    File.WriteAllBytes(filePath, iplImage!);
                    return;
                }

                if (fileExtension == ".mzt")
                {
                    TapeDocumentWriter.SaveMzt(
                        filePath,
                        exportRecords,
                        generateSidecar);
                    return;
                }

                if (fileExtension is ".mzf" or ".m12")
                {
                    if (records.Count == 1)
                    {
                        TapeRecord exportRecord = exportRecords[0];
                        TapeDocumentWriter.SaveMzf(
                            filePath,
                            exportRecord,
                            preserve,
                            generateSidecar);
                        return;
                    }

                    List<TapeRecord> separateRecords = exportRecords;

                    IReadOnlyList<string> outputPaths =
                        SharpTapeExporter.GetSeparateOutputPaths(filePath, records);
                    string[] existingPaths = outputPaths.Where(File.Exists).ToArray();
                    if (existingPaths.Length > 0)
                    {
                        MessageBoxResult overwrite = MessageBox.Show(
                            this,
                            $"{existingPaths.Length} separate {fileExtension.TrimStart('.').ToUpperInvariant()} file(s) already exist. Overwrite them?",
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
                            preserve,
                            generateSidecar);
                    }

                    MessageBox.Show(
                        this,
                        $"Created {outputPaths.Count} separate {fileExtension.TrimStart('.').ToUpperInvariant()} files in:\n{System.IO.Path.GetDirectoryName(outputPaths[0])}",
                        "Separate export complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (fileExtension is ".lep" or ".l16" or ".wav")
                {
                    ExportWaveform(
                        filePath,
                        records,
                        SharpTapeExporter.GetFormat(fileExtension),
                        machine,
                        separateFiles,
                        wavSampleRate);
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
            if (!TryLeaveDskMode())
            {
                return;
            }
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = GetOpenFilter();

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;
                AudioFileImportResult? audioResult = null;
                if (!AddFile(
                    filePath,
                    bindAsCurrent: true,
                    showIplImportDialog: true,
                    audioResultSink: result => audioResult = result))
                {
                    return;
                }
                if (audioResult?.Imported == true)
                {
                    ShowAudioImportReport(audioResult);
                }

                UpdateStatus();
            }
        }

        private bool CanSaveTapeDocumentInPlace()
        {
            if (document.IsReadOnlyQuickDisk) return false;
            if (document.FilePath == null)
            {
                return false;
            }
            string extension = System.IO.Path.GetExtension(document.FilePath).ToLowerInvariant();
            return document.Format switch
            {
                TapeDocumentFormat.Qdf => extension == ".qdf",
                TapeDocumentFormat.Mzq => extension == ".mzq",
                TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy => extension == ".qd",
                TapeDocumentFormat.Mzt => extension == ".mzt",
                TapeDocumentFormat.Mzf => extension == ".mzf",
                TapeDocumentFormat.M12 => extension == ".m12",
                _ => false
            };
        }

        private void button_Click_Save(object sender, RoutedEventArgs e)
        {
            if (!CanSaveTapeDocumentInPlace() || document.FilePath == null)
            {
                return;
            }
            if (!TryValidateAllBlocks(out string validationError) ||
                !TryValidateOutputFormat(document.Format, System.IO.Path.GetExtension(document.FilePath).ToLowerInvariant(), out validationError))
            {
                MessageBox.Show(validationError, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                SaveNativeTapeDocument(document.FilePath, document.Format);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Error saving file", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveNativeTapeDocument(string filePath, TapeDocumentFormat outputFormat)
        {
            if (document.IsReadOnlyQuickDisk) throw new InvalidOperationException("Non-SHARP/unknown QuickDisk cannot be saved by a SHARP writer.");
            bool allowImportedNonStandard = CanPreserveImportedNonStandard(outputFormat);
            if (outputFormat == TapeDocumentFormat.Mzq)
            {
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    var writer = new MZQFileReader();
                    writer.WriteMZQHeaderToFile(fileStream, checked((byte)(mzfBlocks.Count * 2)), allowImportedNonStandard);
                    foreach (TapeRecord record in mzfBlocks)
                    {
                        writer.WriteMZQFileHeaderToFile(fileStream, record.Header);
                        writer.WriteMZQFileBodyToFile(fileStream, record.Body);
                    }
                }
                DiscardMetadataNotStoredByCurrentFormat();
                SetCurrentDocumentAfterSave(filePath, outputFormat, sidecarPath: null);
                return;
            }
            if (outputFormat == TapeDocumentFormat.Qdf)
            {
                File.WriteAllBytes(filePath, QDFFileReader.BuildImage(mzfBlocks, allowImportedNonStandard));
                DiscardMetadataNotStoredByCurrentFormat();
                SetCurrentDocumentAfterSave(filePath, outputFormat, sidecarPath: null);
                return;
            }
            if (outputFormat is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy or TapeDocumentFormat.QdSharpLegacy)
            {
                QdImageFormat qdFormat = outputFormat switch
                {
                    TapeDocumentFormat.QdHxc => QdImageFormat.HxcPhysical,
                    TapeDocumentFormat.QdFlashFloppy => QdImageFormat.FlashFloppyPhysical,
                    _ => QdImageFormat.SharpLegacyLogical
                };
                QuickDiskPhysicalProfile? profile = document.QuickDiskProfile;
                File.WriteAllBytes(filePath, QdImageReaderWriter.Write(mzfBlocks, qdFormat, profile, allowImportedNonStandard));
                DiscardMetadataNotStoredByCurrentFormat();
                SetCurrentDocumentAfterSave(
                    filePath,
                    outputFormat,
                    sidecarPath: null,
                    profile ?? (qdFormat is QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical
                        ? QuickDiskPhysicalProfile.For(qdFormat)
                        : null));
                return;
            }
            if (outputFormat is TapeDocumentFormat.Mzf or TapeDocumentFormat.M12)
            {
                if (mzfBlocks.Count != 1)
                {
                    throw new InvalidOperationException("An MZF/M12 document must contain exactly one file.");
                }
                int trailingBytes = mzfBlocks[0].Body.TrailingData?.Length ?? 0;
                if (!TryChooseTapeSaveOptions(filePath, outputFormat, trailingBytes, out bool preserve, out bool generateSidecar))
                {
                    return;
                }
                TapeDocumentWriter.SaveMzf(filePath, mzfBlocks[0], preserve, generateSidecar);
                SetCurrentDocumentAfterSave(filePath, outputFormat, GetExistingSidecarPath(filePath));
                return;
            }
            if (outputFormat == TapeDocumentFormat.Mzt)
            {
                bool generateSidecar = false;
                if (mzfBlocks.Count > 0 && !TryChooseTapeSaveOptions(filePath, outputFormat, 0, out _, out generateSidecar))
                {
                    return;
                }
                TapeDocumentWriter.SaveMzt(filePath, mzfBlocks, generateSidecar);
                document.ContainerTrailingData = Array.Empty<byte>();
                SetCurrentDocumentAfterSave(filePath, outputFormat, GetExistingSidecarPath(filePath));
                return;
            }
            throw new InvalidOperationException("This document cannot be saved back to its source format. Use Save As instead.");
        }

        private bool SaveIplRecords(string path, IReadOnlyList<TapeRecord> records)
        {
            byte[]? image = PrepareIplImage(records);
            if (image == null) return false;
            File.WriteAllBytes(path, image);
            return true;
        }

        private byte[]? PrepareIplImage(IReadOnlyList<TapeRecord> records)
        {
            if (records.Count == 0) throw new InvalidOperationException("IPL DSK requires at least one program.");
            if (records.Count == 1)
            {
                var dialog = new IplDskOptionsDialog(records[0]) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.PackedRecord == null) return null;
                return Mz800IplDskWriter.Build(dialog.PackedRecord, dialog.BootName);
            }
            else
            {
                var dialog = new MultiGameIplDskOptionsDialog(records) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.BuildResult == null) return null;
                return dialog.BuildResult.Image;
            }
        }

        private void button_Click_SaveAs(object sender, RoutedEventArgs e)
        {
            if (document.IsReadOnlyQuickDisk) return;
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
                bool iplDskOutput = fileExtension == ".dsk";
                if (outputFormat == TapeDocumentFormat.None && !waveformOutput && !iplDskOutput)
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

                if (!waveformOutput && !iplDskOutput &&
                    !TryValidateOutputFormat(outputFormat, fileExtension, out validationError))
                {
                    MessageBox.Show(validationError, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    bool allowImportedNonStandard = CanPreserveImportedNonStandard(outputFormat);
                    if (iplDskOutput)
                    {
                        if (!SaveIplRecords(filePath, mzfBlocks)) return;
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
                    else if (outputFormat is TapeDocumentFormat.Mzf or TapeDocumentFormat.M12)
                    {
                        if (mzfBlocks.Count > 1)
                        {
                            MessageBox.Show("MZF/M12 files contain one tape record. Please use Export or save as MZT for multiple records.", "Single-record file limitation", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                        int trailingBytes = mzfBlocks[0].Body.TrailingData?.Length ?? 0;
                        if (!TryChooseTapeSaveOptions(
                            filePath,
                            outputFormat,
                            trailingBytes,
                            out bool preserve,
                            out bool generateSidecar))
                        {
                            return;
                        }
                        TapeDocumentWriter.SaveMzf(filePath, mzfBlocks[0], preserve, generateSidecar);
                        SetCurrentDocumentAfterSave(filePath, outputFormat, GetExistingSidecarPath(filePath));
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
                            out bool separateFiles,
                            out int wavSampleRate))
                        {
                            return;
                        }
                        ExportWaveform(
                            filePath,
                            mzfBlocks,
                            SharpTapeExporter.GetFormat(fileExtension),
                            machine,
                            separateFiles,
                            wavSampleRate);
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
            document.QuickDiskSourceImage = document.IsQuickDisk ? File.ReadAllBytes(filePath) : null;
            document.IplDskInfo = null;
            document.IsModified = false;
            actFileName = System.IO.Path.GetFileName(filePath);
            Title = BuildWindowTitle(actFileName);
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
            qdCompressionButton.IsEnabled = tapeContextCompression.IsEnabled = document.Format != TapeDocumentFormat.None && !document.IsReadOnlyQuickDisk && MzfDataGrid.SelectedItems.Count > 0;
            // Tlačítko "View" bude aktivní pouze pokud je vybrán nějaký řádek
            viewButton.IsEnabled = MzfDataGrid.SelectedItem != null;
            exportButton.IsEnabled = MzfDataGrid.SelectedItem != null;
            deleteButton.IsEnabled = MzfDataGrid.SelectedItem != null;

            int[] selectedIndices = GetSelectedGridIndices();
            quickDiskMap?.HighlightFiles(selectedIndices);
            if (!synchronizingQuickDiskSelection && quickDiskBlocks != null)
            {
                synchronizingQuickDiskSelection = true;
                try
                {
                    quickDiskBlocks.SelectedItem = null;
                    quickDiskMap?.Select(null);
                    quickDiskHexButton.IsEnabled = false;
                    quickDiskMapDetail.Text = "* Length is in the units shown above. Gaps are not necessarily free space.";
                }
                finally { synchronizingQuickDiskSelection = false; }
            }
            moveUpButton.IsEnabled = selectedIndices.Length > 0 && selectedIndices[0] > 0;
            moveDownButton.IsEnabled = selectedIndices.Length > 0 && selectedIndices[^1] < mzfBlocks.Count - 1;
            UpdateQuickDiskSelectionButtons();
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
            else if (e.Key == Key.F2 && MzfDataGrid.SelectedItem is MzfDisplayData item)
            {
                MzfDataGrid.CurrentCell = new DataGridCellInfo(item, fileNameColumn);
                e.Handled = MzfDataGrid.BeginEdit();
            }
        }

        private void MzfDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var row = FindVisualParent<DataGridRow>(source);
            tapeContextExport.Tag = row?.Item as MzfDisplayData;
            if (row?.Item is MzfDisplayData && !row.IsSelected)
            { MzfDataGrid.SelectedItems.Clear(); row.IsSelected = true; }
        }

        private void MzfDataGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            tapeContextAdd.IsEnabled = addButton.IsEnabled;
            if (e?.CursorLeft < 0) tapeContextExport.Tag = MzfDataGrid.CurrentItem as MzfDisplayData;
            int exportIndex = tapeContextExport.Tag is MzfDisplayData item ? MzfDisplayDataCollection.IndexOf(item) : -1;
            tapeContextExport.IsEnabled = exportButton.IsEnabled && exportIndex >= 0 && exportIndex < mzfBlocks.Count;
            tapeContextExport.Header = tapeContextExport.IsEnabled
                ? $"Export “{ConvertMzfNameToASCIIString(mzfBlocks[exportIndex].Header.MzfFname)}”…" : "Export...";
            tapeContextExportSelected.Header = $"Export selected ({MzfDataGrid.SelectedItems.Count})…";
            tapeContextExportSelected.Visibility = MzfDataGrid.SelectedItems.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            tapeContextDelete.IsEnabled = deleteButton.IsEnabled && !document.IsReadOnlyQuickDisk && MzfDataGrid.SelectedItems.Count > 0;
            tapeContextRename.IsEnabled = !document.IsReadOnlyQuickDisk && MzfDataGrid.SelectedItems.Count == 1;
            tapeContextView.IsEnabled = viewButton.IsEnabled && MzfDataGrid.SelectedItems.Count > 0;
        }

        private void TapeRename_Click(object sender, RoutedEventArgs e)
        {
            if (document.IsReadOnlyQuickDisk || MzfDataGrid.SelectedItems.Count != 1) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MzfDataGrid.Focus(); MzfDataGrid.CurrentCell = new DataGridCellInfo(MzfDataGrid.SelectedItem, fileNameColumn); MzfDataGrid.BeginEdit();
            }));
        }

        private void MzfDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Column != fileNameColumn ||
                e.Row.Item is not MzfDisplayData displayData || e.EditingElement is not TextBox editor)
            {
                return;
            }

            int index = MzfDisplayDataCollection.IndexOf(displayData);
            if ((uint)index >= mzfBlocks.Count)
            {
                return;
            }
            try
            {
                byte[] encodedName = EncodeMzfFileName(editor.Text);
                TapeRecord record = mzfBlocks[index];
                if (record.Header.MzfFname.SequenceEqual(encodedName))
                {
                    return;
                }
                MZQFileHeader header = record.Header;
                header.MzfFname = encodedName;
                header.MzfFnameEnd = 0x0D;
                record.Header = header;
                document.IsModified = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshGrid();
                    if ((uint)index < MzfDisplayDataCollection.Count)
                    {
                        MzfDataGrid.SelectedItem = MzfDisplayDataCollection[index];
                        MzfDataGrid.CurrentCell = new DataGridCellInfo(MzfDisplayDataCollection[index], fileNameColumn);
                    }
                }));
            }
            catch (Exception exception)
            {
                e.Cancel = true;
                MessageBox.Show(this, exception.Message, "Rename", MessageBoxButton.OK, MessageBoxImage.Error);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MzfDataGrid.CancelEdit(DataGridEditingUnit.Cell);
                    RefreshGrid();
                }));
            }
        }

        internal static byte[] EncodeMzfFileName(string value)
        {
            string name = value.Trim();
            if (name.Length is < 1 or > 16)
            {
                throw new InvalidOperationException("The MZF file name must contain 1 to 16 characters.");
            }
            byte[] encoded = ConvertASCIIStringToSHASCIIBytes(name);
            for (int index = 0; index < name.Length; index++)
            {
                if (name[index] > 0x7F || FromSHASCII(encoded[index]) != (byte)name[index])
                {
                    throw new InvalidOperationException($"The character '{name[index]}' cannot be represented in a Sharp MZ file name.");
                }
            }
            var result = Enumerable.Repeat((byte)0x0D, 16).ToArray();
            encoded.CopyTo(result, 0);
            return result;
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

            DataObject dragData;
            try { dragData = WorkspaceDragTransfer.Create(dragOwner, WorkspaceDragTransfer.Capture(draggedRecords)); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "Copy records"); return; }
            localDraggedRecords = draggedRecords;
            rowDragInProgress = true;
            try
            {
                DragDrop.DoDragDrop(MzfDataGrid, dragData, DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                ClearDropTargetIndicator();
                rowDragInProgress = false;
                localDraggedRecords = null;
                rowDragStartItem = null;
            }
        }

        private void MzfDataGrid_DragOver(object sender, DragEventArgs e)
        {
            if (!WorkspaceDragTransfer.IsPresent(e.Data))
            {
                return;
            }

            e.Effects = WorkspaceDragTransfer.IsLocal(e.Data, dragOwner) ? DragDropEffects.Move :
                document.IsReadOnlyQuickDisk ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
            Point position = e.GetPosition(MzfDataGrid);
            UpdateDropTargetIndicator(position);
            ScrollDataGridDuringDrag(position);
        }

        private void MzfDataGrid_DragLeave(object sender, DragEventArgs e)
        {
            if (WorkspaceDragTransfer.IsPresent(e.Data))
            {
                ClearDropTargetIndicator();
            }
        }

        private void MzfDataGrid_Drop(object sender, DragEventArgs e)
        {
            if (!WorkspaceDragTransfer.IsPresent(e.Data)) return;
            e.Handled = true;
            int insertionIndex = GetDropInsertionIndex(e.GetPosition(MzfDataGrid));
            ClearDropTargetIndicator();
            if (!WorkspaceDragTransfer.IsLocal(e.Data, dragOwner))
            {
                PreviewRecordCopy(e, insertionIndex);
                return;
            }
            if (localDraggedRecords is not { } draggedRecords) return;
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

        private void PreviewRecordCopy(DragEventArgs e, int insertionIndex)
        {
            e.Effects = DragDropEffects.None;
            try
            {
                var preview = new TapeCopyPreview(document, WorkspaceDragTransfer.Read(e.Data), insertionIndex);
                var dialog = new DskPatchPreviewWindow(this, preview.Report, () => preview.Apply(document), "Copy records preview", "Copy to this workspace");
                dialog.ShowDialog();
                if (dialog.Applied) { RefreshGrid(); e.Effects = DragDropEffects.Copy; }
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "Copy records", MessageBoxButton.OK, MessageBoxImage.Error); }
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

        private AudioFileImportResult ImportAudioFile(
            string filePath,
            WavImportMode importMode,
            AudioReportMode reportMode,
            int fileIndex = 0,
            int fileCount = 0)
        {
            if (importMode != WavImportMode.Heuristic)
            {
                try
                {
                    return new AudioFileImportResult
                    {
                        SourceFile = filePath,
                        Records = SharpTapeImporter.ReadFile(filePath),
                        ReportMode = reportMode
                    };
                }
                catch (Exception exception)
                {
                    return new AudioFileImportResult
                    {
                        SourceFile = filePath,
                        Records = [],
                        Error = exception,
                        ReportMode = reportMode
                    };
                }
            }

            var progressWindow = new WavAnalysisProgressWindow(filePath, fileIndex, fileCount)
            {
                Owner = this
            };
            bool? analysisAccepted = progressWindow.ShowDialog();
            if (progressWindow.AnalysisError != null)
            {
                // Format, decoder and I/O failures deliberately do not enter the
                // standard-decoder fallback. Recovery failures are represented by
                // the structured analysis result below.
                return new AudioFileImportResult
                {
                    SourceFile = filePath,
                    Records = [],
                    Error = progressWindow.AnalysisError,
                    ReportMode = reportMode
                };
            }
            if (analysisAccepted != true || progressWindow.AnalysisResult == null)
            {
                return new AudioFileImportResult
                {
                    SourceFile = filePath,
                    Records = [],
                    Cancelled = true,
                    ReportMode = reportMode
                };
            }

            WavHeuristicAnalysisResult analysis = progressWindow.AnalysisResult;
            IReadOnlyList<TapeRecord> records = analysis.Records;
            bool fallbackUsed = false;
            Exception? fallbackError = null;
            if (AudioImportPolicy.ShouldTryStandardDecoder(analysis))
            {
                try
                {
                    IReadOnlyList<TapeRecord> standardRecords = SharpTapeImporter.ReadFile(filePath);
                    if (standardRecords.Count > records.Count)
                    {
                        records = standardRecords;
                        fallbackUsed = true;
                    }
                }
                catch (Exception exception)
                {
                    fallbackError = exception;
                }
            }

            Exception? error = records.Count == 0
                ? new InvalidDataException(
                    fallbackError == null
                        ? "Heuristic analysis did not recover a checksum-valid program."
                        : $"Heuristic recovery failed and the standard decoder also failed: {fallbackError.Message}",
                    fallbackError)
                : null;
            return new AudioFileImportResult
            {
                SourceFile = filePath,
                Records = records,
                Analysis = analysis,
                StandardFallbackUsed = fallbackUsed,
                Error = error,
                ReportMode = reportMode
            };
        }

        private void ShowAudioImportReport(AudioFileImportResult result)
        {
            if (result.ReportMode == AudioReportMode.None)
            {
                return;
            }
            if (result.ReportMode == AudioReportMode.Detailed && result.Analysis != null)
            {
                new WavAnalysisStatisticsWindow(result.Analysis.Statistics)
                {
                    Owner = this
                }.ShowDialog();
            }
            else
            {
                new WavAnalysisStatisticsWindow([result])
                {
                    Owner = this
                }.ShowDialog();
            }
            if (result.StandardFallbackUsed)
            {
                MessageBox.Show(
                    this,
                    "The heuristic result was incomplete. The checksum-valid standard decoder recovered more records and its result was imported.",
                    "Audio imported with standard decoder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void ShowAudioImportReports(IReadOnlyList<AudioFileImportResult> results)
        {
            if (results.Count == 0 || results[0].ReportMode == AudioReportMode.None)
            {
                return;
            }
            if (results.Count == 1 && results[0].Imported)
            {
                ShowAudioImportReport(results[0]);
                return;
            }
            new WavAnalysisStatisticsWindow(
                results,
                includeDetails: results[0].ReportMode == AudioReportMode.Detailed)
            {
                Owner = this
            }.ShowDialog();
        }

        private bool AddFile(
            string filePath,
            bool bindAsCurrent = false,
            bool showIplImportDialog = false,
            WavImportMode? selectedAudioImportMode = null,
            AudioReportMode? selectedAudioReportMode = null,
            Action<AudioFileImportResult>? audioResultSink = null,
            bool deferAudioErrors = false,
            int audioFileIndex = 0,
            int audioFileCount = 0)
        {
            if (!bindAsCurrent && document.IsReadOnlyQuickDisk)
            {
                MessageBox.Show(this, "Non-SHARP/unknown physical QuickDisk is read-only.", "QuickDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            string fileExtension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            WavImportMode wavImportMode = selectedAudioImportMode ?? WavImportMode.Standard;
            AudioReportMode wavReportMode = selectedAudioReportMode ?? AudioReportMode.Summary;
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
                wavReportMode = options.ReportMode;
            }
            var recordsToAdd = new List<TapeRecord>();
            byte[] containerTrailing = Array.Empty<byte>();
            string? loadedSidecar = null;
            QuickDiskPhysicalProfile? loadedQdProfile = null;
            QdReadResult? loadedQdResult = null;
            byte[]? loadedQdSource = null;
            Mz800IplDskInfo? loadedIplDskInfo = null;
            AudioFileImportResult? audioImportResult = null;
            bool audioResultReported = false;
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
                    loadedQdSource = File.ReadAllBytes(filePath);
                    QdReadResult result = QdImageReaderWriter.Read(loadedQdSource);
                    if (!bindAsCurrent && !result.Analysis.Identification.IsNativeSharpMz)
                        throw new InvalidDataException("Non-SHARP/unknown physical QuickDisk cannot be imported as MZF records. Open it for read-only inspection.");
                    loadedQdResult = result;
                    format = result.DocumentFormat;
                    loadedQdProfile = result.PhysicalProfile;
                    recordsToAdd.AddRange(result.Records);
                }
                else if (fileExtension == ".hfe")
                {
                    new PhysicalTrackViewerWindow(this, HfeImage.Parse(File.ReadAllBytes(filePath)), filePath).ShowDialog();
                    return false;
                }
                else if (fileExtension == ".dsk")
                {
                    DskDocument dskDocument = DskDocument.Open(filePath);
                    if (dskDocument.FileSystem.Type != DskFileSystemType.BootOnly)
                    {
                        ShowDskDocument(dskDocument);
                        return false;
                    }
                    try
                    {
                        Mz800IplDskReadResult result = Mz800IplDskReader.ReadFile(filePath);
                        format = TapeDocumentFormat.Mzf;
                        loadedIplDskInfo = result.DiskInfo;
                        recordsToAdd.Add(result.Record);
                    }
                    catch (InvalidDataException)
                    {
                        ShowDskDocument(dskDocument);
                        return false;
                    }
                }
                else if (fileExtension is ".mzf" or ".m12" or ".mz0" or ".mz7")
                {
                    format = fileExtension == ".m12" ? TapeDocumentFormat.M12 : TapeDocumentFormat.Mzf;
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
                    if (fileExtension is ".wav" or ".flac")
                    {
                        audioImportResult = ImportAudioFile(
                            filePath,
                            wavImportMode,
                            wavReportMode,
                            audioFileIndex,
                            audioFileCount);
                        if (audioImportResult.Cancelled)
                        {
                            audioResultSink?.Invoke(audioImportResult);
                            return false;
                        }
                        if (audioImportResult.Error != null)
                        {
                            throw audioImportResult.Error;
                        }
                        recordsToAdd.AddRange(audioImportResult.Records);
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
                    byte[]? quickDiskSource = format is TapeDocumentFormat.Mzq or TapeDocumentFormat.Qdf or
                        TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy
                        ? loadedQdSource ?? File.ReadAllBytes(filePath) : null;
                    document.Clear();
                    document.FilePath = System.IO.Path.GetFullPath(filePath);
                    document.Format = format;
                    document.ContainerTrailingData = containerTrailing;
                    document.SidecarPath = loadedSidecar;
                    document.QuickDiskProfile = loadedQdProfile;
                    document.QuickDiskReadResult = loadedQdResult;
                    document.QuickDiskSourceImage = quickDiskSource;
                    quickDiskInspectorHost.Content = loadedQdResult != null && !loadedQdResult.Analysis.Identification.IsNativeSharpMz && quickDiskSource != null
                        ? new QuickDiskInspectorControl(loadedQdResult, quickDiskSource) : null;
                    document.IplDskInfo = loadedIplDskInfo;
                    document.IsModified = false;
                    actFileName = System.IO.Path.GetFileName(filePath);
                    Title = BuildWindowTitle(actFileName);
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
                if (audioImportResult != null)
                {
                    audioResultSink?.Invoke(audioImportResult);
                    audioResultReported = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                if ((fileExtension is ".wav" or ".flac") && audioResultSink != null && !audioResultReported)
                {
                    audioResultSink(audioImportResult ?? new AudioFileImportResult
                    {
                        SourceFile = filePath,
                        Records = [],
                        Error = ex,
                        ReportMode = wavReportMode
                    });
                }
                if (!deferAudioErrors || fileExtension is not (".wav" or ".flac"))
                {
                    MessageBox.Show(ex.Message, "Error reading file", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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
                AudioReportMode audioReportMode = AudioReportMode.Summary;
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
                    audioReportMode = options.ReportMode;
                }

                bool bindAsCurrent = document.Format == TapeDocumentFormat.None && mzfBlocks.Count == 0;
                var audioResults = new List<AudioFileImportResult>();
                int audioIndex = 0;
                foreach (string filePath in filePaths)
                {
                    string extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                    bool isAudio = extension is ".wav" or ".flac";
                    if (isAudio)
                    {
                        audioIndex++;
                    }
                    WavImportMode? mode = isAudio
                        ? audioImportMode
                        : null;
                    if (!AddFile(
                        filePath,
                        bindAsCurrent,
                        selectedAudioImportMode: mode,
                        selectedAudioReportMode: isAudio ? audioReportMode : null,
                        audioResultSink: audioResults.Add,
                        deferAudioErrors: audioPaths.Length > 1,
                        audioFileIndex: audioIndex,
                        audioFileCount: audioPaths.Length))
                    {
                        if (isAudio && audioPaths.Length > 1)
                        {
                            continue;
                        }
                        return;
                    }
                    bindAsCurrent = false;
                }

                ShowAudioImportReports(audioResults);

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

        private void TapeContextExport_Click(object sender, RoutedEventArgs e)
        {
            if (tapeContextExport.Tag is not MzfDisplayData item) return;
            int index = MzfDisplayDataCollection.IndexOf(item);
            if (index < 0 || index >= mzfBlocks.Count) return;
            TapeRecord record = mzfBlocks[index];
            ExportRecords([(index, record)], ConvertMzfNameToASCIIString(record.Header.MzfFname));
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
            if (document.IsReadOnlyQuickDisk) return;
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

        private void button_Click_Close(object sender, RoutedEventArgs e)
        {
            if (dskEditorControl.Visibility == Visibility.Visible)
            {
                TryLeaveDskMode();
                return;
            }
            if (!TryCloseTapeDocument())
            {
                return;
            }

            Title = BuildWindowTitle();
            RefreshGrid();
        }

        private bool TryCloseTapeDocument()
        {
            if (document.Format == TapeDocumentFormat.None)
            {
                return true;
            }
            if (document.IsModified && MessageBox.Show(
                    this,
                    "The current document contains unsaved changes. Close it and discard the changes?",
                    "Close",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return false;
            }

            document.Clear();
            actFileName = string.Empty;
            MzfDataGrid.SelectedItems.Clear();
            return true;
        }

        private void button_Click_NewQuickDisk(object sender, RoutedEventArgs e)
        {
            if (!TryLeaveDskMode())
            {
                return;
            }
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
            Title = BuildWindowTitle("New");
            RefreshGrid();
        }

        private void button_Click_NewDsk(object sender, RoutedEventArgs e)
        {
            if (!TryLeaveDskMode())
            {
                return;
            }
            if (!ConfirmTapeDocumentReplacement())
            {
                return;
            }
            DskNewOptions? options = DskNewDialog.Show(this);
            if (options == null) return;
            try
            {
                if (options.Format == DskNewFormat.IplMulti)
                {
                    ShowNewMultiIplEditor();
                    return;
                }
                if (options.Format == DskNewFormat.IplSingle)
                {
                    ShowNewSingleIplEditor();
                    return;
                }

                DskDocument dsk = options.Format switch
                {
                    DskNewFormat.MzBasic => DskDocumentFactory.CreateFsmz(ipldisk: false, options.Tracks, options.Sides),
                    DskNewFormat.IplDisk => DskDocumentFactory.CreateFsmz(ipldisk: true, options.Tracks, options.Sides),
                    DskNewFormat.PersonalCpm => DskDocumentFactory.CreatePersonalCpm80(sds400: false),
                    DskNewFormat.Sds400 => DskDocumentFactory.CreatePersonalCpm80(sds400: true),
                    DskNewFormat.LecCpmDd => DskDocumentFactory.CreateCpm(highDensity: false, options.Tracks, options.Sides),
                    DskNewFormat.LecCpmHd => DskDocumentFactory.CreateCpm(highDensity: true, options.Tracks, options.Sides),
                    DskNewFormat.Mrs => DskDocumentFactory.CreateMrs(options.Tracks, options.Sides),
                    DskNewFormat.Lemmings => DskDocumentFactory.CreateLemmings(options.Tracks, options.Sides),
                    DskNewFormat.CustomRaw => DskDocumentFactory.CreateRaw(
                        options.Tracks, options.Sides, options.Sectors, options.SectorSize, 1,
                        options.SectorSize == 256 ? (byte)0x2A : (byte)0x4E, options.Filler, "MZTools", options.SectorOrder, options.SectorIds),
                    _ => throw new ArgumentOutOfRangeException()
                };
                ShowDskDocument(dsk, tapeReplacementConfirmed: true);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "New DSK", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowNewMultiIplEditor()
        {
            if (dskEditorControl.Visibility == Visibility.Visible && !dskEditorControl.TryCloseDocument())
            {
                return;
            }
            document.Clear();
            actFileName = string.Empty;
            RefreshGrid();
            dskEditorControl.LoadNewMultiIpl();
            dskEditorControl.Visibility = Visibility.Visible;
            UpdateQuickDiskFeatureVisibility();
            Title = BuildWindowTitle(dskEditorControl.DocumentTitle);
        }

        private void ShowNewSingleIplEditor()
        {
            if (dskEditorControl.Visibility == Visibility.Visible && !dskEditorControl.TryCloseDocument())
            {
                return;
            }
            document.Clear();
            actFileName = string.Empty;
            RefreshGrid();
            dskEditorControl.LoadNewSingleIpl();
            dskEditorControl.Visibility = Visibility.Visible;
            UpdateQuickDiskFeatureVisibility();
            Title = BuildWindowTitle(dskEditorControl.DocumentTitle);
        }

        private bool ConfirmTapeDocumentReplacement()
        {
            if (document.Format == TapeDocumentFormat.None && !document.IsModified && mzfBlocks.Count == 0)
            {
                return true;
            }
            return MessageBox.Show(this,
                "Creating or opening a DSK image will close the current QD or tape document. Continue?",
                "New DSK",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void ShowDskDocument(DskDocument dsk, bool tapeReplacementConfirmed = false)
        {
            if (dskEditorControl.Visibility == Visibility.Visible && !dskEditorControl.TryCloseDocument())
            {
                return;
            }
            if (!tapeReplacementConfirmed && !ConfirmTapeDocumentReplacement())
            {
                return;
            }

            document.Clear();
            actFileName = string.Empty;
            RefreshGrid();
            dskEditorControl.LoadDocument(dsk);
            dskEditorControl.Visibility = Visibility.Visible;
            UpdateQuickDiskFeatureVisibility();
            Title = BuildWindowTitle(dskEditorControl.DocumentTitle);
        }

        private bool TryLeaveDskMode()
        {
            if (dskEditorControl.Visibility != Visibility.Visible) return true;
            if (!dskEditorControl.TryCloseDocument()) return false;
            dskEditorControl.Visibility = Visibility.Collapsed;
            UpdateQuickDiskFeatureVisibility();
            Title = BuildWindowTitle(string.IsNullOrWhiteSpace(actFileName) ? null : actFileName);
            return true;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (dskEditorControl.Visibility == Visibility.Visible && !dskEditorControl.TryCloseDocument())
            {
                e.Cancel = true;
                return;
            }
            if (dskEditorControl.Visibility != Visibility.Visible && !TryCloseTapeDocument())
            {
                e.Cancel = true;
            }
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
            if (!document.IsQdImage || document.IsReadOnlyQuickDisk && document.QuickDiskReadResult?.Analysis.Identification.IsBlank != true)
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
            // Formatting a proven blank disk is an explicit choice to create a SHARP layout.
            document.QuickDiskReadResult = null;
            quickDiskInspectorHost.Content = null;
            document.ContainerTrailingData = Array.Empty<byte>();
            document.IsModified = true;
            RefreshGrid();
        }

    }
}
