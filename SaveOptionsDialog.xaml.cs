using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace QDTool
{
    public partial class SaveOptionsDialog : Window
    {
        private readonly IReadOnlyList<(int Index, TapeRecord Record)>? compressionRecords;
        private readonly CompressionTarget? compressionTarget;
        private CancellationTokenSource? previewCancellation;

        internal SaveOptionsDialog(
            TapeDocumentFormat format,
            int trailingBytes,
            bool sidecarAlreadyExists,
            CompressionTarget? compressionTarget = null,
            IReadOnlyList<(int Index, TapeRecord Record)>? compressionRecords = null)
        {
            InitializeComponent();
            this.compressionTarget = compressionTarget;
            this.compressionRecords = compressionRecords;
            waveformOptionsPanel.Visibility = Visibility.Collapsed;

            bool isMzf = format == TapeDocumentFormat.Mzf;
            string sidecarName = isMzf ? "MFI" : "MTI";
            Title = "Tape save options";
            headingTextBlock.Text = isMzf ? "MZF save options" : "MZT save options";

            preserveTrailingCheckBox.Content = $"Preserve trailing data ({trailingBytes} B)";
            preserveTrailingCheckBox.Visibility = isMzf && trailingBytes > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            generateSidecarCheckBox.Content = $"Generate {sidecarName}";
            generateSidecarCheckBox.IsChecked = sidecarAlreadyExists;
            generateSidecarCheckBox.IsEnabled = !sidecarAlreadyExists;
            if (sidecarAlreadyExists)
            {
                existingSidecarTextBlock.Text =
                    $"The existing {sidecarName} will be regenerated to stay synchronized.";
                existingSidecarTextBlock.Visibility = Visibility.Visible;
            }

            if (compressionTarget.HasValue)
            {
                compressionOptionsControl.ConfigureTarget(compressionTarget.Value);
                bool hasCompressedRecords = compressionRecords?.Any(value =>
                    IsCompressionLocked(value.Record, out _)) == true;
                if (hasCompressedRecords)
                {
                    existingCompressionGroupBox.Visibility = Visibility.Visible;
                    compressionScopeTextBlock.Visibility = Visibility.Visible;
                }
                if (compressionRecords != null && compressionRecords.All(value =>
                    IsCompressionLocked(value.Record, out _)))
                {
                    compressionOptionsControl.IsEnabled = false;
                }
                compressionOptionsControl.Visibility = Visibility.Visible;
                compressionPreviewBorder.Visibility = Visibility.Visible;
                compressionOptionsControl.OptionsChanged += CompressionOptionsControl_OptionsChanged;
                Loaded += async (_, _) => await RefreshCompressionPreviewAsync();
            }
        }

        internal SaveOptionsDialog(string extension, int recordCount)
        {
            InitializeComponent();
            tapeOptionsPanel.Visibility = Visibility.Collapsed;
            Title = "Tape export options";
            headingTextBlock.Text = $"{extension.TrimStart('.').ToUpperInvariant()} save options";

            Visibility layoutVisibility = recordCount > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            layoutOptionsPanel.Visibility = layoutVisibility;
            separateHintTextBlock.Visibility = layoutVisibility;
        }

        public bool PreserveTrailing => preserveTrailingCheckBox.IsChecked == true;

        public bool GenerateSidecar => generateSidecarCheckBox.IsChecked == true;

        internal MzfCompressionOptions CompressionOptions
        {
            get
            {
                if (!compressionOptionsControl.TryGetOptions(out MzfCompressionOptions options, out string error))
                {
                    throw new InvalidOperationException(error);
                }
                return options;
            }
        }

        internal IReadOnlyList<TapeRecord> PackedRecords { get; private set; } = Array.Empty<TapeRecord>();

        internal SharpTapeMachine SelectedMachine => machineComboBox.SelectedIndex == 1
            ? SharpTapeMachine.Mz700
            : SharpTapeMachine.Mz800;

        internal bool SeparateFiles => separateRadioButton.IsChecked == true;

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (compressionOptionsControl.Visibility == Visibility.Visible &&
                !compressionOptionsControl.TryGetOptions(out _, out string error))
            {
                MessageBox.Show(this, error, "Invalid compression options", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (compressionOptionsControl.Visibility == Visibility.Visible &&
                PackedRecords.Count != (compressionRecords?.Count ?? 0))
            {
                return;
            }
            DialogResult = true;
        }

        private async void CompressionOptionsControl_OptionsChanged(object? sender, EventArgs e) =>
            await RefreshCompressionPreviewAsync();

        private async void DecompressKnownCheckBox_Changed(object sender, RoutedEventArgs e) =>
            await RefreshCompressionPreviewAsync();

        private async Task RefreshCompressionPreviewAsync()
        {
            if (!compressionTarget.HasValue || compressionRecords == null)
            {
                return;
            }

            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            previewCancellation = new CancellationTokenSource();
            CancellationTokenSource cancellation = previewCancellation;
            CancellationToken token = previewCancellation.Token;
            PackedRecords = Array.Empty<TapeRecord>();
            saveButton.IsEnabled = false;
            compressionPreviewProgressBar.Visibility = Visibility.Collapsed;
            compressionDetailsTextBox.Visibility = Visibility.Collapsed;

            if (!compressionOptionsControl.TryGetOptions(out MzfCompressionOptions options, out string error))
            {
                compressionPreviewTextBlock.Text = error;
                return;
            }

            bool decompressKnown = decompressKnownCheckBox.IsChecked == true;
            compressionPreviewTextBlock.Text = GetProgressText(options, decompressKnown);
            compressionPreviewProgressBar.Visibility = Visibility.Visible;
            try
            {
                var packed = new List<TapeRecord>(compressionRecords.Count);
                var applied = new List<string>(compressionRecords.Count);
                var details = new List<string>(compressionRecords.Count);
                int incompleteEmbeddedDescriptions = 0;
                int row = 0;
                foreach ((int index, TapeRecord source) in compressionRecords)
                {
                    row++;
                    MzfCompressionResult result;
                    string appliedDescription;
                    bool compressionLocked = IsCompressionLocked(source, out string existingCompression);
                    try
                    {
                        result = await PrepareCompressionForExportAsync(
                            source,
                            options,
                            compressionTarget.Value,
                            decompressKnown,
                            token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        string name = SharpMzEncoding.ConvertMzfNameToASCIIString(source.Header.MzfFname);
                        throw new InvalidOperationException(
                            $"Transformation failed for record {index + 1} \"{name}\": {exception.Message}",
                            exception);
                    }
                    appliedDescription = compressionLocked
                        ? decompressKnown
                            ? $"Decompressed {existingCompression}"
                            : $"{existingCompression} (kept)"
                        : FormatAlgorithm(result.AppliedOptions);
                    if (compressionLocked && decompressKnown &&
                        MzfLoaderBuilder.TryGetCompressionInfo(source, out MzfCompressionInfo? info) &&
                        info?.EmbeddedLoader == true)
                    {
                        incompleteEmbeddedDescriptions++;
                    }
                    packed.Add(result.Record);
                    applied.Add(appliedDescription);
                    string recordName = SharpMzEncoding.ConvertMzfNameToASCIIString(source.Header.MzfFname);
                    int originalRecordSize = source.Body.MzfBody.Length;
                    int packedRecordSize = result.Record.Body.MzfBody.Length;
                    double ratio = originalRecordSize == 0
                        ? 100
                        : packedRecordSize * 100.0 / originalRecordSize;
                    details.Add(
                        $"{row,2}. {recordName,-16}  {appliedDescription,-29}  " +
                        $"{originalRecordSize,6} -> {packedRecordSize,6} B  ({ratio,5:F1}%)");
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                PackedRecords = packed;
                int originalSize = compressionRecords.Sum(value => value.Record.Body.MzfBody.Length);
                int packedSize = packed.Sum(value => value.Body.MzfBody.Length);
                int sizeChangePercent = originalSize == 0
                    ? 0
                    : (int)Math.Round(((double)packedSize / originalSize - 1.0) * 100);
                string sizeChange = sizeChangePercent.ToString("+0;-0;0") + "%";
                if (packed.Count == 1)
                {
                    TapeRecord record = packed[0];
                    string selection = applied[0].EndsWith("(kept)", StringComparison.Ordinal)
                        ? $"Existing compression: {applied[0]}"
                        : applied[0].StartsWith("Decompressed ", StringComparison.Ordinal)
                            ? $"Operation: {applied[0]}"
                        : options.Algorithm == MzfCompressionAlgorithm.Auto
                            ? $"Auto selected: {applied[0]}"
                            : $"Compression: {applied[0]}";
                    compressionPreviewTextBlock.Text =
                        $"{selection}\nInput: {originalSize} B   Output: {packedSize} B   Size change: {sizeChange}\n" +
                        $"LOAD ${record.Header.MzfStart:X4}   EXEC ${record.Header.MzfExec:X4}";
                }
                else
                {
                    string policy;
                    if (options.Algorithm == MzfCompressionAlgorithm.Auto)
                    {
                        string selections = string.Join(", ", applied
                            .GroupBy(value => value)
                            .Select(group => group.Count() == 1
                                ? group.Key
                                : $"{group.Key} ({group.Count()} records)"));
                        policy = $"Auto selected: {selections}.";
                    }
                    else
                    {
                        policy = $"Policy: {FormatAlgorithm(options)}.";
                    }
                    if (compressionRecords.Any(value => IsCompressionLocked(value.Record, out _)))
                    {
                        policy += decompressKnown
                            ? " Recognized compressed records: decompress for export."
                            : " Recognized compressed records: keep unchanged.";
                    }
                    compressionPreviewTextBlock.Text =
                        $"{packed.Count} records   Input: {originalSize} B   Output: {packedSize} B   Size change: {sizeChange}\n{policy}";
                    compressionDetailsTextBox.Text = string.Join(Environment.NewLine, details);
                    compressionDetailsTextBox.Visibility = Visibility.Visible;
                }
                if (incompleteEmbeddedDescriptions > 0)
                {
                    compressionPreviewTextBlock.Text +=
                        $"\nWarning: {incompleteEmbeddedDescriptions} embedded ZX7 description(s) cannot be reconstructed; loader bytes will be cleared.";
                }
                saveButton.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    compressionPreviewTextBlock.Text = exception.Message;
                }
            }
            finally
            {
                if (ReferenceEquals(previewCancellation, cancellation))
                {
                    compressionPreviewProgressBar.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static string GetProgressText(MzfCompressionOptions options, bool decompressKnown) =>
            decompressKnown
                ? "Analyzing decompression and export sizes..."
                : options.Algorithm == MzfCompressionAlgorithm.None
                ? "Validating export..."
                : "Analyzing compression... This may take a while.";

        internal static bool IsCompressionLocked(TapeRecord record, out string compression)
        {
            compression = MzfLoaderBuilder.DetectCompression(record);
            return !string.Equals(compression, "None / unknown", StringComparison.Ordinal);
        }

        internal static Task<MzfCompressionResult> PrepareCompressionForExportAsync(
            TapeRecord source,
            MzfCompressionOptions options,
            CompressionTarget target,
            bool decompressKnownCompression = false,
            CancellationToken cancellationToken = default)
        {
            if (IsCompressionLocked(source, out _))
            {
                if (decompressKnownCompression)
                {
                    return Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        MzfDecompressionResult decompressed = MzfDecompressionService.Decompress(source);
                        cancellationToken.ThrowIfCancellationRequested();
                        return new MzfCompressionResult(
                            decompressed.Record,
                            new MzfCompressionOptions(MzfCompressionAlgorithm.None),
                            source.Body.MzfBody.Length);
                    }, cancellationToken);
                }
                return Task.FromResult(new MzfCompressionResult(
                    source.DeepClone(),
                    new MzfCompressionOptions(MzfCompressionAlgorithm.None),
                    source.Body.MzfBody.Length));
            }
            return MzfCompressionService.CompressAsync(source, options, target, cancellationToken);
        }

        private static string FormatAlgorithm(MzfCompressionOptions options)
        {
            if (options.Algorithm is MzfCompressionAlgorithm.None or MzfCompressionAlgorithm.Auto)
            {
                return options.Algorithm.ToString();
            }
            return options.Algorithm.ToString().ToUpperInvariant() +
                (options.Zx0Quick ? " quick" : string.Empty) +
                (options.Direction == CompressionDirection.Backward ? " backward" : " forward");
        }

        protected override void OnClosed(EventArgs e)
        {
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            base.OnClosed(e);
        }
    }
}
