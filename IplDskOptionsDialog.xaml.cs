using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace QDTool
{
    public partial class IplDskOptionsDialog : Window
    {
        private readonly TapeRecord source;
        private CancellationTokenSource? previewCancellation;

        internal IplDskOptionsDialog(TapeRecord source)
        {
            InitializeComponent();
            this.source = source;
            string name = SharpMzEncoding.ConvertMzfNameToASCIIString(source.Header.MzfFname);
            nameTextBlock.Text = $"MZF: {name}";
            originalTextBlock.Text =
                $"Original: {source.Body.MzfBody.Length} B   LOAD ${source.Header.MzfStart:X4}   EXEC ${source.Header.MzfExec:X4}";
            bootNameTextBox.Text = Mz800IplDskWriter.NormalizeBootName(name);
            compressionControl.ConfigureTarget(CompressionTarget.IplDsk);
            compressionControl.OptionsChanged += CompressionControl_OptionsChanged;
            Loaded += async (_, _) => await RefreshPreviewAsync();
        }

        internal TapeRecord? PackedRecord { get; private set; }
        internal MzfCompressionOptions? SelectedCompression { get; private set; }
        internal string BootName => bootNameTextBox.Text;

        private async void CompressionControl_OptionsChanged(object? sender, EventArgs e) =>
            await RefreshPreviewAsync();

        private async Task RefreshPreviewAsync()
        {
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            previewCancellation = new CancellationTokenSource();
            CancellationTokenSource cancellation = previewCancellation;
            CancellationToken token = previewCancellation.Token;
            PackedRecord = null;
            SelectedCompression = null;
            saveButton.IsEnabled = false;
            previewProgressBar.Visibility = Visibility.Collapsed;

            if (!compressionControl.TryGetOptions(out MzfCompressionOptions options, out string error))
            {
                previewTextBlock.Text = error;
                return;
            }

            previewTextBlock.Text = GetProgressText(options);
            previewProgressBar.Visibility = Visibility.Visible;
            try
            {
                MzfCompressionResult result = await MzfCompressionService.CompressAsync(
                    source,
                    options,
                    CompressionTarget.IplDsk,
                    token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                PackedRecord = result.Record;
                SelectedCompression = result.AppliedOptions;
                int sectors = 1 + (result.PackedSize + Mz800IplDskWriter.SectorSize - 1) /
                    Mz800IplDskWriter.SectorSize;
                int savedPercent = result.OriginalSize == 0
                    ? 0
                    : (int)Math.Round((1.0 - (double)result.PackedSize / result.OriginalSize) * 100);
                string algorithm = options.Algorithm == MzfCompressionAlgorithm.Auto
                    ? $"Auto selected: {FormatAppliedOptions(result.AppliedOptions)}"
                    : $"Compression: {FormatAppliedOptions(result.AppliedOptions)}";
                previewTextBlock.Text =
                    $"{algorithm}\nPacked: {result.PackedSize} B   Saved: {savedPercent}%\n" +
                    $"LOAD ${result.Record.Header.MzfStart:X4}   EXEC ${result.Record.Header.MzfExec:X4}   Sectors: {sectors}";
                saveButton.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    previewTextBlock.Text = exception.Message;
                }
            }
            finally
            {
                if (ReferenceEquals(previewCancellation, cancellation))
                {
                    previewProgressBar.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static string GetProgressText(MzfCompressionOptions options) =>
            options.Algorithm == MzfCompressionAlgorithm.None
                ? "Validating export..."
                : "Analyzing compression... This may take a while.";

        private static string FormatAppliedOptions(MzfCompressionOptions options)
        {
            if (options.Algorithm == MzfCompressionAlgorithm.None)
            {
                return "None";
            }
            string detail = options.Algorithm.ToString().ToUpperInvariant();
            if (options.Zx0Quick)
            {
                detail += " quick";
            }
            detail += options.Direction == CompressionDirection.Backward ? " backward" : " forward";
            return detail;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (PackedRecord != null)
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
}
