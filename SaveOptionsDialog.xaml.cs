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

            if (!compressionOptionsControl.TryGetOptions(out MzfCompressionOptions options, out string error))
            {
                compressionPreviewTextBlock.Text = error;
                return;
            }

            compressionPreviewTextBlock.Text = GetProgressText(options);
            compressionPreviewProgressBar.Visibility = Visibility.Visible;
            try
            {
                var packed = new List<TapeRecord>(compressionRecords.Count);
                var applied = new List<MzfCompressionOptions>(compressionRecords.Count);
                foreach ((int index, TapeRecord source) in compressionRecords)
                {
                    MzfCompressionResult result;
                    try
                    {
                        result = await MzfCompressionService.CompressAsync(
                            source,
                            options,
                            compressionTarget.Value,
                            token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        string name = SharpMzEncoding.ConvertMzfNameToASCIIString(source.Header.MzfFname);
                        throw new InvalidOperationException(
                            $"Compression failed for record {index + 1} \"{name}\": {exception.Message}",
                            exception);
                    }
                    packed.Add(result.Record);
                    applied.Add(result.AppliedOptions);
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                PackedRecords = packed;
                int originalSize = compressionRecords.Sum(value => value.Record.Body.MzfBody.Length);
                int packedSize = packed.Sum(value => value.Body.MzfBody.Length);
                int savedPercent = originalSize == 0
                    ? 0
                    : (int)Math.Round((1.0 - (double)packedSize / originalSize) * 100);
                if (packed.Count == 1)
                {
                    TapeRecord record = packed[0];
                    string selection = options.Algorithm == MzfCompressionAlgorithm.Auto
                        ? $"Auto selected: {FormatAlgorithm(applied[0])}"
                        : $"Compression: {FormatAlgorithm(applied[0])}";
                    compressionPreviewTextBlock.Text =
                        $"{selection}\nOriginal: {originalSize} B   Packed: {packedSize} B   Saved: {savedPercent}%\n" +
                        $"LOAD ${record.Header.MzfStart:X4}   EXEC ${record.Header.MzfExec:X4}";
                }
                else
                {
                    string policy;
                    if (options.Algorithm == MzfCompressionAlgorithm.Auto)
                    {
                        string selections = string.Join(", ", applied
                            .GroupBy(FormatAlgorithm)
                            .Select(group => group.Count() == 1
                                ? group.Key
                                : $"{group.Key} ({group.Count()} records)"));
                        policy = $"Auto selected: {selections}.";
                    }
                    else
                    {
                        policy = $"Policy: {FormatAlgorithm(options)}.";
                    }
                    compressionPreviewTextBlock.Text =
                        $"{packed.Count} records   Original: {originalSize} B   Packed: {packedSize} B   Saved: {savedPercent}%\n{policy}";
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

        private static string GetProgressText(MzfCompressionOptions options) =>
            options.Algorithm == MzfCompressionAlgorithm.None
                ? "Validating export..."
                : "Analyzing compression... This may take a while.";

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
