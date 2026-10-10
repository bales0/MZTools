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
                algorithmComboBox.Items.Insert(0, new ComboBoxItem { Content = "Keep source (no added compression)", Tag = "KeepSource" });
                algorithmComboBox.SelectedIndex = 0;
            }
            else if (target != CompressionTarget.NativeCom && keep != null) algorithmComboBox.Items.Remove(keep);
            bool isIpl = target == CompressionTarget.IplDsk;
            embeddedCheckBox.IsChecked = false;
            embeddedCheckBox.IsEnabled = !isIpl;
            expertExpander.IsExpanded = false;
            expertExpander.IsEnabled = !isIpl;
            skipTextBox.Text = "0";
            targetHintTextBlock.Text = isIpl
                ? "Direct IPL does not load the MZF header. ZX7 embedded loader and partial/skip compression are therefore unavailable."
                : target == CompressionTarget.NativeCom
                    ? "None unpacks recognized ZX0/ZX7 input. COM requires the complete program, so partial/skip compression is unavailable. Embedded ZX7 is checked against any executable header."
                    : string.Empty;
            targetHintTextBlock.Visibility = isIpl || target == CompressionTarget.NativeCom ? Visibility.Visible : Visibility.Collapsed;
            updating = false;
            UpdateAvailability();
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

        internal static string Describe(MzfCompressionOptions options)
        {
            if (options.Algorithm is MzfCompressionAlgorithm.None or MzfCompressionAlgorithm.Auto) return options.Algorithm.ToString();
            return options.Algorithm.ToString().ToUpperInvariant() + (options.Zx0Quick ? " quick" : "") +
                (options.Direction == CompressionDirection.Backward ? " backward" : " forward") +
                (options.Zx7EmbeddedLoader ? ", embedded" : "") + (options.SkipBytes > 0 ? $", skip {options.SkipBytes}" : "");
        }

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
            embeddedCheckBox.IsEnabled = algorithm == "Zx7" && target != CompressionTarget.IplDsk;
            embeddedCheckBox.Visibility = Visibility.Visible;
            expertExpander.IsEnabled = concrete && target is not (CompressionTarget.IplDsk or CompressionTarget.NativeCom);
            if (algorithm != "Zx0")
            {
                quickCheckBox.IsChecked = false;
            }
            if (algorithm != "Zx7" || target == CompressionTarget.IplDsk)
            {
                embeddedCheckBox.IsChecked = false;
            }
            if (!concrete || target is CompressionTarget.IplDsk or CompressionTarget.NativeCom)
            {
                skipTextBox.Text = "0";
            }
            updating = false;
        }
    }
}
