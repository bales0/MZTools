using System;
using System.Windows;
using System.Windows.Controls;

namespace MZTools
{
    public partial class CompressionOptionsControl : UserControl
    {
        private bool updating;
        private bool initialized;
        private CompressionTarget target = CompressionTarget.MzfTape;

        public CompressionOptionsControl()
        {
            InitializeComponent();
            initialized = true;
            UpdateAvailability();
        }

        internal event EventHandler? OptionsChanged;
        internal bool KeepSource => (algorithmComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "KeepSource";

        internal void ConfigureTarget(CompressionTarget value)
        {
            target = value;
            updating = true;
            var keep = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<ComboBoxItem>(algorithmComboBox.Items), item => Equals(item.Tag, "KeepSource"));
            if (target == CompressionTarget.NativeCom && keep == null)
            {
                algorithmComboBox.Items.Insert(0, new ComboBoxItem { Content = "Keep source encoding (no recompression)", Tag = "KeepSource" });
                algorithmComboBox.SelectedIndex = 0;
            }
            else if (target != CompressionTarget.NativeCom && keep != null) algorithmComboBox.Items.Remove(keep);
            compressionGroupBox.Header = target == CompressionTarget.NativeCom ? "Output compression" : "Compression";
            foreach (ComboBoxItem item in algorithmComboBox.Items)
            {
                string? tag = item.Tag?.ToString();
                if (tag == "None")
                {
                    item.Content = target == CompressionTarget.NativeCom ? "Uncompressed (decode recognized ZX0/ZX7)" : "None";
                    item.ToolTip = target == CompressionTarget.NativeCom
                        ? "Decode recognized ZX0/ZX7 input and add no compression. Other or custom compression is not decoded." : null;
                }
                else if (tag == "Auto")
                {
                    item.Content = target == CompressionTarget.NativeCom ? "Auto (smallest valid COM)" : "Auto (smallest safe)";
                    item.ToolTip = target == CompressionTarget.NativeCom
                        ? "Choose the smallest successfully verified COM candidate, including the original source encoding." : null;
                }
            }
            SetSourceCompression(null);
            bool isIpl = target == CompressionTarget.IplDsk;
            embeddedCheckBox.IsChecked = false;
            embeddedCheckBox.IsEnabled = !isIpl;
            expertExpander.IsExpanded = false;
            expertExpander.IsEnabled = !isIpl;
            skipTextBox.Text = "0";
            targetHintTextBlock.Text = isIpl
                ? "Direct IPL does not load the MZF header. ZX7 embedded loader and partial/skip compression are therefore unavailable."
                : target == CompressionTarget.NativeCom
                    ? "Keep preserves the source payload and loader. Uncompressed decodes recognized ZX0/ZX7 only. COM requires the complete program, so partial/skip compression is unavailable. Embedded ZX7 is checked against any executable header."
                    : target == CompressionTarget.QuickDisk ? "QuickDisk stores programs without the full MZF header. Embedded ZX7 and partial/skip compression are unavailable. Changes apply in memory; use Save to write the image." : string.Empty;
            targetHintTextBlock.Visibility = isIpl || target is CompressionTarget.NativeCom or CompressionTarget.QuickDisk ? Visibility.Visible : Visibility.Collapsed;
            updating = false;
            UpdateAvailability();
        }

        // Label changes deliberately preserve selection and do not raise OptionsChanged.
        internal void SetSourceCompression(MzfCompressionInfo? info)
        {
            if (target != CompressionTarget.NativeCom) return;
            foreach (ComboBoxItem item in algorithmComboBox.Items)
                if (Equals(item.Tag, "KeepSource"))
                {
                    item.Content = info == null ? "Keep source encoding (no recompression)" : "Keep original compression";
                    item.ToolTip = info == null
                        ? "Preserve the loaded payload and loader without decoding or recompressing. Other or custom compression may be present."
                        : "Preserve the recognized source compression, payload and loader without decoding or recompressing.";
                }
        }

        internal void SetOptions(MzfCompressionOptions options)
        {
            updating = true;
            foreach (ComboBoxItem item in algorithmComboBox.Items)
                if (Equals(item.Tag, options.Algorithm.ToString())) { algorithmComboBox.SelectedItem = item; break; }
            forwardRadioButton.IsChecked = options.Direction == CompressionDirection.Forward;
            backwardRadioButton.IsChecked = options.Direction == CompressionDirection.Backward;
            quickCheckBox.IsChecked = options.Zx0Quick;
            embeddedCheckBox.IsChecked = options.Zx7EmbeddedLoader;
            skipTextBox.Text = options.SkipBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            updating = false;
            UpdateAvailability();
            OptionsChanged?.Invoke(this, EventArgs.Empty);
        }

        internal static string Describe(MzfCompressionOptions options) => CompressionDescription.Describe(options);

        internal bool TryGetOptions(out MzfCompressionOptions options, out string error)
        {
            string algorithmName = (algorithmComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            if (algorithmName == "KeepSource") algorithmName = "None";
            MzfCompressionAlgorithm algorithm = Enum.Parse<MzfCompressionAlgorithm>(algorithmName);
            if (!int.TryParse(skipTextBox.Text, out int skip) || skip < 0)
            {
                options = new(MzfCompressionAlgorithm.None);
                error = "Skip bytes must be a non-negative integer.";
                return false;
            }

            options = new MzfCompressionOptions(
                algorithm,
                backwardRadioButton.IsChecked == true
                    ? CompressionDirection.Backward
                    : CompressionDirection.Forward,
                quickCheckBox.IsChecked == true,
                embeddedCheckBox.IsChecked == true,
                skip);
            try
            {
                MzfCompressionService.ValidateOptions(options, target, int.MaxValue);
                error = string.Empty;
                return true;
            }
            catch (InvalidOperationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!initialized || updating)
            {
                return;
            }
            UpdateAvailability();
            OptionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateAvailability()
        {
            if (algorithmComboBox == null)
            {
                return;
            }

            updating = true;
            string algorithm = (algorithmComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            bool concrete = algorithm is "Zx0" or "Zx7";
            directionPanel.IsEnabled = concrete;
            quickCheckBox.IsEnabled = algorithm == "Zx0";
            quickCheckBox.Visibility = Visibility.Visible;
            embeddedCheckBox.IsEnabled = algorithm == "Zx7" && target is not (CompressionTarget.IplDsk or CompressionTarget.QuickDisk);
            embeddedCheckBox.Visibility = Visibility.Visible;
            expertExpander.IsEnabled = concrete && target is not (CompressionTarget.IplDsk or CompressionTarget.NativeCom or CompressionTarget.QuickDisk);
            if (algorithm != "Zx0")
            {
                quickCheckBox.IsChecked = false;
            }
            if (algorithm != "Zx7" || target is CompressionTarget.IplDsk or CompressionTarget.QuickDisk)
            {
                embeddedCheckBox.IsChecked = false;
            }
            if (!concrete || target is CompressionTarget.IplDsk or CompressionTarget.NativeCom or CompressionTarget.QuickDisk)
            {
                skipTextBox.Text = "0";
            }
            updating = false;
        }
    }
}
